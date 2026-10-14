using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Mail;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>"Connect with Notion / HubSpot / Slack / Airtable": the consent address, the sealed state, the callback, refresh, and owner scoping.</summary>
public class ConnectorOauthTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private const string ApiBase = "https://api.example.test";
    private const string Web = "https://app.example.test";

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private static readonly MailOptions Mail = new() { TokenKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()), WebBase = Web };

    private static IntegrationOptions Apps() => new()
    {
        Notion = new() { ClientId = "notion-id", ClientSecret = "notion-secret" },
        HubSpot = new() { ClientId = "hub-id", ClientSecret = "hub-secret" },
        Slack = new() { ClientId = "slack-id", ClientSecret = "slack-secret" },
        Airtable = new() { ClientId = "air-id" },
    };

    /// <summary>What the services answer. Token requests are recognised by their address; everything else is the API.</summary>
    private static ConnectorResponse Services(ConnectorRequest r) => r.Url switch
    {
        "https://api.notion.com/v1/oauth/token" => new(200, """{"access_token":"ntn-access","refresh_token":"ntn-refresh","bot_id":"b1","workspace_id":"w1","workspace_name":"Acme"}"""),
        "https://api.hubapi.com/oauth/v1/token" => r.Form!["grant_type"] == "refresh_token"
            ? (r.Form["refresh_token"] == "hub-refresh" ? new(200, """{"access_token":"hub-access-2","refresh_token":"hub-refresh","expires_in":1800}""") : new(400, """{"status":"BAD_REFRESH_TOKEN"}"""))
            : new(200, """{"access_token":"hub-access","refresh_token":"hub-refresh","expires_in":1800}"""),
        "https://slack.com/api/oauth.v2.access" => new(200, """
            {"ok":true,"access_token":"xoxb-1","team":{"name":"Acme","id":"T1"},
             "incoming_webhook":{"channel":"#leads","channel_id":"C1","configuration_url":"https://acme.slack.com/services/B1","url":"https://hooks.slack.com/services/T1/B1/hooksecret"}}
            """),
        "https://airtable.com/oauth2/v1/token" => new(200, """{"access_token":"air-access","refresh_token":"air-refresh","token_type":"Bearer ","expires_in":3600,"refresh_expires_in":5184000}"""),
        "https://api.hubapi.com/account-info/v3/details" => new(200, """{"portalId":42,"uiDomain":"app-eu1.hubspot.com"}"""),
        "https://api.notion.com/v1/search" => new(200, """
            {"results":[{"object":"database","id":"1a2b3c4d-5e6f-47a8-b9c0-d1e2f3a4b5c6","title":[{"plain_text":"Leads"}]},{"object":"page","id":"x"}]}
            """),
        var u when u.StartsWith("https://api.notion.com/v1/databases/", StringComparison.Ordinal) => new(200, """{"title":[{"plain_text":"Leads"}],"properties":{"Company":{"type":"title"}}}"""),
        _ => new(200, """{"results":[]}""")
    };

    private sealed record Setup(ConnectorOauthService Oauth, IntegrationService Integrations, InMemoryAppDbContext Db, FakeConnectorHttp Http, Clock Clock, Guid Owner, TokenProtector Protector);

    private static Setup Create(IntegrationOptions? apps = null, Guid? owner = null, InMemoryAppDbContext? db = null, Func<ConnectorRequest, ConnectorResponse>? answer = null)
    {
        db ??= new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = owner ?? Guid.NewGuid();
        var http = new FakeConnectorHttp(answer ?? Services);
        var clock = new Clock(T0);
        var protector = new TokenProtector(Options.Create(Mail));
        ILeadTarget[] targets = [new HubSpotTarget(http), new NotionTarget(http), new AirtableTarget(http)];
        var oauth = new ConnectorOauthService(db, new Owner(me), clock, protector, http, targets, Options.Create(apps ?? Apps()), Options.Create(Mail), NullLogger<ConnectorOauthService>.Instance);
        var integrations = new IntegrationService(db, new Owner(me), clock, protector, http, targets, [], oauth);
        return new(oauth, integrations, db, http, clock, me, protector);
    }

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private static async Task<string> ConnectAsync(Setup s, IntegrationKind kind)
    {
        var state = Query(s.Oauth.Begin(kind, ApiBase).Url)["state"];
        return await s.Oauth.CompleteAsync(kind, "code-1", state, null, ApiBase, default);
    }

    [Fact]
    public void The_consent_address_follows_each_services_docs()
    {
        var s = Create();
        var notion = s.Oauth.Begin(IntegrationKind.Notion, ApiBase).Url;
        Assert.StartsWith("https://api.notion.com/v1/oauth/authorize?", notion);
        var q = Query(notion);
        Assert.Equal(("notion-id", "code", "user"), (q["client_id"], q["response_type"], q["owner"]));
        Assert.Equal("https://api.example.test/api/v1/integrations/oauth/callback/Notion", q["redirect_uri"]);

        var hub = s.Oauth.Begin(IntegrationKind.HubSpot, ApiBase).Url;
        Assert.StartsWith("https://app.hubspot.com/oauth/authorize?", hub);
        Assert.Contains("scope=oauth%20crm.objects.companies.read%20crm.objects.companies.write%20crm.objects.contacts.read%20crm.objects.contacts.write", hub);

        Assert.Equal("incoming-webhook", Query(s.Oauth.Begin(IntegrationKind.Slack, ApiBase).Url)["scope"]);

        var air = Query(s.Oauth.Begin(IntegrationKind.Airtable, ApiBase).Url);
        Assert.Equal(("S256", "data.records:read data.records:write schema.bases:read"), (air["code_challenge_method"], air["scope"]));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", air["code_challenge"]);
        // Airtable: state is 16–1024 characters of letters, digits, '.', '-' and '_'.
        Assert.Matches("^[A-Za-z0-9._-]{16,1024}$", air["state"]);
        // The state is sealed: nothing readable, no tokens or secrets in the address.
        Assert.DoesNotContain("air-secret", string.Join(" ", air.Values));
        Assert.DoesNotContain(s.Owner.ToString(), string.Join(" ", air.Values));
    }

    [Fact]
    public void A_service_without_an_app_or_without_sign_in_is_refused()
    {
        var s = Create(new IntegrationOptions { Notion = new() { ClientId = "only-id" } });
        Assert.False(s.Oauth.Configured(IntegrationKind.Notion));
        Assert.False(s.Oauth.Configured(IntegrationKind.HubSpot));
        Assert.Contains("Use a token instead", Assert.Throws<ConflictException>(() => s.Oauth.Begin(IntegrationKind.Notion, ApiBase)).Message);
        Assert.Throws<RequestValidationException>(() => s.Oauth.Begin(IntegrationKind.Hunter, ApiBase));
        // Airtable works with a client id alone (PKCE), the others need the secret too.
        Assert.True(Create(new IntegrationOptions { Airtable = new() { ClientId = "air-id" } }).Oauth.Configured(IntegrationKind.Airtable));
    }

    [Fact]
    public async Task A_tampered_expired_or_wrong_service_state_connects_nothing()
    {
        var s = Create();
        var state = Query(s.Oauth.Begin(IntegrationKind.Notion, ApiBase).Url)["state"];
        var tampered = state[..^4] + (state.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        Assert.Equal($"{Web}/connectors?error=expired&service=Notion", await s.Oauth.CompleteAsync(IntegrationKind.Notion, "c", tampered, null, ApiBase, default));
        Assert.Equal($"{Web}/connectors?error=expired&service=HubSpot", await s.Oauth.CompleteAsync(IntegrationKind.HubSpot, "c", state, null, ApiBase, default));
        Assert.Equal($"{Web}/connectors?error=declined&service=Notion", await s.Oauth.CompleteAsync(IntegrationKind.Notion, null, state, "access_denied", ApiBase, default));
        // A mailbox sign-in state (same key) is not accepted here.
        var mailState = new OAuthState(s.Owner, MailProvider.Google, "v", T0.AddMinutes(5)).Seal(s.Protector);
        Assert.Contains("error=expired", await s.Oauth.CompleteAsync(IntegrationKind.Notion, "c", mailState, null, ApiBase, default));

        s.Clock.Now = T0.Add(OAuthState.Lifetime).AddSeconds(1);
        Assert.Contains("error=expired", await s.Oauth.CompleteAsync(IntegrationKind.Notion, "c", state, null, ApiBase, default));
        Assert.Empty(s.Http.Calls);
        Assert.Empty(await s.Db.IntegrationConnections.ToListAsync());
    }

    [Fact]
    public async Task Notion_stores_the_tokens_encrypted_never_returns_them_and_then_lets_the_person_pick_a_database()
    {
        var s = Create();
        Assert.Equal($"{Web}/connectors?connected=Notion", await ConnectAsync(s, IntegrationKind.Notion));

        var token = Assert.Single(s.Http.Calls);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("notion-id:notion-secret")), token.Headers!["Authorization"]);
        var body = JsonDocument.Parse(token.JsonBody!).RootElement;
        Assert.Equal(("authorization_code", "code-1"), (body.Str("grant_type"), body.Str("code")));
        Assert.Equal("https://api.example.test/api/v1/integrations/oauth/callback/Notion", body.Str("redirect_uri"));

        var row = await s.Db.IntegrationConnections.SingleAsync();
        Assert.Equal((s.Owner, "Notion: Acme"), (row.OwnerId, row.Label));
        Assert.DoesNotContain("ntn-access", row.EncryptedSecret);
        Assert.DoesNotContain("ntn-refresh", row.SettingsJson);
        Assert.Equal("ntn-access", s.Protector.Unprotect(row.EncryptedSecret));

        var dto = (await s.Integrations.ListAsync(ApiBase, default)).Single(d => d.Kind == IntegrationKind.Notion);
        Assert.True(dto.Connected && dto.OauthConfigured && dto.OauthConnected && dto.NeedsTarget);
        Assert.DoesNotContain("ntn-", JsonSerializer.Serialize(await s.Integrations.ListAsync(ApiBase, default)));
        Assert.Contains("Choose which Notion database", (await Assert.ThrowsAsync<ConflictException>(() => s.Integrations.PushAsync(IntegrationKind.Notion, new([Guid.NewGuid()]), default))).Message);

        var found = Assert.Single(await s.Integrations.TargetsAsync(IntegrationKind.Notion, default));
        Assert.Equal(("1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6", "Leads"), (found.Target, found.Name));
        Assert.Equal("database", JsonDocument.Parse(s.Http.Calls[^1].JsonBody!).RootElement.GetProperty("filter").Str("value"));

        var chosen = await s.Integrations.SetTargetAsync(IntegrationKind.Notion, new(found.Target, null), ApiBase, default);
        Assert.Equal(("Notion: Leads", false, true), (chosen.Label, chosen.NeedsTarget, chosen.OauthConnected));
        var settings = JsonDocument.Parse((await s.Db.IntegrationConnections.SingleAsync()).SettingsJson).RootElement;
        Assert.Equal("1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6", settings.Str("databaseId"));
        Assert.Equal("ntn-refresh", s.Protector.Unprotect(settings.GetProperty("oauth").Str("refresh")!));
    }

    [Fact]
    public async Task Slack_keeps_the_webhook_address_exactly_like_a_pasted_one()
    {
        var s = Create();
        Assert.EndsWith("?connected=Slack", await ConnectAsync(s, IntegrationKind.Slack));
        var exchange = s.Http.Calls[0];
        Assert.Equal("https://slack.com/api/oauth.v2.access", exchange.Url);
        Assert.Equal("code-1", exchange.Form!["code"]);
        Assert.False(exchange.Form.ContainsKey("client_secret"));
        // The welcome message went to the channel the person picked.
        Assert.Equal("https://hooks.slack.com/services/T1/B1/hooksecret", s.Http.Calls[1].Url);

        var row = await s.Db.IntegrationConnections.SingleAsync();
        Assert.Equal("https://hooks.slack.com/services/T1/B1/hooksecret", s.Protector.Unprotect(row.EncryptedSecret));
        Assert.Equal("Slack #leads in Acme", row.Label);
        Assert.Equal("sent,leads,digest,replies", row.Events);
        Assert.DoesNotContain("xoxb", row.SettingsJson);
        Assert.NotNull(row.LastUsedAt);
    }

    [Fact]
    public async Task Slack_refusing_the_code_connects_nothing()
    {
        var s = Create(answer: r => r.Url.Contains("oauth.v2.access", StringComparison.Ordinal) ? new(200, """{"ok":false,"error":"invalid_code"}""") : new(200, "{}"));
        Assert.Equal($"{Web}/connectors?error=failed&service=Slack", await ConnectAsync(s, IntegrationKind.Slack));
        Assert.Empty(await s.Db.IntegrationConnections.ToListAsync());
    }

    [Fact]
    public async Task HubSpot_refreshes_an_expiring_token_before_sending_and_asks_to_reconnect_when_refused()
    {
        var s = Create();
        Assert.EndsWith("?connected=HubSpot", await ConnectAsync(s, IntegrationKind.HubSpot));
        var exchange = s.Http.Calls[0];
        Assert.Equal(("hub-id", "hub-secret"), (exchange.Form!["client_id"], exchange.Form["client_secret"]));
        // The account check after sign-in uses the new access token.
        Assert.Equal("hub-access", s.Http.Calls[1].Bearer);
        var row = await s.Db.IntegrationConnections.SingleAsync();
        Assert.Equal("HubSpot account 42", row.Label);

        // Still fresh: no refresh.
        Assert.Equal("hub-access", await s.Oauth.AccessTokenAsync(row, default));
        // 29 minutes later it is within the margin: refreshed first, with the same redirect address.
        s.Http.Calls.Clear();
        s.Clock.Now = T0.AddMinutes(29);
        Assert.Equal("hub-access-2", await s.Oauth.AccessTokenAsync(row, default));
        var refresh = Assert.Single(s.Http.Calls);
        Assert.Equal(("refresh_token", "hub-refresh"), (refresh.Form!["grant_type"], refresh.Form["refresh_token"]));
        Assert.Equal("https://api.example.test/api/v1/integrations/oauth/callback/HubSpot", refresh.Form["redirect_uri"]);
        Assert.Equal("hub-access-2", s.Protector.Unprotect((await s.Db.IntegrationConnections.SingleAsync()).EncryptedSecret));

        // A refused refresh token: the connection says so, and it is a 409 asking to connect again.
        var settings = System.Text.Json.Nodes.JsonNode.Parse(row.SettingsJson)!.AsObject();
        settings["oauth"]!["refresh"] = s.Protector.Protect("revoked");
        row.RotateSecret(row.EncryptedSecret, settings.ToJsonString());
        s.Clock.Now = T0.AddHours(2);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => s.Oauth.AccessTokenAsync(row, default));
        Assert.Contains("Connect HubSpot again", ex.Message);
        Assert.Contains("Connect HubSpot again", (await s.Db.IntegrationConnections.SingleAsync()).LastError);
    }

    [Fact]
    public async Task Airtable_sends_the_pkce_verifier_and_lists_tables_in_editable_bases()
    {
        var s = Create(answer: r => r.Url switch
        {
            "https://api.airtable.com/v0/meta/bases" => new(200, """{"bases":[{"id":"appAbCdEfGh123456","name":"CRM","permissionLevel":"create"},{"id":"appReadOnly1234567","name":"Old","permissionLevel":"read"}]}"""),
            "https://api.airtable.com/v0/meta/bases/appAbCdEfGh123456/tables" => new(200, """{"tables":[{"id":"tblZyXwVuT9876543","name":"Leads","primaryFieldId":"fld1","fields":[{"id":"fld1","name":"Name","type":"singleLineText"}]}]}"""),
            _ => Services(r)
        });
        var start = Query(s.Oauth.Begin(IntegrationKind.Airtable, ApiBase).Url);
        Assert.EndsWith("?connected=Airtable", await s.Oauth.CompleteAsync(IntegrationKind.Airtable, "code-1", start["state"], null, ApiBase, default));

        var exchange = s.Http.Calls[0];
        var verifier = exchange.Form!["code_verifier"];
        Assert.Equal(start["code_challenge"], TokenProtector.Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        // No client secret configured: the id goes in the body and no Basic header is sent.
        Assert.Equal("air-id", exchange.Form["client_id"]);
        Assert.False(exchange.Headers!.ContainsKey("Authorization"));

        var table = Assert.Single(await s.Integrations.TargetsAsync(IntegrationKind.Airtable, default));
        Assert.Equal(("appAbCdEfGh123456", "tblZyXwVuT9876543", "Leads", "CRM"), (table.Target, table.Table, table.Name, table.Group));
        var chosen = await s.Integrations.SetTargetAsync(IntegrationKind.Airtable, new(table.Target, table.Table), ApiBase, default);
        Assert.Equal(("Airtable: Leads", false), (chosen.Label, chosen.NeedsTarget));
    }

    [Fact]
    public async Task The_connection_belongs_to_the_person_who_started_the_sign_in()
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var alice = Create(db: db);
        var state = Query(alice.Oauth.Begin(IntegrationKind.Notion, ApiBase).Url)["state"];
        // The callback has no signed-in user; whoever's request finishes it, the sealed state names Alice.
        var bob = Create(db: db);
        await bob.Oauth.CompleteAsync(IntegrationKind.Notion, "code-1", state, null, ApiBase, default);

        Assert.Equal(alice.Owner, (await db.IntegrationConnections.SingleAsync()).OwnerId);
        Assert.False((await bob.Integrations.ListAsync(ApiBase, default)).Single(d => d.Kind == IntegrationKind.Notion).Connected);
        await Assert.ThrowsAsync<NotFoundException>(() => bob.Integrations.TargetsAsync(IntegrationKind.Notion, default));
        await Assert.ThrowsAsync<NotFoundException>(() => bob.Integrations.SetTargetAsync(IntegrationKind.Notion, new("1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6", null), ApiBase, default));
    }

    [Fact]
    public async Task A_pasted_token_connection_is_used_as_it_is()
    {
        var s = Create();
        var row = new IntegrationConnection(s.Owner, IntegrationKind.HubSpot, "HubSpot account 1", s.Protector.Protect("pat-1"), """{"portalId":"1"}""", "", T0);
        s.Clock.Now = T0.AddDays(30);
        Assert.Equal("pat-1", await s.Oauth.AccessTokenAsync(row, default));
        Assert.Empty(s.Http.Calls);
        Assert.Equal((false, false), ConnectorOauthService.Flags(row.SettingsJson));
    }
}

internal static class JsonTestExtensions
{
    public static string? Str(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
