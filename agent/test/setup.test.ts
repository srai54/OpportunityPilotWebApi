import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { browserChannel } from '../src/browser.ts'
import { setApiKey } from '../src/config.ts'

describe('setup', () => {
  it('saves the agent key into config.json and keeps the rest', () => {
    const path = join(mkdtempSync(join(tmpdir(), 'op-')), 'config.json')
    writeFileSync(path, JSON.stringify({ api: { url: 'https://x.test', key: '' }, answers: { fields: [] } }))
    setApiKey('  opk_abcdefghijklmnopqrstuvwxyz0123456789-_ABCDE ', path)
    const saved = JSON.parse(readFileSync(path, 'utf8'))
    expect(saved.api).toEqual({ url: 'https://x.test', key: 'opk_abcdefghijklmnopqrstuvwxyz0123456789-_ABCDE' })
    expect(saved.answers).toEqual({ fields: [] })
    expect(() => setApiKey('not a key', path)).toThrow(/does not look like an agent key/)
  })
  it('uses Edge on Windows, Playwright Chromium elsewhere, unless OP_BROWSER says otherwise', () => {
    expect(browserChannel({}, 'win32')).toBe('msedge')
    expect(browserChannel({}, 'linux')).toBeUndefined()
    expect(browserChannel({ OP_BROWSER: 'chromium' }, 'win32')).toBeUndefined()
    expect(browserChannel({ OP_BROWSER: 'chrome' }, 'linux')).toBe('chrome')
  })
})
