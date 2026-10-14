using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>JDC1 (the people around a lead) and LI1 (LinkedIn notes sent by the desktop agent) over the real API and Postgres.</summary>
public class CircleApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Company = """
        Northwind Integrations
        Website: https://northwind.example
        Location: London
        Northwind implements Dynamics 365 and is hiring .NET developers.
        """;

    private const string Note = "Hi Asha, I saw Northwind is hiring .NET developers. I build .NET integrations for Dynamics 365. I'd like to connect. Ravi";

    private static async Task<(HttpClient User, Guid OpportunityId)> LeadAsync(PostgresApiFactory factory, string who)
    {
        var user = PostgresApiFactory.ClientFor(factory, who);
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Customer", new { keywords = new[] { "Dynamics 365" }, locations = new[] { "London" } });
        await ResearchApi.AddPasteAsync(user, campaign.Id(), Company);
        await ResearchApi.QueueAsync(user, campaign.Id());
        await PostgresApiFactory.RunResearchAsync(factory.Services);
        return (user, Assert.Single(await ResearchApi.OpportunitiesAsync(user, campaign.Id())).Id());
    }

    private async Task<HttpClient> AgentFor(HttpClient user, string[]? scopes = null)
    {
        var key = (await (await user.PostAsJsonAsync("/api/v1/agent-keys", new { name = "Laptop", scopes })).Json(HttpStatusCode.Created)).Str("key");
        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Agent-Key", key);
        return agent;
    }

    private static object Person(string slug, string relation = "Recruiter", int? degree = 2) =>
        new { name = $"Person {slug}", title = "Talent Acquisition at Northwind", profileUrl = $"https://www.linkedin.com/in/{slug}/?trk=search", relation, degree };

    [Fact]
    public async Task A_requested_circle_is_mapped_by_the_agent_and_stays_with_its_owner()
    {
        var (user, id) = await LeadAsync(factory, "circle-owner@example.test");
        var agent = await AgentFor(user);
        Assert.Equal("NotRequested", (await user.GetJson($"/api/v1/opportunities/{id}/circle")).Str("status"));
        Assert.Empty((await agent.GetJson("/api/v1/agent/circle-requests")).EnumerateArray());

        var waiting = await (await user.PostAsync($"/api/v1/opportunities/{id}/circle/request", null)).Json(HttpStatusCode.OK);
        Assert.Equal("Waiting", waiting.Str("status"));
        var request = Assert.Single((await agent.GetJson("/api/v1/agent/circle-requests")).EnumerateArray().ToList());
        Assert.Equal(id, request.GetProperty("opportunityId").GetGuid());
        Assert.Equal("Northwind Integrations", request.Str("organization"));

        var delivered = await (await agent.PostAsJsonAsync("/api/v1/agent/circle", new
        {
            opportunityId = id,
            people = new[] { Person("asha-rao"), Person("asha-rao", degree: 1), Person("ravi-menon", "HiringManager", 3) }
        })).Json(HttpStatusCode.OK);
        Assert.Equal((2, 2, 2), (delivered.Int("accepted"), delivered.Int("added"), delivered.Int("total")));

        var ready = await user.GetJson($"/api/v1/opportunities/{id}/circle");
        Assert.Equal("Ready", ready.Str("status"));
        var people = ready.GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(["https://www.linkedin.com/in/asha-rao", "https://www.linkedin.com/in/ravi-menon"], people.Select(p => p.Str("profileUrl")));
        Assert.Equal("LinkedInAgent", people[0].Str("source"));
        Assert.Equal(1, people[0].Int("degree"));
        Assert.Empty((await agent.GetJson("/api/v1/agent/circle-requests")).EnumerateArray());

        // By hand: added, a duplicate refused, a wrong link refused, removed.
        var manual = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/circle",
            new { name = "Meera", profileUrl = "in.linkedin.com/in/meera", relation = "Team" })).Json(HttpStatusCode.Created);
        Assert.Equal("Manual", manual.Str("source"));
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/circle",
            new { name = "Meera", profileUrl = "https://www.linkedin.com/in/meera", relation = "Team" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/circle",
            new { name = "Meera", profileUrl = "https://example.com/meera", relation = "Team" })).StatusCode);

        // Another owner sees nothing and changes nothing.
        var stranger = PostgresApiFactory.ClientFor(factory, "circle-stranger@example.test");
        var strangerAgent = await AgentFor(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/opportunities/{id}/circle")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/v1/opportunities/{id}/circle/request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/v1/opportunities/{id}/circle/{manual.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerAgent.PostAsJsonAsync("/api/v1/agent/circle", new { opportunityId = id, people = new[] { Person("x") } })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/opportunities/{id}/circle/{manual.Id()}")).StatusCode);
        Assert.Equal(2, (await user.GetJson($"/api/v1/opportunities/{id}/circle")).GetProperty("people").GetArrayLength());
    }

    [Fact]
    public async Task People_endpoints_need_an_agent_key_with_the_People_scope()
    {
        var (user, _) = await LeadAsync(factory, "circle-scopes@example.test");
        var researchOnly = await AgentFor(user, ["Research"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await researchOnly.GetAsync("/api/v1/agent/circle-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await researchOnly.GetAsync("/api/v1/agent/linkedin-outbox")).StatusCode);
        var people = await AgentFor(user, ["People"]);
        Assert.Equal(HttpStatusCode.OK, (await people.GetAsync("/api/v1/agent/linkedin-outbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await people.GetAsync("/api/v1/agent/campaigns")).StatusCode);
        // A user token never works on agent endpoints, and a default key has every scope including People.
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/agent/circle-requests")).StatusCode);
        var created = await (await user.PostAsJsonAsync("/api/v1/agent-keys", new { name = "Default" })).Json(HttpStatusCode.Created);
        Assert.Contains("People", created.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task An_approved_LinkedIn_note_to_a_profile_link_is_handed_to_the_agent_and_sent_at_most_once()
    {
        var (user, id) = await LeadAsync(factory, "linkedin-outbox@example.test");
        var agent = await AgentFor(user);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/drafts",
            new { channel = "LinkedInMessage", recipient = "https://www.linkedin.com/company/northwind" })).StatusCode);
        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/drafts",
            new { channel = "LinkedInMessage", recipient = "linkedin.com/in/asha-rao/?trk=x" })).Json(HttpStatusCode.Created);
        Assert.Equal("https://www.linkedin.com/in/asha-rao", draft.Str("recipient"));
        draft = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}",
            new { recipient = draft.Str("recipient"), body = Note, expectedVersion = draft.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Empty((await agent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray());   // not approved yet

        var approved = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
        var item = Assert.Single((await agent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray().ToList());
        Assert.Equal((draft.Id(), approved.Int("version"), "https://www.linkedin.com/in/asha-rao", Note, true),
            (item.GetProperty("draftId").GetGuid(), item.Int("version"), item.Str("profileUrl"), item.Str("body"), item.GetProperty("fitsConnectionNote").GetBoolean()));

        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/start", new { version = 99 })).StatusCode);
        var started = await (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/start", new { version = approved.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Equal(("LinkedInAgent", "AwaitingManualConfirmation", false), (started.Str("provider"), started.Str("state"), started.GetProperty("reused").GetBoolean()));
        var again = await (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/start", new { version = approved.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Equal(started.Id(), again.Id());
        Assert.True(again.GetProperty("reused").GetBoolean());
        Assert.Empty((await agent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray());

        var done = await (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/confirm",
            new { receipt = "Connection request with note sent", providerReference = "connect" })).Json(HttpStatusCode.OK);
        Assert.Equal("Succeeded", done.Str("state"));
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/confirm", new { receipt = "again" })).StatusCode);
        Assert.Equal("Sent", (await user.GetJson($"/api/v1/drafts/{draft.Id()}")).Str("state"));
        Assert.Equal("Contacted", (await user.GetJson($"/api/v1/opportunities/{id}")).Str("status"));
        Assert.Single((await user.GetJson($"/api/v1/executions?subjectId={draft.Id()}")).EnumerateArray());

        // Another owner's agent cannot touch it.
        var strangerAgent = await AgentFor(PostgresApiFactory.ClientFor(factory, "linkedin-stranger@example.test"));
        Assert.Equal(HttpStatusCode.NotFound, (await strangerAgent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/start", new { version = 1 })).StatusCode);
        Assert.Empty((await strangerAgent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray());
    }

    [Fact]
    public async Task A_note_addressed_by_name_or_suppressed_never_reaches_the_outbox()
    {
        var (user, id) = await LeadAsync(factory, "linkedin-outbox-guards@example.test");
        var agent = await AgentFor(user);
        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{id}/drafts", new { channel = "LinkedInMessage", recipient = "Ms Rao" })).Json(HttpStatusCode.Created);
        draft = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new { recipient = "Ms Rao", body = Note, expectedVersion = draft.Int("version") })).Json(HttpStatusCode.OK);
        draft = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Empty((await agent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/start", new { version = draft.Int("version") })).StatusCode);

        draft = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}",
            new { recipient = "https://www.linkedin.com/in/blocked", body = Note, expectedVersion = draft.Int("version") })).Json(HttpStatusCode.OK);
        draft = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") })).Json(HttpStatusCode.OK);
        Assert.Single((await agent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray());
        await (await user.PostAsJsonAsync("/api/v1/suppressions", new { recipient = "https://www.linkedin.com/in/blocked", reason = "asked" })).Json(HttpStatusCode.Created);
        Assert.Empty((await agent.GetJson("/api/v1/agent/linkedin-outbox")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsJsonAsync($"/api/v1/agent/linkedin-outbox/{draft.Id()}/start", new { version = draft.Int("version") })).StatusCode);
    }
}
