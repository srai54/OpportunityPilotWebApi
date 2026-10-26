---
description: Review the current branch against main before the PR — correctness, security, owner scoping, tests and docs — and report findings
---

1. `git diff main...HEAD --stat`, then read every changed file in full (not only the hunks).
2. Check, and list each finding with `file:line`, what goes wrong and how to fix it:
   - **Correctness:** edge cases, null and empty input, concurrency (`Version`), time zones (UTC), off-by-one in limits.
   - **Security:** owner scoping on every query, `[AllowAnonymous]` only with a rate limit and a token, no secret returned or logged,
     input lengths bounded, no SSRF (outbound fetches go through `SafeFetcher` / `ConnectorHttp`).
   - **AI calls:** daily budget and rate limit, fallback when the provider fails, nothing invented reaches the user unlabelled.
   - **Tests:** new logic has unit tests, including the failure paths.
   - **Docs:** the `docs-sync` skill's table is satisfied (`python tools/endpoints.py --check` passes).
3. Rank findings most severe first. Say plainly when there are none. Do not fix anything unless asked.
