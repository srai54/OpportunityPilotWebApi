# Third parties

Every outside service the app or its development uses, what it receives and whose terms apply. Updated 2026-10-26. Add a row
before adding a provider (and a `DECISIONS.md` entry when it replaces another).

## Runtime (the product)

| Service | Used for | Receives | Account / terms | Off switch |
|---|---|---|---|---|
| Render | Hosts the API | All API traffic | Owner's Render account; Render terms | — |
| Supabase (Postgres) | Database | All stored data (see DATA_INVENTORY.md) | Owner's Supabase project; Supabase terms and DPA | — |
| Vercel | Hosts the web apps; page-view analytics | Page requests; anonymous page views | Owner's Vercel account; Vercel terms | Analytics only in Vercel builds |
| Hanko / Supabase Auth | Sign-in | Email, passkeys / password hashes (held by the provider) | Provider terms | Auth settings |
| Groq | Default AI (text, Whisper voice) | The text or voice clip a request needs; no contact details, pay or rates | Server key; Groq terms | `Ai__GroqApiKey` empty |
| Google Gemini | Fallback AI | As Groq | Server key; Google AI terms | `Features__GeminiEnabled=false` |
| **Anthropic (Claude), per person** | AI for people who connect their own key; resume tailoring | The text a request needs (goal, draft, resume + job description) | **The person's own Anthropic account and its commercial terms / data policy**; the app holds the key encrypted | "Remove key" in Settings → Claude |
| JSearch (RapidAPI), Adzuna, Remotive, public job feeds | Job search | Search terms, locations | Server keys; each provider's terms (JSearch ≤ 200 searches a month) | Per-provider keys |
| Apollo | Lead search (paid, currently 0 credits) | Company and role filters | Server key; Apollo terms | Daily budget 0 |
| Composio | One-click connectors (optional) | OAuth for the apps the owner connects | Composio terms | Plugin options empty |
| Microsoft Graph / Gmail / SMTP | Sending from the owner's mailbox after approval | The approved message | The owner's own mailbox and provider terms | Disconnect the mailbox |

Job sites without an API (LinkedIn, Naukri) are never scraped by the server; the desktop agent works in the person's own browser,
under their own account and that site's terms, and stops at any login or CAPTCHA.

## Development

| Service | Used for | Receives | Rule |
|---|---|---|---|
| GitHub | Code, CI | The repositories | No secrets in the repo (gitleaks in CI and pre-commit) |
| Claude Code (Anthropic) | AI-assisted development | Code and docs in the session | `docs/AI_TOOLING.md`: no production data or secrets in prompts; no MCP server without a policy entry |
