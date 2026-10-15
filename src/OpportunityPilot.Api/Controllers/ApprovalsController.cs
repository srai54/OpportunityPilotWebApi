using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Approvals;

namespace OpportunityPilot.Api.Controllers;

/// <summary>The batch approval queue: Suggested opportunities and the user's approve/reject decisions.</summary>
[ApiController]
[Route("api/v1/approvals")]
public sealed class ApprovalsController(IApprovalService approvals) : ControllerBase
{
    /// <summary>Suggested opportunities, highest score first; take 1–200 (default 100).</summary>
    [HttpGet]
    public async Task<ActionResult<ApprovalPageDto>> List(
        [FromQuery] Guid? campaignId, [FromQuery] int take = ApprovalService.DefaultTake, [FromQuery] int skip = 0, CancellationToken ct = default) =>
        await approvals.ListAsync(campaignId, take, skip, ct);

    /// <summary>Approve → Shortlisted, reject → Dismissed; ids that are not Suggested (or not yours) are skipped.</summary>
    [HttpPost("decide")]
    public async Task<ActionResult<DecideApprovalsResult>> Decide(DecideApprovalsRequest request, CancellationToken ct) =>
        await approvals.DecideAsync(request, ct);

    [HttpPost("decide-all")]
    public async Task<ActionResult<DecideApprovalsResult>> DecideAll(DecideAllApprovalsRequest request, CancellationToken ct) =>
        await approvals.DecideAllAsync(request, ct);
}
