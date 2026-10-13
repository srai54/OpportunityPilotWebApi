import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  acquireLock, DEFAULT_SCHEDULE, EMPTY_STATE, inWindow, nextRun, parseWindow, readState, runCycle, schedulerLines, validateSchedule, writeState,
  type CycleDeps, type ScheduleConfig,
} from '../src/schedule.ts'

const base: ScheduleConfig = { ...DEFAULT_SCHEDULE, targets: [{ platform: 'linkedin', campaignId: 'c1' }], jitterMinutes: 0 }
// Local-time dates so the tests do not depend on the machine's time zone.
const at = (y: number, mo: number, d: number, h: number, mi = 0) => new Date(y, mo - 1, d, h, mi)

describe('window and validation', () => {
  it('parses a daily window and rejects nonsense', () => {
    expect(parseWindow('09:00-18:00')).toEqual({ start: 540, end: 1080 })
    expect(parseWindow('9:30-17:05')).toEqual({ start: 570, end: 1025 })
    for (const bad of ['18:00-09:00', '09:00', '25:00-26:00', 'abc', '09:60-10:00']) expect(parseWindow(bad)).toBeNull()
  })
  it('knows whether a moment is inside the window', () => {
    expect(inWindow(at(2026, 10, 13, 9, 0), '09:00-18:00')).toBe(true)
    expect(inWindow(at(2026, 10, 13, 17, 59), '09:00-18:00')).toBe(true)
    expect(inWindow(at(2026, 10, 13, 18, 0), '09:00-18:00')).toBe(false)
    expect(inWindow(at(2026, 10, 13, 8, 59), '09:00-18:00')).toBe(false)
  })
  it('reports every problem in plain words, and an empty schedule is not valid to run', () => {
    expect(validateSchedule(base)).toEqual([])
    const problems = validateSchedule({ ...base, everyHours: 0, window: 'x', jitterMinutes: -1, applyLimit: 99, targets: [{ platform: 'indeed' as never, campaignId: '' }] })
    expect(problems.length).toBe(6)
    expect(validateSchedule(DEFAULT_SCHEDULE)[0]).toContain('schedule.targets')
  })
})

describe('nextRun', () => {
  it('starts now when it is inside the window and nothing ran yet', () => {
    expect(nextRun(at(2026, 10, 13, 10, 0), null, base)).toEqual(at(2026, 10, 13, 10, 0))
  })
  it('waits for the window to open, and moves to tomorrow morning after it closed', () => {
    expect(nextRun(at(2026, 10, 13, 6, 30), null, base)).toEqual(at(2026, 10, 13, 9, 0))
    expect(nextRun(at(2026, 10, 13, 19, 0), null, base)).toEqual(at(2026, 10, 14, 9, 0))
  })
  it('never runs again before everyHours have passed', () => {
    const last = at(2026, 10, 13, 10, 0)
    expect(nextRun(at(2026, 10, 13, 12, 0), last, base)).toEqual(at(2026, 10, 14, 10, 0))
    expect(nextRun(at(2026, 10, 15, 12, 0), last, base)).toEqual(at(2026, 10, 15, 12, 0))
  })
  it('adds a random delay but never past the end of the window', () => {
    const s = { ...base, jitterMinutes: 20 }
    expect(nextRun(at(2026, 10, 13, 10, 0), null, s, () => 0.5)).toEqual(at(2026, 10, 13, 10, 10))
    expect(nextRun(at(2026, 10, 13, 17, 50), null, s, () => 0.99)).toEqual(at(2026, 10, 13, 17, 59))
  })
})

describe('state and lock', () => {
  it('reads an empty state when nothing was written or the file is damaged', () => {
    const dir = mkdtempSync(join(tmpdir(), 'op-sched-'))
    try {
      expect(readState(join(dir, 'none.json'))).toEqual(EMPTY_STATE)
      writeFileSync(join(dir, 'bad.json'), '{not json')
      expect(readState(join(dir, 'bad.json'))).toEqual(EMPTY_STATE)
      writeState(join(dir, 's.json'), { ...EMPTY_STATE, cycles: 3, lastResult: 'ok' })
      expect(readState(join(dir, 's.json')).cycles).toBe(3)
    } finally { rmSync(dir, { recursive: true, force: true }) }
  })
  it('lets one cycle run at a time, frees the lock, and ignores a dead or stale holder', () => {
    const dir = mkdtempSync(join(tmpdir(), 'op-lock-'))
    const lock = join(dir, 'x.lock')
    const now = at(2026, 10, 13, 10, 0)
    try {
      const release = acquireLock(lock, now, 111, () => true)!
      expect(release).toBeTypeOf('function')
      expect(acquireLock(lock, now, 222, () => true)).toBeNull()
      release()
      expect(acquireLock(lock, now, 222, () => true)).not.toBeNull()
      expect(acquireLock(lock, now, 333, () => false)).not.toBeNull() // holder's process is gone
      expect(acquireLock(lock, at(2026, 10, 13, 20, 0), 444, () => true)).not.toBeNull() // lock older than six hours
    } finally { rmSync(dir, { recursive: true, force: true }) }
  })
})

describe('runCycle', () => {
  const clock = () => new Date('2026-10-13T10:00:00Z')
  const run = (schedule: ScheduleConfig, deps: Partial<CycleDeps>, riskAccepted = false) => {
    const calls: string[] = []
    const logs: string[] = []
    const full: CycleDeps = {
      collect: async (t) => { calls.push(`collect ${t.platform}`); return { sent: 4 } },
      apply: async (t, o) => { calls.push(`apply ${t.platform} submit=${o.submit} limit=${o.limit}`); return {} },
      log: (l) => logs.push(l),
      ...deps,
    }
    return runCycle(schedule, { accountRiskAccepted: riskAccepted, maxPerRun: 10, now: clock }, full, EMPTY_STATE).then((state) => ({ state, calls, logs }))
  }

  it('collects first, then applies to what was approved, as a dry run by default', async () => {
    const { state, calls } = await run(base, {})
    expect(calls).toEqual(['collect linkedin', 'apply linkedin submit=false limit=5'])
    expect(state).toMatchObject({ cycles: 1, lastResult: 'ok' })
    expect(state.lastMessage).toContain('dry run done')
  })
  it('submits only when the schedule says so and the account risk was accepted', async () => {
    const on = { ...base, submit: true }
    expect((await run(on, {}, false)).calls[1]).toContain('submit=false')
    expect((await run(on, {}, false)).logs.join()).toContain('dry run')
    expect((await run(on, {}, true)).calls[1]).toContain('submit=true')
  })
  it('never asks for more than the per-run limit', async () => {
    expect((await run({ ...base, applyLimit: 50 }, {})).calls[1]).toContain('limit=10')
  })
  it('stops the whole cycle at a security check or limit and says why', async () => {
    const two = { ...base, targets: [...base.targets, { platform: 'naukri' as const, campaignId: 'c2' }] }
    const { state, calls } = await run(two, { apply: async () => ({ stoppedBecause: 'Security check on the page.' }) })
    expect(calls).toEqual(['collect linkedin'])
    expect(state.lastResult).toBe('stopped')
    expect(state.lastMessage).toContain('Security check')
  })
  it('records a failure with its reason and does not retry the other targets', async () => {
    const two = { ...base, targets: [...base.targets, { platform: 'naukri' as const, campaignId: 'c2' }] }
    const { state, calls } = await run(two, { collect: async () => { throw new Error('Not logged in to LinkedIn.') } })
    expect(calls).toEqual([])
    expect(state.lastResult).toBe('failed')
    expect(state.lastMessage).toContain('Not logged in')
  })
})

describe('scheduler lines', () => {
  it('prints a Windows Task Scheduler line or a cron line, and never installs anything', () => {
    const win = schedulerLines('D:\\agent', '09:05', 'win32')
    expect(win[0]).toContain('schtasks /Create /SC DAILY /ST 09:05')
    expect(win[0]).toContain('npm run agent -- cycle')
    expect(schedulerLines('/home/me/agent', '09:05', 'linux')[0]).toMatch(/^5 9 \* \* \* cd \/home\/me\/agent/)
  })
})
