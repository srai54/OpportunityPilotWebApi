using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>S13/S5/P6: one execution per approved content, a receipt before anything counts as sent or placed.</summary>
public class ProviderGatewayApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Company = """
        Northwind Integrations
        Website: https://northwind.example
        Location: London
        Northwind implements Dynamics 365 and is hiring .NET developers.
        """;

    private static async Task<(HttpClient User, Guid OpportunityId)> ShortlistedCompanyAsync(PostgresApiFactory factory, string who)
    {
        var user = PostgresApiFactory.ClientFor(factory, who);
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company);
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(factory.Services);
        var id = Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id();
        await (await user.PatchAsJsonAsync($"/api/v1/opportunities/{id}/status", new { status = "Shortlisted" })).Json(HttpStatusCode.OK);
        return (user, id);
    }

    private static async Task<JsonElement> ApprovedEmailAsync(HttpClient user, Guid opportunityId, string recipient)
    {
        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{opportunityId}/drafts", new { channel = "Email", recipient })).Json(HttpStatusCode.Created);
        draft = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new
        {
            recipient, subject = "Dynamics 365 delivery", body = "Hello Northwind team, we deliver .NET integration work. Regards, Asha", expectedVersion = draft.Int("version")
        })).Json(HttpStatusCode.OK);
        return await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_approved_email_is_sent_once_and_only_a_receipt_marks_it_sent()
    {
        var (user, opportunityId) = await ShortlistedCompanyAsync(factory, "gateway-email@example.test");
        var draft = await ApprovedEmailAsync(user, opportunityId, "ops@northwind.example");

        var first = await (await user.PostAsJsonAsync($"/api/v1/executions/drafts/{draft.Id()}", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Equal("AwaitingManualConfirmation", first.Str("state"));
        Assert.Equal("Manual", first.Str("provider"));
        var again = await (await user.PostAsJsonAsync($"/api/v1/executions/drafts/{draft.Id()}", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Equal(first.Id(), again.Id());
        Assert.True(again.GetProperty("reused").GetBoolean());

        // Not sent until confirmed.
        Assert.Equal("Approved", (await user.GetJson($"/api/v1/opportunities/{opportunityId}/drafts")).EnumerateArray().Single().Str("state"));
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync($"/api/v1/executions/{first.Id()}/confirm", new { receipt = " " })).StatusCode);

        var done = await (await user.PostAsJsonAsync($"/api/v1/executions/{first.Id()}/confirm", new { receipt = "Gmail sent item 'Dynamics 365 delivery' 9 Oct" })).Json(HttpStatusCode.OK);
        Assert.Equal("Succeeded", done.Str("state"));
        Assert.Equal("Sent", (await user.GetJson($"/api/v1/opportunities/{opportunityId}/drafts")).EnumerateArray().Single().Str("state"));
        Assert.Equal("Sent", (await user.GetJson("/api/v1/drafts?take=50")).GetProperty("items").EnumerateArray().Single().Str("state"));  // the inbox list agrees
        Assert.Equal("Contacted", (await user.GetJson($"/api/v1/opportunities/{opportunityId}")).Str("status"));
        // Recording the send schedules one follow-up reminder, so nothing goes quiet.
        var followUps = (await user.GetJson($"/api/v1/opportunities/{opportunityId}/next-actions")).EnumerateArray().ToList();
        Assert.Single(followUps, f => f.Str("kind") == "FollowUp" && f.Str("state") == "Open");

        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsJsonAsync($"/api/v1/executions/{first.Id()}/confirm", new { receipt = "twice" })).StatusCode);
        var edit = await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new { recipient = "ops@northwind.example", subject = "x", body = "changed", expectedVersion = draft.Int("version") });
        Assert.False(edit.IsSuccessStatusCode);
        Assert.Single((await user.GetJson($"/api/v1/executions?subjectId={draft.Id()}")).EnumerateArray());
    }

    [Fact]
    public async Task Unapproved_or_suppressed_drafts_are_refused_and_a_failure_allows_a_new_attempt()
    {
        var (user, opportunityId) = await ShortlistedCompanyAsync(factory, "gateway-guards@example.test");
        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{opportunityId}/drafts", new { channel = "LinkedInMessage", recipient = "Ms Rao" })).Json(HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync($"/api/v1/executions/drafts/{draft.Id()}", new { version = draft.Int("version") })).StatusCode);

        var approved = await ApprovedEmailAsync(user, opportunityId, "b@northwind.example");
        var started = await (await user.PostAsJsonAsync($"/api/v1/executions/drafts/{approved.Id()}", new { version = approved.Int("version") })).Json(HttpStatusCode.OK);
        var failed = await (await user.PostAsJsonAsync($"/api/v1/executions/{started.Id()}/fail", new { reason = "Mailbox bounced" })).Json(HttpStatusCode.OK);
        Assert.Equal("Failed", failed.Str("state"));
        var retry = await (await user.PostAsJsonAsync($"/api/v1/executions/drafts/{approved.Id()}", new { version = approved.Int("version") })).Json(HttpStatusCode.OK);
        Assert.NotEqual(started.Id(), retry.Id());

        await (await user.PostAsJsonAsync("/api/v1/suppressions", new { recipient = "b@northwind.example", reason = "asked" })).Json(HttpStatusCode.Created);
        await (await user.PostAsJsonAsync($"/api/v1/executions/{retry.Id()}/fail", new { reason = "Stopped" })).Json(HttpStatusCode.OK);
        var blocked = await user.PostAsJsonAsync($"/api/v1/executions/drafts/{approved.Id()}", new { version = approved.Int("version") });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("suppression", await blocked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bid_is_placed_only_through_the_gateway_with_a_receipt()
    {
        var user = PostgresApiFactory.ClientFor(factory, "gateway-bid@example.test");
        var project = await (await user.PostAsJsonAsync("/api/v1/sales/projects", new { source = "Manual", title = "API integration", buyer = "Acme", description = "User-provided integration brief." })).Json(HttpStatusCode.Created);
        project = await (await user.PostAsJsonAsync($"/api/v1/sales/projects/{project.Id()}/bid",
            new { amount = 1200m, currency = "USD", deliveryDays = 14, proposal = "We will build the integration in two weeks." })).Json(HttpStatusCode.OK);
        var bid = project.GetProperty("bids").EnumerateArray().Single();
        project = await (await user.PostAsJsonAsync($"/api/v1/sales/bids/{bid.Id()}/approve", new { version = bid.Int("version") })).Json(HttpStatusCode.OK);
        bid = project.GetProperty("bids").EnumerateArray().Single();

        var execution = await (await user.PostAsJsonAsync($"/api/v1/executions/bids/{bid.Id()}", new { version = bid.Int("version") })).Json(HttpStatusCode.OK);
        await (await user.PostAsJsonAsync($"/api/v1/executions/{execution.Id()}/confirm", new { receipt = "Freelancer bid #88213", providerReference = "88213" })).Json(HttpStatusCode.OK);
        var after = await user.GetJson($"/api/v1/sales/projects/{project.Id()}");
        Assert.Equal("BidPlaced", after.Str("state"));
        Assert.Equal("Placed", after.GetProperty("bids").EnumerateArray().Single().Str("state"));

        // The legacy confirm endpoint reuses the same execution instead of recording a second placement.
        await (await user.PostAsJsonAsync($"/api/v1/sales/bids/{bid.Id()}/confirm-placement", new { version = bid.Int("version"), confirmed = true })).Json(HttpStatusCode.OK);
        Assert.Single((await user.GetJson($"/api/v1/executions?subjectId={bid.Id()}")).EnumerateArray());
    }
}
