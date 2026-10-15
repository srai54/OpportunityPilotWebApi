using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Sources;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class SourceRepository(AppDbContext db) : ISourceRepository
{
    public Task<List<OpportunityMode>> CampaignModesAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Campaigns.Where(c => c.Id == campaignId && c.OwnerId == ownerId).Select(c => c.Mode).ToListAsync(ct);

    public Task<List<Source>> ListAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Sources
            .Where(s => s.OwnerId == ownerId && s.CampaignId == campaignId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);

    public Task<int> CountAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Sources.CountAsync(s => s.OwnerId == ownerId && s.CampaignId == campaignId, ct);

    public Task<Source?> FindAsync(Guid ownerId, Guid campaignId, Guid sourceId, CancellationToken ct) =>
        db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.CampaignId == campaignId && s.OwnerId == ownerId, ct);

    public void Add(Source source) => db.Sources.Add(source);

    public async Task RemoveWithItemsAsync(Source source, CancellationToken ct)
    {
        db.SourceItems.RemoveRange(await db.SourceItems.Where(i => i.SourceId == source.Id).ToListAsync(ct));
        db.Sources.Remove(source);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
