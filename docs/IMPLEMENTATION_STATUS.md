# Implementation status

Backend repository (`OpportunityPilotWebApi`). Milestones follow the implementation plan §20.
"Verified" means the commands below were run and passed in this repository; anything not run is called out.

Last updated: 2026-10-06.

| Milestone | Status | Notes |
|---|---|---|
| M0 Inspect, scaffold | Done (earlier sessions) | Solution, Clean Architecture layers, health endpoints, ProblemDetails, demo mode |
| M1 Auth, database, profiles | Guest mode done; hosted account flow awaits optional configuration | Durable hashed opaque guest sessions need no auth provider; Supabase JWT/JWKS (RS256/ES256/EdDSA; isolated opt-in HS256 legacy support), dev-bypass guard, GUID owner subjects, owner-scoped profiles, SqlServer + Postgres migrations. Browser signup/sign-in/refresh/reset hardening is in the web repo; live Supabase account verification still needs production keys |
| M2 Campaigns and source imports | **Done (verified)** | Job and Customer only; Partner/Investor/Freelance return 400 until M7 |
| M3 Research pipeline and scoring | **Done (verified locally)** | Live fetching of real public internet pages was not exercised (tests use a loopback server and stubbed handlers) |
| M4 Shortlist, Gemini, manual AI recovery | In progress | Deterministic CoverNote template and user approval are built; Gemini goal preview and draft rewrite (CON8, suggestion only, unit-tested with a fake client; not live-verified against Gemini) are built; manual AI exchange is not |
| M5 Outreach and pipeline tracking | In progress | Versioned CoverNote persistence and agent handoff are built; other outreach channels, inbox and follow-ups are not |
| M6 Gmail | Not started | |
| M7 Partner, Investor, Freelance modes | In progress | Manual sales project/bid/approve API plus provider migrations exist; campaign mode, Freelancer/tender providers, and the Sales UI are not wired |
| M8 Mongo archive, scheduled research | Not started | The in-process processor is not a scheduler (plan §18, §29) |
| M9 Deployment readiness | Not started for this slice | `render.yaml` and the Dockerfile exist from M0; the research slice has not been deployed |

## M2 + M3: what exists

Design spec: `docs/RESEARCH_CONTRACT.md`. What exists now: [api-contracts.md](api-contracts.md), [db-schema.md](db-schema.md).

- Domain — `src/OpportunityPilot.Domain/Campaigns/Campaign.cs`; `Research/` (`Source`, `SourceItem`, `ImportBatch`,
  `ResearchJob` with lease/claim/cancel rules, `ResearchEvent`, `Evidence`, enums); `Opportunities/` (`Opportunity`
  — research never touches `Status` — `Activity`, `OpportunityEvidence`, enums).
- Persistence — `src/OpportunityPilot.Infrastructure/Persistence/AppDbContext.cs`, `ProviderContexts.cs`
  (jsonb on Postgres; filtered unique indexes for `(SourceId, ExternalId)` and one active job per campaign).
  Migration `AddResearchPipeline` in both sets (`Migrations/SqlServer/20261001105144_…`, `Migrations/Postgres/20261001105153_…`); both only create tables and indexes.
- Application — `Campaigns/` (service, criteria, weights), `Sources/` (service, paste parser), `Imports/`
  (RFC 4180 reader/writer, preview/commit), `Research/` (`ResearchService`, `ResearchRunner`, `ResearchOptions`,
  candidates and dedupe keys, `Rules/JobRules.cs`, `Rules/CustomerRules.cs`, `Rules/Scoring.cs`, `Rules/TextMatch.cs`),
  `Opportunities/` (list/detail/status/export), `Agents/AgentResearchService.cs`; `Applications/ApplicationService.cs`
  now moves a reported opportunity to Applied.
- Infrastructure — `Research/SafeFetcher.cs` (connect-callback DNS validation, manual redirects, caps, retries),
  `Research/FetchAddressPolicy.cs` (address classifier), `Research/ContentParser.cs` (AngleSharp 1.8.3, RSS/Atom with DTDs prohibited).
- API — `Controllers/CampaignsController.cs`, `SourcesController.cs`, `ImportsController.cs`, `ResearchJobsController.cs`,
  `OpportunitiesController.cs`, `AgentResearchController.cs` (agent key only), `OverviewController.cs` (adds `campaigns`, `shortlisted`);
  `Hosting/ResearchProcessor.cs` (BackgroundService, `Research:ProcessorEnabled`).
- Capabilities — `csv-import`, `public-urls`, `feeds`, `rules` report Ready.

## Verification (run 2026-10-01)

| Command | Result |
|---|---|
| `dotnet build --no-incremental` | 0 warnings, 0 errors |
| `dotnet test` | Unit 225 passed / 0 failed; Integration 42 passed / 0 failed (Postgres 17 via Testcontainers, Docker 29.7) |
| `dotnet ef migrations add AddResearchPipeline …` (SqlServer and Postgres contexts) | Generated; reviewed: create-table/create-index only |
| `dotnet ef database update --context SqlServerAppDbContext` on a scratch LocalDB database, then `database drop` | All three SqlServer migrations applied; scratch database dropped |

Postgres migrations are applied by every integration test run (`Database:MigrateOnStartup` in the test factory). The current
sales migration has been generated for both providers; a Docker-backed migration/integration run is still required.

Integration coverage for this slice: Job research over pasted postings (Qualified / Excluded / NeedsVerification,
scores, coverage, evidence links, reasons); rerun without duplicates keeping Shortlisted; CSV preview errors, commit,
re-import without duplicates; Customer scoring (the plan's 65/100 example); export formula neutralisation; two-user
ownership (404s); stale campaign edit (409); cancel queued and cancel running; two processors racing; expired-lease
reclaim without duplicates; poison job failed after 3 attempts; run limits and result limit; agent flow (campaigns →
postings → research → shortlist → report Applied); agent-key/user-token separation; blocked addresses (literal IPs,
IPv4-mapped IPv6, metadata, `localhost`) failing safely with a loopback server proving no connection is made; feed and
page fetching over real sockets from a loopback server; demo mode (InMemory) with the real background processor.

## Not verified / known gaps

Tracked with owners and priorities in [open-questions.md](open-questions.md): real-internet fetching (OQ-BE-005),
evidence excerpt windows (OQ-BE-015), the in-process processor not being a scheduler (OQ-BE-023), rate limiting and
research quota (OQ-BE-021), live LinkedIn/Naukri selectors (OQ-BE-001) and deployment of this slice (OQ-BE-013).

## Beyond the plan (user decisions)

- **Local desktop agent** (`agent/`, owned by another engineer) applies to jobs on LinkedIn/Naukri from the user's own
  logged-in browser. The API side — `POST /api/v1/applications/report`, `/api/v1/applications`, agent keys — predates
  this slice.
- **Agent as research source and applier** (2026-10-01): `/api/v1/agent/campaigns`, `/agent/campaigns/{id}/postings`,
  `/agent/shortlist`, and `opportunityId` on reports. Only jobs the user shortlists are offered to the agent.

## Exact next task

Wire the React app's campaign, source, import, research-progress and opportunity screens to these endpoints
([api-contracts.md](api-contracts.md)) and run one end-to-end smoke test in Development: profile → Job campaign → paste source →
research → shortlist → agent report. Then start M4 (Gemini request parsing and evidence summaries with rules fallback).

> Docs audit 2026-10-05: according to the workspace HANDOFF the web screens and the end-to-end check were done on
> 2026-10-01, and the next slice is M4 + M5 ([M4_M5_CONTRACT.md](M4_M5_CONTRACT.md), not built — OQ-BE-006). This file
> has not been re-verified since 2026-10-01 (OQ-BE-013).
