# Operations

Runbooks for running the API: provider tests, security, demo operation, data storage and retention. Merged on 2026-10-19.

Contents:
- [Provider test runbook](#provider-test-runbook)
- [Security runbook](#security-runbook)
- [Replayable Sales and staffing demo](#replayable-sales-and-staffing-demo)
- [Where the data lives, and how to look at it](#where-the-data-lives-and-how-to-look-at-it)
- [Retention, cleanup, archive and export](#retention-cleanup-archive-and-export)

---

<!-- merged from docs/PROVIDER_TEST_RUNBOOK.md on 2026-10-19 -->

## Provider test runbook

Status: active implementation evidence for S14/S15. This file stores no password, cookie, token or session data.

### Test accounts supplied by the owner

- Upwork test workspace: `https://www.upwork.com/nx/find-work/best-matches`
- LinkedIn test profile: `https://www.linkedin.com/in/test1-r-780a82441/?isSelfProfile=true`

These URLs authorize read-only inspection of the signed-in test sessions. They do not authorize copying cookies,
credentials or personal data into OpportunityPilot. Every real proposal submission, Connects expenditure, connection
request, InMail or LinkedIn message requires action-time confirmation from the owner.

### Upwork acceptance path

1. Confirm the account is signed in and the Best Matches page is accessible.
2. Read a bounded sample of visible job cards and one selected job detail without submitting anything.
3. Capture only fields required by the product contract: provider job id/URL, title, summary, skills, budget/rate,
   experience level, client verification/summary, published time and proposal/Connects metadata when visible.
4. Import the selected record as source-linked evidence; deduplicate by provider id and canonical URL.
   Store the visible Connects requirement and, when the user supplies it, the balance observed at the same time.
   Research, review and drafting remain available when the balance is unknown or insufficient.
5. Generate a proposal draft from an approved Sales/Services profile and source evidence.
6. Bind approval to exact job, proposal text, rate, milestones, answers, attachments and visible Connects cost.
7. Open the provider handoff. Stop before the final Upwork submission button and request explicit confirmation.
8. After a confirmed test submission, record provider receipt/result exactly once. An uncertain result remains
   `Unknown` and is never retried blindly.

Until an approved Upwork API/MCP credential is connected, steps 2–7 are an assisted manual handoff. Browser HTML is
not treated as a stable server-side API and OpportunityPilot does not store the Upwork login session.

### LinkedIn acceptance path

1. Confirm the test profile is signed in and its own profile page is accessible.
2. Read the self-profile only to verify the manual handoff target and identity shown to the user.
3. OpportunityPilot may store the user-entered profile URL and prepare connection-note/message drafts.
4. The app opens LinkedIn and copies the approved exact draft for the user.
5. The user sends the request/message. OpportunityPilot records `Sent` only after explicit manual confirmation.

LinkedIn pages are not scraped server-side, cookies are never copied to Render, and the local Candidate application
agent is not reused for Sales connection requests, InMail or messages.

### Evidence log

| Date | Provider | Read-only result | External action | Product follow-up |
|---|---|---|---|---|
| 2026-10-07 | Upwork | Login page reached in the in-app browser; owner screenshot supplied one $50 fixed-price, Intermediate job requiring 8 Connects | None | Owner-scoped research queue, Connects snapshot/status, shortlist/dismiss and bid promotion implemented; signed-in verification still needs the owner session |
| 2026-10-07 | LinkedIn | Auth wall reached; the in-app browser is not signed in | None | Existing LinkedIn draft/copy path is being tightened to approved content only; sign-in is needed for profile/deep-link verification |

---

<!-- merged from docs/SECURITY_RUNBOOK.md on 2026-10-19 -->

## Security runbook

How to rotate every secret, what the quotas are, and how to check the database is not exposed. Secrets live only in
Render (API) and Vercel (web) environment variables — never in the repo, chat or logs.

### 1. Rotating secrets

| Secret | Where | Rotate by | Effect on users |
|---|---|---|---|
| `ConnectionStrings__Main` (Supabase DB password) | Render | Supabase → Project settings → Database → Reset password; paste the new Session-pooler URI into Render; redeploy | none after the restart |
| Supabase JWT signing keys | Supabase | Supabase → Auth → Signing keys → rotate; the API reads `{Auth__SupabaseUrl}/auth/v1/.well-known/jwks.json`, so nothing to change in Render | signed-in users get a new token on their next refresh |
| `Auth__LegacyJwtSecret` (only old projects) | Render | Move the project to asymmetric keys, then delete this variable | as above |
| `Jsearch__Key` | Render | RapidAPI → Apps → regenerate key; update Render | none |
| `Adzuna__AppId` / `Adzuna__AppKey` | Render | developer.adzuna.com → regenerate; update Render | none |
| `Ai__GeminiApiKey` | Render | Google AI Studio → create new key, delete the old; update Render | none |
| Agent keys (`opk_…`) | per user, DB stores only SHA-256 | User revokes in Applications → Agent setup and creates a new one (scopes: Research, Shortlist, Applications) | that user's agent needs the new key |
| Guest sessions (`opg_…`) | DB stores only SHA-256, 30-day expiry | To end every guest session at once: `DELETE FROM app.guest_sessions;` | every guest is signed out and starts a new private workspace |

After any rotation: `GET /health/ready` must return 200 and `GET /api/v1/capabilities` must show the provider as before.

### 2. Quotas (`src/OpportunityPilot.Api/Hosting/RateLimits.cs`)

Every request: 120 per minute per signed-in owner (else per peer address). On top of that:

| Policy | Endpoint | Limit |
|---|---|---|
| `guest-sign-in` | `POST /api/v1/auth/guest` | 10 per hour per address |
| `imports` | `POST /api/v1/imports/preview` | 30 per hour per owner |
| `research-queue` | `POST /api/v1/campaigns/{id}/research` | 60 per hour per owner |
| `job-board-search` | `/api/v1/jobboards/*`, `/api/v1/indeed/*` | 60 per hour per owner (protects the JSearch monthly quota together with its 6 h cache) |

A rejected request gets 429; the web shows "Too many requests in a short time".

### 3. Agent-key scopes

A key is limited to the agent endpoints (it never satisfies the user policy) and to its scopes: `Research` (read
campaigns, upload postings), `Shortlist` (read the shortlist), `Applications` (report results). Keys made before
scopes existed have all three.

### 4. Security events

`GET /api/v1/security/events` lists the owner's own events: guest session issued, agent key created/revoked, account
data exported/deleted. Details name a key's prefix, never the key. Deleting account data removes the owner's events and
keeps one "AccountDataDeleted" record (type and time only).

### 5. Is schema `app` exposed?

Supabase exposes schemas through its REST API to the `anon` and `authenticated` roles. `app` must not be usable by
either. Check:

```sql
SELECT r.rolname
FROM pg_roles r
WHERE r.rolname IN ('anon', 'authenticated')
  AND has_schema_privilege(r.rolname, 'app', 'USAGE');
```

No rows = not exposed. The same query runs in `GET /api/v1/diagnostics` (field `schemaExposure`). If a role is
listed: Supabase → Settings → API → remove `app` from "Exposed schemas", then `REVOKE USAGE ON SCHEMA app FROM anon,
authenticated;`.

---

<!-- merged from docs/SALES_DEMO_OPERATOR_GUIDE.md on 2026-10-19 -->

## Replayable Sales and staffing demo

This walkthrough uses only records prefixed `Demo —` and fictional `.example` identities. It never sends a message,
submits a bid, spends marketplace credits, or contacts a real person.

### Run it

1. Open the Sales workspace Overview and select **Create Sales demo**.
2. The application creates Product-profile facts plus Customer, Partner, Investor and Freelance campaigns and queues the
   normal research pipeline. The card reports stored counts; it does not invent progress.
3. When research reaches a terminal state, the UI automatically finishes the demo: one qualified record per mode is
   shortlisted, email/LinkedIn/cover-note drafts and due follow-ups are created, and one exact draft is approved.
4. Open **Companies** to inspect scores and evidence, then **Outreach** to inspect the draft states.
5. Open **Staffing deals**. The fictional deal demonstrates qualification, manual outreach receipt, reply/meeting,
   consented candidate submission, interview notification and completion, client feedback, accepted offer, signed
   contract and placement. Stored lifecycle events drive the staffing KPIs.
6. Select **Reset demo**. After confirmation, only the current owner's `Demo —` records are removed; unrelated data is
   preserved. The workflow can then be replayed.

### Provider boundary

- LinkedIn messages are drafts for copy/open/manual confirmation. OpportunityPilot does not scrape profiles or send them.
- Upwork and Freelancer submissions require approved official API/MCP access and explicit confirmation of the exact bid.
- Email and calendar delivery require provider OAuth. Until configured, the demo records a labelled manual receipt only.
- The signed document reference is private demo metadata, not a real contract or public file.

### Verification

The API integration scenario is `SalesDemoApiTests`. It proves isolation, replay, all four Sales modes, research output,
shortlists, drafts, approval, Won staffing lifecycle, KPIs and reset preservation. It requires Docker/PostgreSQL.

---

<!-- merged from docs/DATA_STORAGE.md on 2026-10-19 -->

## Where the data lives, and how to look at it

Everything the app creates is stored in the **production Postgres database on Supabase** (the API on Render connects with
`ConnectionStrings__Main`). Nothing important lives in the browser or in memory. The live diagnostics confirm it
(`GET /api/v1/diagnostics`, 2026-10-09): `provider: Postgres, reachable: true, pendingMigrations: 0`.

- All tables are in the schema **`app`** (not `public`). The Supabase client API cannot reach it: `anon` and `authenticated`
  have no usage on schema `app`, so only the API (with the server connection string) reads and writes.
- Each user's rows carry their `OwnerId`; every query in the API filters on it. Guests are real owners, kept in `guest_sessions`.
- Schema changes are EF Core migrations (`src/.../Persistence/Migrations/Postgres`), applied on startup; CI fails if the model and migrations differ.

### Tables by area

| Area | Tables |
|---|---|
| People and keys | `guest_sessions`, `agent_keys`, `security_events`, `ai_usage` |
| Profiles | `profiles`, `profile_versions` |
| Campaigns and research | `campaigns`, `sources`, `source_items`, `import_batches`, `research_jobs`, `research_events`, `campaign_schedules` |
| Results | `opportunities`, `evidence`, `opportunity_evidence`, `activities`, `next_actions` |
| Outreach | `outreach_drafts`, `provider_executions`, `suppressions` |
| Applications | `job_applications`, `wellfound_jobs`, `wellfound_applications`, `wellfound_activities` |
| Staffing | `staffing_accounts`, `staffing_contacts`, `staffing_deals`, `staffing_deal_activities`, `staffing_candidates`, `staffing_submissions`, `staffing_interviews`, `staffing_feedback`, `staffing_offers`, `staffing_rate_cards`, `staffing_proposals`, `staffing_messages`, `staffing_meetings` |
| Sales (removed from the UI, data kept) | `sales_projects`, `sales_bids`, `upwork_opportunities` |

### Look at it (Supabase → SQL editor, runs as the project owner)

```sql
select count(*) from app.opportunities;
select * from app.opportunities order by 1 desc limit 20;      -- newest results (columns include title, organization, score, status)
select * from app.outreach_drafts limit 20;                    -- drafts and their state
select * from app.campaign_schedules;                          -- scheduled campaigns and their next run
select * from app.provider_executions limit 20;                -- every send / bid confirmation with its receipt
select table_name from information_schema.tables where table_schema = 'app' order by 1;   -- every table
```

Use `select * ... limit 20` first: Postgres keeps the EF column names, which may be quoted PascalCase (`"OwnerId"`).

---

<!-- merged from docs/RETENTION.md on 2026-10-19 -->

## Retention, cleanup, archive and export

`RetentionService` runs in the API (`RetentionProcessor`): 3 minutes after start, then every 6 hours, 5 minutes
apart while a batch was full. Each pass deletes at most 500 rows per kind across all owners. Demo mode (no database)
skips it — in-memory data disappears on restart anyway.

| Data | Kept for | Why it goes |
|---|---|---|
| Import previews not committed | until their expiry (`ImportBatch.Lifetime`) | a preview that was never committed has no further use |
| Research run events (the step log of a run) | 90 days | runs, counts, opportunities and evidence stay; only the step-by-step log goes |
| AI usage rows | 90 days | the daily limit only needs today; 90 days covers billing questions |
| Security events | 365 days | one year of key and data-export history |
| Guest sessions | until they expire (30 days) | an expired session can never sign in again |

Never removed by retention: profiles and their versions, campaigns, sources, research jobs and their input snapshots,
opportunities, evidence, drafts, applications, outreach, schedules, Wellfound and Sales records, and every staffing
record (accounts, contacts, deals and their activity, candidates, submissions, interviews, feedback, offers, rate cards,
proposals, messages, meetings). Those are removed only by the owner's **Settings → Delete all data**
(`DELETE /api/v1/account-data`, which also removes the owner's AI usage and security events and keeps one
"AccountDataDeleted" record with no content).

**Export.** `GET /api/v1/account-data/export` returns every table above for the signed-in owner as JSON (Settings →
Export JSON). There is no import/restore endpoint: an export is a copy for the owner's records, not a backup to load
back. Database backups are Supabase's (daily on the free plan); restoring one restores everyone's data to that point.

**Archive.** The optional MongoDB research archive (M8, `Features__MongoArchiveEnabled`) is not built; nothing is
archived outside the main database.
