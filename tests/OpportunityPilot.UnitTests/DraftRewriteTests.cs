using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Ai;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.Drafts;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Ai;
using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Profiles;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

public class DraftRewriteTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
    private const string Body = "Hello Contoso,\n\nI noticed Backend Engineer. I build .NET APIs for 5 years.\n\nWould a short conversation be useful?\n\nRegards,\nAsha Rao";

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }

    private sealed class FakeLlm(string? json, AiOutcome outcome = AiOutcome.Succeeded) : ILlmClient
    {
        public int Calls { get; private set; }
        public string? LastInput { get; private set; }
        public Task<LlmResult> GenerateJsonAsync(string operation, string system, string input, CancellationToken ct)
        {
            Calls++;
            LastInput = input;
            return Task.FromResult(new LlmResult(json, outcome, outcome == AiOutcome.Succeeded ? null : "Gemini answered 503", 1, TimeSpan.FromMilliseconds(80)));
        }
    }

    private sealed record Setup(DraftRewriteService Service, AiService Ai, InMemoryAppDbContext Db, OutreachDraft Draft, FakeLlm Llm);

    private static async Task<Setup> Create(string? answer, bool enabled = true, string? key = "test-key", int dailyLimit = 5)
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var profile = new Profile(owner, ProfileType.Candidate, "P", """{"fullName":"Asha Rao","phone":"98765 43210","summary":"I build .NET APIs for 5 years."}""", true, T0);
        var campaign = new Campaign(owner, profile.Id, OpportunityMode.Job, "C", null, "{}", "{}", 25, T0);
        var opportunity = new Opportunity(owner, campaign.Id, OpportunityMode.Job, "job:Other:1", T0);
        var facts = JsonSerializer.Serialize(new[]
        {
            new FactRow("organization", "Organization", "Contoso", Guid.NewGuid().ToString(), false),
            new FactRow("salary", "Salary guess", "Fabrikam pays 40 LPA", null, true)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        opportunity.ApplyResearch("Backend Engineer", "Contoso", null, null, null, null, null, null, 80, 80, FilterOutcome.Qualified, null,
            "[]", facts, "[]", 0, Guid.NewGuid(), T0);
        var claims = JsonSerializer.Serialize(new[] { new DraftClaimDto("I build .NET APIs for 5 years.", "Profile v1", null) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var draft = new OutreachDraft(owner, opportunity.Id, DraftChannel.Email, "jobs@contoso.example", true, "Backend Engineer", Body, DraftSource.Template, claims, T0);
        db.AddRange(profile, campaign, opportunity, draft);
        await db.SaveChangesAsync();
        var llm = new FakeLlm(answer);
        var ai = new AiService(db, new Owner(owner), llm, Options.Create(new FeatureOptions { GeminiEnabled = enabled }),
            Options.Create(new AiOptions { GeminiApiKey = key, DailyCallLimit = dailyLimit }), TimeProvider.System);
        return new(new DraftRewriteService(db, new Owner(owner), llm, ai), ai, db, draft, llm);
    }

    private static string Answer(string subject, string body) => JsonSerializer.Serialize(new { subject, body });

    [Fact]
    public async Task A_rewrite_is_returned_as_a_suggestion_and_nothing_is_saved()
    {
        const string shorter = "Hello Contoso,\n\nI build .NET APIs for 5 years and saw the Backend Engineer role. Could we talk?\n\nRegards,\nAsha Rao";
        var s = await Create(Answer("Backend Engineer", shorter));

        var result = await s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 1), default);

        Assert.Equal(shorter, result.Body);
        Assert.Equal("Backend Engineer", result.Subject);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, s.Draft.Version);
        Assert.Equal(Body, s.Draft.Body);
        Assert.Equal(AiOutcome.Succeeded, Assert.Single(s.Db.AiUsages).Outcome);
        // The model sees the draft, the profile facts it uses, the stated opportunity facts and the sign-off; nothing else.
        Assert.Contains("I build .NET APIs for 5 years.", s.Llm.LastInput);
        Assert.Contains("Contoso", s.Llm.LastInput);
        Assert.Contains("Asha Rao", s.Llm.LastInput);
        Assert.DoesNotContain("98765", s.Llm.LastInput);
        Assert.DoesNotContain("Fabrikam", s.Llm.LastInput);
        Assert.DoesNotContain("jobs@contoso.example", s.Llm.LastInput);
    }

    [Fact]
    public async Task A_rewrite_that_adds_a_link_is_dropped_with_a_conflict()
    {
        var s = await Create(Answer("Backend Engineer", Body + "\nPortfolio: https://asha.example/work"));

        var error = await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("friendlier", 1), default));

        Assert.StartsWith("The rewrite added facts that are not in your profile", error.Message);
        Assert.Equal(AiOutcome.InvalidResponse, Assert.Single(s.Db.AiUsages).Outcome);
        Assert.Equal(Body, s.Draft.Body);
    }

    [Fact]
    public async Task A_rewrite_that_adds_an_email_address_is_dropped()
    {
        var s = await Create(Answer("Backend Engineer", Body + "\nWrite to asha@private.example"));
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("more formal", 1), default));
    }

    [Fact]
    public async Task New_numbers_and_names_are_flagged_as_warnings()
    {
        var s = await Create(Answer("Backend Engineer", Body.Replace("5 years", "10 years at Microsoft")));

        var result = await s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("Mention my experience", 1), default);

        Assert.Contains(result.Warnings, w => w.Contains("number") && w.EndsWith("10"));
        Assert.Contains(result.Warnings, w => w.Contains("name") && w.EndsWith("Microsoft"));
        Assert.DoesNotContain(result.Warnings, w => w.EndsWith(": 5") || w.EndsWith("Asha") || w.EndsWith("Regards"));
    }

    [Theory]
    [InlineData(false, "test-key")]
    [InlineData(true, null)]
    public async Task When_ai_is_off_nothing_is_called_and_no_fake_rewrite_is_returned(bool enabled, string? key)
    {
        var s = await Create(Answer("x", "y"), enabled, key);

        var error = await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 1), default));

        Assert.Equal("AI rewrite is off on this server.", error.Message);
        Assert.Equal(0, s.Llm.Calls);
        Assert.Empty(s.Db.AiUsages);
        Assert.False((await s.Ai.StatusAsync(default)).RewriteAvailable);
    }

    [Fact]
    public async Task The_daily_ai_budget_is_shared_and_a_spent_budget_is_refused()
    {
        var s = await Create(Answer("Backend Engineer", Body), dailyLimit: 1);
        Assert.True((await s.Ai.StatusAsync(default)).RewriteAvailable);
        await s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 1), default);

        var error = await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 1), default));

        Assert.StartsWith("Daily AI limit reached", error.Message);
        Assert.Equal(1, s.Llm.Calls);
        Assert.False((await s.Ai.StatusAsync(default)).RewriteAvailable);
    }

    [Fact]
    public async Task An_approved_draft_is_refused_until_its_approval_is_revoked()
    {
        var s = await Create(Answer("Backend Engineer", Body));
        s.Draft.Approve(1, T0);
        await s.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 1), default));

        Assert.Contains("Revoke the approval", error.Message);
        Assert.Equal(0, s.Llm.Calls);
    }

    [Fact]
    public async Task A_stale_version_conflicts_and_a_missing_instruction_is_invalid()
    {
        var s = await Create(Answer("Backend Engineer", Body));
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 2), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest(" ", 1), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest(new string('a', 201), 1), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RewriteAsync(Guid.NewGuid(), new RewriteDraftRequest("shorter", 1), default));
        Assert.Equal(0, s.Llm.Calls);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_rewrite_is_refused_and_recorded_as_invalid()
    {
        var s = await Create("not json");
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.RewriteAsync(s.Draft.Id, new RewriteDraftRequest("shorter", 1), default));
        Assert.Equal(AiOutcome.InvalidResponse, Assert.Single(s.Db.AiUsages).Outcome);
    }
}
