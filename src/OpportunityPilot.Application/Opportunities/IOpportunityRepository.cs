using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Opportunities;

/// <summary>Data access for opportunities and their evidence, activities and follow-ups. Every method takes the owner id.</summary>
public interface IOpportunityRepository
{
    Task<Campaign?> FindCampaignAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    Task<bool> CampaignExistsAsync(Guid ownerId, Guid campaignId, CancellationToken ct);

    /// <summary>One page of a campaign's opportunities and the filtered total. Sort: score (default), recent, found or oldest.</summary>
    Task<(int Total, List<Opportunity> Items)> PageAsync(Guid ownerId, Guid campaignId, FilterOutcome? outcome,
        OpportunityStatus? status, string? sort, int take, int skip, CancellationToken ct);
    Task<int> CountAsync(Guid ownerId, OpportunityStatus status, FilterOutcome? outcome, CancellationToken ct);
    Task<Opportunity?> FindAsync(Guid ownerId, Guid id, CancellationToken ct);

    /// <summary>Export order: Qualified, Needs verification, Excluded, then score; at most <paramref name="max"/> rows.</summary>
    Task<List<Opportunity>> ListForExportAsync(Guid ownerId, Guid campaignId, int max, CancellationToken ct);
    /// <summary>Space-separated distinct evidence URLs per opportunity.</summary>
    Task<Dictionary<Guid, string>> EvidenceUrlsAsync(Guid ownerId, IReadOnlyCollection<Guid> opportunityIds, CancellationToken ct);
    /// <summary>Newest first.</summary>
    Task<List<Evidence>> EvidenceAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    Task<Dictionary<Guid, string>> SourceLabelsAsync(Guid ownerId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct);
    /// <summary>Newest first, at most <paramref name="take"/>.</summary>
    Task<List<ActivityDto>> ActivitiesAsync(Guid ownerId, Guid opportunityId, int take, CancellationToken ct);
    Task<ScoredByDto?> ScoredByAsync(Guid ownerId, Guid researchJobId, CancellationToken ct);

    Task<bool> HasOpenFollowUpAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    void AddActivity(Activity activity);
    void AddNextAction(NextAction action);
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateConcurrencyException</c> on a version clash.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
