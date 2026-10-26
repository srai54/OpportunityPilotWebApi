# agent/ — the local desktop agent (scoped instructions)

Loaded when working under `agent/`. The repo `CLAUDE.md` still applies.

- **What it is:** a Node + Playwright CLI that runs on the person's own computer, in their own browser profile, to collect postings,
  apply to shortlisted jobs, map people around a job and send approved LinkedIn notes. The API never does this server-side.
- **Commands:** `npm install`, `npm test` (unit + e2e against `test/mock/server.ts`, no real site), `npm run agent -- <command>`
  (see `README.md`). Tests never touch LinkedIn, Naukri or Instahyre.
- **Hard rules:** dry run is the default (`--submit` is explicit); stop at any login wall, CAPTCHA or security check and tell the person;
  never store passwords or cookies outside `.data/` (git-ignored); never edit `config.json` or `.data/` (the person's key and session —
  the write hook blocks it); report every result to the API with the agent key, only on the agent endpoints.
- **Changing a platform adapter** (`src/platforms/*`, `form.ts`, `answers.ts`, the mock site): read `.claude/skills/agent-adapter/SKILL.md` first,
  and add or update the mock page and an e2e test for the new behaviour.
- Scratch scripts (`scratch-*.mjs`) in this folder are local and untracked; do not commit them.
