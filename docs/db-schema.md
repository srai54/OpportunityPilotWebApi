# Database schema

Source of truth: `src/OpportunityPilot.Infrastructure/Persistence/AppDbContext.cs` (shared model) and
`ProviderContexts.cs` (provider differences). This file must change in the same commit as any entity, mapping or
migration change. How to add a migration: [skills/migrations.SKILL.md](../skills/migrations.SKILL.md).

## Conventions that apply to every table

| Rule | Detail |
|---|---|
| Schema | Everything lives in schema `app`, including `__EFMigrationsHistory` (Supabase's Data API does not expose it by default) |
| Table names | snake_case plural, set explicitly with `ToTable` |
| Column names | EF default: the C# property name (PascalCase), e.g. `OwnerId` |
| Primary keys | `Id` GUID, generated in the domain constructor (`ValueGeneratedNever`), except `opportunity_evidence` (composite) |
| Ownership | Every user-owned table has `OwnerId` (GUID from the token's `sub`). There is no FK to a users table; Supabase owns identities |
| Enums | Stored as strings (`HasConversion<string>()`), max 32 characters. Renaming an enum member breaks stored rows |
| Times | UTC everywhere. SqlServer `datetime2` is read back with `DateTimeKind.Utc` (`UtcDateTimeConverter`); Postgres `timestamp with time zone` |
| JSON columns | `jsonb` on Postgres, `nvarchar(max)` on SqlServer, text in InMemory. Never empty: default `{}` or `[]` |
| Concurrency | Optimistic: a property marked `IsConcurrencyToken` (no rowversion). The domain increments it on each change |
| Index names | EF default `IX_<table>_<columns>`; the one hand-named index is `UX_research_jobs_active_campaign` |

Type notation below: `uuid`, `string(n)` (Postgres `character varying(n)` / SqlServer `nvarchar(n)`, or `nvarchar(max)` when n > 4000),
`int`, `bool`, `time` (UTC timestamp), `json`. "null" marks a nullable column; everything else is NOT NULL.

## Tables

### profiles — `Domain/Profiles/Profile.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| Type | string(32) | `ProfileType`: Product, Business, Candidate, Services |
| Name | string(200) | trimmed |
| StructuredDataJson | json | any JSON object, ≤32 KB (checked in `ProfileService`) |
| Version | int | concurrency token; 1 after create, +1 per update |
| ConfirmedAt | time, null | set to the save time when the request says `confirmed: true`, cleared otherwise |
| CreatedAt, UpdatedAt | time | |

Indexes: `(OwnerId, UpdatedAt)`.

### profile_versions — `Domain/Profiles/ProfileVersion.cs`

Immutable readable snapshots of an owned profile: profile id/version, type, name, structured JSON, confirmation time and
creation time. Unique `(OwnerId, ProfileId, Version)`; profile FK cascades.

### job_applications — `Domain/Applications/JobApplication.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| Platform | string(32) | `ApplicationPlatform`: LinkedIn, Naukri, Instahyre |
| ExternalJobId | string(100) | the platform's job id, trimmed |
| JobUrl | string(1000) | |
| Title, Company | string(300) | |
| Location | string(200), null | |
| Status | string(32) | `ApplicationStatus`: Applied, DryRun, NeedsManual, Skipped, Failed |
| Detail | string(1000), null | |
| OccurredAt | time | when the agent acted |
| CreatedAt, UpdatedAt | time | |

Indexes: unique `(OwnerId, Platform, ExternalJobId)`; `(OwnerId, OccurredAt)`. No concurrency token.

### agent_keys — `Domain/Agents/AgentKey.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| Name | string(100) | |
| KeyHash | string(64) | lowercase hex SHA-256 of the key; the key itself is never stored |
| Prefix | string(12) | first 12 characters of the key, for display |
| CreatedAt | time | |
| LastUsedAt, RevokedAt | time, null | revocation keeps the first time |
| Scopes | int | `AgentKeyScope` flags (stored as a number, not a string): Research 1, Shortlist 2, Applications 4, People 8; default and "every scope" = 15. Migration AddJdCircle moved keys stored with 7 (every scope before People existed) to 15 |

Indexes: unique `KeyHash`; `OwnerId`.

### campaigns — `Domain/Campaigns/Campaign.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| ProfileId | uuid | FK → profiles, **Restrict** |
| Mode | string(32) | `OpportunityMode`; fixed at creation |
| Name | string(200) | |
| Goal | string(2000) | empty string when not given |
| CriteriaJson | json | `CampaignCriteria`, camelCase |
| WeightsJson | json | `{ "<criterionKey>": int }`, sums to 100 |
| ResultLimit | int | 1–100, default 25 |
| AutoSuggestMinScore | int, null | Job-only approval threshold, 1–100; null disables auto-suggest |
| Version | int | concurrency token |
| CreatedAt, UpdatedAt | time | |

Indexes: `(OwnerId, CreatedAt)`; `ProfileId`.

### campaign_schedules — `Domain/Automation/CampaignSchedule.cs`

One owner-scoped schedule per campaign with time zone, cadence minutes, next run, pause flag, lease, last queued time,
safe error, timestamps and concurrency version. Unique `(OwnerId, CampaignId)` and processor index
`(Paused, NextRunAt)`; campaign FK cascades.

### sources — `Domain/Research/Source.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| CampaignId | uuid | FK → campaigns, Cascade |
| Kind | string(32) | `SourceKind`: Paste, Csv, Url, Feed, Agent, Greenhouse, Lever, Adzuna, Ashby, SmartRecruiters, Recruitee, Workable, JobSearch, Workday, Remotive, RemoteOk, Arbeitnow, Jobicy, WeWorkRemotely, TheMuse (new members need no migration: the column is a string) |
| Label | string(200) | |
| Url | string(1000), null | Url and Feed only |
| Text | string(50000), null | Paste only (SqlServer `nvarchar(max)`) |
| PermissionNote | string(500), null | |
| Platform | string(32), null | `JobPlatform`; set on Agent source rows; fetched board candidates carry their platform into opportunities |
| Status | string(32) | `SourceStatus`: Pending, Ok, Failed, Skipped |
| LastFetchedAt | time, null | |
| SafeError | string(500), null | user-facing reason; cleared on Ok |
| ItemCount | int | rows (Csv/Agent), parsed postings (Paste) or entries found (Url/Feed) |
| CreatedAt | time | |

Indexes: `(OwnerId, CampaignId, CreatedAt)`; `CampaignId`.

### source_items — `Domain/Research/SourceItem.cs`

Rows of Csv and Agent sources.

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| SourceId | uuid | FK → sources, Cascade |
| ExternalId | string(100), null | platform job id (Agent) or CSV `id` column |
| Title | string(300) | job title, or company name for Customer rows |
| Organization | string(300) | empty string when unknown |
| Location | string(200), null | |
| Url, Website | string(1000), null | |
| Description | string(20000), null | SqlServer `nvarchar(max)` |
| Country | string(100), null | |
| Industry | string(200), null | |
| CreatedAt, UpdatedAt | time | |

Indexes: unique **filtered** `(SourceId, ExternalId) WHERE ExternalId IS NOT NULL`; `(SourceId, UpdatedAt)`.

### import_batches — `Domain/Research/ImportBatch.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| CampaignId | uuid | FK → campaigns, Cascade |
| RowsJson | json | `ImportRowDto[]` (every row with its errors), camelCase |
| CreatedAt, ExpiresAt | time | expires 1 hour after creation |
| Committed | bool | **concurrency token**: two simultaneous commits cannot both succeed |

Indexes: `(OwnerId, CreatedAt)`; `CampaignId`.

### research_jobs — `Domain/Research/ResearchJob.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| CampaignId | uuid | FK → campaigns, Cascade |
| State | string(32) | `ResearchJobState` |
| Stage | string(32) | `ResearchStage` |
| CountsJson | json | `ResearchCounts`, camelCase |
| ProfileVersion, CampaignVersion | int | exact input versions captured when queued |
| ProfileSnapshotJson, CriteriaSnapshotJson | json | reproducible bounded inputs captured when queued |
| Attempts | int | number of claims |
| LeaseUntil | time, null | |
| CancelRequested | bool | |
| CreatedAt | time | |
| StartedAt, FinishedAt | time, null | |
| SafeError | string(500), null | |
| Version | int | concurrency token; starts at 1 |

Indexes: `(OwnerId, CampaignId, CreatedAt)`; `(State, CreatedAt)` (processor poll);
unique **filtered** `UX_research_jobs_active_campaign` on `CampaignId WHERE State IN ('Queued','Running')`.

### research_events — `Domain/Research/ResearchEvent.cs`

Not owner-stamped; read only through its job.

| Column | Type | Notes |
|---|---|---|
| Id | uuid | |
| JobId | uuid | FK → research_jobs, Cascade |
| At | time | strictly increasing within a run |
| Stage | string(32) | |
| Level | string(32) | `EventLevel`: Info, Warning, Error |
| Message | string(500) | safe summary, truncated with "…" |

Indexes: `(JobId, At)`.

### evidence — `Domain/Research/Evidence.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| CampaignId | uuid | FK → campaigns, Cascade |
| SourceId | uuid | **no FK on purpose**: deleting a source keeps the evidence behind existing scores |
| Url | string(1000), null | |
| RetrievedAt | time | |
| ContentHash | string(64) | lowercase hex SHA-256 of the stored excerpt |
| Excerpt | string(2000) | |
| ExtractionMethod | string(32) | currently always `Rules` |

Indexes: `(CampaignId, SourceId, ContentHash)` (not unique); `OwnerId`.

### opportunities — `Domain/Opportunities/Opportunity.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| CampaignId | uuid | FK → campaigns, Cascade |
| Mode | string(32) | copied from the campaign |
| DedupeKey | string(400) | see [business-rules.md](business-rules.md#dedupe-and-upsert) |
| Title, Organization | string(300) | Organization is empty when unknown |
| Location | string(200), null | |
| Url, ApplyUrl | string(1000), null | a URL longer than 1000 is dropped, not cut |
| Platform | string(32), null | `JobPlatform` |
| ExternalId | string(100), null | |
| Description | string(4000), null | |
| Score, Coverage | int | 0–100 |
| Outcome | string(32) | `FilterOutcome` |
| OutcomeReason | string(500), null | |
| Status | string(32) | `OpportunityStatus` |
| BreakdownJson | json | `BreakdownRow[]` |
| FactsJson | json | `FactRow[]` |
| GapsJson | json | `string[]` |
| GapsCount | int | denormalised from GapsJson for list queries |
| LastResearchJobId | uuid, null | no FK |
| CreatedAt, UpdatedAt | time | |
| Version | int | concurrency token |
| CircleRequestedAt | time, null | JD circle: when the person last asked the desktop agent to map the people around this lead. Asking again while a request waits keeps the first time. Not a pipeline change: `Version` is not bumped |
| CircleMappedAt | time, null | when the agent last delivered a circle (an empty one counts). Waiting = requested and (`CircleMappedAt` null or earlier than `CircleRequestedAt`) |

Indexes: unique `(CampaignId, DedupeKey)`; `(OwnerId, CampaignId, Score)`; `(OwnerId, Status, UpdatedAt)`.

### circle_people — `Domain/Circle/CirclePerson.cs`

The people around a job or company lead (JD circle). Only what a LinkedIn search card shows is stored — no email or phone.

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| OpportunityId | uuid | FK → opportunities, Cascade |
| Name | string(200) | |
| Headline | string(300), null | the person's headline or title as the card showed it (`title` in the API) |
| ProfileUrl | string(400) | always `https://www.linkedin.com/in/{slug}` (`LinkedInProfileUrl`): host, scheme, query and trailing path normalised; the slug keeps its case (LinkedIn's `ACoAA…` ids are case-sensitive) |
| Relation | string(32) | `CircleRelation`: Recruiter, HiringManager, Team, Connection, Other |
| Degree | int, null | 1, 2 or 3 (3rd+) connection degree; null when unknown |
| Source | string(32) | `CircleSource`: LinkedInAgent, Hunter, Apollo, Manual. A later agent delivery never changes the relation of a Manual row |
| FoundAt, UpdatedAt | time | |

Indexes: unique `(OwnerId, OpportunityId, ProfileUrl)` (case-sensitive on Postgres; `CircleService` also compares links case-insensitively, and InMemory relies on that check); `OpportunityId` (FK).

### job_interviews — `Domain/Interviews/JobInterview.cs`

Interviews the candidate records for a job lead they applied to (Candidate workspace). Typed in by the person; nothing is read from a calendar.

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| OpportunityId | uuid | FK → opportunities, Cascade; a `Job` mode lead only (checked in `JobInterviewService`) |
| Round | int | 1–20; defaults to the next round for that job |
| ScheduledAt | time, null | UTC; null while the time is not agreed |
| MeetingUrl | string(1000), null | http(s) only |
| Notes | string(2000), null | |
| Status | string(16) | `JobInterviewStatus`: Scheduled, Completed, NoShow, Cancelled. Completed/NoShow need a time |
| Version | int | concurrency token |
| CreatedAt, UpdatedAt | time | |

Indexes: `(OwnerId, OpportunityId, Round)`, `(OwnerId, ScheduledAt)`, `OpportunityId` (FK).

### opportunity_evidence — `Domain/Opportunities/Activity.cs` (`OpportunityEvidence`)

| Column | Type | Notes |
|---|---|---|
| OpportunityId | uuid | PK part; FK → opportunities, Cascade |
| EvidenceId | uuid | PK part; FK → evidence, **Restrict** (SqlServer rejects two cascade paths from campaigns) |

Indexes: PK `(OpportunityId, EvidenceId)`; `EvidenceId`. Not owner-stamped.

### activities — `Domain/Opportunities/Activity.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| OpportunityId | uuid | FK → opportunities, Cascade |
| Kind | string(32) | plain string, not an enum: `StatusChanged`, `Applied`, `Researched` (`ActivityKinds`) |
| OccurredAt | time | |
| Detail | string(500) | truncated with "…" |

Indexes: `(OpportunityId, OccurredAt)`; `OwnerId`.

### outreach_drafts — `Domain/Drafts/OutreachDraft.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| OpportunityId | uuid | FK → opportunities, Cascade |
| Channel | string(32) | `DraftChannel`; CoverNote is the implemented generator |
| Recipient | string(320), null | for `LinkedInMessage` either a name or a LinkedIn profile link, stored as `https://www.linkedin.com/in/{slug}`; only drafts with a link reach the agent's LinkedIn outbox |
| RecipientVerified | bool | false for the current CoverNote flow |
| Subject | string(300), null | |
| Body | string(10000) | exact user-editable draft text |
| Version | int | concurrency token; starts at 1 |
| State | string(32) | Draft or Approved |
| ApprovedHash | string(64), null | SHA-256 hex of exact approved content |
| ApprovedVersion | int, null | |
| ApprovedAt | time, null | |
| Source | string(32) | Template or Gemini (current generator is Template) |
| ClaimsJson | json | `{ text, basis, evidenceId? }[]` |
| CreatedAt, UpdatedAt | time | |

Indexes: unique `(OpportunityId, Channel)`; `(OwnerId, OpportunityId, UpdatedAt)`; `(OwnerId, State, UpdatedAt)`.

### suppressions — `Domain/Outreach/Suppression.cs`

Owner-scoped normalized recipients that block approval. Unique `(OwnerId, NormalizedRecipient)`.

| Column | Type / rule |
|---|---|
| Id, OwnerId | uuid |
| NormalizedRecipient | required, 320; trimmed lower-case |
| Reason | required storage, 200 |
| CreatedAt | UTC |

### next_actions — `Domain/Outreach/NextAction.cs`

In-app reminders only; no notification delivery is implied.

| Column | Type / rule |
|---|---|
| Id, OwnerId | uuid |
| OpportunityId | FK → opportunities, Cascade |
| Kind | FollowUp, CheckStatus, Call, Other |
| Note | 500 |
| DueAt, CreatedAt, CompletedAt | UTC |
| TimeZone | required IANA/browser zone, 100 |
| State | Open, Done, Cancelled |

### upwork_opportunities — `Domain/Sales/UpworkOpportunity.cs`

Owner-scoped Upwork research and decision queue. It stores visible job facts and the Connects snapshot without claiming
a proposal was submitted. Unique `(OwnerId, ProviderJobId)` makes repeated assisted/API imports idempotent.

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| ProviderJobId | string(200) | stable Upwork job reference |
| Title | string(300) | |
| Url | string(1000) | provider URL |
| Summary | string(8000), null | visible job summary |
| Location | string(200), null | |
| BudgetType | string(32) | Unknown, FixedPrice or Hourly |
| BudgetMin, BudgetMax | decimal(18,2), null | visible range/value only |
| Currency | string(3), null | |
| ExperienceLevel | string(100), null | |
| ConnectsRequired, AvailableConnectsAtReview | int, null | non-negative snapshot; required value may later change |
| PaymentVerified | bool, null | visible client signal |
| PostedAt | time, null | |
| ObservedAt | time | when facts were observed/imported |
| EvidenceJson | json | additional visible evidence; never credentials/cookies |
| State | string(32) | Saved, Shortlisted, Dismissed or Promoted |
| SalesProjectId | uuid, null | FK → sales_projects, SetNull |
| Version | int | concurrency token |
| CreatedAt, UpdatedAt | time | |

Indexes: unique `(OwnerId, ProviderJobId)`; `(OwnerId, State, ObservedAt)`; `SalesProjectId`.

### sales_projects — `Domain/Sales/SalesProject.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| Source | string(32) | `Upwork`, `Freelancer`, `TenderFeed`, `PublicUrl` or `Manual` |
| ExternalId | string(200), null | Provider id; unique per owner/source when present |
| Title | string(300) | |
| Buyer | string(300), null | |
| Description | string(8000), null | |
| Url | string(1000), null | |
| DeadlineUtc | time, null | |
| State | string(32) | `New`, `Shortlisted`, `BidPrepared`, `BidApproved`, `BidPlaced`, `ManualHandoff`, `Dismissed` |
| EvidenceJson | json | Evidence references; no inferred claims |
| Version | int | concurrency token |
| CreatedAt, UpdatedAt | time | |

Indexes: unique `(OwnerId, Source, ExternalId)` when `ExternalId` is present; `(OwnerId, UpdatedAt)`.

### wellfound_jobs — `Domain/Wellfound/WellfoundJob.cs`

Shared normalized store for Candidate discovery imports/demo records and recruiter-owned jobs. It contains provider id,
scope, title/company, location/work mode, salary/currency, equity range, experience/employment, industry/funding/company
size, visa signal, posted/apply data, summary, skills/evidence JSON, match score, state, explicit `IsDemo`, optimistic
version and timestamps. Unique `(OwnerId, ProviderJobId)`; queue index `(OwnerId, Scope, State, PostedAt)`.

### wellfound_applications — `Domain/Wellfound/WellfoundApplication.cs`

Recruiter-visible applications linked to `wellfound_jobs` (Cascade): stable provider application id, candidate display
name, optional fit score, lifecycle state, evidence JSON, explicit demo marker, version and timestamps. Unique
`(OwnerId, ProviderApplicationId)` and queue index `(OwnerId, State, UpdatedAt)`.

### wellfound_activities — `Domain/Wellfound/WellfoundApplication.cs`

Immutable audit rows for imports, local job/application decisions and provider-confirmed sync observations. Optional job
and application FKs use Restrict; `ProviderConfirmed` prevents demo/local actions from being reported as Wellfound
actions. Indexed by `(OwnerId, OccurredAt)`.

### sales_bids — `Domain/Sales/SalesBid.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | |
| ProjectId | uuid | FK → sales_projects, Cascade |
| Amount | decimal(18,2) | User-supplied; never inferred |
| Currency | string(3) | ISO alphabetic code |
| DeliveryDays | int | User-supplied, 1–3650 |
| Proposal | string(10000) | exact versioned text |
| ClaimsJson | json | claim basis/evidence |
| Version | int | concurrency token; starts at 1 |
| State | string(32) | Draft, Approved, Placed or Failed |
| ApprovedHash | string(64), null | SHA-256 of exact amount, timing, text and version |
| ApprovedVersion, ApprovedAt | int/time, null | |
| CreatedAt, UpdatedAt | time | |

Indexes: `(OwnerId, ProjectId, UpdatedAt)`; `ProjectId`.

### staffing_accounts — `Domain/Staffing/StaffingAccount.cs`

Owner-scoped prospective or active client companies. Name is required; domain is normalized lower-case when supplied.
Source records whether the identity came from Manual, Import, PublicWeb, SalesIntelligence, Upwork, Freelancer, Tender
or Referral. Optional industry, location and source reference remain attributable to that source. `Version` is a
concurrency token. Indexes: `(OwnerId, UpdatedAt)` and `(OwnerId, Domain)`.

### staffing_contacts — `Domain/Staffing/StaffingContact.cs`

Owner-scoped buyers/stakeholders linked to `staffing_accounts` (Cascade). Stores name, optional title/email/LinkedIn URL,
an evidence/source note and explicit `EmailVerified`; no provider identity is invented. `Version` is a concurrency token.
Index: `(OwnerId, AccountId, Name)`.

### staffing_deals — `Domain/Staffing/StaffingDeal.cs`

Commercial staffing opportunities linked to an account (Cascade) and optional contact (Restrict). Stores source,
external reference, estimated value/currency, enforced `StaffingDealStage`, optional pre-hold stage, next action/due time,
timestamps and concurrency `Version`. Indexes: `(OwnerId, Stage, UpdatedAt)` and
`(OwnerId, Source, ExternalReference)`.

### staffing_deal_activities — `Domain/Staffing/StaffingDealActivity.cs`

Immutable owner-scoped deal facts: Created, StageChanged, DetailsChanged, NoteAdded, ManualActionConfirmed or
ProviderReceiptRecorded. Detail is required and limited to 2000 characters; deal FK cascades. Index:
`(OwnerId, DealId, OccurredAt)`.

### guest_sessions — `Domain/Auth/GuestSession.cs`

| Column | Type | Notes |
|---|---|---|
| Id, OwnerId | uuid | Random server-generated ids |
| TokenHash | string(64) | Unique lowercase SHA-256; the opaque `opg_…` token is never stored |
| CreatedAt, ExpiresAt | time | 30-day lifetime |

Indexes: unique `TokenHash` for authentication lookup; `ExpiresAt` for bounded cleanup.

## Delete behaviour

| From | To | Behaviour |
|---|---|---|
| campaigns | sources, import_batches, research_jobs, evidence, opportunities | Cascade |
| sources | source_items | Cascade (and deleted explicitly by `SourceService`, because InMemory runs no cascades) |
| research_jobs | research_events | Cascade |
| opportunities | opportunity_evidence, activities, outreach_drafts, next_actions, circle_people, job_interviews | Cascade |
| sales_projects | sales_bids | Cascade |
| sales_projects | upwork_opportunities | SetNull |
| wellfound_jobs | wellfound_applications | Cascade |
| wellfound_jobs / wellfound_applications | wellfound_activities | Restrict |
| staffing_accounts | staffing_contacts, staffing_deals | Cascade |
| staffing_contacts | staffing_deals | Restrict |
| staffing_deals | staffing_deal_activities | Cascade |
| evidence | opportunity_evidence | Restrict |
| profiles | campaigns | Restrict |
| profiles | profile_versions | Cascade |
| campaigns | campaign_schedules | Cascade |

No endpoint deletes profiles, campaigns or opportunities today (see [open-questions.md](open-questions.md)).

## Provider differences

| Concern | SqlServer (`SqlServerAppDbContext`, local LocalDB) | Postgres (`PostgresAppDbContext`, Supabase and tests) | InMemory (`InMemoryAppDbContext`, demo mode) |
|---|---|---|---|
| JSON columns | `nvarchar(max)` | `jsonb` (research, draft, sales, Upwork and Wellfound evidence/skills JSON properties) | text |
| Strings > 4000 | `nvarchar(max)` (Source.Text, SourceItem.Description) | `character varying(n)` | — |
| DateTime | `datetime2` + UTC converter | `timestamp with time zone` | — |
| Filtered unique indexes | `[ExternalId] IS NOT NULL`, `[State] IN (N'Queued', N'Running')` | `"ExternalId" IS NOT NULL`, `"State" IN ('Queued', 'Running')` | **not enforced** |
| Unique indexes, FKs, cascades | enforced | enforced | **not enforced**: services check duplicates and delete children themselves |
| Migrations | `Persistence/Migrations/SqlServer` | `Persistence/Migrations/Postgres` | none (`EnsureCreated` is not used either; the model is used directly) |

## Migrations

Both sets must contain the same logical migrations in the same order.

| # | Name | SqlServer file | Postgres file | Creates |
|---|---|---|---|---|
| 1 | InitialProfiles | `20260930120109_InitialProfiles` | `20260930120115_InitialProfiles` | schema `app`, profiles |
| 2 | AddApplicationsAndAgentKeys | `20261001065453_AddApplicationsAndAgentKeys` | `20261001065506_AddApplicationsAndAgentKeys` | job_applications, agent_keys |
| 3 | AddResearchPipeline | `20261001105144_AddResearchPipeline` | `20261001105153_AddResearchPipeline` | campaigns, sources, source_items, import_batches, research_jobs, research_events, evidence, opportunities, opportunity_evidence, activities |
| 4 | AddAutoSuggest | `20261005062559_AddAutoSuggest` | `20261005062613_AddAutoSuggest` | nullable `campaigns.AutoSuggestMinScore` |
| 5 | AddOutreachDrafts | `20261006100640_AddOutreachDrafts` | `20261006100654_AddOutreachDrafts` | outreach_drafts |
| 6 | AddSalesPipeline | `20261006105733_AddSalesPipeline` | `20261006105746_AddSalesPipeline` | sales_projects, sales_bids |
| 7 | AddPersistentGuestSessions | `20261006123942_AddPersistentGuestSessions` | `20261006123947_AddPersistentGuestSessions` | guest_sessions |
| 8 | AddOutreachFollowUps | `20261006182426_AddOutreachFollowUps` | `20261006182652_AddOutreachFollowUps` | suppressions, next_actions, evidence excerpt 8000 |
| 9 | AddAutomationSnapshotsAndStaffingCrm | `20261007053856_AddAutomationSnapshotsAndStaffingCrm` | `20261007053903_AddAutomationSnapshotsAndStaffingCrm` | profile_versions, campaign_schedules, research input snapshots, staffing accounts/contacts/deals/activities |
| 10 | AddProviderResearchQueues | `20261007103650_AddProviderResearchQueues` | `20261007103644_AddProviderResearchQueues` | upwork_opportunities, wellfound_jobs, wellfound_applications, wellfound_activities |
| 11–21 | (see the migration folders) | | | staffing pipeline, schedules, security events and key scopes, AI usage, provider executions, mailboxes, user events, progress links, auto-approve, integrations |
| 22 | AddJdCircle | `20261014055510_AddJdCircle` | `20261014055528_AddJdCircle` | circle_people; nullable `opportunities.CircleRequestedAt`/`CircleMappedAt`; `agent_keys.Scopes` default 7 → 15 and a data step moving stored 7 to 15 (Down strips the People bit and revokes People-only keys) |
| 23 | AddJobInterviews | `20261015062624_AddJobInterviews` | `20261015062641_AddJobInterviews` | job_interviews |

Persisted AI usage records from [M4_M5_CONTRACT.md](M4_M5_CONTRACT.md) remain unbuilt; suppressions and next actions are built.
