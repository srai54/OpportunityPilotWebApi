namespace OpportunityPilot.Domain.Access;

/// <summary>Which views a user may use. None means "not chosen yet": that user keeps both views, exactly as before roles existed.</summary>
[Flags]
public enum UserRole
{
    None = 0,
    Candidate = 1,
    Sales = 2,
    Both = Candidate | Sales
}

/// <summary>
/// One signed-in user's access: the role they chose at sign-up or the admin set, and when they were last seen (so the admin can find
/// them). Keyed by the owner id from the validated token. The email is a label for the admin only; it never grants anything.
/// </summary>
public sealed class UserAccess
{
    public const int MaxEmailLength = 256;

    private UserAccess() { }

    public UserAccess(Guid ownerId, string? email, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        OwnerId = ownerId;
        Email = Clip(email);
        Role = UserRole.None;
        FirstSeenAt = utcNow;
        LastSeenAt = utcNow;
    }

    public Guid OwnerId { get; private set; }
    public string? Email { get; private set; }
    public UserRole Role { get; private set; }
    public DateTime FirstSeenAt { get; private set; }
    public DateTime LastSeenAt { get; private set; }
    public DateTime? RoleSetAt { get; private set; }
    /// <summary>True when the admin set the role (rather than the user at sign-up).</summary>
    public bool RoleSetByAdmin { get; private set; }

    public bool RoleChosen => Role != UserRole.None;

    private static string? Clip(string? email)
    {
        var e = email?.Trim();
        return string.IsNullOrEmpty(e) ? null : e[..Math.Min(e.Length, MaxEmailLength)];
    }

    public void Seen(string? email, DateTime utcNow)
    {
        if (Clip(email) is { } e) Email = e;
        LastSeenAt = utcNow;
    }

    /// <summary>The user's own choice at sign-up; only while no role is set, so a user cannot widen their own access later.</summary>
    public void Choose(UserRole role, DateTime utcNow)
    {
        if (RoleChosen) throw new InvalidOperationException("The role is already set. Ask the admin to change it.");
        Set(role, utcNow, byAdmin: false);
    }

    public void SetByAdmin(UserRole role, DateTime utcNow) => Set(role, utcNow, byAdmin: true);

    private void Set(UserRole role, DateTime utcNow, bool byAdmin)
    {
        if (role is not (UserRole.Candidate or UserRole.Sales or UserRole.Both)) throw new ArgumentOutOfRangeException(nameof(role), "Choose Candidate, Sales or Both.");
        Role = role;
        RoleSetAt = utcNow;
        RoleSetByAdmin = byAdmin;
    }
}
