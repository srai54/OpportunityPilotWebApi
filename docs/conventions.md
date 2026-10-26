# Conventions

How code in this repository is written. Follow the surrounding code when this file is silent. How to throw and map
errors step by step: [.claude/skills/error-handling/SKILL.md](../.claude/skills/error-handling/SKILL.md); how to write tests:
[.claude/skills/unit-test/SKILL.md](../.claude/skills/unit-test/SKILL.md).

## Naming

| Thing | Convention | Example |
|---|---|---|
| Namespaces | file-scoped, equal to the folder path | `OpportunityPilot.Application.Research.Rules` |
| Feature folder | singular or plural noun, the same name in every layer | `Campaigns/`, `Research/`, `Agents/` |
| Service | `<Feature>Service : I<Feature>Service`, one per feature, registered scoped by its interface | `CampaignService : ICampaignService` |
| Repository | `I<Feature>Repository` in Application, `<Feature>Repository` in `Infrastructure/Persistence/Repositories` | `ICampaignRepository` |
| Controller | `<Feature>Controller`, route `api/v1/<kebab-case>` | `AgentKeysController` → `api/v1/agent-keys` |
| Route ids | constrained `{id:guid}`, `{campaignId:guid}` | |
| DTOs | `<Thing>Dto`; inputs `<Verb><Thing>Request`; all in `<Feature>Dtos.cs` beside the service | `CreateCampaignRequest`, `CampaignSummaryDto` |
| Async methods | `…Async`, last parameter `CancellationToken ct` | `ListAsync(Guid campaignId, CancellationToken ct)` |
| Constants | PascalCase `const` on the type that owns the rule | `Campaign.MaxNameLength`, `ImportService.MaxRows` |
| Tables | snake_case plural; columns PascalCase | `research_jobs.CampaignId` |
| JSON | camelCase (System.Text.Json web defaults); enums as strings | `"state": "CompletedWithGaps"` |
| Validation keys | camelCase path of the field | `name`, `criteria.workModes`, `weights.experience`, `items[3].jobUrl` |
| Spelling | the code uses British spelling in names and messages (`Normalise`, `neutralised`) alongside `Organization`; keep whatever the file already uses | |

## C# style

| Rule | Detail |
|---|---|
| Target | .NET 10, `Nullable` and `ImplicitUsings` enabled; the build must stay at 0 warnings |
| Primary constructors | for services, controllers, handlers, hosted services and exceptions |
| `sealed` | services, controllers, DTO records, exceptions, handlers and provider DbContexts |
| Records | every DTO and request is a `sealed record` with positional parameters |
| Domain entities | non-sealed classes; private parameterless constructor for EF; a public constructor that validates and sets `Id = Guid.NewGuid()`; private setters; behaviour as methods (`ChangeStatus`, `Claim`); length limits as `const` |
| Domain invariants | `ArgumentException` / `ArgumentOutOfRangeException` / `InvalidOperationException`: these are programming errors (500). Services validate user input first |
| Owned queries | repository methods take `Guid ownerId` and filter by it; the service passes `user.OwnerId` and owns `FindOwnedAsync`, which throws `NotFoundException`. Unlayered services still use a private `Owned` property |
| Persistence | Application code talks to an `I<Feature>Repository` (layered modules, see Layering) or `IAppDbContext` (the rest) only; no raw SQL, no provider-specific calls, so SqlServer, Postgres and InMemory all work |
| Time | inject `TimeProvider clock` and use `clock.GetUtcNow().UtcDateTime`; domain methods take a `DateTime utcNow` argument. Never `DateTime.UtcNow` in Domain or Application |
| Collections | collection expressions (`[]`, `[..x]`), `IReadOnlyList<T>` in DTOs |
| Formatting | 4 spaces, Allman braces, expression-bodied members for one-liners; most lines stay under ~150 characters (generated regexes are longer). No `.editorconfig` or formatter is configured |

## Layering

Target shape for every module: **Controller → `I<Feature>Service` → `I<Feature>Repository` → EF Core**.

| Layer | Holds | Must not |
|---|---|---|
| Controller (`Api/Controllers`) | routing, binding, status codes; depends on `I<Feature>Service` only | query data or hold rules |
| Service (`Application/<Feature>/<Feature>Service.cs`) | validation, rules, versioning/conflicts, DTO mapping, owner id from `ICurrentUser` | build EF queries or touch `IAppDbContext` |
| Repository interface (`Application/<Feature>/I<Feature>Repository.cs`) | the queries and writes the service needs, in domain types or DTO projections | expose `IQueryable` |
| Repository (`Infrastructure/Persistence/Repositories/<Feature>Repository.cs`) | EF Core queries on the scoped `AppDbContext`, registered in `Infrastructure/DependencyInjection.AddRepositories` | hold business rules |

Repositories share the request's `AppDbContext`, so `SaveChangesAsync` on any repository commits everything the request
staged (for example a `UserEventLog` row); `DbUpdateConcurrencyException` is still caught in the service and mapped to
`ConflictException`. Layered so far: **Campaigns, Opportunities, Profiles**. Other modules still use `IAppDbContext` from the
service and move over one at a time, with no change to routes, shapes or status codes.

## Requests and validation

1. Controllers are thin: bind, call one service method, return. No try/catch, no business logic.
2. Services collect every field problem into a `Dictionary<string, string[]>`, then throw one `RequestValidationException`.
3. Limits come from the entity constants so the API, the database column and the message agree.
4. Messages are plain sentences for end users, ending with a full stop, never exception text or internal names.
5. Paging: `take` is clamped (not rejected) to 1–200, `skip` to ≥0; lists return `{ total, items }`.

## Response and error envelope

Errors are RFC 7807 ProblemDetails. Every error body also carries `correlationId` (the value of the `X-Correlation-ID`
response header, which echoes a safe inbound id of 8–64 letters, digits or hyphens, or a new one) and ASP.NET Core's `traceId`.

| Field | Present |
|---|---|
| `type`, `title`, `status` | always |
| `detail` | 404 and 409 from services (the exception message) |
| `errors` | 400 validation: `{ "<field>": ["message"] }` |
| `correlationId`, `traceId` | always |

| Situation | Status |
|---|---|
| Field validation (service or model binding) | 400 |
| Missing or invalid credentials, wrong scheme for the endpoint | 401 |
| Record missing **or owned by someone else** | 404 (never 403: existence is not leaked) |
| Stale `expectedVersion`, concurrent save, already committed import | 409 |
| Body over the endpoint's size limit | 413 |
| Rate limit | 429 |
| Anything unexpected | 500 with a generic detail; logged with the exception |

Success bodies are the DTO itself (no wrapper). Creates return 201 (with `Location` for profiles and campaigns),
queued work 202, deletes 204.

## Logging

- Structured placeholders (`{JobId}`), never string interpolation in log calls.
- Log hosts, ids and error categories; never URLs with query strings, request bodies, tokens, keys or connection strings.
- Only unexpected errors are logged at Error. Research runs add a `ResearchJobId` scope.

## Comments

- `/// <summary>` on public types and non-obvious members, saying **why** (the rule or risk), not what the code does.
- Short inline comments for non-obvious decisions (e.g. why a FK is Restrict). No commented-out code, no TODO without an OQ id.

## Tests

| Rule | Detail |
|---|---|
| Framework | xUnit 2 with `Assert` only; no mocking library: hand-written stubs as private sealed nested classes |
| Class names | `<Subject>Tests` (unit) and `<Feature>ApiTests` (integration) |
| Method names | snake_case sentence stating the behaviour: `Applied_is_never_downgraded_by_a_later_report` |
| Time | fixed `T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)` style constants, never the wall clock in assertions |
| Integration users | a unique `X-Dev-User` per test (`"<case>@example.test"`), because the database is shared across a test class |

## Desktop agent (TypeScript, `agent/`)

| Rule | Detail |
|---|---|
| Modules | ESM; relative imports **with** the `.ts` extension; `import type` for types (`verbatimModuleSyntax`) |
| Style | 2 spaces, no semicolons, single quotes, trailing commas; strict TypeScript, no unused locals or parameters |
| Shape | plain functions and interfaces; platform adapters are factory functions returning a `PlatformAdapter` |
| Errors | `StopRun` ends the whole run (login, security check, limits); a per-job problem becomes an `Outcome` (`Failed`, `NeedsManual`, `Skipped`), never an exception that stops the run |
| API calls | return `{ ok: true, data }` or `{ ok: false, reason }`; never throw for HTTP failures |
| Messages | written for a non-developer: what happened and the exact command to run next |
| Tests | Vitest; pure logic in `test/*.test.ts`, browser flows against `test/mock/server.ts` |
