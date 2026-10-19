# OpportunityPilot — Web API

ASP.NET Core (.NET 10) API for OpportunityPilot. Clean Architecture:

```
src/
  OpportunityPilot.Domain          entities (Profile, JobApplication, AgentKey, Campaign, Source, ResearchJob,
                                   Evidence, Opportunity, Activity…), ownership marker, enums
  OpportunityPilot.Application     services, DTOs, options, capability catalogue, research runner and rules engine
  OpportunityPilot.Infrastructure  EF Core: SqlServer + Postgres contexts and migration sets; safe fetcher, HTML/feed parsing
  OpportunityPilot.Api             controllers, auth (Supabase JWT / guest / dev bypass / agent key), hosting, research processor
tests/
  OpportunityPilot.UnitTests         no I/O (rules, parsers, fetcher policy with stubbed network)
  OpportunityPilot.IntegrationTests  real API + Postgres via Testcontainers (needs Docker)
agent/                               local desktop agent (Node + Playwright) — see agent/README.md
```

## Quick start (local)

```bash
dotnet run --project src/OpportunityPilot.Api     # http://localhost:5051
dotnet test                                       # integration tests need a running Docker daemon
```

Development uses SQL Server LocalDB, migrates on startup, and accepts the
`X-Dev-User` header instead of a Supabase token. Pair it with the web app's
`VITE_DEV_AUTH=true`. Without a connection string or Supabase URL the API runs in
demo mode (in-memory data, guest sign-in).

## Documentation

| Topic | File |
|---|---|
| Working on this repo (stack, commands, folder layout, rules) | [CLAUDE.md](CLAUDE.md) |
| Setup, configuration, Supabase, Render | [docs/SETUP.md](docs/SETUP.md) |
| **Endpoints** (authoritative) | [docs/api-contracts.md](docs/api-contracts.md) |
| Database schema and migrations | [docs/db-schema.md](docs/db-schema.md) |
| Business rules, limits and scoring | [docs/business-rules.md](docs/business-rules.md) |
| States and transitions | [docs/opportunity-lifecycle.md](docs/opportunity-lifecycle.md) |
| Product flow, status and plans (incl. vector search) | [docs/plans-and-status.md](docs/plans-and-status.md) |
| Operations: provider tests, security, demo, storage, retention | [docs/operations.md](docs/operations.md) |
| Conventions and error envelope | [docs/conventions.md](docs/conventions.md) |
| Open questions and known gaps | [docs/open-questions.md](docs/open-questions.md) |
| Progress and decisions | [docs/plans-and-status.md](docs/plans-and-status.md), [docs/DECISIONS.md](docs/DECISIONS.md) |
| Slice design specs | [docs/feature-contracts.md](docs/feature-contracts.md) (M4/M5 not built) |
