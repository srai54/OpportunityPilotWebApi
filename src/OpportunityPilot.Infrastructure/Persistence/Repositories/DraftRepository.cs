using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Drafts;
using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Profiles;

namespace OpportunityPilot.Infrastructure.Persistence.Repositories;

public sealed class DraftRepository(AppDbContext db) : IDraftRepository
{
    private IQueryable<OutreachDraft> Owned(Guid ownerId) => db.OutreachDrafts.Where(d => d.OwnerId == ownerId);

    public Task<OutreachDraft?> FindAsync(Guid ownerId, Guid id, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<List<OutreachDraft>> ListForOpportunityAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        Owned(ownerId).Where(d => d.OpportunityId == opportunityId)
            .OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id).ToListAsync(ct);

    public async Task<(int Total, List<DraftListRow> Rows)> PageAsync(Guid ownerId, DraftState? state, DraftChannel? channel,
        Guid? campaignId, int take, int skip, CancellationToken ct)
    {
        var query = Owned(ownerId);
        if (state is { } value) query = query.Where(d => d.State == value);
        if (channel is { } requestedChannel) query = query.Where(d => d.Channel == requestedChannel);
        var joined = from draft in query
                     join opportunity in db.Opportunities on draft.OpportunityId equals opportunity.Id
                     join campaign in db.Campaigns on opportunity.CampaignId equals campaign.Id
                     where opportunity.OwnerId == ownerId && campaign.OwnerId == ownerId
                         && (campaignId == null || campaign.Id == campaignId)
                     select new { draft, opportunity.Title, opportunity.Organization, opportunity.FactsJson, CampaignId = campaign.Id, CampaignName = campaign.Name };
        var total = await joined.CountAsync(ct);
        var rows = await joined.OrderByDescending(x => x.draft.UpdatedAt).ThenBy(x => x.draft.Id)
            .Skip(skip).Take(take).ToListAsync(ct);
        return (total, rows.Select(x => new DraftListRow(x.draft, x.Title, x.Organization, x.FactsJson, x.CampaignId, x.CampaignName)).ToList());
    }

    public Task<bool> ExistsForChannelAsync(Guid ownerId, Guid opportunityId, DraftChannel channel, CancellationToken ct) =>
        db.OutreachDrafts.AnyAsync(d => d.OwnerId == ownerId && d.OpportunityId == opportunityId && d.Channel == channel, ct);

    public Task<int> CountAsync(Guid ownerId, DraftState state, CancellationToken ct) =>
        Owned(ownerId).CountAsync(d => d.State == state, ct);

    public Task<int> CountSentAsync(Guid ownerId, CancellationToken ct) => Owned(ownerId).CountAsync(d => d.SentAt != null, ct);

    public Task<bool> CampaignExistsAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Campaigns.AnyAsync(c => c.Id == campaignId && c.OwnerId == ownerId, ct);

    public Task<List<Opportunity>> ShortlistedWithoutEmailDraftAsync(Guid ownerId, Guid campaignId, CancellationToken ct)
    {
        var withEmail = db.OutreachDrafts.Where(d => d.OwnerId == ownerId && d.Channel == DraftChannel.Email).Select(d => d.OpportunityId);
        return db.Opportunities.AsNoTracking()
            .Where(o => o.OwnerId == ownerId && o.CampaignId == campaignId && o.Status == OpportunityStatus.Shortlisted && !withEmail.Contains(o.Id))
            .OrderByDescending(o => o.Score).ThenBy(o => o.Id).ToListAsync(ct);
    }

    public Task<Opportunity?> FindOpportunityAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        db.Opportunities.FirstOrDefaultAsync(o => o.Id == opportunityId && o.OwnerId == ownerId, ct);

    public Task<bool> OpportunityExistsAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        db.Opportunities.AnyAsync(o => o.Id == opportunityId && o.OwnerId == ownerId, ct);

    public Task<string> OpportunityFactsAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        db.Opportunities.Where(o => o.Id == opportunityId && o.OwnerId == ownerId).Select(o => o.FactsJson).SingleAsync(ct);

    public Task<string?> OpportunityTitleAsync(Guid ownerId, Guid opportunityId, CancellationToken ct) =>
        db.Opportunities.AsNoTracking().Where(o => o.Id == opportunityId && o.OwnerId == ownerId).Select(o => (string?)o.Title).FirstOrDefaultAsync(ct);

    public Task<Campaign> GetCampaignAsync(Guid ownerId, Guid campaignId, CancellationToken ct) =>
        db.Campaigns.FirstAsync(c => c.Id == campaignId && c.OwnerId == ownerId, ct);

    public Task<Profile?> FindProfileAsync(Guid ownerId, Guid profileId, CancellationToken ct) =>
        db.Profiles.FirstOrDefaultAsync(p => p.Id == profileId && p.OwnerId == ownerId, ct);

    public Task<bool> IsSuppressedAsync(Guid ownerId, string normalizedRecipient, CancellationToken ct) =>
        db.Suppressions.AnyAsync(x => x.OwnerId == ownerId && x.NormalizedRecipient == normalizedRecipient, ct);

    public void Add(OutreachDraft draft) => db.OutreachDrafts.Add(draft);

    public void Remove(OutreachDraft draft) => db.OutreachDrafts.Remove(draft);

    public void AddActivity(Activity activity) => db.Activities.Add(activity);

    public void ClearTracked() => db.ChangeTracker.Clear();

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
