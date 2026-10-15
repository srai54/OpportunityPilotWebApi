using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Profiles;

namespace OpportunityPilot.Application.Drafts;

/// <summary>A draft with the opportunity and campaign fields the draft list shows.</summary>
public sealed record DraftListRow(OutreachDraft Draft, string Title, string Organization, string FactsJson, Guid CampaignId, string CampaignName);

/// <summary>Data access for outreach drafts and what they are generated from. Every method takes the owner id.</summary>
public interface IDraftRepository
{
    Task<OutreachDraft?> FindAsync(Guid ownerId, Guid id, CancellationToken ct);
    /// <summary>Newest first.</summary>
    Task<List<OutreachDraft>> ListForOpportunityAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    /// <summary>Newest first; drafts whose opportunity or campaign is not the owner's are left out.</summary>
    Task<(int Total, List<DraftListRow> Rows)> PageAsync(Guid ownerId, DraftState? state, DraftChannel? channel, Guid? campaignId,
        int take, int skip, CancellationToken ct);
    Task<bool> ExistsForChannelAsync(Guid ownerId, Guid opportunityId, DraftChannel channel, CancellationToken ct);
    Task<int> CountAsync(Guid ownerId, DraftState state, CancellationToken ct);
    Task<int> CountSentAsync(Guid ownerId, CancellationToken ct);

    Task<bool> CampaignExistsAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    /// <summary>Untracked shortlisted opportunities of the campaign that have no email draft yet, highest score first.</summary>
    Task<List<Opportunity>> ShortlistedWithoutEmailDraftAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    Task<Opportunity?> FindOpportunityAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    Task<bool> OpportunityExistsAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    /// <summary>The opportunity's facts JSON; throws when it is missing.</summary>
    Task<string> OpportunityFactsAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    /// <summary>Untracked read of the opportunity title, or null.</summary>
    Task<string?> OpportunityTitleAsync(Guid ownerId, Guid opportunityId, CancellationToken ct);
    /// <summary>The opportunity's campaign; throws when it is missing.</summary>
    Task<Campaign> GetCampaignAsync(Guid ownerId, Guid campaignId, CancellationToken ct);
    Task<Profile?> FindProfileAsync(Guid ownerId, Guid profileId, CancellationToken ct);
    Task<bool> IsSuppressedAsync(Guid ownerId, string normalizedRecipient, CancellationToken ct);

    void Add(OutreachDraft draft);
    void Remove(OutreachDraft draft);
    void AddActivity(Activity activity);
    /// <summary>Drops every tracked change, so one failed item of a batch does not leak into the next save.</summary>
    void ClearTracked();
    /// <summary>Saves the shared unit of work; throws <c>DbUpdateException</c> / <c>DbUpdateConcurrencyException</c> as EF does.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
