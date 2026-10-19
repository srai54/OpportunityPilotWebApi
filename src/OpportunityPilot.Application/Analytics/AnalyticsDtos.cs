using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Analytics;

/// <summary>Candidate is the Job campaign mode, Sales the Customer mode.</summary>
public enum AnalyticsWorkspace
{
    Candidate,
    Sales
}

public enum AttentionKind
{
    Approvals,
    ShortlistedNotApplied,
    AgentNeedsYou,
    SourceFailing,
    FollowUpsOverdue
}

/// <summary>
/// The v2 overview for one workspace (docs/feature-contracts.md). Every number is computed from the caller's stored data;
/// a field the stored data cannot answer is null, never an estimate.
/// </summary>
/// <param name="ApplicationsPerDay">Candidate only: the last 14 UTC days, oldest first. Null for Sales.</param>
/// <param name="QualifiedByIndustry">Sales only; null for Candidate.</param>
/// <param name="SignalsFound">Sales only; null for Candidate.</param>
public sealed record AnalyticsOverviewDto(
    AnalyticsWorkspace Workspace,
    int Days,
    DateTime GeneratedAt,
    int CampaignCount,
    AnalyticsKpisDto Kpis,
    IReadOnlyList<FunnelStageDto> Funnel,
    FitHistogramDto FitHistogram,
    IReadOnlyList<UnknownCriterionDto> UnknownCriteria,
    IReadOnlyList<SourceYieldDto> Sources,
    IReadOnlyList<ApplicationsDayDto>? ApplicationsPerDay,
    IReadOnlyList<AttentionItemDto> Attention,
    ActiveResearchDto? ActiveResearch,
    IReadOnlyList<IndustryCountDto>? QualifiedByIndustry,
    IReadOnlyList<SignalCountDto>? SignalsFound,
    SalesOutreachDto? Outreach = null);

/// <summary>Sales only. Counted now (not windowed) from stored drafts, bids and follow-ups; nothing is estimated.</summary>
/// <param name="FollowUpsDue">Open follow-ups due in the next 7 days.</param>
/// <param name="ReplyRate">Responded / contacted in the window; null when nothing was contacted.</param>
public sealed record SalesOutreachDto(int DraftsAwaitingReview, int DraftsApproved, int BidsPlaced, int BidsFailed,
    int FollowUpsDue, int FollowUpsOverdue, double? ReplyRate);

/// <param name="QualifyRate">Qualified / found, 0–1 (4 decimals); null when nothing was found.</param>
/// <param name="Applied">Candidate: opportunities whose first move to Applied (or beyond) happened in the window. Null for Sales.</param>
/// <param name="AppliedByAgent">Of <paramref name="Applied"/>, moved by an agent report (activity kind Applied).</param>
/// <param name="AppliedByYou">Of <paramref name="Applied"/>, moved by the user's status change (activity kind StatusChanged).</param>
/// <param name="Contacted">Null in both workspaces until Outreach exists.</param>
/// <param name="Responded">Candidate: first reached Responded (or beyond) in the window. Null for Sales.</param>
/// <param name="RespondedRate">Responded / applied; null when applied is 0 or null.</param>
public sealed record AnalyticsKpisDto(
    int Found,
    int Qualified,
    double? QualifyRate,
    int AwaitingApproval,
    int Shortlisted,
    int ShortlistedNotApplied,
    int? Applied,
    int? AppliedByAgent,
    int? AppliedByYou,
    int? Contacted,
    int? Responded,
    double? RespondedRate,
    int AgentNeedsYou);

/// <param name="Count">Null when the stage cannot be measured with stored data (Sales contacted/responded).</param>
public sealed record FunnelStageDto(string Key, string Label, int? Count, string Note);

/// <param name="To">Inclusive; the last band is 90–100.</param>
public sealed record HistogramBandDto(int From, int To, int Count);

/// <param name="Threshold">The most common AutoSuggestMinScore among the workspace's campaigns (ties: the higher one); null when none set.</param>
/// <param name="AboveThreshold">Qualified opportunities in the window scoring at least the threshold; null without a threshold.</param>
public sealed record FitHistogramDto(IReadOnlyList<HistogramBandDto> Bands, int? Threshold, int? AboveThreshold);

public sealed record UnknownCriterionDto(string Criterion, string Label, int UnknownCount);

/// <param name="Read">Items the source yielded on its last fetch (Csv/Agent: rows held).</param>
/// <param name="Qualified">Opportunities that are Qualified now and whose current evidence comes from this source (all time).</param>
/// <param name="Rate">Qualified / read; null when read is 0.</param>
public sealed record SourceYieldDto(
    Guid SourceId,
    Guid CampaignId,
    string Label,
    SourceKind Kind,
    JobPlatform? Platform,
    int Read,
    int Qualified,
    double? Rate,
    DateTime? LastFetchedAt,
    bool Failing);

/// <param name="Applied">Opportunities first reaching Applied that UTC day.</param>
/// <param name="Replies">Opportunities first reaching Responded that UTC day.</param>
public sealed record ApplicationsDayDto(DateOnly Date, int Applied, int Replies);

public sealed record AttentionItemDto(AttentionKind Kind, int Count, string Detail);

public sealed record ActiveResearchCountsDto(int Candidates, int Sources, int SourcesDone);

public sealed record ActiveResearchDto(
    Guid JobId,
    Guid CampaignId,
    string CampaignName,
    ResearchJobState State,
    ResearchStage Stage,
    ActiveResearchCountsDto Counts);

public sealed record IndustryCountDto(string Industry, int Count);

public sealed record SignalCountDto(string Signal, int Count);

/// <summary>One stored activity line, as the funnel "reached" logic reads it.</summary>
public sealed record AnalyticsActivity(string Kind, string Detail, DateTime OccurredAt);
