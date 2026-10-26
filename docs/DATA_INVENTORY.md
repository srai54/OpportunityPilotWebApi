# Data inventory and classification

What the API stores, how sensitive it is, who can see it and how long it is kept. Columns: [db-schema.md](db-schema.md).
Rules for people's data: [PRIVACY.md](PRIVACY.md). Updated 2026-10-26; the `docs-sync` skill keeps it current.

## Classes

| Class | Meaning | Handling |
|---|---|---|
| **Secret** | Credentials that act for someone | Encrypted (AES-256-GCM) or hashed at rest; never returned, logged or exported; removed with "Delete all data" |
| **Personal** | Identifies a person (name, email, phone, resume, journey) | Owner-scoped queries only; shared with a candidate or client only through what the owner ticks; exported and deleted on request |
| **Confidential** | Business data of the owner (deals, pay, rates, notes, scores) | Owner-scoped; never shown to a candidate or client |
| **Operational** | Logs, usage and error counts without content | Kept for a fixed period, then deleted by `RetentionService` |
| **Public** | Facts from public sources (job postings, company pages) with their source | Stored with the source URL as evidence |

## Tables

| Table(s) | Class | Contains | Visible to | Kept |
|---|---|---|---|---|
| `ai_credentials` | Secret | A person's own Anthropic API key (encrypted) and its last 4 characters | Nobody (the key is write-only) | Until removed or account deletion |
| `agent_keys` | Secret | Desktop agent keys (hashed) and scopes | Owner sees the prefix only | Until revoked |
| `guest_sessions` | Secret | Guest tokens (hashed) | — | 30 days |
| `candidate_progress_links` | Secret + Personal | Link token hash, what is shared, note | Owner; the candidate through the link | Until revoked or consent withdrawn |
| mailbox / integration connections | Secret | Refresh tokens or app passwords (encrypted) | Nobody | Until disconnected |
| `profiles`, `profile_versions` | Personal | The owner's own profile and resume text | Owner | Until deleted |
| `staffing_candidates`, `candidate_resumes`, `candidate_journey_entries`, `job_interviews`, `staffing_interviews` | Personal | Candidates' details, resumes, journey, interviews, consent | Owner; the candidate sees what the link shares | Until deleted; consent withdrawal stops sharing |
| `staffing_contacts`, `circle_people` | Personal | Client contacts and people found for a role (name, title, work email, phone) | Owner | Until deleted; suppressions honoured |
| `user_access` | Personal | Sign-in email and role | Admin | Until account deletion |
| `user_feedback` | Personal | Feedback message, rating, page, sign-in email | Admin | Until account deletion |
| `staffing_accounts`, `staffing_deals`, `staffing_deal_activities`, submissions, offers, proposals, `sales_projects`, `sales_bids` | Confidential | Clients, deals, rates, pay, notes, scores | Owner | Until deleted |
| `outreach_drafts`, `suppressions`, `next_actions`, `activities` | Confidential | Drafted messages, do-not-contact list, follow-ups | Owner | Until deleted |
| `campaigns`, `campaign_schedules`, `job_applications`, `wellfound_*`, `upwork_opportunities` | Confidential | Search criteria, applications and their states | Owner | Until deleted |
| `opportunities`, `evidence`, `opportunity_evidence`, `sources`, `source_items` | Public (+ owner's scores) | Postings and company facts with source URLs | Owner | Until deleted |
| `import_batches` | Confidential | Uploaded rows awaiting review | Owner | Until committed or expired |
| `research_jobs`, `research_events` | Operational | Research progress | Owner | Events 90 days |
| `ai_usage` | Operational | AI call outcome, duration, safe reason (never prompts or answers) | Owner (status) | 90 days |
| `security_events` | Operational | Sign-ins, refusals, key use | Admin / operator | 365 days |
| `client_errors` | Operational | Browser error message, page path, stack lines, build | Admin | 30 days after last seen |

## Data that leaves the system

See [THIRD_PARTIES.md](THIRD_PARTIES.md). In short: AI providers get only the text a request needs (never contact details, pay or
rates); job and company sources get search terms; mail goes from the owner's own mailbox after approval.
