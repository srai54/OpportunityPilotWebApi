import type { Page } from 'playwright'
import { sendCircle, type CircleRequest } from './api.ts'
import { captureDebug } from './browser.ts'
import { buildCircle, circleQueries, searchPeople, type FoundPerson } from './platforms/linkedin-people.ts'
import { between, log, sleep, StopRun } from './util.ts'

export interface CircleOptions {
  page: Page
  /** https://www.linkedin.com, or the mock site in tests. */
  base: string
  api: { url: string; key: string }
  requests: CircleRequest[]
  /** Leads mapped this run. */
  limit: number
  /** Result pages per search (1–3). */
  pages: number
  pauseSeconds: [number, number]
}

export interface CircleSummary {
  mapped: { opportunityId: string; organization: string; found: number }[]
  failed: { opportunityId: string; reason: string }[]
  stoppedBecause: string | null
}

/**
 * Maps the people around each requested lead: three LinkedIn people searches (recruiters, likely hiring managers, the person's own
 * 1st-degree connections at the company), a few result pages each, human-like pauses. Only the visible cards are read; nobody is
 * contacted. Stops the whole run at any checkpoint, CAPTCHA or logged-out page and leaves the remaining requests waiting.
 */
export async function mapCircles(opts: CircleOptions): Promise<CircleSummary> {
  const summary: CircleSummary = { mapped: [], failed: [], stoppedBecause: null }
  const [min, max] = opts.pauseSeconds
  const pause = () => (max > 0 ? sleep(between(min * 500, max * 500)) : Promise.resolve())
  try {
    for (const request of opts.requests.slice(0, opts.limit)) {
      if (!request.organization.trim()) continue
      const found: Omit<FoundPerson, 'relation'>[] = []
      try {
        for (const query of circleQueries(request.organization, request.title)) {
          for (let p = 0; p < Math.min(3, Math.max(1, opts.pages)); p++) {
            const people = await searchPeople(opts.page, opts.base, query, p)
            log(`${request.organization}: ${query.kind}, page ${p + 1}: ${people.length} card(s)`)
            found.push(...people)
            await pause()
            if (people.length < 10) break // a short page is the last one
          }
        }
      } catch (e) {
        if (e instanceof StopRun) throw e
        const evidence = await captureDebug(opts.page, `linkedin-circle-${request.opportunityId}`)
        summary.failed.push({ opportunityId: request.opportunityId, reason: `${(e as Error).message.split('\n')[0]} (evidence: ${evidence}.png)` })
        log(`Could not map ${request.organization}: ${(e as Error).message.split('\n')[0]}`)
        continue
      }
      const circle = buildCircle(found, request.organization, request.title)
      const sent = await sendCircle(opts.api, request.opportunityId, circle)
      if (!sent.ok) {
        summary.failed.push({ opportunityId: request.opportunityId, reason: sent.reason })
        log(`Could not send the circle for ${request.organization}: ${sent.reason}`)
        continue
      }
      summary.mapped.push({ opportunityId: request.opportunityId, organization: request.organization, found: circle.length })
      log(`${request.organization} — ${request.title}: ${circle.length} people (${sent.data.added} new) sent to the app.`)
    }
  } catch (e) {
    if (!(e instanceof StopRun)) throw e
    summary.stoppedBecause = e.message
    log(`Stopped: ${e.message}`)
  }
  return summary
}
