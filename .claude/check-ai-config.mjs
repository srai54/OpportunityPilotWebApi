#!/usr/bin/env node
// CI check of the Claude Code setup (2026-10-26). Fails when:
// - .mcp.json names a server that is not in the allowed table of docs/AI_TOOLING.md, or has inline credentials;
// - project MCP servers are enabled automatically, or the deny rules for secrets and destructive commands are missing;
// - a hook wired in .claude/settings.json does not exist, or a skill has no name/description front matter.
// Run: node .claude/check-ai-config.mjs
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const problems = []
const read = (rel) => readFileSync(join(ROOT, rel), 'utf8')

// MCP servers: only those listed in the policy table, never with secrets inline.
const mcp = JSON.parse(read('.mcp.json'))
const policy = read('docs/AI_TOOLING.md')
const allowed = new Set([...policy.matchAll(/^\|\s*`?([a-z0-9._-]+)`?\s*\|/gim)].map((m) => m[1].toLowerCase()).filter((n) => n !== 'server' && n !== 'none'))
for (const [name, server] of Object.entries(mcp.mcpServers ?? {})) {
  if (!allowed.has(name.toLowerCase())) problems.push(`.mcp.json: server "${name}" is not in the allowed table of docs/AI_TOOLING.md`)
  const env = JSON.stringify(server.env ?? {}) + JSON.stringify(server.headers ?? {})
  if (/(sk-|ghp_|xox[bp]-|AIza|Bearer\s+[A-Za-z0-9])/.test(env)) problems.push(`.mcp.json: server "${name}" has a credential inline; use \${ENV_VAR}`)
}

// Settings: no automatic project servers, deny rules in place, hooks present.
const settings = JSON.parse(read('.claude/settings.json'))
if (settings.enableAllProjectMcpServers !== false) problems.push('settings.json: enableAllProjectMcpServers must be false')
const deny = settings.permissions?.deny ?? []
for (const rule of ['Read(./.env)', 'Bash(git push --force:*)']) if (!deny.includes(rule)) problems.push(`settings.json: deny rule ${rule} is missing`)
if (!deny.some((r) => r.includes('--no-verify'))) problems.push('settings.json: a deny rule for --no-verify is missing')
for (const group of Object.values(settings.hooks ?? {}))
  for (const entry of group)
    for (const h of entry.hooks ?? []) {
      const m = /\.claude\/hooks\/([\w.-]+)/.exec(h.command ?? '')
      if (m && !existsSync(join(ROOT, '.claude', 'hooks', m[1]))) problems.push(`settings.json: hook ${m[1]} does not exist`)
    }

// Skills: every SKILL.md has a name and a description, so Claude Code can load it.
const skillsDir = join(ROOT, '.claude', 'skills')
if (existsSync(skillsDir))
  for (const name of readdirSync(skillsDir)) {
    const file = join(skillsDir, name, 'SKILL.md')
    if (!existsSync(file)) { problems.push(`.claude/skills/${name}: SKILL.md is missing`); continue }
    const head = /^---\r?\n([\s\S]*?)\r?\n---/.exec(readFileSync(file, 'utf8'))?.[1] ?? ''
    if (!/^name:\s*\S/m.test(head) || !/^description:\s*\S/m.test(head)) problems.push(`.claude/skills/${name}/SKILL.md: front matter needs name and description`)
  }

if (problems.length) {
  console.error(`AI config check failed:\n- ${problems.join('\n- ')}`)
  process.exit(1)
}
console.log(`AI config OK: ${Object.keys(mcp.mcpServers ?? {}).length} MCP servers, ${deny.length} deny rules, skills checked.`)
