using System.Text.Json.Serialization;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Campaigns;

public sealed record LastJobDto(Guid Id, ResearchJobState State, ResearchStage Stage, DateTime? FinishedAt);

public sealed record CampaignSummaryDto(
    Guid Id,
    Guid ProfileId,
    OpportunityMode Mode,
    string Name,
    string Goal,
    int ResultLimit,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int SourceCount,
    int OpportunityCount,
    LastJobDto? LastJob,
    int? AutoSuggestMinScore,
    int? AutoApproveMinScore = null);

/// <summary>CampaignSummary plus the editable criteria and the normalised weights.</summary>
public sealed record CampaignDto(
    Guid Id,
    Guid ProfileId,
    OpportunityMode Mode,
    string Name,
    string Goal,
    int ResultLimit,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int SourceCount,
    int OpportunityCount,
    LastJobDto? LastJob,
    CampaignCriteria Criteria,
    IReadOnlyDictionary<string, int> Weights,
    int? AutoSuggestMinScore,
    int? AutoApproveMinScore = null);

/// <param name="Weights">Optional; the mode's defaults when omitted. Normalised to sum 100.</param>
/// <param name="AutoSuggestMinScore">Job only: 1–100 turns on the batch approval queue; null (or omitted) leaves it off.</param>
public sealed record CreateCampaignRequest(
    Guid ProfileId,
    OpportunityMode Mode,
    string Name,
    string? Goal,
    CampaignCriteria? Criteria,
    IReadOnlyDictionary<string, double>? Weights,
    int? ResultLimit,
    int? AutoSuggestMinScore = null,
    int? AutoApproveMinScore = null);

/// <summary>ExpectedVersion must match the stored version, otherwise the edit is rejected with 409.</summary>
public sealed record UpdateCampaignRequest(
    string Name,
    string? Goal,
    CampaignCriteria? Criteria,
    IReadOnlyDictionary<string, double>? Weights,
    int? ResultLimit,
    int ExpectedVersion)
{
    private readonly int? _autoSuggestMinScore;

    /// <summary>
    /// Omitted → the stored value is kept; <c>null</c> → auto-suggest off; 1–100 → the threshold. A plain positional
    /// parameter cannot tell "omitted" from "null", so this is a property whose setter only runs when the field is sent.
    /// </summary>
    public int? AutoSuggestMinScore
    {
        get => _autoSuggestMinScore;
        init
        {
            _autoSuggestMinScore = value;
            AutoSuggestMinScoreSent = true;
        }
    }

    private readonly int? _autoApproveMinScore;

    /// <summary>Omitted keeps the stored value; null turns hands-off approval off; 1–100 is the score that approves automatically.</summary>
    public int? AutoApproveMinScore
    {
        get => _autoApproveMinScore;
        init
        {
            _autoApproveMinScore = value;
            AutoApproveMinScoreSent = true;
        }
    }

    [JsonIgnore]
    public bool AutoApproveMinScoreSent { get; private init; }

    [JsonIgnore]
    public bool AutoSuggestMinScoreSent { get; private init; }
}
