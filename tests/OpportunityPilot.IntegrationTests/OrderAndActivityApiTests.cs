using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        // Lines about a job or lead point at it; lines about the person's own actions (a saved profile, a run) point at nothing.
        Assert.All(mine.Where(x => x.GetProperty("opportunityId").ValueKind != JsonValueKind.Null), x => Assert.Equal(id, x.GetProperty("opportunityId").GetGuid()));
        var dates = mine.Select(x => x.GetProperty("at").GetDateTime()).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
        Assert.DoesNotContain((await other.GetJson("/api/v1/analytics/activity?workspace=Sales")).EnumerateArray(), x => x.GetProperty("opportunityId").ValueKind != JsonValueKind.Null);
        // The other workspace has none of these (they are Sales leads), and a bad workspace is refused.
        Assert.DoesNotContain((await user.GetJson("/api/v1/analytics/activity?workspace=Candidate")).EnumerateArray(), x => x.GetProperty("opportunityId").ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/analytics/activity?workspace=Nope")).StatusCode);
    }

    private static async Task<(Guid CampaignId, Guid OpportunityId)> OneLeadAsync(HttpClient user)
    {
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company("Northwind Integrations"));
        return (campaign.Id(), Guid.Empty);
    }

    [Fact]
    public async Task What_the_person_does_is_recorded_with_kind_filters_and_paging_and_nobody_else_sees_it()
    {
        var user = PostgresApiFactory.ClientFor(factory, "events-a@example.test");
        var other = PostgresApiFactory.ClientFor(factory, "events-b@example.test");
        var (campaignId, _) = await OneLeadAsync(user);
        await ResearchApi.QueueAsync(user, campaignId);
        await ResearchApi.QueueAsync(user, campaignId); // a second click while queued is not a second action
        await PostgresApiFactory.RunResearchAsync(factory.Services);
        var oppId = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaignId)).Id();
        await (await user.PatchAsJsonAsync($"/api/v1/opportunities/{oppId}/status", new { status = "Shortlisted" })).Json(HttpStatusCode.OK);
        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{oppId}/drafts", new { channel = "Email", recipient = "ops@northwind.example" })).Json(HttpStatusCode.Created);
        draft = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new { recipient = "ops@northwind.example", subject = "Hello", body = "Hello Northwind team, we deliver .NET work. Regards, Asha", expectedVersion = draft.Int("version") })).Json(HttpStatusCode.OK);
        await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);

        var all = (await user.GetJson("/api/v1/analytics/activity?workspace=Sales&take=100")).EnumerateArray().ToList();
        var kinds = all.Select(x => x.Str("kind")).ToList();
        Assert.Contains("ProfileSaved", kinds);
        Assert.Equal(1, kinds.Count(k => k == "RunStarted"));
        Assert.Contains("DraftCreated", kinds);
        Assert.Contains("DraftEdited", kinds);
        Assert.Contains("DraftApproved", kinds);
        var approved = all.First(x => x.Str("kind") == "DraftApproved");
        Assert.Equal(oppId, approved.GetProperty("opportunityId").GetGuid());
        Assert.Contains("email draft", approved.Str("detail"));
        Assert.Equal("Northwind Integrations", approved.Str("organization"));
        var times = all.Select(x => x.GetProperty("at").GetDateTime()).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);

        // Filter by kind, by date and by cursor.
        var onlyDrafts = (await user.GetJson("/api/v1/analytics/activity?workspace=Sales&kinds=DraftCreated,DraftApproved")).EnumerateArray().ToList();
        Assert.All(onlyDrafts, x => Assert.Contains(x.Str("kind"), new[] { "DraftCreated", "DraftApproved" }));
        Assert.Equal(2, onlyDrafts.Count);
        Assert.Empty((await user.GetJson($"/api/v1/analytics/activity?workspace=Sales&from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"))}")).EnumerateArray());
        var newest = all[0].GetProperty("at").GetDateTime();
        var older = (await user.GetJson($"/api/v1/analytics/activity?workspace=Sales&take=100&before={Uri.EscapeDataString(newest.ToUniversalTime().ToString("O"))}")).EnumerateArray().ToList();
        Assert.True(older.Count < all.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/analytics/activity?workspace=Sales&kinds=Nonsense")).StatusCode);

        Assert.DoesNotContain((await other.GetJson("/api/v1/analytics/activity?workspace=Sales&take=100")).EnumerateArray(), x => x.Str("kind") == "DraftApproved");
    }

    [Fact]
    public async Task Exporting_includes_the_activity_and_never_a_mailbox_token_and_deleting_removes_both()
    {
        var user = PostgresApiFactory.ClientFor(factory, "events-export@example.test");
        await ResearchApi.CreateProfileAsync(user);
        var export = await (await user.GetAsync("/api/v1/account-data/export")).Content.ReadAsStringAsync();
        Assert.Contains("userEvents", export);
        Assert.Contains("mailboxes", export);
        Assert.DoesNotContain("EncryptedRefreshToken", export);
        Assert.Contains("ProfileSaved", export);
        var del = await user.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/account-data") { Content = JsonContent.Create(new { confirm = true }) });
        Assert.True(del.IsSuccessStatusCode, await del.Content.ReadAsStringAsync());
        Assert.Empty((await user.GetJson("/api/v1/analytics/activity?workspace=Sales")).EnumerateArray());
    }
}
