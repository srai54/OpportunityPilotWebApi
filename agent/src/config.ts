import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Answers } from './answers.ts'
import { DEFAULT_SCHEDULE, type ScheduleConfig } from './schedule.ts'

export const AGENT_DIR = resolve(dirname(fileURLToPath(import.meta.url)), '..')
export const CONFIG_PATH = join(AGENT_DIR, 'config.json')
export const DATA_DIR = join(AGENT_DIR, '.data')

/** What one platform search uses. Built from a campaign's criteria; filtering and scoring happen in research. */
export interface SearchConfig {
  location: string
  remote: 'any' | 'remote' | 'hybrid' | 'onsite'
  postedWithinDays: number
}

export interface Config {
  api: { url: string; key: string }
  /** Must be true before --submit sends real applications. */
  iUnderstandAccountRisk: boolean
  limits: {
    maxApplicationsPerRun: number
    maxApplicationsPerDay: number
    /** Pause between applications, in seconds [min, max], so the platform is not hammered. */
    pauseSeconds: [number, number]
    maxPages: number
    /** Postings `collect` opens and sends per run. */
    maxPostingsPerCollect: number
    /** Leads `circle` maps per run. */
    maxCircleLeadsPerRun: number
    /** Result pages read per LinkedIn people search (1–3). */
    circlePages: number
    /** LinkedIn notes/messages `outreach --submit` sends per day (counted in the local log). */
    maxLinkedInNotesPerDay: number
  }
  search: { postedWithinDays: number }
  /** acceptTermsCheckboxes: tick "I agree / I confirm" boxes on application forms. Off = such forms become "Needs you". */
  profile: { resumePath: string; followCompanies: boolean; acceptTermsCheckboxes: boolean }
  answers: Answers
  /** Used only by `agent cycle` and `agent watch`; empty targets = nothing is scheduled. */
  schedule: ScheduleConfig
}

export const EXAMPLE_CONFIG: Config = {
  api: { url: 'https://opportunitypilotwebapi.onrender.com', key: '' },
  iUnderstandAccountRisk: false,
  limits: {
    maxApplicationsPerRun: 10, maxApplicationsPerDay: 25, pauseSeconds: [8, 20], maxPages: 2, maxPostingsPerCollect: 30,
    maxCircleLeadsPerRun: 5, circlePages: 1, maxLinkedInNotesPerDay: 10,
  },
  search: { postedWithinDays: 7 },
  profile: { resumePath: '', followCompanies: false, acceptTermsCheckboxes: false },
  schedule: DEFAULT_SCHEDULE,
  answers: {
    fields: [
      { match: ['notice period'], value: '30' },
      { match: ['current ctc', 'current salary', 'current compensation', 'current annual'], value: '1200000' },
      { match: ['expected ctc', 'expected salary', 'expected compensation', 'desired salary'], value: '1800000' },
      { match: ['mobile', 'phone'], value: '9999999999' },
      { match: ['current location', 'city', 'where are you located'], value: 'Bengaluru' },
      { match: ['sponsorship', 'visa'], value: 'No' },
      { match: ['authorized to work', 'authorised to work', 'legally', 'work permit'], value: 'Yes' },
      { match: ['relocate', 'relocation'], value: 'Yes' },
      { match: ['immediate joiner', 'join immediately'], value: 'No' },
      { match: ['hybrid', 'from office', 'onsite', 'on-site'], value: 'Yes' },
      { match: ['gender', 'race', 'ethnicity', 'veteran', 'disability'], value: 'Prefer not to say' },
      { match: ['linkedin profile', 'linkedin url'], value: 'https://www.linkedin.com/in/your-handle' },
    ],
    skills: { 'c#': 5, '.net core': 5, '.net': 5, 'asp.net': 5, react: 3, typescript: 3, sql: 5, azure: 2 },
    // Nothing is assumed: questions your rules don't cover become "Needs you". Opt in to defaults here if you want them.
    defaults: { yearsOfExperience: null, yesNo: '' },
  },
}

export function writeExampleConfig(): boolean {
  if (existsSync(CONFIG_PATH)) return false
  writeFileSync(CONFIG_PATH, JSON.stringify(EXAMPLE_CONFIG, null, 2) + '\n')
  return true
}

/** Reads config.json and reports every problem at once, in words a non-developer can act on. */
export function loadConfig(path = CONFIG_PATH): Config {
  if (!existsSync(path)) throw new Error(`No config found at ${path}. Run: npm run agent -- init`)
  let raw: Partial<Config>
  try {
    raw = JSON.parse(readFileSync(path, 'utf8'))
  } catch (e) {
    throw new Error(`config.json is not valid JSON: ${(e as Error).message}`)
  }
  const c = { ...EXAMPLE_CONFIG, ...raw } as Config
  c.limits = { ...EXAMPLE_CONFIG.limits, ...raw.limits }
  c.search = { ...EXAMPLE_CONFIG.search, ...raw.search }
  c.profile = { ...EXAMPLE_CONFIG.profile, ...raw.profile }
  c.api = { ...EXAMPLE_CONFIG.api, ...raw.api }
  c.schedule = { ...DEFAULT_SCHEDULE, ...raw.schedule }
  c.answers = {
    fields: raw.answers?.fields ?? [],
    skills: raw.answers?.skills ?? {},
    defaults: { ...EXAMPLE_CONFIG.answers.defaults, ...raw.answers?.defaults },
  }

  const problems: string[] = []
  if (!(c.limits.maxPostingsPerCollect > 0)) problems.push('limits.maxPostingsPerCollect must be above 0')
  if (!(c.limits.maxApplicationsPerRun > 0)) problems.push('limits.maxApplicationsPerRun must be above 0')
  if (!(c.limits.maxApplicationsPerDay > 0)) problems.push('limits.maxApplicationsPerDay must be above 0')
  if (!Array.isArray(c.limits.pauseSeconds) || c.limits.pauseSeconds.length !== 2) problems.push('limits.pauseSeconds must be [min, max]')
  if (!(c.limits.maxCircleLeadsPerRun > 0)) problems.push('limits.maxCircleLeadsPerRun must be above 0')
  if (!(c.limits.circlePages >= 1 && c.limits.circlePages <= 3)) problems.push('limits.circlePages must be 1, 2 or 3')
  if (!(c.limits.maxLinkedInNotesPerDay > 0 && c.limits.maxLinkedInNotesPerDay <= 25)) problems.push('limits.maxLinkedInNotesPerDay must be between 1 and 25')
  if (!Array.isArray(c.answers.fields) || c.answers.fields.some((r) => !Array.isArray(r.match) || typeof r.value !== 'string'))
    problems.push('answers.fields must be a list of { "match": ["..."], "value": "..." }')
  if (c.profile.resumePath && !existsSync(c.profile.resumePath)) problems.push(`profile.resumePath does not exist: ${c.profile.resumePath}`)
  // An empty schedule is fine (nothing scheduled); a half-filled one is reported when it is used, by `cycle` and `watch`.
  if (problems.length) throw new Error(`config.json needs fixing:\n  - ${problems.join('\n  - ')}`)
  return c
}
