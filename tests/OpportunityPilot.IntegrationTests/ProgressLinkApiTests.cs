using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>FB17: a candidate sees only what the sales team chose to share, with no sign-in; pay and internal notes never appear; revoking stops the link at once.</summary>
public class ProgressLinkApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private async Task<(HttpClient Client, JsonElement Candidate)> SeedAsync(string user)
    {
        var client = PostgresApiFactory.ClientFor(factory, user);
        var account = await (await client.PostAsJsonAsync("/api/v1/staffing/accounts", new { name = "Acme Logistics", source = "Manual" })).Json(HttpStatusCode.Created);
        var deal = await (await client.PostAsJsonAsync($"/api/v1/staffing/accounts/{account.Id()}/deals", new { title = "3 .NET engineers", source = "Manual", estimatedValue = 90000, currency = "USD" })).Json(HttpStatusCode.Created);
        var version = deal.Int("version");
        foreach (var stage in new[] { "Qualified", "Shortlisted", "OutreachApproved", "Contacted", "Replied", "MeetingScheduled", "RequirementConfirmed" })
            version = (await (await client.PostAsJsonAsync($"/api/v1/staffing/deals/{deal.Id()}/stage", new { stage, expectedVersion = version })).Json(HttpStatusCode.OK)).Int("version");
        var candidate = await (await client.PostAsJsonAsync("/api/v1/staffing/candidates", new { name = "Priya Sharma", headline = "Senior .NET engineer", rateAmount = 45, rateCurrency = "USD", rateUnit = "Hour" })).Json(HttpStatusCode.Created);
        candidate = await (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/consent", new { consent = "Granted", shareableFields = new[] { "Name", "Skills" }, evidence = "Email", expectedVersion = candidate.Int("version") })).Json(HttpStatusCode.OK);
        var api = $"/api/v1/staffing/deals/{deal.Id()}";
        var submission = await (await client.PostAsJsonAsync($"{api}/submissions", new { candidateId = candidate.Id(), sharedFields = new[] { "Name", "Skills" } })).Json(HttpStatusCode.OK);
        submission = await (await client.PostAsJsonAsync($"{api}/submissions/{submission.Id()}/approve", new { expectedVersion = submission.Int("version") })).Json(HttpStatusCode.OK);
        submission = await (await client.PostAsJsonAsync($"{api}/submissions/{submission.Id()}/sent", new { expectedVersion = submission.Int("version"), channel = "Email", receipt = "Sent msg 1" })).Json(HttpStatusCode.OK);
        var interview = await (await client.PostAsJsonAsync($"{api}/interviews", new { submissionId = submission.Id() })).Json(HttpStatusCode.OK);
        await (await client.PostAsJsonAsync($"{api}/interviews/{interview.Id()}/schedule", new
        {
            scheduledAt = "2026-10-20T09:30:00Z", timeZone = "Asia/Kolkata", durationMinutes = 45, mode = "Video", location = "https://meet.example.test/abc",
            candidateNotes = "Tech round", internalNotes = "Client prefers Azure depth", expectedVersion = interview.Int("version")
        })).Json(HttpStatusCode.OK);
        return (client, candidate);
    }

    [Fact]
    public async Task The_link_shows_only_the_chosen_items_and_never_pay_or_internal_notes()
    {
        var (client, candidate) = await SeedAsync("progress-owner@example.test");
        var created = await (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = new[] { "Interviews" } })).Json(HttpStatusCode.OK);
        var token = created.Str("token");
        Assert.False(string.IsNullOrWhiteSpace(token));

        // No sign-in on the public page.
        var anonymous = factory.CreateClient();
        var view = await (await anonymous.GetAsync($"/api/v1/public/progress/{token}")).Json(HttpStatusCode.OK);
        Assert.Equal("Priya", view.Str("firstName"));
        var item = view.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Interview scheduled", item.Str("stage"));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("client").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("role").ValueKind);
        Assert.Equal("Tech round", item.GetProperty("interview").Str("notes"));
        var raw = view.GetRawText();
        Assert.DoesNotContain("Acme", raw);
        Assert.DoesNotContain("Azure depth", raw);
        Assert.DoesNotContain("USD", raw);
        Assert.DoesNotContain("rate", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("headline", raw, StringComparison.OrdinalIgnoreCase);

        // Choosing more shows more.
        created = await (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = new[] { "Client", "Role", "Interviews" } })).Json(HttpStatusCode.OK);
        view = await (await anonymous.GetAsync($"/api/v1/public/progress/{created.Str("token")}")).Json(HttpStatusCode.OK);
        item = view.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Acme Logistics", item.Str("client"));
        Assert.Equal("3 .NET engineers", item.Str("role"));

        // The new link replaced the old one.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/public/progress/{token}")).StatusCode);
        // The saved link never exposes its token again.
        var status = await client.GetJson($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link");
        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("token").ValueKind);
    }

    [Fact]
    public async Task Note_prep_and_suggested_roles_show_only_when_shared_and_the_candidate_can_answer()
    {
        var (client, candidate) = await SeedAsync("progress-extras@example.test");
        var account = await (await client.PostAsJsonAsync("/api/v1/staffing/accounts", new { name = "Globex", source = "Manual" })).Json(HttpStatusCode.Created);
        var role = await (await client.PostAsJsonAsync($"/api/v1/staffing/accounts/{account.Id()}/deals", new { title = "Lead React developer", source = "Manual", estimatedValue = 120000, currency = "EUR" })).Json(HttpStatusCode.Created);
        var link = await client.GetJson($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link");
        var interviewId = link.GetProperty("interviews").EnumerateArray().Single().Id();
        var prep = await (await client.PutAsJsonAsync($"/api/v1/staffing/interviews/{interviewId}/prep", new { prep = "Revise system design; Meera leads the call" })).Json(HttpStatusCode.OK);
        Assert.Equal("Revise system design; Meera leads the call", prep.Str("prep"));

        // Old-style link (no new items): nothing new appears.
        var created = await (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = new[] { "Interviews" }, note = "Hidden note", roleIds = new[] { role.Id() } })).Json(HttpStatusCode.OK);
        var anonymous = factory.CreateClient();
        var view = await (await anonymous.GetAsync($"/api/v1/public/progress/{created.Str("token")}")).Json(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("note").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("roles").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("items").EnumerateArray().Single().GetProperty("interview").GetProperty("prep").ValueKind);

        // Shared: note, prep and the suggested role (title only; never the value).
        created = await (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = new[] { "Interviews", "Note", "Prep", "Roles" }, note = "Great news: the client liked your profile." })).Json(HttpStatusCode.OK);
        var token = created.Str("token");
        view = await (await anonymous.GetAsync($"/api/v1/public/progress/{token}")).Json(HttpStatusCode.OK);
        Assert.Equal("Great news: the client liked your profile.", view.Str("note"));
        Assert.Equal("Revise system design; Meera leads the call", view.GetProperty("items").EnumerateArray().Single().GetProperty("interview").Str("prep"));
        var suggested = view.GetProperty("roles").EnumerateArray().Single();
        Assert.Equal("Lead React developer", suggested.Str("title"));
        Assert.Equal(JsonValueKind.Null, suggested.GetProperty("client").ValueKind);
        Assert.Equal("None", suggested.Str("answer"));
        Assert.DoesNotContain("120000", view.GetRawText());
        Assert.DoesNotContain("EUR", view.GetRawText());

        // The candidate answers; the sales team sees it; a bad answer or someone else's role is refused.
        var answered = await (await anonymous.PostAsJsonAsync($"/api/v1/public/progress/{token}/roles/{suggested.Id()}", new { answer = "Interested" })).Json(HttpStatusCode.OK);
        Assert.Equal("Interested", answered.Str("answer"));
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync($"/api/v1/public/progress/{token}/roles/{suggested.Id()}", new { answer = "Maybe" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync($"/api/v1/public/progress/{token}/roles/{Guid.NewGuid()}", new { answer = "Interested" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync($"/api/v1/public/progress/not-a-token/roles/{suggested.Id()}", new { answer = "Interested" })).StatusCode);
        link = await client.GetJson($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link");
        Assert.Equal("Interested", link.GetProperty("roles").EnumerateArray().Single().Str("answer"));
        Assert.Equal("Great news: the client liked your profile.", link.Str("note"));

        // Another team cannot write prep on this interview.
        var other = PostgresApiFactory.ClientFor(factory, "progress-extras-other@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/v1/staffing/interviews/{interviewId}/prep", new { prep = "x" })).StatusCode);
    }

    [Fact]
    public async Task Revoking_stops_the_link_and_nobody_else_can_make_one()
    {
        var (client, candidate) = await SeedAsync("progress-revoke@example.test");
        var created = await (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = Array.Empty<string>() })).Json(HttpStatusCode.OK);
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/public/progress/{created.Str("token")}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/public/progress/{created.Str("token")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/public/progress/not-a-real-token")).StatusCode);

        var other = PostgresApiFactory.ClientFor(factory, "progress-other@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/staffing/candidates/{candidate.Id()}/progress-link", new { show = new[] { "Pay" } })).StatusCode);
    }
}
