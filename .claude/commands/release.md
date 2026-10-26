---
description: Raise the one PR for a finished, verified batch and merge it (owner-authorised workflow)
---

1. Make sure every task of the batch is done and `/verify` passed on this branch; if not, stop and say what is missing.
2. Write the PR body (What and why, Checked with real numbers, Risk and rollback) to a file in the scratchpad.
3. Run `bash tools/pr.sh OpportunityPilotWebApi "<title>" <body-file> --merge` from the workspace root. For a change with a database migration, raise the PR first without `--merge`, wait for CI to pass, then merge.
4. After the deploy, test the live site in a browser and report what passed and what did not. Update `TASKS.md` and `HANDOFF.md`.
