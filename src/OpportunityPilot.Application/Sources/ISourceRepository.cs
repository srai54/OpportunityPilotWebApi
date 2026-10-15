using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Sources;

/// <summary>Data access for research sources. Every method takes the owner id.</summary>
public interface ISourceRepository
{
    /// <summary>The modes of the matching campaigns: one entry when the campaign exists and is the owner's.</summary>
    Task<List<OpportunityMode>> CampaignModesAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    /// <summary>Oldest first.</summary>
    Task<List<Source>> ListAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    Task<int> CountAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    Task<Source?> FindAsync(Guid ownerId, Guid campaignId, Guid sourceId, CancellationToken ct);
    void Add(Source source);
    /// <summary>Removes the source and its items explicitly, because InMemory (demo mode) does not run database cascades.</summary>
    Task RemoveWithItemsAsync(Source source, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
