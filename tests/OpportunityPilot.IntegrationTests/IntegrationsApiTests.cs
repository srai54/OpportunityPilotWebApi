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
        public Task<ConnectorResponse> SendAsync(ConnectorRequest r, CancellationToken ct)
        {
            Calls.Add(r);
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
}
