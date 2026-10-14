using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpportunityPilot.Api.Hosting;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Application.Outreach;
using OpportunityPilot.Domain.Mail;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Connect a person's own Outlook or Gmail so approved messages can be sent from it. Secrets and tokens never appear in a response.</summary>
[ApiController]
[Route("api/v1")]
public sealed class MailboxesController(MailboxService mailboxes, ReplyCheckService replies) : ControllerBase
{
    [HttpGet("mailboxes")]
    public async Task<ActionResult<IReadOnlyList<MailboxDto>>> List(CancellationToken ct) => Ok(await mailboxes.ListAsync(ct));

    [HttpPost("mailboxes/{provider}/connect")]
    public ActionResult<ConnectMailboxResponse> Connect(MailProvider provider) => mailboxes.BeginConnect(provider);

    /// <summary>Connect the person's own email with an app password (no sign-in screen). The password is verified with one test message to themselves and stored encrypted.</summary>
    [HttpPost("mailboxes/smtp")]
    public async Task<ActionResult<MailboxDto>> ConnectSmtp(ConnectSmtpRequest request, CancellationToken ct) => await mailboxes.ConnectSmtpAsync(request, ct);

    /// <summary>Reads the inbox of the email connected with an app password (headers only, read-only) and records replies to sent emails.</summary>
    [HttpPost("mailboxes/smtp/check-replies")]
    [EnableRateLimiting(RateLimits.ReplyCheck)]
    public async Task<ActionResult<ReplyCheckResult>> CheckReplies(CancellationToken ct) => await replies.CheckAsync(ct);

    [HttpDelete("mailboxes/{provider}")]
    public async Task<IActionResult> Disconnect(MailProvider provider, CancellationToken ct) { await mailboxes.DisconnectAsync(provider, ct); return NoContent(); }

    /// <summary>The provider sends the browser here, so there is no bearer token; the sealed, expiring state identifies the person.</summary>
    [AllowAnonymous]
    [HttpGet("mailboxes/callback/{provider}")]
    public async Task<IActionResult> Callback(MailProvider provider, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct) =>
        Redirect(await mailboxes.CompleteAsync(provider, code, state, error, ct));

    [HttpPost("drafts/send-via-mailbox-batch")]
    public async Task<ActionResult<IReadOnlyList<BatchSendResultItem>>> SendBatch(BatchSendRequest request, CancellationToken ct) => Ok(await mailboxes.SendBatchAsync(request, ct));

    [HttpPost("drafts/{id:guid}/send-via-mailbox")]
    public async Task<ActionResult<ProviderExecutionDto>> Send(Guid id, SendViaMailboxRequest request, CancellationToken ct) => await mailboxes.SendDraftAsync(id, request, ct);
}
