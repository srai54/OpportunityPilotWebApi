using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpportunityPilot.Application.Feedback;

namespace OpportunityPilot.Api.Controllers;

/// <summary>In-app feedback and browser error reports (2026-10-26); the admin reads both. Triage loop: docs/OBSERVABILITY.md.</summary>
[ApiController]
[Route("api/v1")]
public sealed class FeedbackController(FeedbackService feedback) : ControllerBase
{
    [HttpPost("feedback")]
    [EnableRateLimiting(Hosting.RateLimits.Feedback)]
    public async Task<ActionResult<FeedbackDto>> Send(FeedbackRequest request, CancellationToken ct) => Ok(await feedback.SendAsync(request, ct));

    /// <summary>A browser error. Open to everyone (the candidate page has no sign-in); counted, not stored twice, and rate limited.</summary>
    [HttpPost("client-errors")]
    [AllowAnonymous]
    [EnableRateLimiting(Hosting.RateLimits.ClientErrors)]
    public async Task<IActionResult> Report(ClientErrorRequest request, CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value;
        await feedback.ReportErrorAsync(request, Guid.TryParse(sub, out var id) ? id : null, ct);
        return Accepted();
    }

    [HttpGet("admin/feedback")]
    public async Task<ActionResult<IReadOnlyList<FeedbackDto>>> List(CancellationToken ct) => Ok(await feedback.ListAsync(ct));

    [HttpPut("admin/feedback/{id:guid}/status")]
    public async Task<ActionResult<FeedbackDto>> SetStatus(Guid id, FeedbackStatusRequest request, CancellationToken ct) => Ok(await feedback.SetStatusAsync(id, request, ct));

    [HttpGet("admin/client-errors")]
    public async Task<ActionResult<IReadOnlyList<ClientErrorDto>>> Errors(CancellationToken ct) => Ok(await feedback.ErrorsAsync(ct));

    [HttpGet("admin/feedback/summary")]
    public async Task<ActionResult<FeedbackSummaryDto>> Summary(CancellationToken ct) => Ok(await feedback.SummaryAsync(ct));
}
