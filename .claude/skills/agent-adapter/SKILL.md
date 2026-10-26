---
name: agent-adapter
description: How to add or fix a platform adapter in the desktop agent (agent/src/platforms) — selector fallbacks, the tsx __name shim for page.evaluate, fixing selectors from .data/debug evidence, mock-site tests, and the safety rules an adapter must never break (security checks, dry runs, never guessing answers). Read before touching agent/src/platforms, form.ts, answers.ts or the mock site.
---

# Desktop agent platform adapters

The agent (`agent/`, Node + Playwright) drives the user's own logged-in browser. It is owned by another engineer: keep
changes small and run its tests. Agent rules: [docs/business-rules.md](../../../docs/business-rules.md#desktop-agent--local-agent);
TypeScript conventions: [docs/conventions.md](../../../docs/conventions.md#desktop-agent-typescript-agent).

## Files

| File | Role |
|---|---|
| `agent/src/platforms/types.ts` | `PlatformAdapter` contract: `platform`, `loginUrl`, `isLoggedIn`, `searchPage`, `openJob`, `readDescription`, `apply` |
| `agent/src/platforms/linkedin.ts`, `naukri.ts` | one factory per site, taking `base` so tests can point it at the mock |
| `agent/src/platforms/linkedin-people.ts` | LinkedIn people search cards (JD circle) and profile actions (Connect → Add a note, Message); pure parsing/decision functions plus page functions taking `base` |
| `agent/src/form.ts` | generic field discovery (labels resolved like assistive tech) and filling for multi-step forms |
| `agent/src/answers.ts` | rule-based answers from `config.json`; returns `undefined` when nothing matches |
| `agent/src/browser.ts` | persistent profile, the `__name` shim (`prepareContext`), `captureDebug` |
| `agent/src/cli.ts` | `adapterFor(name)` — register a new adapter here |
| `agent/test/mock/server.ts` | a stand-in for each site built from its real structure; records `submissions` |
| `agent/test/e2e.test.ts` | flows against the mock |

## Decision table

| Task | Do |
|---|---|
| A selector stopped matching on the live site | open the newest `.data/debug/*.html`, find the element, **add** the new selector in front of the old one in the comma list (`'.new-class, .old-class'`), mirror the new structure in `test/mock/server.ts`, run `npm test` |
| Prefer stable hooks | roles and accessible names (`getByRole('button', { name: /easy apply/i })`), `data-*` ids, `aria-label`, then class names; combine with `.or()` |
| Read text | the adapter's `text(locator)` helper (count check, 2 s timeout, whitespace collapsed, `''` on failure) |
| Extract many cards | `locator.evaluateAll((els, arg) => …, arg)`; the callback must be self-contained (no imports, no outer variables — pass them as the argument) |
| Detect login wall / security check / platform limit | `assertUsable(page)` throwing `StopRun` with the exact command to run next |
| Add a new platform | new `src/platforms/<site>.ts` factory; extend `Platform` in `store.ts`; register in `cli.ts`; mock pages + e2e tests; the API must also accept it (`JobPlatform`/`ApplicationPlatform` enums, agent postings validation) — see OQ-BE-024 |
| A question the saved answers do not cover | return `{ status: 'NeedsManual', detail: 'No saved answer for: …' }` and leave the form unsent |
| Unknown page state | `captureDebug(page, '<site>-<job>-<what>')`, return `Failed` with a one-line reason |

## The `__name` shim

The CLI runs through `tsx`, whose transform wraps named functions in a `__name()` helper. Callbacks passed to
`page.evaluate`/`evaluateAll` run inside the page, where that helper does not exist. `prepareContext` defines a no-op
`__name` in every page (given as a string so the transform cannot touch it), and `vitest.config.ts` sets
`esbuild.keepNames` so tests reproduce the CLI's transform.

- Always get pages from `openBrowser` (CLI) or `newPreparedPage(browser)` (tests).
- A `ReferenceError: __name is not defined` means a page was created without `prepareContext`.

## Test pattern

```ts
// From test/e2e.test.ts: the mock serves a login wall under /wall.
it('stops the whole run when LinkedIn shows the login wall', async () => {
  const store = new Store(join(dir, 'log.jsonl'))
  const summary = await applyToShortlist({ adapter: linkedIn(`${mock.origin}/wall`), config: config(), store, jobs: list(), submit: true, page })
  expect(summary.outcomes).toHaveLength(0)
  expect(summary.stoppedBecause).toMatch(/not logged in to linkedin/i)
})
```

Every adapter change needs: a dry run that leaves `mock.submissions` empty, a submit that records exactly the saved
answers, a `NeedsManual` case, and a stop case. The mock proves the logic, not the live site; say so when reporting.

## Anti-patterns

- Never bypass, solve or wait out a CAPTCHA, checkpoint, 2-step check or rate-limit message — stop the run with `StopRun`.
- Never add stealth (fake user agents, fingerprint spoofing, hidden automation flags) or store passwords/cookies outside the user's own browser profile in `.data/`.
- Never move platform automation server-side; the API must never fetch LinkedIn or Naukri.
- Never let a dry run click a final Submit/Apply (Naukri submits on the first Apply click, so its dry run stops before it).
- Never guess an answer the user did not save; never type free text that is not in `config.json`.
- Never apply to anything that did not come from `GET /api/v1/agent/shortlist`.
- Never send a LinkedIn note or message that did not come from `GET /api/v1/agent/linkedin-outbox`, never change its text, and never send one whose start came back `reused`.
- Never let an outreach dry run click a final Send or leave typed text behind in a message box.
- Never remove an old selector in the same change that adds a new one unless the debug evidence shows it is gone everywhere.
- Never read, edit or commit `agent/config.json` or `agent/.data/**` — they hold the user's answers, key and session.
