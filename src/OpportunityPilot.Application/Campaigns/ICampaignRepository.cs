using OpportunityPilot.Domain.Campaigns;

namespace OpportunityPilot.Application.Campaigns;

/// <summary>Per-campaign counts and the latest research job, shown on list and detail.</summary>
public sealed record CampaignStats(int Sources, int Opportunities, LastJobDto? LastJob);

/// <summary>Data access for campaigns. Every method takes the owner id; the service passes <c>ICurrentUser.OwnerId</c>.</summary>
public interface ICampaignRepository
{
    /// <summary>Newest first, then by id.</summary>
    Task<List<Campaign>> ListAsync(Guid ownerId, CancellationToken ct);
    Task<int> CountAsync(Guid ownerId, CancellationToken ct);
    Task<Campaign?> FindAsync(Guid ownerId, Guid id, CancellationToken ct);
    Task<bool> ProfileExistsAsync(Guid ownerId, Guid profileId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, CampaignStats>> StatsAsync(Guid ownerId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
    void Add(Campaign campaign);
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateConcurrencyException</c> on a version clash.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
