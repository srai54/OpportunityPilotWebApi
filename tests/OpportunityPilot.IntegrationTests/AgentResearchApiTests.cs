using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpportunityPilot.IntegrationTests;

/// <summary>The desktop agent as a research source and as the applier of shortlisted jobs.</summary>
public class AgentResearchApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private static readonly object Criteria = new
    {
        keywords = new[] { ".NET developer" },
        requiredSkills = new[] { "C#", ".NET" },
        locations = new[] { "Pune" }
    };

    private static object Posting(string id, string title = "Senior .NET Developer") => new
    {
        externalId = id,
        url = $"https://www.linkedin.com/jobs/view/{id}/",
        title,
        company = "Acme",
        location = "Pune",
        description = "C# and .NET microservices with Docker. 3-5 years."
    };

    private async Task<HttpClient> AgentFor(HttpClient user)
    {
        var key = (await (await user.PostAsJsonAsync("/api/v1/agent-keys", new { name = "Laptop" })).Json(HttpStatusCode.Created)).Str("key");
        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Agent-Key", key);
        return agent;
    }

    [Fact]
    public async Task Status_says_whether_the_agent_is_running_and_what_waits_for_it()
    {
        var user = factory.ClientFor("agent-status@example.test");
        var before = await user.GetJson("/api/v1/agent-keys/status");
        Assert.False(before.GetProperty("connected").GetBoolean());
        Assert.False(before.GetProperty("online").GetBoolean());

        var agent = await AgentFor(user);
        var notYet = await user.GetJson("/api/v1/agent-keys/status");
        Assert.True(notYet.GetProperty("connected").GetBoolean());
        Assert.False(notYet.GetProperty("online").GetBoolean());   // a key that never called in is not a running agent

        await agent.GetJson("/api/v1/agent/campaigns");
        var running = await user.GetJson("/api/v1/agent-keys/status");
        Assert.True(running.GetProperty("online").GetBoolean());
        Assert.Equal(0, running.Int("waiting"));
        Assert.Empty(running.GetProperty("recent").EnumerateArray());
    }

    [Fact]
    public async Task Agent_postings_become_opportunities_and_a_shortlisted_job_is_applied_once()
    {
        var user = factory.ClientFor("agent-flow@example.test");
        var agent = await AgentFor(user);
        var jobCampaign = await ResearchApi.CreateCampaignAsync(user, "Job", Criteria, "LinkedIn .NET");
        await ResearchApi.CreateCampaignAsync(user, "Customer", new { industries = new[] { "fintech" } }, "Not for the agent");

        var campaigns = (await agent.GetJson("/api/v1/agent/campaigns")).EnumerateArray().ToList();
        var listed = Assert.Single(campaigns);
        Assert.Equal(jobCampaign.Id(), listed.Id());
        Assert.Equal("Job", listed.Str("mode"));
        Assert.Equal(".NET developer", listed.GetProperty("criteria").GetProperty("keywords")[0].GetString());

        var delivered = await (await agent.PostAsJsonAsync($"/api/v1/agent/campaigns/{jobCampaign.Id()}/postings", new
        {
            platform = "LinkedIn",
            items = new[] { Posting("4012345678"), Posting("4012345678", "Senior .NET Developer (updated)"), Posting("4099999999", "Java Lead") },
            queueResearch = true
        })).Json(HttpStatusCode.OK);
        Assert.Equal(2, delivered.Int("accepted"));
        var jobId = delivered.GetProperty("jobId").GetGuid();
        var sourceId = delivered.GetProperty("sourceId").GetGuid();

        await factory.RunResearchAsync();
        Assert.Equal("Completed", (await ResearchApi.JobAsync(user, jobId)).Str("state"));

        var items = await ResearchApi.OpportunitiesAsync(user, jobCampaign.Id());
        Assert.Equal(2, items.Count);
        var dotnet = items.Single(i => i.Str("title") == "Senior .NET Developer (updated)");
        Assert.Equal("LinkedIn", dotnet.Str("platform"));
        Assert.Equal("Qualified", dotnet.Str("outcome"));
        Assert.Equal("https://www.linkedin.com/jobs/view/4012345678/", dotnet.Str("applyUrl"));

        // Delivering the same posting again updates the one Agent source row instead of adding another.
        var again = await (await agent.PostAsJsonAsync($"/api/v1/agent/campaigns/{jobCampaign.Id()}/postings",
            new { platform = "LinkedIn", items = new[] { Posting("4012345678") }, queueResearch = false })).Json(HttpStatusCode.OK);
        Assert.Equal(sourceId, again.GetProperty("sourceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("jobId").ValueKind);
        var agentSource = (await user.GetJson($"/api/v1/campaigns/{jobCampaign.Id()}/sources")).EnumerateArray().Single();
        Assert.Equal("Agent", agentSource.Str("kind"));
        Assert.Equal("LinkedIn", agentSource.Str("platform"));
        Assert.Equal(2, agentSource.Int("itemCount"));

        Assert.Empty((await agent.GetJson("/api/v1/agent/shortlist?platform=LinkedIn")).EnumerateArray());
        await (await user.PatchAsJsonAsync($"/api/v1/opportunities/{dotnet.Id()}/status", new { status = "Shortlisted" })).Json(HttpStatusCode.OK);

        var draft = await (await user.PostAsJsonAsync($"/api/v1/opportunities/{dotnet.Id()}/drafts",
            new { channel = "CoverNote" })).Json(HttpStatusCode.Created);
        Assert.Equal("Draft", draft.Str("state"));
        Assert.Equal("Template", draft.Str("source"));
        Assert.Contains("Senior .NET Developer", draft.Str("body"));
        Assert.Contains("Fictional test profile", draft.Str("body"));
        Assert.False(draft.GetProperty("sendReady").GetBoolean());

        var shortlist = (await agent.GetJson("/api/v1/agent/shortlist?platform=LinkedIn")).EnumerateArray().ToList();
        var entry = Assert.Single(shortlist);
        Assert.Equal(dotnet.Id(), entry.GetProperty("opportunityId").GetGuid());
        Assert.Equal(jobCampaign.Id(), entry.GetProperty("campaignId").GetGuid());
        Assert.Equal("4012345678", entry.Str("externalId"));
        Assert.Equal("LinkedIn", entry.Str("platform"));
        Assert.Equal("Acme", entry.Str("organization"));
        Assert.Equal("https://www.linkedin.com/jobs/view/4012345678/", entry.Str("url"));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("coverNote").ValueKind);

        var withPlaceholders = await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = draft.Int("version") });
        Assert.Equal(HttpStatusCode.BadRequest, withPlaceholders.StatusCode);
        var filled = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new
        {
            recipient = (string?)null,
            subject = (string?)null,
            body = System.Text.RegularExpressions.Regex.Replace(draft.Str("body")!, @"\[[^\]]+\]", "filled in"),
            expectedVersion = draft.Int("version")
        })).Json(HttpStatusCode.OK);
        var approved = await (await user.PostAsJsonAsync($"/api/v1/drafts/{draft.Id()}/approve", new { version = filled.Int("version") }))
            .Json(HttpStatusCode.OK);
        Assert.Equal("Approved", approved.Str("state"));
        Assert.False(approved.GetProperty("sendReady").GetBoolean());
        entry = Assert.Single((await agent.GetJson("/api/v1/agent/shortlist?platform=LinkedIn")).EnumerateArray().ToList());
        Assert.Equal(approved.Str("body"), entry.Str("coverNote"));

        var edited = await (await user.PutAsJsonAsync($"/api/v1/drafts/{draft.Id()}", new
        {
            recipient = (string?)null,
            subject = (string?)null,
            body = approved.Str("body") + "\nReviewed by the candidate.",
            expectedVersion = approved.Int("version")
        })).Json(HttpStatusCode.OK);
        Assert.Equal("Draft", edited.Str("state"));
        entry = Assert.Single((await agent.GetJson("/api/v1/agent/shortlist?platform=LinkedIn")).EnumerateArray().ToList());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("coverNote").ValueKind);
        Assert.Empty((await agent.GetJson("/api/v1/agent/shortlist?platform=Naukri")).EnumerateArray());

        var report = await agent.PostAsJsonAsync("/api/v1/applications/report", new
        {
            items = new[]
            {
                new
                {
                    platform = "LinkedIn", externalJobId = "4012345678", jobUrl = "https://www.linkedin.com/jobs/view/4012345678/",
                    title = "Senior .NET Developer", company = "Acme", location = "Pune", status = "Applied", detail = "Easy Apply",
                    occurredAt = DateTime.UtcNow, opportunityId = dotnet.Id()
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        var applied = await user.GetJson($"/api/v1/opportunities/{dotnet.Id()}");
        Assert.Equal("Applied", applied.Str("status"));
        Assert.Contains(applied.GetProperty("activities").EnumerateArray(), a => a.Str("kind") == "Applied");
        Assert.Empty((await agent.GetJson("/api/v1/agent/shortlist?platform=LinkedIn")).EnumerateArray());
        Assert.Equal(1, (await user.GetJson("/api/v1/applications")).Int("total"));

        // A report naming a job the user never shortlisted is recorded, but does not move that opportunity.
        var java = items.Single(i => i.Str("title") == "Java Lead");
        var unlisted = await agent.PostAsJsonAsync("/api/v1/applications/report", new
        {
            items = new[]
            {
                new
                {
                    platform = "LinkedIn", externalJobId = "4099999999", jobUrl = "https://www.linkedin.com/jobs/view/4099999999/",
                    title = "Java Lead", company = "Acme", location = "Pune", status = "Applied", detail = "Easy Apply",
                    occurredAt = DateTime.UtcNow, opportunityId = java.Id()
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, unlisted.StatusCode);
        Assert.Equal("New", (await user.GetJson($"/api/v1/opportunities/{java.Id()}")).Str("status"));
        Assert.Equal(2, (await user.GetJson("/api/v1/applications")).Int("total"));

        // Research again: the applied opportunity stays applied.
        await ResearchApi.QueueAsync(user, jobCampaign.Id());
        await factory.RunResearchAsync();
        Assert.Equal("Applied", (await user.GetJson($"/api/v1/opportunities/{dotnet.Id()}")).Str("status"));
    }

    [Fact]
    public async Task Agent_keys_and_user_tokens_stay_on_their_own_endpoints()
    {
        var user = factory.ClientFor("agent-schemes@example.test");
        var agent = await AgentFor(user);
        var campaign = await ResearchApi.CreateCampaignAsync(user, "Job", Criteria);

        foreach (var url in new[] { "/api/v1/campaigns", $"/api/v1/campaigns/{campaign.Id()}", $"/api/v1/campaigns/{campaign.Id()}/opportunities", "/api/v1/overview" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id()}/sources", new { kind = "Paste", text = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/agent/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/agent/shortlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.PostAsJsonAsync($"/api/v1/agent/campaigns/{campaign.Id()}/postings",
            new { platform = "LinkedIn", items = new[] { Posting("1") }, queueResearch = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/agent/campaigns")).StatusCode);
    }

    [Fact]
    public async Task Agents_only_see_and_feed_their_owners_job_campaigns()
    {
        var alice = factory.ClientFor("agent-alice@example.test");
        var bob = factory.ClientFor("agent-bob@example.test");
        var aliceCampaign = await ResearchApi.CreateCampaignAsync(alice, "Job", Criteria);
        var aliceCustomer = await ResearchApi.CreateCampaignAsync(alice, "Customer", new { });
        var bobAgent = await AgentFor(bob);
        var aliceAgent = await AgentFor(alice);

        Assert.Empty((await bobAgent.GetJson("/api/v1/agent/campaigns")).EnumerateArray());
        var foreign = await bobAgent.PostAsJsonAsync($"/api/v1/agent/campaigns/{aliceCampaign.Id()}/postings",
            new { platform = "LinkedIn", items = new[] { Posting("1") }, queueResearch = true });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var customer = await aliceAgent.PostAsJsonAsync($"/api/v1/agent/campaigns/{aliceCustomer.Id()}/postings",
            new { platform = "LinkedIn", items = new[] { Posting("1") }, queueResearch = false });
        Assert.Equal(HttpStatusCode.BadRequest, customer.StatusCode);

        var invalid = await aliceAgent.PostAsJsonAsync($"/api/v1/agent/campaigns/{aliceCampaign.Id()}/postings",
            new { platform = "Other", items = new[] { new { externalId = "", url = "javascript:alert(1)", title = "", company = "" } }, queueResearch = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("platform", body);
        Assert.Contains("items[0].url", body);

        Assert.Empty((await alice.GetJson($"/api/v1/campaigns/{aliceCampaign.Id()}/sources")).EnumerateArray());
    }
}
