using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;

namespace OpportunityPilot.Application.Accounts;

public sealed record DeleteAccountDataRequest(bool Confirm);

public sealed class AccountDataService(IAppDbContext db, ICurrentUser user, TimeProvider clock, Auth.SecurityAudit audit)
{
    public async Task<object> ExportAsync(CancellationToken ct)
    {
        var owner = user.OwnerId;
        audit.Record(owner, Domain.Auth.SecurityEventType.AccountDataExported);
        await db.SaveChangesAsync(ct);
        return new
        {
            exportedAt = clock.GetUtcNow().UtcDateTime,
            ownerId = owner,
            profiles = await db.Profiles.Where(x => x.OwnerId == owner).ToListAsync(ct),
            campaigns = await db.Campaigns.Where(x => x.OwnerId == owner).ToListAsync(ct),
            sources = await db.Sources.Where(x => x.OwnerId == owner).ToListAsync(ct),
            opportunities = await db.Opportunities.Where(x => x.OwnerId == owner).ToListAsync(ct),
            activities = await db.Activities.Where(x => x.OwnerId == owner).ToListAsync(ct),
            drafts = await db.OutreachDrafts.Where(x => x.OwnerId == owner).ToListAsync(ct),
            nextActions = await db.NextActions.Where(x => x.OwnerId == owner).ToListAsync(ct),
            suppressions = await db.Suppressions.Where(x => x.OwnerId == owner).ToListAsync(ct),
            applications = await db.JobApplications.Where(x => x.OwnerId == owner).ToListAsync(ct),
            jobInterviews = await db.JobInterviews.Where(x => x.OwnerId == owner).ToListAsync(ct),
            salesProjects = await db.SalesProjects.Where(x => x.OwnerId == owner).ToListAsync(ct),
            salesBids = await db.SalesBids.Where(x => x.OwnerId == owner).ToListAsync(ct),
            upworkOpportunities = await db.UpworkOpportunities.Where(x => x.OwnerId == owner).ToListAsync(ct),
            wellfoundJobs = await db.WellfoundJobs.Where(x => x.OwnerId == owner).ToListAsync(ct),
            wellfoundApplications = await db.WellfoundApplications.Where(x => x.OwnerId == owner).ToListAsync(ct),
            wellfoundActivities = await db.WellfoundActivities.Where(x => x.OwnerId == owner).ToListAsync(ct),
            schedules = await db.CampaignSchedules.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingAccounts = await db.StaffingAccounts.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingContacts = await db.StaffingContacts.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingDeals = await db.StaffingDeals.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingDealActivities = await db.StaffingDealActivities.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingCandidates = await db.StaffingCandidates.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingSubmissions = await db.StaffingSubmissions.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingInterviews = await db.StaffingInterviews.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingFeedback = await db.StaffingFeedbackEntries.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingOffers = await db.StaffingOffers.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingRateCards = await db.StaffingRateCards.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingProposals = await db.StaffingProposals.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingMessages = await db.StaffingMessages.Where(x => x.OwnerId == owner).ToListAsync(ct),
            staffingMeetings = await db.StaffingMeetings.Where(x => x.OwnerId == owner).ToListAsync(ct),
            securityEvents = await db.SecurityEvents.Where(x => x.OwnerId == owner).ToListAsync(ct),
            aiUsage = await db.AiUsages.Where(x => x.OwnerId == owner).ToListAsync(ct),
            providerExecutions = await db.ProviderExecutions.Where(x => x.OwnerId == owner).ToListAsync(ct),
            userEvents = await db.UserEvents.Where(x => x.OwnerId == owner).ToListAsync(ct),
            // The encrypted refresh token is a key to the person's mailbox: it is never part of an export.
            mailboxes = await db.MailboxConnections.Where(x => x.OwnerId == owner)
                .Select(x => new { x.Provider, x.EmailAddress, x.Status, x.ConnectedAt, x.LastUsedAt }).ToListAsync(ct),
            // Same for webhook addresses and service tokens: only what was connected and when.
            integrations = await db.IntegrationConnections.Where(x => x.OwnerId == owner)
                .Select(x => new { x.Kind, x.Label, x.Events, x.ConnectedAt, x.LastUsedAt }).ToListAsync(ct)
        };
    }

    public async Task DeleteAsync(DeleteAccountDataRequest request, CancellationToken ct)
    {
        if (request?.Confirm != true) throw new RequestValidationException(new Dictionary<string, string[]> { ["confirm"] = ["Set confirm to true to permanently delete account data."] });
        var owner = user.OwnerId;
        // Staffing records first, children before parents (several foreign keys restrict deletes).
        db.StaffingFeedbackEntries.RemoveRange(await db.StaffingFeedbackEntries.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingInterviews.RemoveRange(await db.StaffingInterviews.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingOffers.RemoveRange(await db.StaffingOffers.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingSubmissions.RemoveRange(await db.StaffingSubmissions.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingProposals.RemoveRange(await db.StaffingProposals.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingMessages.RemoveRange(await db.StaffingMessages.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingMeetings.RemoveRange(await db.StaffingMeetings.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingDealActivities.RemoveRange(await db.StaffingDealActivities.Where(x => x.OwnerId == owner).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.StaffingDeals.RemoveRange(await db.StaffingDeals.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingCandidates.RemoveRange(await db.StaffingCandidates.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingRateCards.RemoveRange(await db.StaffingRateCards.Where(x => x.OwnerId == owner).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.StaffingContacts.RemoveRange(await db.StaffingContacts.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.StaffingAccounts.RemoveRange(await db.StaffingAccounts.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.CampaignSchedules.RemoveRange(await db.CampaignSchedules.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.WellfoundActivities.RemoveRange(await db.WellfoundActivities.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.WellfoundApplications.RemoveRange(await db.WellfoundApplications.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.WellfoundJobs.RemoveRange(await db.WellfoundJobs.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.UpworkOpportunities.RemoveRange(await db.UpworkOpportunities.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.SalesProjects.RemoveRange(await db.SalesProjects.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.JobApplications.RemoveRange(await db.JobApplications.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.JobInterviews.RemoveRange(await db.JobInterviews.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.AgentKeys.RemoveRange(await db.AgentKeys.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.Suppressions.RemoveRange(await db.Suppressions.Where(x => x.OwnerId == owner).ToListAsync(ct));
        // Opportunities first: their evidence links restrict deleting evidence, which campaigns cascade to.
        db.Opportunities.RemoveRange(await db.Opportunities.Where(x => x.OwnerId == owner).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.Campaigns.RemoveRange(await db.Campaigns.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.Profiles.RemoveRange(await db.Profiles.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.GuestSessions.RemoveRange(await db.GuestSessions.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.ProviderExecutions.RemoveRange(await db.ProviderExecutions.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.AiUsages.RemoveRange(await db.AiUsages.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.UserEvents.RemoveRange(await db.UserEvents.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.MailboxConnections.RemoveRange(await db.MailboxConnections.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.IntegrationConnections.RemoveRange(await db.IntegrationConnections.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.CandidateProgressLinks.RemoveRange(await db.CandidateProgressLinks.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.ProgressRoleSuggestions.RemoveRange(await db.ProgressRoleSuggestions.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.CandidateJourneyEntries.RemoveRange(await db.CandidateJourneyEntries.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.CandidateResumes.RemoveRange(await db.CandidateResumes.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.UserAccess.RemoveRange(await db.UserAccess.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.AiCredentials.RemoveRange(await db.AiCredentials.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.UserFeedback.RemoveRange(await db.UserFeedback.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.ClientErrors.RemoveRange(await db.ClientErrors.Where(x => x.OwnerId == owner).ToListAsync(ct));
        db.SecurityEvents.RemoveRange(await db.SecurityEvents.Where(x => x.OwnerId == owner).ToListAsync(ct));
        // The one record kept: that this owner's data was deleted, and when. It holds nothing else.
        audit.Record(owner, Domain.Auth.SecurityEventType.AccountDataDeleted);
        await db.SaveChangesAsync(ct);
    }
}
