using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Mail;

namespace OpportunityPilot.Application.Integrations;

/// <summary>
/// One server key for a managed-connection platform (Composio) that brings its own sign-in apps for Notion, Gmail, HubSpot, Slack and
/// Airtable, so "Connect" works like a ChatGPT plugin: no app to register and no token to paste. <c>Plugins__ApiKey</c> switches it on.
/// </summary>
public sealed class PluginOptions
{
    public const string SectionName = "Plugins";
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://backend.composio.dev";
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);
    public string Api(string path) => $"{BaseUrl.TrimEnd('/')}/api/v3.1/{path}";
    public Dictionary<string, string> KeyHeader() => new() { ["x-api-key"] = ApiKey!.Trim() };
}

/// <summary>
/// What a plugin connection stores instead of a token: <c>plugin:{connected account id}</c>, plus <c>/{channel}</c> for Slack. It is not a
/// secret (useless without the server key) but is kept encrypted like every other connection secret.
/// </summary>
public static class PluginRef
{
    public const string Prefix = "plugin:";
    public static string Make(string accountId, string? channel = null) => Prefix + accountId + (channel is null ? "" : "/" + channel);
    public static bool Is(string? secret) => secret is not null && secret.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string? secret, out string accountId, out string? channel)
    {
        accountId = ""; channel = null;
        if (!Is(secret)) return false;
        var parts = secret![Prefix.Length..].Split('/', 2);
        accountId = parts[0];
        channel = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        return accountId.Length > 0;
    }
}

/// <summary>
/// Sends a request whose bearer is a <see cref="PluginRef"/> through the platform's proxy, which adds the person's own token there, so
/// HubSpot, Notion, Airtable, Slack and Gmail calls stay exactly as written for a pasted token. Everything else passes straight through.
/// </summary>
public sealed class PluginConnectorHttp(IConnectorHttp inner, IOptions<PluginOptions> options) : IConnectorHttp
{
    public async Task<ConnectorResponse> SendAsync(ConnectorRequest r, CancellationToken ct)
    {
        if (!PluginRef.TryParse(r.Bearer, out var account, out _)) return await inner.SendAsync(r, ct);
        var o = options.Value;
        if (!o.Configured || r.Form is not null) return new(401, "");
        var uri = new Uri(r.Url);
        var parameters = (r.Headers ?? new Dictionary<string, string>()).Select(h => new { name = h.Key, value = h.Value, type = "header" }).ToList();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)))
            parameters.Add(new { name = Uri.UnescapeDataString(pair[0]), value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "", type = "query" });
        object? body = r.JsonBody is null ? null : JsonDocument.Parse(r.JsonBody).RootElement.Clone();
        var reply = await inner.SendAsync(new ConnectorRequest(HttpMethod.Post, o.Api("tools/execute/proxy"), ConnectorJson.Write(new
        {
            endpoint = uri.GetLeftPart(UriPartial.Path), method = r.Method.Method.ToUpperInvariant(), connected_account_id = account, body, parameters,
        }), Headers: o.KeyHeader()), ct);
        if (!reply.Ok) return new(reply.Status is 0 or >= 500 ? reply.Status : 401, reply.Body);
        var j = reply.Json();
        var status = j.ValueKind == JsonValueKind.Object && j.TryGetProperty("status", out var s) && s.TryGetInt32(out var n) ? n : 502;
        var data = j.ValueKind == JsonValueKind.Object && j.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetRawText() : "";
        return new(status, data);
    }
}

/// <summary>Carried through the platform's sign-in and back: who started it, for which service, and an expiry. Encrypted.</summary>
public sealed record PluginState(string Purpose, Guid OwnerId, IntegrationKind Kind, DateTime ExpiresAt)
{
    public const string PluginPurpose = "plugin";
    public string Seal(TokenProtector protector) => protector.Protect(JsonSerializer.Serialize(this));

    public static PluginState? Open(TokenProtector protector, string? sealedState, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(sealedState) || sealedState.Length > 2_000 || protector.Unprotect(sealedState) is not { } json) return null;
        try { return JsonSerializer.Deserialize<PluginState>(json) is { Purpose: PluginPurpose } s && s.ExpiresAt > now ? s : null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// "Connect" for Notion, Gmail, HubSpot, Slack and Airtable through the managed-connection platform. The person approves on the service's
/// own screen and comes back connected. Only the platform's connected-account id is stored; tokens stay with the platform.
/// </summary>
public sealed class PluginService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, TokenProtector protector, IConnectorHttp http, IEnumerable<ILeadTarget> targets,
    IOptions<PluginOptions> options, IOptions<MailOptions> mail, ILogger<PluginService> logger)
{
    private static readonly ConcurrentDictionary<string, string> AuthConfigs = new();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private PluginOptions O => options.Value;

    public static string? Toolkit(IntegrationKind kind) => kind switch
    {
        IntegrationKind.Notion => "notion",
        IntegrationKind.HubSpot => "hubspot",
        IntegrationKind.Slack => "slack",
        IntegrationKind.Airtable => "airtable",
        IntegrationKind.Gmail => "gmail",
        _ => null
    };

    /// <summary>The server has the platform key, a key to seal the state, and the web address to come back to.</summary>
    public bool Ready(IntegrationKind kind) => Toolkit(kind) is not null && O.Configured && mail.Value.HasKey && !string.IsNullOrWhiteSpace(mail.Value.WebBase);

    public static string CallbackUri(IntegrationKind kind, string apiBase) => $"{apiBase.TrimEnd('/')}/api/v1/integrations/plugin/callback/{kind}";

    public async Task<OauthStartResponse> BeginAsync(IntegrationKind kind, string apiBase, CancellationToken ct)
    {
        var name = IntegrationKinds.Name(kind);
        if (Toolkit(kind) is null) throw Invalid("kind", $"{name} does not connect with one click.");
        if (!Ready(kind)) throw new ConflictException($"One-click connect for {name} is not switched on on this server yet.");
        var authConfig = await AuthConfigAsync(kind, ct);
        var state = new PluginState(PluginState.PluginPurpose, user.OwnerId, kind, Now.Add(OAuthState.Lifetime)).Seal(protector);
        var reply = await http.SendAsync(new ConnectorRequest(HttpMethod.Post, O.Api("connected_accounts/link"), ConnectorJson.Write(new
        {
            auth_config_id = authConfig, user_id = user.OwnerId.ToString("N"), callback_url = $"{CallbackUri(kind, apiBase)}?state={Uri.EscapeDataString(state)}",
        }), Headers: O.KeyHeader()), ct);
        var url = reply.Json().Str("redirect_url");
        if (!reply.Ok || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("Starting a {Kind} plugin connection failed with HTTP {Status}", kind, reply.Status);
            throw new ConflictException($"{name} could not be connected right now. Try again in a minute.");
        }
        return new(url!);
    }

    /// <summary>The platform sends the browser back here. Says where to go next; never throws for a service or user mistake.</summary>
    public async Task<string> CompleteAsync(IntegrationKind kind, string? state, string? accountId, string? status, CancellationToken ct)
    {
        var back = (mail.Value.WebBase ?? string.Empty).TrimEnd('/') + "/connectors";
        string Failed(string why) => $"{back}?error={why}&service={kind}";
        var s = PluginState.Open(protector, state, Now);
        if (s is null || s.Kind != kind || !Ready(kind)) return Failed("expired");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(accountId) || accountId.Length > 200) return Failed("declined");
        try
        {
            var account = await AccountAsync(accountId, ct);
            if (account is null || account.Value.Status != "ACTIVE" || account.Value.UserId != s.OwnerId.ToString("N")) return Failed("failed");
            var token = PluginRef.Make(accountId);
            var plugin = new JsonObject { ["plugin"] = true };
            string label, secret = token, settings, events = "";
            switch (kind)
            {
                case IntegrationKind.HubSpot:
                {
                    (label, settings) = await targets.First(t => t.Kind == kind).VerifyAsync(token, null, null, ct);
                    var js = JsonNode.Parse(settings) as JsonObject ?? [];
                    js["oauth"] = plugin;
                    settings = js.ToJsonString();
                    break;
                }
                case IntegrationKind.Slack:
                {
                    var me = (await Checked(http.SendAsync(new(HttpMethod.Get, "https://slack.com/api/auth.test", Bearer: token), ct), "Slack")).Json();
                    var channel = me.Str("user_id") ?? throw new ConnectorProblem("Slack did not say who connected.");
                    secret = PluginRef.Make(accountId, channel);
                    plugin["account"] = Cut(me.Str("team"), 100);
                    label = "Slack" + (Cut(me.Str("team"), 100) is { } team ? $": {team}" : "");
                    settings = new JsonObject { ["oauth"] = plugin }.ToJsonString();
                    events = IntegrationEvents.Normalise(null);
                    break;
                }
                case IntegrationKind.Gmail:
                {
                    var email = (await Checked(http.SendAsync(new(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile", Bearer: token), ct), "Gmail")).Json().Str("emailAddress")
                        ?? throw new ConnectorProblem("Gmail did not say which address connected.");
                    label = $"Gmail: {Cut(email, 150)}";
                    settings = new JsonObject { ["oauth"] = plugin }.ToJsonString();
                    var box = await db.MailboxConnections.FirstOrDefaultAsync(m => m.OwnerId == s.OwnerId && m.Provider == MailProvider.Google, ct);
                    if (box is null) db.MailboxConnections.Add(new MailboxConnection(s.OwnerId, MailProvider.Google, email, protector.Protect(token), Now));
                    else box.Reconnect(email, protector.Protect(token), Now);
                    break;
                }
                default:
                    label = IntegrationKinds.Name(kind);
                    settings = new JsonObject { ["needsTarget"] = true, ["oauth"] = plugin }.ToJsonString();
                    break;
            }

            var row = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.OwnerId == s.OwnerId && c.Kind == kind, ct);
            var old = row is null ? null : protector.Unprotect(row.EncryptedSecret);
            if (row is null) db.IntegrationConnections.Add(row = new IntegrationConnection(s.OwnerId, kind, label, protector.Protect(secret), settings, events, Now));
            else row.Reconnect(label, protector.Protect(secret), settings, events, Now);
            if (kind == IntegrationKind.Slack)
            {
                try
                {
                    await WebhookPoster.PostAsync(http, kind, secret, new ChannelMessage("test", "OpportunityPilot is connected",
                        ["Messages about your leads will appear here.", "You choose which ones in Connectors."]), Now, ct);
                    row.MarkUsed(Now);
                }
                catch (ConnectorProblem p) { row.MarkFailed(p.Message); }
            }
            await db.SaveChangesAsync(ct);
            if (PluginRef.TryParse(old, out var oldAccount, out _) && oldAccount != accountId) await ForgetAsync(old, ct);
            return $"{back}?connected={kind}";
        }
        catch (ConnectorProblem p)
        {
            logger.LogWarning("Connecting {Kind} through the plugin platform failed: {Reason}", kind, p.Message);
            return Failed("failed");
        }
    }

    /// <summary>The platform's view of a connection: ACTIVE, or anything else (signed out, expired). Null when it could not be asked.</summary>
    public async Task<string?> StatusAsync(string? secret, CancellationToken ct) =>
        PluginRef.TryParse(secret, out var id, out _) && O.Configured ? (await AccountAsync(id, ct))?.Status : null;

    /// <summary>Removes the connection on the platform too. Best effort: a failure there never blocks a disconnect here.</summary>
    public async Task ForgetAsync(string? secret, CancellationToken ct)
    {
        if (!PluginRef.TryParse(secret, out var id, out _) || !O.Configured) return;
        var reply = await http.SendAsync(new ConnectorRequest(HttpMethod.Delete, O.Api($"connected_accounts/{Uri.EscapeDataString(id)}"), Headers: O.KeyHeader()), ct);
        if (!reply.Ok) logger.LogInformation("Removing a plugin connection answered HTTP {Status}", reply.Status);
    }

    /// <summary>Gmail send for an approved draft whose mailbox was connected as a plugin. The caller has already passed the gateway checks.</summary>
    public static async Task<MailSendResult> SendGmailAsync(IConnectorHttp http, string pluginRef, OutgoingMail m, CancellationToken ct)
    {
        if (!MailBuilders.SafeHeader(m.To) || !MailBuilders.SafeHeader(m.Subject))
            return new(false, null, string.Empty, "The recipient or subject contains a line break, so it was not sent.");
        var r = await http.SendAsync(new ConnectorRequest(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send", MailBuilders.GmailSend(m), Bearer: pluginRef), ct);
        if (r.Status == 0) return new(false, null, string.Empty, "Gmail could not be reached. Check your Sent folder before trying again.");
        if (r.Status is 401 or 403) return new(false, null, string.Empty, "Gmail refused the connection. Connect Gmail again.", TokenRejected: true);
        if (r.Status == 429) return new(false, null, string.Empty, "Gmail asked us to slow down. Nothing was sent; try again in a few minutes.");
        if (!r.Ok) return new(false, null, string.Empty, $"Gmail did not accept the message (HTTP {r.Status}). Nothing was sent.");
        var id = r.Json().Str("id");
        return new(true, id, $"Accepted by Gmail{(id is null ? string.Empty : $" (message {id})")}. Accepted is not a delivery confirmation.", null);
    }

    private async Task<(string Status, string? UserId)?> AccountAsync(string id, CancellationToken ct)
    {
        var reply = await http.SendAsync(new ConnectorRequest(HttpMethod.Get, O.Api($"connected_accounts/{Uri.EscapeDataString(id)}"), Headers: O.KeyHeader()), ct);
        if (!reply.Ok) return reply.Status is 404 or 410 ? ("DELETED", null) : null;
        var j = reply.Json();
        return (j.Str("status")?.ToUpperInvariant() ?? "UNKNOWN", j.Str("user_id"));
    }

    /// <summary>The platform's own sign-in app for the service ("Composio-managed auth"): found once, created when missing, then remembered.</summary>
    private async Task<string> AuthConfigAsync(IntegrationKind kind, CancellationToken ct)
    {
        var slug = Toolkit(kind)!;
        var key = $"{O.ApiKey!.Trim().GetHashCode()}|{slug}";
        if (AuthConfigs.TryGetValue(key, out var cached)) return cached;
        var list = await http.SendAsync(new ConnectorRequest(HttpMethod.Get, O.Api($"auth_configs?toolkit_slug={slug}"), Headers: O.KeyHeader()), ct);
        string? id = null;
        if (list.Ok && list.Json() is { ValueKind: JsonValueKind.Object } lj && lj.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            id = items.EnumerateArray().FirstOrDefault(i => i.TryGetProperty("is_composio_managed", out var m) && m.ValueKind == JsonValueKind.True
                && !string.Equals(i.Str("status"), "DISABLED", StringComparison.OrdinalIgnoreCase)
                && (!i.TryGetProperty("toolkit", out var t) || t.Str("slug") is null || t.Str("slug") == slug)).Str("id");
        if (id is null)
        {
            var made = await http.SendAsync(new ConnectorRequest(HttpMethod.Post, O.Api("auth_configs"), ConnectorJson.Write(new
            {
                toolkit = new { slug }, auth_config = new { type = "use_composio_managed_auth" },
            }), Headers: O.KeyHeader()), ct);
            id = made.Ok && made.Json() is { ValueKind: JsonValueKind.Object } mj && mj.TryGetProperty("auth_config", out var ac) ? ac.Str("id") : null;
            if (id is null)
            {
                logger.LogWarning("Creating the {Kind} plugin sign-in app failed with HTTP {Status}", kind, made.Status);
                throw new ConflictException($"{IntegrationKinds.Name(kind)} could not be connected right now. Try again in a minute.");
            }
        }
        AuthConfigs[key] = id;
        return id;
    }

    private static async Task<ConnectorResponse> Checked(Task<ConnectorResponse> call, string name)
    {
        var r = await call;
        if (!r.Ok) throw new ConnectorProblem($"{name} answered {r.Status}.");
        if (name == "Slack" && !(r.Json() is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            throw new ConnectorProblem("Slack did not accept the connection.");
        return r;
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length > max ? s[..max] : s.Trim();

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
