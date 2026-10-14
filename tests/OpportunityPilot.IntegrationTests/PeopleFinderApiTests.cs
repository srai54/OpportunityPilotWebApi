using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpportunityPilot.Application.Integrations;

namespace OpportunityPilot.IntegrationTests;

/// <summary>REACH2: people lookups need a connected Hunter/Apollo key, stay owner-scoped, and never return the key.</summary>
public class PeopleFinderApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private sealed class FakeHunter : IConnectorHttp
    {
        public List<ConnectorRequest> Calls { get; } = [];
        public Task<ConnectorResponse> SendAsync(ConnectorRequest r, CancellationToken ct)
        {
            Calls.Add(r);
            var good = r.Headers is not null && r.Headers.TryGetValue("X-API-KEY", out var k) && k == "hunter-good-key";
            return Task.FromResult(!good ? new ConnectorResponse(401, """{"errors":[{"id":"authentication_failed","code":401,"details":"No user found for the API key supplied"}]}""")
                : r.Url.StartsWith("https://api.hunter.io/v2/account", StringComparison.Ordinal) ? new(200, """{"data":{"email":"me@agency.example","plan_name":"Free"}}""")
                : new(200, """{"data":{"domain":"northwind.example","emails":[{"value":"asha@northwind.example","type":"personal","confidence":91,"sources":[{"uri":"https://a.example"}],"first_name":"Asha","last_name":"Rao","position":"CTO"}]}}"""));
        }
    }

    private WebApplicationFactory<Program> App(FakeHunter fake) => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IConnectorHttp>(fake)));

    [Fact]
    public async Task Finding_people_needs_a_connected_key_is_owner_scoped_and_never_returns_the_key()
    {
        var fake = new FakeHunter();
        await using var app = App(fake);
        var user = PostgresApiFactory.ClientFor(app, "people@example.test");
        var other = PostgresApiFactory.ClientFor(app, "people-other@example.test");

        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "payroll" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), "Northwind Traders\nWebsite: https://northwind.example\nLocation: London\nNorthwind needs payroll software.");
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(app.Services);
        var id = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();

        var notConnected = await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/people/find", new { source = "Hunter" });
        Assert.Equal(HttpStatusCode.Conflict, notConnected.StatusCode);
        Assert.Contains("Connect Hunter in Connectors first", await notConnected.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsJsonAsync("/api/v1/integrations/Hunter", new { token = "hunter-bad-key" })).StatusCode);
        var connected = await (await user.PutAsJsonAsync("/api/v1/integrations/Hunter", new { token = "hunter-good-key" })).Json(HttpStatusCode.OK);
        Assert.Equal("Hunter Free plan (me@agency.example)", connected.Str("label"));
        Assert.DoesNotContain("hunter-good-key", await user.GetStringAsync("/api/v1/integrations"));

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/opportunities/{id}/people/find", new { source = "Hunter" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/people/find", new { source = "HubSpot" })).StatusCode);

        var found = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/people/find", new { source = "Hunter" })).Json(HttpStatusCode.OK);
        Assert.Equal("northwind.example", found.Str("domain"));
        var person = found.GetProperty("people")[0];
        Assert.Equal(("asha@northwind.example", "Hunter"), (person.Str("email"), person.Str("source")));
        Assert.Contains("Third-party data", person.Str("note"));
        Assert.All(fake.Calls, c => Assert.DoesNotContain("hunter-good-key", c.Url));

        var detail = await user.GetStringAsync($"/api/v1/opportunities/{id}");
        Assert.Contains("Looked up 1 person at northwind.example with Hunter", detail);

        var reveal = await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/people/reveal", new { source = "Apollo", personId = "67bdafd0c3a4c50001bbd7c2" });
        Assert.Equal(HttpStatusCode.Conflict, reveal.StatusCode);   // Apollo is not connected
    }
}
