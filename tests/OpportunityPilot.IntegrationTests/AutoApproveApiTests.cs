using System.Net;
using System.Net.Http.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>Hands-off approval: strong matches go straight to Shortlisted with a plain activity line; nothing else changes, and only Job campaigns can use it.</summary>
public class AutoApproveApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Postings = """
        Senior .NET Engineer
        Company: Acme
        Location: Pune
        URL: https://jobs.example/acme-1
        We build APIs in C# on ASP.NET Core. 3-5 years of experience.
        """;

    [Fact]
    public async Task A_run_approves_strong_matches_by_itself_and_says_so()
    {
        var user = PostgresApiFactory.ClientFor(factory, "autoapprove@example.test");
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Job",
            new { keywords = new[] { ".NET" }, requiredSkills = new[] { "C#" }, locations = new[] { "Pune" } }, autoSuggestMinScore: 1);
        // Set hands-off approval with an edit, the way the app does.
        var updated = await (await user.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id()}", new
        {
            name = campaign.Str("name"), goal = campaign.Str("goal"), criteria = campaign.GetProperty("criteria"), expectedVersion = campaign.Int("version"), autoApproveMinScore = 1
        })).Json(HttpStatusCode.OK);
        Assert.Equal(1, updated.Int("autoApproveMinScore"));

        await ResearchApi.AddPasteAsync(user, campaign.Id(), Postings);
        var jobId = await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(factory.Services);
        Assert.Equal("Completed", (await ResearchApi.JobAsync(user, jobId)).Str("state"));

        var opportunity = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id()));
        var detail = await user.GetJson($"/api/v1/opportunities/{opportunity.Id()}");
        Assert.Equal("Shortlisted", detail.Str("status"));
        Assert.Contains(detail.GetProperty("activities").EnumerateArray(), a => a.Str("detail").StartsWith("Approved automatically", StringComparison.Ordinal));
        // Nothing is left waiting in the approval queue.
        Assert.Empty((await user.GetJson("/api/v1/approvals")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Sales_campaigns_can_turn_it_on_too_and_the_score_is_checked()
    {
        var user = PostgresApiFactory.ClientFor(factory, "autoapprove-rules@example.test");
        var profileId = await ResearchApi.CreateProfileAsync(user, "Product");
        var customer = await user.PostAsJsonAsync("/api/v1/campaigns", new { profileId, mode = "Customer", name = "C", goal = "g", criteria = new { keywords = new[] { "x" } }, autoApproveMinScore = 50 });
        Assert.Equal(HttpStatusCode.Created, customer.StatusCode); // decided 2026-10-16 (U17-13b)
        var jobProfile = await ResearchApi.CreateProfileAsync(user, "Candidate");
        var tooHigh = await user.PostAsJsonAsync("/api/v1/campaigns", new { profileId = jobProfile, mode = "Job", name = "J", goal = "g", criteria = new { keywords = new[] { "x" } }, autoApproveMinScore = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
    }
}
