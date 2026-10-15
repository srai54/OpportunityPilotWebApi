import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { chromium, type Browser, type BrowserContext, type Page } from 'playwright'
import { DATA_DIR } from './config.ts'

/**
 * A dedicated browser profile on this machine. The user logs in once in a visible window; the
 * session lives in this folder. The agent never sees or stores the password.
 */
export async function openBrowser(opts: { headless?: boolean; profileDir?: string } = {}): Promise<{ context: BrowserContext; page: Page }> {
  const profileDir = opts.profileDir ?? join(DATA_DIR, 'browser-profile')
  mkdirSync(profileDir, { recursive: true })
  const context = await chromium.launchPersistentContext(profileDir, {
    // Microsoft Edge is on every Windows PC, so nothing else needs downloading there; OP_BROWSER=chromium switches back.
    channel: browserChannel(),
    headless: opts.headless ?? false,
    viewport: { width: 1280, height: 900 },
  })
  await prepareContext(context)
  const page = context.pages()[0] ?? (await context.newPage())
  page.setDefaultTimeout(15_000)
  return { context, page }
}

/**
 * The CLI runs through tsx, whose transform wraps named functions in a __name() helper. Functions passed
 * to page.evaluate run inside the page, where that helper does not exist, so define a no-op there.
 * (Given as a string so the transform cannot touch it.)
 */
/** "msedge" on Windows unless OP_BROWSER says otherwise; Playwright's own Chromium elsewhere. */
export function browserChannel(env: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): string | undefined {
  const chosen = env.OP_BROWSER?.trim().toLowerCase()
  if (chosen === 'chromium') return undefined
  if (chosen) return chosen
  return platform === 'win32' ? 'msedge' : undefined
}

export async function prepareContext(context: BrowserContext) {
  await context.addInitScript('globalThis.__name = globalThis.__name || ((fn) => fn)')
}

/** A fresh, prepared context and page — used by tests so they run exactly what the CLI runs. */
export async function newPreparedPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext()
  await prepareContext(context)
  return context.newPage()
}

/** Saves a screenshot and the page HTML so a selector that stopped matching can be fixed from the evidence. */
export async function captureDebug(page: Page, name: string): Promise<string> {
  const dir = join(DATA_DIR, 'debug')
  mkdirSync(dir, { recursive: true })
  const base = join(dir, `${new Date().toISOString().replace(/[:.]/g, '-')}-${name.replace(/[^a-z0-9-]+/gi, '_').slice(0, 60)}`)
  try {
    await page.screenshot({ path: `${base}.png`, fullPage: true })
    writeFileSync(`${base}.html`, await page.content())
  } catch {
    /* the page may already be gone */
  }
  return base
}
