using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Profiles;
using OpportunityPilot.Domain.Profiles;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class ProfileRepository(AppDbContext db) : IProfileRepository
{
    private IQueryable<Profile> Owned(Guid ownerId) => db.Profiles.Where(p => p.OwnerId == ownerId);

    public async Task<IReadOnlyList<ProfileSummaryDto>> ListAsync(Guid ownerId, CancellationToken ct) =>
        await Owned(ownerId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new ProfileSummaryDto(p.Id, p.Type, p.Name, p.Version, p.ConfirmedAt, p.UpdatedAt))
            .ToListAsync(ct);

    public Task<int> CountAsync(Guid ownerId, CancellationToken ct) => Owned(ownerId).CountAsync(ct);

    public Task<Profile?> FindAsync(Guid ownerId, Guid id, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<List<ProfileVersion>> VersionsAsync(Guid ownerId, Guid profileId, CancellationToken ct) =>
        db.ProfileVersions.Where(v => v.ProfileId == profileId && v.OwnerId == ownerId)
            .OrderByDescending(v => v.Version).ToListAsync(ct);

    public void Add(Profile profile) => db.Profiles.Add(profile);

    public void AddVersion(ProfileVersion version) => db.ProfileVersions.Add(version);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
