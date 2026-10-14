using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Outreach;

public enum ExecutionSubject { OutreachDraft, SalesBid }
/// <summary>Stored as a string. LinkedInAgent = the desktop agent sent it from the person's own logged-in LinkedIn browser.</summary>
public enum ExecutionProvider { Manual, Gmail, Freelancer, Upwork, Outlook, Smtp, LinkedInAgent }
public enum ExecutionState { AwaitingManualConfirmation, Succeeded, Failed }

/// <summary>
/// One attempt to carry out an approved action (send a message, place a bid) through a provider. The idempotency key is
/// derived from the subject and the approved content hash and is unique per owner, so the same approved content can only
/// ever produce one execution — a retry returns the existing one instead of acting twice. Success requires a receipt.
/// </summary>
public sealed class ProviderExecution : IOwned
{
    public const int MaxKeyLength = 200;
    public const int MaxReceiptLength = 1_000;
    public const int MaxReferenceLength = 300;
    public const int MaxFailureLength = 500;

    private ProviderExecution() { }

    public ProviderExecution(Guid ownerId, ExecutionSubject subject, Guid subjectId, int subjectVersion, string contentHash,
        ExecutionProvider provider, DateTime utcNow, int attempt = 1)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (ownerId == Guid.Empty || subjectId == Guid.Empty) throw new ArgumentException("Owner and subject are required.");
        if (!Enum.IsDefined(subject) || !Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(subject));
        if (string.IsNullOrWhiteSpace(contentHash)) throw new ArgumentException("An approved content hash is required.", nameof(contentHash));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Subject = subject;
        SubjectId = subjectId;
        SubjectVersion = subjectVersion;
        ContentHash = contentHash;
        Provider = provider;
        IdempotencyKey = $"{KeyFor(subject, subjectId, contentHash)}:{attempt}";
        Attempt = attempt;
        // Only a manual provider exists today; an API connector would execute here and record its own receipt.
        State = ExecutionState.AwaitingManualConfirmation;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>The key shared by every attempt at the same approved content; each attempt appends its number.</summary>
    public static string KeyFor(ExecutionSubject subject, Guid subjectId, string contentHash) => $"{subject}:{subjectId:N}:{contentHash}";

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public ExecutionSubject Subject { get; private set; }
    public Guid SubjectId { get; private set; }
    public int SubjectVersion { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public ExecutionProvider Provider { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    /// <summary>1 for the first attempt; a new attempt is allowed only after the previous one failed.</summary>
    public int Attempt { get; private set; }
    public ExecutionState State { get; private set; }
    public string? Receipt { get; private set; }
    public string? ProviderReference { get; private set; }
    public string? SafeFailure { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public void Succeed(string receipt, string? providerReference, DateTime utcNow)
    {
        if (State != ExecutionState.AwaitingManualConfirmation) throw new InvalidOperationException($"This execution is already {State}.");
        Receipt = Guard.Required(receipt, MaxReceiptLength, nameof(receipt));
        ProviderReference = Guard.Optional(providerReference, MaxReferenceLength, nameof(providerReference));
        State = ExecutionState.Succeeded;
        CompletedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public void Fail(string reason, DateTime utcNow)
    {
        if (State != ExecutionState.AwaitingManualConfirmation) throw new InvalidOperationException($"This execution is already {State}.");
        SafeFailure = Guard.Required(reason, MaxFailureLength, nameof(reason));
        State = ExecutionState.Failed;
        CompletedAt = utcNow;
        UpdatedAt = utcNow;
    }
}
