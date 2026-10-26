# Observability and feedback

How we see what goes wrong and what people think, without a third-party tracker. Added 2026-10-26.

## What is collected

| Signal | Where it comes from | Where to read it | Kept |
|---|---|---|---|
| Server logs | Structured logging with a correlation id on every request (`X-Correlation-ID`, also in every error response) | Render → Logs; search the id | Render's retention |
| Health | `GET /health`, `GET /api/v1/diagnostics` (version, pending migrations) | Settings → This deployment; cron ping | — |
| AI usage | `ai_usage` rows: operation, outcome, duration, attempts, safe reason (never prompts or answers) | `GET /api/v1/ai/status`; the database | 90 days |
| Security events | Sign-ins, key use, refusals | `security_events` | 365 days |
| Browser errors | The web apps report uncaught errors, rejected promises and pages that failed to render (`POST /api/v1/client-errors`) | Settings → Feedback and errors (admin) | 30 days after last seen |
| Feedback | "Send feedback" at the bottom of every page: kind, optional 1–5 rating, message, page | Settings → Feedback and errors (admin) | until deleted |
| Page views | Vercel Analytics (web, Vercel builds only) | Vercel dashboard | Vercel's retention |

Error reports carry the message, the page path (a progress link's token is masked), the first 12 stack lines, the browser, the build
commit and, when known, the correlation id. Each distinct message is sent once per page load and at most 10 per load; the server counts
repeats of the same message on the same page within an hour on one row, and rate limits reports to 60 an hour per visitor.

## Triage loop (weekly, and after every release)

1. Open Settings → Feedback and errors. The figures show new feedback, the last 7 days, the average rating and errors in the last 24 hours.
2. **Errors:** sort by count. For each new kind, check the build it came from; if it is the current build, open an issue (or a TASKS.md row)
   with the message, page and stack. Use the correlation id to find the server side in Render's logs.
3. **Feedback:** mark each message Seen when read, Done when acted on. A bug becomes a TASKS.md row with the feedback id; an idea goes to the
   backlog section.
4. After a release, check the card again within an hour: a spike of one message on the new build means roll back or fix forward.

## Alerts

| Alert | How | Who is told |
|---|---|---|
| API down | `.github/workflows/keepalive.yml` calls `/health/live` every 10 minutes and fails on any non-2xx after two retries | GitHub emails the repo owner on a failed scheduled run (Settings → Notifications → Actions) |
| A release breaks a build or a test | CI on every push to `main` | GitHub emails the author of the push |
| Error spike after a release | Step 4 of the triage loop (the Settings card within an hour of a deploy) | The person releasing |

## Not yet

- No paging and no automatic error-spike alert: the free Render plan has no log alerts. A daily summary email is the next step.
- No server-side exception aggregation beyond logs; unexpected 500s are logged with the correlation id.
