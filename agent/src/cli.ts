import { join } from 'node:path'
import { createInterface } from 'node:readline/promises'
import { agentCampaigns, report, sendPostings, shortlist } from './api.ts'
import { collectPostings } from './collect.ts'
import { openBrowser } from './browser.ts'
import { AGENT_DIR, CONFIG_PATH, DATA_DIR, loadConfig, writeExampleConfig } from './config.ts'
import { linkedIn } from './platforms/linkedin.ts'
import { naukri } from './platforms/naukri.ts'
import { instahyre } from './platforms/instahyre.ts'
import type { PlatformAdapter } from './platforms/types.ts'
import { applyToShortlist } from './run.ts'
import { Store } from './store.ts'
import {
  acquireLock, inWindow, nextRun, readState, runCycle, schedulerLines, validateSchedule, writeState,
  type ScheduleTarget,
} from './schedule.ts'

const HELP = `OpportunityPilot agent — research input and applications from your own logged-in browser.

  npm run agent -- init                      create config.json (answers, limits, agent key)
  npm run agent -- login <linkedin|naukri|instahyre>   log in once in the browser window that opens
  npm run agent -- campaigns                 list your Job campaigns and their ids
  npm run agent -- collect <linkedin|naukri|instahyre> --campaign <id>
                                             search with the campaign's criteria and send the postings
                                             to it for research (nothing is applied to)
  npm run agent -- apply <linkedin|naukri|instahyre>   dry run over the jobs YOU shortlisted after research
        --submit      send real applications (needs "iUnderstandAccountRisk": true)
        --limit N     at most N applications this run
        --headless    no visible window (not recommended)
  npm run agent -- cycle                     one scheduled round now: for each schedule.targets entry, collect, then apply
                                             to the jobs you approved (a dry run unless schedule.submit is true)
  npm run agent -- watch                     keep running: a cycle every schedule.everyHours inside schedule.window
  npm run agent -- schedule                  print the Task Scheduler / cron line that starts a cycle for you
  npm run agent -- sync                      upload results the web app has not received
  npm run agent -- status                    summary of the local log
`

const RISK = `Unofficial automation: job platforms may prohibit automated use and may restrict
your account. Keep the limits in config.json low. The agent stops at any security check.`

function adapterFor(name: string | undefined): PlatformAdapter {
  if (name === 'linkedin') return linkedIn()
  if (name === 'naukri') return naukri()
  if (name === 'instahyre') return instahyre()
  throw new Error(`Choose a platform: linkedin, naukri or instahyre (got "${name ?? ''}").`)
}

const store = () => new Store(join(DATA_DIR, 'applications.jsonl'))

async function collectRun(platform: string | undefined, campaignId: string | undefined, opts: { headless: boolean; limit?: number }): Promise<{ sent: number }> {
  const adapter = adapterFor(platform)
  const config = loadConfig()
  if (!campaignId) throw new Error('Which campaign? Add --campaign <id> (see: npm run agent -- campaigns)')
  const campaigns = await agentCampaigns(config.api)
  if (!campaigns.ok) throw new Error(`Could not read the campaign: ${campaigns.reason}`)
  const campaign = campaigns.data.find((c) => c.id === campaignId)
  if (!campaign) throw new Error(`No Job campaign ${campaignId}. See: npm run agent -- campaigns`)

  const { context, page } = await openBrowser({ headless: opts.headless })
  try {
    if (!(await adapter.isLoggedIn(page))) throw new Error(`Not logged in to ${adapter.platform}. Run: npm run agent -- login ${platform}`)
    const jobs = await collectPostings({
      adapter,
      page,
      campaign,
      postedWithinDays: config.search.postedWithinDays,
      maxPages: config.limits.maxPages,
      limit: opts.limit ?? config.limits.maxPostingsPerCollect,
      pauseSeconds: config.limits.pauseSeconds,
      skip: (id) => store().isDone(adapter.platform, id),
    })
    if (jobs.length === 0) { console.log('No new postings found.'); return { sent: 0 } }
    let jobId: string | null | undefined
    for (let i = 0; i < jobs.length; i += 100) {
      const batch = jobs.slice(i, i + 100)
      const sent = await sendPostings(config.api, campaign.id, adapter.platform, batch, i + 100 >= jobs.length)
      if (!sent.ok) throw new Error(`Could not send postings: ${sent.reason}`)
      jobId = sent.data.jobId ?? jobId
    }
    console.log(`Sent ${jobs.length} posting(s) to "${campaign.name}"${jobId ? ' and queued research' : ''}.`)
    console.log(`Next: open the campaign's Opportunities in the web app, shortlist the jobs you want, then run: npm run agent -- apply ${platform}`)
    return { sent: jobs.length }
  } finally {
    await context.close()
  }
}

async function applyRun(platform: string | undefined, opts: { submit: boolean; limit?: number; headless: boolean }): Promise<{ stoppedBecause: string | null; refused?: boolean }> {
  const adapter = adapterFor(platform)
  const config = loadConfig()
  if (opts.submit && !config.iUnderstandAccountRisk) {
    console.error(`${RISK}

To send real applications, set "iUnderstandAccountRisk": true in config.json, then run again.`)
    return { stoppedBecause: null, refused: true }
  }
  const list = await shortlist(config.api, adapter.platform)
  if (!list.ok) throw new Error(`Could not read your shortlist: ${list.reason}`)
  console.log(`${RISK}
`)
  const jobs = list.data.map((x) => ({
    externalJobId: x.externalId,
    url: x.url,
    title: x.title,
    company: x.organization,
    location: null,
    opportunityId: x.opportunityId,
    coverNote: x.coverNote ?? undefined,
  }))
  const summary = await applyToShortlist({ adapter, config, store: store(), jobs, submit: opts.submit, limit: opts.limit, headless: opts.headless })
  return { stoppedBecause: summary.stoppedBecause }
}

const STATE_PATH = join(DATA_DIR, 'schedule-state.json')
const LOCK_PATH = join(DATA_DIR, 'schedule.lock')

/** One scheduled round over every target. Returns the process exit code: 0 ok, 1 failed, 2 stopped by a guard. */
async function cycleOnce(): Promise<number> {
  const config = loadConfig()
  const problems = validateSchedule(config.schedule)
  if (problems.length) throw new Error(`The schedule in config.json needs fixing:\n  - ${problems.join('\n  - ')}`)
  const release = acquireLock(LOCK_PATH, new Date(), process.pid)
  if (!release) { console.log('Another cycle is already running; this one was skipped.'); return 0 }
  try {
    const state = await runCycle(config.schedule, { accountRiskAccepted: config.iUnderstandAccountRisk, maxPerRun: config.limits.maxApplicationsPerRun, now: () => new Date() }, {
      collect: (t: ScheduleTarget) => collectRun(t.platform, t.campaignId, { headless: false }),
      apply: async (t: ScheduleTarget, o) => {
        const r = await applyRun(t.platform, { submit: o.submit, limit: o.limit, headless: false })
        return { stoppedBecause: r.stoppedBecause }
      },
      log: (line) => console.log(line),
    }, readState(STATE_PATH))
    writeState(STATE_PATH, state)
    console.log(`Cycle ${state.cycles}: ${state.lastResult}. ${state.lastMessage ?? ''}`)
    return state.lastResult === 'ok' ? 0 : state.lastResult === 'stopped' ? 2 : 1
  } finally {
    release()
  }
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

/** Keeps running: waits for the next allowed time, runs a cycle, repeats. Stops by itself after a guard stop or three failed cycles. */
async function watch(): Promise<number> {
  const config = loadConfig()
  const problems = validateSchedule(config.schedule)
  if (problems.length) throw new Error(`The schedule in config.json needs fixing:\n  - ${problems.join('\n  - ')}`)
  let failures = 0
  for (;;) {
    const state = readState(STATE_PATH)
    const when = nextRun(new Date(), state.lastStart ? new Date(state.lastStart) : null, config.schedule)
    console.log(`Next cycle at ${when.toLocaleString()} (window ${config.schedule.window}). Press Ctrl+C to stop.`)
    while (Date.now() < when.getTime()) await sleep(Math.min(60_000, when.getTime() - Date.now()))
    if (!inWindow(new Date(), config.schedule.window)) continue
    const code = await cycleOnce()
    if (code === 2) { console.log('Stopped by a guard (security check, logged out or a limit). Fix it, then start watch again.'); return 2 }
    failures = code === 1 ? failures + 1 : 0
    if (failures >= 3) { console.log('Three cycles in a row failed. Stopping so nothing keeps retrying.'); return 1 }
  }
}

async function main(argv: string[]) {
  const [command, platform, ...rest] = argv
  const flag = (name: string) => rest.includes(name) || platform === name
  const option = (name: string) => {
    const i = rest.indexOf(name)
    return i >= 0 ? rest[i + 1] : undefined
  }
  const limit = option('--limit') !== undefined ? Number(option('--limit')) : undefined
  if (limit !== undefined && !(limit > 0)) throw new Error('--limit needs a number above 0')

  switch (command) {
    case 'init': {
      const created = writeExampleConfig()
      console.log(
        created
          ? `Created ${CONFIG_PATH}\nEdit it: your search, your answers (salary, notice period, years per skill…) and api.key from the web app's Applications page.`
          : `${CONFIG_PATH} already exists; left unchanged.`,
      )
      return
    }

    case 'login': {
      const adapter = adapterFor(platform)
      const { context, page } = await openBrowser({ headless: false })
      await page.goto(adapter.loginUrl)
      const rl = createInterface({ input: process.stdin, output: process.stdout })
      await rl.question(`Log in to ${adapter.platform} in the browser window (complete any 2-step check). Then press Enter here… `)
      rl.close()
      const ok = await adapter.isLoggedIn(page)
      await context.close()
      console.log(ok ? `Logged in to ${adapter.platform}. The session is saved on this computer only.` : `Still not logged in to ${adapter.platform}. Run login again.`)
      process.exitCode = ok ? 0 : 1
      return
    }

    case 'campaigns': {
      const config = loadConfig()
      const result = await agentCampaigns(config.api)
      if (!result.ok) throw new Error(`Could not list campaigns: ${result.reason}`)
      if (result.data.length === 0) return console.log('No Job campaigns yet. Create one in the web app (Campaigns → New campaign, mode Jobs).')
      for (const c of result.data) console.log(`${c.id}  ${c.name}  — keywords: ${c.criteria.keywords.join(', ') || '(none)'}`)
      return
    }

    case 'collect': {
      await collectRun(platform, option('--campaign'), { headless: flag('--headless'), limit })
      return
    }

    case 'apply': {
      const r = await applyRun(platform, { submit: flag('--submit'), limit, headless: flag('--headless') })
      if (r.refused) process.exitCode = 1
      else if (r.stoppedBecause) process.exitCode = 2
      return
    }

    case 'cycle': {
      process.exitCode = await cycleOnce()
      return
    }

    case 'watch': {
      process.exitCode = await watch()
      return
    }

    case 'schedule': {
      const config = loadConfig()
      const w = config.schedule.window.split('-')[0] ?? '09:00'
      console.log('Edit "schedule" in config.json first (targets, everyHours, window, submit). Then add this so your computer starts a cycle for you:\n')
      for (const line of schedulerLines(AGENT_DIR, w, process.platform)) console.log('  ' + line)
      console.log('\nThe agent window opens on your desktop when it runs, so keep the computer on and signed in. Applying stays a dry run until schedule.submit is true.')
      return
    }

    case 'sync': {
      const config = loadConfig()
      const s = store()
      const pending = s.all.filter((r) => !r.synced)
      if (pending.length === 0) return console.log('Nothing to sync.')
      for (let i = 0; i < pending.length; i += 100) {
        const batch = pending.slice(i, i + 100)
        const result = await report(config.api, batch)
        if (!result.ok) {
          console.error(`Sync failed: ${result.reason}`)
          process.exitCode = 1
          return
        }
        s.markSynced(batch)
      }
      console.log(`Synced ${pending.length} result(s) to the web app.`)
      return
    }

    case 'status': {
      const s = store()
      const rows = new Map<string, number>()
      for (const r of s.all) rows.set(`${r.platform} ${r.status}`, (rows.get(`${r.platform} ${r.status}`) ?? 0) + 1)
      console.log(`Local log: ${s.all.length} record(s), ${s.all.filter((r) => !r.synced).length} not yet synced, ${s.appliedToday()} applied today.`)
      for (const [k, v] of [...rows].sort()) console.log(`  ${k.padEnd(24)} ${v}`)
      const cycle = readState(STATE_PATH)
      if (cycle.cycles > 0) console.log(`Last scheduled cycle (#${cycle.cycles}): ${cycle.lastResult} at ${cycle.lastEnd ?? cycle.lastStart}. ${cycle.lastMessage ?? ''}`)
      return
    }

    default:
      console.log(HELP)
  }
}

main(process.argv.slice(2)).catch((e: Error) => {
  console.error(e.message)
  process.exitCode = 1
})
