using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpportunityPilot.IntegrationTests;

public class OverviewApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private record OverviewResponse(int Profiles, int DraftsApproved, int DraftsSent, int FollowUpsOpen);

    [Fact]
    public async Task Capabilities_are_readable_before_sign_in_and_contain_no_secrets()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(body).RootElement.GetProperty("items");
        Assert.Contains(items.EnumerateArray(), i => i.GetProperty("key").GetString() == "database");
        Assert.DoesNotContain("ApiKey", body);
        Assert.DoesNotContain("Password", body);
    }

    [Fact]
    public async Task Overview_requires_authentication()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Overview_counts_only_the_callers_profiles()
    {
        var carol = factory.ClientFor("carol@example.test");
        var dave = factory.ClientFor("dave@example.test");

        foreach (var name in new[] { "Carol one", "Carol two" })
        {
            var created = await carol.PostAsJsonAsync("/api/v1/profiles",
                new { type = "Services", name, data = new { offer = "Design reviews" }, confirmed = false });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        Assert.Equal(2, (await carol.GetFromJsonAsync<OverviewResponse>("/api/v1/overview"))!.Profiles);
        var daveView = (await dave.GetFromJsonAsync<OverviewResponse>("/api/v1/overview"))!;
        Assert.Equal(0, daveView.Profiles);
        Assert.Equal(0, daveView.DraftsApproved + daveView.DraftsSent + daveView.FollowUpsOpen);
    }
}
