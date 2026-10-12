using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Application.Outreach;
using OpportunityPilot.Domain.Mail;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Connect a person's own Outlook or Gmail so approved messages can be sent from it. Secrets and tokens never appear in a response.</summary>
[ApiController]
[Route("api/v1")]
public sealed class MailboxesController(MailboxService mailboxes) : ControllerBase
{
    [HttpGet("mailboxes")]
    public async Task<ActionResult<IReadOnlyList<MailboxDto>>> List(CancellationToken ct) => Ok(await mailboxes.ListAsync(ct));

    [HttpPost("mailboxes/{provider}/connect")]
    public ActionResult<ConnectMailboxResponse> Connect(MailProvider provider) => mailboxes.BeginConnect(provider);

    [HttpDelete("mailboxes/{provider}")]
    public async Task<IActionResult> Disconnect(MailProvider provider, CancellationToken ct) { await mailboxes.DisconnectAsync(provider, ct); return NoContent(); }

    /// <summary>The provider sends the browser here, so there is no bearer token; the sealed, expiring state identifies the person.</summary>
    [AllowAnonymous]
    [HttpGet("mailboxes/callback/{provider}")]
    public async Task<IActionResult> Callback(MailProvider provider, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct) =>
        Redirect(await mailboxes.CompleteAsync(provider, code, state, error, ct));

    [HttpPost("drafts/{id:guid}/send-via-mailbox")]
    public async Task<ActionResult<ProviderExecutionDto>> Send(Guid id, SendViaMailboxRequest request, CancellationToken ct) => await mailboxes.SendDraftAsync(id, request, ct);
}
