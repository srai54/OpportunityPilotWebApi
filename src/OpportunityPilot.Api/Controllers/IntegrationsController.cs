using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpportunityPilot.Api.Hosting;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Slack, Teams, webhooks, HubSpot, Notion, Airtable and a calendar feed. Secrets never appear in a response.</summary>
[ApiController]
[Route("api/v1")]
public sealed class IntegrationsController(IntegrationService integrations, IntegrationNotifier notifier, IOptions<IntegrationOptions> options) : ControllerBase
{
    private string FeedBase => string.IsNullOrWhiteSpace(options.Value.PublicApiBase) ? $"{Request.Scheme}://{Request.Host}" : options.Value.PublicApiBase!;

    [HttpGet("integrations")]
    public async Task<ActionResult<IReadOnlyList<IntegrationDto>>> List(CancellationToken ct) => Ok(await integrations.ListAsync(FeedBase, ct));

    /// <summary>Checks the address or token against the real service (a webhook gets one "connected" message) before storing it encrypted.</summary>
    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpPut("integrations/{kind}")]
    public async Task<ActionResult<IntegrationDto>> Connect(IntegrationKind kind, ConnectIntegrationRequest request, CancellationToken ct) =>
        await integrations.ConnectAsync(kind, request, FeedBase, ct);

    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpPost("integrations/{kind}/test")]
    public async Task<ActionResult<IntegrationTestResult>> Test(IntegrationKind kind, CancellationToken ct) => await integrations.TestAsync(kind, ct);

    [HttpPut("integrations/{kind}/events")]
    public async Task<ActionResult<IntegrationDto>> Events(IntegrationKind kind, UpdateIntegrationEventsRequest request, CancellationToken ct) =>
        await integrations.SetEventsAsync(kind, request, FeedBase, ct);

    [HttpDelete("integrations/{kind}")]
    public async Task<IActionResult> Disconnect(IntegrationKind kind, CancellationToken ct) { await integrations.DisconnectAsync(kind, ct); return NoContent(); }

    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpPost("integrations/{kind}/push")]
    public async Task<ActionResult<IReadOnlyList<PushLeadResult>>> Push(IntegrationKind kind, PushLeadsRequest request, CancellationToken ct) =>
        Ok(await integrations.PushAsync(kind, request, ct));

    /// <summary>Calendar apps fetch this without signing in; the long random key in the link is the permission, and disconnecting revokes it.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.PublicProgress)]
    [HttpGet("calendar/{id:guid}/{key}.ics")]
    public async Task<IActionResult> Calendar(Guid id, string key, CancellationToken ct) =>
        await notifier.CalendarAsync(id, key, ct) is { } ics ? File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8") : NotFound();
}
