---
name: docs-sync
description: Which document must change when code changes in OpportunityPilot, so docs never drift — endpoint index, API contracts, schema, privacy, decisions, CLAUDE.md, HANDOFF and TASKS. Read before finishing any batch, and whenever behaviour, schema, settings or a rule changes.
---

# Keep the docs in step with the code

Docs change in the **same commit** as the code. Before raising the PR, walk this table against `git diff --stat main`.

| What changed | Update |
|---|---|
| A controller action (added, removed, route or auth changed) | `python tools/endpoints.py` → `docs/api-endpoints.md` (CI checks it), and the shape in `docs/api-contracts.md` |
| An entity, mapping or migration | `docs/db-schema.md` (table section + migration row) |
| Personal data stored, sent to a provider, kept or deleted differently | `docs/PRIVACY.md` |
| A choice between real options (provider, library, trade-off) | a dated entry in `docs/DECISIONS.md` (Decision, Why, Trade-offs) |
| A setting / environment variable | `.env.example` (placeholder), `docs/operations.md`, and `render.yaml` when it must be set on Render |
| A rule for how to work (new command, check, folder) | this repo's `CLAUDE.md` (keep it short; details go in `docs/`) |
| Logs, error reports, alerts | `docs/OBSERVABILITY.md` |
| MCP servers, hooks, permissions, skills | `docs/AI_TOOLING.md` (and `.claude/check-ai-config.mjs` must still pass) |
| End of a session | workspace `HANDOFF.md` (where things stand) and `TASKS.md` (status and counts) |

Write docs in plain words for a developer new to the repo: what it is, the rule, and where in the code. No marketing, no future tense
for things that are not built.
