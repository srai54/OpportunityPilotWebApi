---
description: Run every check this repo needs before a commit and report the results honestly
---

Run, in order, and stop at the first failure (show its output):

1. `dotnet build OpportunityPilot.sln -v q` (0 warnings: warnings are errors)
2. `dotnet test tests/OpportunityPilot.UnitTests --no-build` (integration tests need Docker; they run in CI)
3. A schema change: `dotnet ef migrations has-pending-model-changes` for both providers
4. `agent/`: `npm test` when agent files changed

Then report one line per check with its result and counts (tests passed / failed). Never report a check you did not run. For UI changes, also say whether the Edge check (1280 + 375, light + dark, axe 0) was done.
