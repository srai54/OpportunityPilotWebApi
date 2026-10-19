using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.Application.Integrations;

/// <summary>
/// One service's sign-in ("Connect with Notion"), exactly as its own docs describe it.
/// </summary>
/// <param name="ResponseType">The authorize address needs <c>response_type=code</c> (Notion, Airtable).</param>
/// <param name="Pkce">The sign-in uses a PKCE code challenge (Airtable requires it).</param>
/// <param name="SecretRequired">The app cannot work without a client secret (Airtable can).</param>
/// <param name="JsonBody">The token request is JSON (Notion) instead of a form.</param>
/// <param name="SecretInBody">The client id and secret go in the token request body (HubSpot) instead of HTTP Basic.</param>
public sealed record OauthProvider(
    IntegrationKind Kind, string AuthorizeUrl, string TokenUrl, string? Scope, IReadOnlyDictionary<string, string> Extra,
    bool ResponseType, bool Pkce, bool SecretRequired, bool JsonBody, bool SecretInBody);

public static class OauthProviders
{
    private static readonly Dictionary<string, string> None = [];

    public static readonly IReadOnlyDictionary<IntegrationKind, OauthProvider> All = new Dictionary<IntegrationKind, OauthProvider>
    {
        // https://developers.notion.com/docs/authorization — owner=user, Basic auth, JSON body. The person picks pages and databases on Notion's screen.
        [IntegrationKind.Notion] = new(IntegrationKind.Notion, "https://api.notion.com/v1/oauth/authorize", "https://api.notion.com/v1/oauth/token", null,
            new Dictionary<string, string> { ["owner"] = "user" }, ResponseType: true, Pkce: false, SecretRequired: true, JsonBody: true, SecretInBody: false),
        // https://developers.hubspot.com/docs/api/oauth-quickstart-guide — scopes space-separated; access tokens last 30 minutes.
        [IntegrationKind.HubSpot] = new(IntegrationKind.HubSpot, "https://app.hubspot.com/oauth/authorize", "https://api.hubapi.com/oauth/v1/token",
            "oauth crm.objects.companies.read crm.objects.companies.write crm.objects.contacts.read crm.objects.contacts.write", None,
            ResponseType: false, Pkce: false, SecretRequired: true, JsonBody: false, SecretInBody: true),
        // https://docs.slack.dev/messaging/sending-messages-using-incoming-webhooks — the person picks the channel; the reply carries incoming_webhook.url.
        [IntegrationKind.Slack] = new(IntegrationKind.Slack, "https://slack.com/oauth/v2/authorize", "https://slack.com/api/oauth.v2.access", "incoming-webhook", None,
            ResponseType: false, Pkce: false, SecretRequired: true, JsonBody: false, SecretInBody: false),
        // https://airtable.com/developers/web/api/oauth-reference — PKCE (S256) required; access tokens last 60 minutes, refresh tokens 60 days.
        [IntegrationKind.Airtable] = new(IntegrationKind.Airtable, "https://airtable.com/oauth2/v1/authorize", "https://airtable.com/oauth2/v1/token",
            "data.records:read data.records:write schema.bases:read", None, ResponseType: true, Pkce: true, SecretRequired: false, JsonBody: false, SecretInBody: false),
    };

    public static bool Supports(IntegrationKind kind) => All.ContainsKey(kind);
}

/// <summary>What the sign-in carries through the other service and back: who started it, for which service, the PKCE verifier and an expiry. Encrypted, so it cannot be forged or read.</summary>
/// <param name="Purpose">Always <see cref="ConnectorPurpose"/>, so a mailbox sign-in state can never be replayed here.</param>
public sealed record ConnectorOauthState(string Purpose, Guid OwnerId, IntegrationKind Kind, string? Verifier, DateTime ExpiresAt)
{
    public const string ConnectorPurpose = "connector";

    public string Seal(TokenProtector protector) => protector.Protect(JsonSerializer.Serialize(this));

    public static ConnectorOauthState? Open(TokenProtector protector, string? sealedState, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(sealedState) || sealedState.Length > 2_000 || protector.Unprotect(sealedState) is not { } json) return null;
        try { return JsonSerializer.Deserialize<ConnectorOauthState>(json) is { Purpose: ConnectorPurpose } s && s.ExpiresAt > now ? s : null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>A token reply. <paramref name="Raw"/> is the whole reply (Slack's webhook, Notion's workspace); it never leaves this class unencrypted.</summary>
public sealed record OauthTokens(string? AccessToken, string? RefreshToken, DateTime? ExpiresAt, JsonElement Raw);

/// <summary>
/// "Connect with Notion / HubSpot / Slack / Airtable": the person approves on the service's own screen and comes back connected, instead of
/// creating and pasting a token. Tokens are stored only encrypted (access token as the connection's secret, refresh token encrypted inside
/// its settings), refreshed before they expire, and never returned or put in a URL.
/// </summary>
public sealed class ConnectorOauthService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, TokenProtector protector, IConnectorHttp http, IEnumerable<ILeadTarget> targets,
    IOptions<IntegrationOptions> options, IOptions<MailOptions> mail, ILogger<ConnectorOauthService> logger)
{
    /// <summary>A token this close to its expiry is refreshed first, so it cannot run out in the middle of sending leads.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The server has the app's id (and secret where the service needs one), a key to encrypt tokens, and the web address to come back to.</summary>
    public bool Configured(IntegrationKind kind) =>
        OauthProviders.All.TryGetValue(kind, out var p) && options.Value.App(kind) is { HasId: true } app && (app.HasSecret || !p.SecretRequired)
        && mail.Value.HasKey && !string.IsNullOrWhiteSpace(mail.Value.WebBase);

    public static string RedirectUri(IntegrationKind kind, string apiBase) => $"{apiBase.TrimEnd('/')}/api/v1/integrations/oauth/callback/{kind}";

    public OauthStartResponse Begin(IntegrationKind kind, string apiBase)
    {
        var name = IntegrationKinds.Name(kind);
        if (!OauthProviders.All.TryGetValue(kind, out var p)) throw Invalid("kind", $"{name} connects with an address or key, not a sign-in.");
        if (!Configured(kind)) throw new ConflictException($"Connecting with {name} is not switched on on this server yet. Use a token instead.");
        string? verifier = null, challenge = null;
        if (p.Pkce) (verifier, challenge) = OAuthState.NewPkce();
        var state = new ConnectorOauthState(ConnectorOauthState.ConnectorPurpose, user.OwnerId, kind, verifier, Now.Add(OAuthState.Lifetime)).Seal(protector);
        var query = new List<(string Key, string Value)> { ("client_id", options.Value.App(kind)!.ClientId!.Trim()), ("redirect_uri", RedirectUri(kind, apiBase)) };
        if (p.ResponseType) query.Add(("response_type", "code"));
        if (p.Scope is not null) query.Add(("scope", p.Scope));
        query.AddRange(p.Extra.Select(e => (e.Key, e.Value)));
        query.Add(("state", state));
        if (challenge is not null) { query.Add(("code_challenge", challenge)); query.Add(("code_challenge_method", "S256")); }
        return new(p.AuthorizeUrl + "?" + string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}")));
    }

    /// <summary>
    /// The service's anonymous callback only hands the code and state to the web app; the signed-in app posts them to
    /// <see cref="FinishAsync"/>, which checks the state belongs to the person finishing. A consent link started by someone else
    /// therefore cannot attach a victim's Notion/HubSpot/Slack/Airtable to another account.
    /// </summary>
    public string HandOff(IntegrationKind kind, string? code, string? state, string? error)
    {
        var back = (mail.Value.WebBase ?? string.Empty).TrimEnd('/') + "/connectors";
        if (!string.IsNullOrEmpty(error)) return $"{back}?error=declined&service={kind}";
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || code.Length > 2_000) return $"{back}?error=expired&service={kind}";
        return $"{back}?oauthCode={Uri.EscapeDataString(code)}&oauthState={Uri.EscapeDataString(state)}&service={kind}";
    }

    /// <summary>Finishes connecting for the signed-in person only (see <see cref="HandOff"/>).</summary>
    public Task<string> FinishAsync(IntegrationKind kind, string? code, string? state, string apiBase, CancellationToken ct) =>
        CompleteAsync(kind, code, state, null, apiBase, ct, user.OwnerId);

    /// <summary>
    /// Finishes the browser round trip and says where to send the person next: <c>/connectors?connected=Notion</c>, or
    /// <c>/connectors?error=declined|expired|failed&amp;service=Notion</c>. Never throws for a service or user mistake; never puts a token in the address.
    /// </summary>
    public async Task<string> CompleteAsync(IntegrationKind kind, string? code, string? state, string? error, string apiBase, CancellationToken ct, Guid? requiredOwner = null)
    {
        var back = (mail.Value.WebBase ?? string.Empty).TrimEnd('/') + "/connectors";
        string Failed(string why) => $"{back}?error={why}&service={kind}";
        if (!OauthProviders.Supports(kind)) return Failed("failed");
        if (!string.IsNullOrEmpty(error)) return Failed("declined");
        var s = ConnectorOauthState.Open(protector, state, Now);
        if (s is null || s.Kind != kind || string.IsNullOrWhiteSpace(code) || code.Length > 2_000 || !Configured(kind)) return Failed("expired");
        if (requiredOwner is { } owner && s.OwnerId != owner) return Failed("expired");

        var redirect = RedirectUri(kind, apiBase);
        var fields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect };
        if (s.Verifier is not null) fields["code_verifier"] = s.Verifier;
        try
        {
            var tokens = await RequestTokensAsync(kind, fields, ct);
            var (label, secret, settings, events) = await FinishAsync(kind, tokens, redirect, ct);
            var row = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.OwnerId == s.OwnerId && c.Kind == kind, ct);
            var encrypted = protector.Protect(secret);
            if (row is null) db.IntegrationConnections.Add(row = new IntegrationConnection(s.OwnerId, kind, label, encrypted, settings, events, Now));
            else row.Reconnect(label, encrypted, settings, events, Now);
            if (kind == IntegrationKind.Slack) await WelcomeAsync(row, secret, ct);
            await db.SaveChangesAsync(ct);
            return $"{back}?connected={kind}";
        }
        catch (ConnectorProblem p)
        {
            logger.LogWarning("Connecting {Kind} by sign-in failed: {Reason}", kind, p.Message);
            return Failed("failed");
        }
    }

    /// <summary>
    /// The access token for a connection, refreshed first when it is about to expire (HubSpot, Airtable). A pasted token is returned as it is.
    /// A refused refresh marks the connection and is a 409 asking the person to connect again.
    /// </summary>
    public async Task<string> AccessTokenAsync(IntegrationConnection row, CancellationToken ct)
    {
        var name = IntegrationKinds.Name(row.Kind);
        var token = protector.Unprotect(row.EncryptedSecret) ?? throw new ConflictException("The stored login can no longer be read on this server. Connect again.");
        var settings = Settings(row.SettingsJson);
        if (settings["oauth"] is not JsonObject oauth || !TryDate(oauth["expiresAt"], out var expiresAt) || expiresAt > Now.Add(RefreshMargin)) return token;

        var refresh = oauth["refresh"]?.GetValue<string>() is { } sealedRefresh ? protector.Unprotect(sealedRefresh) : null;
        var again = $"{name} has signed this connection out. Connect {name} again.";
        if (refresh is null || !Configured(row.Kind)) return await FailAsync(row, again, ct);
        var fields = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh };
        if (row.Kind == IntegrationKind.HubSpot && oauth["redirectUri"]?.GetValue<string>() is { } redirect) fields["redirect_uri"] = redirect;
        OauthTokens tokens;
        try { tokens = await RequestTokensAsync(row.Kind, fields, ct); }
        catch (ConnectorProblem p) { return await FailAsync(row, p.Message.Contains("could not be reached", StringComparison.Ordinal) ? p.Message : again, ct); }

        oauth["refresh"] = protector.Protect(tokens.RefreshToken ?? refresh);
        oauth["expiresAt"] = tokens.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture);
        row.RotateSecret(protector.Protect(tokens.AccessToken!), settings.ToJsonString());
        await db.SaveChangesAsync(ct);
        return tokens.AccessToken!;
    }

    private async Task<string> FailAsync(IntegrationConnection row, string message, CancellationToken ct)
    {
        row.MarkFailed(message);
        await db.SaveChangesAsync(ct);
        throw new ConflictException(message);
    }

    /// <summary>Connected by sign-in, and (Notion, Airtable) still waiting for the database or table to be chosen.</summary>
    public static (bool Oauth, bool NeedsTarget) Flags(string settingsJson)
    {
        var s = Settings(settingsJson);
        return (s["oauth"] is JsonObject, s["needsTarget"]?.GetValueKind() == JsonValueKind.True);
    }

    /// <summary>The settings the target check produced, keeping the sign-in part (refresh token, expiry) of what was stored.</summary>
    public static string KeepOauth(string targetSettingsJson, string storedSettingsJson)
    {
        var target = Settings(targetSettingsJson);
        if (Settings(storedSettingsJson)["oauth"] is JsonObject oauth) target["oauth"] = oauth.DeepClone();
        return target.ToJsonString();
    }

    private async Task<OauthTokens> RequestTokensAsync(IntegrationKind kind, Dictionary<string, string> fields, CancellationToken ct)
    {
        var p = OauthProviders.All[kind];
        var app = options.Value.App(kind)!;
        var name = IntegrationKinds.Name(kind);
        var headers = new Dictionary<string, string>();
        if (p.SecretInBody || !app.HasSecret)
        {
            fields["client_id"] = app.ClientId!.Trim();
            if (app.HasSecret) fields["client_secret"] = app.ClientSecret!.Trim();
        }
        else headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId!.Trim()}:{app.ClientSecret!.Trim()}"));

        var reply = await http.SendAsync(p.JsonBody
            ? new ConnectorRequest(HttpMethod.Post, p.TokenUrl, ConnectorJson.Write(fields), Headers: headers)
            : new ConnectorRequest(HttpMethod.Post, p.TokenUrl, Headers: headers, Form: fields), ct);
        if (reply.Status == 0) throw new ConnectorProblem($"{name} could not be reached. Try again in a minute.");
        var json = reply.Json();
        var slackRefused = kind == IntegrationKind.Slack && !(json.ValueKind == JsonValueKind.Object && json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);
        var access = json.Str("access_token");
        if (!reply.Ok || slackRefused || (kind != IntegrationKind.Slack && string.IsNullOrEmpty(access)))
            throw new ConnectorProblem($"{name} did not accept the sign-in (answer {reply.Status}{(json.Str("error") is { Length: <= 60 } e ? ", " + e : "")}).");
        DateTime? expires = int.TryParse(json.Str("expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? Now.AddSeconds(seconds) : null;
        return new(access, json.Str("refresh_token"), expires, json);
    }

    private async Task<(string Label, string Secret, string Settings, string Events)> FinishAsync(IntegrationKind kind, OauthTokens t, string redirect, CancellationToken ct)
    {
        var oauth = new JsonObject();
        if (t.RefreshToken is { Length: > 0 } refresh) oauth["refresh"] = protector.Protect(refresh);
        if (t.ExpiresAt is { } at) oauth["expiresAt"] = at.ToString("O", CultureInfo.InvariantCulture);
        if (kind == IntegrationKind.HubSpot) oauth["redirectUri"] = redirect;

        switch (kind)
        {
            case IntegrationKind.Slack:
            {
                var hook = t.Raw.TryGetProperty("incoming_webhook", out var h) ? h : default;
                var url = hook.Str("url");
                if (WebhookPoster.CheckUrl(IntegrationKind.Slack, url) is not null) throw new ConnectorProblem("Slack did not give a channel address.");
                var team = t.Raw.TryGetProperty("team", out var tm) ? tm.Str("name") : null;
                var channel = Cut(hook.Str("channel"), 80);
                oauth["account"] = Cut(team, 100);
                oauth["channel"] = channel;
                var label = $"Slack {channel ?? "channel"}" + (string.IsNullOrWhiteSpace(team) ? "" : $" in {Cut(team, 100)}");
                return (label, url!.Trim(), new JsonObject { ["oauth"] = oauth }.ToJsonString(), IntegrationEvents.Normalise(null));
            }
            case IntegrationKind.HubSpot:
            {
                var (label, settings) = await targets.First(x => x.Kind == kind).VerifyAsync(t.AccessToken!, null, null, ct);
                var s = Settings(settings);
                s["oauth"] = oauth;
                return (label, t.AccessToken!, s.ToJsonString(), "");
            }
            default:
            {
                var workspace = Cut(t.Raw.Str("workspace_name"), 100);
                if (workspace is not null) oauth["account"] = workspace;
                var label = kind == IntegrationKind.Notion ? $"Notion: {workspace ?? "workspace"}" : "Airtable";
                return (label, t.AccessToken!, new JsonObject { ["needsTarget"] = true, ["oauth"] = oauth }.ToJsonString(), "");
            }
        }
    }

    /// <summary>Slack: one "connected" message in the chosen channel, like a pasted webhook gets. A failure is recorded, not fatal.</summary>
    private async Task WelcomeAsync(IntegrationConnection row, string url, CancellationToken ct)
    {
        try
        {
            await WebhookPoster.PostAsync(http, IntegrationKind.Slack, url, new ChannelMessage("test", "OpportunityPilot is connected",
                ["Messages about your leads will appear here.", "You choose which ones in Connectors."]), Now, ct);
            row.MarkUsed(Now);
        }
        catch (ConnectorProblem p) { row.MarkFailed(p.Message); }
    }

    private static JsonObject Settings(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    private static bool TryDate(JsonNode? node, out DateTime value)
    {
        value = default;
        return node?.GetValueKind() == JsonValueKind.String
               && DateTime.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length > max ? s[..max] : s.Trim();

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
