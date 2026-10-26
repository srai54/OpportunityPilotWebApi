---
description: After a deploy, check the live API and web app and report what passed and what did not
---

1. Wait for the deploy: `GET https://opportunitypilotwebapi.onrender.com/api/v1/diagnostics` until `version` shows the merged commit
   (the free host can take a minute to wake).
2. API: call each endpoint the batch added or changed with a guest token (`POST /api/v1/auth/guest`) and record the status codes.
   Do not spend paid quotas beyond the limits in `TASKS.md` (JSearch ≤ 10 searches per task; no Apollo credits).
3. Web: open https://opportunity-pilot-webapp.vercel.app in Edge (Playwright) at 1280 and 375, light and dark, on every screen the batch touched; run axe; note
   console errors and sideways scroll.
4. Report one line per check: passed / failed / not run, with the evidence (status code, axe count, screenshot path).
