using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

public enum JourneyKind { Learning, Certification, Project, InterviewPrep, Note }
public enum JourneyAuthor { Candidate, Sales }

/// <summary>
/// One thing a resource did (or has booked) since joining the bench: a course, a certification, a project they built, interview prep
/// or a note. Logged by the candidate through their private link or by the sales team; the bench and the resource's 360 view read them.
/// A future date is an upcoming event (an exam booked, a course starting).
/// </summary>
public sealed class CandidateJourneyEntry : IOwned
{
    public const int MaxTitleLength = 300;
    public const int MaxDetailLength = 1_000;

    private CandidateJourneyEntry() { }

    public CandidateJourneyEntry(Guid ownerId, Guid candidateId, JourneyKind kind, string title, string? detail, DateTime occurredAt, int? hours,
        JourneyAuthor addedBy, DateTime utcNow)
    {
        if (ownerId == Guid.Empty || candidateId == Guid.Empty) throw new ArgumentException("Owner and candidate are required.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (hours is < 0 or > 2_000) throw new ArgumentOutOfRangeException(nameof(hours), "Hours 0 to 2000.");
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        CandidateId = candidateId;
        Kind = kind;
        Title = Guard.Required(title, MaxTitleLength, nameof(title));
        Detail = Guard.Optional(detail, MaxDetailLength, nameof(detail));
        OccurredAt = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc);
        Hours = hours;
        AddedBy = addedBy;
        CreatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid CandidateId { get; private set; }
    public JourneyKind Kind { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Detail { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public int? Hours { get; private set; }
    public JourneyAuthor AddedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
}
