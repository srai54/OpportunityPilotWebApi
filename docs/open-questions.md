# Open questions

Unresolved gaps, stubs, hard-coded values, unverified integrations and follow-ups. **Log a new entry here instead of
improvising** when something is unclear; do not duplicate an existing entry — extend it. Close an entry by setting its
status to `Closed (<date>, <commit or PR>)` and updating every doc it touches in the same change.

Priority: **P1** blocks real use or is a security/data-safety risk · **P2** needed for a planned milestone or correctness ·
**P3** hygiene. Owner: **Shivanshu** (decision, credentials, accounts or live verification needed) · **Claude** (code change
that can be made and verified in this repo).

Audit date: 2026-10-05.

## Summary

| ID | Title | Priority | Owner | Status |
|---|---|---|---|---|
| OQ-BE-001 | Live LinkedIn and Naukri selectors are unverified | P1 | Shivanshu | Open |
| OQ-BE-002 | Production runs in demo mode: Supabase not configured on Render | P1 | Shivanshu | Open |
| OQ-BE-003 | Supabase Data API exposure of schema `app` not verified | P1 | Shivanshu | Open |
| OQ-BE-004 | Agent defaults answer questions the user never answered | P1 | Shivanshu | Closed 2026-10-05 — example config assumes nothing (`yesNo: ""`, `yearsOfExperience: null`); "I agree" boxes need `profile.acceptTermsCheckboxes` |
| OQ-BE-005 | Fetching of the real public internet is unverified | P2 | Shivanshu | Open |
| OQ-BE-006 | Remaining M4 + M5 AI, outreach and follow-ups not built | P2 | Claude | Closed 2026-10-07 — review flow and goal parser built; AI summaries remain separate |
| OQ-BE-007 | Gemini integration absent; model id and `Ai:Mode` unused | P2 | Shivanshu | Open |
| OQ-BE-008 | Hard-coded deployment URLs disagree | P2 | Shivanshu | Open |
| OQ-BE-009 | No account data deletion or export; no delete endpoints | P2 | Shivanshu | Open |
| OQ-BE-010 | No continuous integration | P2 | Shivanshu | Closed 2026-10-07 — GitHub Actions added |
| OQ-BE-011 | API lets the user move an Applied opportunity back to New | P2 | Shivanshu | Closed 2026-10-07 — backwards pipeline moves rejected |
| OQ-BE-012 | Agent security-check and daily-limit paths are untested | P2 | Claude | Open |
| OQ-BE-013 | Research slice not deployed; status docs dated 2026-10-01 | P2 | Shivanshu | Open |
| OQ-BE-014 | No research resume endpoint (plan §16) | P3 | Claude | Open |
| OQ-BE-015 | Evidence holds only the first 2000 characters | P3 | Claude | Closed 2026-10-07 — bounded storage raised to 8000 |
| OQ-BE-016 | Regex cache in `TextMatch` grows without bound | P3 | Claude | Closed 2026-10-07 — bounded at 512 patterns |
| OQ-BE-017 | Model-binding 400s reveal internal type names | P3 | Claude | Closed 2026-10-07 — formatter exception messages disabled |
| OQ-BE-018 | Forwarded headers trusted from any source | P3 | Claude | Closed 2026-10-07 — X-Forwarded-For no longer trusted |
| OQ-BE-019 | Supabase JWKS refresh blocks a request thread | P3 | Claude | Open |
| OQ-BE-020 | No retention for import batches, research jobs and events | P3 | Shivanshu | Open |
| OQ-BE-021 | Single global rate limit; no research quota | P3 | Shivanshu | Open |
| OQ-BE-022 | Agent report can mark any owned opportunity Applied | P3 | Shivanshu | Closed 2026-10-05 — only shortlisted Job opportunities move to Applied (integration test added) |
| OQ-BE-023 | M6–M8 capabilities not built (Gmail, other modes, Mongo, scheduler) | P3 | Shivanshu | Open |
| OQ-BE-024 | InstaHyre exists in the API enum but not in the agent | P3 | Shivanshu | Closed 2026-10-07 — adapter/API path added; live selectors need U3 |
| OQ-BE-025 | Agent crashes on a corrupt local log line | P3 | Claude | Closed 2026-10-05 — unreadable lines are reported and skipped |
| OQ-BE-026 | Stale code comments describe removed behaviour | P3 | Claude | Closed 2026-10-07 |
| OQ-BE-027 | Agent README config table is broken | P3 | Claude | Closed 2026-10-05 — table repaired, new setting documented |
| OQ-BE-028 | Tooling not pinned: no dotnet-ef manifest, no formatter config | P3 | Claude | Closed 2026-10-07 — tool manifest and editorconfig added |
| OQ-BE-029 | Sales discovery/provider accounts are not selected or connected | P1 | Shivanshu | Open |
| OQ-BE-030 | Wellfound Recruit/Reach live OAuth is not authorized | P1 | Shivanshu | Open |
| OQ-BE-031 | Indeed listing/application API is not approved | P1 | Shivanshu | Open |
| OQ-BE-032 | Hanko sign-in not verified against a live Hanko project | P1 | Shivanshu | Open |

## Entries

### OQ-BE-001 — Live LinkedIn and Naukri selectors are unverified
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `agent/src/platforms/linkedin.ts:52-96`, `agent/src/platforms/naukri.ts:42-76`, `agent/test/mock/server.ts:1-4`
- **What is needed** The adapters are tested only against the local mock. Run `npm run agent -- collect linkedin --campaign <id>` and `apply linkedin` (dry run) on a real account, then the same for Naukri; send `agent/.data/debug/*` from any failure so selectors can be fixed (see `skills/agent-adapter.SKILL.md`).

### OQ-BE-002 — Production runs in demo mode: Supabase not configured on Render
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `render.yaml:27-31`; `Infrastructure/DependencyInjection.cs:47-51`; `Api/Auth/AuthSetup.cs:29-32`
- **What is needed** Create the Supabase project, set `ConnectionStrings__Main` (session pooler) and `Auth__SupabaseUrl` on Render, set the web app's Supabase variables, redeploy, then smoke-test sign-in on the live URLs. Until then all data is in memory and lost on every restart or sleep.

### OQ-BE-003 — Supabase Data API exposure of schema `app` not verified
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `Infrastructure/Persistence/AppDbContext.cs:12-19`; plan §17
- **What is needed** Confirm in the Supabase project that schema `app` is not exposed through the Data API (or enable RLS with owner policies), and that the API's database role has only the privileges it needs. No RLS policies exist in the migrations.

### OQ-BE-004 — [Closed] Agent defaults answer questions the user never answered
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `agent/src/config.ts:41-58` (example answers incl. phone `9999999999`, salaries; `defaults.yesNo: 'Yes'`, `yearsOfExperience: 5`), `agent/src/answers.ts:58-66`, `agent/src/form.ts:136-149`
- **What is needed** A decision. With the example config, any uncovered Yes/No question is answered "Yes", any experience question for an unknown skill "5", and required or terms/consent checkboxes are ticked automatically. This conflicts with "anything not covered is never guessed" (`agent/README.md:35-36`) and plan §23 (no invented claims). Options: default `yesNo` to `""`, no default years, refuse `--submit` while example placeholder values are unchanged, list auto-ticked consents in the result.

### OQ-BE-005 — Fetching of the real public internet is unverified
- **Priority** P2 · **Owner** Shivanshu · **Status** Open
- **Where** `Infrastructure/Research/SafeFetcher.cs`; `docs/IMPLEMENTATION_STATUS.md` (Not verified)
- **What is needed** One manual run in Development against a few real public pages and feeds (TLS, real redirects, real HTML, charsets) and a note of the results. Tests use stubbed handlers and a loopback server only.

### OQ-BE-006 — Remaining M4 + M5 AI, outreach and follow-ups not built
- **Priority** P2 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** `docs/M4_M5_CONTRACT.md`; CoverNote persistence, template generation, approval and agent handoff are built, but there is no `ILlmClient`, Suppression, NextAction, UsageRecord, other outreach channel or cross-opportunity draft inbox
- **What is needed** Build the remaining contract slices, keeping the deterministic CoverNote path as the no-AI fallback.

### OQ-BE-007 — Gemini integration absent; model id and `Ai:Mode` unused
- **Priority** P2 · **Owner** Shivanshu · **Status** Open
- **Where** `Application/Configuration/Options.cs:22-24`, `appsettings.json:41-47`, `Application/Capabilities/CapabilityService.cs:61-66`
- **What is needed** An AI Studio key with free-tier quota to verify live calls; confirm the model id `gemini-3.8-flash`. `Ai:Mode` is bound but never read (capabilities derive the AI mode from key presence) — remove it or use it when M4 lands.

### OQ-BE-008 — Hard-coded deployment URLs disagree
- **Priority** P2 · **Owner** Shivanshu · **Status** Open
- **Where** `appsettings.json:22-27` (CORS: Render web URL + Vercel URL), `render.yaml:6` (service `opportunitypilot-api`) and `render.yaml:33-34` (CORS index 0 = Render web URL), `agent/src/config.ts:36` (agent default `https://opportunitypilotwebapi.onrender.com`)
- **What is needed** Decide the canonical API and web origins. A fresh Blueprint deploy would be named `opportunitypilot-api`, so the agent's default URL would not reach it; the web app actually runs on Vercel (index 1, only from appsettings). Move origins to environment configuration and make the agent require an explicit `api.url`.

### OQ-BE-009 — No account data deletion or export; no delete endpoints
- **Priority** P2 · **Owner** Shivanshu · **Status** Open
- **Where** `Api/Controllers/*` (only sources and agent keys can be deleted); plan §17
- **What is needed** Decide the retention and deletion policy, then add deletion/export for an account (profiles, campaigns and everything below them, applications, keys) and delete endpoints for profiles and campaigns. Note `campaigns → profiles` is Restrict.

### OQ-BE-010 — No continuous integration
- **Priority** P2 · **Owner** Shivanshu · **Status** Closed 2026-10-07
- **Where** repository root (no `.github/` or other pipeline)
- **What is needed** Choose a CI host; run `dotnet build`, `dotnet test` (Docker available for Testcontainers) and `agent` `npm run typecheck && npm test` on every push.

### OQ-BE-011 — API lets the user move an Applied opportunity back to New
- **Priority** P2 · **Owner** Shivanshu · **Status** Closed 2026-10-07
- **Where** `Domain/Opportunities/Opportunity.cs:102-111`, `Application/Opportunities/OpportunityService.cs:46-65`
- **What is needed** Decide which transitions are allowed. Any status → any status is accepted; the web app was patched not to downgrade Applied (HANDOFF), but the API does not enforce it.

### OQ-BE-012 — Agent security-check and daily-limit paths are untested
- **Priority** P2 · **Owner** Claude · **Status** Open
- **Where** `agent/src/platforms/linkedin.ts:21-27,34-37` (note `isLoggedIn` checks `checkpoint` but not `challenge`), `agent/test/e2e.test.ts` (only the login wall is tested)
- **What is needed** Add mock pages for `/checkpoint/`, `/challenge/` and the Easy Apply limit message, and tests proving the run stops without sending anything.

### OQ-BE-013 — Research slice not deployed; status docs dated 2026-10-01
- **Priority** P2 · **Owner** Shivanshu · **Status** Open
- **Where** `docs/IMPLEMENTATION_STATUS.md` (M9 row, "Last updated")
- **What is needed** Deploy the research slice, run the end-to-end smoke test listed under "Exact next task", and refresh the status file.

### OQ-BE-014 — No research resume endpoint (plan §16)
- **Priority** P3 · **Owner** Claude · **Status** Open
- **Where** `Api/Controllers/ResearchJobsController.cs`
- **What is needed** Decide whether `POST /api/v1/research-jobs/{id}/resume` is still wanted; today a job resumes only when its lease expires.

### OQ-BE-015 — Evidence holds only the first 2000 characters
- **Priority** P3 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** `Domain/Research/Evidence.cs:13`, `Application/Research/ResearchRunner.cs:352-374`
- **What is needed** Snippet-level evidence windows, so a skill matched late in a long posting is visible in the cited excerpt.

### OQ-BE-016 — Regex cache in `TextMatch` grows without bound
- **Priority** P3 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** `Application/Research/Rules/TextMatch.cs:21,40`
- **What is needed** Every distinct user-supplied term ever scored is cached for the life of the process, across all users. Bound the cache (size limit or LRU).

### OQ-BE-017 — Model-binding 400s reveal internal type names
- **Priority** P3 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** `Api/Program.cs:31-32`
- **What is needed** A body that fails JSON conversion returns e.g. "could not be converted to OpportunityPilot.Application.Profiles.CreateProfileRequest". Set `JsonOptions.AllowInputFormatterExceptionMessages = false` or customise the invalid-model-state response.

### OQ-BE-018 — Forwarded headers trusted from any source
- **Priority** P3 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** `Api/Program.cs:60-65`
- **What is needed** `KnownIPNetworks`/`KnownProxies` are cleared so `X-Forwarded-For` is accepted from anyone. On Render the last hop is the proxy's, but if the app is reachable directly the per-IP rate limit for anonymous calls can be spoofed. Restrict when the hosting network is known.

### OQ-BE-019 — Supabase JWKS refresh blocks a request thread
- **Priority** P3 · **Owner** Claude · **Status** Open
- **Where** `Api/Auth/SupabaseJwks.cs:19-32`
- **What is needed** The key set is fetched synchronously inside a lock (up to the 10 s client timeout) during token validation. Consider a background refresh or `ConfigurationManager<JsonWebKeySet>`.

### OQ-BE-020 — No retention for import batches, research jobs and events
- **Priority** P3 · **Owner** Shivanshu · **Status** Open
- **Where** `Domain/Research/ImportBatch.cs:8`, `Application/Research/ResearchService.cs`
- **What is needed** A retention rule: expired import batches and old jobs/events are never deleted.

### OQ-BE-021 — Single global rate limit; no research quota
- **Priority** P3 · **Owner** Shivanshu · **Status** Open
- **Where** `Api/Program.cs:51-58`
- **What is needed** Decide per-endpoint limits (e.g. research queueing, CSV preview, guest sign-in) per plan §17.

### OQ-BE-022 — [Closed] Agent report can mark any owned opportunity Applied
- **Priority** P3 · **Owner** Shivanshu · **Status** Open
- **Where** `Application/Applications/ApplicationService.cs:52-66`, `Domain/Opportunities/Opportunity.cs:113-121`
- **What is needed** Decide whether `MarkApplied` should require a Shortlisted Job opportunity on the same platform/external id. Today any owned opportunity in any status (Dismissed, Customer mode) becomes Applied.

### OQ-BE-023 — M6–M8 capabilities not built (Gmail, other modes, Mongo, scheduler)
- **Priority** P3 · **Owner** Shivanshu · **Status** Open
- **Where** `Application/Capabilities/CapabilityService.cs:86-111`, `Application/Campaigns/CampaignCriteria.cs:72`
- **What is needed** Gmail (M6), Partner/Investor/Freelance modes (M7), Mongo archive and a reliable scheduler (M8) are capability entries only. The first manual sales project/bid/approve endpoints and migrations now exist; Freelancer discovery/placement, tender feeds, proposal drafts, and Freelance mode remain unimplemented.

### OQ-BE-024 — InstaHyre exists in the API enum but not in the agent
- **Priority** P3 · **Owner** Shivanshu · **Status** Closed 2026-10-07
- **Where** `Domain/Applications/ApplicationPlatform.cs:7`, `Domain/Opportunities/JobPlatform.cs`, `Application/Capabilities/CapabilityService.cs:101-103`
- **What is needed** Decide whether to build an InstaHyre adapter; `JobPlatform` has no InstaHyre value, so research cannot target it.

### OQ-BE-025 — [Closed] Agent crashes on a corrupt local log line
- **Priority** P3 · **Owner** Claude · **Status** Open
- **Where** `agent/src/store.ts:29-35`
- **What is needed** One unparsable line in `.data/applications.jsonl` makes every command that opens the store throw. Skip and report bad lines instead.

### OQ-BE-026 — Stale code comments describe removed behaviour
- **Priority** P3 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** `Application/Configuration/SetupState.cs:3-6` (says affected requests get 503 "Setup required"; demo mode replaced that), `Api/Auth/AgentKeyAuthenticationHandler.cs:10-11` (says the key works on "the report endpoint" only; `/api/v1/agent/*` also accepts it)
- **What is needed** Update the comments.

### OQ-BE-027 — [Closed] Agent README config table is broken
- **Priority** P3 · **Owner** Claude · **Status** Open
- **Where** `agent/README.md:100-108`
- **What is needed** A paragraph inside the `config.json` reference table splits it; the rows after it render as plain text. The agent folder belongs to another engineer, so it was not edited in the docs pass.

### OQ-BE-028 — Tooling not pinned: no dotnet-ef manifest, no formatter config
- **Priority** P3 · **Owner** Claude · **Status** Closed 2026-10-07
- **Where** repository root (no `.config/dotnet-tools.json`, no `.editorconfig`)
- **What is needed** Pin `dotnet-ef` 10.x in a local tool manifest so migration commands are reproducible, and add an `.editorconfig` matching the current style.

### OQ-BE-029 — Sales discovery/provider accounts are not selected or connected
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `docs/STAFFING_SALES_INTEGRATION_PLAN.md`; tasks X3/X5/X7/X12
- **What is needed** Connect or provide approved accounts/scopes for Upwork MCP/API, the selected sales-intelligence
  provider, Gmail or Microsoft email, Google or Outlook Calendar, and an optional e-signature provider. LinkedIn
  scraping/browser automation is not an option; LinkedIn remains manual/assisted unless an official approved product
  explicitly grants the required read or action capability. Do not run credit-consuming enrichment without explicit
  approval.

### OQ-BE-030 — Wellfound Recruit/Reach live OAuth is not authorized
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `Application/Wellfound`, `/api/v1/wellfound/status`, `TASKS.md` W1/W7
- **What is needed** Complete Recruit Pro/Reach account setup and authorize each remote MCP separately. Then enumerate
  the granted tools, verify pagination and read-only sync against the test company before enabling any live write tool.
  Anonymous public job discovery is implemented separately and does not require this OAuth. Private recruiter jobs,
  applicants and any provider-side decision remain unavailable until authorization.

### OQ-BE-031 — Indeed listing/application API is not approved
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** Web `src/features/wellfound/IndeedSearchPanel.tsx`; Indeed Partner Developer Agreement and API guides
- **What is needed** Submit the intended Candidate/Sales use case to Indeed and obtain written integration approval plus
  the applicable partner credentials. The documented Job Sync and Candidate APIs serve approved employer/ATS flows,
  not an unrestricted public-search feed. Until approval exists, keep Indeed as an official search handoff and do not
  scrape, copy listings, store account cookies, or claim provider-side applications/messages.

### OQ-BE-032 — Hanko sign-in not verified against a live Hanko project
- **Priority** P1 · **Owner** Shivanshu · **Status** Open
- **Where** `Api/Auth/HankoAuth.cs`, `Api/Auth/AuthSetup.cs`; web `docs/AUTH_SETUP.md`
- **What is needed** Set `Auth__HankoApiUrl` (and `Auth__HankoAudience`) on Render and `VITE_HANKO_API_URL` on Vercel, sign
  in on the live site, and confirm the API accepts the token (no "session ended" right after sign-in). Validation is proven
  only with locally generated keys. If Hanko Cloud sets an `iss` other than the API URL, set `Auth__HankoIssuer` to it.
  Revocation in Hanko is not checked per request (token lifetime applies).
