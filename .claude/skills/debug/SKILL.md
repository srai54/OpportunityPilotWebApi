---
name: debug
description: How to reproduce, locate and isolate failures in OpportunityPilot — build and test failures, API errors, stuck or failed research jobs, migration problems, sign-in and CORS issues, and desktop agent failures. Lists repro commands, where every log and piece of evidence lives, and isolation steps. Read before investigating any failure.
---

# Debugging

Reproduce first, then isolate to one layer, then fix with a test that fails before the fix
([unit-test.SKILL.md](../unit-test/SKILL.md)). If the cause is an unresolved product question, log it in
[docs/open-questions.md](../../../docs/open-questions.md) instead of guessing.

## Where the evidence is

| Evidence | Location |
|---|---|
| API logs | console of `dotnet run` (Development: Information; EF SQL is hidden at Warning by `appsettings.json`). On Render: the service's Logs tab |
| Demo-mode gaps | startup warnings starting `Demo mode:`; `GET /api/v1/capabilities` → `setupRequired`, `guestSignIn`, `temporaryStorage` |
| One failing request | `X-Correlation-ID` response header = `correlationId` in the ProblemDetails body = the request's `TraceIdentifier`; send your own id (8–64 letters, digits, hyphens) in `X-Correlation-ID` to find it in logs |
| 500 details | only in the log line `Unhandled exception` (the response is generic by design) |
| Research run | `GET /api/v1/research-jobs/{id}` → `state`, `stage`, `counts`, `safeError`, latest 100 `events`; log scope `ResearchJobId`; failed jobs log "Research job {JobId} stopped on an unexpected error" |
| A source that failed | `GET /api/v1/campaigns/{id}/sources` → `status`, `safeError`, `lastFetchedAt` |
| Fetch failures | log lines "Fetch of {Host} failed: {Error}" / "returned an unreadable body" |
| Database health | `GET /health/ready` (503 names the failing check, never connection details) |
| Agent run | terminal output; `agent/.data/applications.jsonl` (local log); `npm run agent -- status` |
| Agent page failure | `agent/.data/debug/<time>-<platform>-<job>-*.png` and `.html` |

## Repro commands

| Scenario | Command |
|---|---|
| Clean build with warnings | `dotnet build --no-incremental` |
| One test class / one test | `dotnet test --filter "FullyQualifiedName~JobRulesTests"` / `--filter "FullyQualifiedName~JobRulesTests.Fail_wins_over_unknown"` |
| Tests that need no Docker | `dotnet test tests/OpportunityPilot.UnitTests` and `dotnet test tests/OpportunityPilot.IntegrationTests --filter "FullyQualifiedName~StartupGuardTests"` |
| Verbose test output | append `--logger "console;verbosity=detailed"` |
| API as in production but without setup (demo mode) | Git Bash: `ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5051 ConnectionStrings__Main= Auth__SupabaseUrl= dotnet run --project src/OpportunityPilot.Api --no-launch-profile` |
| API in Development | `dotnet run --project src/OpportunityPilot.Api`, then `curl -H "X-Dev-User: alice" http://localhost:5051/api/v1/overview` |
| Pending model changes | `dotnet ef migrations has-pending-model-changes -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context SqlServerAppDbContext` (repeat with `PostgresAppDbContext`) |
| Container as deployed | `docker build -t opportunitypilot-api .` then `docker run --rm -p 8080:8080 opportunitypilot-api` |
| Agent checks | `cd agent && npm run typecheck && npm test` |

## Isolation table — symptom → first checks

| Symptom | Check, in order |
|---|---|
| Integration tests fail at start | Docker running? (`docker info`); image `postgres:17-alpine` pullable? StartupGuardTests still pass? |
| `dotnet run` fails to connect to the database | LocalDB installed and running (`sqllocaldb info MSSQLLocalDB`); the connection string in `appsettings.Development.json`; migrations apply on startup in Development |
| 401 on every call | Development: header `X-Dev-User` sent and `Auth:DevBypass` true. Deployed accounts: token issuer equals `{Auth:SupabaseUrl}/auth/v1`, audience `authenticated`; old Supabase projects need `Auth__LegacyJwtSecret`. Guest mode: token starts `opg_`, its hash exists in `app.guest_sessions`, and it has not expired |
| 401 with an agent key | the key revoked, or the in-memory demo store restarted (keys lost); user tokens never work on `/api/v1/agent/*` or `/applications/report`, keys never work elsewhere |
| Browser CORS error | the web origin is in `Cors:AllowedOrigins` (env `Cors__AllowedOrigins__0`…); the request only uses allowed headers |
| 404 for a record that exists | it belongs to another `sub` (dev user names map to different owners) |
| 409 on save | another save bumped `Version`; reload and retry. On agent report: concurrent report or research re-score; resend |
| Data vanished | `temporaryStorage: true` → demo mode, InMemory resets on every restart/sleep |
| Job stays Queued | processor disabled (`Research:ProcessorEnabled`), API asleep on the free host, or poll errors (log "could not poll for jobs; retrying in 30s") |
| Job stays Running | the processor died; the lease (2 min) must expire before another claim; after 3 claims it is Failed |
| Job CompletedWithGaps | read each source's `safeError`; fetch reasons are user-facing (blocked address, status code, content type, size, timeout) |
| Source Skipped "NeedsManualInput" | the page has < 200 characters without JavaScript or a login: paste the content instead |
| Score looks wrong | open the opportunity: `breakdown` shows weight, value, points and reason per criterion; compare with [docs/business-rules.md](../../../docs/business-rules.md#scoring); reproduce with a `JobRulesTests` case |
| Migration error on deploy | the container exits before serving; the previous deploy keeps running. Run the same migration locally against a scratch database |
| Agent: `ReferenceError: __name is not defined` | a `page.evaluate` callback ran without the shim; the page must come from `openBrowser` or `newPreparedPage` (see [agent-adapter.SKILL.md](../agent-adapter/SKILL.md)) |
| Agent: Failed / "site layout has probably changed" | open the newest `.data/debug/*.png` and `.html`; compare selectors in `src/platforms/<site>.ts` |
| Agent: "Not syncing to the web app" | API URL/key wrong or API asleep; results stay in the local log; run `npm run agent -- sync` |

## Isolation steps

1. Reproduce with the smallest command above and capture the `correlationId` or job id.
2. Decide the layer: wrong input rejected (Application validation) → wrong state (Domain) → wrong SQL/schema (Infrastructure, compare provider) → wrong HTTP behaviour (Api auth/middleware).
3. Compare providers: if it fails only on Postgres or only in demo mode, suspect a provider difference (filtered indexes, cascades, JSON) — see [docs/db-schema.md](../../../docs/db-schema.md#provider-differences).
4. Write the failing test, fix, run the full `dotnet test`.

## Anti-patterns

- Never "fix" a failure by widening the fetch policy, disabling auth, enabling DevBypass outside Development, or skipping owner filters.
- Never debug against production data or paste connection strings, tokens or agent keys into logs, issues or chat.
- Never run `dotnet ef database drop` or `update` against anything but a scratch/local database.
- Never retry a failing integration test until it passes; find the race.
- Never delete `agent/.data/applications.jsonl` to "unstick" the agent — it is what prevents applying twice.
