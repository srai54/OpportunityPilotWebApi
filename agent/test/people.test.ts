import { chromium, type Browser, type Page } from 'playwright'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { newPreparedPage } from '../src/browser.ts'
import {
  buildCircle, circleQueries, classifyRelation, companyCore, decideAction, mentionsCompany, normalizeProfileUrl, noteFits, parseCard,
  parseDegree, peopleSearchUrl, readPeopleCards, titleKeywords, type RawCard,
} from '../src/platforms/linkedin-people.ts'

describe('profile links', () => {
  it('keeps only LinkedIn member links, in the one form the API stores', () => {
    expect(normalizeProfileUrl('https://www.linkedin.com/in/asha-rao?miniProfileUrn=urn%3Ali&trk=x#a')).toBe('https://www.linkedin.com/in/asha-rao')
    expect(normalizeProfileUrl('http://in.linkedin.com/in/asha-rao/details/experience/')).toBe('https://www.linkedin.com/in/asha-rao')
    expect(normalizeProfileUrl('/in/ACoAABcdEf123/')).toBe('https://www.linkedin.com/in/ACoAABcdEf123') // relative, case kept
    expect(normalizeProfileUrl('https://www.linkedin.com/in/jos%C3%A9-garc%C3%ADa')).toBe('https://www.linkedin.com/in/jos%C3%A9-garc%C3%ADa')
    for (const bad of [null, '', 'https://www.linkedin.com/company/northwind/', 'https://www.linkedin.com/search/results/people/?keywords=x',
      'https://evil.example/in/asha', 'https://linkedin.com.evil.example/in/asha', 'https://www.linkedin.com/in/', 'javascript:alert(1)',
      'https://www.linkedin.com/in/asha%3Cscript%3E', 'https://www.linkedin.com/sales/in/asha'])
      expect(normalizeProfileUrl(bad)).toBeNull()
  })
})

describe('degree, company and title', () => {
  it('reads the connection degree from a badge or a line', () => {
    expect(parseDegree('• 2nd')).toBe(2)
    expect(parseDegree('1st degree connection')).toBe(1)
    expect(parseDegree('• 3rd+')).toBe(3)
    expect(parseDegree('21st Century Fox')).toBeNull()
    expect(parseDegree('')).toBeNull()
  })

  it('places a person at the company from the headline or the current position only', () => {
    expect(companyCore('Northwind Technologies Pvt. Ltd.')).toBe('northwind')
    expect(mentionsCompany('Senior Recruiter at Northwind Technologies', 'Northwind Technologies Pvt Ltd')).toBe(true)
    expect(mentionsCompany('Engineering Manager | NORTHWIND', 'Northwind Integrations')).toBe(true)
    expect(mentionsCompany('Recruiter at Contoso', 'Northwind')).toBe(false)
    expect(mentionsCompany('Recruiter at Northwinds Bank', 'Northwind')).toBe(false)
    expect(mentionsCompany('Works at Acme', 'Acme')).toBe(true)
  })

  it('takes the words that say what the job is, not its seniority', () => {
    expect(titleKeywords('Senior .NET Engineer (Remote)')).toEqual(['.net', 'engineer'])
    expect(titleKeywords('Sr. Data Scientist - II, Payments', 4)).toEqual(['data', 'scientist', 'payments'])
    expect(titleKeywords('C# Developer')).toEqual(['c#', 'developer'])
  })

  it('builds the three searches and their LinkedIn URLs', () => {
    const [recruiters, managers, connections] = circleQueries('Northwind', 'Senior .NET Engineer')
    expect(recruiters.keywords).toBe('"Northwind" (recruiter OR "talent acquisition")')
    expect(managers.keywords).toBe('"Northwind" ("hiring manager" OR "engineering manager" OR "head of") .net engineer')
    expect(connections).toEqual({ kind: 'connections', keywords: '"Northwind"', firstDegreeOnly: true })
    const url = new URL(peopleSearchUrl('https://www.linkedin.com', connections, 1))
    expect(url.pathname).toBe('/search/results/people/')
    expect(url.searchParams.get('network')).toBe('["F"]')
    expect(url.searchParams.get('page')).toBe('2')
    expect(new URL(peopleSearchUrl('https://www.linkedin.com', recruiters, 0)).searchParams.get('network')).toBeNull()
  })
})

describe('classification', () => {
  const words = ['.net', 'engineer']
  const c = (context: string, degree: 1 | 2 | 3 | null = 2) => classifyRelation({ context, degree }, words)
  it('recognises recruiters, hiring managers, the team and connections', () => {
    expect(c('Senior Technical Recruiter at Northwind')).toBe('Recruiter')
    expect(c('Talent Acquisition Partner | Northwind')).toBe('Recruiter')
    expect(c('Engineering Manager, .NET Platform')).toBe('HiringManager')
    expect(c('Hiring Manager at Northwind')).toBe('HiringManager')
    expect(c('Head of Engineering at Northwind')).toBe('HiringManager')
    expect(c('Software Engineer at Northwind')).toBe('Team')
    expect(c('Finance Analyst at Northwind', 1)).toBe('Connection')
    expect(c('Marketing Manager at Northwind')).toBe('Other') // a manager, but not of this kind of work
    expect(c('Finance Analyst at Northwind', 2)).toBe('Other')
  })
})

describe('result cards', () => {
  const raw = (over: Partial<RawCard>): RawCard => ({ href: null, name: '', headline: '', summary: '', badge: '', lines: [], ...over })

  it('parses a card in the older layout from its classes', () => {
    expect(parseCard(raw({
      href: 'https://www.linkedin.com/in/asha-rao?miniProfileUrn=x', name: 'Asha Rao', badge: '• 2nd',
      headline: 'Senior Technical Recruiter at Northwind', summary: 'Current: Recruiter at Northwind',
    }))).toEqual({
      name: 'Asha Rao', title: 'Senior Technical Recruiter at Northwind', profileUrl: 'https://www.linkedin.com/in/asha-rao', degree: 2,
      context: 'Senior Technical Recruiter at Northwind Current: Recruiter at Northwind',
    })
  })

  it('falls back to the visible lines when the classes changed', () => {
    const p = parseCard(raw({
      href: '/in/ravi-menon/', name: '',
      lines: ['Ravi Menon', '• 3rd+', 'View Ravi Menon’s profile', 'Engineering Manager, .NET Platform', 'Pune', 'Current: Engineering Manager at Northwind', 'Connect'],
    }))
    expect(p).toMatchObject({ name: 'Ravi Menon', title: 'Engineering Manager, .NET Platform', degree: 3, profileUrl: 'https://www.linkedin.com/in/ravi-menon' })
    expect(p?.context).toContain('Current: Engineering Manager at Northwind')
  })

  it('drops cards with no profile link or a hidden member, and ignores a past-only position', () => {
    expect(parseCard(raw({ href: null, name: 'LinkedIn Member' }))).toBeNull()
    expect(parseCard(raw({ href: 'https://www.linkedin.com/in/x', name: 'LinkedIn Member' }))).toBeNull()
    expect(parseCard(raw({ href: 'https://www.linkedin.com/in/y', name: 'Old Colleague', headline: 'Consultant', summary: 'Past: Developer at Northwind' }))?.context).toBe('Consultant')
  })

  it('builds the circle: at the company only, one row per person, best relation first, at most 50', () => {
    const found = [
      { name: 'Asha Rao', title: 'Recruiter at Northwind', profileUrl: 'https://www.linkedin.com/in/asha', degree: null, context: 'Recruiter at Northwind' },
      { name: 'Asha Rao', title: 'Recruiter at Northwind', profileUrl: 'https://www.linkedin.com/in/ASHA', degree: 2 as const, context: 'Recruiter at Northwind' },
      { name: 'Kim Lee', title: 'Recruiter at Contoso', profileUrl: 'https://www.linkedin.com/in/kim', degree: 2 as const, context: 'Recruiter at Contoso' },
      { name: 'Meera Iyer', title: 'Software Engineer at Northwind', profileUrl: 'https://www.linkedin.com/in/meera', degree: 1 as const, context: 'Software Engineer at Northwind' },
      { name: 'Ravi Menon', title: 'Engineering Manager', profileUrl: 'https://www.linkedin.com/in/ravi', degree: 3 as const, context: 'Engineering Manager Current: Engineering Manager at Northwind' },
    ]
    const circle = buildCircle(found, 'Northwind', 'Senior .NET Engineer')
    expect(circle.map((p) => [p.name, p.relation, p.degree])).toEqual([
      ['Asha Rao', 'Recruiter', 2], ['Ravi Menon', 'HiringManager', 3], ['Meera Iyer', 'Team', 1],
    ])
    const many = Array.from({ length: 70 }, (_, i) => ({ ...found[0], profileUrl: `https://www.linkedin.com/in/p${i}` }))
    expect(buildCircle(many, 'Northwind', 'Engineer')).toHaveLength(50)
  })
})

describe('what to do on a profile', () => {
  const note = 'Hi Asha, I saw Northwind is hiring. I would like to connect. Ravi'
  it('messages a 1st-degree connection and connects with a note otherwise', () => {
    expect(decideAction({ degree: 1, connect: null, pending: false, canMessage: true }, 'x'.repeat(900))).toEqual({ kind: 'message' })
    expect(decideAction({ degree: 2, connect: 'button', pending: false, canMessage: false }, note)).toEqual({ kind: 'connect' })
    expect(decideAction({ degree: 3, connect: 'more', pending: false, canMessage: true }, note)).toEqual({ kind: 'connect' })
  })
  it('refuses a note longer than 300 characters, a pending invitation, or a profile with no way to connect', () => {
    expect(noteFits('x'.repeat(300))).toBe(true)
    expect(noteFits('x'.repeat(301))).toBe(false)
    expect(noteFits('')).toBe(false)
    expect(decideAction({ degree: 2, connect: 'button', pending: false, canMessage: false }, 'x'.repeat(301))).toMatchObject({ kind: 'refuse', reason: expect.stringMatching(/301 characters.*300/) })
    expect(decideAction({ degree: 2, connect: null, pending: true, canMessage: false }, note)).toMatchObject({ kind: 'refuse', reason: expect.stringMatching(/pending/) })
    expect(decideAction({ degree: 3, connect: null, pending: false, canMessage: false }, note)).toMatchObject({ kind: 'refuse', reason: expect.stringMatching(/no Connect/) })
    expect(decideAction({ degree: 1, connect: null, pending: false, canMessage: false }, note)).toMatchObject({ kind: 'refuse' })
  })
})

// Saved HTML-ish fixtures of LinkedIn's people search, older and newer layouts; read with the same page function the agent uses.
const FIXTURE = `<!doctype html><html><body><nav>nav</nav><main><ul>
  <li class="reusable-search__result-container"><div class="entity-result">
    <span class="entity-result__title-text"><a class="app-aware-link" href="https://www.linkedin.com/in/asha-rao?miniProfileUrn=urn%3Ali%3Afs">
      <span dir="ltr"><span aria-hidden="true">Asha Rao</span><span class="visually-hidden" style="position:absolute;width:1px;height:1px;overflow:hidden">View Asha Rao’s profile</span></span></a>
      <span class="entity-result__badge"><span class="entity-result__badge-text"><span aria-hidden="true">• 2nd</span></span></span></span>
    <div class="entity-result__primary-subtitle">Senior Technical Recruiter at Northwind</div>
    <div class="entity-result__secondary-subtitle">Pune</div>
    <p class="entity-result__summary">Current: Technical Recruiter at Northwind</p></div></li>
  <li class="reusable-search__result-container"><div class="entity-result">
    <span class="entity-result__title-text"><span aria-hidden="true">LinkedIn Member</span></span>
    <div class="entity-result__primary-subtitle">Talent Acquisition at Northwind</div></div></li>
</ul>
<div data-view-name="search-entity-result-universal-template"><div class="a1">
  <a class="b2" href="https://www.linkedin.com/in/ravi-menon/"><span aria-hidden="true">Ravi Menon</span></a>
  <div class="c3">• 3rd+</div><div class="d4">Engineering Manager, .NET Platform</div><div class="e5">Pune</div>
  <div class="f6">Current: Engineering Manager at Northwind</div></div></div>
</main></body></html>`

describe('reading result cards from a page (fixture)', () => {
  let browser: Browser
  let page: Page
  beforeAll(async () => {
    browser = await chromium.launch()
    page = await newPreparedPage(browser)
  })
  afterAll(async () => {
    await browser?.close()
  })

  it('reads both layouts, skips the hidden member, and classifies the result', async () => {
    await page.setContent(FIXTURE)
    const cards = await readPeopleCards(page)
    expect(cards).toHaveLength(2)
    const people = cards.map((c) => parseCard(c)).filter((p) => p !== null)
    expect(people.map((p) => [p.name, p.degree])).toEqual([['Asha Rao', 2], ['Ravi Menon', 3]])
    expect(buildCircle(people, 'Northwind', 'Senior .NET Engineer').map((p) => [p.name, p.relation])).toEqual([
      ['Asha Rao', 'Recruiter'], ['Ravi Menon', 'HiringManager'],
    ])
  })
})
