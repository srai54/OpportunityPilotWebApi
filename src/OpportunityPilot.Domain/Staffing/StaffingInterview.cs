using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Staffing;

public enum InterviewState { Requested, Scheduled, Completed, Cancelled, NoShow }
public enum InterviewMode { Video, Phone, Onsite }
public enum NotificationStatus { NotNotified, NotifiedManually, NotifiedByEmail }

/// <summary>One interview round for a submitted candidate. Candidate-visible notes are kept apart from internal notes.</summary>
public sealed class StaffingInterview : IOwned
{
    public const int MaxNotesLength = 2_000;
    public const int MaxLocationLength = 1_000;
    public const int MaxTimeZoneLength = 64;

    private StaffingInterview() { }

    public StaffingInterview(Guid ownerId, Guid dealId, Guid submissionId, int round, DateTime utcNow)
    {
        if (ownerId == Guid.Empty || dealId == Guid.Empty || submissionId == Guid.Empty)
            throw new ArgumentException("Owner, deal and submission are required.");
        if (round is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(round));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        DealId = dealId;
        SubmissionId = submissionId;
        Round = round;
        State = InterviewState.Requested;
        CandidateNotification = NotificationStatus.NotNotified;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid DealId { get; private set; }
    public Guid SubmissionId { get; private set; }
    public int Round { get; private set; }
    public InterviewState State { get; private set; }
    public DateTime? ScheduledAt { get; private set; }
    public string? TimeZone { get; private set; }
    public int? DurationMinutes { get; private set; }
    public InterviewMode? Mode { get; private set; }
    /// <summary>Meeting link or address.</summary>
    public string? Location { get; private set; }
    public string? CandidateNotes { get; private set; }
    public string? InternalNotes { get; private set; }
    /// <summary>Prep tips for the candidate (who they meet, what to prepare). Shown on the progress link only when the team shares prep.</summary>
    public string? CandidatePrep { get; private set; }
    public NotificationStatus CandidateNotification { get; private set; }
    public DateTime? CandidateNotifiedAt { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Scheduling or rescheduling resets the candidate notification, so a changed time is never assumed to be known.</summary>
    public void Schedule(DateTime scheduledAtUtc, string timeZone, int durationMinutes, InterviewMode mode, string? location,
        string? candidateNotes, string? internalNotes, DateTime utcNow)
    {
        if (State is InterviewState.Completed or InterviewState.Cancelled or InterviewState.NoShow)
            throw new InvalidOperationException("A finished interview cannot be rescheduled.");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (durationMinutes is < 5 or > 600) throw new ArgumentException("Duration must be 5–600 minutes.", nameof(durationMinutes));
        var changed = ScheduledAt != scheduledAtUtc || Location != location?.Trim();
        ScheduledAt = DateTime.SpecifyKind(scheduledAtUtc, DateTimeKind.Utc);
        TimeZone = Guard.Required(timeZone, MaxTimeZoneLength, nameof(timeZone));
        DurationMinutes = durationMinutes;
        Mode = mode;
        Location = Guard.Optional(location, MaxLocationLength, nameof(location));
        CandidateNotes = Guard.Optional(candidateNotes, MaxNotesLength, nameof(candidateNotes));
        InternalNotes = Guard.Optional(internalNotes, MaxNotesLength, nameof(internalNotes));
        State = InterviewState.Scheduled;
        if (changed) { CandidateNotification = NotificationStatus.NotNotified; CandidateNotifiedAt = null; }
        Version++;
        UpdatedAt = utcNow;
    }

    /// <summary>Sets the candidate's prep tips. It does not count as a change to the interview (no new version, same "last change"),
    /// so scheduling and the progress stages behave as before.</summary>
    public void SetCandidatePrep(string? prep) => CandidatePrep = Guard.Optional(prep, MaxNotesLength, nameof(prep));

    public void Finish(InterviewState outcome, DateTime utcNow)
    {
        if (outcome is not (InterviewState.Completed or InterviewState.Cancelled or InterviewState.NoShow))
            throw new ArgumentException("Finish with Completed, Cancelled or NoShow.", nameof(outcome));
        if (outcome != InterviewState.Cancelled && State != InterviewState.Scheduled)
            throw new InvalidOperationException("Only a scheduled interview can be completed or marked as a no-show.");
        State = outcome;
        Version++;
        UpdatedAt = utcNow;
    }

    public void RecordCandidateNotified(NotificationStatus status, DateTime utcNow)
    {
        if (status == NotificationStatus.NotNotified || !Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (State != InterviewState.Scheduled && State != InterviewState.Cancelled)
            throw new InvalidOperationException("Notify the candidate once the interview is scheduled or cancelled.");
        CandidateNotification = status;
        CandidateNotifiedAt = utcNow;
        Version++;
        UpdatedAt = utcNow;
    }
}
