using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Approvals;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class ApprovalRepository(AppDbContext db) : IApprovalRepository
{
    private IQueryable<Opportunity> Suggested(Guid ownerId) =>
        db.Opportunities.Where(o => o.OwnerId == ownerId && o.Status == OpportunityStatus.Suggested);

    public Task<bool> CampaignExistsAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OwnerId == ownerId, ct);

    public Task<Dictionary<Guid, string>> CampaignNamesAsync(Guid ownerId, IReadOnlyCollection<Guid> campaignIds, CancellationToken ct)
    {
        var ids = campaignIds.ToList();
        return db.Campaigns.Where(c => c.OwnerId == ownerId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    public async Task<(int Total, List<Opportunity> Items)> PageSuggestedAsync(Guid ownerId, Guid? campaignId, int take, int skip, CancellationToken ct)
    {
        var query = Suggested(ownerId);
        if (campaignId is { } id) query = query.Where(o => o.CampaignId == id);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.Score).ThenByDescending(o => o.Coverage).ThenByDescending(o => o.UpdatedAt).ThenBy(o => o.Id)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
        return (total, items);
    }

    public Task<int> CountSuggestedAsync(Guid ownerId, CancellationToken ct) => Suggested(ownerId).CountAsync(ct);

    public Task<List<Opportunity>> SuggestedAsync(Guid ownerId, Guid? campaignId, CancellationToken ct)
    {
        var query = Suggested(ownerId);
        if (campaignId is { } id) query = query.Where(x => x.CampaignId == id);
        return query.ToListAsync(ct);
    }

    public Task<List<Opportunity>> SuggestedByIdsAsync(Guid ownerId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        return Suggested(ownerId).Where(o => idList.Contains(o.Id)).ToListAsync(ct);
    }

    public void AddActivity(Activity activity) => db.Activities.Add(activity);

    public void ClearTracked() => db.ChangeTracker.Clear();

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
