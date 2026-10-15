using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Domain.Campaigns;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class CampaignRepository(AppDbContext db) : ICampaignRepository
{
    private IQueryable<Campaign> Owned(Guid ownerId) => db.Campaigns.Where(c => c.OwnerId == ownerId);

    public Task<List<Campaign>> ListAsync(Guid ownerId, CancellationToken ct) =>
        Owned(ownerId).OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);

    public Task<int> CountAsync(Guid ownerId, CancellationToken ct) => Owned(ownerId).CountAsync(ct);

    public Task<Campaign?> FindAsync(Guid ownerId, Guid id, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<bool> ProfileExistsAsync(Guid ownerId, Guid profileId, CancellationToken ct) =>
        db.Profiles.AnyAsync(p => p.Id == profileId && p.OwnerId == ownerId, ct);

    // Separate grouped queries rather than correlated subqueries: portable across Postgres, SQL Server and InMemory.
    public async Task<IReadOnlyDictionary<Guid, CampaignStats>> StatsAsync(Guid ownerId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        var sources = await db.Sources.Where(s => s.OwnerId == ownerId && idList.Contains(s.CampaignId))
            .GroupBy(s => s.CampaignId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var opportunities = await db.Opportunities.Where(o => o.OwnerId == ownerId && idList.Contains(o.CampaignId))
            .GroupBy(o => o.CampaignId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var jobs = (await db.ResearchJobs.Where(j => j.OwnerId == ownerId && idList.Contains(j.CampaignId))
                .Select(j => new { j.CampaignId, j.Id, j.State, j.Stage, j.FinishedAt, j.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(j => j.CampaignId)
            .ToDictionary(g => g.Key, g =>
            {
                var last = g.OrderByDescending(j => j.CreatedAt).First();
                return new LastJobDto(last.Id, last.State, last.Stage, last.FinishedAt);
            });
        return idList.Distinct().ToDictionary(id => id, id => new CampaignStats(
            sources.GetValueOrDefault(id), opportunities.GetValueOrDefault(id), jobs.GetValueOrDefault(id)));
    }

    public void Add(Campaign campaign) => db.Campaigns.Add(campaign);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
