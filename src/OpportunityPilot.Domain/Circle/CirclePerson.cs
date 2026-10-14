using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Circle;

/// <summary>
/// One person around a job or company lead (the "JD circle"): a recruiter, the likely hiring manager, someone on the team, or one of
/// the person's own connections at the company. Unique per (owner, opportunity, profile link). Only a LinkedIn profile link is stored —
/// no email, phone or anything read from the profile page beyond the search card (name, headline, connection degree).
/// </summary>
public sealed class CirclePerson : IOwned
{
    public const int MaxNameLength = 200;
    public const int MaxHeadlineLength = 300;

    private CirclePerson() { }

    public CirclePerson(Guid ownerId, Guid opportunityId, string name, string? headline, string profileUrl, CircleRelation relation,
        int? degree, CircleSource source, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (opportunityId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(opportunityId));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (!LinkedInProfileUrl.TryNormalize(profileUrl, out var url))
            throw new ArgumentException("profileUrl must be a LinkedIn profile link (https://www.linkedin.com/in/…).", nameof(profileUrl));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        OpportunityId = opportunityId;
        ProfileUrl = url;
        Source = source;
        FoundAt = utcNow;
        Apply(name, headline, relation, degree, utcNow);
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Headline { get; private set; }
    public string ProfileUrl { get; private set; } = string.Empty;
    public CircleRelation Relation { get; private set; }
    /// <summary>LinkedIn connection degree as the person sees it: 1, 2 or 3 (3 = "3rd+"); null when unknown.</summary>
    public int? Degree { get; private set; }
    public CircleSource Source { get; private set; }
    public DateTime FoundAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// A later search found the same person again: refresh the name, headline and degree. The relation the person chose by hand
    /// (a Manual entry) is kept; otherwise the newest classification wins.
    /// </summary>
    public void Refresh(string name, string? headline, CircleRelation relation, int? degree, DateTime utcNow) =>
        Apply(name, headline ?? Headline, Source == CircleSource.Manual ? Relation : relation, degree ?? Degree, utcNow);

    private void Apply(string name, string? headline, CircleRelation relation, int? degree, DateTime utcNow)
    {
        if (!Enum.IsDefined(relation)) throw new ArgumentOutOfRangeException(nameof(relation));
        if (degree is not null and not (1 or 2 or 3)) throw new ArgumentOutOfRangeException(nameof(degree), "degree must be 1, 2, 3 or empty.");
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        Headline = Guard.Optional(headline, MaxHeadlineLength, nameof(headline));
        Relation = relation;
        Degree = degree;
        UpdatedAt = utcNow;
    }
}
