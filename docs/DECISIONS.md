# Decisions

Short records of choices that shape the code. Newest last.

## 2026-10-01 — Research first slice: Job and Customer only

The shared pipeline (sources → evidence → hard filters → scoring → opportunities) is built once and proven on two
modes. Job serves the user's immediate goal (the desktop agent applies to shortlisted jobs); Customer is the plan's
primary workflow. Partner, Investor and Freelance return 400 "not supported yet" until M7 rather than being scored
with rules that were never designed for them.

## 2026-10-01 — Deterministic rules engine with fixed default weights

Rules (keyword matching on word boundaries, a few skill aliases, experience-range regexes, work-mode keywords) run
before any AI. They are pure functions in `Application/Research/Rules`, unit-tested per criterion, and versioned with
the code: changing a pattern or alias changes scores. Weights have fixed per-mode defaults, are editable per campaign and
are stored as integers summing to exactly 100, so a breakdown always adds up. Hard filters run before scoring and an
unknown is never a pass. The exact values and rules: [business-rules.md](business-rules.md#scoring).

## 2026-10-01 — Not applicable is redistributed; unknown is not

A criterion the user configured nothing for (no preferred skills, no years of experience) is left out and its weight is
spread proportionally over the rest, so a campaign that does not care about experience is not capped at 80. A criterion
that *is* configured but that the source does not answer stays in, scores 0 and lowers coverage, so sparse sources
cannot look like strong matches (plan §12: "do not renormalize away unknowns"). Contact path has nothing to configure
and is always applicable in Customer campaigns.

## 2026-10-01 — Durable jobs with leases, driven by an injectable runner

`ResearchJob` rows are the queue. A single hosted processor claims one job by setting a two-minute lease guarded by the
row's version (optimistic concurrency, so no provider-specific SQL — it works on Postgres, SQL Server and InMemory),
renews it after each source, and an expired lease is reclaimed after a crash or restart. A job interrupted three
times is failed instead of retried forever. Progress saves merge the user's concurrent changes (cancel requests,
status changes) instead of overwriting them. Opportunities are upserted by `(campaign, dedupe key)` and research never
changes their status, so reruns are safe. Tests disable the hosted loop and call `IResearchRunner` directly.

## 2026-10-01 — Safe fetching is not configurable

The fetcher resolves DNS inside the connect callback, refuses any answer containing a non-public address, and connects
to the address it checked (DNS rebinding cannot slip through). Redirects are followed by hand, each hop re-checked.
http is allowed only in Development; ports 80/443 only. The address policy is a DI service with one production
implementation and no configuration binding; the only relaxed policies live in the test project.

## 2026-10-01 — The desktop agent as a research source

LinkedIn and Naukri are never crawled server-side. The agent reads postings in the user's own logged-in browser and
delivers them into one Agent source per campaign and platform, keyed by the platform's job id. Research scores them like
any other source. The agent is offered only opportunities the user shortlisted, and an Applied report with the
opportunity id moves it to Applied. Agent keys work only on the agent endpoints and the report endpoint.

## 2026-10-01 — Demo mode keeps research working

Without a connection string the API uses the EF InMemory store; the processor and rules run unchanged, because claiming
relies on concurrency tokens rather than raw SQL. Unique indexes are not enforced there, so services check for
duplicates themselves. Data still disappears on restart, and capabilities say so.

## 2026-10-06 — Guests stay available next to real accounts

**Decision:** "Continue as guest" is offered even when Supabase sign-in is configured (`Auth:AllowGuests`, default `true`). Each guest is a random, server-signed identity with its own isolated data; set `Auth__AllowGuests=false` to require an account.
**Why:** design round 3 (`AuthSignIn`) keeps guest access for demos next to email/Google sign-in, and the user asked for the full design to be implemented.
**Consequence (superseded below):** the original implementation required `Auth__GuestSigningKey` for restart survival.
Guest data is never merged into an account.

## 2026-10-06 — Guest mode has no external-auth or signing-key dependency

**Decision:** Until Supabase Auth is configured, guest login must work seamlessly on its own and survive Render
restarts when the main database is durable.

**Consequence:** guest credentials are now opaque 256-bit `opg_…` tokens. The database stores only SHA-256 hashes,
random owner ids and expiry times; no plaintext credential, Supabase Auth project or manually configured guest
signing key is required. In pure in-memory demo mode, guest sessions reset together with the data they protect.

## 2026-10-07 — Indeed stays an official search handoff until partner approval

**Decision:** OpportunityPilot may build a user-controlled link to the official Indeed search experience, but it does
not scrape Indeed, copy search results into its database, store Indeed cookies, or claim an application/message was
sent. In-app listings or employer/candidate actions require Indeed's written approval and the exact applicable partner
API credentials.

**Why:** Indeed's current public partner documentation exposes approved ATS/employer job and candidate workflows, not
an unrestricted public job-search feed. Its developer agreement requires approval and restricts scraping/database
copies. A truthful handoff gives Candidate and Sales users useful filters today without fabricating integration depth.

## 2026-10-08 — Live Indeed, LinkedIn and SEEK postings through a licensed aggregator (supersedes 2026-10-07 Indeed handoff)

**Decision:** Job discovery shows current postings published on Indeed, LinkedIn or SEEK by calling JSearch (OpenWeb
Ninja on RapidAPI, a Google-for-Jobs data service) from the server. A posting is shown only when it carries an https
link on that board's own domain; each row links there. Results are shown, not stored in the database; identical
searches are reused from memory for `Jsearch:CacheMinutes` (default 6 h). Nothing is applied to or sent. Without
`Jsearch__Key` the tabs say live listings are not set up and keep the official search link.

**Why:** the user asked for Indeed "with live data, like Wellfound". Checked 2026-10-08: Indeed's RSS returns 404 and
its search pages return 403 to non-browser clients; its Publisher API is closed. LinkedIn job/feed APIs and SEEK APIs
are partner-only. Scraping any of them breaks their terms, so the only live route without partner approval is a
licensed aggregator. The 2026-10-07 rule against copying results into a database still holds.

**Open:** whether showing aggregator-sourced Indeed/LinkedIn/SEEK postings is acceptable for this product's terms is the
user's call; JSearch's own terms govern the data. Not verified live until the key is set (TASKS U5).

## 2026-10-08 — Indeed is also a campaign source (W17)

**Decision:** a Job campaign can add an `Indeed` source. Each run searches JSearch (same server key and 6 h cache as the
Job discovery tabs) with the campaign's first 3 keywords in its first non-"Remote" location (only "Remote" → remote-only),
posted in the last month, keeps only postings with an https indeed.com link, and turns them into candidates that are
filtered, scored and suggested exactly like Greenhouse jobs (`JobPlatform.Indeed`; the user applies on Indeed). Each
search that is not served from the cache costs one fetch of the run budget. Without `Jsearch__Key` the source fails with
"Indeed needs the server's JSearch key". This supersedes "results are shown, not stored" for campaign runs only; the
Job discovery tabs still do not store.

**Why:** the user chose "Indeed as campaign source" (2026-10-08) so Indeed jobs reach Approvals and Applications.

**Extended (W18/W19, same day):** the source kind is `JobSearch` with the board on the source (`Indeed`, `LinkedIn`,
`Seek`). LinkedIn postings keep LinkedIn's own job id (`/jobs/view/{id}`), so they de-duplicate with the agent's finds
and a shortlisted one is applied to by the local agent; Indeed/SEEK jobs are applied to by the user. In Sales (non-Job)
campaigns the same search (keywords, then buying signals) yields one lead per hiring company, its postings as evidence.
The agent shortlist now lists only LinkedIn, Naukri and InstaHyre jobs (before, every non-Greenhouse/Lever/Adzuna job).

## 2026-10-08 — Sites that cannot be integrated

| Site | Why not | What the app does instead |
|---|---|---|
| Indeed profile (`profile.indeed.com`) | Personal page behind your Indeed login; no API for it | Live Indeed postings (above); you apply on Indeed |
| LinkedIn feed (`linkedin.com/feed`) | Behind your login; LinkedIn APIs are partner-only; reading it would be scraping | Live LinkedIn postings (above); the local agent applies through your own browser |
| Stellantis "thehub" (`idpm.stellantis.com`) | A private corporate sign-in portal (employee/supplier identity), not a job source; no public jobs or API | Nothing. Add Stellantis' public careers board as a campaign source if it uses a supported ATS |
| Upwork | Official GraphQL API (`api.upwork.com/graphql`, `marketplaceJobPostingsSearch`) needs an approved key and the Authorization Code grant (your Upwork account connected); job RSS feeds were retired in 2024 | Shown as *Not built yet* (W14). The client is built once the key is approved and the query can be tested (TASKS U6 → W16). Proposals/Connects are never spent by the app |

## 2026-10-14 — JD circle and LinkedIn notes run only in the person's own browser (JDC1, LI1)

**Decision:** the people around a lead (recruiters, hiring managers, team, the person's own connections) are read by the local
desktop agent from LinkedIn people search in the person's own logged-in browser, only for leads the person asked to map, and only
from the visible result cards (name, headline, profile link, degree). Approved `LinkedInMessage` drafts addressed to a profile link
are sent by the same agent: a dry run by default (fills the Connect → "Add a note" dialog or the message box, no final click);
real sends need `--submit` and `"iUnderstandAccountRisk": true`, are capped per day (config, default 10; server ceiling 25), stop at
any checkpoint or CAPTCHA, and go through the provider gateway (provider `LinkedInAgent`) so approval-version, suppression, quota and
at-most-once hold exactly as for mailbox sends. The circle request is two nullable columns on `opportunities`
(`CircleRequestedAt`, `CircleMappedAt`) rather than a table: one request per lead is all that is needed.

**Why:** the user asked for the people around a job and for notes that actually get sent. The API stays free of logged-in platform
automation (CLAUDE.md guardrail); the agent already holds the person's own LinkedIn session for Easy Apply. Unofficial automation can
restrict a LinkedIn account, hence dry-run default, low caps and the same explicit risk opt-in as applying. Not verified against
live LinkedIn: selectors follow LinkedIn's known structure and are proven only against the mock site.

