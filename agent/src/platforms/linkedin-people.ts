/**
 * LinkedIn people: reading search result cards for the JD circle, and the profile page actions (Connect → "Add a note", Message)
 * used to send approved notes. Everything here runs in the person's own logged-in browser. The parsing and decision functions are
 * pure so they can be tested on saved HTML-ish fixtures; the page functions only collect raw text and click.
 */
import type { Locator, Page } from 'playwright'
import type { CirclePerson, CircleRelation } from '../api.ts'
import { between, sleep, StopRun } from '../util.ts'

/** LinkedIn's limit for the note on a connection request. */
export const CONNECTION_NOTE_LIMIT = 300

// ---------------------------------------------------------------- pure: links, degree, company, title

/**
 * The one form the API stores: https://www.linkedin.com/in/{slug}. Any linkedin.com host, the query, fragment and anything after the
 * slug are dropped; the slug keeps its case (LinkedIn's ACoAA… member ids are case-sensitive). Null for anything else.
 */
export function normalizeProfileUrl(href: string | null | undefined, base = 'https://www.linkedin.com'): string | null {
  if (!href) return null
  let url: URL
  try {
    url = new URL(href.trim(), base)
  } catch {
    return null
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') return null
  if (url.username || url.password) return null
  const host = url.hostname.toLowerCase()
  const isLinkedIn = host === 'linkedin.com' || host.endsWith('.linkedin.com')
  if (!isLinkedIn && host !== new URL(base).hostname.toLowerCase()) return null
  if (url.port && isLinkedIn) return null
  const parts = url.pathname.split('/').filter(Boolean)
  // The real site has /in/ first; the test stand-in serves LinkedIn under a path prefix (/li/in/...).
  const at = isLinkedIn ? (parts[0]?.toLowerCase() === 'in' ? 0 : -1) : parts.findIndex((p) => p.toLowerCase() === 'in')
  if (at < 0 || !parts[at + 1]) return null
  const slug = parts[at + 1]
  let decoded: string
  try {
    decoded = decodeURIComponent(slug)
  } catch {
    return null
  }
  if (decoded.length > 200 || !/^[\p{L}\p{N}._-]+$/u.test(decoded)) return null
  return `https://www.linkedin.com/in/${slug}`
}

/** The profile page for a stored link on the site the agent drives (the real site, or the mock in tests). */
export function profilePageUrl(profileUrl: string, base: string): string {
  const slug = profileUrl.replace(/^https:\/\/www\.linkedin\.com\/in\//, '')
  return `${base.replace(/\/$/, '')}/in/${slug}/`
}

/** "• 2nd", "2nd degree connection", "1st" → 2 / 1; "3rd+" → 3; anything else → null. */
export function parseDegree(text: string | null | undefined): 1 | 2 | 3 | null {
  const m = /\b(1st|2nd|3rd)\+?(?![a-z])/i.exec(text ?? '')
  if (!m) return null
  return ({ '1st': 1, '2nd': 2, '3rd': 3 } as const)[m[1].toLowerCase() as '1st' | '2nd' | '3rd']
}

const LEGAL = /\b(inc|incorporated|ltd|limited|llc|llp|plc|gmbh|ag|sa|bv|pvt|private|corp|corporation|co|company|pte|technologies|technology|solutions|services|group|global|india)\b/g

/** The company name reduced to what people write in a headline: lower case, no punctuation or legal suffix. */
export function companyCore(company: string): string {
  return company.toLowerCase().replace(/[^\p{L}\p{N}&+ ]+/gu, ' ').replace(LEGAL, ' ').replace(/\s+/g, ' ').trim()
}

/** Whether a headline / "Current:" line places the person at the company now. */
export function mentionsCompany(text: string, company: string): boolean {
  const core = companyCore(company)
  if (!core) return false
  const hay = ` ${text.toLowerCase().replace(/[^\p{L}\p{N}&+ ]+/gu, ' ').replace(/\s+/g, ' ')} `
  if (hay.includes(` ${core} `)) return true
  const first = core.split(' ')[0]
  return first.length >= 4 && hay.includes(` ${first} `)
}

const TITLE_NOISE = new Set([
  'senior', 'sr', 'junior', 'jr', 'principal', 'staff', 'associate', 'lead', 'i', 'ii', 'iii', 'iv', 'the', 'a', 'an', 'of', 'and', 'for', 'to',
  'in', 'with', 'at', 'remote', 'hybrid', 'onsite', 'on-site', 'full', 'time', 'full-time', 'part-time', 'contract', 'intern', 'internship',
  'm/f/d', 'f/m/d', 'w/m/d', 'mid', 'level', 'experienced', 'immediate', 'joiner', 'urgent', '-', '&', '|', '/',
])

/** The words of a job title that say what the work is: "Senior .NET Engineer (Remote)" → [".net", "engineer"]. */
export function titleKeywords(title: string, max = 2): string[] {
  const words = title
    .toLowerCase()
    .replace(/\(.*?\)/g, ' ')
    .split(/[\s,/|()]+/)
    .map((w) => w.replace(/^[^\p{L}\p{N}.#+]+|[^\p{L}\p{N}#+]+$/gu, ''))
    .filter((w) => w.length > 1 && !TITLE_NOISE.has(w) && !/^\d+$/.test(w))
  return [...new Set(words)].slice(0, max)
}

export interface CircleQuery {
  /** Why this search runs; the classifier uses it only as a fallback. */
  kind: 'recruiters' | 'managers' | 'connections'
  keywords: string
  /** Only the person's own 1st-degree connections (LinkedIn's network=["F"] filter). */
  firstDegreeOnly: boolean
}

/** The three searches for one lead: recruiters, the likely hiring manager, and the person's own connections there. */
export function circleQueries(company: string, title: string): CircleQuery[] {
  const quoted = `"${company.replace(/"/g, '').trim()}"`
  const words = titleKeywords(title).join(' ')
  return [
    { kind: 'recruiters', keywords: `${quoted} (recruiter OR "talent acquisition")`, firstDegreeOnly: false },
    { kind: 'managers', keywords: `${quoted} ("hiring manager" OR "engineering manager" OR "head of")${words ? ` ${words}` : ''}`, firstDegreeOnly: false },
    { kind: 'connections', keywords: quoted, firstDegreeOnly: true },
  ]
}

export function peopleSearchUrl(base: string, query: CircleQuery, pageIndex: number): string {
  const q = new URLSearchParams({ keywords: query.keywords, origin: 'GLOBAL_SEARCH_HEADER' })
  if (query.firstDegreeOnly) q.set('network', '["F"]')
  if (pageIndex > 0) q.set('page', String(pageIndex + 1))
  return `${base.replace(/\/$/, '')}/search/results/people/?${q}`
}

// ---------------------------------------------------------------- pure: cards and classification

/** What the page function reads from one result card, untouched. */
export interface RawCard {
  href: string | null
  /** The name as shown (aria-hidden span of the title link), when found. */
  name: string
  /** The headline element's text, when found by class. */
  headline: string
  /** The "Current: … / Past: …" snippet, when shown. */
  summary: string
  /** The degree badge ("• 2nd"), when found by class. */
  badge: string
  /** The whole card's visible text, one entry per line; the fallback when classes changed. */
  lines: string[]
}

export interface FoundPerson extends CirclePerson {
  /** Headline + current-position text, used to check the person is at the company. */
  context: string
}

const BUTTON_WORDS = /^(connect|message|follow|following|pending|view profile|send inmail|save in sales navigator)$/i
const DEGREE_LINE = /^(•\s*)?(1st|2nd|3rd\+?)(\s*degree connection)?$|degree connection$/i

const clean = (s: string) => s.replace(/\s+/g, ' ').trim()

/** Turns a raw card into a person, or null when it is not a usable member (no profile link, "LinkedIn Member"). */
export function parseCard(raw: RawCard, base = 'https://www.linkedin.com'): Omit<FoundPerson, 'relation'> | null {
  const profileUrl = normalizeProfileUrl(raw.href, base)
  if (!profileUrl) return null
  const lines = raw.lines.map(clean).filter(Boolean)
  let name = clean(raw.name)
    .replace(/^view\s+/i, '')
    .replace(/[’']s\s+profile$/i, '')
    .replace(/\s*•?\s*(1st|2nd|3rd\+?)\s*(degree connection)?$/i, '')
    .trim()
  if (!name) name = (lines[0] ?? '').replace(/\s*•?\s*(1st|2nd|3rd\+?)$/i, '').trim()
  if (!name || /^linkedin member$/i.test(name)) return null

  const isNoise = (l: string) =>
    l === name || DEGREE_LINE.test(l) || BUTTON_WORDS.test(l) || /^view .*profile$/i.test(l) || /^status is /i.test(l) ||
    /mutual connection|followers?$/i.test(l) || /^(current|past|summary|skills):/i.test(l)
  const headline = clean(raw.headline) || lines.find((l) => !isNoise(l) && !l.startsWith(name)) || ''
  const summary = clean(raw.summary) || lines.find((l) => /^(current|past):/i.test(l)) || ''
  const degree = parseDegree(raw.badge) ?? parseDegree(lines.filter((l) => DEGREE_LINE.test(l) || /•/.test(l)).join(' '))
  const current = /^past:/i.test(summary) ? '' : summary
  return { name: name.slice(0, 200), title: headline ? headline.slice(0, 300) : null, profileUrl, degree, context: `${headline} ${current}`.trim() }
}

const RECRUITER = /\b(recruit\w*|talent acquisition|talent partner|talent sourc\w*|sourcer|technical sourcing|hr business partner|human resources|people partner|staffing|headhunt\w*|ta (lead|partner|specialist|manager))\b/i
const HIRING_MANAGER = /\b(hiring manager)\b/i
const MANAGER = /\b(manager|head of|director|vp|vice president|cto|chief technology officer|founder|co-?founder)\b/i
const TECH_LEADER = /\b(engineering|technology|software|development|platform|cto|founder|co-?founder)\b/i

/**
 * How the person relates to the job, from their headline: recruiters first, then a likely hiring manager (a manager in engineering or
 * in the job's own field), then the team (shares the job's key words), then the person's own 1st-degree connections, else Other.
 */
export function classifyRelation(person: { context: string; degree: 1 | 2 | 3 | null }, titleWords: string[]): CircleRelation {
  const text = person.context.toLowerCase()
  if (RECRUITER.test(text) && !HIRING_MANAGER.test(text)) return 'Recruiter'
  const overlap = titleWords.some((w) => w.length > 1 && text.includes(w.toLowerCase()))
  if (HIRING_MANAGER.test(text) || (MANAGER.test(text) && (overlap || TECH_LEADER.test(text)))) return 'HiringManager'
  if (overlap) return 'Team'
  if (person.degree === 1) return 'Connection'
  return 'Other'
}

const RANK: Record<CircleRelation, number> = { Recruiter: 0, HiringManager: 1, Team: 2, Connection: 3, Other: 4 }

/**
 * Keeps only people placed at the company now, classifies them, merges the same profile found by several searches (the more specific
 * relation and the known degree win) and returns at most `max`, recruiters and hiring managers first.
 */
export function buildCircle(found: Omit<FoundPerson, 'relation'>[], company: string, title: string, max = 50): CirclePerson[] {
  const words = titleKeywords(title, 4)
  const byUrl = new Map<string, CirclePerson>()
  for (const f of found) {
    if (!mentionsCompany(f.context, company)) continue
    const person: CirclePerson = { name: f.name, title: f.title, profileUrl: f.profileUrl, degree: f.degree, relation: classifyRelation(f, words) }
    const key = f.profileUrl.toLowerCase()
    const seen = byUrl.get(key)
    if (!seen) byUrl.set(key, person)
    else
      byUrl.set(key, {
        ...seen,
        relation: RANK[person.relation] < RANK[seen.relation] ? person.relation : seen.relation,
        degree: seen.degree ?? person.degree,
        title: seen.title ?? person.title,
      })
  }
  return [...byUrl.values()]
    .sort((a, b) => RANK[a.relation] - RANK[b.relation] || (a.degree ?? 9) - (b.degree ?? 9) || a.name.localeCompare(b.name))
    .slice(0, max)
}

// ---------------------------------------------------------------- pure: what to do on a profile

export interface ProfileState {
  degree: 1 | 2 | 3 | null
  /** Where the Connect action is: a button on the card, inside the "More" menu, or nowhere. */
  connect: 'button' | 'more' | null
  /** An invitation is already pending. */
  pending: boolean
  canMessage: boolean
}

export type ProfileAction = { kind: 'message' } | { kind: 'connect' } | { kind: 'refuse'; reason: string }

/** Already connected → a message; otherwise a connection request with the note (only if it fits LinkedIn's 300 characters). */
export function decideAction(state: ProfileState, body: string): ProfileAction {
  if (state.degree === 1) return state.canMessage ? { kind: 'message' } : { kind: 'refuse', reason: 'Connected, but the profile shows no Message button' }
  if (state.pending) return { kind: 'refuse', reason: 'An invitation to this person is already pending' }
  if (!state.connect) return { kind: 'refuse', reason: 'The profile offers no Connect button (it may allow Follow only)' }
  if (!noteFits(body))
    return { kind: 'refuse', reason: `Not connected yet and the note is ${body.length} characters; a connection note allows ${CONNECTION_NOTE_LIMIT}. Shorten it in the app` }
  return { kind: 'connect' }
}

export const noteFits = (body: string) => body.length > 0 && body.length <= CONNECTION_NOTE_LIMIT

// ---------------------------------------------------------------- page: checks

/** Ends the run on anything only the person can resolve: a login wall, a checkpoint, a CAPTCHA, a search limit. */
export async function assertPeopleUsable(page: Page) {
  const url = page.url()
  if (/\/(checkpoint|challenge)\//.test(url)) throw new StopRun('LinkedIn is asking for a security check. Complete it in the browser, then run again.')
  if (/\/(login|authwall|uas\/login|signup)/.test(url)) throw new StopRun('Not logged in to LinkedIn. Run: npm run agent -- login linkedin')
  const captcha = page.locator('iframe[src*="captcha" i], #captcha-internal, [id*="captcha" i]').first()
  if (await captcha.isVisible().catch(() => false)) throw new StopRun('LinkedIn is showing a CAPTCHA. Complete it in the browser, then run again.')
  if (await page.getByText(/quick security check|verify (that )?you('re| are) (a )?human|unusual activity/i).first().isVisible().catch(() => false))
    throw new StopRun('LinkedIn is asking for a security check. Complete it in the browser, then run again.')
  if (await page.getByText(/reached the (monthly|commercial) (search )?(use )?limit|commercial use limit/i).first().isVisible().catch(() => false))
    throw new StopRun('LinkedIn says the search limit is reached. Try again later.')
}

// ---------------------------------------------------------------- page: people search

/** Reads the visible result cards of a people search page. Only text and the profile link; nothing is clicked. */
export async function readPeopleCards(page: Page): Promise<RawCard[]> {
  const containers = page.locator(
    [
      '[data-view-name="search-entity-result-universal-template"]',
      'li.reusable-search__result-container',
      'div.entity-result',
      '[data-chameleon-result-urn]',
      'main [role="list"] > [role="listitem"]',
      'main ul > li',
    ].join(', '),
  )
  try {
    await containers.first().waitFor({ timeout: 12_000 })
  } catch {
    return []
  }
  for (let i = 0; i < 4; i++) {
    await page.mouse.wheel(0, 900).catch(() => {})
    await sleep(between(250, 500))
  }
  const cards = await containers.evaluateAll((els) => {
    // Keep the outermost card only: newer layouts nest an entity-result inside a list item.
    const set = new Set(els)
    const outer = els.filter((el) => {
      for (let p = el.parentElement; p; p = p.parentElement) if (set.has(p)) return false
      return true
    })
    const txt = (el: Element | null) => ((el as HTMLElement | null)?.innerText ?? el?.textContent ?? '').replace(/\s+/g, ' ').trim()
    return outer
      .map((card) => {
        const link = card.querySelector('a[href*="/in/"]') as HTMLAnchorElement | null
        const named = link?.querySelector('span[aria-hidden="true"]') ?? card.querySelector('.entity-result__title-text span[aria-hidden="true"]')
        return {
          href: link?.getAttribute('href') ?? null,
          name: txt(named) || txt(link).split(' View ')[0] || '',
          headline: txt(card.querySelector('.entity-result__primary-subtitle, [class*="primary-subtitle"]')),
          summary: txt(card.querySelector('.entity-result__summary, p[class*="summary"]')),
          badge: txt(card.querySelector('.entity-result__badge-text, .entity-result__badge, [class*="badge-text"]')),
          lines: ((card as HTMLElement).innerText ?? card.textContent ?? '').split('\n').map((l) => l.trim()).filter(Boolean).slice(0, 20),
        }
      })
      .filter((c) => c.href)
  })
  return cards
}

/** One people search page, read politely. The caller decides how many pages and pauses between searches. */
export async function searchPeople(page: Page, base: string, query: CircleQuery, pageIndex: number): Promise<Omit<FoundPerson, 'relation'>[]> {
  await page.goto(peopleSearchUrl(base, query, pageIndex), { waitUntil: 'domcontentloaded' })
  await assertPeopleUsable(page)
  if (await page.getByText(/no results found/i).first().isVisible().catch(() => false)) return []
  const raw = await readPeopleCards(page)
  await assertPeopleUsable(page)
  return raw.map((r) => parseCard(r, base)).filter((p): p is Omit<FoundPerson, 'relation'> => p !== null)
}

// ---------------------------------------------------------------- page: profile actions

async function visible(locator: Locator): Promise<boolean> {
  return locator.first().isVisible().catch(() => false)
}

const topCard = (page: Page) => page.locator('main section.pv-top-card, main .pv-top-card, main section[data-member-id], main section').first()

/** Opens a profile and reads which actions it offers. Read-only. */
export async function readProfile(page: Page, url: string): Promise<ProfileState> {
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await assertPeopleUsable(page)
  await page.locator('main h1').first().waitFor({ timeout: 12_000 }).catch(() => {})
  await assertPeopleUsable(page)
  const card = topCard(page)
  const degreeText = await card.locator('.dist-value, [class*="distance-badge"], .pv-top-card__distance-badge').first().innerText({ timeout: 2000 }).catch(() => '')
  const degree = parseDegree(degreeText) ?? parseDegree(((await card.innerText({ timeout: 2000 }).catch(() => '')) || '').split('\n').slice(0, 6).join(' '))
  const pending = await visible(card.locator('button[aria-label*="Pending" i], button:has-text("Pending")'))
  const connectButton = await visible(connectOnCard(page))
  const canMessage = await visible(messageButton(page))
  let connect: ProfileState['connect'] = connectButton ? 'button' : null
  if (!connect && !pending && degree !== 1) {
    const more = card.locator('button[aria-label="More actions"], button[aria-label*="More actions" i]').first()
    if (await visible(more)) {
      await more.click().catch(() => {})
      await sleep(between(400, 800))
      if (await visible(connectInMore(page))) connect = 'more'
      await page.keyboard.press('Escape').catch(() => {})
    }
  }
  return { degree, connect, pending, canMessage }
}

const connectOnCard = (page: Page) =>
  topCard(page).locator('button[aria-label^="Invite" i][aria-label*="connect" i], button:has-text("Connect")').first()
const connectInMore = (page: Page) =>
  page.locator('[role="menu"] [aria-label^="Invite" i][aria-label*="connect" i], .artdeco-dropdown__content [aria-label^="Invite" i], [role="menuitem"]:has-text("Connect"), .artdeco-dropdown__content [role="button"]:has-text("Connect")').first()
const messageButton = (page: Page) =>
  topCard(page).locator('button[aria-label^="Message" i], a[href*="/messaging/compose"], button:has-text("Message")').first()

export interface SendResult {
  /** True once the final Send was clicked (submit mode only). */
  clicked: boolean
  /** LinkedIn showed that it went out. */
  confirmed: boolean
  detail: string
}

/**
 * Connect → "Add a note" → types the exact note. Dry run (`send` false): closes the dialog without sending. Throws a plain Error for an
 * unexpected page (nothing was sent), StopRun for a security check.
 */
export async function connectWithNote(page: Page, state: ProfileState, note: string, send: boolean): Promise<SendResult> {
  if (!noteFits(note)) throw new Error(`The note is ${note.length} characters; LinkedIn allows ${CONNECTION_NOTE_LIMIT}`)
  if (state.connect === 'more') {
    await topCard(page).locator('button[aria-label*="More actions" i]').first().click()
    await sleep(between(300, 700))
    await connectInMore(page).click()
  } else await connectOnCard(page).click()
  await sleep(between(600, 1200))
  await assertPeopleUsable(page)

  const dialog = page.locator('[role="dialog"]').first()
  if (!(await dialog.isVisible().catch(() => false))) {
    // Some layouts send an invitation on the first click, without a note.
    if (await visible(topCard(page).locator('button:has-text("Pending"), button[aria-label*="Pending" i]')))
      return { clicked: true, confirmed: false, detail: 'LinkedIn sent the invitation on the first click, without the note' }
    throw new Error('Clicking Connect opened no dialog')
  }
  if (await visible(dialog.getByText(/how do you know|enter (their|his|her) email/i)))
    throw new Error('LinkedIn asks for the person\'s email address before connecting')
  const addNote = dialog.getByRole('button', { name: /add a note/i }).first()
  if (!(await visible(addNote))) throw new Error('The invitation dialog has no "Add a note" button')
  await addNote.click()
  await sleep(between(400, 900))
  if (await visible(page.getByText(/personali[sz]ed invitations|upgrade to premium to (add|send)|you('ve| have) used all/i)))
    throw new NoteQuotaError('LinkedIn allows no more personalised notes on this account right now (free-account limit)')
  const box = page.locator('[role="dialog"] textarea[name="message"], [role="dialog"] #custom-message, [role="dialog"] textarea').first()
  await box.waitFor({ state: 'visible', timeout: 8000 })
  await box.fill(note)
  await sleep(between(300, 700))
  if ((await box.inputValue()) !== note) throw new Error('The note box did not take the exact text')
  const sendButton = page.locator('[role="dialog"] button[aria-label="Send invitation"], [role="dialog"] button[aria-label="Send now"]')
    .or(page.locator('[role="dialog"]').getByRole('button', { name: /^send( invitation| now)?$/i })).first()
  if (!send) {
    if (!(await visible(sendButton))) throw new Error('The invitation dialog has no Send button')
    await closeDialog(page)
    return { clicked: false, confirmed: false, detail: 'Filled the connection note; did not send (dry run)' }
  }
  await sendButton.click()
  const sent = await Promise.race([
    page.getByText(/invitation sent|your invitation (to .+ )?was sent/i).first().waitFor({ timeout: 10_000 }).then(() => true),
    topCard(page).locator('button:has-text("Pending"), button[aria-label*="Pending" i]').first().waitFor({ timeout: 10_000 }).then(() => true),
  ]).catch(() => false)
  return { clicked: true, confirmed: sent, detail: sent ? 'Connection request with note sent' : 'Clicked Send but LinkedIn showed no confirmation' }
}

/** Message box for a 1st-degree connection: types the exact text; dry run clears it again and closes the box. */
export async function sendMessage(page: Page, text: string, send: boolean): Promise<SendResult> {
  await messageButton(page).click()
  await sleep(between(700, 1300))
  await assertPeopleUsable(page)
  const box = page.locator('.msg-form__contenteditable[contenteditable="true"], [role="textbox"][contenteditable="true"]').last()
  await box.waitFor({ state: 'visible', timeout: 10_000 })
  await box.click()
  await box.fill(text)
  await sleep(between(300, 700))
  const typed = (await box.innerText().catch(() => '')).replace(/\r/g, '').trim()
  if (typed !== text.replace(/\r/g, '').trim()) {
    await box.fill('').catch(() => {})
    throw new Error('The message box did not take the exact text')
  }
  const sendButton = page.locator('button.msg-form__send-button, form button[type="submit"]:has-text("Send")').last()
  if (!send) {
    if (!(await visible(sendButton))) throw new Error('The message box has no Send button')
    await box.fill('')
    await page.locator('button[aria-label^="Close your conversation" i], button[aria-label^="Close your draft" i]').last().click({ timeout: 3000 })
      .catch(() => page.keyboard.press('Escape'))
    await page.getByRole('button', { name: /^discard$/i }).first().click({ timeout: 2000 }).catch(() => {})
    return { clicked: false, confirmed: false, detail: 'Typed the message; did not send (dry run)' }
  }
  await sendButton.click()
  const sent = await page.locator('.msg-s-event-listitem__body, .msg-s-message-list__event').filter({ hasText: text.slice(0, 40) }).first()
    .waitFor({ timeout: 10_000 }).then(() => true).catch(() => false)
  return { clicked: true, confirmed: sent, detail: sent ? 'Message sent' : 'Clicked Send but the message did not appear in the conversation' }
}

async function closeDialog(page: Page) {
  await page.locator('[role="dialog"] button[aria-label="Dismiss"]').first().click({ timeout: 3000 }).catch(() => page.keyboard.press('Escape'))
  await sleep(300)
}

/** The account cannot add notes now; connection requests stop for this run, messages may continue. */
export class NoteQuotaError extends Error {}
