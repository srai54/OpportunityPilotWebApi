using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Opportunities;

/// <summary>
/// A scored candidate in one campaign, unique per (campaign, <see cref="DedupeKey"/>). Research may re-score it any
/// number of times; the only status move research makes is New → Suggested (<see cref="SuggestForApproval"/>).
/// Every other move belongs to the user (or a confirmed agent report): a rerun never resets a Shortlisted or Applied
/// opportunity.
/// </summary>
public class Opportunity : IOwned
{
    public const int MaxDedupeKeyLength = 400;
    public const int MaxTitleLength = 300;
    public const int MaxOrganizationLength = 300;
    public const int MaxLocationLength = 200;
    public const int MaxUrlLength = 1000;
    public const int MaxExternalIdLength = 100;
    public const int MaxDescriptionLength = 4000;
    public const int MaxOutcomeReasonLength = 500;

    private Opportunity() { }

    public Opportunity(Guid ownerId, Guid campaignId, OpportunityMode mode, string dedupeKey, DateTime utcNow)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (campaignId == Guid.Empty) throw new ArgumentException("Campaign is required.", nameof(campaignId));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        CampaignId = campaignId;
        Mode = mode;
        DedupeKey = Guard.Required(dedupeKey, MaxDedupeKeyLength, nameof(dedupeKey));
        Status = OpportunityStatus.New;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid CampaignId { get; private set; }
    public OpportunityMode Mode { get; private set; }
    public string DedupeKey { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;

    /// <summary>Empty when the source did not say (e.g. a pasted posting with no "Company:" line).</summary>
    public string Organization { get; private set; } = string.Empty;

    public string? Location { get; private set; }
    public string? Url { get; private set; }
    public string? ApplyUrl { get; private set; }
    public JobPlatform? Platform { get; private set; }
    public string? ExternalId { get; private set; }
    public string? Description { get; private set; }
    public int Score { get; private set; }
    public int Coverage { get; private set; }
    public FilterOutcome Outcome { get; private set; }
    public string? OutcomeReason { get; private set; }
    public OpportunityStatus Status { get; private set; }
    public string BreakdownJson { get; private set; } = "[]";
    public string FactsJson { get; private set; } = "[]";
    public string GapsJson { get; private set; } = "[]";

    /// <summary>Denormalised from <see cref="GapsJson"/> so list queries need not parse JSON.</summary>
    public int GapsCount { get; private set; }

    public Guid? LastResearchJobId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public int Version { get; private set; }

    /// <summary>When the person last asked the desktop agent to map the people around this lead (JD circle); null = never.</summary>
    public DateTime? CircleRequestedAt { get; private set; }

    /// <summary>When the agent last delivered a circle (possibly empty) for this lead.</summary>
    public DateTime? CircleMappedAt { get; private set; }

    /// <summary>True while a circle request waits for the agent: requested, and nothing delivered since.</summary>
    public bool CircleWaiting => CircleRequestedAt is { } requested && (CircleMappedAt is not { } mapped || mapped < requested);

    /// <summary>
    /// Queues a circle mapping for the desktop agent. Idempotent while one is waiting (the first request time is kept). Not a pipeline
    /// change, so <see cref="Version"/> is left alone.
    /// </summary>
    public void RequestCircle(DateTime utcNow)
    {
        if (CircleWaiting) return;
        CircleRequestedAt = utcNow;
    }

    /// <summary>The agent delivered its search results (an empty result counts: it looked and found nobody).</summary>
    public void MarkCircleMapped(DateTime utcNow) => CircleMappedAt = utcNow;

    /// <summary>Overwrites what research knows about the candidate. Deliberately leaves <see cref="Status"/> alone.</summary>
    public void ApplyResearch(
        string title, string? organization, string? location, string? url, string? applyUrl,
        JobPlatform? platform, string? externalId, string? description,
        int score, int coverage, FilterOutcome outcome, string? outcomeReason,
        string breakdownJson, string factsJson, string gapsJson, int gapsCount,
        Guid researchJobId, DateTime utcNow)
    {
        if (score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(score));
        if (coverage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(coverage));
        Title = Guard.Truncate(Guard.Required(title, int.MaxValue, nameof(title)), MaxTitleLength);
        Organization = Guard.Truncate(organization, MaxOrganizationLength);
        Location = Guard.TruncateOptional(location, MaxLocationLength);
        Url = UrlOrNull(url);
        ApplyUrl = UrlOrNull(applyUrl);
        Platform = platform;
        ExternalId = Guard.TruncateOptional(externalId, MaxExternalIdLength);
        Description = Guard.TruncateOptional(description, MaxDescriptionLength);
        Score = score;
        Coverage = coverage;
        Outcome = outcome;
        OutcomeReason = Guard.TruncateOptional(outcomeReason, MaxOutcomeReasonLength);
        BreakdownJson = string.IsNullOrWhiteSpace(breakdownJson) ? "[]" : breakdownJson;
        FactsJson = string.IsNullOrWhiteSpace(factsJson) ? "[]" : factsJson;
        GapsJson = string.IsNullOrWhiteSpace(gapsJson) ? "[]" : gapsJson;
        GapsCount = Math.Max(0, gapsCount);
        LastResearchJobId = researchJobId;
        UpdatedAt = utcNow;
        Version++;
    }

    /// <summary>A user-chosen pipeline move. Returns the activity to record, or null when the status is unchanged.</summary>
    public Activity? ChangeStatus(OpportunityStatus status, DateTime utcNow)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (status == Status) return null;
        if (Status == OpportunityStatus.Applied && status is OpportunityStatus.New or OpportunityStatus.Suggested or OpportunityStatus.Shortlisted or OpportunityStatus.Dismissed)
            throw new InvalidOperationException("An applied opportunity cannot be moved back to an earlier pipeline stage.");
        var from = Status;
        Status = status;
        UpdatedAt = utcNow;
        Version++;
        return new Activity(OwnerId, Id, ActivityKinds.StatusChanged, utcNow, $"{from} → {status}");
    }

    /// <summary>
    /// The research rule for the batch approval queue: a <b>New</b> Job opportunity that is Qualified and scores at
    /// least <paramref name="minScore"/> becomes Suggested. Any other status (Shortlisted, Dismissed, Applied…) is never
    /// touched, and null <paramref name="minScore"/> (auto-suggest off) does nothing. Call after the latest scoring.
    /// </summary>
    public Activity? SuggestForApproval(int? minScore, DateTime utcNow)
    {
        if (minScore is not { } threshold || Mode != OpportunityMode.Job || Status != OpportunityStatus.New ||
            Outcome != FilterOutcome.Qualified || Score < threshold)
            return null;
        Status = OpportunityStatus.Suggested;
        UpdatedAt = utcNow;
        Version++;
        return new Activity(OwnerId, Id, ActivityKinds.Suggested, utcNow, $"Suggested for approval: scored {Score} ≥ {threshold}.");
    }

    /// <summary>
    /// Hands-off approval: a New (or Suggested) Qualified Job opportunity scoring at least <paramref name="minScore"/> goes straight to
    /// Shortlisted. Null (off), another mode, another status, or a lower score does nothing and returns null.
    /// </summary>
    public Activity? ApproveAutomatically(int? minScore, DateTime utcNow)
    {
        if (minScore is not { } threshold || Mode != OpportunityMode.Job || Outcome != FilterOutcome.Qualified || Score < threshold ||
            Status is not (OpportunityStatus.New or OpportunityStatus.Suggested))
            return null;
        Status = OpportunityStatus.Shortlisted;
        UpdatedAt = utcNow;
        Version++;
        return new Activity(OwnerId, Id, ActivityKinds.Approved, utcNow, $"Approved automatically: scored {Score} ≥ {threshold}.");
    }

    /// <summary>
    /// The user's decision in the approval queue: approve → Shortlisted, reject → Dismissed. Only a Suggested
    /// opportunity can be decided; anything else returns null and is left alone (it was decided or moved meanwhile).
    /// </summary>
    public Activity? DecideSuggestion(bool approve, DateTime utcNow)
    {
        if (Status != OpportunityStatus.Suggested) return null;
        Status = approve ? OpportunityStatus.Shortlisted : OpportunityStatus.Dismissed;
        UpdatedAt = utcNow;
        Version++;
        return approve
            ? new Activity(OwnerId, Id, ActivityKinds.Approved, utcNow, "Approved: Suggested → Shortlisted.")
            : new Activity(OwnerId, Id, ActivityKinds.Rejected, utcNow, "Rejected: Suggested → Dismissed.");
    }

    /// <summary>The desktop agent confirmed it submitted the application. Idempotent.</summary>
    public Activity? MarkApplied(string detail, DateTime utcNow)
    {
        if (Status == OpportunityStatus.Applied) return null;
        Status = OpportunityStatus.Applied;
        UpdatedAt = utcNow;
        Version++;
        return new Activity(OwnerId, Id, ActivityKinds.Applied, utcNow, detail);
    }

    // A URL longer than the column is not worth keeping truncated: a cut URL points somewhere else.
    private static string? UrlOrNull(string? url) =>
        string.IsNullOrWhiteSpace(url) || url.Trim().Length > MaxUrlLength ? null : url.Trim();
}
