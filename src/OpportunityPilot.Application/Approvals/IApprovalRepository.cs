using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Approvals;

/// <summary>Data access for Suggested opportunities (the approval queue). Every method takes the owner id.</summary>
public interface IApprovalRepository
{
    Task<bool> CampaignExistsAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    Task<Dictionary<Guid, string>> CampaignNamesAsync(Guid ownerId, IReadOnlyCollection<Guid> campaignIds, CancellationToken ct);
    /// <summary>Suggested opportunities, highest score first, and the total; optionally one campaign.</summary>
    Task<(int Total, List<Opportunity> Items)> PageSuggestedAsync(Guid ownerId, Guid? campaignId, int take, int skip, CancellationToken ct);
    Task<int> CountSuggestedAsync(Guid ownerId, CancellationToken ct);
    /// <summary>Tracked Suggested opportunities; optionally one campaign.</summary>
    Task<List<Opportunity>> SuggestedAsync(Guid ownerId, Guid? campaignId, CancellationToken ct);
    /// <summary>Tracked Suggested opportunities among <paramref name="ids"/>.</summary>
    Task<List<Opportunity>> SuggestedByIdsAsync(Guid ownerId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
    void AddActivity(Activity activity);
    /// <summary>Drops every tracked change before a retry.</summary>
    void ClearTracked();
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateConcurrencyException</c> on a version clash.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
