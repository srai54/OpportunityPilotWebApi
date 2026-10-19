using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

/// <summary>A buyer or stakeholder attached to an owner-scoped staffing account.</summary>
public sealed class StaffingContact : IOwned
{
    public const int MaxNameLength = 200;
    public const int MaxTitleLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxLinkedInUrlLength = 1_000;
    public const int MaxEvidenceLength = 1_000;
    /// <summary>Same limit as <see cref="StaffingCandidate.MaxContactLength"/> so a phone fits wherever a candidate's does.</summary>
    public const int MaxPhoneLength = StaffingCandidate.MaxContactLength;

    private StaffingContact() { }

    public StaffingContact(Guid ownerId, Guid accountId, string name, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (accountId == Guid.Empty) throw new ArgumentException("Account is required.", nameof(accountId));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        AccountId = accountId;
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid AccountId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Title { get; private set; }
    public string? Email { get; private set; }
    public string? LinkedInUrl { get; private set; }
    public string? Phone { get; private set; }
    public string? Evidence { get; private set; }
    public bool EmailVerified { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public void Update(string name, string? title, string? email, bool emailVerified, string? linkedInUrl, string? evidence, DateTime utcNow) =>
        Update(name, title, email, emailVerified, linkedInUrl, evidence, Phone, utcNow);

    public void Update(
        string name, string? title, string? email, bool emailVerified, string? linkedInUrl, string? evidence, string? phone, DateTime utcNow)
    {
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        Title = Guard.Optional(title, MaxTitleLength, nameof(title));
        Email = Guard.Optional(email, MaxEmailLength, nameof(email));
        LinkedInUrl = Guard.Optional(linkedInUrl, MaxLinkedInUrlLength, nameof(linkedInUrl));
        Evidence = Guard.Optional(evidence, MaxEvidenceLength, nameof(evidence));
        Phone = Guard.Optional(phone, MaxPhoneLength, nameof(phone));
        EmailVerified = emailVerified && Email is not null;
        Version++;
        UpdatedAt = utcNow;
    }
}
