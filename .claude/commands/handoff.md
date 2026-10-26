---
description: End the session cleanly — update HANDOFF.md and TASKS.md with what was done, verified, left and blocked
---

1. Update the workspace `TASKS.md`: each task touched gets its real status (Done only when merged and verified live), then recount
   the counts table.
2. Update the workspace `HANDOFF.md` top section: date, what was merged (PR numbers), what was verified and how (tests with counts,
   browser checks, live checks), what is committed but not pushed (branch names), what is blocked on the owner, and the next step.
3. Never claim a check that was not run; say "not verified" where it applies.
4. Show the user the three to five lines that matter most.
