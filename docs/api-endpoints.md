# API endpoints (generated)

Generated from `src/OpportunityPilot.Api/Controllers` by `python tools/endpoints.py`; CI fails when it is out of date.
Do not edit by hand. Request and response shapes: [api-contracts.md](api-contracts.md).

244 endpoints. Auth: `user` = any signed-in person (guests included unless the policy says otherwise),
`NotGuest` = signed-in account only, `anonymous` = no sign-in. Rate limit: the per-endpoint policy in `Hosting/RateLimits.cs`
(every request also counts against the global 120 a minute).

| Method | Path | Controller · action | Auth | Rate limit | Summary |
|---|---|---|---|---|---|
| DELETE | `/api/v1/account-data` | AccountData · Delete | user | — |  |
| GET | `/api/v1/account-data/export` | AccountData · Export | user | — |  |
| GET | `/api/v1/admin/client-errors` | Feedback · Errors | user | — |  |
| GET | `/api/v1/admin/feedback` | Feedback · List | user | — |  |
| GET | `/api/v1/admin/feedback/summary` | Feedback · Summary | user | — |  |
| PUT | `/api/v1/admin/feedback/{id}/status` | Feedback · Set status | user | — |  |
| GET | `/api/v1/admin/users` | Access · Users | user | — |  |
| PUT | `/api/v1/admin/users/{accountId}/role` | Access · Set role | user | — |  |
| GET | `/api/v1/agent-keys` | AgentKeys · List | user | — |  |
| POST | `/api/v1/agent-keys` | AgentKeys · Create | user | — | The response is the only time the plaintext key is returned. |
| GET | `/api/v1/agent-keys/status` | AgentKeys · Status | user | — | INT6: is the desktop agent running, what it applied to lately, and how many approved jobs wait for it. |
| DELETE | `/api/v1/agent-keys/{id}` | AgentKeys · Revoke | user | — |  |
| GET | `/api/v1/agent/campaigns` | AgentResearch · Campaigns | ResearchPolicy | — | The owner's Job campaigns, with the criteria the agent searches with. |
| POST | `/api/v1/agent/campaigns/{id}/postings` | AgentResearch · Postings | ResearchPolicy | — |  |
| POST | `/api/v1/agent/circle` | AgentPeople · Circle | PeoplePolicy | — |  |
| GET | `/api/v1/agent/circle-requests` | AgentPeople · Circle requests | PeoplePolicy | — | Leads the person asked the agent to map, oldest request first (max 20). |
| GET | `/api/v1/agent/linkedin-outbox` | AgentPeople · Outbox | PeoplePolicy | — |  |
| POST | `/api/v1/agent/linkedin-outbox/{draftId}/confirm` | AgentPeople · Confirm | PeoplePolicy | — |  |
| POST | `/api/v1/agent/linkedin-outbox/{draftId}/fail` | AgentPeople · Fail | PeoplePolicy | — |  |
| POST | `/api/v1/agent/linkedin-outbox/{draftId}/start` | AgentPeople · Start | PeoplePolicy | — |  |
| GET | `/api/v1/agent/shortlist` | AgentResearch · Shortlist | ShortlistPolicy | — | Shortlisted Job opportunities on the platform that have not been applied to. |
| GET | `/api/v1/ai/claude` | Claude · Status | NotGuest | — |  |
| PUT | `/api/v1/ai/claude` | Claude · Connect | NotGuest | ClaudeConnect | Tests the key with a free call to Anthropic, then stores it encrypted. 400 when Anthropic refuses it. |
| PATCH | `/api/v1/ai/claude` | Claude · Update | NotGuest | — |  |
| DELETE | `/api/v1/ai/claude` | Claude · Remove | NotGuest | — |  |
| GET | `/api/v1/ai/status` | Ai · Status | user | — |  |
| GET | `/api/v1/analytics/activity` | Analytics · Activity | user | — | The caller's own recorded actions, newest first. kinds = comma list; from/to = UTC range; before = paging cursor (the oldest `at` already shown); take 1–100, default 30. |
| GET | `/api/v1/analytics/overview` | Analytics · Overview | user | — | workspace Candidate (Job campaigns) or Sales (Customer campaigns); days 1–365 (default 30). |
| GET | `/api/v1/analytics/trend` | Analytics · Trend | user | — | Daily activity for the last 7–90 days (default 30) with the window before it, for the Insights trend chart. |
| GET | `/api/v1/applications` | Applications · List | user | — |  |
| POST | `/api/v1/applications/report` | Applications · Report | ApplicationsPolicy | — | Called by the local desktop agent only. User tokens are rejected here; agent keys work nowhere else. |
| GET | `/api/v1/applications/summary` | Applications · Summary | user | — |  |
| GET | `/api/v1/approvals` | Approvals · List | user | — | Suggested opportunities, highest score first; take 1–200 (default 100). |
| POST | `/api/v1/approvals/decide` | Approvals · Decide | user | — | Approve → Shortlisted, reject → Dismissed; ids that are not Suggested (or not yours) are skipped. |
| POST | `/api/v1/approvals/decide-all` | Approvals · Decide all | user | — |  |
| POST | `/api/v1/assistant` | Assistant · Ask | NotGuest | Assistant |  |
| POST | `/api/v1/auth/guest` | Auth · Guest | anonymous | GuestSignIn | A fresh random guest identity. 404 when guests are switched off (Auth:AllowGuests=false with accounts on). |
| GET | `/api/v1/calendar/{id}/{key}.ics` | Integrations · Calendar | anonymous | PublicProgress | Calendar apps fetch this without signing in; the long random key in the link is the permission, and disconnecting revokes it. |
| GET | `/api/v1/campaigns` | Campaigns · List | user | — |  |
| POST | `/api/v1/campaigns` | Campaigns · Create | user | — | Any OpportunityMode; an unknown mode returns 400. |
| POST | `/api/v1/campaigns/{campaignId}/drafts/prepare` | Drafts · Prepare | user | AiWrites | Sales hands-off, step one: write the email for every shortlisted lead that has a published address. Approving and sending stay separate, explicit steps. |
| GET | `/api/v1/campaigns/{campaignId}/export` | Opportunities · Export | user | — | Spreadsheet-safe CSV: every cell quoted, formula-like values prefixed with an apostrophe. |
| GET | `/api/v1/campaigns/{campaignId}/opportunities` | Opportunities · List | user | — | sort = score (default), recent (last updated), found (newest first) or oldest (first found first); take 1–200 (default 50). |
| POST | `/api/v1/campaigns/{campaignId}/research` | ResearchJobs · Queue | user | ResearchQueue | Queues a run and returns at once; an already queued or running job for the campaign is returned instead. |
| GET | `/api/v1/campaigns/{campaignId}/research-jobs` | ResearchJobs · List | user | — |  |
| PUT | `/api/v1/campaigns/{campaignId}/schedule` | Schedules · Upsert | user | — |  |
| GET | `/api/v1/campaigns/{campaignId}/sources` | Sources · List | user | — |  |
| POST | `/api/v1/campaigns/{campaignId}/sources` | Sources · Create | user | — | Paste, Url or Feed. Pasted text is at most 50 000 characters. |
| DELETE | `/api/v1/campaigns/{campaignId}/sources/{sourceId}` | Sources · Delete | user | — |  |
| GET | `/api/v1/campaigns/{id}` | Campaigns · Get | user | — |  |
| PUT | `/api/v1/campaigns/{id}` | Campaigns · Update | user | — |  |
| DELETE | `/api/v1/campaigns/{id}` | Campaigns · Delete | user | — | Deletes the campaign with its leads, drafts and schedules. 404 when it is not the caller's. |
| GET | `/api/v1/capabilities` | Capabilities · Get | anonymous | — | What this deployment can actually do. Contains no secrets; readable before sign-in. |
| POST | `/api/v1/client-errors` | Feedback · Report | anonymous | ClientErrors | A browser error. Open to everyone (the candidate page has no sign-in); counted, not stored twice, and rate limited. |
| GET | `/api/v1/diagnostics` | Diagnostics · Get | user | — |  |
| GET | `/api/v1/drafts` | Drafts · List all | user | — |  |
| POST | `/api/v1/drafts/batch-approve` | Drafts · Batch approve | user | — |  |
| POST | `/api/v1/drafts/send-via-mailbox-batch` | Mailboxes · Send batch | NotGuest | — |  |
| GET | `/api/v1/drafts/{id}` | Drafts · Get | user | — |  |
| PUT | `/api/v1/drafts/{id}` | Drafts · Update | user | — |  |
| DELETE | `/api/v1/drafts/{id}` | Drafts · Delete | user | — |  |
| POST | `/api/v1/drafts/{id}/approve` | Drafts · Approve | user | — |  |
| POST | `/api/v1/drafts/{id}/reopen` | Drafts · Reopen | user | — | Turns a sent draft into a new editable version ("edit and send again"). |
| POST | `/api/v1/drafts/{id}/revoke-approval` | Drafts · Revoke | user | — |  |
| POST | `/api/v1/drafts/{id}/rewrite` | Drafts · Rewrite | NotGuest | AiWrites | AI rewrite suggestion (not saved); apply it with PUT so the normal draft checks run. |
| POST | `/api/v1/drafts/{id}/send-via-mailbox` | Mailboxes · Send | NotGuest | — |  |
| GET | `/api/v1/executions` | Executions · List | user | — |  |
| POST | `/api/v1/executions/bids/{bidId}` | Executions · Start bid | user | — |  |
| POST | `/api/v1/executions/drafts/{draftId}` | Executions · Start draft | user | — |  |
| POST | `/api/v1/executions/{id}/confirm` | Executions · Confirm | user | — |  |
| POST | `/api/v1/executions/{id}/fail` | Executions · Fail | user | — |  |
| POST | `/api/v1/feedback` | Feedback · Send | user | Feedback |  |
| POST | `/api/v1/goal-previews` | Ai · Preview | NotGuest | AiWrites |  |
| POST | `/api/v1/imports/preview` | Imports · Preview | user | Imports | CSV text up to 1 MB and 1000 rows. Nothing is imported until the preview is committed. |
| POST | `/api/v1/imports/{importId}/commit` | Imports · Commit | user | — |  |
| GET | `/api/v1/integrations` | Integrations · List | user | — |  |
| GET | `/api/v1/integrations/oauth/callback/{kind}` | Integrations · Oauth callback | anonymous | PublicProgress | The service sends the browser here, so there is no bearer token; the sealed, expiring state names the person. Tokens never go in the redirect. |
| POST | `/api/v1/integrations/oauth/complete/{kind}` | Integrations · Finish oauth | NotGuest | Connectors | The signed-in web app finishes the sign-in; only the person who started it can (the state is checked against them). |
| GET | `/api/v1/integrations/plugin/callback/{kind}` | Integrations · Plugin callback | anonymous | PublicProgress | The platform sends the browser back here with the connected-account id; the sealed state names the person, and the platform must agree. |
| PUT | `/api/v1/integrations/{kind}` | Integrations · Connect | NotGuest | Connectors | Checks the address or token against the real service (a webhook gets one "connected" message) before storing it encrypted. |
| DELETE | `/api/v1/integrations/{kind}` | Integrations · Disconnect | user | — |  |
| PUT | `/api/v1/integrations/{kind}/events` | Integrations · Events | user | — |  |
| POST | `/api/v1/integrations/{kind}/oauth` | Integrations · Start oauth | NotGuest | Connectors | "Connect with Notion / HubSpot / Slack / Airtable": the address of the service's own consent screen. 409 when the server has no app for it. |
| POST | `/api/v1/integrations/{kind}/plugin` | Integrations · Start plugin | NotGuest | Connectors | One-click "Connect" (Notion, Gmail, HubSpot, Slack, Airtable): the managed-connection platform's sign-in address. 409 without Plugins__ApiKey. |
| POST | `/api/v1/integrations/{kind}/push` | Integrations · Push | NotGuest | Connectors |  |
| PUT | `/api/v1/integrations/{kind}/target` | Integrations · Set target | user | Connectors |  |
| GET | `/api/v1/integrations/{kind}/targets` | Integrations · Targets | user | Connectors | Notion databases or Airtable tables this connection can see. |
| POST | `/api/v1/integrations/{kind}/test` | Integrations · Test | user | Connectors |  |
| GET | `/api/v1/interviews` | JobInterviews · List | user | — |  |
| PUT | `/api/v1/interviews/{id}` | JobInterviews · Update | user | — |  |
| DELETE | `/api/v1/interviews/{id}` | JobInterviews · Delete | user | — |  |
| POST | `/api/v1/interviews/{id}/status` | JobInterviews · Status | user | — |  |
| GET | `/api/v1/jobboards/feeds` | JobBoards · Feeds | user | JobBoardSearch | "Search all": the free public feeds (no key, no quota) and Adzuna when the server has its keys, merged and de-duplicated. sites is a comma list; empty means all (Adzuna only when configured; asked for by name without keys, it answers NotConfigured). |
| GET | `/api/v1/jobboards/jobs` | JobBoards · Jobs | NotGuest | JobBoardSearch |  |
| GET | `/api/v1/jobboards/jobs` | JobBoards · Jobs | NotGuest | JobBoardSearch |  |
| GET | `/api/v1/jobboards/status` | JobBoards · Status | user | JobBoardSearch |  |
| GET | `/api/v1/mailboxes` | Mailboxes · List | user | — |  |
| GET | `/api/v1/mailboxes/callback/{provider}` | Mailboxes · Callback | anonymous | — | The provider sends the browser here, so there is no bearer token; the sealed, expiring state identifies the person. |
| POST | `/api/v1/mailboxes/smtp` | Mailboxes · Connect smtp | NotGuest | — | Connect the person's own email with an app password (no sign-in screen). The password is verified with one test message to themselves and stored encrypted. |
| POST | `/api/v1/mailboxes/smtp/check-replies` | Mailboxes · Check replies | user | ReplyCheck | Reads the inbox of the email connected with an app password (headers only, read-only) and records replies to sent emails. |
| DELETE | `/api/v1/mailboxes/{provider}` | Mailboxes · Disconnect | user | — |  |
| POST | `/api/v1/mailboxes/{provider}/complete` | Mailboxes · Finish | NotGuest | Connectors | The signed-in web app finishes the connection; only the person who started it can (the state is checked against them). |
| POST | `/api/v1/mailboxes/{provider}/connect` | Mailboxes · Connect | NotGuest | — |  |
| GET | `/api/v1/me` | Access · Me | user | — | The caller's role (Candidate, Sales or Both), whether it was chosen, and whether the caller is the admin. |
| PUT | `/api/v1/me/role` | Access · Choose role | user | — | The sign-up choice; only once (409 afterwards: the admin changes it). |
| GET | `/api/v1/next-actions` | Outreach · Next actions | user | — |  |
| PATCH | `/api/v1/next-actions/{id}` | Outreach · Update next action | user | — |  |
| GET | `/api/v1/opportunities/{id}` | Opportunities · Get | user | — |  |
| GET | `/api/v1/opportunities/{id}/circle` | Circle · Get | user | — |  |
| POST | `/api/v1/opportunities/{id}/circle` | Circle · Add | user | — |  |
| POST | `/api/v1/opportunities/{id}/circle/request` | Circle · Ask | user | — | Asks the desktop agent to map this lead's circle on its next run. Idempotent while a request waits. |
| DELETE | `/api/v1/opportunities/{id}/circle/{personId}` | Circle · Delete | user | — |  |
| POST | `/api/v1/opportunities/{id}/contacts/find` | Opportunities · Find contacts | user | JobBoardSearch | Suggests contact details that the posting or the company's own contact pages publish. Reads at most three public pages; nothing is stored. |
| POST | `/api/v1/opportunities/{id}/people/find` | Opportunities · Find people | user | Connectors | Looks up recruiters and decision makers at the company with the person's own Hunter or Apollo key. Third-party data; nothing is stored but an activity line. |
| POST | `/api/v1/opportunities/{id}/people/reveal` | Opportunities · Reveal person | user | Connectors | Apollo only: reveals one person's work email. This uses the person's Apollo credits. |
| PATCH | `/api/v1/opportunities/{id}/status` | Opportunities · Update status | user | — |  |
| GET | `/api/v1/opportunities/{opportunityId}/activities` | Outreach · Activities | user | — |  |
| POST | `/api/v1/opportunities/{opportunityId}/activities` | Outreach · Create activity | user | — |  |
| GET | `/api/v1/opportunities/{opportunityId}/drafts` | Drafts · List | user | — |  |
| POST | `/api/v1/opportunities/{opportunityId}/drafts` | Drafts · Create | user | AiWrites |  |
| POST | `/api/v1/opportunities/{opportunityId}/interviews` | JobInterviews · Create | user | — |  |
| GET | `/api/v1/opportunities/{opportunityId}/next-actions` | Outreach · Opportunity next actions | user | — |  |
| POST | `/api/v1/opportunities/{opportunityId}/next-actions` | Outreach · Create next action | user | — |  |
| GET | `/api/v1/overview` | Overview · Get | user | — |  |
| GET | `/api/v1/profiles` | Profiles · List | user | — |  |
| POST | `/api/v1/profiles` | Profiles · Create | user | — |  |
| POST | `/api/v1/profiles/improve` | Profiles · Improve | NotGuest | AiWrites | "Improve with AI": suggestions for the person's own fields (no new facts); nothing is saved. |
| GET | `/api/v1/profiles/{id}` | Profiles · Get | user | — |  |
| PUT | `/api/v1/profiles/{id}` | Profiles · Update | user | — |  |
| GET | `/api/v1/profiles/{id}/versions` | Profiles · Versions | user | — |  |
| GET | `/api/v1/providers/readiness` | Providers · Readiness | user | — |  |
| GET | `/api/v1/public/progress/{token}` | PublicProgress · Progress | anonymous | PublicProgress |  |
| GET | `/api/v1/public/progress/{token}/journey` | PublicProgress · Journey | anonymous | PublicProgress | The candidate's journey since joining (when the team shares it): timeline, upcoming events, their recruiter. |
| POST | `/api/v1/public/progress/{token}/journey` | PublicProgress · Add journey | anonymous | PublicProgress | The candidate logs progress: a course, a certification, a project, interview prep or a note. |
| GET | `/api/v1/public/progress/{token}/resumes` | PublicProgress · Resumes | anonymous | PublicProgress | Resume builder: the roles the candidate may write for, with the job description and their saved resume. |
| PUT | `/api/v1/public/progress/{token}/resumes/{dealId}` | PublicProgress · Save resume | anonymous | PublicProgress |  |
| POST | `/api/v1/public/progress/{token}/resumes/{dealId}/submit` | PublicProgress · Submit resume | anonymous | PublicProgress |  |
| POST | `/api/v1/public/progress/{token}/resumes/{dealId}/tailor` | PublicProgress · Tailor resume | anonymous | ResumeTailor | Tailors the resume to the role's job description with Claude (or the server's AI). Returns it for review; nothing is saved. |
| POST | `/api/v1/public/progress/{token}/roles/{id}` | PublicProgress · Answer role | anonymous | PublicProgress |  |
| GET | `/api/v1/research-jobs/{id}` | ResearchJobs · Get | user | — |  |
| POST | `/api/v1/research-jobs/{id}/cancel` | ResearchJobs · Cancel | user | — | A queued job is cancelled at once; a running one stops between items and keeps what it found. |
| POST | `/api/v1/sales/bids/batch-approve` | Sales · Batch approve | user | — |  |
| PUT | `/api/v1/sales/bids/{id}` | Sales · Update bid | user | — |  |
| POST | `/api/v1/sales/bids/{id}/approve` | Sales · Approve bid | user | — |  |
| POST | `/api/v1/sales/bids/{id}/confirm-placement` | Sales · Confirm placement | user | — |  |
| POST | `/api/v1/sales/bids/{id}/handoff` | Sales · Handoff | user | — |  |
| POST | `/api/v1/sales/lead-search` | Sales · Lead search | NotGuest | LeadSearch | Companies or people from Apollo (RapidAPI) by keyword, country and company size. Paid per result: signed-in only, capped per day. |
| GET | `/api/v1/sales/projects` | Sales · List | user | — |  |
| POST | `/api/v1/sales/projects` | Sales · Create | user | — |  |
| GET | `/api/v1/sales/projects/{id}` | Sales · Get | user | — |  |
| POST | `/api/v1/sales/projects/{id}/bid` | Sales · Create bid | user | — |  |
| GET | `/api/v1/sales/upwork-opportunities` | UpworkOpportunities · List | user | — |  |
| POST | `/api/v1/sales/upwork-opportunities/import` | UpworkOpportunities · Import | user | — |  |
| PATCH | `/api/v1/sales/upwork-opportunities/{id}/decision` | UpworkOpportunities · Decide | user | — |  |
| POST | `/api/v1/sales/upwork-opportunities/{id}/promote` | UpworkOpportunities · Promote | user | — |  |
| POST | `/api/v1/sample-data` | SampleData · Create | user | — |  |
| GET | `/api/v1/schedules` | Schedules · List | user | — |  |
| GET | `/api/v1/security/events` | Security · Events | user | — |  |
| GET | `/api/v1/staffing/accounts` | Staffing · List accounts | user | — |  |
| POST | `/api/v1/staffing/accounts` | Staffing · Create account | user | — |  |
| POST | `/api/v1/staffing/accounts/{accountId}/contacts` | Staffing · Create contact | user | — |  |
| PUT | `/api/v1/staffing/accounts/{accountId}/contacts/{contactId}` | Staffing · Update contact | user | — |  |
| POST | `/api/v1/staffing/accounts/{accountId}/deals` | Staffing · Create deal | user | — |  |
| GET | `/api/v1/staffing/bench` | StaffingPipeline · Bench | user | — | The bench: every resource with status, resume strength, best fit, certifications and the next event. |
| GET | `/api/v1/staffing/candidates` | StaffingPipeline · Candidates | user | — |  |
| POST | `/api/v1/staffing/candidates` | StaffingPipeline · Create candidate | user | — |  |
| GET | `/api/v1/staffing/candidates/{id}` | StaffingPipeline · Candidate | user | — |  |
| PUT | `/api/v1/staffing/candidates/{id}` | StaffingPipeline · Update candidate | user | — |  |
| POST | `/api/v1/staffing/candidates/{id}/consent` | StaffingPipeline · Consent | user | — |  |
| GET | `/api/v1/staffing/candidates/{id}/journey` | StaffingPipeline · Journey | user | — |  |
| POST | `/api/v1/staffing/candidates/{id}/journey` | StaffingPipeline · Add journey | user | — |  |
| PUT | `/api/v1/staffing/candidates/{id}/journey-profile` | StaffingPipeline · Journey profile | user | — |  |
| GET | `/api/v1/staffing/candidates/{id}/progress-link` | StaffingPipeline · Progress link | user | — |  |
| POST | `/api/v1/staffing/candidates/{id}/progress-link` | StaffingPipeline · Create progress link | user | — |  |
| DELETE | `/api/v1/staffing/candidates/{id}/progress-link` | StaffingPipeline · Revoke progress link | user | — |  |
| GET | `/api/v1/staffing/candidates/{id}/resumes` | StaffingPipeline · Resumes | user | — |  |
| GET | `/api/v1/staffing/deals` | Staffing · List deals | user | — |  |
| POST | `/api/v1/staffing/deals/job-descriptions` | StaffingPipeline · Job descriptions | user | — |  |
| GET | `/api/v1/staffing/deals/{dealId}/conversation` | StaffingPipeline · Conversation | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/feedback` | StaffingPipeline · Feedback | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/interviews` | StaffingPipeline · Request interview | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/interviews/{id}/finish` | StaffingPipeline · Finish | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/interviews/{id}/notified` | StaffingPipeline · Notified | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/interviews/{id}/schedule` | StaffingPipeline · Schedule | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/meetings` | StaffingPipeline · Plan meeting | user | — |  |
| PUT | `/api/v1/staffing/deals/{dealId}/meetings/{id}` | StaffingPipeline · Replan meeting | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/meetings/{id}/approve` | StaffingPipeline · Approve meeting | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/meetings/{id}/finish` | StaffingPipeline · Finish meeting | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/meetings/{id}/invited` | StaffingPipeline · Meeting invited | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/messages` | StaffingPipeline · Draft message | user | — |  |
| PUT | `/api/v1/staffing/deals/{dealId}/messages/{id}` | StaffingPipeline · Edit message | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/messages/{id}/approve` | StaffingPipeline · Approve message | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/messages/{id}/intent` | StaffingPipeline · Classify reply | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/messages/{id}/sent` | StaffingPipeline · Message sent | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/offers` | StaffingPipeline · Create offer | user | — |  |
| PUT | `/api/v1/staffing/deals/{dealId}/offers/{id}` | StaffingPipeline · Update offer | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/offers/{id}/contract` | StaffingPipeline · Contract | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/offers/{id}/outcome` | StaffingPipeline · Outcome | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/offers/{id}/state` | StaffingPipeline · Move offer | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/proposals` | StaffingPipeline · Create proposal | user | — |  |
| PUT | `/api/v1/staffing/deals/{dealId}/proposals/{id}` | StaffingPipeline · Edit proposal | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/proposals/{id}/answer` | StaffingPipeline · Proposal answer | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/proposals/{id}/approve` | StaffingPipeline · Approve proposal | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/proposals/{id}/sent` | StaffingPipeline · Proposal sent | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/replies` | StaffingPipeline · Record reply | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/submissions` | StaffingPipeline · Create submission | user | — |  |
| PUT | `/api/v1/staffing/deals/{dealId}/submissions/{id}` | StaffingPipeline · Update submission | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/submissions/{id}/approve` | StaffingPipeline · Approve submission | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/submissions/{id}/sent` | StaffingPipeline · Submission sent | user | — |  |
| POST | `/api/v1/staffing/deals/{dealId}/submissions/{id}/withdraw` | StaffingPipeline · Withdraw submission | user | — |  |
| GET | `/api/v1/staffing/deals/{dealId}/work` | StaffingPipeline · Work | user | — |  |
| GET | `/api/v1/staffing/deals/{id}` | Staffing · Get deal | user | — |  |
| PUT | `/api/v1/staffing/deals/{id}` | Staffing · Update deal | user | — |  |
| PUT | `/api/v1/staffing/deals/{id}/job-description` | StaffingPipeline · Set job description | user | — |  |
| POST | `/api/v1/staffing/deals/{id}/notes` | Staffing · Add note | user | — |  |
| POST | `/api/v1/staffing/deals/{id}/stage` | Staffing · Move deal | user | — |  |
| POST | `/api/v1/staffing/from-opportunity/{opportunityId}` | Staffing · From opportunity | user | — |  |
| GET | `/api/v1/staffing/interviews` | StaffingPipeline · Interview board | user | — | The sales interview tracker: every interview across deals and candidates. |
| PUT | `/api/v1/staffing/interviews/{id}/prep` | StaffingPipeline · Set interview prep | user | — |  |
| DELETE | `/api/v1/staffing/journey/{entryId}` | StaffingPipeline · Delete journey | user | — |  |
| GET | `/api/v1/staffing/kpis` | StaffingPipeline · Kpis | user | — |  |
| GET | `/api/v1/staffing/rate-cards` | StaffingPipeline · Rate cards | user | — |  |
| POST | `/api/v1/staffing/rate-cards` | StaffingPipeline · Create rate card | user | — |  |
| PUT | `/api/v1/staffing/rate-cards/{id}` | StaffingPipeline · Update rate card | user | — |  |
| POST | `/api/v1/staffing/rate-cards/{id}/status` | StaffingPipeline · Rate card status | user | — |  |
| POST | `/api/v1/staffing/resumes/{id}/review` | StaffingPipeline · Review resume | user | — |  |
| GET | `/api/v1/suppressions` | Outreach · Suppressions | user | — |  |
| POST | `/api/v1/suppressions` | Outreach · Suppress | user | — |  |
| DELETE | `/api/v1/suppressions/{id}` | Outreach · Unsuppress | user | — |  |
| GET | `/api/v1/trends` | Trends · Get | NotGuest | JobBoardSearch |  |
| POST | `/api/v1/voice/speak` | Voice · Speak | NotGuest | VoiceSpeak | A natural (human-sounding) English voice for the assistant's reply: Groq's Orpheus text-to-speech, returned as WAV. 503 without a Groq key, 502 when Groq refuses (for example the model terms were not accepted yet); the browser then uses its own voice. |
| GET | `/api/v1/voice/status` | Voice · Status | user | — |  |
| POST | `/api/v1/voice/transcribe` | Voice · Transcribe | NotGuest | VoiceTranscribe |  |
| GET | `/api/v1/wellfound/activities` | Wellfound · Activities | user | — |  |
| GET | `/api/v1/wellfound/applications` | Wellfound · Applications | user | — |  |
| PATCH | `/api/v1/wellfound/applications/{id}/state` | Wellfound · Change application state | user | — |  |
| POST | `/api/v1/wellfound/demo/load` | Wellfound · Load demo | user | — |  |
| GET | `/api/v1/wellfound/jobs` | Wellfound · Jobs | user | — |  |
| PATCH | `/api/v1/wellfound/jobs/{id}/state` | Wellfound · Change job state | user | — |  |
| GET | `/api/v1/wellfound/kpis` | Wellfound · Kpis | user | — |  |
| POST | `/api/v1/wellfound/public/sync` | Wellfound · Sync public | user | — |  |
| GET | `/api/v1/wellfound/status` | Wellfound · Status | user | — |  |
| GET | `/health/live` | Health · Live | anonymous | — | Process is up. No dependencies checked, no configuration returned. |
| GET | `/health/ready` | Health · Ready | anonymous | — | Database reachable. The response names the failing check but never connection details. |
