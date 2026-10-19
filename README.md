# OpportunityPilot — Web API

[![ci](https://github.com/shr1-live/OpportunityPilotWebApi/actions/workflows/ci.yml/badge.svg)](https://github.com/shr1-live/OpportunityPilotWebApi/actions/workflows/ci.yml)

ASP.NET Core (.NET 10) API behind **OpportunityPilot**: one pipeline (find → score → approve → act → follow up) for a job seeker and for the sales team of a staffing company. **Product overview, live demo, AI usage and architecture diagram:** see the web app's README (`OpportunityPilotWebapp/README.md`) and `docs/ARCHITECTURE.md` there.

Live: https://opportunitypilotwebapi.onrender.com (`/health/ready`, `/api/v1/capabilities`). Web: https://opportunity-pilot-webapp.vercel.app.

## What is in here

| Area | Highlights |
|---|---|
| Sources | Free feeds (Arbeitnow, Jobicy, We Work Remotely, Remotive, Remote OK, Himalayas, The Muse, Instahyre), ATS boards (Greenhouse, Lever, Ashby, Workday public CXS), Wellfound public listings; keyed: JSearch (LinkedIn/Indeed), SEEK, Apollo, Apify (Naukri), Adzuna |
| Scoring | Transparent rules engine (`Application/Research/Rules`): every point has a quoted reason |
| AI | Groq / Gemini through `Application/Ai/AiService.cs` (goal → criteria, assistant, draft rewrite, profile improve) with JSON-only output, daily budgets and rules fallback; Whisper for voice |
| Safety | `SafeFetcher` (public IPs only, redirects re-checked, size/time caps), owner-scoped queries, per-visitor rate limits, encrypted mailbox/connector secrets, no server-side automation of logged-in sites |
| Background | `ResearchProcessor` claims durable `research_jobs` with a lease; campaign scheduler |
| Agent | `agent/`: Node + Playwright desktop agent for LinkedIn/Naukri in the user's own browser; stops at any login check or CAPTCHA |
| Tests | 580+ xUnit tests: unit (no I/O) and integration (real API + PostgreSQL via Testcontainers); CI checks EF migrations match the model for both providers |

## Layout

```
src/
  OpportunityPilot.Domain          entities (Profile, JobApplication, AgentKey, Campaign, Source, ResearchJob,
                                   Evidence, Opportunity, Activity…), ownership marker, enums
  OpportunityPilot.Application     services, DTOs, options, capability catalogue, research runner and rules engine
  OpportunityPilot.Infrastructure  EF Core: SqlServer + Postgres contexts and migration sets; safe fetcher, HTML/feed parsing
  OpportunityPilot.Api             controllers, auth (Hanko / Supabase JWT / guest / dev bypass / agent key), hosting, research processor
tests/
  OpportunityPilot.UnitTests         no I/O (rules, parsers, fetcher policy with stubbed network)
  OpportunityPilot.IntegrationTests  real API + Postgres via Testcontainers (needs Docker)
agent/                               local desktop agent (Node + Playwright) — see agent/README.md
```

## Quick start (local)

```bash
# Demo mode on any OS: no database, no keys, guest sign-in, data in memory
ConnectionStrings__Main= Cors__AllowedOrigins__0=http://localhost:5173 ASPNETCORE_URLS=http://localhost:5051 \
  dotnet run --no-launch-profile --project src/OpportunityPilot.Api
dotnet test tests/OpportunityPilot.UnitTests      # unit tests, no Docker
dotnet test                                       # everything; integration tests need a running Docker daemon
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
| Security policy | [SECURITY.md](SECURITY.md) |
| Conventions and error envelope | [docs/conventions.md](docs/conventions.md) |
| Open questions and known gaps | [docs/open-questions.md](docs/open-questions.md) |
| Progress and decisions | [docs/plans-and-status.md](docs/plans-and-status.md), [docs/DECISIONS.md](docs/DECISIONS.md) |
| Slice design specs | [docs/feature-contracts.md](docs/feature-contracts.md) (M4/M5 not built) |
