# Setup

**Demo mode.** The API never refuses to start over missing setup: without
`ConnectionStrings__Main` data is kept in memory, and without `Auth__SupabaseUrl`
visitors sign in as guests. The exact behaviour is in
[business-rules.md → Demo mode](business-rules.md#demo-mode);
the web app shows a "Demo mode" banner from `/api/v1/capabilities`. Enabling
`Auth:DevBypass` outside Development still stops the process — that is a security guard.

## 1. Local development

Prerequisites: .NET 10 SDK, SQL Server LocalDB (ships with Visual Studio), and
Docker if you want to run the integration tests.

```bash
dotnet run --project src/OpportunityPilot.Api
```

`appsettings.Development.json` already sets:

| Setting | Value | Why |
|---|---|---|
| `Database:Provider` | `SqlServer` | LocalDB, no install beyond Visual Studio |
| `Database:MigrateOnStartup` | `true` | Development only; startup throws if set elsewhere |
| `ConnectionStrings:Main` | LocalDB `OpportunityPilot` | |
| `Auth:DevBypass` | `true` | `X-Dev-User: <any name>` signs in as a synthetic user |
| `Cors:AllowedOrigins` | `http://localhost:5173` | the Vite dev server |

Each distinct `X-Dev-User` value maps to a stable, separate owner id, so you can
test isolation between users by changing the name. Dev bypass throws at startup
outside `Development`.

To develop against Postgres instead (e.g. your Supabase database), use user
secrets so nothing lands in git:

```bash
cd src/OpportunityPilot.Api
dotnet user-secrets init
dotnet user-secrets set "Database:Provider" "Postgres"
dotnet user-secrets set "ConnectionStrings:Main" "Host=...;Database=postgres;Username=...;Password=...;SSL Mode=Require"
```

## 2. Configuration reference

Environment variables use `__` for nesting (`ConnectionStrings__Main`).

| Key | Required | Notes |
|---|---|---|
| `ConnectionStrings__Main` | for real use | Without it: demo mode, in-memory data |
| `Database__Provider` | yes | `SqlServer` or `Postgres` (production default `Postgres`) |
| `Auth__SupabaseUrl` | yes outside dev | `https://<ref>.supabase.co`, https only. Without it: demo mode, guest sign-in only. Tokens are validated against `{url}/auth/v1/.well-known/jwks.json` |
| `Auth__Audience` | no | default `authenticated` |
| `Auth__LegacyJwtSecret` | no | server-only; only for old Supabase projects still signing with HS256. Leave unset for asymmetric signing keys |
| `Auth__DevBypass` | no | Development only |
| `Auth__AllowGuests` | no | default `true`: keep "Continue as guest" once Supabase or Hanko sign-in is on; `false` requires an account |
| `Auth__HankoApiUrl` | no | Hanko Cloud API URL from the Hanko console (`https://<id>.hanko.io`), https only. When set, Hanko session tokens are accepted **next to** Supabase ones (keys from `{url}/.well-known/jwks.json`, RS256). Must be set together with the web app's `VITE_HANKO_API_URL` |
| `Auth__HankoAudience` | no, recommended | the token audience Hanko sets: the passkey relying-party id, i.e. the web app's domain without `https://` (e.g. `opportunitypilot.vercel.app`); comma-separate several. Unset: audience not checked |
| `Auth__HankoIssuer` | no | only if the Hanko project sets a custom `iss`. Unset: a Hanko token may carry no `iss` or the Hanko API URL; any other issuer is rejected |
| `Cors__AllowedOrigins__0` | yes for a browser | the web app's origin, e.g. `https://app.example.com`; add `__1`, `__2`… for more |
| `Cors__AllowedOriginPatterns__0` | no | https origin with one `*` for a single hostname fragment (letters, digits, hyphens — no dots), for hosts that give each deployment its own address. Default allows this project's Vercel deployments: `https://opportunity-pilot-webapp-*-vsr6.vercel.app` |
| `Features__GeminiEnabled` | no | `false`. Capability shows *Configured · unverified* when true and a key is present; live calls arrive in M4 |
| `Ai__GeminiApiKey` | no | server-side secret; never returned to the browser |
| `Ai__GeminiModel`, `Ai__MaxCallsPerRun`, `Ai__MaxOutputTokens`, `Ai__AllowPaidUsage` | no | budgets for M4 |
| `Jsearch__Key` | no | server-side secret (RapidAPI, free plan 200 requests/month). Turns on the Indeed campaign source and live Indeed, LinkedIn and SEEK postings in Job discovery (`GET /api/v1/jobboards/jobs?board=`). `Jsearch__CacheMinutes` (default 360) reuses identical searches to save the quota. Without it the boards show a search link only |
| `Adzuna__AppId`, `Adzuna__AppKey` | no | server-side secrets (free developer keys from developer.adzuna.com; both needed). Turn on Adzuna campaign sources and Adzuna in Find → All sites (`GET /api/v1/jobboards/feeds`), searched in the country of the place (India by default). Never logged or returned; without them Adzuna reports *Not configured* and sends nothing |
| `Features__GmailEnabled`, `Features__MongoArchiveEnabled` | no | flip capability status from *Disabled* to *Not built yet* |
| `Research__ProcessorEnabled`, `Research__PollSeconds`, `Research__MaxCandidates`, `Research__MaxFetches`, `Research__TimeoutSeconds`, `Research__MaxBytes`, `Research__Concurrency` | no | research worker switch and run limits; defaults and server ceilings in [business-rules.md → Research runs](business-rules.md#research-runs). Integration tests set `Research__ProcessorEnabled=false` and run jobs themselves |
| `MIGRATE_ON_START` | no | container only, default `true`: runs `--migrate` as a separate process before the server starts; skipped while no connection string is set |
| `PORT` | no | set by Render; the app binds `0.0.0.0:$PORT` unless `ASPNETCORE_URLS` is set |

## 3. Migrations

There are two migration sets, one per provider (SqlServer and Postgres), both in schema `app`. The exact commands are in
the [CLAUDE.md](../CLAUDE.md#2-cheat-sheet-commands) cheat-sheet and the procedure with its review checklist in
[skills/migrations.SKILL.md](../skills/migrations.SKILL.md). Outside Development, applying them is an explicit release
step: `dotnet OpportunityPilot.Api.dll --migrate` applies pending migrations and exits.

## 4. Supabase

1. Create a project. **Project Settings → API**: copy the project URL into
   `Auth__SupabaseUrl` (API) and `VITE_SUPABASE_URL` (web), and the publishable
   (anon) key into `VITE_SUPABASE_PUBLISHABLE_KEY` (web only).
2. **Database → Connect**: use the session pooler connection string for
   `ConnectionStrings__Main` with `Database__Provider=Postgres`.
3. **Authentication → URL configuration**: set the site URL to the web app's
   origin so confirmation emails link back to it. Add the exact production
   `/reset/new` URL to the allowed redirects; add localhost redirects only to the
   development project.
4. Keep email confirmation enabled. Set the Auth password minimum to at least 12
   characters (matching the web app) and enable leaked-password protection when
   the project plan supports it.
5. Prefer an asymmetric signing key (`ES256` or `RS256`). The API reads the public
   keys from `/auth/v1/.well-known/jwks.json`; no private key or service-role key is
   needed. When rotating a key, wait at least 20 minutes before revoking the old
   key so cached JWKS and already-issued short-lived access tokens can age out.
6. Do not expose schema `app` through the Data API. If it must be exposed later,
   enable and verify owner-scoped RLS policies first (OQ-BE-003).

The browser only uses Supabase for sign-in; it never reads the database directly.

## 5. Deploying to Render

`render.yaml` in this repo is a Render Blueprint for the API (Docker, free plan,
Singapore). The web app repo has its own blueprint. Full step-by-step for both,
in order, is in `DEPLOY.md` at the workspace root; the short version:

1. Render Dashboard → **New → Blueprint** → this repo. Render reads `render.yaml`.
2. When prompted, enter `ConnectionStrings__Main` (Supabase **session pooler**
   string, port 5432 — the direct `db.<ref>.supabase.co` host is IPv6-only and
   unreachable from Render) and `Auth__SupabaseUrl`.
3. Deploy. The container runs `--migrate` first (`MIGRATE_ON_START=true`), then
   starts on `$PORT`. A failed migration stops the new container; the previous
   deploy keeps serving.
4. Check `https://<service>.onrender.com/health/ready` returns `ready`.

`postgres://` URIs and key=value strings both work (`PostgresConnectionString`).
If the web app's URL differs from `https://opportunitypilot-web.onrender.com`,
edit `Cors__AllowedOrigins__0` in the dashboard.

Older Supabase projects that still sign tokens with the shared HS256 secret (the
JWKS endpoint returns no keys) also need `Auth__LegacyJwtSecret` (Project
Settings → JWT keys → legacy secret). Symptom without it: sign-in succeeds, then
the app immediately says the session expired.

**Hanko sign-in (main sign-in when set).** Set `Auth__HankoApiUrl` (and, recommended, `Auth__HankoAudience`)
on Render and `VITE_HANKO_API_URL` on Vercel; the owner steps are in the web repo's `docs/AUTH_SETUP.md`. A Hanko
user's data is stored under an owner id derived from the Hanko user id (`HankoAuth.OwnerIdFor`, a name-based UUID),
so it never collides with a Supabase user or a guest; the same person signing in with Supabase is a different owner.
`GET /api/v1/capabilities` lists the active systems in `signInProviders`.

For an account-only production deployment, also set `Auth__AllowGuests=false`.
Guest access otherwise needs no signing key or identity provider: opaque session
tokens are stored only as SHA-256 hashes and survive restarts whenever the main
database is durable. Never put `Auth__LegacyJwtSecret` in a `VITE_` variable.

Free instances sleep when idle; the web app shows "API · starting" and retries
with backoff rather than reporting a failure.

## 6. Tests

`dotnet test` runs everything; integration tests start `postgres:17-alpine` with Testcontainers and need a running
Docker daemon, unit tests and `StartupGuardTests` do not. How the test harness works (dev-bypass users, the research
processor switched off, the loopback fetch policy) is in [skills/unit-test.SKILL.md](../skills/unit-test.SKILL.md).
