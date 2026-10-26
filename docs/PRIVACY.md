# Privacy and data handling

What OpportunityPilot stores about people, why, for how long, and how it is removed. Applies to the API (`OpportunityPilotWebApi`), the web app and the candidate app.

## Whose data

| Who | What is stored | Why |
|---|---|---|
| Account holders (sales team, candidates who sign in) | Account id from the sign-in provider (Hanko / Supabase), email as a label, role, last seen | Sign-in, role-based access |
| Guests | A random guest id; no email | Try the app; data is separate per guest |
| Candidates on the bench | Name, headline, skills, availability, rate, contact details, consent and what may be shared, journey entries, resumes they wrote | Submitting them to clients with their consent |
| Contacts at companies | Name, title, work email or phone found on public pages or entered by the team | Outreach the team approves |

## Rules

- **Consent first:** a candidate is submitted only with recorded consent (`Granted`) and only the fields they agreed to share.
- **Candidate links show only what the team ticks.** Pay, rates and internal notes are never shown to a candidate or a client.
- **Nothing is sent without approval.** Emails go from the team's own mailbox after they approve them.
- **Job sites:** official APIs and public feeds; the desktop agent works in the person's own browser and stops at any login or CAPTCHA.
- **Logs:** no passwords, tokens, keys or message bodies in logs; ids and counts only. Tokens are stored hashed (progress links, guest sessions) or encrypted (mailbox and connector credentials).

## Retention and removal

- Research events older than 90 days are deleted by the retention service; guest sessions expire after 30 days; browser error reports are deleted 30 days after they were last seen.
- **Export:** Settings → Your data → Export JSON (`GET /api/v1/account-data/export`).
- **Delete:** Settings → Delete all data (`DELETE /api/v1/account-data`) removes every record of the account, including candidates, resumes, journeys and links.
- A candidate who withdraws consent: their private link stops working at once.

## Where data lives

PostgreSQL (Render), the web app on Vercel. AI providers (Groq, Gemini, Whisper) receive only the text needed for the request (a goal, a draft, a voice clip), never a candidate's contact details or rate.

**Claude with your own key (2026-10-26):** a person may connect their own Anthropic API key. Their AI requests then go to Anthropic under
*their* Anthropic account and its data terms. Resume tailoring sends the resume text (name, summary, skills, projects) and the role's job
description; never contact details, pay or rates. The key is stored encrypted, shown only as its last four characters, never logged or
exported, and deleted with "Remove key" or "Delete all data". A candidate's own key pasted on the resume page is used for that one request
and not stored.

**Feedback and error reports:** feedback stores the message, rating, page path and the sender's sign-in email (for a reply). Error reports
store the error message, the page path (link tokens masked), the first stack lines, the browser and the build; no form contents.

## Contact

Questions or removal requests: the repo owner (see `SECURITY.md`).
