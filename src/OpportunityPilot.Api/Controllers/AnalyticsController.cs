using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Analytics;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Overview metrics for the v2 Candidate and Sales overviews (docs/ANALYTICS_CONTRACT.md). User auth only.</summary>
[ApiController]
[Route("api/v1/analytics")]
public sealed class AnalyticsController(AnalyticsService analytics) : ControllerBase
{
    /// <summary>workspace Candidate (Job campaigns) or Sales (Customer campaigns); days 1–365 (default 30).</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<AnalyticsOverviewDto>> Overview(
        [FromQuery] string? workspace, [FromQuery] int days = AnalyticsService.DefaultDays,
        [FromQuery] Domain.Common.OpportunityMode? mode = null, CancellationToken ct = default) =>
        await analytics.OverviewAsync(workspace, days, ct, mode);

    /// <summary>The caller's own recorded actions, newest first (take 1–100, default 30).</summary>
    [HttpGet("activity")]
    public async Task<ActionResult<IReadOnlyList<ActivityFeedItemDto>>> Activity([FromQuery] string? workspace, [FromQuery] int take = 30, CancellationToken ct = default) =>
        Ok(await analytics.ActivityFeedAsync(workspace, take, ct));

    /// <summary>Daily activity for the last 7–90 days (default 30) with the window before it, for the Insights trend chart.</summary>
    [HttpGet("trend")]
    public async Task<ActionResult<AnalyticsTrendDto>> Trend([FromQuery] string? workspace, [FromQuery] int days = 30, CancellationToken ct = default) =>
        await analytics.TrendAsync(workspace, days, ct);
}
