using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class OpportunityRepository(AppDbContext db) : IOpportunityRepository
{
    private IQueryable<Opportunity> Owned(Guid ownerId) => db.Opportunities.Where(o => o.OwnerId == ownerId);

    public Task<Campaign?> FindCampaignAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId && c.OwnerId == ownerId, ct);

    public Task<bool> CampaignExistsAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OwnerId == ownerId, ct);

    public async Task<(int Total, List<Opportunity> Items)> PageAsync(Guid ownerId, Guid campaignId, FilterOutcome? outcome,
        OpportunityStatus? status, string? sort, int take, int skip, CancellationToken ct)
    {
        var query = Owned(ownerId).Where(o => o.CampaignId == campaignId);
        if (outcome is { } oc) query = query.Where(o => o.Outcome == oc);
        if (status is { } st) query = query.Where(o => o.Status == st);

        var total = await query.CountAsync(ct);
        // "found" / "oldest" order by when each job or lead was first found, so it is clear which came first.
        var ordered = string.Equals(sort, "recent", StringComparison.OrdinalIgnoreCase)
            ? query.OrderByDescending(o => o.UpdatedAt).ThenBy(o => o.Id)
            : string.Equals(sort, "found", StringComparison.OrdinalIgnoreCase)
                ? query.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
            : string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase)
                ? query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            // Qualified first, then Needs verification, then Excluded — a high-scoring excluded job must not top the list.
            : query.OrderBy(o => o.Outcome == FilterOutcome.Qualified ? 0 : o.Outcome == FilterOutcome.NeedsVerification ? 1 : 2)
                .ThenByDescending(o => o.Score).ThenByDescending(o => o.Coverage).ThenByDescending(o => o.UpdatedAt).ThenBy(o => o.Id);
        var items = await ordered.Skip(skip).Take(take).ToListAsync(ct);
        return (total, items);
    }

    public Task<int> CountAsync(Guid ownerId, OpportunityStatus status, FilterOutcome? outcome, CancellationToken ct) =>
        outcome is { } oc
            ? Owned(ownerId).CountAsync(o => o.Status == status && o.Outcome == oc, ct)
            : Owned(ownerId).CountAsync(o => o.Status == status, ct);

    public Task<Opportunity?> FindAsync(Guid ownerId, Guid id, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<List<Opportunity>> ListForExportAsync(Guid ownerId, Guid campaignId, int max, CancellationToken ct) =>
        Owned(ownerId).Where(o => o.CampaignId == campaignId)
            .OrderBy(o => o.Outcome == FilterOutcome.Qualified ? 0 : o.Outcome == FilterOutcome.NeedsVerification ? 1 : 2).ThenByDescending(o => o.Score).ThenBy(o => o.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<Dictionary<Guid, string>> EvidenceUrlsAsync(Guid ownerId, IReadOnlyCollection<Guid> opportunityIds, CancellationToken ct)
    {
        var ids = opportunityIds.ToList();
        return (await (from l in db.OpportunityEvidence
                    join e in db.Evidence on l.EvidenceId equals e.Id
                    where ids.Contains(l.OpportunityId) && e.OwnerId == ownerId
                    select new { l.OpportunityId, e.Url })
                .ToListAsync(ct))
            .GroupBy(x => x.OpportunityId)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(x => x.Url).Where(u => u is not null).Distinct()));
    }

    public Task<List<Evidence>> EvidenceAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        (from l in db.OpportunityEvidence
                join e in db.Evidence on l.EvidenceId equals e.Id
                where l.OpportunityId == opportunityId && e.OwnerId == ownerId
                orderby e.RetrievedAt descending
                select e)
            .ToListAsync(ct);

    public Task<Dictionary<Guid, string>> SourceLabelsAsync(Guid ownerId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct)
    {
        var ids = sourceIds.ToList();
        return db.Sources.Where(s => ids.Contains(s.Id) && s.OwnerId == ownerId).ToDictionaryAsync(s => s.Id, s => s.Label, ct);
    }

    public Task<List<ActivityDto>> ActivitiesAsync(Guid ownerId, Guid opportunityId, int take, CancellationToken ct) =>
        db.Activities.Where(a => a.OpportunityId == opportunityId && a.OwnerId == ownerId)
            .OrderByDescending(a => a.OccurredAt).ThenBy(a => a.Id)
            .Take(take)
            .Select(a => new ActivityDto(a.Kind, a.OccurredAt, a.Detail))
            .ToListAsync(ct);

    public Task<ScoredByDto?> ScoredByAsync(Guid ownerId, Guid researchJobId, CancellationToken ct) =>
        db.ResearchJobs.Where(j => j.Id == researchJobId && j.OwnerId == ownerId)
            .Select(j => new ScoredByDto(j.Id, j.CampaignVersion, j.ProfileVersion, j.StartedAt ?? j.CreatedAt)).FirstOrDefaultAsync(ct);

    public Task<bool> HasOpenFollowUpAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        db.NextActions.AnyAsync(x => x.OwnerId == ownerId && x.OpportunityId == opportunityId
            && x.State == NextActionState.Open && x.Kind == NextActionKind.FollowUp, ct);

    public void AddActivity(Activity activity) => db.Activities.Add(activity);

    public void AddNextAction(NextAction action) => db.NextActions.Add(action);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
