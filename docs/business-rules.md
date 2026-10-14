# Business rules

Every validation rule, limit, cut-off and scoring rule the code enforces, with the file that enforces it. Change the code
and this file together. State transitions live in [opportunity-lifecycle.md](opportunity-lifecycle.md); endpoint
shapes in [api-contracts.md](api-contracts.md).

## Profiles

Code: `Application/Profiles/ProfileService.cs`

| Rule | Value |
|---|---|
| Name | required, ≤200 characters after trimming |
| Type | Product, Business, Candidate or Services |
| Data | optional JSON **object** (any shape), ≤32 KB UTF-8; stored as given |
| Confirmation | `confirmed: true` stamps `confirmedAt` with the save time; any save without it clears `confirmedAt` |
| Edit | `expectedVersion` must equal the stored version, otherwise 409 |

## Analytics

Code: `Application/Analytics/AnalyticsService.cs`; detailed response semantics: [ANALYTICS_CONTRACT.md](ANALYTICS_CONTRACT.md).

| Rule | Value |
|---|---|
| Workspace | required, case-insensitive `Candidate` (Job mode) or `Sales` (Customer mode) |
| Window | `days` 1–365, default 30; applies to new opportunities, status/application activity and completed research jobs |
| Ownership | every query is scoped to the authenticated owner; another owner's records never contribute |
| Unknown values | `null`, never estimated (for example Sales contacted/responded until Outreach exists) |
| Rates | numerator / denominator, rounded to 4 decimals; `null` when the denominator is zero or either input is unknown |
| Funnel history | current status or stored status activity proves a stage was reached; later Dismissed/Closed does not erase an earlier reached stage |
| Fit histogram | 10 bands: 0–9 through 90–100; Qualified opportunities only |
| Sources | ordered by items read, maximum 20; qualified yield is deduplicated by opportunity |
| Candidate activity chart | 14 UTC calendar days, oldest first |

## Campaigns

Code: `Application/Campaigns/*`, `Domain/Campaigns/Campaign.cs`

| Rule | Value |
|---|---|
| Modes accepted | Job, Customer, Partner, Investor and Freelance; non-Job modes use the evidence-based business criteria/weights path |
| Mode | fixed at creation |
| Profile | must exist and belong to the caller (400 `profileId` otherwise) |
| Name / goal | name required ≤200; goal optional ≤2000 |
| Result limit | 1–100, default 25: caps **new** opportunities saved per run |
| Auto-suggest threshold | Job only; null = off, otherwise 1–100. A new Qualified result at or above it moves New → Suggested |
| Criteria lists | each list ≤50 entries, each entry ≤100 characters; trimmed; blanks dropped; case-insensitive duplicates dropped |
| Work modes | Remote, Hybrid, Onsite; input is canonicalised ignoring case, spaces and hyphens ("on-site" → Onsite) |
| Candidate years | 0–60, optional |
| Edit | `expectedVersion` required; validation errors (400) are reported before a stale version (409) |

### Weights

Code: `Application/Campaigns/CampaignWeights.cs`

| Mode | Criterion keys and defaults |
|---|---|
| Job | `mandatorySkills` 40, `experience` 20, `location` 20, `preferredSkills` 20 |
| Customer | `industry` 25, `problem` 30, `geography` 15, `signal` 20, `contactPath` 10 |

- `weights` omitted on create → defaults; omitted on edit → the stored weights are kept.
- Keys are matched case-insensitively; a key outside the mode is a 400; each value must be 0–100; a key left out counts as 0.
- All zero → 400. Otherwise values are scaled to integers summing to exactly 100 (largest remainder, ties broken by key).
- Stored JSON that cannot be read falls back to the defaults.

## Public job boards

Code: `Application/Research/Boards/*`, `Application/Sources/SourceService.cs`.

| Source | Input and behavior |
|---|---|
| Greenhouse, Lever, Ashby, SmartRecruiters, Recruitee, Workable | One company slug or trusted careers URL; normalized to `[a-z0-9-]{1,100}` so callers cannot redirect the fetcher to another host |
| Workday | One public `https://<tenant>.wd<N>.myworkdayjobs.com/<site>` careers URL (locale and job-detail suffix allowed); normalized to `<tenant>.wd<N>/<site>`. Login, account, foreign-host, credential and non-default-port URLs are rejected. At most 3 campaign keywords are searched; each search reads one bounded page and matching detail records |
| Adzuna | Up to 3 campaign keywords and first non-Remote location; needs server-side keys (`Adzuna__AppId`, `Adzuna__AppKey`) and fails safely when absent. The country index comes from the place (`AdzunaCountry`: in, gb, us, au, ca, de, fr, nl, sg, za, nz, pl, it, es, br, at, be, ch, mx; cities and two-letter codes recognised), India when none matches; a place that only names the country sends no `where`. The request URL carries the keys and is never logged, cached by or put in a message |
| Remotive, Remote OK, Arbeitnow, Jobicy, We Work Remotely | Board-wide public feed; no key or company slug; jobs kept when every word of one campaign keyword appears |
| The Muse | Free public API without word search (20 postings a page): the newest 3 pages (`ResearchOptions.TheMusePages`) are read, one request each within the run's fetch budget; a posting seen on two pages counts once; then kept by keyword like the other feeds. Its `location`/`category` filters are not used (exact Muse names only; a probe on 2026-10-13 gave the same postings for different places) |
| Find → All sites (`GET /api/v1/jobboards/feeds`) | Each feed's answer is kept 30 minutes (Himalayas not, it is searched by the words). Adzuna is in "all" only when its keys are set, is searched with the person's words in the country of their place and cached 30 minutes per country/words/place (at most 200 searches kept); named without keys it answers `NotConfigured` and sends nothing. Its results are not filtered again by words or place (Adzuna searched the full text; it returns only a snippet) |
| All fetched sources | Job campaigns only; safe-fetch address/redirect/size/time limits apply; share the run fetch and candidate budgets fairly |
| Apply path | LinkedIn/Naukri use the local agent; every public board exposes an external application URL for the user |

## Scoring

Code: `Application/Research/Rules/Scoring.cs`

| Rule | Detail |
|---|---|
| Criterion value | 1, 0.5, 0 or unknown (null), each with a reason |
| Applicable | a criterion is scored only when the user configured something for it **and** its weight is above 0 |
| Not applicable | left out entirely; its weight is redistributed proportionally over the applicable ones |
| Unknown | stays in, scores 0 and lowers coverage — never treated as a pass, never removed |
| Effective weight | `weight × 100 / Σ applicable weights` |
| Score | `Σ effective weight × value`, rounded half away from zero, clamped 0–100 |
| Coverage | `Σ effective weight of known criteria`, same rounding |
| Fraction rule | all found → 1; at least half → 0.5; fewer → 0 (`FractionValue`) |
| Nothing configured | score 0, coverage 0, empty breakdown |
| Evidence | every known criterion cites the candidate's single evidence row; every awarded point also carries the matching sentence or structured-field note (bounded to 300 characters) |

### Text matching

Code: `Application/Research/Rules/TextMatch.cs`

- Case-insensitive, on boundaries where a boundary is any non-letter/non-digit (so `c#`, `.net`, `node.js` work); inner spaces match any whitespace.
- Aliases (versioned with the code; editing them changes scores): `c#` = `csharp` = `c sharp`; `.net` = `dotnet` = `asp.net`; `javascript` = `js`; `typescript` = `ts`.

### Job rules

Code: `Application/Research/Rules/JobRules.cs`

| Criterion | Applicable when | 1 | 0.5 | 0 | Unknown |
|---|---|---|---|---|---|
| mandatorySkills | requiredSkills set | all found in title + text | ≥ half found | fewer | posting text empty |
| preferredSkills | preferredSkills set | all found | ≥ half | fewer | text empty |
| experience | candidateYears set | years inside the posted range | within 1 year outside it | further | no range found |
| location | locations or workModes set | a location matches (field or text; "Remote" matches a remote posting); with only workModes set, a detected mode | — | detected work mode not accepted, or a known location outside the list | location (or mode, when only modes are set) not stated |

- Experience parsing reads the first match of, in order: `3-5 years` / `3 to 5 yrs`; `5+ years`; `minimum|min.|at least 4 years`; `4 years (of) … experience` (a minimum). Values above 50 are ignored.
- Work mode: a structured Lever `workplaceType` wins; otherwise Hybrid wins, then Remote (`remote`, `work from home`, `wfh`, `work from anywhere`), then Onsite (`on-site`, `onsite`, `in office`, `work from office`, `wfo`); searched in location + title + text.
- Evidence sentences split only at punctuation followed by a capital/digit or at a line break, so dotted technology names such as `.NET` and `Node.js` are not truncated.
- Facts recorded: company, location, work mode, experience asked, required skills found, salary (currency or LPA pattern), source posting date and detected staffing-agency signal. "Company not stated" is a gap.

### Customer rules

Code: `Application/Research/Rules/CustomerRules.cs`

Text checked = title + industry + description.

| Criterion | Applicable when | 1 | 0.5 | 0 | Unknown |
|---|---|---|---|---|---|
| industry | industries set | any industry mentioned | — | text present, none mentioned | no description and no industry |
| problem | problems set | ≥2 mentioned, or the only configured one | 1 of several | none | no description and no industry |
| geography | locations set | location/country or text matches | — | known place outside the list | not stated |
| signal | signals set | any signal mentioned | — | **never** (absence is unknown, not negative) | none found |
| contactPath | **always** (nothing to configure) | website, a page link to contact/careers/jobs/partners or mailto, email, contact or careers URL, or "contact us" | — | — | none found |

## Hard filters and outcome

Code: `Rules/SharedRules.cs`, `JobRules.cs`, `CustomerRules.cs`

Each configured filter gives Pass, Fail or Unknown. **Any Fail → Excluded; else any Unknown → NeedsVerification; else Qualified.**
The outcome reason joins the failing (or unknown) reasons.

| Filter | Modes | Fail | Unknown |
|---|---|---|---|
| excludeKeywords | both | any keyword in title or text | — |
| excludeOrganizations | both | organisation contains an entry (case-insensitive substring) | organisation not stated |
| requiredSkills | Job | text present and **none** of the required skills appear | posting text empty |
| workModes | Job | detected mode not in the list | mode not detected |
| locations | both | known location matches none | location not stated |
| excludeStaffingAgencies | Job | body says `our client`, `staffing`, `recruitment agency`, `on behalf of our`, `C2H` or `contract to hire`, or company name matches staffing/consultancy/recruit/manpower/talent solutions | — (absence passes) |
| maxPostingAgeDays | Job | source date is older than 1–365 configured days | — (missing date passes) |

## Dedupe and upsert

Code: `Research/Candidates.cs`, `Research/ResearchRunner.cs`

| Rule | Detail |
|---|---|
| Job key | `job:{platform}:{externalId}` when both are known, else `normalised title|normalised organisation` |
| Customer key | website domain without `www.` when known, else the normalised organisation (or title) |
| Normalised | lowercase, every run of non-letters/digits → one space, trimmed; key cut to 400 characters |
| Same key twice in one run | the higher-scoring copy is kept |
| Existing opportunity | always re-scored and overwritten (title, facts, score, outcome…); **status never changes**; a score change adds a `Researched` activity |
| New opportunities | sorted Qualified → NeedsVerification → Excluded, then by score; only the first `resultLimit` are saved; the rest are counted in the Score event |
| Evidence | one row per candidate; reused when the same source yields the same excerpt (same SHA-256); the opportunity's evidence link is replaced by the latest |

## Research runs

Code: `Research/ResearchService.cs`, `Research/ResearchRunner.cs`, `Research/ResearchOptions.cs`

| Setting (`Research:*`) | Default | Allowed range (values outside are clamped) | Meaning |
|---|---|---|---|
| ProcessorEnabled | true | — | in-process worker on/off |
| PollSeconds | 2 | 1–60 | idle poll interval |
| MaxCandidates | 100 | 1–100 | candidates scored per run |
| MaxFetches | 50 | 1–50 | HTTP requests per run (redirect hops and retries count) |
| TimeoutSeconds | 10 | 1–30 | per request, body included |
| MaxBytes | 1 048 576 | 1024–1 048 576 | per response, after decompression |
| Concurrency | 2 | 1–4 | simultaneous fetches per process |

| Rule | Value |
|---|---|
| Queueing | needs ≥1 source; at most one Queued/Running job per campaign (a second request returns the active job) |
| Sources per campaign | ≤20 (manual sources and CSV commits; the agent source per platform is created without this check) |
| Order | sources oldest first; Csv/Agent rows newest first, only as many as the run still has room for |
| Lease | 2 minutes, renewed after every source; claim attempts per poll: 5 |
| Attempts | a Running job reclaimed after 3 claims is failed ("stopped after 3 interrupted attempts") |
| Cancel | checked before each source and after gathering; results so far are kept |
| Text bounds | candidate text ≤20 000 chars; evidence excerpt ≤2000; stored description ≤4000 |
| Page too thin | fewer than 200 characters of text → source Skipped with "NeedsManualInput: …" (login or JavaScript page) |
| Feeds | RSS 2.0 and Atom only, ≤100 entries, DTDs refused |
| Final state | Cancelled if cancelled; CompletedWithGaps if any source Failed; else Completed; Failed only on an unexpected error |
| Error backoff | the hosted processor waits 30 s after a polling error |

## Safe fetching

Code: `Infrastructure/Research/SafeFetcher.cs`, `FetchAddressPolicy.cs`

Not configurable: the address policy has one production implementation and no configuration binding.

| Rule | Detail |
|---|---|
| Scheme | https; http only when the environment is Development |
| URL shape | absolute, no user name or password, port 80 or 443, host present |
| Host names | `localhost` and `*.localhost` refused before DNS |
| Addresses | only public unicast. Refused: 0/8, 10/8, 100.64/10, 127/8, 169.254/16 (incl. metadata), 172.16/12, 192.0.0/24, 192.0.2/24, 192.88.99/24, 192.168/16, 198.18/15, 198.51.100/24, 203.0.113/24, 224/4 and above; IPv6 outside 2000::/3, 2001:db8::/32, 2001::/32 (Teredo), 6to4 of a refused IPv4; IPv4-mapped forms |
| DNS | resolved inside the connect callback; any refused address in the answer refuses the host; the socket connects to the checked address and the peer is checked again (no rebinding) |
| Proxy, cookies | never used |
| Redirects | followed by hand, max 5, every hop re-checked (an http hop outside Development is refused) |
| JSON POST | Workday public search uses the same address, timeout, retry and size rules; redirects are refused so a request body is never replayed to another address |
| Retries | 408, 429, 5xx, timeouts and network errors: 3 retries with 0.5 s / 1 s / 2 s backoff + up to 250 ms jitter |
| Content types | text/html, application/xhtml+xml, text/plain, application/rss+xml, application/atom+xml, application/xml, text/xml |
| Failures | returned as a safe reason (never thrown, never an exception message); 401/403 add "The page may need a login." |
| Logged-in sites | LinkedIn and Naukri are never fetched server-side; only the desktop agent reads them, in the user's own browser |

## Pasted text

Code: `Application/Sources/PasteParser.cs`

- ≤50 000 characters; at least one entry must parse.
- Entries are separated by a line that is exactly `---`. First non-empty line = title (Job) or name (Customer).
- `Company:`, `Location:`, `Website:`, `URL:`, `Country:`, `Industry:` lines (case-insensitive) set fields; first occurrence wins; other lines are the description.

## CSV import

Code: `Application/Imports/ImportService.cs`, `Csv.cs`

| Rule | Value |
|---|---|
| Size | ≤1 MB UTF-8, ≤1000 data rows, ≤50 columns, header row required |
| Parsing | RFC 4180 (quotes, doubled quotes, embedded commas/newlines, CRLF or LF); BOM and empty lines ignored; an unclosed quote rejects the file |
| Job columns | `title`, `company` required; `location`, `url`, `description`, `id` optional |
| Customer columns | `name` required; `website`, `country`, `industry`, `description` optional |
| Headers | case-insensitive; unknown and duplicate columns become warnings |
| Row errors | missing required value, value over the column limit (the source-item limits in [db-schema.md](db-schema.md)), non-http(s) `url`, unusable `website` (bare domains get `https://`), duplicate `id` in the file, extra non-empty values |
| Preview | returns the first 50 rows plus counts over all rows; nothing is imported |
| Commit | once, within 1 hour, only rows without errors, into a new Csv source (label default `CSV import yyyy-MM-dd HH:mm`, ≤200) |

## Export

Code: `Application/Opportunities/OpportunityService.cs`, `Imports/Csv.cs`

Top 5000 opportunities by score. Every cell is quoted; a cell starting with `=`, `+`, `-`, `@`, tab or CR gets a leading `'`.

## Desktop agent — server side

### Agent keys

Code: `Application/Agents/AgentKeyService.cs`

| Rule | Value |
|---|---|
| Format | `opk_` + 32 random bytes as unpadded base64url (47 characters) |
| Storage | SHA-256 hex only; plaintext returned once at creation; 12-character prefix shown afterwards |
| Limits | name required ≤100; ≤10 active keys per owner; presented keys over 100 characters or without `opk_` are rejected before hashing |
| Use | every accepted request stamps `lastUsedAt`; revoked keys are rejected |
| Scopes | `Research` (campaigns, postings), `Shortlist`, `Applications` (report), `People` (JD circle and LinkedIn outbox); omitted = all four. Keys that had all three scopes before People existed were moved to all four by migration AddJdCircle, so existing agents keep working. A missing scope answers 403 |

### Postings

Code: `Application/Agents/AgentResearchService.cs`

- Job campaigns only; platform LinkedIn or Naukri; 1–100 items per request.
- Item fields: externalId ≤100, title ≤300, company ≤300 (all required), location ≤200, description ≤20 000, url required, absolute http(s), ≤1000.
- One Agent source per (campaign, platform), created on first delivery; items upserted by externalId (last one in a batch wins).
- The source keeps at most 1000 rows; the least recently updated rows beyond that are deleted.
- `queueResearch: true` queues research as `POST …/research` does (returns the active job if one exists).

### Shortlist

Code: `Application/Agents/AgentResearchService.cs`

Job opportunities with status Shortlisted, a platform and an external id, optionally one platform; best score first, max 200;
URL is `applyUrl ?? url` (items with neither are left out). Excluded: any job the owner already has an **Applied** application
for (same platform and external id). `coverNote` is returned only when the opportunity has a CoverNote whose state,
approved version and SHA-256 content hash all still match.

### Outreach drafts and cover notes

Code: `Application/Drafts/DraftService.cs`, `Domain/Drafts/OutreachDraft.cs`

- One draft per channel per opportunity. CoverNote creation is limited to Job opportunities; every generated channel requires the campaign profile to be confirmed.
- The deterministic template uses the sourced role title, verified organization/skill facts, confirmed campaign years,
  and the confirmed profile's own offer/summary and availability text. Missing profile summary is shown as a placeholder.
- Drafts start at version 1. A material recipient/subject/body edit increments the version and clears approval.
- A non-empty recipient is checked against the owner's suppression list during create, edit, approval and provider execution. A recipient matching stored opportunity evidence is labelled verified with its evidence id; otherwise it remains explicitly user-entered and unverified.
- Approval is refused while the subject or body contains a bracketed `[placeholder]`. Sales bid proposals use the same placeholder guard.
- Imported provider/tender bids additionally require non-empty stored source evidence before approval; Manual bids require a user-provided project brief. Bid approval binds the project evidence context plus exact proposal, amount, currency, delivery and version, so an edit invalidates approval.
- Approval stores the version and SHA-256 of `id|version|channel|recipient|subject|body`; a mismatch is reported as Draft.
- The cross-opportunity inbox can filter by stored state, channel and campaign. Batch approval evaluates each selected id/version independently and returns approved, skipped or stale outcomes without approving an ineligible version.
- No outbound delivery occurs. A valid approved CoverNote is exposed to the desktop agent, which fills it only into an
  explicitly cover-letter-like free-text field. Unapproved, edited or revoked text is never returned to the agent.

### JD circle (JDC1)

Code: `Application/Circle/CircleService.cs`, `Domain/Circle/`

- The circle is the people around a job or company lead: recruiters, the likely hiring manager, people on the team, and the
  person's own connections at the company. Any lead mode; a lead without a company name cannot be requested (400).
- Mapping happens only when the person asks (`POST …/circle/request`), and only in the person's own logged-in LinkedIn browser
  through the desktop agent (`npm run agent -- circle linkedin`). The API never contacts LinkedIn.
- Status: NotRequested (never asked), Waiting (asked and nothing delivered since), Ready. Asking again while waiting changes nothing;
  asking after Ready queues a fresh mapping. A request is not a pipeline change (the opportunity's version and status stay).
- A profile link is accepted only as a LinkedIn member link and stored as `https://www.linkedin.com/in/{slug}` (any linkedin.com
  host, http/https, query, fragment and anything after the slug dropped; the slug keeps its case; ≤400). Company pages and other
  sites are refused.
- Stored per person: name ≤200, headline/title ≤300, profile link, relation (Recruiter, HiringManager, Team, Connection, Other),
  degree 1/2/3 or empty, source, found time. No email, phone or anything else from the profile.
- Agent delivery: ≤50 people per request, upserted by profile link (compared case-insensitively; the last duplicate in a request
  wins), the lead is marked mapped even when nobody was found, and a `Note` activity records the count. A person added by hand
  keeps the relation the person chose when the agent finds them again. At most 200 people per lead (400 beyond).
- By hand: add (same rules, 409 when the link is already there) and remove. Everything is owner-scoped; another owner's lead or
  person answers 404, also for the agent's key.

### LinkedIn outbox (LI1)

Code: `Application/Outreach/LinkedInOutbox.cs`, `Application/Outreach/ProviderGateway.cs`

- LinkedIn automation runs **only in the person's own logged-in browser** via the desktop agent. The API never contacts LinkedIn
  and stores no LinkedIn password or cookie.
- A `LinkedInMessage` draft may be addressed to a name (the person sends it themselves) or to a LinkedIn profile link (normalised
  as above; any other link is refused). Only drafts addressed to a profile link reach the agent.
- The outbox lists LinkedInMessage drafts that are Approved with a still-valid approval (exact version and SHA-256 content hash),
  addressed to a profile link, not suppressed, and with no pending or successful execution for the approved content; oldest
  approval first, max 50. `body` is the exact approved text; `fitsConnectionNote` says whether it fits LinkedIn's 300-character note.
- Every send goes through the provider gateway as provider `LinkedInAgent`: `start` checks the exact version, the approval, the
  suppression list, the gateway's daily quota (100 executions) and a LinkedIn ceiling of 25 agent sends per UTC day (failed ones do
  not count). The same approved content executes at most once: a second `start` returns the existing execution with `reused: true`
  and the agent must not send again. Only `confirm` with a receipt marks the draft Sent (and the lead Contacted, plus a follow-up);
  `fail` records the reason and lets a new attempt start. Confirm/fail without a pending agent send is 409.
- If the agent clicked Send but could not see LinkedIn confirm it, it leaves the execution pending (never fails it, so it cannot be
  sent twice); the person checks LinkedIn and confirms or fails it in the app.

### Third-party contact data (REACH2: Hunter, Apollo)

Code: `Application/Integrations/PeopleSources.cs`, `Application/Opportunities/PeopleFinder.cs`

- Only with the person's own Hunter or Apollo API key, connected in Connectors (checked against the real account first, stored encrypted,
  never returned or logged, sent only in a request header). Nothing is looked up without a connected key (409).
- The company domain comes from the lead's own website (Customer/Company leads) or a website the person passes; a Job lead's link is the
  job board, so without a website the request is refused with "Add the company website first.". IP addresses and single-label hosts are refused.
- Every person returned is third-party data labelled with its source (`source`, and a `note` saying where it came from and that
  OpportunityPilot did not check it). Hunter's confidence and Apollo's `email_status` are shown as the service's own opinion; the app never
  marks such an address verified. Unlike "Find a contact" (published addresses only), these may be addresses the service inferred.
- Nothing is stored: not the people, not the addresses. The only write is a `Note` activity (count, domain, service; never an address) and the
  connection's last-used time or last error. At most 10 people per lookup; at most 10 titles of ≤80 characters.
- Apollo search uses no credits and returns no emails; revealing one (`people/reveal`, Apollo `people/match`, personal emails not requested)
  uses the person's Apollo credits, and the response's `creditsNote` says so. A Hunter search that finds someone uses one of the plan's searches.
- If the person copies one of these addresses into a draft, the existing draft rule applies unchanged: it does not match stored evidence, so
  the draft shows it as `UserEntered` (typed, unverified).
- Upstream refusals (401 key refused, 429 limit used up, 403 key lacks the endpoint) become 409 with a plain reason; the reason never contains
  the key or the request URL. Rate limit: `connectors` (60/h per owner).

### AI rewrite of a draft (CON8)

Code: `Application/Drafts/DraftRewriteService.cs`, `Application/Drafts/DraftRewriteCheck.cs`

- Instruction is required, at most 200 characters; `shorter`, `friendlier` and `more formal` map to fixed guidance, anything else is passed as free text (treated as data, not as a source of facts).
- Refused with 409 for a stale version, a sent draft (reopen first) or a draft with a valid approval (revoke first). 404 for a missing or someone else's draft.
- Uses Gemini only when `Features:GeminiEnabled` is true and a key is set (else 409 "AI rewrite is off on this server."). It shares the daily budget with goal previews (`Ai:DailyCallLimit`, counted from `ai_usages`); a spent budget is 409 "Daily AI limit reached…" and records an `OverDailyLimit` row. There is never a template/fake rewrite.
- The model receives only the draft's subject and body, the texts of the draft's `Profile…` claims, the opportunity's non-inference facts (label: value) and the confirmed profile's sign-off name. Not the recipient, not the rest of the profile. It is told not to add claims, numbers, names, links or promises and to keep the sign-off name and `[placeholders]`.
- The answer is checked against that input: a new URL (`http(s)://` or `www.`) or email address drops the suggestion (409 "The rewrite added facts that are not in your profile…", usage recorded as `InvalidResponse`); a new number or a capitalised word not in the input (minus a list of common email words) becomes a `warnings[]` line (at most 10). LinkedIn messages over 300 characters also get a warning.
- Nothing is saved. The suggestion is applied by the normal `PUT /drafts/{id}`, so suppression, placeholder, version and approval rules all still apply.
- `GET /ai/status` reports `rewriteAvailable` = enabled, key set and today's budget not spent.

### Reply detection (CON5)

Code: `Application/Mail/ReplyCheckService.cs`, `Infrastructure/Mail/ImapClient.cs`, `Infrastructure/Mail/ImapMailboxReader.cs`, `Api/Hosting/ReplyCheckProcessor.cs`

- Works only with the email connected with an app password (`Smtp`); the same stored login reads the inbox over IMAP (implicit TLS, port 993).
  IMAP server: Gmail `imap.gmail.com`, Outlook.com and Microsoft 365 `outlook.office365.com`, Yahoo `imap.mail.yahoo.com`, a custom server
  its SMTP host with `smtp.` replaced by `imap.`. The IMAP host must resolve to public addresses only and the connection goes to exactly those
  addresses (`Mail:AllowPrivateSmtpHosts` lifts this, tests only).
- Read-only: `EXAMINE INBOX` and `BODY.PEEK` headers (From, Subject, Date, In-Reply-To, References, Message-ID, Auto-Submitted) plus the
  arrival time; nothing is marked read, moved or deleted; the body is never read. Messages since the UTC day of the oldest email sent in the
  last 30 days, newest 500 at most, 20 s for the whole read. A login containing a line break, NUL or a non-ASCII character is refused before
  connecting.
- Watched sends: email drafts with a `SentAt` in the last 30 days and a recipient. Without one, the inbox is not read (`checked: 0`).
- A message is a reply when its From address equals a watched recipient (case-insensitive) and it arrived after that send. It belongs to the
  latest send to that address before it arrived. Automatic answers (an `Auto-Submitted` other than `no`, or a subject starting "Automatic
  reply", "Auto:", "Autoreply", "Out of office"…) and mail from the person's own address are not replies.
- For each new reply: a `Replied` activity "Reply received from {from} on {yyyy-MM-dd HH:mm:ss} UTC: {subject}" at the arrival time; a lead
  in New, Suggested, Shortlisted, Applied or Contacted moves to Responded (a `StatusChanged` activity; Dismissed, Responded, Interested and
  Closed stay); its open FollowUp next actions become Cancelled (other kinds stay open), so no follow-up is sent after a reply.
- Idempotent: an activity with the same "Reply received from {from} on {time} UTC" start on that lead is not recorded again.
- Refused login: the mailbox becomes NeedsReconnect (sending stops until it is reconnected) and the check answers 409 "Your email provider
  refused the login for reading replies…". Outlook.com and Microsoft 365 hosts are the exception: Microsoft has turned off app-password (basic)
  IMAP sign-in for most of these accounts, so the message says replies cannot be checked with this connection and the mailbox stays Connected
  for sending. A stored login this server can no longer decrypt also sets NeedsReconnect.
- Background: `ReplyCheckProcessor` starts 5 minutes after start-up and runs every 30 minutes for owners with a Connected `Smtp` mailbox and an
  email sent in the last 30 days, at most 50 per pass, continuing after the last owner of the previous pass. One owner's failure is logged
  (never with the address, password or subject) and the pass moves on. New replies post the `replies` event to Slack, Teams or webhooks.
- The endpoint is rate limited by `reply-check`: 20 checks an hour per owner.

### Upwork research and Connects

- Research/import, deduplication, review and proposal drafting never require a paid Upwork plan or an available Connects balance.
- The queue stores only visible facts supplied by the user or an approved Upwork API/MCP integration. It never scrapes authenticated pages and never stores passwords, cookies or OAuth tokens.
- `connectsStatus` is `Unknown` unless both required and available Connects were observed together; otherwise it is `Sufficient` or `Insufficient` by direct comparison.
- Required Connects are a timestamped snapshot and may change on Upwork. Recheck immediately before submission.
- Only a human-shortlisted item can become a sales project. Insufficient Connects does not prevent drafting.
- Any real MCP submission requires a separate explicit confirmation at action time.

### Wellfound shared integration

- Public discovery reads only the anonymous `https://wellfound.com/jobs` page through the SSRF-safe bounded fetcher: no login, cookies, credentials or browser session are used.
- At most 100 recognizable cards are imported per refresh. Each row keeps the numeric Wellfound job id, exact job URL, observed timestamp and raw public-card evidence; missing fields stay null.
- A public refresh removes legacy demo jobs/applicants for that owner. Candidate filters and Sales hiring signals operate on the stored public observations.
- Wellfound discovery filters are owner-scoped and conjunctive: each supplied field must match. Comma-separated
  `techStack` terms are capped at 12 and every term must occur in the stored title, summary or skills JSON. An unknown
  provider fact does not pass a filter that requires that fact. `postedWithinDays` is clamped to 1–365.
- Sort values are bounded to newest (default), salary, company or match; an unknown sort falls back to newest. Result
  count remains capped at 200.
- The Recruit MCP is not represented as a global public-job feed: recruiter-owned jobs and applicants remain OAuth-only.
- Recruit and Reach use separate OAuth issuers/resources and least-privilege read scopes. Passwords, browser cookies and manually copied access tokens are never accepted.
- Live application or candidate write tools remain disabled until OAuth is connected, the exact current provider record is shown and the user confirms the action.
- Local Save/Applied decisions on public jobs update KPIs immediately and create immutable activities. A provider-confirmed activity is written only after a successful OAuth provider re-read/receipt.

### Reports

Code: `Application/Applications/ApplicationService.cs`

| Rule | Value |
|---|---|
| Items | 1–100; known platform and status; externalJobId ≤100, title ≤300, company ≤300 (required); location ≤200; detail ≤1000; jobUrl absolute http(s) ≤1000 |
| occurredAt | required; a time without offset is taken as UTC; more than 5 minutes in the future → 400 |
| Duplicates | last item per (platform, externalJobId) in a batch wins; rows upsert by (owner, platform, externalJobId) |
| Status precedence | see [opportunity-lifecycle.md](opportunity-lifecycle.md#job-application-status) |
| opportunityId | with status Applied, the caller's opportunity moves to Applied (+ `Applied` activity) **only if it is a Job opportunity the user shortlisted**; any other id (not owned, not shortlisted, Customer mode) is ignored, not rejected — the application row is still recorded |
| Concurrency | a simultaneous insert or version clash → 409 "send the report again" |

## Desktop agent — local (`agent/`)

| Rule | Where |
|---|---|
| Dry run is the default; `--submit` requires `"iUnderstandAccountRisk": true` | `agent/src/cli.ts` |
| Only the user's shortlist is ever applied to; `collect` only reads postings | `agent/src/run.ts`, `agent/src/collect.ts` |
| A job is never reopened once the local log has it as Applied, Skipped or NeedsManual (DryRun and Failed do not count) | `agent/src/store.ts` |
| Caps: `limits.maxApplicationsPerRun` (Applied + DryRun count), `limits.maxApplicationsPerDay` (submit mode, Applied today), `limits.maxPostingsPerCollect`, `limits.maxPages`; pause `limits.pauseSeconds` between jobs (collect pauses 30% of it) | `agent/src/run.ts`, `agent/src/collect.ts` |
| A run stops on: not logged in, a security check (LinkedIn `/checkpoint/` or `/challenge/`), the platform's daily limit message, the daily cap, three failures in a row | `agent/src/platforms/*.ts`, `agent/src/run.ts` |
| Form answers come only from `config.json` rules; uncovered required fields → NeedsManual. Defaults exist for experience years and Yes/No questions (see OQ-BE-004) | `agent/src/answers.ts`, `agent/src/form.ts` |
| Naukri dry run stops before Apply (Naukri submits on the first click) | `agent/src/platforms/naukri.ts` |
| Results are kept in `.data/applications.jsonl` and reported one by one; after the first failed report the run stops syncing and `sync` re-uploads later in batches of 100 | `agent/src/run.ts`, `agent/src/cli.ts` |

## Staffing CRM foundation

- Every account, contact, deal and activity is filtered by the authenticated owner's id; cross-owner access is 404.
- Account domains are trimmed/lower-cased. The service rejects a second owned account with the same supplied domain;
  multiple accounts without a domain are permitted.
- Contacts belong to the selected owned account. `EmailVerified` can be true only when an email is stored and does not
  assert deliverability beyond the supplied evidence.
- Estimated value cannot be negative. Currency, when supplied, is upper-cased and limited to a three-letter code.
- Deal updates and stage moves require the current `expectedVersion`; stale changes return 409.
- Deal stages follow the forward lifecycle in `opportunity-lifecycle.md`; terminal deals cannot reopen and an on-hold
  deal may resume only to its remembered prior stage.
- Creation, details changes, stage changes and notes create immutable activities. Notes are limited to 2000 characters.
- The foundation records no provider success. A later provider gateway must supply a receipt or explicit manual
  confirmation before moving a deal to an externally acted stage such as Contacted.
- LinkedIn URLs and source references are user/provider evidence only. The server does not fetch or scrape LinkedIn.

## Demo mode

Code: `Infrastructure/DependencyInjection.cs`, `Api/Auth/AuthSetup.cs`

| Missing setting | Behaviour |
|---|---|
| `ConnectionStrings__Main` (or a Postgres value that cannot be parsed) | EF InMemory store named `opportunitypilot-demo`; everything works, nothing survives a restart; migrations skipped; `/health/ready` reports ready |
| `Auth__SupabaseUrl` (or a non-https value) and dev bypass off | guest sign-in only |

- Each gap is listed in `capabilities.setupRequired`, sets `guestSignIn` / `temporaryStorage`, and is logged once at startup as `Demo mode: …`.
- InMemory enforces no unique index, FK or cascade: services must check duplicates and delete children explicitly.
- Setting the real value switches the fallback off; the guest endpoint then answers 404.

## Sign-in

Code: `Api/Auth/*`

| Rule | Value |
|---|---|
| Supabase JWT | issuer `{SupabaseUrl}/auth/v1`, audience `Auth:Audience` (default `authenticated`), signed, lifetime checked, 1 minute clock skew; keys from `{SupabaseUrl}/auth/v1/.well-known/jwks.json` (cached 10 minutes, refreshed for an unknown key id at most every 30 s) plus `Auth:LegacyJwtSecret` (HS256) when set |
| Guest token | Opaque `opg_` + 32 random bytes, 30 days; only its SHA-256 hash and random owner id are stored. A durable database keeps it valid across API restarts; in-memory data and sessions reset together |
| Dev bypass | Development only (startup throws elsewhere); `X-Dev-User` ≤100 characters → stable owner id from SHA-256 of `dev:` + lower-cased name |
| Owner id | the `sub` claim must be a non-empty GUID, otherwise 401 |

## Rate limit and request size

Code: `Api/Program.cs`, controllers

| Rule | Value |
|---|---|
| Global limiter | 120 requests per minute, fixed window, per `sub` claim of the default scheme, else per client IP; 429 when exceeded |
| Research quota | none beyond the run limits above |
| Body size | sources 256 KB, CSV preview 3 MB, agent postings 4 MB, others the Kestrel default |
| CORS | origins from `Cors:AllowedOrigins`; headers Authorization, Content-Type, X-Correlation-ID, X-Dev-User; methods GET, POST, PUT, PATCH, DELETE; exposes X-Correlation-ID |
