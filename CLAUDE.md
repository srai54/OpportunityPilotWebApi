# CLAUDE.md — OpportunityPilot Web API

## 1. Project & Stack

| Item | Value |
|---|---|
| What | ASP.NET Core Web API for OpportunityPilot (research → score → shortlist → act → track), plus the local desktop agent in `agent/` |
| Runtime | .NET 10 (`net10.0`), C# with `Nullable` + `ImplicitUsings`; SDK 10.0.x |
| Architecture | Clean Architecture: Domain ← Application ← Infrastructure ← Api (`OpportunityPilot.sln` holds these 4 plus the 2 test projects) |
| ORM | EF Core 10.0.12 |
| Database — local | SQL Server LocalDB (`SqlServerAppDbContext`), migrated on startup in Development |
| Database — cloud | Supabase PostgreSQL via Npgsql 10 (`PostgresAppDbContext`); also the integration-test database (Testcontainers `postgres:17-alpine`) |
| Database — demo | EF InMemory (`InMemoryAppDbContext`) when `ConnectionStrings__Main` is unset; data resets on restart |
| Schema | everything in schema `app` (snake_case tables) |
| Auth | Supabase JWT (JWKS); Hanko session JWT (JWKS) next to it when `Auth:HankoApiUrl` is set (owner id = name-based UUID of the Hanko user id, `Auth/HankoAuth.cs`); guest tokens in demo mode; `X-Dev-User` dev bypass in Development only; `X-Agent-Key` for the desktop agent |
| Other libraries | AngleSharp 1.8.3 (HTML), Microsoft.AspNetCore.OpenApi (`/openapi/v1.json`, Development only) |
| Ports | `http://localhost:5051` (profile `http`, default); `https://localhost:7232` + 5051 (profile `https`); container listens on 8080 or `$PORT` |
| Web app | separate repo `OpportunityPilotWebapp` (Vite, `http://localhost:5173` in CORS for Development) |
| Background work | one in-process `ResearchProcessor` (BackgroundService) claiming durable `research_jobs` rows with a lease |
| Docker / hosting | multi-stage `Dockerfile` (sdk:10.0 → aspnet:10.0, non-root, runs `--migrate` first when `MIGRATE_ON_START=true`); `render.yaml` Blueprint (Render free plan, Singapore). Deployed API is on Render, web app on Vercel |
| Tests | xUnit 2: `tests/OpportunityPilot.UnitTests` (no I/O), `tests/OpportunityPilot.IntegrationTests` (real API + Postgres container, needs Docker) |
| Desktop agent | `agent/`: Node ≥20.19, TypeScript ~6, Playwright, tsx, Vitest |
| AI | Scoring is the rules engine (no model). LLM features through `Application/Ai/AiService.cs` with `Infrastructure/Ai/GroqLlmClient.cs` (Groq) and `GeminiLlmClient.cs` (Gemini): campaign goal → criteria, assistant (`Application/Assistant`), draft rewrite, profile improve. Voice: Groq Whisper / OpenAI whisper-1 in `Api/Controllers/VoiceController.cs`. Every AI call has a daily budget and a rules fallback; guests are blocked |

## 2. Cheat-sheet commands

Run from the repository root unless noted. dotnet-ef 10.x must be installed globally (`dotnet tool install --global dotnet-ef`).

| Task | Command |
|---|---|
| Run API (LocalDB, dev bypass) | `dotnet run --project src/OpportunityPilot.Api` |
| Run API with HTTPS | `dotnet run --project src/OpportunityPilot.Api --launch-profile https` |
| Run API in demo mode (Git Bash) | `ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5051 ConnectionStrings__Main= Auth__SupabaseUrl= dotnet run --project src/OpportunityPilot.Api --no-launch-profile` |
| Build (must be 0 warnings) | `dotnet build --no-incremental` |
| All tests (Docker running) | `dotnet test` |
| Unit tests only | `dotnet test tests/OpportunityPilot.UnitTests` |
| Integration tests (Docker) | `dotnet test tests/OpportunityPilot.IntegrationTests` |
| Integration tests without Docker | `dotnet test tests/OpportunityPilot.IntegrationTests --filter "FullyQualifiedName~StartupGuardTests"` |
| One test class | `dotnet test --filter "FullyQualifiedName~JobRulesTests"` |
| Add migration — SqlServer | `dotnet ef migrations add <Name> -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context SqlServerAppDbContext -o Persistence/Migrations/SqlServer` |
| Add migration — Postgres | `dotnet ef migrations add <Name> -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context PostgresAppDbContext -o Persistence/Migrations/Postgres` |
| Remove last unapplied migration | `dotnet ef migrations remove -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context <SqlServerAppDbContext or PostgresAppDbContext>` |
| Check model vs migrations | `dotnet ef migrations has-pending-model-changes -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context <Context>` |
| Script SQL for review | `dotnet ef migrations script <From> <To> -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context <Context>` |
| Apply to LocalDB by hand | `dotnet ef database update -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context SqlServerAppDbContext` |
| Apply in a release | `dotnet OpportunityPilot.Api.dll --migrate` (Docker does this when `MIGRATE_ON_START=true`) |
| Docker image / run | `docker build -t opportunitypilot-api .` · `docker run --rm -p 8080:8080 opportunitypilot-api` |
| Agent setup (in `agent/`) | `npm install` · `npx playwright install chromium` · `npm run agent -- init` |
| Agent commands (in `agent/`) | `npm run agent -- login <linkedin\|naukri>` · `campaigns` · `collect <platform> --campaign <id>` · `apply <platform> [--submit] [--limit N] [--headless]` · `circle linkedin [--limit N]` · `outreach linkedin [--submit] [--limit N]` · `cycle` · `watch` · `sync` · `status` |
| Agent checks (in `agent/`) | `npm run typecheck` · `npm test` (unit + e2e against the local mock) |
| Lint / format | none configured; the 0-warning build is the gate (OQ-BE-028) |

## 3. Folder blueprint

| Path | Holds |
|---|---|
| `src/OpportunityPilot.Domain/<Feature>/` | entities (`IOwned`, private setters, validating constructor, `Max…Length` constants) and one enum per file; `Common/` has `IOwned`, `OpportunityMode`, `Guard` |
| `src/OpportunityPilot.Application/<Feature>/` | `<Feature>Service.cs` + `<Feature>Dtos.cs` (+ feature helpers, e.g. `Research/Rules/`); `Abstractions/` (`IAppDbContext`, `ICurrentUser`), `Common/AppExceptions.cs`, `Configuration/` (options, `SetupState`), `DependencyInjection.cs` |
| `src/OpportunityPilot.Infrastructure/` | `Persistence/` (`AppDbContext`, `ProviderContexts`, `DesignTimeFactories`, `Repositories/` (EF `I<Feature>Repository` implementations), `Migrations/SqlServer`, `Migrations/Postgres`); `Research/` (safe fetcher, address policy, content parser); `DependencyInjection.cs` |
| `src/OpportunityPilot.Api/` | `Controllers/`, `Auth/` (schemes, guest tokens, JWKS, `CurrentUser`), `Hosting/` (exception handler, correlation id, health check, research processor), `Program.cs`, `appsettings*.json` |
| `tests/OpportunityPilot.UnitTests/` | pure tests; research ones under `Research/` |
| `tests/OpportunityPilot.IntegrationTests/` | `<Feature>ApiTests.cs` on `PostgresApiFactory`; `StartupGuardTests.cs` for production/demo startup |
| `agent/src/` | `cli.ts` (commands), `run.ts` (apply), `collect.ts`, `circle.ts` (JD circle), `outreach.ts` (LinkedIn notes), `schedule.ts`, `api.ts`, `form.ts`, `answers.ts`, `browser.ts`, `store.ts`, `config.ts`, `util.ts`, `platforms/` (`types.ts`, one adapter per site, `linkedin-people.ts` for people search and profile actions) |
| `agent/test/` | `*.test.ts` (Vitest) and `mock/server.ts` (local LinkedIn/Naukri stand-in + fake API) |
| `docs/` | reference docs (table below); `feature-contracts.md` holds the older per-slice specs |
| `.claude/skills/` | task procedures, loaded as Claude Code skills (table below) |
| `.claude/` | `settings.json` hooks and `hooks/*.mjs` guards |

New feature `Foo`, in this order:

| Step | File |
|---|---|
| 1 Domain | `Domain/Foo/Foo.cs` (+ `Domain/Foo/FooStatus.cs`) |
| 2 Persistence | `DbSet<Foo>` in `Application/Abstractions/IAppDbContext.cs` and `Infrastructure/Persistence/AppDbContext.cs`; mapping in `AppDbContext.OnModelCreating`; provider-only parts in `ProviderContexts.cs`; migrations for both providers |
| 3 Application | `Application/Foo/IFooService.cs` + `FooService.cs`, `Application/Foo/IFooRepository.cs`, `Application/Foo/FooDtos.cs`; register `AddScoped<IFooService, FooService>()` in `Application/DependencyInjection.cs` |
| 3b Repository | `Infrastructure/Persistence/Repositories/FooRepository.cs`; register in `AddRepositories` in `Infrastructure/DependencyInjection.cs` (layering: `docs/conventions.md` → Layering) |
| 4 External adapter (if any) | interface in `Application/Foo/`, implementation in `Infrastructure/Foo/`, registered in `Infrastructure/DependencyInjection.cs` |
| 5 Api | `Api/Controllers/FooController.cs` (`[ApiController]`, `[Route("api/v1/foo")]`, thin, depends on `IFooService`) |
| 6 Tests | `UnitTests/FooTests.cs`; `IntegrationTests/FooApiTests.cs` |
| 7 Docs | `docs/db-schema.md`, `docs/api-contracts.md`, `docs/business-rules.md`, lifecycle doc if it has states |

Co-location: DTOs and requests live in the feature's single `<Feature>Dtos.cs` next to its service; feature options live in
the feature folder (`Research/ResearchOptions.cs`), shared options in `Configuration/Options.cs`; exceptions only in
`Application/Common`; nothing feature-specific in `Common/`.

## 4. Path aliases

None. There are no import aliases in either stack.

| Stack | How references work |
|---|---|
| .NET projects | project references only: Domain (none) ← Application ← Infrastructure ← Api; UnitTests → Application + Infrastructure + Api (auth helpers); IntegrationTests → Api |
| .NET namespaces | file-scoped and equal to the folder path: `OpportunityPilot.<Layer>.<Folder>[.<Sub>]` (e.g. `OpportunityPilot.Application.Research.Rules`); test projects have a global `using Xunit` |
| Agent (TypeScript) | ESM with relative imports that include the `.ts` extension (`./platforms/types.ts`); `import type` for types; no `paths` in `tsconfig.json` |

## 5. Mandatory rules

| Trigger | Rule |
|---|---|
| Before throwing, catching or mapping any error | Read `.claude/skills/error-handling/SKILL.md` |
| Before writing or changing a test | Read `.claude/skills/unit-test/SKILL.md` |
| Before investigating any failure (build, test, runtime, research job, agent) | Read `.claude/skills/debug/SKILL.md` |
| Before changing an entity, `AppDbContext`, `ProviderContexts` or a migration | Read `docs/db-schema.md` and `.claude/skills/migrations/SKILL.md`; add the migration to **both** providers; update `docs/db-schema.md` in the same change |
| Before changing scoring, filters, weights, dedupe, limits, CSV/paste rules, agent rules or approval | Read `docs/business-rules.md`; update it in the same change |
| Before changing a status, state or transition | Read `docs/opportunity-lifecycle.md`; update it in the same change |
| Before adding or changing an endpoint | Read `docs/api-contracts.md` and `docs/conventions.md`; update `docs/api-contracts.md` in the same change |
| Before touching `agent/src/platforms`, `form.ts`, `answers.ts` or the mock site | Read `.claude/skills/agent-adapter/SKILL.md` |
| When something is unclear, unbuilt or a decision is needed | Read `docs/open-questions.md`; log a new `OQ-BE-###` entry instead of improvising |
| When the stack, a port, a command or a folder changes | Update this file in the same change |

Hard guardrails:

- **Owner scoping**: every query filters by `OwnerId` from `ICurrentUser` (the research runner uses the job's owner). Never accept an owner id from a request. Not found and not yours are both `NotFoundException` (404).
- **Never log or return secrets**: connection strings, JWTs, guest signing key, legacy JWT secret, Gemini key, agent keys (only the SHA-256 and the 12-character prefix are stored). User-facing `SafeError`, events and activities never contain exception text.
- **Agent keys only on agent endpoints**: the `AgentKey` scheme stays out of the fallback policy; only `POST /api/v1/applications/report` and `/api/v1/agent/*` name it. Do not let user tokens in there or agent keys anywhere else.
- **No logged-in platform automation server-side**: the API never fetches LinkedIn/Naukri, stores no passwords or cookies, bypasses no CAPTCHA or security check. Platform automation exists only in the local agent, which stops at any check.
- **Outbound HTTP for research only through `IWebFetcher`/`SafeFetcher`**. Do not make `IFetchAddressPolicy` configurable or relax it outside the test project.
- **Migrations for both providers**, never edit an applied migration, never `Database:MigrateOnStartup` outside Development, never run `database update`/`drop` against Supabase or any shared database (drop only databases named scratch).
- **Demo mode must keep working**: no raw SQL or provider-only features in Application; InMemory enforces no unique index, FK or cascade, so check duplicates and delete children in services; `StartupGuardTests` must pass.
- **Research never changes `Opportunity.Status`**; only the user (PATCH) and an Applied agent report do.
- **Time**: inject `TimeProvider`; no `DateTime.UtcNow` in Domain or Application. Store UTC only.
- **Git workflow**: Every feature or change goes on a **new branch** (`feature/`, `fix/`, `docs/`, `chore/` + name); never commit to `main`. Run this repo's checks, commit, then from the workspace root run `bash tools/pr.sh <repo> "<title>" <body-file>`: it pushes, raises a PR to `main` and **squash-merges** it (auto-merge authorised by the user *for now*; if withdrawn, pass `--no-merge` and wait), deletes the branch and pulls `main`. `main` deploys automatically, so a merge is a release.
- **Push only at the end of the session or when the user says so (rule, set by the user 2026-10-06 — replaces "always push"):** commit locally as each task finishes; a local commit deploys nothing. Do **not** push, raise or merge a PR after a task or a minor change: every push/merge to `main` deploys Vercel and Render and uses up free-tier limits. Push + PR + merge only when **all tasks of the session are done and verified** (typecheck, lint, tests, build, browser check through the local proxy against the live API), or when the user **explicitly** asks. If the session must stop early, leave the work committed locally on its branch (no push) and note the branch in `TASKS.md`. See `../CLAUDE.md`.
- **Enums are stored as strings**: do not rename a member of a stored enum without a data migration.
- **Capabilities tell the truth**: never report `Ready` for something not built and verified; missing optional config degrades to demo mode or NotConfigured, never a crash. Only security guards (DevBypass outside Development) stop startup.
- **Specs are not code**: `docs/feature-contracts.md` holds designs; `docs/api-contracts.md` lists what exists. M4–M9 items are not built.
- Do not edit `.env*`, `appsettings.*.local.json`, lock files, `agent/config.json` or `agent/.data/**`; do not write real keys or passwords into any file (hooks in `.claude/settings.json` block these).
- Do not commit or push unless asked; do not use `--no-verify` or force-push.
- Keep the build at 0 warnings and `dotnet test` green before calling work done.

### Reference table — these files are not auto-loaded; open the one that matches the task

| Domain | File |
|---|---|
| Tables, columns, indexes, provider differences, migration list | `docs/db-schema.md` |
| Endpoints, auth per endpoint, request/response shapes, status codes | `docs/api-contracts.md` |
| Validation, limits, scoring, filters, dedupe, safe fetch, CSV, agent rules, demo mode, rate limits | `docs/business-rules.md` |
| Opportunity status, filter outcome, research job states, source status, application status | `docs/opportunity-lifecycle.md` |
| Naming, C# and TypeScript style, error envelope, test naming | `docs/conventions.md` |
| Known gaps, stubs, unverified integrations, decisions needed | `docs/open-questions.md` |
| Local setup, configuration keys, Supabase, Render deployment | `docs/SETUP.md` |
| Milestone status and verification record | `docs/plans-and-status.md` |
| Why things are the way they are | `docs/DECISIONS.md` |
| Slice design specs (M2–M5; M4/M5 not built) | `docs/feature-contracts.md` |
| Error handling procedure | `.claude/skills/error-handling/SKILL.md` |
| Writing tests | `.claude/skills/unit-test/SKILL.md` |
| Debugging | `.claude/skills/debug/SKILL.md` |
| Changing the database model | `.claude/skills/migrations/SKILL.md` |
| Agent platform adapters | `.claude/skills/agent-adapter/SKILL.md` |
| Agent usage for end users | `agent/README.md` |

## AI tooling, privacy and review

- AI tooling policy (MCP, skills, hooks, permissions, secrets): `docs/AI_TOOLING.md`. No MCP server is configured; add one only with that policy.
- Privacy and data handling: `docs/PRIVACY.md`.
- Before a commit run `/verify`; a finished batch goes out with `/release` (PR template in `.github/`).
