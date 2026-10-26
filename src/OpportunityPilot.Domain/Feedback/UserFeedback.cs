using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Feedback;

public enum FeedbackKind { Bug, Idea, Praise, Other }
public enum FeedbackStatus { New, Seen, Done }

/// <summary>
/// Something a person told us from inside the app (2026-10-26): a bug, an idea or praise, with the page they were on and an optional
/// 1–5 rating. The admin reads them in Settings and marks them seen or done; the triage loop is in docs/OBSERVABILITY.md.
/// </summary>
public sealed class UserFeedback
{
    public const int MaxMessageLength = 2_000;
    public const int MaxPageLength = 300;
    public const int MaxVersionLength = 64;
    public const int MaxEmailLength = 256;

    private UserFeedback() { }

    public UserFeedback(Guid ownerId, bool guest, string? email, FeedbackKind kind, int? rating, string message, string? page, string? appVersion, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating), "Rating 1 to 5.");
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Guest = guest;
        Email = Guard.Optional(email, MaxEmailLength, nameof(email));
        Kind = kind;
        Rating = rating;
        Message = Guard.Required(message, MaxMessageLength, nameof(message));
        Page = Guard.TruncateOptional(page, MaxPageLength);
        AppVersion = Guard.TruncateOptional(appVersion, MaxVersionLength);
        Status = FeedbackStatus.New;
        CreatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public bool Guest { get; private set; }
    public string? Email { get; private set; }
    public FeedbackKind Kind { get; private set; }
    public int? Rating { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string? Page { get; private set; }
    public string? AppVersion { get; private set; }
    public FeedbackStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? HandledAt { get; private set; }

    public void SetStatus(FeedbackStatus status, DateTime utcNow)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        HandledAt = status == FeedbackStatus.New ? null : utcNow;
    }
}

/// <summary>
/// An error the browser hit (an uncaught exception, a rejected promise, a page that failed to render), reported by the web app so problems
/// are seen without a third-party tracker. Same messages are counted, not stored twice an hour; kept 30 days.
/// </summary>
public sealed class ClientError
{
    public const int MaxMessageLength = 500;
    public const int MaxStackLength = 4_000;
    public const int MaxTextLength = 300;

    private ClientError() { }

    public ClientError(Guid? ownerId, string message, string? source, string? page, string? stack, string? userAgent, string? appVersion,
        string? correlationId, DateTime utcNow)
    {
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Message = Guard.Truncate(string.IsNullOrWhiteSpace(message) ? "Unknown error" : message, MaxMessageLength);
        Source = Guard.TruncateOptional(source, MaxTextLength);
        Page = Guard.TruncateOptional(page, MaxTextLength);
        Stack = Guard.TruncateOptional(stack, MaxStackLength);
        UserAgent = Guard.TruncateOptional(userAgent, MaxTextLength);
        AppVersion = Guard.TruncateOptional(appVersion, 64);
        CorrelationId = Guard.TruncateOptional(correlationId, 64);
        Count = 1;
        FirstSeenAt = utcNow;
        LastSeenAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid? OwnerId { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string? Source { get; private set; }
    public string? Page { get; private set; }
    public string? Stack { get; private set; }
    public string? UserAgent { get; private set; }
    public string? AppVersion { get; private set; }
    public string? CorrelationId { get; private set; }
    public int Count { get; private set; }
    public DateTime FirstSeenAt { get; private set; }
    public DateTime LastSeenAt { get; private set; }

    public void SeenAgain(DateTime utcNow)
    {
        Count = Count >= int.MaxValue - 1 ? Count : Count + 1;
        LastSeenAt = utcNow;
    }
}
