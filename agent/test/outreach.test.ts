import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { chromium, type Browser, type Page } from 'playwright'
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { circleRequests, linkedInOutbox } from '../src/api.ts'
import { newPreparedPage } from '../src/browser.ts'
import { mapCircles } from '../src/circle.ts'
import { OutreachLog, sendOutreach, submitRefusal } from '../src/outreach.ts'
import { startFakeApi, startMock, type FakeApi, type FakeOutboxItem } from './mock/server.ts'

let mock: Awaited<ReturnType<typeof startMock>>
let api: FakeApi
let browser: Browser
let page: Page
let dir: string

const NOTE = 'Hi, I saw Northwind is hiring a Senior .NET Engineer. I build .NET services. I would like to connect. Ravi'

const item = (slug: string, over: Partial<FakeOutboxItem> = {}): FakeOutboxItem => ({
  draftId: `d-${slug}`, opportunityId: `o-${slug}`, version: 2, profileUrl: `https://www.linkedin.com/in/${slug}`, body: NOTE,
  fitsConnectionNote: true, organization: 'Northwind', title: 'Senior .NET Engineer', personName: slug, ...over,
})

beforeAll(async () => {
  mock = await startMock()
  browser = await chromium.launch()
})
afterAll(async () => {
  await browser?.close()
  await mock?.close()
})
beforeEach(async () => {
  mock.submissions.length = 0
  api = await startFakeApi([])
  dir = mkdtempSync(join(tmpdir(), 'op-people-'))
  page = await newPreparedPage(browser)
  page.setDefaultTimeout(5000)
  return async () => {
    await page.context().close()
    await api.close()
    rmSync(dir, { recursive: true, force: true })
  }
})

const keyed = () => ({ url: api.url, key: 'opk_test' })
const base = () => `${mock.origin}/li`

async function run(submit: boolean, items: FakeOutboxItem[], opts: { limit?: number; maxPerDay?: number; log?: OutreachLog } = {}) {
  api.outbox = items
  const outbox = await linkedInOutbox(keyed())
  if (!outbox.ok) throw new Error(outbox.reason)
  const log = opts.log ?? new OutreachLog(join(dir, 'outreach.jsonl'))
  const summary = await sendOutreach({
    page, base: base(), api: keyed(), log, items: outbox.data, submit, limit: opts.limit ?? 10, maxPerDay: opts.maxPerDay ?? 10, pauseSeconds: [0, 0],
  })
  return { summary, log, by: Object.fromEntries(summary.results.map((r) => [r.draftId, r])) }
}

describe('circle (people around a lead)', () => {
  it('runs the three searches, keeps the people at the company and sends one de-duplicated circle', async () => {
    api.circleRequests = [{ opportunityId: 'o1', mode: 'Job', organization: 'Northwind', title: 'Senior .NET Engineer', location: 'Pune', requestedAt: '2026-10-14T09:00:00Z' }]
    const requests = await circleRequests(keyed())
    if (!requests.ok) throw new Error(requests.reason)
    const summary = await mapCircles({ page, base: base(), api: keyed(), requests: requests.data, limit: 5, pages: 2, pauseSeconds: [0, 0] })
    expect(summary).toMatchObject({ stoppedBecause: null, failed: [], mapped: [{ opportunityId: 'o1', found: 3 }] })
    const sent = api.circles[0]
    expect(sent.opportunityId).toBe('o1')
    expect(sent.people.map((p) => [p.name, p.relation, p.degree, p.profileUrl])).toEqual([
      ['Asha Rao', 'Recruiter', 2, 'https://www.linkedin.com/in/asha-rao'],
      ['Ravi Menon', 'HiringManager', 3, 'https://www.linkedin.com/in/ravi-menon'],
      ['Meera Iyer', 'Team', 1, 'https://www.linkedin.com/in/meera-iyer'],
    ]) // Kim Lee (Contoso), the hidden member and a past-only colleague are left out
    expect(mock.submissions).toHaveLength(0) // nobody is contacted
  })

  it('stops at a LinkedIn security check and leaves the request waiting', async () => {
    const summary = await mapCircles({
      page, base: base(), api: keyed(), limit: 5, pages: 1, pauseSeconds: [0, 0],
      requests: [{ opportunityId: 'o2', mode: 'Job', organization: 'Checkpoint Corp', title: 'Engineer', location: null, requestedAt: '2026-10-14T09:00:00Z' }],
    })
    expect(summary.stoppedBecause).toMatch(/security check/i)
    expect(api.circles).toHaveLength(0)
  })
})

describe('outreach (approved LinkedIn notes)', () => {
  it('refuses --submit unless the account-risk opt-in is set; a dry run is always allowed', () => {
    expect(submitRefusal(true, false)).toMatch(/iUnderstandAccountRisk/)
    expect(submitRefusal(true, true)).toBeNull()
    expect(submitRefusal(false, false)).toBeNull()
  })

  it('is a dry run by default: fills the note and the message, clicks no Send, and tells the API nothing', async () => {
    const { by, summary } = await run(false, [item('asha-rao'), item('meera-iyer', { body: 'x'.repeat(500) }), item('ravi-menon'), item('pending-person'), item('follow-only'), item('asha-rao', { draftId: 'd-long', body: 'y'.repeat(301) })])
    expect(by['d-asha-rao']).toMatchObject({ status: 'DryRun', action: 'connect' })
    expect(by['d-meera-iyer']).toMatchObject({ status: 'DryRun', action: 'message' }) // connected: a long message is fine
    expect(by['d-ravi-menon']).toMatchObject({ status: 'DryRun', action: 'connect' }) // Connect sits in the More menu
    expect(by['d-pending-person']).toMatchObject({ status: 'Refused', detail: expect.stringMatching(/pending/) })
    expect(by['d-follow-only']).toMatchObject({ status: 'Refused', detail: expect.stringMatching(/no Connect/) })
    expect(by['d-long']).toMatchObject({ status: 'Refused', detail: expect.stringMatching(/301 characters/) })
    expect(summary.stoppedBecause).toBeNull()
    expect(mock.submissions).toHaveLength(0) // nothing sent, no message draft left behind
    expect(api.executions).toHaveLength(0)
  })

  it('submit sends the exact approved text once, through start and confirm', async () => {
    const { by, log } = await run(true, [item('asha-rao'), item('meera-iyer', { body: 'Hello Meera, a long message is fine here.\nRavi' })])
    expect(by['d-asha-rao']).toMatchObject({ status: 'Sent', action: 'connect' })
    expect(by['d-meera-iyer']).toMatchObject({ status: 'Sent', action: 'message' })
    expect(mock.submissions).toEqual([
      { platform: 'LinkedIn', job: 'invite:asha-rao', values: { note: NOTE } },
      { platform: 'LinkedIn', job: 'message:meera-iyer', values: { text: 'Hello Meera, a long message is fine here.\nRavi' } },
    ])
    expect(api.executions.map((e) => [e.draftId, e.step])).toEqual([['d-asha-rao', 'start'], ['d-asha-rao', 'confirm'], ['d-meera-iyer', 'start'], ['d-meera-iyer', 'confirm']])
    expect(api.executions[1].body).toMatchObject({ providerReference: 'connect', receipt: expect.stringContaining('https://www.linkedin.com/in/asha-rao') })
    expect(log.sentToday()).toBe(2)

    // Again: the outbox no longer offers them, and the local log would refuse them anyway.
    const again = await run(true, [item('asha-rao'), item('meera-iyer')], { log })
    expect(again.summary.results).toHaveLength(0)
    expect(mock.submissions).toHaveLength(2)
  })

  it('records a refusal through fail, and never sends a note that is too long', async () => {
    const { by } = await run(true, [item('asha-rao', { body: 'z'.repeat(301), fitsConnectionNote: false }), item('pending-person')])
    expect(by['d-asha-rao']).toMatchObject({ status: 'Refused', detail: expect.stringMatching(/301 characters/) })
    expect(by['d-pending-person']).toMatchObject({ status: 'Refused' })
    expect(api.executions.map((e) => e.step)).toEqual(['start', 'fail', 'start', 'fail'])
    expect(mock.submissions).toHaveLength(0)
  })

  it('does not send what was already started elsewhere (reused execution)', async () => {
    api.outbox = [item('asha-rao')]
    await fetch(`${api.url}/api/v1/agent/linkedin-outbox/d-asha-rao/start`, { method: 'POST', headers: { 'X-Agent-Key': 'opk_test' }, body: JSON.stringify({ version: 2 }) })
    const log = new OutreachLog(join(dir, 'outreach.jsonl'))
    const summary = await sendOutreach({ page, base: base(), api: keyed(), log, items: [item('asha-rao')], submit: true, limit: 5, maxPerDay: 5, pauseSeconds: [0, 0] })
    expect(summary.results[0]).toMatchObject({ status: 'Skipped', detail: expect.stringMatching(/already started/) })
    expect(mock.submissions).toHaveLength(0)
  })

  it('keeps to the daily cap and stops at a security check, failing the send it had started', async () => {
    const capped = await run(true, [item('asha-rao'), item('meera-iyer')], { maxPerDay: 1 })
    expect(capped.summary.results.filter((r) => r.status === 'Sent')).toHaveLength(1)
    const full = await run(true, [item('ravi-menon')], { maxPerDay: 1, log: capped.log })
    expect(full.summary.stoppedBecause).toMatch(/daily cap/i)

    mock.submissions.length = 0
    const stopped = await run(true, [item('captcha-person'), item('asha-rao', { draftId: 'd-after' })])
    expect(stopped.summary.stoppedBecause).toMatch(/CAPTCHA/)
    expect(stopped.summary.results).toHaveLength(0) // it stopped while reading the profile, before starting anything
    expect(mock.submissions).toHaveLength(0)
  })
})
