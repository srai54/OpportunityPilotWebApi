using System.Net;
using System.Net.Http.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>CON8: POST /drafts/{id}/rewrite is owner-scoped, version-checked and refuses honestly when AI is off (the default here).</summary>
public class DraftRewriteApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Company = "Northwind Integrations\nWebsite: https://northwind.example\nLocation: London\nNorthwind Integrations implements Dynamics 365 and is hiring .NET developers.";

    private async Task<System.Text.Json.JsonElement> DraftAsync(HttpClient user)
    {
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company);
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(factory.Services);
        var oppId = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();
        return await (await user.PostAsJsonAsync($"/api/v1/opportunities/{oppId}/drafts", new { channel = "Email" })).Json(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Rewrite_is_404_for_someone_elses_draft_409_on_a_stale_version_and_409_when_ai_is_off()
    {
        var alice = factory.ClientFor("rewrite-alice@example.test");
        var bob = factory.ClientFor("rewrite-bob@example.test");
        var draft = await DraftAsync(alice);
        var version = draft.Int("version");

        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/rewrite", new { instruction = "shorter", version })).StatusCode);

        var stale = await (await alice.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/rewrite", new { instruction = "shorter", version = version + 5 })).Json(HttpStatusCode.Conflict);
        Assert.Contains("changed elsewhere", stale.Str("detail"));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await alice.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/rewrite", new { instruction = "", version })).StatusCode);

        var off = await (await alice.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/rewrite", new { instruction = "shorter", version })).Json(HttpStatusCode.Conflict);
        Assert.Equal("AI rewrite is off on this server.", off.Str("detail"));

        var status = await alice.GetJson("/api/v1/ai/status");
        Assert.False(status.GetProperty("rewriteAvailable").GetBoolean());

        // Nothing was saved.
        var after = await alice.GetJson($"/api/v1/drafts/{draft.Id()}");
        Assert.Equal(version, after.Int("version"));
        Assert.Equal(draft.Str("body"), after.Str("body"));
    }
}
