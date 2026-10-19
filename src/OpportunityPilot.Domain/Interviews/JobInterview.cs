using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Interviews;

/// <summary>Stored as strings.</summary>
public enum JobInterviewStatus { Scheduled, Completed, NoShow, Cancelled }

/// <summary>
/// One interview round the candidate recorded for a job they applied to (Candidate workspace). The person types it in: nothing is
/// read from a calendar or a job board. The meeting link must be http(s).
/// </summary>
public sealed class JobInterview : IOwned
{
    public const int MaxLinkLength = 1_000;
    public const int MaxNotesLength = 2_000;
    public const int MaxRound = 20;

    private JobInterview() { }

    public JobInterview(Guid ownerId, Guid opportunityId, int round, DateTime? scheduledAtUtc, string? meetingUrl, string? notes, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (opportunityId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(opportunityId));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        OpportunityId = opportunityId;
        Status = JobInterviewStatus.Scheduled;
        CreatedAt = utcNow;
        Apply(round, scheduledAtUtc, meetingUrl, notes, utcNow);
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public int Round { get; private set; }
    /// <summary>UTC; null while the time is not agreed yet.</summary>
    public DateTime? ScheduledAt { get; private set; }
    public string? MeetingUrl { get; private set; }
    public string? Notes { get; private set; }
    public JobInterviewStatus Status { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Edits the details. The status is changed with <see cref="SetStatus"/>.</summary>
    public void Update(int round, DateTime? scheduledAtUtc, string? meetingUrl, string? notes, DateTime utcNow)
    {
        Apply(round, scheduledAtUtc, meetingUrl, notes, utcNow);
        Version++;
    }

    public void SetStatus(JobInterviewStatus status, DateTime utcNow)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (status is JobInterviewStatus.Completed or JobInterviewStatus.NoShow && ScheduledAt is null)
            throw new InvalidOperationException("Add the date first: an interview without a time cannot be done or missed.");
        if (status == Status) return;
        Status = status;
        Version++;
        UpdatedAt = utcNow;
    }

    /// <summary>True for an http(s) address of at most <see cref="MaxLinkLength"/> characters (or nothing).</summary>
    public static bool IsValidLink(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        (url.Trim().Length <= MaxLinkLength && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp));

    private void Apply(int round, DateTime? scheduledAtUtc, string? meetingUrl, string? notes, DateTime utcNow)
    {
        if (round is < 1 or > MaxRound) throw new ArgumentOutOfRangeException(nameof(round), $"round must be 1–{MaxRound}.");
        if (!IsValidLink(meetingUrl)) throw new ArgumentException("meetingUrl must be an http(s) link.", nameof(meetingUrl));
        Round = round;
        ScheduledAt = scheduledAtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
        MeetingUrl = Guard.Optional(meetingUrl, MaxLinkLength, nameof(meetingUrl));
        Notes = Guard.Optional(notes, MaxNotesLength, nameof(notes));
        UpdatedAt = utcNow;
    }
}
