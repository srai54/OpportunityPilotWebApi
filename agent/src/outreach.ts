import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'
import type { Page } from 'playwright'
import { confirmLinkedInSend, failLinkedInSend, startLinkedInSend, type OutboxItem } from './api.ts'
import { captureDebug } from './browser.ts'
import {
  connectWithNote, decideAction, NoteQuotaError, profilePageUrl, readProfile, sendMessage, type ProfileAction, type SendResult,
} from './platforms/linkedin-people.ts'
import { between, log, sleep, StopRun } from './util.ts'

export type OutreachStatus = 'Sent' | 'DryRun' | 'Refused' | 'Failed' | 'Unconfirmed' | 'Skipped'

export interface OutreachRecord {
  draftId: string
  version: number
  profileUrl: string
  action: 'connect' | 'message' | null
  status: OutreachStatus
  detail: string
  occurredAt: string
  /** The API has the result (confirm/fail answered). False for a Sent record whose confirm did not reach the API yet. */
  synced: boolean
}

/** The agent's own LinkedIn outreach log (JSON lines): the daily cap and "never twice" are checked against it, whatever the API says. */
export class OutreachLog {
  private records: OutreachRecord[] = []

  constructor(private readonly path: string) {
    if (existsSync(path))
      this.records = readFileSync(path, 'utf8').split('\n').filter((l) => l.trim()).flatMap((l) => {
        try { return [JSON.parse(l) as OutreachRecord] } catch { return [] }
      })
  }

  get all(): readonly OutreachRecord[] { return this.records }

  /** Sent today, counting sends LinkedIn did not visibly confirm (they may have gone out). */
  sentToday(now = new Date()): number {
    const day = now.toDateString()
    return this.records.filter((r) => (r.status === 'Sent' || r.status === 'Unconfirmed') && new Date(r.occurredAt).toDateString() === day).length
  }

  /** This exact approved version was sent, may have been sent, or was refused: never try it again (an edit makes a new version). */
  isDone(draftId: string, version: number): boolean {
    return this.records.some((r) => r.draftId === draftId && r.version === version && ['Sent', 'Unconfirmed', 'Refused'].includes(r.status))
  }

  add(record: OutreachRecord) {
    this.records.push(record)
    mkdirSync(dirname(this.path), { recursive: true })
    appendFileSync(this.path, JSON.stringify(record) + '\n')
  }

  markSynced(record: OutreachRecord) {
    record.synced = true
    writeFileSync(this.path, this.records.map((r) => JSON.stringify(r)).join('\n') + '\n')
  }
}

/**
 * Real LinkedIn sends need both the explicit `--submit` (or schedule.outreachSubmit) and `"iUnderstandAccountRisk": true` — the same rule
 * as applying. Returns why a submit is refused, or null when the run may go ahead (a dry run always may).
 */
export function submitRefusal(submit: boolean, iUnderstandAccountRisk: boolean): string | null {
  if (!submit || iUnderstandAccountRisk === true) return null
  return 'To send real LinkedIn notes, set "iUnderstandAccountRisk": true in config.json, then run again.'
}

export interface OutreachOptions {
  page: Page
  base: string
  api: { url: string; key: string }
  log: OutreachLog
  items: OutboxItem[]
  /** false = dry run: fill the note or message, never click Send, and tell the API nothing. */
  submit: boolean
  limit: number
  maxPerDay: number
  pauseSeconds: [number, number]
}

export interface OutreachSummary {
  results: (OutreachRecord & { personName: string | null })[]
  stoppedBecause: string | null
}

/**
 * Sends approved LinkedIn notes from the person's own browser. Dry run by default. With submit, every send is started through the API
 * gateway first (exact approved version, suppression, quota, at most once), then done on LinkedIn, then confirmed with a receipt — or
 * failed with the reason. A send LinkedIn did not visibly confirm after the click is left pending (never failed), so it cannot go twice.
 */
export async function sendOutreach(opts: OutreachOptions): Promise<OutreachSummary> {
  const summary: OutreachSummary = { results: [], stoppedBecause: null }
  const { api } = opts
  const remainingToday = opts.maxPerDay - opts.log.sentToday()
  const limit = Math.min(opts.limit, opts.submit ? remainingToday : opts.limit)
  if (limit <= 0) {
    summary.stoppedBecause = `Daily cap of ${opts.maxPerDay} LinkedIn notes already reached.`
    log(summary.stoppedBecause)
    return summary
  }

  // A send whose confirm did not reach the API last time: confirm it now (409 = already settled there).
  if (opts.submit)
    for (const r of opts.log.all.filter((x) => x.status === 'Sent' && !x.synced)) {
      const c = await confirmLinkedInSend(api, r.draftId, r.detail, r.action ?? 'connect')
      if (c.ok || /409/.test(c.reason)) opts.log.markSynced(r)
    }

  const pending = opts.items.filter((i) => !(opts.submit && opts.log.isDone(i.draftId, i.version)))
  log(`${opts.submit ? 'SUBMIT mode — approved notes will be sent' : 'DRY RUN — notes are filled in but nothing is sent'}. ${pending.length} approved note(s); limit this run: ${limit}.`)
  let attempts = 0
  let notesBlocked: string | null = null
  const [min, max] = opts.pauseSeconds

  const record = (item: OutboxItem, action: OutreachRecord['action'], status: OutreachStatus, detail: string, synced: boolean) => {
    const r: OutreachRecord = { draftId: item.draftId, version: item.version, profileUrl: item.profileUrl, action, status, detail, occurredAt: new Date().toISOString(), synced }
    // A dry run leaves no trace that could block a later real send, except something LinkedIn may really have sent.
    if ((opts.submit && status !== 'Skipped' && status !== 'DryRun') || status === 'Unconfirmed') opts.log.add(r)
    summary.results.push({ ...r, personName: item.personName })
    log(`${status.padEnd(11)} ${item.personName ?? item.profileUrl} — ${item.organization}: ${detail}`)
  }

  try {
    for (const item of pending) {
      if (attempts >= limit) break
      const who = item.personName ?? item.profileUrl
      let started = false
      let clicked = false
      let action: ProfileAction | null = null
      try {
        const state = await readProfile(opts.page, profilePageUrl(item.profileUrl, opts.base))
        action = decideAction(state, item.body)
        if (action.kind === 'connect' && notesBlocked) action = { kind: 'refuse', reason: notesBlocked }
        const kind = action.kind === 'refuse' ? null : action.kind

        if (!opts.submit) {
          if (action.kind === 'refuse') record(item, null, 'Refused', `Would not send: ${action.reason}`, true)
          else {
            const r = action.kind === 'connect' ? await connectWithNote(opts.page, state, item.body, false) : await sendMessage(opts.page, item.body, false)
            if (r.clicked) record(item, kind, 'Unconfirmed', `${r.detail}. Check LinkedIn`, true) // first-click invitation; never expected in a dry run
            else record(item, kind, 'DryRun', `Would send as a ${action.kind === 'connect' ? 'connection note' : 'message'} to ${who}`, true)
          }
          attempts++
        } else {
          const start = await startLinkedInSend(api, item.draftId, item.version)
          if (!start.ok) {
            record(item, kind, 'Skipped', `The app refused to start this send: ${start.reason}`, true)
            if (/quota|limit/i.test(start.reason)) throw new StopRun(`The app's daily limit for LinkedIn notes is reached.`)
            continue
          }
          if (start.data.reused) {
            record(item, kind, 'Skipped', 'This approved text was already started earlier; not sending it again. Check it in the app', true)
            continue
          }
          started = true
          if (action.kind === 'refuse') {
            const f = await failLinkedInSend(api, item.draftId, action.reason)
            record(item, null, 'Refused', action.reason, f.ok)
          } else {
            const r: SendResult = action.kind === 'connect'
              ? await connectWithNote(opts.page, state, item.body, true)
              : await sendMessage(opts.page, item.body, true)
            clicked = r.clicked
            if (r.confirmed) {
              const receipt = `${r.detail} to ${item.profileUrl} by the desktop agent at ${new Date().toISOString()}`
              const c = await confirmLinkedInSend(api, item.draftId, receipt, action.kind)
              record(item, action.kind, 'Sent', receipt, c.ok)
              if (!c.ok) log(`The send went out but the app did not take the receipt (${c.reason}); the next run confirms it again.`)
            } else {
              // It may have gone out: leave the execution pending so it can never be sent twice; the person settles it.
              record(item, action.kind, 'Unconfirmed', `${r.detail}. Check LinkedIn, then confirm or fail this send in the app`, false)
            }
          }
          attempts++
        }
      } catch (e) {
        if (e instanceof NoteQuotaError) notesBlocked = e.message
        const stop = e instanceof StopRun
        const reason = (e as Error).message.split('\n')[0]
        if (started && !clicked) {
          const f = await failLinkedInSend(api, item.draftId, stop ? `Stopped: ${reason}` : reason)
          record(item, action?.kind === 'refuse' ? null : (action?.kind ?? null), e instanceof NoteQuotaError ? 'Refused' : 'Failed', reason, f.ok)
        } else if (started && clicked) {
          record(item, action?.kind === 'refuse' ? null : (action?.kind ?? null), 'Unconfirmed', `${reason} after clicking Send. Check LinkedIn, then confirm or fail this send in the app`, false)
        } else if (e instanceof NoteQuotaError) {
          record(item, null, 'Refused', reason, true)
        } else if (!stop) {
          const evidence = await captureDebug(opts.page, `linkedin-outreach-${item.draftId}`)
          record(item, null, 'Failed', `${reason} (evidence: ${evidence}.png)`, true)
        }
        if (stop) throw e
      }
      if (max > 0) await sleep(between(min * 1000, max * 1000))
    }
  } catch (e) {
    if (!(e instanceof StopRun)) throw e
    summary.stoppedBecause = e.message
    log(`Stopped: ${e.message}`)
  }
  const counts = summary.results.reduce<Record<string, number>>((acc, r) => ({ ...acc, [r.status]: (acc[r.status] ?? 0) + 1 }), {})
  log(`Done. ${Object.entries(counts).map(([k, v]) => `${k}: ${v}`).join(', ') || 'Nothing to send.'}`)
  return summary
}
