using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Domain.Campaigns;

/// <summary>
/// What to look for, judged against one of the owner's profiles. Criteria and weights are stored as JSON
/// the application layer validates and normalises; every save increments <see cref="Version"/>.
/// </summary>
public class Campaign : IOwned
{
    public const int MaxNameLength = 200;
    public const int MaxGoalLength = 2000;
    public const int MinResultLimit = 1;
    public const int MaxResultLimit = 100;
    public const int DefaultResultLimit = 25;
    public const int MinAutoSuggestScore = 1;
    public const int MaxAutoSuggestScore = 100;

    private Campaign() { }

    public Campaign(
        Guid ownerId, Guid profileId, OpportunityMode mode, string name, string? goal,
        string criteriaJson, string weightsJson, int resultLimit, DateTime utcNow, int? autoSuggestMinScore = null)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (profileId == Guid.Empty) throw new ArgumentException("Profile is required.", nameof(profileId));
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        ProfileId = profileId;
        Mode = mode;
        CreatedAt = utcNow;
        Version = 0;
        Apply(name, goal, criteriaJson, weightsJson, resultLimit, autoSuggestMinScore, utcNow);
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid ProfileId { get; private set; }

    /// <summary>Fixed at creation: criteria keys and weights only make sense for one mode.</summary>
    public OpportunityMode Mode { get; private set; }

    public string Name { get; private set; } = string.Empty;
    public string Goal { get; private set; } = string.Empty;
    public string CriteriaJson { get; private set; } = "{}";
    public string WeightsJson { get; private set; } = "{}";
    public int ResultLimit { get; private set; }

    /// <summary>
    /// Batch approval: research moves New, Qualified Job opportunities scoring at least this much to Suggested.
    /// Null means off. Only Job campaigns may set it.
    /// </summary>
    public int? AutoSuggestMinScore { get; private set; }

    /// <summary>
    /// Hands-off mode: research moves New, Qualified Job opportunities scoring at least this much straight to Shortlisted (approved),
    /// with an activity line saying it was automatic. Null means off. Only Job campaigns may set it. Nothing is applied by this:
    /// the desktop agent (LinkedIn, Naukri, Instahyre) or the person does that.
    /// </summary>
    public int? AutoApproveMinScore { get; private set; }

    public void SetAutoApprove(int? minScore)
    {
        if (minScore is < MinAutoSuggestScore or > MaxAutoSuggestScore)
            throw new ArgumentOutOfRangeException(nameof(minScore), $"Auto-approve score must be {MinAutoSuggestScore}–{MaxAutoSuggestScore}.");
        // Job and Sales (Customer) campaigns alike since 2026-10-16 (U17-13b); approving only marks a lead, sending needs a tap.
        AutoApproveMinScore = minScore;
    }

    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public void Update(string name, string? goal, string criteriaJson, string weightsJson, int resultLimit, int? autoSuggestMinScore,
        DateTime utcNow) =>
        Apply(name, goal, criteriaJson, weightsJson, resultLimit, autoSuggestMinScore, utcNow);

    private void Apply(string name, string? goal, string criteriaJson, string weightsJson, int resultLimit, int? autoSuggestMinScore,
        DateTime utcNow)
    {
        if (resultLimit is < MinResultLimit or > MaxResultLimit)
            throw new ArgumentOutOfRangeException(nameof(resultLimit), $"Result limit must be {MinResultLimit}–{MaxResultLimit}.");
        if (autoSuggestMinScore is < MinAutoSuggestScore or > MaxAutoSuggestScore)
            throw new ArgumentOutOfRangeException(nameof(autoSuggestMinScore),
                $"Auto-suggest score must be {MinAutoSuggestScore}–{MaxAutoSuggestScore}.");
        if (autoSuggestMinScore is not null && Mode != OpportunityMode.Job)
            throw new ArgumentException("Only Job campaigns can auto-suggest.", nameof(autoSuggestMinScore));
        Name = Guard.Required(name, MaxNameLength, nameof(name));
        Goal = Guard.Optional(goal, MaxGoalLength, nameof(goal)) ?? string.Empty;
        CriteriaJson = string.IsNullOrWhiteSpace(criteriaJson) ? "{}" : criteriaJson;
        WeightsJson = string.IsNullOrWhiteSpace(weightsJson) ? "{}" : weightsJson;
        ResultLimit = resultLimit;
        AutoSuggestMinScore = autoSuggestMinScore;
        UpdatedAt = utcNow;
        Version++;
    }
}
