---
name: unit-test
description: Where and how to write tests in OpportunityPilot — xUnit unit tests, Testcontainers integration tests against the real API, demo-mode tests, and Vitest/Playwright tests for the desktop agent. Includes skeletons, stub patterns and a coverage checklist. Read before writing or changing any test.
---

# Writing tests

Naming and style rules: [docs/conventions.md](../../../docs/conventions.md#tests). Commands: `CLAUDE.md` cheat-sheet.

## Decision table — where does the test go?

| You changed | Test project / file | Harness |
|---|---|---|
| Domain entity method or invariant | `tests/OpportunityPilot.UnitTests/<Entity>Tests.cs` or `Research/ResearchDomainTests.cs` | plain `new` + fixed `T0` |
| Scoring, filters, text matching | `UnitTests/Research/JobRulesTests.cs`, `CustomerRulesTests.cs` | call `JobRules.Evaluate` / `CustomerRules.Evaluate` with a `RuleInput` |
| Weights, criteria, dedupe keys, research limits | `UnitTests/Research/CampaignSettingsTests.cs` | static methods |
| CSV, paste parsing, export cells | `UnitTests/Research/TextImportTests.cs` | static methods |
| HTML/feed parsing | `UnitTests/Research/ContentParserTests.cs` | `new ContentParser()` |
| Safe fetcher / address policy | `UnitTests/Research/SafeFetcherTests.cs` | `ScriptedHandler` (records requests) and the `resolve` hook of `SafeFetcher.CreateHandler` |
| Capabilities | `UnitTests/CapabilityServiceTests.cs` | `Options.Create(...)` + `new SetupState()` |
| An endpoint, owner scoping, 409s, wire shape | `tests/OpportunityPilot.IntegrationTests/<Feature>ApiTests.cs` | `IClassFixture<PostgresApiFactory>` (needs Docker) |
| A research run end to end | `IntegrationTests/ResearchApiTests.cs` / `ResearchJobLifecycleTests.cs` | queue via API, then `await factory.RunResearchAsync()` |
| Fetched sources over real sockets | `IntegrationTests/FetchedSourceTests.cs` | `TinyHttpServer` + a test-only `IFetchAddressPolicy` |
| Startup guards, demo mode, guest tokens | `IntegrationTests/StartupGuardTests.cs` | `ProductionFactory` with settings; **no Docker** |
| Agent pure logic (answers, options) | `agent/test/answers.test.ts` | Vitest |
| Agent browser flow | `agent/test/e2e.test.ts` | Playwright against `test/mock/server.ts`, `newPreparedPage(browser)` |

## Skeletons

Unit (pure):

```csharp
public class OpportunityStatusTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Status_changes_record_an_activity_and_unchanged_status_records_none()
    {
        var o = new Opportunity(Guid.NewGuid(), Guid.NewGuid(), OpportunityMode.Job, "job:LinkedIn:1", T0);
        Assert.NotNull(o.ChangeStatus(OpportunityStatus.Shortlisted, T0));
        Assert.Null(o.ChangeStatus(OpportunityStatus.Shortlisted, T0));
    }
}
```

Integration (real API + Postgres container):

```csharp
public class WidgetApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [Fact]
    public async Task Other_users_get_404()
    {
        var alice = factory.ClientFor("widget-alice@example.test");   // unique per test
        var bob = factory.ClientFor("widget-bob@example.test");
        var campaign = await ResearchApi.CreateCampaignAsync(alice, "Job", new { requiredSkills = new[] { "C#" } });

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/campaigns/{campaign.Id()}")).StatusCode);
    }
}
```

Agent (Vitest):

```ts
it('never guesses free-text questions no rule covers', () => {
  expect(answerFor({ label: 'Why do you want to join us?', kind: 'textarea' }, answers)).toBeUndefined()
})
```

## Patterns that already exist — reuse them

| Need | Use |
|---|---|
| Read a response and assert the status with the body in the failure message | `await response.Json(HttpStatusCode.Created)` (extension in `ResearchApi.cs`) |
| Create profile → campaign → paste source → queue | `ResearchApi.CreateCampaignAsync`, `AddPasteAsync`, `QueueAsync` |
| An agent client | create a key with the user client, then a new client with `X-Agent-Key` (`AgentResearchApiTests.AgentFor`) |
| Simulate a crashed processor | `AbandonAsync` in `ResearchJobLifecycleTests` (claims with a time in the past) |
| A guest in demo mode | `StartupGuardTests.GuestClient` |
| Pasted postings | `"Title\nCompany: Acme\nLocation: Pune\nC#\n---\nNext…"` |

## Coverage checklist for a change

- [ ] Happy path asserts the **wire shape** (camelCase names, enum strings), not just the status.
- [ ] Every new validation rule has a 400 case asserting the `errors` key.
- [ ] Owner scoping: a second user gets 404 on read, update and delete.
- [ ] Concurrency: a stale `expectedVersion` gets 409 and the body contains `correlationId`.
- [ ] Unknown is tested separately from 0 for any new scoring criterion; not-applicable redistributes weight.
- [ ] Research changes: rerun keeps user status and does not duplicate; cancel and limits still hold.
- [ ] Anything touching persistence still works in demo mode (InMemory has no unique indexes or cascades).
- [ ] Agent changes: dry run sends nothing (`mock.submissions` stays empty).

## Anti-patterns

- Never call the real internet or a real LinkedIn/Naukri page from a test; use stubs, the loopback server or the mock site.
- Never relax the production address policy for a test by configuration; register a test-only `IFetchAddressPolicy` in the test project.
- Never turn the hosted research processor on in Postgres tests (`Research:ProcessorEnabled=false`); run jobs with `RunResearchAsync` so results are deterministic.
- Never assume an empty database in integration tests: the container is shared by the whole test class. Filter by your own user.
- Never use `DateTime.Now`/`UtcNow` as an expected value or `Thread.Sleep`/`Task.Delay` to wait for work.
- Never add a mocking library or FluentAssertions; follow the existing hand-written stubs and `Assert`.
- Never use a real key, token or password in a fixture. Fake `opk_…` keys belong only under `tests/` or `agent/test/`.
- Never delete or weaken an existing assertion to make a change pass; if behaviour changes on purpose, update docs and the test together.
