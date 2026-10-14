using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Application.Ai;

public enum AiSource { Gemini, Rules, Template }
public sealed record GoalPreviewRequest(Guid ProfileId, string Goal, OpportunityMode? Mode);
public sealed record GoalPreviewDto(AiSource Source, string? FallbackReason, OpportunityMode? Mode,
    CampaignCriteria Criteria, IReadOnlyList<string> Ambiguities, IReadOnlyList<string> Notes);
public sealed record AiStatusDto(bool Active, string Provider, string Model, string? FallbackReason,
    int CallsToday, int DailyLimit, int MaxCallsPerRun, object? LastFailure, bool RewriteAvailable);

/// <summary>What one AI call produced. <see cref="Json"/> is null when it failed; <see cref="Outcome"/> says why.</summary>
public sealed record LlmResult(string? Json, Domain.Ai.AiOutcome Outcome, string? Reason, int Attempts, TimeSpan Duration);

public interface ILlmClient
{
    Task<LlmResult> GenerateJsonAsync(string operation, string system, string input, CancellationToken ct);
}

public sealed record AiUsageSummary(int CallsToday, int DailyLimit, string? LastFailure, DateTime? LastFailureAt);
