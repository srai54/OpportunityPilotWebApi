using System.Net;
using System.Net.Http.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>L2/L5: the order jobs and leads were found in, and the person's own activity feed.</summary>
public class OrderAndActivityApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private static string Company(string name) => $"{name}\nWebsite: https://{name.Split(' ')[0].ToLowerInvariant()}.example\nLocation: London\n{name} implements Dynamics 365 and is hiring .NET developers.";

    [Fact]
    public async Task Opportunities_carry_when_they_were_found_and_can_be_listed_oldest_or_newest_first()
    {
        var user = PostgresApiFactory.ClientFor(factory, "order@example.test");
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        foreach (var name in new[] { "Northwind Integrations", "Contoso Systems", "Fabrikam Partners" })
        {
            await ResearchApi.AddPasteAsync(user, campaign.Id(), Company(name));
            await ResearchApi.QueueAsync(user, campaign.Id());
            await PostgresApiFactory.RunResearchAsync(factory.Services);
            await Task.Delay(20);
        }

        var newest = await ResearchApi.OpportunitiesAsync(user, campaign.Id(), "?sort=found");
        var oldest = await ResearchApi.OpportunitiesAsync(user, campaign.Id(), "?sort=oldest");
        Assert.True(newest.Count >= 2);
        Assert.All(newest, o => Assert.True(o.TryGetProperty("foundAt", out var f) && f.GetDateTime() > DateTime.UnixEpoch));
        var newestDates = newest.Select(o => o.GetProperty("foundAt").GetDateTime()).ToList();
        var oldestDates = oldest.Select(o => o.GetProperty("foundAt").GetDateTime()).ToList();
        Assert.Equal(newestDates.OrderByDescending(d => d), newestDates);
        Assert.Equal(oldestDates.OrderBy(d => d), oldestDates);
        Assert.Equal(oldest.Select(o => o.Id()).Reverse(), newest.Select(o => o.Id()));
    }

    [Fact]
    public async Task My_activity_lists_only_the_callers_own_actions_newest_first()
    {
        var user = PostgresApiFactory.ClientFor(factory, "activity-a@example.test");
        var other = PostgresApiFactory.ClientFor(factory, "activity-b@example.test");
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company("Northwind Integrations"));
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(factory.Services);
        var id = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();
        await (await user.PatchAsJsonAsync($"/api/v1/opportunities/{id}/status", new { status = "Shortlisted" })).Json(HttpStatusCode.OK);

        var mine = (await user.GetJson("/api/v1/analytics/activity?workspace=Sales")).EnumerateArray().ToList();
        Assert.NotEmpty(mine);
        Assert.All(mine, x => Assert.Equal(id, x.GetProperty("opportunityId").GetGuid()));
        var dates = mine.Select(x => x.GetProperty("at").GetDateTime()).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
        Assert.Empty((await other.GetJson("/api/v1/analytics/activity?workspace=Sales")).EnumerateArray());
        // The other workspace has none of these (they are Sales leads), and a bad workspace is refused.
        Assert.Empty((await user.GetJson("/api/v1/analytics/activity?workspace=Candidate")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/analytics/activity?workspace=Nope")).StatusCode);
    }
}
