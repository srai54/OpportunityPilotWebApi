# Security

## Reporting a vulnerability

Please do not open a public issue. Contact the maintainers privately (repository owner) with steps to reproduce. We aim to answer within 3 working days.

## Controls in place

| Area | Control | Where |
|---|---|---|
| Authentication | Hanko session JWT (JWKS), Supabase JWT (JWKS), signed guest tokens in demo mode, `X-Agent-Key` only on agent endpoints; the dev bypass refuses to start outside Development | `src/OpportunityPilot.Api/Auth/` |
| Authorisation | Every query is owner-scoped from the signed-in user; "not found" and "not yours" are both 404. Guests are blocked from paid parts (AI, voice, live paid boards, sending) by the `NotGuest` policy | services, `Auth/Policies` |
| Abuse limits | Per-visitor rate limits: guest sign-in, imports, research queue, job-board search, lead search, public progress, connectors, reply check, voice transcribe/speak, assistant, AI writes; 4 MB body cap; daily budgets per paid provider and AI | `Hosting/RateLimits.cs`, `Application/Common/ProviderBudget.cs` |
| Outbound requests | `SafeFetcher`: public addresses only (checked at connect time), redirects re-checked, size and time caps, bounded concurrency held per attempt, POSTs never retried. Connectors: https on the default port only, public addresses only. Mail hosts: public addresses only | `Infrastructure/Research/SafeFetcher.cs`, `Infrastructure/Integrations/ConnectorHttp.cs`, `Application/Mail/MailModels.cs` |
| Secrets at rest | Mailbox and connector secrets encrypted with AES-GCM (`TokenProtector`); agent keys stored as SHA-256 + prefix only | `Application/Mail`, `Domain/Agents` |
| Secrets in code | None in `appsettings*.json`; keys come from Render environment variables; hooks block writing `.env*` and `agent/config.json` | `.claude/settings.json` |
| Errors | Problem details with a correlation id; no exception text, secrets or hosts in responses or activities | `Hosting/` |
| Automation boundary | The server never automates a logged-in site; the desktop agent runs in the user's own browser and stops at any check | `agent/` |

## Known gaps (tracked in the workspace `TASKS.md`, section SEC)

1. OAuth callbacks (mailbox and connector connect) finish in an anonymous callback using the owner sealed in `state`; they should finish in an authenticated request that checks the owner, with single-use state.
2. Rate limits partition by IP for Hanko/guest/agent callers because only the default scheme runs before the limiter; a policy-scheme selector would partition them per user.
3. When `Mail__TokenKey` is not set, the encryption key is derived from the connection string; production should require `Mail__TokenKey` (rotation with re-encrypt is planned, decision D3).
4. SMTP sends re-resolve DNS after the host check (DNS-rebinding window); connect to the checked address instead.
