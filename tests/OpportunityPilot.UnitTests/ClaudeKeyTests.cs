using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Ai;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Ai;
using OpportunityPilot.Infrastructure.Ai;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>Bring your own Claude key (2026-10-26): checked, stored encrypted, never shown; all AI of that person then runs on Claude.</summary>
public class ClaudeKeyTests
{
    private const string Key = "sk-ant-api03-test-key-0000000000000000wxyz";
    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class NoUser : ICurrentUser { public Guid OwnerId => throw new UnauthorizedAccessException(); }

    private sealed class FakeClaude(ClaudeKeyCheck check = ClaudeKeyCheck.Ok, Func<LlmResult>? answer = null) : IClaudeClient
    {
        public List<(string Key, string Model)> Calls { get; } = [];
        public Task<ClaudeKeyCheck> CheckKeyAsync(string apiKey, CancellationToken ct) => Task.FromResult(check);
        public Task<LlmResult> GenerateJsonAsync(string apiKey, string model, string operation, string system, string input, int maxTokens, CancellationToken ct)
        {
            Calls.Add((apiKey, model));
            return Task.FromResult(answer?.Invoke() ?? new LlmResult("""{"from":"claude"}""", AiOutcome.Succeeded, null, 1, TimeSpan.Zero, "Claude"));
        }
    }

    private static InMemoryAppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TokenProtector Protector() => new(Options.Create(new MailOptions { FallbackSecret = "unit-test-secret" }));

    private static ClaudeKeyService Keys(InMemoryAppDbContext db, ICurrentUser user, IClaudeClient claude) =>
        new(db, user, Protector(), claude, TimeProvider.System, NullLogger<ClaudeKeyService>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("sk-ant-admin01-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("sk-ant-api03 with spaces in it 0000000")]
    public async Task Malformed_and_admin_keys_are_refused_before_any_call(string key)
    {
        var claude = new FakeClaude();
        await Assert.ThrowsAsync<RequestValidationException>(() => Keys(NewDb(), new Owner(Guid.NewGuid()), claude).ConnectAsync(new(key, null), default));
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task A_key_Anthropic_refuses_is_not_stored()
    {
        var db = NewDb();
        await Assert.ThrowsAsync<RequestValidationException>(() => Keys(db, new Owner(Guid.NewGuid()), new FakeClaude(ClaudeKeyCheck.Rejected)).ConnectAsync(new(Key, null), default));
        Assert.Empty(await db.AiCredentials.ToListAsync());
    }

    [Fact]
    public async Task A_good_key_is_stored_encrypted_and_only_its_last_four_characters_are_shown()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var keys = Keys(db, new Owner(owner), new FakeClaude());
        var status = await keys.ConnectAsync(new(Key, "claude-sonnet-5-5"), default);
        Assert.Equal((true, "…wxyz", "claude-sonnet-5-5", false), (status.Connected, status.KeyHint, status.Model, status.ShareWithCandidates));
        var row = await db.AiCredentials.SingleAsync();
        Assert.DoesNotContain("test-key", row.EncryptedKey);
        Assert.Equal(Key, (await keys.CurrentAsync(default))!.ApiKey);
        Assert.DoesNotContain(Key, System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Candidates_can_use_the_key_only_when_the_owner_shares_it_and_removing_it_deletes_it()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var keys = Keys(db, new Owner(owner), new FakeClaude());
        await keys.ConnectAsync(new(Key, null), default);
        Assert.Null(await keys.SharedByAsync(owner, default));
        await keys.UpdateAsync(new(null, true), default);
        Assert.NotNull(await keys.SharedByAsync(owner, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => keys.UpdateAsync(new("gpt-4", null), default));
        await keys.RemoveAsync(default);
        Assert.Empty(await db.AiCredentials.ToListAsync());
        Assert.Null(await keys.CurrentAsync(default));
    }

    [Fact]
    public async Task No_signed_in_person_means_no_key_rather_than_an_error()
    {
        Assert.Null(await Keys(NewDb(), new NoUser(), new FakeClaude()).CurrentAsync(default));
    }

    [Fact]
    public async Task With_an_own_key_AI_is_available_even_when_the_server_has_none()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var keys = Keys(db, new Owner(owner), new FakeClaude());
        var ai = new AiService(db, new Owner(owner), new FakeLlm(), Options.Create(new FeatureOptions { GeminiEnabled = false }), Options.Create(new AiOptions()), TimeProvider.System, keys);
        Assert.Equal("Disabled", await ai.UnavailableReasonAsync(default));
        await keys.ConnectAsync(new(Key, null), default);
        var fresh = Keys(db, new Owner(owner), new FakeClaude());
        ai = new AiService(db, new Owner(owner), new FakeLlm(), Options.Create(new FeatureOptions { GeminiEnabled = false }), Options.Create(new AiOptions()), TimeProvider.System, fresh);
        Assert.Null(await ai.UnavailableReasonAsync(default));
        var status = await ai.StatusAsync(default);
        Assert.Equal(("Claude", AiCredential.DefaultModel, true, AiService.OwnKeyDailyLimit), (status.Provider, status.Model, status.Active, status.DailyLimit));
    }

    private sealed class FakeLlm : ILlmClient
    {
        public Task<LlmResult> GenerateJsonAsync(string operation, string system, string input, CancellationToken ct) =>
            Task.FromResult(new LlmResult("{}", AiOutcome.Succeeded, null, 1, TimeSpan.Zero));
    }

    // ---------- Routing ----------

    private sealed class StubKey(UserClaudeKey? key) : IUserClaudeKey
    {
        public Task<UserClaudeKey?> CurrentAsync(CancellationToken ct) => Task.FromResult(key);
        public Task<UserClaudeKey?> SharedByAsync(Guid ownerId, CancellationToken ct) => Task.FromResult<UserClaudeKey?>(null);
    }

    private sealed class GroqStub : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"from\":\"groq\"}"}}]}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private static (RoutingLlmClient Router, GroqStub Groq) Router(UserClaudeKey? key, IClaudeClient claude, bool serverKey = true)
    {
        var options = Options.Create(new AiOptions { GroqApiKey = serverKey ? "groq" : null });
        var stub = new GroqStub();
        var groq = new GroqLlmClient(new HttpClient(stub) { BaseAddress = new Uri("https://groq.test/") }, options);
        var gemini = new GeminiLlmClient(new HttpClient(stub) { BaseAddress = new Uri("https://gemini.test/") }, options);
        return (new RoutingLlmClient(groq, gemini, options, new StubKey(key), claude), stub);
    }

    private static readonly UserClaudeKey OwnKey = new(Guid.NewGuid(), Key, "claude-opus-5-5");

    [Fact]
    public async Task A_person_with_a_key_gets_Claude_and_others_get_the_server_provider()
    {
        var claude = new FakeClaude();
        var (router, groq) = Router(OwnKey, claude);
        var r = await router.GenerateJsonAsync("Op", "sys", "in", default);
        Assert.Equal(("Claude", 1, 0), (r.Provider, claude.Calls.Count, groq.Calls));

        var (plain, groq2) = Router(null, claude);
        var s = await plain.GenerateJsonAsync("Op", "sys", "in", default);
        Assert.Equal(("Groq", 1), (s.Provider, groq2.Calls));
    }

    [Fact]
    public async Task Claude_being_down_falls_back_but_a_refused_key_is_reported()
    {
        var down = new FakeClaude(answer: () => new LlmResult(null, AiOutcome.ProviderError, "Claude is unavailable right now (5xx)", 1, TimeSpan.Zero, "Claude"));
        var (router, groq) = Router(OwnKey, down);
        Assert.Equal(("Groq", AiOutcome.Succeeded), ((await router.GenerateJsonAsync("Op", "s", "i", default)).Provider, AiOutcome.Succeeded));
        Assert.Equal(1, groq.Calls);

        var refused = new FakeClaude(answer: () => new LlmResult(null, AiOutcome.ProviderError, ClaudeLlmClient.KeyRejectedReason, 1, TimeSpan.Zero, "Claude"));
        var (router2, groq2) = Router(OwnKey, refused);
        var r = await router2.GenerateJsonAsync("Op", "s", "i", default);
        Assert.Equal((ClaudeLlmClient.KeyRejectedReason, 0), (r.Reason, groq2.Calls));
    }

    [Theory]
    [InlineData("""{"a":1}""", """{"a":1}""")]
    [InlineData("```json\n{\"a\":1}\n```", """{"a":1}""")]
    [InlineData("Here you go: {\"a\":{\"b\":2}} done", """{"a":{"b":2}}""")]
    [InlineData("no json here", null)]
    [InlineData("{broken", null)]
    public void Json_is_taken_from_the_answer_even_with_a_fence_around_it(string text, string? expected) =>
        Assert.Equal(expected, ClaudeLlmClient.ExtractJson(text));

    // ---------- Resume tailoring ----------

    private static ResumeTailorService.Doc Resume() => new("Asha Rao", "Developer", "Builds web apps.",
        [new("Backend", "C#, .NET 8, SQL Server"), new("Frontend", "React, TypeScript")],
        [new("Payments portal", "Card payments for a bank", "6", "ASP.NET Core, React", "Built APIs"), new("HR app", "Leave tracking", "3", "Node.js", "Wrote reports")]);

    [Fact]
    public void Tailoring_keeps_the_facts_and_leaves_out_skills_the_resume_never_mentions()
    {
        const string answer = """
            {"title":"Senior .NET Developer","summary":"Backend developer focused on .NET and SQL Server.",
             "skills":[{"label":"Backend","items":"C#, .NET 8, SQL Server, Kubernetes"},{"label":"Frontend","items":"React, nodejs"}],
             "projects":[{"index":0,"responsibilities":"Designed payment APIs in ASP.NET Core\nTuned SQL Server queries"},{"index":7,"responsibilities":"x"}],
             "changes":["Put .NET first"],"name":"Someone Else"}
            """;
        var merged = ResumeTailorService.Merge(Resume(), answer)!.Value;
        Assert.Equal("Asha Rao", merged.Doc.Name);
        Assert.Equal("Senior .NET Developer", merged.Doc.Title);
        Assert.Equal("C#, .NET 8, SQL Server", merged.Doc.Skills[0].Items);
        Assert.Equal("React, nodejs", merged.Doc.Skills[1].Items);
        Assert.Equal(["Kubernetes"], merged.NotAdded);
        Assert.Equal(("Payments portal", "6", "ASP.NET Core, React"), (merged.Doc.Projects[0].Name, merged.Doc.Projects[0].TeamSize, merged.Doc.Projects[0].Technology));
        Assert.StartsWith("Designed payment APIs", merged.Doc.Projects[0].Responsibilities);
        Assert.Equal("Wrote reports", merged.Doc.Projects[1].Responsibilities);
        Assert.Equal(["Put .NET first"], merged.Changes);
    }

    [Fact]
    public void An_unusable_answer_gives_nothing_and_an_empty_one_keeps_the_original()
    {
        Assert.Null(ResumeTailorService.Merge(Resume(), "not json"));
        var same = ResumeTailorService.Merge(Resume(), "{}")!.Value;
        Assert.Equal(Resume().Title, same.Doc.Title);
        Assert.Equal(Resume().Skills, same.Doc.Skills);
    }

    [Fact]
    public void The_builder_format_is_read_back()
    {
        var doc = ResumeTailorService.Parse("""{"name":"A","title":"T","summary":"S","skills":[{"label":"L","items":"x"}],"projects":[{"name":"P","teamSize":"4"}]}""")!;
        Assert.Equal(("A", "L", "P", "4", ""), (doc.Name, doc.Skills[0].Label, doc.Projects[0].Name, doc.Projects[0].TeamSize, doc.Projects[0].Responsibilities));
        Assert.Null(ResumeTailorService.Parse("""{"title":"no name"}"""));
    }
}
