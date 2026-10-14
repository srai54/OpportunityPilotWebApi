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

/// <summary>One-click "Connect" through the managed-connection platform: start, callback, owner check, proxying, Slack, Gmail, disconnect.</summary>
public class PluginTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private const string ApiBase = "https://api.example.test";
    private const string Platform = "https://backend.composio.dev/api/v3.1/";

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }

    private static readonly MailOptions Mail = new() { TokenKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()), WebBase = "https://app.example.test" };

    private sealed record Setup(PluginService Plugins, IntegrationService Integrations, InMemoryAppDbContext Db, FakeConnectorHttp Http, IConnectorHttp Connectors, Guid Owner, TokenProtector Protector);

    /// <summary>The platform, and behind its proxy the services. <paramref name="accountOwner"/> is who the platform says connected.</summary>
    private static Func<ConnectorRequest, ConnectorResponse> Answer(Func<string> accountOwner) => r =>
    {
        if (r.Url.StartsWith(Platform + "auth_configs?", StringComparison.Ordinal)) return new(200, """{"items":[]}""");
        if (r.Url == Platform + "auth_configs") return new(201, """{"toolkit":{"slug":"x"},"auth_config":{"id":"ac_1","is_composio_managed":true}}""");
        if (r.Url == Platform + "connected_accounts/link") return new(201, """{"redirect_url":"https://connect.composio.dev/link/lk_1","connected_account_id":"ca_1","link_token":"lk_1","expires_at":"2026-10-14T10:00:00Z"}""");
        if (r.Url == Platform + "connected_accounts/ca_1" && r.Method == HttpMethod.Get) return new(200, $$"""{"id":"ca_1","status":"ACTIVE","user_id":"{{accountOwner()}}"}""");
        if (r.Url == Platform + "connected_accounts/ca_1" && r.Method == HttpMethod.Delete) return new(200, "{}");
        if (r.Url != Platform + "tools/execute/proxy") return new(404, "");
        var endpoint = JsonDocument.Parse(r.JsonBody!).RootElement.GetProperty("endpoint").GetString();
        var data = endpoint switch
        {
            "https://api.hubapi.com/account-info/v3/details" => """{"portalId":42,"uiDomain":"app-eu1.hubspot.com"}""",
            "https://slack.com/api/auth.test" => """{"ok":true,"user_id":"U1","team":"Acme"}""",
            "https://slack.com/api/chat.postMessage" => """{"ok":true}""",
            "https://gmail.googleapis.com/gmail/v1/users/me/profile" => """{"emailAddress":"me@gmail.com"}""",
            "https://gmail.googleapis.com/gmail/v1/users/me/messages/send" => """{"id":"m1"}""",
            _ => """{"results":[]}"""
        };
        return new(200, "{\"data\":" + data + ",\"status\":200,\"headers\":{}}");
    };

    private static Setup Create(string? key = "ck_test")
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = Guid.NewGuid();
        var owner = me.ToString("N");
        var raw = new FakeConnectorHttp(Answer(() => owner));
        // A key per test keeps the remembered sign-in app from leaking between tests.
        var plugin = Options.Create(new PluginOptions { ApiKey = key is null ? null : key + Guid.NewGuid() });
        var http = new PluginConnectorHttp(raw, plugin);
        var protector = new TokenProtector(Options.Create(Mail));
        var clock = new Clock(T0);
        ILeadTarget[] targets = [new HubSpotTarget(http), new NotionTarget(http), new AirtableTarget(http)];
        var oauth = new ConnectorOauthService(db, new Owner(me), clock, protector, http, targets, Options.Create(new IntegrationOptions()), Options.Create(Mail), NullLogger<ConnectorOauthService>.Instance);
        var plugins = new PluginService(db, new Owner(me), clock, protector, http, targets, plugin, Options.Create(Mail), NullLogger<PluginService>.Instance);
        var integrations = new IntegrationService(db, new Owner(me), clock, protector, http, targets, [], oauth, plugins);
        return new(plugins, integrations, db, raw, http, me, protector);
    }

    private static async Task<string> ConnectAsync(Setup s, IntegrationKind kind)
    {
        var start = await s.Plugins.BeginAsync(kind, ApiBase, default);
        Assert.Equal("https://connect.composio.dev/link/lk_1", start.Url);
        var link = JsonDocument.Parse(s.Http.Calls.Last(c => c.Url.EndsWith("connected_accounts/link", StringComparison.Ordinal)).JsonBody!).RootElement;
        var callback = link.GetProperty("callback_url").GetString()!;
        var state = Uri.UnescapeDataString(callback.Split("state=")[1]);
        return await s.Plugins.CompleteAsync(kind, state, "ca_1", "success", default);
    }

    [Fact]
    public async Task Connect_uses_the_platforms_own_sign_in_app_and_our_owner_id()
    {
        var s = Create();
        await s.Plugins.BeginAsync(IntegrationKind.Notion, ApiBase, default);
        var made = JsonDocument.Parse(s.Http.Calls.Single(c => c.Url == Platform + "auth_configs").JsonBody!).RootElement;
        Assert.Equal("notion", made.GetProperty("toolkit").GetProperty("slug").GetString());
        Assert.Equal("use_composio_managed_auth", made.GetProperty("auth_config").GetProperty("type").GetString());
        var link = JsonDocument.Parse(s.Http.Calls.Single(c => c.Url.EndsWith("connected_accounts/link", StringComparison.Ordinal)).JsonBody!).RootElement;
        Assert.Equal("ac_1", link.GetProperty("auth_config_id").GetString());
        Assert.Equal(s.Owner.ToString("N"), link.GetProperty("user_id").GetString());
        Assert.StartsWith($"{ApiBase}/api/v1/integrations/plugin/callback/Notion?state=", link.GetProperty("callback_url").GetString());
        Assert.All(s.Http.Calls, c => Assert.StartsWith("ck_test", c.Headers!["x-api-key"]));

        await s.Plugins.BeginAsync(IntegrationKind.Notion, ApiBase, default);
        Assert.Single(s.Http.Calls, c => c.Url == Platform + "auth_configs"); // remembered, not created twice
    }

    [Fact]
    public async Task Notion_comes_back_connected_storing_only_the_platform_account()
    {
        var s = Create();
        Assert.Equal("https://app.example.test/connectors?connected=Notion", await ConnectAsync(s, IntegrationKind.Notion));
        var row = await s.Db.IntegrationConnections.SingleAsync();
        Assert.Equal("plugin:ca_1", s.Protector.Unprotect(row.EncryptedSecret));
        var dto = (await s.Integrations.ListAsync(ApiBase, default)).Single(d => d.Kind == IntegrationKind.Notion);
        Assert.True(dto is { Connected: true, ViaPlugin: true, NeedsTarget: true, PluginReady: true });
        Assert.Null(dto.LastError);
    }

    [Fact]
    public async Task An_account_that_belongs_to_someone_else_is_refused()
    {
        var s = Create();
        var start = await s.Plugins.BeginAsync(IntegrationKind.Notion, ApiBase, default);
        Assert.NotNull(start);
        var other = Create(); // a state sealed for another person is accepted only if the platform says the account is theirs
        var link = JsonDocument.Parse(s.Http.Calls.Last(c => c.Url.EndsWith("connected_accounts/link", StringComparison.Ordinal)).JsonBody!).RootElement;
        var state = Uri.UnescapeDataString(link.GetProperty("callback_url").GetString()!.Split("state=")[1]);
        Assert.Contains("error=", await other.Plugins.CompleteAsync(IntegrationKind.Notion, state, "ca_1", "success", default));
        Assert.Empty(other.Db.IntegrationConnections);
        Assert.Contains("error=declined", await s.Plugins.CompleteAsync(IntegrationKind.Notion, state, "ca_1", "failed", default));
        Assert.Contains("error=expired", await s.Plugins.CompleteAsync(IntegrationKind.Notion, "forged", "ca_1", "success", default));
    }

    [Fact]
    public async Task HubSpot_calls_go_through_the_platform_proxy_with_headers_and_query()
    {
        var s = Create();
        Assert.Contains("connected=HubSpot", await ConnectAsync(s, IntegrationKind.HubSpot));
        Assert.Equal("HubSpot", (await s.Db.IntegrationConnections.SingleAsync()).Kind.ToString());

        await s.Connectors.SendAsync(new ConnectorRequest(HttpMethod.Get, "https://api.notion.com/v1/databases/d1?page_size=5&q=a%20b", Bearer: "plugin:ca_1",
            Headers: new Dictionary<string, string> { ["Notion-Version"] = "2022-06-28" }), default);
        var call = s.Http.Calls.Last();
        Assert.Equal(Platform + "tools/execute/proxy", call.Url);
        Assert.Null(call.Bearer); // the stored reference never reaches the service
        var body = JsonDocument.Parse(call.JsonBody!).RootElement;
        Assert.Equal("https://api.notion.com/v1/databases/d1", body.GetProperty("endpoint").GetString());
        Assert.Equal("GET", body.GetProperty("method").GetString());
        Assert.Equal("ca_1", body.GetProperty("connected_account_id").GetString());
        var ps = body.GetProperty("parameters").EnumerateArray().Select(p => $"{p.GetProperty("type").GetString()}:{p.GetProperty("name").GetString()}={p.GetProperty("value").GetString()}").ToList();
        Assert.Equal(["header:Notion-Version=2022-06-28", "query:page_size=5", "query:q=a b"], ps);
    }

    [Fact]
    public async Task Slack_alerts_are_a_direct_message_to_the_person_who_connected()
    {
        var s = Create();
        Assert.Contains("connected=Slack", await ConnectAsync(s, IntegrationKind.Slack));
        var row = await s.Db.IntegrationConnections.SingleAsync();
        Assert.Equal("plugin:ca_1/U1", s.Protector.Unprotect(row.EncryptedSecret));
        Assert.Equal("Slack: Acme", row.Label);
        Assert.Null(row.LastError);
        var post = s.Http.Calls.Last(c => c.JsonBody?.Contains("chat.postMessage", StringComparison.Ordinal) == true);
        var body = JsonDocument.Parse(post.JsonBody!).RootElement.GetProperty("body");
        Assert.Equal("U1", body.GetProperty("channel").GetString());
        Assert.Contains("OpportunityPilot is connected", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Gmail_becomes_a_mailbox_that_sends_through_the_platform_and_disconnects_cleanly()
    {
        var s = Create();
        Assert.Contains("connected=Gmail", await ConnectAsync(s, IntegrationKind.Gmail));
        var box = await s.Db.MailboxConnections.SingleAsync();
        Assert.Equal((MailProvider.Google, "me@gmail.com"), (box.Provider, box.EmailAddress));
        var reference = s.Protector.Unprotect(box.EncryptedRefreshToken)!;
        Assert.Equal("plugin:ca_1", reference);

        var sent = await PluginService.SendGmailAsync(s.Connectors, reference, new OutgoingMail("lead@acme.test", "Hello", "Body"), default);
        Assert.True(sent.Accepted);
        Assert.Equal("m1", sent.Reference);
        Assert.False((await PluginService.SendGmailAsync(s.Connectors, reference, new OutgoingMail("a@b.test\r\nBcc: x@y.test", "Hi", "B"), default)).Accepted);

        await s.Integrations.DisconnectAsync(IntegrationKind.Gmail, default);
        Assert.Empty(s.Db.IntegrationConnections);
        Assert.Empty(s.Db.MailboxConnections);
        Assert.Contains(s.Http.Calls, c => c.Method == HttpMethod.Delete && c.Url == Platform + "connected_accounts/ca_1");
    }

    [Fact]
    public async Task Without_the_server_key_nothing_offers_one_click_connect()
    {
        var s = Create(key: null);
        Assert.False(s.Plugins.Ready(IntegrationKind.Notion));
        Assert.All(await s.Integrations.ListAsync(ApiBase, default), d => Assert.False(d.PluginReady));
        await Assert.ThrowsAsync<ConflictException>(() => s.Plugins.BeginAsync(IntegrationKind.Notion, ApiBase, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Plugins.BeginAsync(IntegrationKind.Teams, ApiBase, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Integrations.ConnectAsync(IntegrationKind.Gmail, new(null, "x", null, null, null), ApiBase, default));
    }
}
