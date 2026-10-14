using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Outreach;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Mail;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Mail;

/// <param name="Configured">The server has the credentials for this provider; false means "Not configured" and nothing can be connected.</param>
public sealed record MailboxDto(MailProvider Provider, bool Configured, bool Connected, string? EmailAddress, MailboxStatus? Status, DateTime? ConnectedAt, DateTime? LastUsedAt);

public sealed record ConnectMailboxResponse(string Url);

public sealed record SendViaMailboxRequest(int Version, MailProvider Provider);

public sealed record BatchSendItem(Guid Id, int Version);
public sealed record BatchSendRequest(MailProvider Provider, IReadOnlyList<BatchSendItem> Items);
/// <param name="Outcome">Sent, Failed, or NotAttempted (an earlier message failed, so the rest were left alone).</param>
public sealed record BatchSendResultItem(Guid Id, string Outcome, string? Reason, ProviderExecutionDto? Execution);

/// <summary>
/// Sends an approved draft from the person's own connected Outlook or Gmail. It goes through the same gateway as a manual send
/// (exact-version approval, suppression list, daily quota, idempotency), so the only difference is who presses Send. A 2xx from
/// the provider is recorded as "accepted", never "delivered".
/// </summary>
public sealed class MailboxService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, IMailTransport transport, TokenProtector protector,
    IOptions<MailOptions> options, ProviderGateway gateway, Integrations.IntegrationNotifier? notifier = null, Integrations.IConnectorHttp? connectors = null)
{
    private MailOptions Opts => options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<MailboxDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.MailboxConnections.AsNoTracking().Where(m => m.OwnerId == user.OwnerId).ToListAsync(ct);
        return Enum.GetValues<MailProvider>().Select(p =>
        {
            var row = rows.FirstOrDefault(r => r.Provider == p);
            var plugin = row is not null && Integrations.PluginRef.Is(protector.Unprotect(row.EncryptedRefreshToken));
            return new MailboxDto(p, Opts.Ready(p) || plugin, row is not null, row?.EmailAddress, row?.Status, row?.ConnectedAt, row?.LastUsedAt);
        }).ToList();
    }

    public ConnectMailboxResponse BeginConnect(MailProvider provider)
    {
        if (provider == MailProvider.Smtp) throw Invalid("provider", "Email with an app password connects with the address and app password, not a sign-in.");
        if (!Opts.Ready(provider)) throw new ConflictException($"{Name(provider)} sending is not configured on this server yet.");
        var (verifier, challenge) = OAuthState.NewPkce();
        var state = new OAuthState(user.OwnerId, provider, verifier, Now.Add(OAuthState.Lifetime)).Seal(protector);
        return new(transport.AuthorizeUrl(provider, RedirectUri(provider), state, challenge));
    }

    /// <summary>
    /// Connects the person's own email with an app password. The login is proven by sending one short test message to themselves, so a wrong
    /// password or blocked server fails here, not in the middle of a campaign. Stored only encrypted.
    /// </summary>
    public async Task<MailboxDto> ConnectSmtpAsync(ConnectSmtpRequest r, CancellationToken ct)
    {
        if (!Opts.Ready(MailProvider.Smtp)) throw new ConflictException("Sending with an app password is not available on this server yet.");
        if (r is null || string.IsNullOrWhiteSpace(r.Username) || !ValidAddress(r.Username.Trim())) throw Invalid("username", "Enter your full email address.");
        if (string.IsNullOrWhiteSpace(r.Password) || r.Password.Length > 200) throw Invalid("password", "Enter the app password.");
        string host; int port;
        if (SmtpPresets.All.TryGetValue(r.Preset ?? string.Empty, out var preset)) { host = preset.Host; port = preset.Port; }
        else if (string.Equals(r.Preset, "custom", StringComparison.OrdinalIgnoreCase))
        {
            host = (r.Host ?? string.Empty).Trim().ToLowerInvariant();
            port = r.Port ?? 587;
            if (host.Length is 0 or > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) throw Invalid("host", "Enter the mail server name, like smtp.example.com.");
            if (!SmtpPresets.AllowedPorts.Contains(port)) throw Invalid("port", "Use port 587 (or 2525).");
            if (!await IsPublicHostAsync(host, ct)) throw Invalid("host", "That mail server is not reachable from here. Use its public address.");
        }
        else throw Invalid("preset", "Choose Gmail, Outlook, Microsoft 365, Yahoo or another provider.");

        var username = r.Username.Trim();
        var creds = new SmtpCredentials(host, port, username, r.Password);
        var json = System.Text.Json.JsonSerializer.Serialize(creds);
        MailSendResult test;
        try
        {
            test = await transport.SendAsync(MailProvider.Smtp, json, new OutgoingMail(username, "OpportunityPilot is connected to this email",
                "This is the one test message OpportunityPilot sends when you connect your email. From now on it only sends messages you have approved, one version at a time. You can disconnect in Settings at any time."), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { test = new(false, null, string.Empty, "The email server could not be reached."); }
        if (!test.Accepted) throw Invalid("password", test.Failure ?? "The email server did not accept the login.");

        var encrypted = protector.Protect(json);
        var existing = await db.MailboxConnections.FirstOrDefaultAsync(m => m.OwnerId == user.OwnerId && m.Provider == MailProvider.Smtp, ct);
        if (existing is null) db.MailboxConnections.Add(new MailboxConnection(user.OwnerId, MailProvider.Smtp, username, encrypted, Now));
        else existing.Reconnect(username, encrypted, Now);
        db.UserEvents.Add(new UserEvent(user.OwnerId, UserEventKinds.MailboxConnected, Now, $"Connected your email ({username}) with an app password"));
        await db.SaveChangesAsync(ct);
        return new MailboxDto(MailProvider.Smtp, true, true, username, MailboxStatus.Connected, Now, null);
    }

    // A custom mail server must resolve to public addresses only, so the form cannot be used to probe this server's own network.
    private async Task<bool> IsPublicHostAsync(string host, CancellationToken ct) =>
        Opts.AllowPrivateSmtpHosts || await MailHosts.PublicAddressesAsync(host, allowPrivate: false, ct) is not null;

    /// <summary>Finishes the browser round trip. Returns where to send the person next; never throws for a provider or user mistake.</summary>
    public async Task<string> CompleteAsync(MailProvider provider, string? code, string? state, string? error, CancellationToken ct)
    {
        var back = (Opts.WebBase ?? string.Empty).TrimEnd('/') + "/settings";
        string Result(string outcome) => $"{back}?mailbox={outcome}&provider={provider}";
        if (!string.IsNullOrEmpty(error)) return Result("declined");
        var s = OAuthState.Open(protector, state, Now);
        if (s is null || s.Provider != provider || string.IsNullOrWhiteSpace(code) || !Opts.Ready(provider)) return Result("expired");
        MailTokens tokens;
        try { tokens = await transport.ExchangeCodeAsync(provider, code, s.Verifier, RedirectUri(provider), ct); }
        catch (InvalidOperationException) { return Result("failed"); }
        var encrypted = protector.Protect(tokens.RefreshToken!);
        var existing = await db.MailboxConnections.FirstOrDefaultAsync(m => m.OwnerId == s.OwnerId && m.Provider == provider, ct);
        if (existing is null) db.MailboxConnections.Add(new MailboxConnection(s.OwnerId, provider, tokens.EmailAddress, encrypted, Now));
        else existing.Reconnect(tokens.EmailAddress, encrypted, Now);
        // Written for the person who started the sign-in (the callback carries no signed-in user).
        db.UserEvents.Add(new UserEvent(s.OwnerId, UserEventKinds.MailboxConnected, Now, $"Connected {Name(provider)} ({tokens.EmailAddress})"));
        await db.SaveChangesAsync(ct);
        return Result("connected");
    }

    public async Task DisconnectAsync(MailProvider provider, CancellationToken ct)
    {
        var row = await db.MailboxConnections.FirstOrDefaultAsync(m => m.OwnerId == user.OwnerId && m.Provider == provider, ct)
            ?? throw new NotFoundException("That mailbox is not connected.");
        db.MailboxConnections.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProviderExecutionDto> SendDraftAsync(Guid draftId, SendViaMailboxRequest r, CancellationToken ct)
    {
        if (r is null || !Enum.IsDefined(r.Provider)) throw Invalid("provider", "Choose Outlook, Gmail or your email with an app password.");
        var connection = await db.MailboxConnections.FirstOrDefaultAsync(m => m.OwnerId == user.OwnerId && m.Provider == r.Provider, ct)
            ?? throw Invalid("provider", $"Connect your {Name(r.Provider)} mailbox in Settings first.");
        if (connection.Status != MailboxStatus.Connected) throw Invalid("provider", $"Connect your {Name(r.Provider)} mailbox again in Settings.");
        // A Gmail connected with one-click Connect sends through the plugin platform, so it needs no Google app on this server.
        var plugin = protector.Unprotect(connection.EncryptedRefreshToken) is { } stored && Integrations.PluginRef.Is(stored) && connectors is not null ? stored : null;
        if (plugin is null && !Opts.Ready(r.Provider)) throw new ConflictException($"{Name(r.Provider)} sending is not configured on this server.");

        var draft = await db.OutreachDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId && d.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Draft not found.");
        if (draft.Channel != DraftChannel.Email) throw Invalid("channel", "Only email messages can be sent from a mailbox.");
        if (string.IsNullOrWhiteSpace(draft.Recipient) || !ValidAddress(draft.Recipient)) throw Invalid("recipient", "Add a valid recipient email address first.");
        if (string.IsNullOrWhiteSpace(draft.Subject)) throw Invalid("subject", "Add a subject first.");

        var provider = r.Provider switch { MailProvider.Microsoft => ExecutionProvider.Outlook, MailProvider.Smtp => ExecutionProvider.Smtp, _ => ExecutionProvider.Gmail };
        var execution = await gateway.StartDraftAsync(draftId, new StartExecutionRequest(r.Version), ct, provider);
        // A retry of the same approved version must never send twice: an existing execution means a send already happened or is unresolved.
        if (execution.Reused)
            throw new ConflictException(execution.State == ExecutionState.Succeeded
                ? "This version was already sent."
                : "A send of this version is already in progress or needs confirming. Check your Sent folder, then confirm it as sent.");

        var refreshToken = protector.Unprotect(connection.EncryptedRefreshToken);
        // Email with an app password has no short-lived token: the stored login is what sends.
        var access = refreshToken is null ? null : r.Provider == MailProvider.Smtp || plugin is not null ? refreshToken : await transport.RefreshAsync(r.Provider, refreshToken, ct);
        if (access is null)
        {
            connection.MarkNeedsReconnect();
            await db.SaveChangesAsync(ct);
            await gateway.FailAsync(execution.Id, new FailExecutionRequest("The mailbox access expired or was revoked. Connect it again."), ct);
            throw new ConflictException($"Your {Name(r.Provider)} connection expired. Connect it again in Settings; nothing was sent.");
        }

        MailSendResult result;
        try
        {
            var outgoing = new OutgoingMail(draft.Recipient, draft.Subject, draft.Body);
            result = plugin is not null ? await Integrations.PluginService.SendGmailAsync(connectors!, plugin, outgoing, ct) : await transport.SendAsync(r.Provider, access, outgoing, ct);
        }
        catch (HttpRequestException) { result = new(false, null, string.Empty, "The mail provider could not be reached. Nothing was sent."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { result = new(false, null, string.Empty, "The mail provider took too long to answer. Check your Sent folder before trying again."); }

        if (!result.Accepted)
        {
            if (result.TokenRejected) { connection.MarkNeedsReconnect(); await db.SaveChangesAsync(ct); }
            await gateway.FailAsync(execution.Id, new FailExecutionRequest(result.Failure ?? "The message was not accepted."), ct);
            throw new ConflictException(result.Failure ?? "The message was not accepted.");
        }

        connection.MarkUsed(Now);
        var confirmed = await gateway.ConfirmAsync(execution.Id, new ConfirmExecutionRequest(result.Receipt, result.Reference), ct);
        if (notifier is not null)
        {
            var org = await db.Opportunities.AsNoTracking().Where(o => o.Id == draft.OpportunityId).Select(o => o.Organization).FirstOrDefaultAsync(ct);
            await notifier.NotifyAsync(user.OwnerId, Integrations.IntegrationEvents.Sent, "Email sent",
                [$"To {draft.Recipient}" + (string.IsNullOrWhiteSpace(org) ? "" : $" at {org}"), $"Subject: {draft.Subject}"], "/outreach", ct);
        }
        return confirmed;
    }

    public const int MaxBatch = 25;

    /// <summary>
    /// "Send all": approved drafts go out one at a time through the same checks as a single send (exact approval, suppression, quota,
    /// at most once). The first failure stops the batch and says which message failed; the rest are marked NotAttempted, never sent.
    /// </summary>
    public async Task<IReadOnlyList<BatchSendResultItem>> SendBatchAsync(BatchSendRequest r, CancellationToken ct)
    {
        if (r?.Items is null || r.Items.Count == 0) throw Invalid("items", "At least one message is required.");
        if (r.Items.Count > MaxBatch) throw Invalid("items", $"At most {MaxBatch} messages can be sent at once.");
        if (r.Items.Select(i => i.Id).Distinct().Count() != r.Items.Count) throw Invalid("items", "Each message can appear only once.");
        var results = new List<BatchSendResultItem>(r.Items.Count);
        var stopped = false;
        foreach (var item in r.Items)
        {
            if (stopped) { results.Add(new(item.Id, "NotAttempted", "An earlier message failed, so this one was not sent.", null)); continue; }
            try
            {
                var execution = await SendDraftAsync(item.Id, new SendViaMailboxRequest(item.Version, r.Provider), ct);
                results.Add(new(item.Id, "Sent", null, execution));
            }
            catch (Exception ex) when (ex is NotFoundException or ConflictException or RequestValidationException)
            {
                db.ChangeTracker.Clear();
                var reason = ex is RequestValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;
                results.Add(new(item.Id, "Failed", reason, null));
                stopped = true;
            }
        }
        return results;
    }

    private string RedirectUri(MailProvider provider) => $"{(Opts.PublicApiBase ?? string.Empty).TrimEnd('/')}/api/v1/mailboxes/callback/{provider}";

    private static bool ValidAddress(string value) => MailAddress.TryCreate(value, out var a) && a.Address == value.Trim();

    private static string Name(MailProvider p) => p switch { MailProvider.Microsoft => "Outlook", MailProvider.Smtp => "Email (app password)", _ => "Gmail" };

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
