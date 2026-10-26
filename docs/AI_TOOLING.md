# AI tooling policy

How this repository is worked on with Claude Code, and what the assistant may and may not use. Owner: Shivanshu Rai (repo owner).

## Instructions and memory

- `CLAUDE.md` (this repo) is loaded every session: stack, commands, folder map, mandatory rules, and a table of docs to open only when a task needs them.
- The workspace `CLAUDE.md`, `HANDOFF.md` (where things stand) and `TASKS.md` (single task list) sit one level up.
- Personal assistant memory is kept in the user profile and never committed. Anything the team needs goes into `CLAUDE.md`, `docs/` or `HANDOFF.md`:
  a preference that becomes a team rule is copied into the workspace `CLAUDE.md` "Mandatory rules" with its date.
- Load order: workspace `CLAUDE.md` → this repo's `CLAUDE.md` → a skill when the task matches → a doc from the routing table. Nothing else is read
  at session start; generated files (build output, migrations' designer files, coverage) are denied in `permissions` so they never fill the context.

## Skills and commands

- Skills live in `.claude/skills/<name>/SKILL.md` (front matter: `name`, `description`) and load when their description matches the task.
  This repo: `add-endpoint`, `docs-sync`, `migrations`, `unit-test`, `error-handling`, `debug`, `agent-adapter`.
- Slash commands live in `.claude/commands/`: `/verify` (every check before a commit), `/review` (findings on the branch before the PR),
  `/release` (the one PR for a finished batch), `/live-check` (the deployed site after a merge), `/handoff` (HANDOFF.md and TASKS.md at the end).

## AI data rules

- Never paste production data, a candidate's personal details or a secret into a prompt, an issue or a chat. Use the labelled sample data
  (Settings → Sample data) or the unit-test fixtures.
- In the product, AI providers receive only the text a request needs; contact details, pay and rates are never sent
  (see `docs/THIRD_PARTIES.md` and `docs/DATA_INVENTORY.md`). Every AI output shown to a user is labelled as AI and reviewed before it is
  sent or saved.
- A person's own Claude key is used only for that person's requests (and, if they switch it on, their candidates' resume tailoring).

## Checks that keep this true

- `node .claude/check-ai-config.mjs` (CI job `ai-config`): `.mcp.json` lists only servers from the table below, with no inline
  credentials; project servers are never auto-enabled; the deny rules and hook files exist; every skill has a name and a description.
- `node --test .claude/hooks/hooks.test.mjs` (same CI job): the guard hooks block what they claim to block.
- `.githooks/pre-commit` (enable with `git config core.hooksPath .githooks`): gitleaks on the staged changes.

## MCP servers and plugins

- **No MCP server is configured for this project** (`.mcp.json` lists none). Project servers are never enabled automatically (`enableAllProjectMcpServers: false`); a server is added only by a reviewed change to `.mcp.json` plus an entry in the table below.
- Allowed, when added: read-only documentation servers (library docs), and the design canvas for UI mockups. **Not allowed**: servers that can read mail, calendars or files of a real person, or that write to production systems, payment or CRM accounts.
- Connectors a developer has at user level (outside this repo) must not be used to move production data, secrets or candidate personal data.

| Server | Purpose | Scope | Added in |
|---|---|---|---|
| none | — | — | — |

## Guardrails

- Hooks in `.claude/hooks/` (wired in `.claude/settings.json`): a command guard blocks broad deletes, force pushes, hard resets onto remote refs, piping downloads into a shell, database drops and `--no-verify`; a write guard blocks `.env*` files, local secret settings, the agent's `config.json` and secret-looking content; a write log records every edit.
- `permissions` in `.claude/settings.json` deny reading local secret files and the destructive commands above, and allow the routine build and test commands.

## Secrets

- Keys live only on the server (Render / Vercel environment). `.env.example` holds placeholders; `.env.production` holds public values only.
- CI runs gitleaks on the full history (`.gitleaks.toml` allows the fake keys in tests only).
- A key pasted into a chat or a ticket is treated as leaked and rotated.

## Review

- One branch per batch, one PR, CI green before merge (see `.github/pull_request_template.md`). The assistant states what it ran and never claims a check it did not run.
