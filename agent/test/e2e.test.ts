import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { chromium, type Browser, type Page } from 'playwright'
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { sendPostings, type AgentCampaign } from '../src/api.ts'
import { newPreparedPage } from '../src/browser.ts'
import { collectPostings, searchFor } from '../src/collect.ts'
import { EXAMPLE_CONFIG, type Config } from '../src/config.ts'
import { linkedIn } from '../src/platforms/linkedin.ts'
import { naukri } from '../src/platforms/naukri.ts'
import type { JobRef } from '../src/platforms/types.ts'
import { applyToShortlist } from '../src/run.ts'
import { Store } from '../src/store.ts'
import { startFakeApi, startMock, type FakeApi } from './mock/server.ts'

let mock: Awaited<ReturnType<typeof startMock>>
let browser: Browser
let page: Page
let dir: string

const campaign: AgentCampaign = {
  id: 'c1',
  name: '.NET jobs',
  mode: 'Job',
  criteria: { keywords: ['dotnet'], locations: ['Remote', 'Bengaluru'], workModes: ['Hybrid'] },
}

const config = (api = { url: '', key: '' }): Config => ({
  ...EXAMPLE_CONFIG,
  api,
  limits: { ...EXAMPLE_CONFIG.limits, maxApplicationsPerRun: 10, maxApplicationsPerDay: 25, pauseSeconds: [0, 0], maxPages: 1, maxPostingsPerCollect: 30 },
  answers: { ...EXAMPLE_CONFIG.answers, skills: { 'c#': 6 } },
})

/** A shortlist as the API returns it, for jobs in the mock site. */
const shortlisted = (base: string, path: (id: string) => string, ids: string[]): JobRef[] =>
  ids.map((id) => ({ externalJobId: id, url: `${base}${path(id)}`, title: '', company: '', location: null, opportunityId: `opp-${id}` }))

const byJob = (outcomes: { job: { externalJobId: string }; status: string; detail: string }[]) =>
  Object.fromEntries(outcomes.map((o) => [o.job.externalJobId, o]))

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
  dir = mkdtempSync(join(tmpdir(), 'op-agent-'))
  page = await newPreparedPage(browser)
  page.setDefaultTimeout(5000)
  return async () => {
    await page.context().close()
    rmSync(dir, { recursive: true, force: true })
  }
})

describe('collect (research input)', () => {
  it('builds the platform search from the campaign criteria', () => {
    expect(searchFor(campaign, 7)).toEqual({ location: 'Bengaluru', remote: 'hybrid', postedWithinDays: 7 })
    expect(searchFor({ ...campaign, criteria: { ...campaign.criteria, locations: [], workModes: ['Remote', 'Hybrid'] } }, 3)).toEqual({
      location: '',
      remote: 'any',
      postedWithinDays: 3,
    })
  })

  it('reads every posting with its description and sends them to the campaign without applying', async () => {
    const api: FakeApi = await startFakeApi([campaign])
    try {
      const jobs = await collectPostings({
        adapter: linkedIn(`${mock.origin}/li`),
        page,
        campaign,
        postedWithinDays: 7,
        maxPages: 1,
        limit: 30,
        pauseSeconds: [0, 0],
      })
      expect(jobs.map((j) => j.externalJobId).sort()).toEqual(['1001', '1002', '1003', '1004', '1005'])
      expect(jobs.find((j) => j.externalJobId === '1001')?.description).toMatch(/C#, \.NET and SQL.*3-5 years/s)

      const sent = await sendPostings({ url: api.url, key: 'opk_test' }, campaign.id, 'LinkedIn', jobs, true)
      expect(sent).toMatchObject({ ok: true, data: { jobId: 'j1' } })
      expect(api.postings[0]).toMatchObject({ campaignId: 'c1', body: { platform: 'LinkedIn', queueResearch: true } })
      expect(api.postings[0].body.items).toHaveLength(5)
      expect(mock.submissions).toHaveLength(0) // collecting never applies
    } finally {
      await api.close()
    }
  })

  it('refuses a campaign with nothing to search for', async () => {
    await expect(
      collectPostings({
        adapter: linkedIn(`${mock.origin}/li`),
        page,
        campaign: { ...campaign, criteria: { ...campaign.criteria, keywords: [] } },
        postedWithinDays: 7,
        maxPages: 1,
        limit: 5,
        pauseSeconds: [0, 0],
      }),
    ).rejects.toThrow(/no keywords/)
  })
})

describe('apply (shortlist only) — LinkedIn', () => {
  const adapter = () => linkedIn(`${mock.origin}/li`)
  const list = () => shortlisted(`${mock.origin}/li`, (id) => `/jobs/view/${id}/`, ['1001', '1002', '1003', '1004'])

  it('dry run fills every step but sends nothing, and explains each skip', async () => {
    const store = new Store(join(dir, 'log.jsonl'))
    const { outcomes } = await applyToShortlist({ adapter: adapter(), config: config(), store, jobs: list(), submit: false, page })
    const r = byJob(outcomes)
    expect(r['1001'].status).toBe('DryRun')
    expect(r['1002'].status).toBe('NeedsManual')
    expect(r['1002'].detail).toContain('favourite programming joke')
    expect(r['1003']).toMatchObject({ status: 'Skipped', detail: 'Already applied' })
    expect(r['1004'].detail).toMatch(/not easy apply/i)
    expect(Object.keys(r)).not.toContain('1005') // not shortlisted, never touched
    expect(mock.submissions).toHaveLength(0)
  })

  it('submit sends exactly the saved answers, links the opportunity, and never applies twice', async () => {
    const api = await startFakeApi([campaign])
    try {
      const store = new Store(join(dir, 'log.jsonl'))
      const cfg = config({ url: api.url, key: 'opk_test' })
      const first = await applyToShortlist({ adapter: adapter(), config: cfg, store, jobs: list(), submit: true, page })
      expect(byJob(first.outcomes)['1001'].status).toBe('Applied')
      expect(mock.submissions).toEqual([
        {
          platform: 'LinkedIn',
          job: '1001',
          values: {
            email: 'me@example.com', // prefilled value left alone
            phone: '9999999999',
            city: 'Bengaluru, Karnataka, India', // picked from the typeahead, not just typed
            q1: '6',
            q2: 'Yes',
            spons: 'No',
            follow: false,
          },
        },
      ])
      const applied = api.reports.flatMap((r) => r.items).find((i) => i.externalJobId === '1001')
      expect(applied).toMatchObject({ status: 'Applied', opportunityId: 'opp-1001' })
      expect(store.all.every((r) => r.synced)).toBe(true)

      const second = await applyToShortlist({ adapter: adapter(), config: cfg, store, jobs: list(), submit: true, page })
      expect(second.outcomes).toHaveLength(0)
      expect(mock.submissions).toHaveLength(1)
    } finally {
      await api.close()
    }
  })

  it('stops the whole run when LinkedIn shows the login wall', async () => {
    const store = new Store(join(dir, 'log.jsonl'))
    const summary = await applyToShortlist({ adapter: linkedIn(`${mock.origin}/wall`), config: config(), store, jobs: list(), submit: true, page })
    expect(summary.outcomes).toHaveLength(0)
    expect(summary.stoppedBecause).toMatch(/not logged in to linkedin/i)
  })
})

describe('apply (shortlist only) — Naukri', () => {
  const adapter = () => naukri(`${mock.origin}/nk`)
  const list = () => shortlisted(`${mock.origin}/nk`, (id) => `/job/${id}`, ['2001', '2002', '2003', '2004', '2005'])

  it('dry run stops before Apply because Naukri submits on click', async () => {
    const store = new Store(join(dir, 'log.jsonl'))
    const r = byJob((await applyToShortlist({ adapter: adapter(), config: config(), store, jobs: list(), submit: false, page })).outcomes)
    expect(r['2001'].status).toBe('DryRun')
    expect(r['2003']).toMatchObject({ status: 'Skipped', detail: 'Already applied' })
    expect(r['2004'].detail).toMatch(/company website/)
    expect(mock.submissions).toHaveLength(0)
  })

  it('submit answers the recruiter chatbot from saved answers and flags what it cannot answer', async () => {
    const store = new Store(join(dir, 'log.jsonl'))
    const r = byJob((await applyToShortlist({ adapter: adapter(), config: config(), store, jobs: list(), submit: true, page })).outcomes)
    expect(r['2001'].status).toBe('Applied')
    expect(r['2002'].status).toBe('Applied')
    expect(r['2005'].status).toBe('NeedsManual')
    expect(r['2005'].detail).toContain('favourite project')
    expect(mock.submissions.find((s) => s.job === '2002')?.values).toEqual({ q0: 'Yes', q1: '30' })
    expect(mock.submissions.map((s) => s.job).sort()).toEqual(['2001', '2002'])
  })

  it('reads the Naukri description for research', async () => {
    const jobs = await collectPostings({ adapter: adapter(), page, campaign, postedWithinDays: 7, maxPages: 1, limit: 2, pauseSeconds: [0, 0] })
    expect(jobs).toHaveLength(2)
    expect(jobs[0].description).toMatch(/Minimum 4 years/)
  })
})
