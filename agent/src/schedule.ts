import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

export type PlatformName = 'linkedin' | 'naukri' | 'instahyre'

export interface ScheduleTarget {
  platform: PlatformName
  /** The Job campaign to collect for (see: npm run agent -- campaigns). */
  campaignId: string
}

/**
 * What `agent watch` / `agent cycle` do on a timer. One cycle = for each target: collect new postings for the campaign, then apply
 * to the jobs the person has already approved in the web app. Nothing is applied to that was not approved.
 */
export interface ScheduleConfig {
  targets: ScheduleTarget[]
  /** Hours between cycles. */
  everyHours: number
  /** Local time of day the agent may run, "HH:MM-HH:MM". Outside it the agent waits. */
  window: string
  /** A random delay up to this many minutes is added to every start so runs do not look machine-regular. */
  jitterMinutes: number
  /** false = every scheduled apply is a dry run. true also needs "iUnderstandAccountRisk": true. */
  submit: boolean
  /** Applications per target per cycle (never above limits.maxApplicationsPerRun). */
  applyLimit: number
  /** Map the LinkedIn circle of every lead you asked for (read-only; nothing is sent). Default on. */
  circle: boolean
  /** Send your approved LinkedIn notes in each cycle. Only real sends (never a scheduled dry run); also needs "iUnderstandAccountRisk": true. Default off. */
  outreachSubmit: boolean
}

export const DEFAULT_SCHEDULE: ScheduleConfig = {
  targets: [], everyHours: 24, window: '09:00-18:00', jitterMinutes: 20, submit: false, applyLimit: 5, circle: true, outreachSubmit: false,
}

const PLATFORMS: PlatformName[] = ['linkedin', 'naukri', 'instahyre']

export function parseWindow(window: string): { start: number; end: number } | null {
  const m = /^(\d{1,2}):(\d{2})-(\d{1,2}):(\d{2})$/.exec(window.trim())
  if (!m) return null
  const [sh, sm, eh, em] = m.slice(1).map(Number)
  if (sh > 23 || eh > 23 || sm > 59 || em > 59) return null
  const start = sh * 60 + sm
  const end = eh * 60 + em
  return end > start ? { start, end } : null
}

/** Every problem at once, in words a non-developer can act on. Empty = valid. */
export function validateSchedule(s: ScheduleConfig): string[] {
  const problems: string[] = []
  if (!(s.everyHours >= 1 && s.everyHours <= 168)) problems.push('schedule.everyHours must be between 1 and 168')
  if (!parseWindow(s.window)) problems.push('schedule.window must look like "09:00-18:00" and end after it starts')
  if (!(s.jitterMinutes >= 0 && s.jitterMinutes <= 120)) problems.push('schedule.jitterMinutes must be between 0 and 120')
  if (!(s.applyLimit >= 1 && s.applyLimit <= 50)) problems.push('schedule.applyLimit must be between 1 and 50')
  if (!Array.isArray(s.targets) || s.targets.length === 0) problems.push('schedule.targets needs at least one { "platform": "linkedin", "campaignId": "..." }')
  else for (const t of s.targets) {
    if (!PLATFORMS.includes(t.platform)) problems.push(`schedule target platform "${t.platform}" must be linkedin, naukri or instahyre`)
    if (!t.campaignId || typeof t.campaignId !== 'string') problems.push('every schedule target needs a campaignId')
  }
  return problems
}

const minutesOfDay = (d: Date) => d.getHours() * 60 + d.getMinutes()

export function inWindow(now: Date, window: string): boolean {
  const w = parseWindow(window)
  if (!w) return false
  const m = minutesOfDay(now)
  return m >= w.start && m < w.end
}

/**
 * When the next cycle should start: not before `everyHours` after the last one, never outside the daily window (a start after the
 * window moves to the next morning), plus a random delay that still ends before the window closes. `rand` is injected for tests.
 */
export function nextRun(now: Date, last: Date | null, s: ScheduleConfig, rand: () => number = Math.random): Date {
  const w = parseWindow(s.window) ?? { start: 9 * 60, end: 18 * 60 }
  let t = new Date(Math.max(now.getTime(), last ? last.getTime() + s.everyHours * 3_600_000 : 0))
  const at = (d: Date, minutes: number) => { const x = new Date(d); x.setHours(0, 0, 0, 0); x.setMinutes(minutes); return x }
  if (minutesOfDay(t) < w.start) t = at(t, w.start)
  else if (minutesOfDay(t) >= w.end) { const tomorrow = new Date(t); tomorrow.setDate(tomorrow.getDate() + 1); t = at(tomorrow, w.start) }
  const jitter = Math.floor(rand() * (s.jitterMinutes + 1))
  const jittered = new Date(t.getTime() + jitter * 60_000)
  const latest = at(t, w.end - 1)
  return jittered > latest ? latest : jittered
}

export interface ScheduleState {
  cycles: number
  lastStart: string | null
  lastEnd: string | null
  lastResult: 'ok' | 'stopped' | 'failed' | null
  lastMessage: string | null
}

export const EMPTY_STATE: ScheduleState = { cycles: 0, lastStart: null, lastEnd: null, lastResult: null, lastMessage: null }

export function readState(path: string): ScheduleState {
  if (!existsSync(path)) return { ...EMPTY_STATE }
  try { return { ...EMPTY_STATE, ...JSON.parse(readFileSync(path, 'utf8')) } } catch { return { ...EMPTY_STATE } }
}

export function writeState(path: string, state: ScheduleState) {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, JSON.stringify(state, null, 2) + '\n')
}

const LOCK_STALE_MS = 6 * 3_600_000

/** One cycle at a time: returns a release function, or null when another live run holds the lock. A crashed run's lock expires. */
export function acquireLock(path: string, now: Date, pid: number, isAlive: (pid: number) => boolean = pidAlive): (() => void) | null {
  if (existsSync(path)) {
    try {
      const held = JSON.parse(readFileSync(path, 'utf8')) as { pid: number; at: string }
      if (isAlive(held.pid) && now.getTime() - Date.parse(held.at) < LOCK_STALE_MS) return null
    } catch { /* unreadable lock: take it */ }
  }
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, JSON.stringify({ pid, at: now.toISOString() }))
  return () => { try { rmSync(path, { force: true }) } catch { /* already gone */ } }
}

function pidAlive(pid: number): boolean {
  try { process.kill(pid, 0); return true } catch { return false }
}

export interface CycleDeps {
  /** Reads the postings and sends them to the campaign. Throws with a readable message on failure. */
  collect: (t: ScheduleTarget) => Promise<{ sent: number }>
  /** Applies to the approved jobs. `stoppedBecause` is set when the run met a security check, a limit or a logged-out session. */
  apply: (t: ScheduleTarget, opts: { submit: boolean; limit: number }) => Promise<{ stoppedBecause?: string | null }>
  /** Maps the circles you asked for (when schedule.circle is on). */
  circle?: () => Promise<{ stoppedBecause?: string | null; mapped: number }>
  /** Sends approved LinkedIn notes for real (only when schedule.outreachSubmit and the account-risk opt-in are both on). */
  outreach?: () => Promise<{ stoppedBecause?: string | null; sent: number }>
  log: (line: string) => void
}

/** One scheduled cycle over every target. A stop (security check, logged out, limit) ends the whole cycle so nothing else is tried. */
export async function runCycle(schedule: ScheduleConfig, opts: { accountRiskAccepted: boolean; maxPerRun: number; now: () => Date }, deps: CycleDeps, previous: ScheduleState): Promise<ScheduleState> {
  const submit = schedule.submit && opts.accountRiskAccepted
  if (schedule.submit && !opts.accountRiskAccepted) deps.log('schedule.submit is on but "iUnderstandAccountRisk" is not true: applying as a dry run.')
  const limit = Math.min(schedule.applyLimit, opts.maxPerRun)
  const state: ScheduleState = { ...previous, cycles: previous.cycles + 1, lastStart: opts.now().toISOString(), lastEnd: null, lastResult: 'ok', lastMessage: null }
  const notes: string[] = []
  for (const t of schedule.targets) {
    const label = `${t.platform} / ${t.campaignId}`
    try {
      const c = await deps.collect(t)
      notes.push(`${label}: collected ${c.sent}`)
      deps.log(`${label}: sent ${c.sent} posting(s)`)
      const a = await deps.apply(t, { submit, limit })
      if (a.stoppedBecause) {
        state.lastResult = 'stopped'
        notes.push(`${label}: stopped — ${a.stoppedBecause}`)
        deps.log(`${label}: stopped — ${a.stoppedBecause}`)
        break
      }
      notes.push(`${label}: ${submit ? 'applied' : 'dry run'} done`)
    } catch (e) {
      state.lastResult = 'failed'
      notes.push(`${label}: ${(e as Error).message}`)
      deps.log(`${label}: ${(e as Error).message}`)
      // A logged-out session or unreachable API will fail every later target the same way: stop rather than retry them all.
      break
    }
  }
  // People work after the job targets, and only when nothing above stopped or failed.
  const step = async (label: string, run: () => Promise<{ stoppedBecause?: string | null; note: string }>) => {
    if (state.lastResult !== 'ok') return
    try {
      const r = await run()
      if (r.stoppedBecause) {
        state.lastResult = 'stopped'
        notes.push(`${label}: stopped — ${r.stoppedBecause}`)
        deps.log(`${label}: stopped — ${r.stoppedBecause}`)
      } else notes.push(`${label}: ${r.note}`)
    } catch (e) {
      state.lastResult = 'failed'
      notes.push(`${label}: ${(e as Error).message}`)
      deps.log(`${label}: ${(e as Error).message}`)
    }
  }
  if (schedule.circle && deps.circle) {
    const circle = deps.circle
    await step('circle', async () => { const r = await circle(); return { stoppedBecause: r.stoppedBecause, note: `mapped ${r.mapped}` } })
  }
  if (schedule.outreachSubmit && !opts.accountRiskAccepted) deps.log('schedule.outreachSubmit is on but "iUnderstandAccountRisk" is not true: no LinkedIn notes are sent.')
  if (schedule.outreachSubmit && opts.accountRiskAccepted && deps.outreach) {
    const outreach = deps.outreach
    await step('outreach', async () => { const r = await outreach(); return { stoppedBecause: r.stoppedBecause, note: `sent ${r.sent}` } })
  }
  state.lastEnd = opts.now().toISOString()
  state.lastMessage = notes.join(' · ')
  return state
}

/** The line that makes the operating system start `agent cycle` for you (Windows Task Scheduler or cron). Printed, never installed. */
export function schedulerLines(agentDir: string, startTime: string, platform: NodeJS.Platform): string[] {
  const [h, m] = startTime.split(':').map((x) => Number(x))
  if (platform === 'win32') {
    return [`schtasks /Create /SC DAILY /ST ${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')} /TN "OpportunityPilot agent" /TR "cmd /c cd /d ${agentDir} && npm run agent -- cycle" /F`,
      'Remove it later with: schtasks /Delete /TN "OpportunityPilot agent" /F']
  }
  return [`${m} ${h} * * * cd ${agentDir} && npm run agent -- cycle >> .data/cycle.log 2>&1`, 'Add that line with: crontab -e']
}
