using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Profiles;

namespace OpportunityPilot.Application.Profiles;

public sealed class ProfileService(IAppDbContext db, ICurrentUser user, TimeProvider clock, UserEventLog events)
{
    public const int MaxNameLength = 200;
    public const int MaxDataBytes = 32 * 1024;

    private IQueryable<Profile> Owned => db.Profiles.Where(p => p.OwnerId == user.OwnerId);

    public async Task<IReadOnlyList<ProfileSummaryDto>> ListAsync(CancellationToken ct) =>
        await Owned
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new ProfileSummaryDto(p.Id, p.Type, p.Name, p.Version, p.ConfirmedAt, p.UpdatedAt))
            .ToListAsync(ct);

    public async Task<int> CountAsync(CancellationToken ct) => await Owned.CountAsync(ct);

    public async Task<ProfileDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await FindOwnedAsync(id, ct));

    public async Task<IReadOnlyList<ProfileVersionDto>> VersionsAsync(Guid id, CancellationToken ct)
    {
        var profile = await FindOwnedAsync(id, ct);
        var versions = await db.ProfileVersions.Where(v => v.ProfileId == id && v.OwnerId == user.OwnerId)
            .OrderByDescending(v => v.Version).ToListAsync(ct);
        if (versions.Count == 0) return [ToVersionDto(new ProfileVersion(profile, profile.UpdatedAt))];
        return versions.Select(ToVersionDto).ToList();
    }

    public async Task<ProfileDto> CreateAsync(CreateProfileRequest request, CancellationToken ct)
    {
        var data = Validate(request.Name, request.Data, typeIsDefined: Enum.IsDefined(request.Type));
        var profile = new Profile(user.OwnerId, request.Type, request.Name, data, request.Confirmed, clock.GetUtcNow().UtcDateTime);
        db.Profiles.Add(profile);
        db.ProfileVersions.Add(new ProfileVersion(profile, profile.UpdatedAt));
        events.Add(UserEventKinds.ProfileSaved, $"Created profile \"{profile.Name}\"");
        await db.SaveChangesAsync(ct);
        return ToDto(profile);
    }

    public async Task<ProfileDto> UpdateAsync(Guid id, UpdateProfileRequest request, CancellationToken ct)
    {
        var data = Validate(request.Name, request.Data, typeIsDefined: true);
        var profile = await FindOwnedAsync(id, ct);
        if (profile.Version != request.ExpectedVersion)
            throw new ConflictException($"Profile was changed elsewhere (now version {profile.Version}). Reload before saving.");

        profile.Update(request.Name, data, request.Confirmed, clock.GetUtcNow().UtcDateTime);
        db.ProfileVersions.Add(new ProfileVersion(profile, profile.UpdatedAt));
        events.Add(UserEventKinds.ProfileSaved, $"Saved profile \"{profile.Name}\" as version {profile.Version}");
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Profile was changed elsewhere. Reload before saving.");
        }
        return ToDto(profile);
    }

    private async Task<Profile> FindOwnedAsync(Guid id, CancellationToken ct) =>
        await Owned.FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new NotFoundException("Profile not found.");

    private static string Validate(string? name, JsonElement? data, bool typeIsDefined)
    {
        var errors = new Dictionary<string, string[]>();
        if (!typeIsDefined) errors["type"] = ["Unknown profile type."];
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Name is required."];
        else if (name.Trim().Length > MaxNameLength) errors["name"] = [$"Name must be at most {MaxNameLength} characters."];

        var json = "{}";
        if (data is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element)
        {
            if (element.ValueKind != JsonValueKind.Object) errors["data"] = ["Data must be a JSON object."];
            else
            {
                json = element.GetRawText();
                if (Encoding.UTF8.GetByteCount(json) > MaxDataBytes)
                    errors["data"] = [$"Data must be at most {MaxDataBytes / 1024} KB."];
            }
        }

        if (errors.Count > 0) throw new RequestValidationException(errors);
        return json;
    }

    private static ProfileDto ToDto(Profile p)
    {
        using var doc = JsonDocument.Parse(p.StructuredDataJson);
        return new ProfileDto(p.Id, p.Type, p.Name, doc.RootElement.Clone(), p.Version, p.ConfirmedAt, p.CreatedAt, p.UpdatedAt);
    }

    private static ProfileVersionDto ToVersionDto(ProfileVersion version)
    {
        using var doc = JsonDocument.Parse(version.StructuredDataJson);
        return new(version.Id, version.ProfileId, version.Type, version.Name, doc.RootElement.Clone(), version.Version,
            version.ConfirmedAt, version.CreatedAt);
    }
}
