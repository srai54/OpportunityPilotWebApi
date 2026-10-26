---
name: error-handling
description: How to raise, map and report errors in the OpportunityPilot API and desktop agent — which exception to throw for which situation, what HTTP status it becomes, how research and fetch failures stay "safe", and what never to do. Read before throwing, catching or mapping any error.
---

# Error handling

The envelope (ProblemDetails + `correlationId`) and status meanings are defined in
[docs/conventions.md](../../../docs/conventions.md#response-and-error-envelope). This skill is the procedure.

## Decision table — API (C#)

| Situation | Throw / return | Becomes | Real example |
|---|---|---|---|
| One or more request fields invalid | collect into `Dictionary<string, string[]>`, then `throw new RequestValidationException(errors)` | 400 `errors` | `CampaignService.CreateAsync` |
| Single-field problem | `throw new RequestValidationException(new Dictionary<string, string[]> { ["csv"] = ["CSV is empty."] })` (or a local `Invalid(...)` helper) | 400 | `ImportService.Invalid` |
| Record missing **or owned by someone else** | `throw new NotFoundException("Campaign not found.")` | 404 `detail` | `CampaignService.FindOwnedAsync` |
| Stale `expectedVersion` (checked by hand) | `throw new ConflictException("Campaign was changed elsewhere (now version 4). Reload before saving.")` | 409 | `CampaignService.UpdateAsync` |
| `DbUpdateConcurrencyException` on a user edit | catch around `SaveChangesAsync`, rethrow `ConflictException` | 409 | `OpportunityService.UpdateStatusAsync` |
| `DbUpdateException` from a unique index race | catch, rethrow `ConflictException` ("send it again") or recover | 409 | `ApplicationService.ReportAsync`, `ResearchService.QueueForOwnerAsync` (recovers by returning the active job) |
| Already done, cannot repeat | `ConflictException` | 409 | `ImportService.CommitAsync` (already committed) |
| Subject claim missing/not a GUID | `UnauthorizedAccessException` (thrown by `CurrentUser`) | 401 | `Api/Auth/CurrentUser.cs` |
| Agent key absent / unknown | `AuthenticateResult.NoResult()` / `AuthenticateResult.Fail(...)` in the handler | 401 | `AgentKeyAuthenticationHandler` |
| Domain invariant broken (caller bug) | `ArgumentException` / `ArgumentOutOfRangeException` / `InvalidOperationException` in the entity | 500, logged | `ResearchJob.Claim` |
| Outbound fetch fails | **return** `FetchResult.Fail("The site answered 404.", requests)` — never throw | source `Failed` + safe event | `SafeFetcher.FetchAsync` |
| A source cannot be read during research | record `SourceStatus.Failed` with a safe reason; the run continues and ends `CompletedWithGaps` | job state | `ResearchRunner.GatherAsync` |
| Unexpected error inside a research run | let it reach `RunNextAsync`; it logs, then `RecordFailureAsync` sets `Failed` with "reference {jobId}" | job `Failed` | `ResearchRunner.RunNextAsync` |
| Startup misconfiguration that is a security risk | `throw new InvalidOperationException(...)` at startup | process stops | `AuthSetup` (DevBypass outside Development) |
| Missing configuration that is not a security risk | `setup.DatabaseMissing(...)` / `setup.AuthMissing(...)` and fall back (demo mode) | capabilities report it | `Infrastructure/DependencyInjection.cs` |

Mapping lives in one place: `Api/Hosting/AppExceptionHandler.cs` (+ `AddProblemDetails` in `Program.cs` adding `correlationId`).
Adding a new exception type means adding a case there and a row to the table above.

## Steps for a new service method

1. Validate input first, all fields at once, with the entity's `Max…Length` constants in the messages.
2. Load through the owner-filtered query; missing → `NotFoundException` (same message whether absent or not owned).
3. Check `expectedVersion` → `ConflictException`.
4. Call the domain method (its guards are a last line of defence, not user validation).
5. Wrap `SaveChangesAsync` only where a concurrency or unique-index race is possible; translate to `ConflictException`.
6. Never catch anything else in services or controllers; `AppExceptionHandler` turns the rest into a logged 500.

## Messages

| Audience | Rule | Example |
|---|---|---|
| End user (400/404/409, `SafeError`, research events) | plain sentence, what to do next, no internals | "Add at least one source before running research." |
| Logs | structured, ids and categories; exception object passed to `LogError` | `logger.LogError(ex, "Research job {JobId} stopped on an unexpected error", job.Id)` |
| 500 body | fixed text only | "The error was logged with the correlation id below." |

## Desktop agent (TypeScript)

| Situation | Do |
|---|---|
| Only the user can fix it (logged out, security check, platform limit, 3 failures in a row) | `throw new StopRun('…exact next step…')` |
| One job cannot be handled | return an `Outcome` (`Failed` with `captureDebug`, `NeedsManual`, `Skipped`); the run continues |
| API call fails | `call()` returns `{ ok: false, reason }`; the run keeps the result locally for `sync` |
| CLI command fails | throw `Error` with a user-actionable message; `main().catch` prints it and sets exit code 1 |

## Anti-patterns

- Never return 403 or a different message for "exists but not yours" — it leaks existence. Use `NotFoundException`.
- Never put `ex.Message`, stack traces, SQL, URLs with credentials, keys or connection strings in a response, `SafeError`, `ResearchEvent` or `Activity`.
- Never throw from `IWebFetcher` for network or policy failures, and never let one bad source fail the whole run.
- Never `try/catch` in a controller, and never swallow an exception without logging or recording a safe reason.
- Never use domain `ArgumentException`s as user validation (they become 500s).
- Never return `BadRequest(...)`/`NotFound()` objects from services; services throw the three application exceptions.
- Never turn a missing optional integration (no DB, no Supabase, no Gemini key) into a startup crash; report it through `SetupState`/capabilities. Only security guards stop the process.
- In the agent, never throw for a per-job failure, and never continue past a security check.
