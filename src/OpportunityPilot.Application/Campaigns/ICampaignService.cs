namespace OpportunityPilot.Application.Campaigns;

/// <summary>Campaign use cases: validation, versioning and stats. Owner-scoped through <c>ICurrentUser</c>.</summary>
public interface ICampaignService
{
    Task<IReadOnlyList<CampaignSummaryDto>> ListAsync(CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task<CampaignDto> GetAsync(Guid id, CancellationToken ct);
    Task<CampaignDto> CreateAsync(CreateCampaignRequest request, CancellationToken ct);
    Task<CampaignDto> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
