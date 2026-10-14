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
public sealed class IntegrationsController(
    IntegrationService integrations, ConnectorOauthService oauth, PluginService plugins, IntegrationNotifier notifier, IOptions<IntegrationOptions> options,
    IOptions<OpportunityPilot.Application.Mail.MailOptions> mail) : ControllerBase
{
    private string FeedBase => string.IsNullOrWhiteSpace(options.Value.PublicApiBase) ? $"{Request.Scheme}://{Request.Host}" : options.Value.PublicApiBase!;

    /// <summary>Where the services send the browser back: Integrations__PublicApiBase, else Mail__PublicApiBase, else this request's own address.</summary>
    private string ApiBase => !string.IsNullOrWhiteSpace(options.Value.PublicApiBase) ? options.Value.PublicApiBase!
        : !string.IsNullOrWhiteSpace(mail.Value.PublicApiBase) ? mail.Value.PublicApiBase! : $"{Request.Scheme}://{Request.Host}";

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

    /// <summary>"Connect with Notion / HubSpot / Slack / Airtable": the address of the service's own consent screen. 409 when the server has no app for it.</summary>
    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpPost("integrations/{kind}/oauth")]
    public ActionResult<OauthStartResponse> StartOauth(IntegrationKind kind) => oauth.Begin(kind, ApiBase);

    /// <summary>The service sends the browser here, so there is no bearer token; the sealed, expiring state names the person. Tokens never go in the redirect.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.PublicProgress)]
    [HttpGet("integrations/oauth/callback/{kind}")]
    public async Task<IActionResult> OauthCallback(IntegrationKind kind, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct) =>
        Redirect(await oauth.CompleteAsync(kind, code, state, error, ApiBase, ct));

    /// <summary>One-click "Connect" (Notion, Gmail, HubSpot, Slack, Airtable): the managed-connection platform's sign-in address. 409 without Plugins__ApiKey.</summary>
    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpPost("integrations/{kind}/plugin")]
    public async Task<ActionResult<OauthStartResponse>> StartPlugin(IntegrationKind kind, CancellationToken ct) => await plugins.BeginAsync(kind, ApiBase, ct);

    /// <summary>The platform sends the browser back here with the connected-account id; the sealed state names the person, and the platform must agree.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.PublicProgress)]
    [HttpGet("integrations/plugin/callback/{kind}")]
    public async Task<IActionResult> PluginCallback(IntegrationKind kind, [FromQuery] string? state, [FromQuery(Name = "connected_account_id")] string? accountId,
        [FromQuery] string? connectedAccountId, [FromQuery] string? status, CancellationToken ct) =>
        Redirect(await plugins.CompleteAsync(kind, state, accountId ?? connectedAccountId, status, ct));

    /// <summary>Notion databases or Airtable tables this connection can see.</summary>
    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpGet("integrations/{kind}/targets")]
    public async Task<ActionResult<IReadOnlyList<IntegrationTargetDto>>> Targets(IntegrationKind kind, CancellationToken ct) => Ok(await integrations.TargetsAsync(kind, ct));

    [EnableRateLimiting(RateLimits.Connectors)]
    [HttpPut("integrations/{kind}/target")]
    public async Task<ActionResult<IntegrationDto>> SetTarget(IntegrationKind kind, SelectIntegrationTargetRequest request, CancellationToken ct) =>
        await integrations.SetTargetAsync(kind, request, FeedBase, ct);

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
