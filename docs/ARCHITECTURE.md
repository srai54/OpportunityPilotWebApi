# Architecture and folder map

Moved from `CLAUDE.md` on 2026-10-26 so the file loaded every session stays short. Layering rules: `docs/conventions.md` → Layering.

## Folders

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

