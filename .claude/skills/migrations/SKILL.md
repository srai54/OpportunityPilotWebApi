---
name: migrations
description: How to change the OpportunityPilot database model — entity and mapping changes, generating the migration for BOTH providers (SqlServer and Postgres), keeping demo mode (InMemory) working, and the review checklist before committing. Read before changing an entity, AppDbContext, ProviderContexts or any migration.
---

# Migrations (two providers)

There are two migration sets, one per provider, generated from the same model. Every model change needs a migration in
**both**, with the same name, in the same change. Schema reference: [docs/db-schema.md](../../../docs/db-schema.md) — update it
in the same commit.

## Decision table — where does the change go?

| Change | Configure in | Extra |
|---|---|---|
| New entity | `Domain/<Feature>/<Entity>.cs`; `DbSet` in `Application/Abstractions/IAppDbContext.cs` **and** `Infrastructure/Persistence/AppDbContext.cs`; `modelBuilder.Entity<>` block in `AppDbContext.OnModelCreating` | `ToTable("snake_case_plural")`, `HasKey`, `Property(x => x.Id).ValueGeneratedNever()` |
| String column | `HasMaxLength(Entity.MaxXLength)` using the entity's constant | `.IsRequired()` for non-nullable strings |
| Enum column | `.HasConversion<string>().HasMaxLength(32)` | never rename members of a stored enum without a data migration |
| JSON column | `AppDbContext`: `.IsRequired()`; `ProviderContexts.cs` → `PostgresAppDbContext`: `.HasColumnType("jsonb")` | SqlServer stays `nvarchar(max)`; default value `{}`/`[]` in the entity |
| Concurrency | `.IsConcurrencyToken()` on an int `Version` the domain increments | catch `DbUpdateConcurrencyException` in the service |
| Unique index | `AppDbContext`: `HasIndex(...).IsUnique()` | InMemory does not enforce it: add a service-side check |
| Filtered unique index | **both** provider subclasses, with provider quoting: `"[Col] IS NOT NULL"` vs `"\"Col\" IS NOT NULL"` | see `SourceItem` and `UX_research_jobs_active_campaign` |
| Foreign key | `HasOne<Parent>().WithMany().HasForeignKey(...)` | SqlServer rejects two cascade paths to one table → use `Restrict` on one (see `opportunity_evidence`); InMemory runs no cascades → delete children explicitly |
| DateTime | nothing | SqlServer reads back as UTC via `UtcDateTimeConverter`; always store UTC |

## Steps

1. Change the entity (constants, constructor validation, private setters) and the mapping.
2. Build: `dotnet build`.
3. Generate both migrations (dotnet-ef 10.x; install with `dotnet tool install --global dotnet-ef` if missing):
   - `dotnet ef migrations add <Name> -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context SqlServerAppDbContext -o Persistence/Migrations/SqlServer`
   - `dotnet ef migrations add <Name> -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context PostgresAppDbContext -o Persistence/Migrations/Postgres`
   Generation never touches a database (`DesignTimeFactories.cs` uses a placeholder connection when `ConnectionStrings__Main` is unset).
4. Review both (checklist below). To read the SQL: `dotnet ef migrations script <PreviousName> <Name> -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context PostgresAppDbContext` (and the SqlServer context).
5. Apply locally: run the API in Development (it migrates LocalDB on startup) or `dotnet ef database update -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context SqlServerAppDbContext`.
6. Postgres is applied by every integration test run (`Database:MigrateOnStartup` in `PostgresApiFactory`): run `dotnet test`.
7. Confirm nothing is pending for either context: `dotnet ef migrations has-pending-model-changes -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context <Context>`.
8. Update [docs/db-schema.md](../../../docs/db-schema.md) (table section and the migration list).

To undo an **unapplied** migration: `dotnet ef migrations remove -p src/OpportunityPilot.Infrastructure -s src/OpportunityPilot.Api --context <Context>` for each provider.

## Review checklist

- [ ] Same migration name in `Migrations/SqlServer` and `Migrations/Postgres`; both snapshots changed.
- [ ] Every table and index is in schema `app`.
- [ ] Only the intended operations: no unexpected `DropColumn`, `DropTable`, `RenameColumn` or type narrowing; data-losing steps are deliberate and documented.
- [ ] Postgres JSON columns are `jsonb`; SqlServer equivalents are `nvarchar(max)`.
- [ ] Filtered indexes present in both, with the right quoting.
- [ ] New NOT NULL columns on existing tables have a default or a data step.
- [ ] `Down` reverses `Up`.
- [ ] Demo mode still works: `dotnet test tests/OpportunityPilot.IntegrationTests --filter "FullyQualifiedName~StartupGuardTests"`.
- [ ] db-schema.md updated.

## Release

Outside Development migrations are an explicit step: `dotnet OpportunityPilot.Api.dll --migrate` (the Docker entrypoint
does this when `MIGRATE_ON_START=true`, and skips it while no connection string is set). A failed migration stops the
new container; the previous deploy keeps serving.

## Anti-patterns

- Never add a migration to only one provider, and never hand-copy one provider's migration file into the other folder.
- Never edit or delete a migration that has been applied anywhere shared; add a new one.
- Never set `Database:MigrateOnStartup` outside Development (startup throws by design).
- Never run `dotnet ef database update` or `database drop` against Supabase or any shared database from a dev machine; drop only scratch databases.
- Never use raw SQL, stored procedures or provider-only features in Application code — InMemory (demo mode) must keep working.
- Never rely on a unique index or cascade for correctness without a service-side check (InMemory enforces neither).
