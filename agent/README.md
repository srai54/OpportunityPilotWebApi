# OpportunityPilot agent

Works with the web app's **research** pipeline, from your own logged-in browser on
**LinkedIn (Easy Apply)** and **Naukri**, using answers you saved — no AI, no API keys.

```
Campaign (your criteria) ──► agent collect ──► postings with their text ──► research job
                                                                     (evidence, fit score, gaps)
   you shortlist the best matches in the web app ◄── Opportunities ◄──┘
                     │
                     └──► agent apply ──► only shortlisted jobs ──► opportunity marked Applied
```

The agent never applies to anything you did not shortlist.

> **Unofficial automation.** LinkedIn's and Naukri's terms prohibit automated use;
> they can restrict the account being automated. Keep the limits low. The agent
> does not hide that it is automated and stops at any security check.

## Setup (once)

Requires Node 20.19+ (22 or 24 recommended).

```bash
cd OpportunityPilotWebApi/agent
npm install
npx playwright install chromium
npm run agent -- init                 # creates config.json (git-ignored)
```

Edit `config.json`:

1. **`api.key`** — on the web app's Applications page → *Create agent key* → copy.
2. **`search`** — keywords, location, filters (see below).
3. **`answers`** — your notice period, salaries, phone, city, years per skill, yes/no
   positions. **This is what fills the forms.** Anything not covered is never guessed.
4. **`profile.resumePath`** — optional; uploaded only when a form demands a resume.

Then log in once per site, in the window that opens (2-step checks included):

```bash
npm run agent -- login linkedin
npm run agent -- login naukri
```

The session is kept in `.data/browser-profile/` on this computer. Your password is
never seen or stored by the agent.

## Running

```bash
npm run agent -- campaigns                               # your Job campaigns and their ids
npm run agent -- collect linkedin --campaign <id>        # search with the campaign's keywords/locations,
                                                         # send postings to it and queue research
# → in the web app: Campaigns → the campaign → Opportunities → Shortlist the ones you want
npm run agent -- apply linkedin                          # DRY RUN over your shortlist: fills forms, sends nothing
npm run agent -- apply linkedin --submit --limit 2       # real applications
npm run agent -- status                                  # what the local log holds
npm run agent -- sync                                    # re-upload results the web app is missing
```

`--submit` only works after you set `"iUnderstandAccountRisk": true` in config.json.

**First real use — do it in this order:**
1. Create a **Job** campaign in the web app with keywords, required skills, locations and
   your years of experience.
2. `collect linkedin --campaign <id>`. Watch the window — it only reads postings.
3. In the web app, open the research run and then the opportunities: each has a fit score,
   the evidence behind it and what is unknown. Shortlist a few.
4. `apply linkedin` (dry run). For every *Needs you*, add a rule to `answers.fields`.
5. `apply linkedin --submit --limit 2`, check those two applications on LinkedIn, then raise the limit.

Naukri sends an application the moment Apply is clicked, so its dry run stops *before*
Apply; its recruiter questionnaire is only exercised with `--submit`.

## Scheduling (run the campaign and apply on a timer)

A **cycle** is: for each target, `collect` new postings for the campaign (research then scores them), then `apply` to the jobs **you already approved** in the web app. Nothing is applied to that you did not approve, and a cycle is a **dry run** unless you turn `schedule.submit` on.

Add this to `config.json`:

```json
"schedule": {
  "targets": [{ "platform": "linkedin", "campaignId": "<id from: npm run agent -- campaigns>" }],
  "everyHours": 24,
  "window": "09:00-18:00",
  "jitterMinutes": 20,
  "submit": false,
  "applyLimit": 5
}
```

```bash
npm run agent -- cycle       # one round now (what the timer runs)
npm run agent -- watch       # keep running: next cycle every everyHours, only inside the window, random delay added
npm run agent -- schedule    # prints the Windows Task Scheduler / cron line that starts `cycle` for you
npm run agent -- status      # shows the last cycle and its result
```

| Rule | Behaviour |
|---|---|
| One at a time | A lock file stops two cycles overlapping; a crashed run's lock expires after 6 hours |
| Stops on trouble | A security check, a logged-out session or a daily limit ends the cycle (exit code 2); `watch` exits and waits for you |
| No retry storm | A failed collect/apply stops the cycle; three failed cycles in a row stop `watch` |
| Real applications | Only when `schedule.submit` is `true` **and** `iUnderstandAccountRisk` is `true`; still capped by `applyLimit`, `maxApplicationsPerRun` and `maxApplicationsPerDay` |
| Your computer | The browser window opens on your desktop; keep the computer on and signed in at the scheduled time |

Verified: the timing, window, lock, ordering and stop rules are unit-tested. **Not verified:** a scheduled run against the live LinkedIn/Naukri sites (the agent has not been run live yet).

## What each result means

| Status | Meaning |
|---|---|
| Applied | Submitted and the site confirmed it |
| Dry run | Form filled to the last step, not sent |
| Needs you | A required question your answers don't cover — nothing was sent; apply by hand or add a rule |
| Skipped | Filtered out, already applied, or applies on the company website |
| Failed | Unexpected page; a screenshot + HTML is saved in `.data/debug/` |

A shortlisted job is never applied to twice once it is Applied, Skipped or Needs you (dry
runs don't count). The run stops on a security check, a logged-out session, the site's daily
limit, `limits.maxApplicationsPerDay`, or three failures in a row.

## config.json reference

| Key | Meaning |
|---|---|
| `api.url` / `api.key` | where results go; the key comes from the Applications page |
| `iUnderstandAccountRisk` | must be `true` for `--submit` |
| `limits.maxApplicationsPerRun` / `maxApplicationsPerDay` | caps (applied or dry-run jobs) |
| `limits.pauseSeconds` | `[min, max]` pause between applications (collect pauses less) |
| `limits.maxPages` | result pages per keyword when collecting |
| `limits.maxPostingsPerCollect` | postings opened and sent per collect run |
| `search.postedWithinDays` | only postings this recent |
| `schedule.*` | targets, everyHours, window, jitterMinutes, submit, applyLimit (see Scheduling) |
| `profile.resumePath` | uploaded only when a form demands a resume |
| `profile.followCompanies` | LinkedIn's "follow company" box (default off) |
| `profile.acceptTermsCheckboxes` | tick "I agree / I confirm" boxes for you (default **off** — such forms become *Needs you*) |
| `answers.fields` | `{ "match": ["notice period"], "value": "30" }` — first rule whose phrase appears in the question wins |
| `answers.skills` | `"c#": 6` answers "How many years of experience with C#?" |
| `answers.defaults.yearsOfExperience` | experience in a skill you didn't list; default `null` = *Needs you* |
| `answers.defaults.yesNo` | Yes/No questions no rule covers; default `""` = *Needs you* (set `"Yes"`/`"No"` only if you want a blanket answer) |

Keywords, locations, work mode, skills and exclusions come from the **campaign** —
research applies them with evidence, so the agent does not filter on its own.
Nothing is assumed: every answer comes from a rule you wrote, and anything else is left for you.

## When something breaks

Sites change their pages. If a run reports *Failed* or stops with "the site layout has
probably changed", send the newest files from `.data/debug/` (a `.png` screenshot and
`.html`) — the selectors can be fixed from them.

## Development

```bash
npm run typecheck
npm test        # unit tests + end-to-end runs against a local mock of LinkedIn and Naukri
```

The mock (`test/mock/server.ts`) mirrors the real sites' structure; it proves the
agent's logic, not that the live sites are unchanged. InstaHyre is not supported yet.
