using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Access;

namespace OpportunityPilot.Application.Access;

/// <summary>Who may administer roles. Set on the server only (Render env), never from the browser.</summary>
public sealed class AccessOptions
{
    public const string Section = "Access";

    /// <summary>Comma-separated emails. Matched only against the email inside the validated sign-in token.</summary>
    public string? AdminEmails { get; set; }

    /// <summary>Comma-separated account ids (shown in Settings as "Your account ID"). Works when the token carries no email.</summary>
    public string? AdminOwnerIds { get; set; }
}

/// <summary>What the API knows about the caller beyond the owner id: the email from the validated token (if any) and whether it is a guest.</summary>
public interface ICallerInfo
{
    string? Email { get; }
    bool IsGuest { get; }
}

/// <param name="Role">Candidate, Sales or Both. A user who has not chosen yet gets Both (as before roles existed).</param>
/// <param name="RoleChosen">False until the user picks at sign-up or the admin sets it.</param>
/// <param name="NewAccount">First seen in the last 30 minutes: only then does sign-up ask the role, so an existing user opening the app in a
/// new browser keeps both views.</param>
public sealed record MeDto(Guid AccountId, string Role, bool RoleChosen, bool IsAdmin, bool Guest, bool NewAccount = false);
public sealed record RoleRequest(string? Role);
public sealed record AccessUserDto(Guid AccountId, string? Email, string Role, bool RoleChosen, bool RoleSetByAdmin, DateTime FirstSeenAt, DateTime LastSeenAt);

/// <summary>Role-based access (2026-10-22): Candidate, Sales or Both per user; the admin assigns. No role yet = Both, so nothing changes for existing users.</summary>
public sealed class AccessService(IAppDbContext db, ICurrentUser user, ICallerInfo caller, IOptions<AccessOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(5);

    private static HashSet<string> Csv(string? csv) =>
        new((csv ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    public bool IsAdmin()
    {
        if (caller.IsGuest) return false;
        var o = options.Value;
        if (Csv(o.AdminOwnerIds).Contains(user.OwnerId.ToString())) return true;
        return caller.Email is { Length: > 0 } email && Csv(o.AdminEmails).Contains(email);
    }

    private static UserRole Parse(string? role) =>
        Enum.TryParse<UserRole>(role, true, out var r) && r is UserRole.Candidate or UserRole.Sales or UserRole.Both ? r
            : throw new RequestValidationException(new Dictionary<string, string[]> { ["role"] = ["Choose Candidate, Sales or Both."] });

    private static string Shown(UserRole r) => r == UserRole.None ? nameof(UserRole.Both) : r.ToString();

    private static AccessUserDto Row(UserAccess a) => new(a.OwnerId, a.Email, Shown(a.Role), a.RoleChosen, a.RoleSetByAdmin, a.FirstSeenAt, a.LastSeenAt);

    private static readonly TimeSpan NewFor = TimeSpan.FromMinutes(30);

    private MeDto Me(UserAccess? a) => new(user.OwnerId, Shown(a?.Role ?? UserRole.None), a?.RoleChosen ?? false, IsAdmin(), caller.IsGuest,
        a is not null && clock.GetUtcNow().UtcDateTime - a.FirstSeenAt < NewFor);

    /// <summary>The caller's access. Records a signed-in user (not guests) so the admin can find them; "last seen" is written at most every 5 minutes.</summary>
    /// <param name="label">The email the browser shows (e.g. from Hanko), used only as the admin's label when the token carries no email.
    /// It never grants anything: the admin check reads the token only.</param>
    public async Task<MeDto> MeAsync(CancellationToken ct, string? label = null)
    {
        if (caller.IsGuest) return Me(null);
        var email = caller.Email ?? (label is { Length: <= UserAccess.MaxEmailLength } l && l.Contains('@') ? l.Trim() : null);
        var now = clock.GetUtcNow().UtcDateTime;
        var a = await db.UserAccess.FirstOrDefaultAsync(x => x.OwnerId == user.OwnerId, ct);
        if (a is null)
        {
            a = new UserAccess(user.OwnerId, email, now);
            db.UserAccess.Add(a);
            await db.SaveChangesAsync(ct);
        }
        else if (now - a.LastSeenAt > SeenEvery || (email is not null && a.Email != email))
        {
            a.Seen(email, now);
            await db.SaveChangesAsync(ct);
        }
        return Me(a);
    }

    /// <summary>The sign-up choice. Only once: afterwards only the admin changes it.</summary>
    public async Task<MeDto> ChooseAsync(RoleRequest request, CancellationToken ct)
    {
        if (caller.IsGuest) throw new RequestValidationException(new Dictionary<string, string[]> { ["role"] = ["Sign in to choose a role."] });
        var role = Parse(request?.Role);
        var now = clock.GetUtcNow().UtcDateTime;
        var a = await db.UserAccess.FirstOrDefaultAsync(x => x.OwnerId == user.OwnerId, ct);
        if (a is null)
        {
            a = new UserAccess(user.OwnerId, caller.Email, now);
            db.UserAccess.Add(a);
        }
        if (a.RoleChosen) throw new ConflictException("Your role is already set. Ask the admin to change it.");
        a.Choose(role, now);
        await db.SaveChangesAsync(ct);
        return Me(a);
    }

    public async Task<IReadOnlyList<AccessUserDto>> UsersAsync(CancellationToken ct) =>
        (await db.UserAccess.AsNoTracking().OrderByDescending(x => x.LastSeenAt).Take(500).ToListAsync(ct)).Select(Row).ToList();

    public async Task<AccessUserDto> SetRoleAsync(Guid accountId, RoleRequest request, CancellationToken ct)
    {
        var role = Parse(request?.Role);
        var a = await db.UserAccess.FirstOrDefaultAsync(x => x.OwnerId == accountId, ct) ?? throw new NotFoundException("User not found.");
        a.SetByAdmin(role, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return Row(a);
    }
}
