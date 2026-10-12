using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Applications;
using OpportunityPilot.Application.Approvals;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Opportunities;
using OpportunityPilot.Application.Profiles;
using OpportunityPilot.Application.Drafts;
using OpportunityPilot.Application.Outreach;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Counts for the Overview screen. Only entities that exist in this build are counted.</summary>
[ApiController]
[Route("api/v1/overview")]
public sealed class OverviewController(
    ProfileService profiles, ApplicationService applications, CampaignService campaigns, OpportunityService opportunities,
    ApprovalService approvals, DraftService drafts, OutreachService outreach) : ControllerBase
{
    /// <param name="AwaitingApproval">Suggested opportunities waiting in the approval queue.</param>
    public sealed record OverviewDto(int Profiles, int Applied, int NeedsManual, int Campaigns, int Shortlisted, int AwaitingApproval,
        int DraftsAwaitingReview, int FollowUpsDue, int DraftsApproved, int DraftsSent, int FollowUpsOpen);

    [HttpGet]
    public async Task<ActionResult<OverviewDto>> Get(CancellationToken ct)
    {
        var summary = await applications.SummaryAsync(ct);
        return new OverviewDto(await profiles.CountAsync(ct), summary.Applied, summary.NeedsManual,
            await campaigns.CountAsync(ct), await opportunities.CountShortlistedAsync(ct), await approvals.CountAsync(ct),
            await drafts.CountAwaitingReviewAsync(ct), await outreach.CountDueAsync(ct),
            await drafts.CountApprovedAsync(ct), await drafts.CountSentAsync(ct), await outreach.CountOpenAsync(ct));
    }
}
