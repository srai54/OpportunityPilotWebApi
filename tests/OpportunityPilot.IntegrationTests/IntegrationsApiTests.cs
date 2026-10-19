using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpportunityPilot.Application.Integrations;

namespace OpportunityPilot.IntegrationTests;

/// <summary>CON1–CON6: connect checks the real service first, secrets never come back, leads go to a CRM once, and the calendar link is private.</summary>
public class IntegrationsApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private sealed class FakeServices : IConnectorHttp
    {
        public List<ConnectorRequest> Calls { get; } = [];
        public bool SlackGone { get; set; }
        /// <summary>Answers checked first; null falls through to the defaults below.</summary>
        public Func<ConnectorRequest, ConnectorResponse?> Answer { get; init; } = _ => null;
        public Task<ConnectorResponse> SendAsync(ConnectorRequest r, CancellationToken ct)
        {
            Calls.Add(r);
            if (Answer(r) is { } answer) return Task.FromResult(answer);
            return Task.FromResult(r.Url switch
            {
                var u when u.StartsWith("https://hooks.slack.com/", StringComparison.Ordinal) => SlackGone ? new ConnectorResponse(404, "no_service") : new(200, "ok"),
                var u when u.EndsWith("/account-info/v3/details", StringComparison.Ordinal) => r.Bearer == "pat-good" ? new(200, """{"portalId":42}""") : new(401, "{}"),
                var u when u.EndsWith("/search", StringComparison.Ordinal) => new(200, Calls.Count(c => c.Url.EndsWith("/crm/v3/objects/companies", StringComparison.Ordinal) && c.Method == HttpMethod.Post) > 0
                    ? """{"results":[{"id":"101"}]}""" : """{"results":[]}"""),
                var u when u.EndsWith("/crm/v3/objects/companies", StringComparison.Ordinal) && r.Method == HttpMethod.Post => new(201, """{"id":"101"}"""),
                _ => new(200, "{}")
            });
        }
    }

    private WebApplicationFactory<Program> App(FakeServices fake) => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IConnectorHttp>(fake)));

    [Fact]
    public async Task Slack_connects_only_after_a_real_test_message_and_never_returns_the_address()
    {
        var fake = new FakeServices();
        await using var app = App(fake);
        var user = PostgresApiFactory.ClientFor(app, "slack@example.test");
        const string hook = "https://hooks.slack.com/services/T0/B0/secret123";

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsJsonAsync("/api/v1/integrations/Slack", new { url = "https://evil.test/services/x" })).StatusCode);
        var connected = await (await user.PutAsJsonAsync("/api/v1/integrations/Slack", new { url = hook, events = new[] { "sent", "digest" } })).Json(HttpStatusCode.OK);
        Assert.True(connected.GetProperty("connected").GetBoolean());
        Assert.Single(fake.Calls, c => c.Url == hook);

        var listText = await user.GetStringAsync("/api/v1/integrations");
        Assert.DoesNotContain("secret123", listText);
        var test = await (await user.PostAsync("/api/v1/integrations/Slack/test", null)).Json(HttpStatusCode.OK);
        Assert.True(test.GetProperty("ok").GetBoolean());

        fake.SlackGone = true;
        var failed = await (await user.PostAsync("/api/v1/integrations/Slack/test", null)).Json(HttpStatusCode.OK);
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Contains("Make a new webhook", await user.GetStringAsync("/api/v1/integrations"));

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync("/api/v1/integrations/Slack")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync("/api/v1/integrations/Slack/test", null)).StatusCode);
    }

    [Fact]
    public async Task HubSpot_rejects_a_bad_token_and_sends_a_lead_once()
    {
        var fake = new FakeServices();
        await using var app = App(fake);
        var user = PostgresApiFactory.ClientFor(app, "hubspot@example.test");
        var other = PostgresApiFactory.ClientFor(app, "hubspot-other@example.test");

        var bad = await user.PutAsJsonAsync("/api/v1/integrations/HubSpot", new { token = "pat-bad" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await (await user.PutAsJsonAsync("/api/v1/integrations/HubSpot", new { token = "pat-good" })).Json(HttpStatusCode.OK);

        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "payroll" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), "Northwind Traders\nWebsite: https://northwind.example\nLocation: London\nNorthwind needs payroll software.");
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(app.Services);
        var id = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();

        var first = await (await user.PostAsJsonAsync("/api/v1/integrations/HubSpot/push", new { opportunityIds = new[] { id } })).Json(HttpStatusCode.OK);
        Assert.Equal("Added", first[0].Str("outcome"));
        Assert.Contains("/contacts/42/record/0-2/101", first[0].Str("link"));
        var again = await (await user.PostAsJsonAsync("/api/v1/integrations/HubSpot/push", new { opportunityIds = new[] { id } })).Json(HttpStatusCode.OK);
        Assert.Equal("AlreadyThere", again[0].Str("outcome"));

        // Someone else's lead is not found, and someone without HubSpot connected cannot push.
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/integrations/HubSpot/push", new { opportunityIds = new[] { id } })).StatusCode);
        Assert.DoesNotContain("pat-good", await user.GetStringAsync("/api/v1/integrations"));
    }

    [Fact]
    public async Task Calendar_link_is_private_and_stops_working_after_disconnect()
    {
        await using var app = App(new FakeServices());
        var user = PostgresApiFactory.ClientFor(app, "calendar@example.test");
        var feed = await (await user.PutAsJsonAsync("/api/v1/integrations/CalendarFeed", new { })).Json(HttpStatusCode.OK);
        var url = new Uri(feed.Str("feedUrl"));

        var anonymous = app.CreateClient();
        var ics = await anonymous.GetAsync(url.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, ics.StatusCode);
        Assert.StartsWith("BEGIN:VCALENDAR", await ics.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url.PathAndQuery.Replace(".ics", "x.ics"))).StatusCode);

        await user.DeleteAsync("/api/v1/integrations/CalendarFeed");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url.PathAndQuery)).StatusCode);
    }
    private WebApplicationFactory<Program> OauthApp(FakeServices fake) => factory.WithWebHostBuilder(b =>
    {
        b.ConfigureTestServices(s => s.AddSingleton<IConnectorHttp>(fake));
        b.UseSetting("Mail:TokenKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        b.UseSetting("Mail:WebBase", "https://app.example.test");
        b.UseSetting("Integrations:PublicApiBase", "https://api.example.test");
        b.UseSetting("Integrations:Notion:ClientId", "notion-id");
        b.UseSetting("Integrations:Notion:ClientSecret", "notion-secret");
    });

    [Fact]
    public async Task Notion_connects_by_sign_in_never_returns_the_token_and_only_its_owner_picks_the_database()
    {
        var fake = new FakeServices { Answer = r => r.Url switch
        {
            "https://api.notion.com/v1/oauth/token" => new(200, """{"access_token":"ntn-access-secret","refresh_token":"ntn-refresh-secret","workspace_name":"Acme"}"""),
            "https://api.notion.com/v1/search" => new(200, """{"results":[{"object":"database","id":"1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6","title":[{"plain_text":"Leads"}]}]}"""),
            var u when u.StartsWith("https://api.notion.com/v1/databases/", StringComparison.Ordinal) => new(200, """{"title":[{"plain_text":"Leads"}],"properties":{"Company":{"type":"title"}}}"""),
            _ => null
        } };
        await using var app = OauthApp(fake);
        var user = PostgresApiFactory.ClientFor(app, "oauth-notion@example.test");
        var other = PostgresApiFactory.ClientFor(app, "oauth-notion-other@example.test");

        var list = await user.GetJson("/api/v1/integrations");
        Assert.Contains(list.EnumerateArray(), k => k.Str("kind") == "Notion" && k.GetProperty("oauthConfigured").GetBoolean());
        Assert.Contains(list.EnumerateArray(), k => k.Str("kind") == "HubSpot" && !k.GetProperty("oauthConfigured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync("/api/v1/integrations/HubSpot/oauth", null)).StatusCode);

        var start = await (await user.PostAsync("/api/v1/integrations/Notion/oauth", null)).Json(HttpStatusCode.OK);
        var url = new Uri(start.Str("url"));
        Assert.Equal("api.notion.com", url.Host);
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;

        // The browser comes back without a bearer token; a tampered state connects nothing.
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var handed = await browser.GetAsync($"/api/v1/integrations/oauth/callback/Notion?code=c1&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, handed.StatusCode);
        Assert.StartsWith("https://app.example.test/connectors?oauthCode=c1&oauthState=", handed.Headers.Location!.ToString());
        var bad = await (await user.PostAsJsonAsync("/api/v1/integrations/oauth/complete/Notion", new { code = "c1", state = state[..^3] + "abc" })).Json(HttpStatusCode.OK);
        Assert.Equal("https://app.example.test/connectors?error=expired&service=Notion", bad.Str("next"));
        // Someone else cannot finish this person's sign-in.
        var stolen = await (await other.PostAsJsonAsync("/api/v1/integrations/oauth/complete/Notion", new { code = "c1", state })).Json(HttpStatusCode.OK);
        Assert.Equal("https://app.example.test/connectors?error=expired&service=Notion", stolen.Str("next"));
        var back = await (await user.PostAsJsonAsync("/api/v1/integrations/oauth/complete/Notion", new { code = "c1", state })).Json(HttpStatusCode.OK);
        Assert.Equal("https://app.example.test/connectors?connected=Notion", back.Str("next"));

        var text = await user.GetStringAsync("/api/v1/integrations");
        Assert.DoesNotContain("ntn-", text);
        Assert.Contains("\"needsTarget\":true", text);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/integrations/Notion/targets")).StatusCode);

        var targets = await user.GetJson("/api/v1/integrations/Notion/targets");
        Assert.Equal("Leads", targets[0].Str("name"));
        var chosen = await (await user.PutAsJsonAsync("/api/v1/integrations/Notion/target", new { target = targets[0].Str("target") })).Json(HttpStatusCode.OK);
        Assert.Equal("Notion: Leads", chosen.Str("label"));
        Assert.False(chosen.GetProperty("needsTarget").GetBoolean());
    }
}
