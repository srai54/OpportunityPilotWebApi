using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Api.Auth;
using OpportunityPilot.Application.Applications;
using OpportunityPilot.Domain.Applications;

namespace OpportunityPilot.Api.Controllers;

[ApiController]
[Route("api/v1/applications")]
public sealed class ApplicationsController(IApplicationService applications) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApplicationPageDto>> List(
        [FromQuery] ApplicationStatus? status, [FromQuery] ApplicationPlatform? platform,
        [FromQuery] int take = ApplicationService.DefaultTake, [FromQuery] int skip = 0, CancellationToken ct = default) =>
        await applications.ListAsync(status, platform, take, skip, ct);

    [HttpGet("summary")]
    public async Task<ActionResult<ApplicationSummaryDto>> Summary(CancellationToken ct) =>
        await applications.SummaryAsync(ct);

    /// <summary>Called by the local desktop agent only. User tokens are rejected here; agent keys work nowhere else.</summary>
    [HttpPost("report")]
    [Authorize(Policy = AgentKeyAuthenticationHandler.ApplicationsPolicy)]
    public async Task<ActionResult<ApplicationReportResult>> Report(ApplicationReportRequest request, CancellationToken ct) =>
        await applications.ReportAsync(request, ct);
}
