/**
 * A stand-in for LinkedIn and Naukri, built from their real page structure (roles, labels, the
 * class names the adapters fall back to). It proves the agent's logic end to end; it cannot prove
 * the real sites have not changed — the first real dry run does that.
 */
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'

export interface Submission {
  platform: 'LinkedIn' | 'Naukri'
  job: string
  values: Record<string, string | boolean>
}

const page = (title: string, body: string, script = '') => `<!doctype html><html><head><meta charset="utf-8"><title>${title}</title>
<style>
  body{font-family:sans-serif} .sr{position:absolute;opacity:0;width:1px;height:1px}
  [role=dialog],[role=alertdialog]{border:1px solid #999;padding:12px;margin:12px;background:#fff}
  [role=listbox]{border:1px solid #ccc} .artdeco-inline-feedback--error{color:#b00}
</style></head><body>${body}<script>${script}</script></body></html>`

// ---------------------------------------------------------------- LinkedIn

const LI_JOBS: Record<string, { title: string; company: string; kind: 'easy' | 'unknown-question' | 'applied' | 'external' }> = {
  '1001': { title: '.NET Developer', company: 'Acme', kind: 'easy' },
  '1002': { title: 'C# Engineer', company: 'Globex', kind: 'unknown-question' },
  '1003': { title: 'Senior .NET Engineer', company: 'Initech', kind: 'applied' },
  '1004': { title: 'Full Stack .NET Developer', company: 'Umbrella', kind: 'external' },
  '1005': { title: 'Java Developer Intern', company: 'Hooli', kind: 'easy' },
}

function linkedInSearch(start: number) {
  const cards =
    start > 0
      ? ''
      : Object.entries(LI_JOBS)
          .map(
            ([id, j]) => `<li data-occludable-job-id="${id}">
              <a class="job-card-container__link" href="/li/jobs/view/${id}/" aria-label="${j.title}">${j.title}</a>
              <div class="artdeco-entity-lockup__subtitle">${j.company}</div>
              <div class="artdeco-entity-lockup__caption">Bengaluru, India</div></li>`,
          )
          .join('')
  return page('Jobs', `<nav id="global-nav">nav</nav><ul class="jobs-search-results-list">${cards}</ul>${start > 0 ? '<p>No matching jobs found.</p>' : ''}`)
}

const CONTACT_STEP = `
  <div class="fb-dash-form-element"><label for="email">Email address</label><input id="email" value="me@example.com" required></div>
  <div class="fb-dash-form-element"><label for="phone">Mobile phone number</label><input id="phone" required></div>
  <div class="fb-dash-form-element" style="position:relative"><label for="city">City</label>
    <input id="city" role="combobox" aria-autocomplete="list" required><div id="city-list"></div></div>`

const LI_STEPS: Record<string, string[]> = {
  easy: [
    CONTACT_STEP,
    `<div class="fb-dash-form-element"><label for="q1">How many years of work experience do you have with C#?</label><input id="q1" required data-number></div>
     <div class="fb-dash-form-element"><label for="q2">Are you willing to relocate?</label>
       <select id="q2" required><option>Select an option</option><option>Yes</option><option>No</option></select></div>
     <fieldset aria-required="true"><legend><span class="fb-dash-form-element__label">Will you now or in the future require visa sponsorship?</span></legend>
       <input class="sr" type="radio" name="spons" id="s-yes" value="Yes"><label for="s-yes">Yes</label>
       <input class="sr" type="radio" name="spons" id="s-no" value="No"><label for="s-no">No</label></fieldset>`,
    `<h3>Review your application</h3>
     <input type="checkbox" id="follow" checked><label for="follow">Follow the company to stay up to date with their page.</label>`,
  ],
  'unknown-question': [
    CONTACT_STEP,
    `<div class="fb-dash-form-element"><label for="u1">What is your favourite programming joke?</label><input id="u1" required></div>`,
    `<h3>Review your application</h3>`,
  ],
}

function linkedInJob(id: string) {
  const j = LI_JOBS[id]
  if (!j) return null
  const top = `<nav id="global-nav">nav</nav><div class="job-details-jobs-unified-top-card__job-title"><h1>${j.title}</h1></div>
    <div class="job-details-jobs-unified-top-card__company-name"><a>${j.company}</a></div>
    <div class="jobs-description__content"><p>${j.company} is hiring a ${j.title}. You will build APIs in C#, .NET and SQL.
      3-5 years of experience. Hybrid in Bengaluru.</p></div>`
  const action =
    j.kind === 'applied'
      ? `<div class="artdeco-inline-feedback--success"><span>Applied 3 days ago</span></div>`
      : j.kind === 'external'
        ? `<button class="jobs-apply-button">Apply</button>`
        : `<button class="jobs-apply-button" aria-label="Easy Apply to ${j.title} at ${j.company}">Easy Apply</button>`
  const steps = LI_STEPS[j.kind] ?? []
  const script = `
    const steps = ${JSON.stringify(steps)}; let step = 0; const values = {}; let dialog;
    const btn = document.querySelector('.jobs-apply-button');
    if (btn && /easy/i.test(btn.textContent)) btn.onclick = open;
    function open() {
      dialog = document.createElement('div'); dialog.className = 'jobs-easy-apply-modal'; dialog.setAttribute('role','dialog'); dialog.setAttribute('aria-label','Apply to ${j.company}');
      document.body.appendChild(dialog); render();
    }
    function collect() {
      dialog.querySelectorAll('input,select').forEach(el => {
        if (el.type === 'radio') { if (el.checked) values[el.name] = el.value }
        else if (el.type === 'checkbox') values[el.id] = el.checked
        else values[el.id] = el.value
      });
    }
    function render() {
      const last = step === steps.length - 1;
      const primary = last ? '<button aria-label="Submit application">Submit application</button>'
        : step === steps.length - 2 ? '<button aria-label="Review your application">Review</button>'
        : '<button aria-label="Continue to next step">Next</button>';
      dialog.innerHTML = '<button aria-label="Dismiss">×</button><h2>Apply to ${j.company}</h2><form onsubmit="return false">' + steps[step] + '</form><footer>' + primary + '</footer>';
      const city = dialog.querySelector('#city');
      if (city) city.oninput = () => {
        dialog.querySelector('#city-list').innerHTML = city.value ? '<ul role="listbox"><li role="option">Bengaluru, Karnataka, India</li></ul>' : '';
        const opt = dialog.querySelector('[role=option]'); if (opt) opt.onclick = () => { city.value = opt.textContent; dialog.querySelector('#city-list').innerHTML = ''; city.dataset.picked = '1' };
      };
      dialog.querySelector('footer button').onclick = next;
      dialog.querySelector('[aria-label=Dismiss]').onclick = confirmDiscard;
    }
    function invalid() {
      const bad = [];
      dialog.querySelectorAll('input[required],select[required]').forEach(el => {
        const empty = el.tagName === 'SELECT' ? el.selectedIndex === 0 : !el.value.trim();
        if (empty || (el.dataset.number !== undefined && !/^\\d+$/.test(el.value)) || (el.id === 'city' && !el.dataset.picked)) bad.push(el);
      });
      dialog.querySelectorAll('fieldset[aria-required=true]').forEach(fs => { if (!fs.querySelector('input:checked')) bad.push(fs) });
      return bad;
    }
    function next() {
      dialog.querySelectorAll('.artdeco-inline-feedback--error').forEach(e => e.remove());
      const bad = invalid();
      if (bad.length) { bad.forEach(el => el.insertAdjacentHTML('afterend', '<div class="artdeco-inline-feedback--error" role="alert">Please enter a valid answer</div>')); return; }
      collect();
      if (step < steps.length - 1) { step++; render(); return; }
      fetch('/__submit', { method: 'POST', body: JSON.stringify({ platform: 'LinkedIn', job: '${id}', values }) }).then(() => {
        dialog.innerHTML = '<h2 id="post-apply-modal">Your application was sent to ${j.company}</h2><button aria-label="Dismiss">Done</button>';
        dialog.querySelector('button').onclick = () => dialog.remove();
      });
    }
    function confirmDiscard() {
      const c = document.createElement('div'); c.setAttribute('role','alertdialog');
      c.innerHTML = '<p>Discard application?</p><button>Discard</button><button>Save</button>';
      document.body.appendChild(c);
      c.querySelector('button').onclick = () => { c.remove(); dialog.remove(); };
    }`
  return page(j.title, top + action, script)
}

// ---------------------------------------------------------------- Naukri

const NK_JOBS: Record<string, { title: string; kind: 'direct' | 'chatbot' | 'applied' | 'company' | 'chatbot-unknown' }> = {
  '2001': { title: '.NET Developer', kind: 'direct' },
  '2002': { title: 'C# Backend Engineer', kind: 'chatbot' },
  '2003': { title: '.NET Lead', kind: 'applied' },
  '2004': { title: 'Full Stack .NET Engineer', kind: 'company' },
  '2005': { title: 'C# Developer', kind: 'chatbot-unknown' },
}

function naukriSearch(pathname: string) {
  const cards = /-\d+$/.test(pathname)
    ? ''
    : Object.entries(NK_JOBS)
        .map(
          ([id, j]) => `<div class="srp-jobtuple-wrapper" data-job-id="${id}"><a class="title" href="/nk/job/${id}">${j.title}</a>
            <span class="comp-name">Beta Ltd</span><span class="locWdth">Pune</span></div>`,
        )
        .join('')
  return page('Naukri jobs', `<div class="list">${cards}</div>`)
}

function naukriJob(id: string) {
  const j = NK_JOBS[id]
  if (!j) return null
  const header = `<h1>${j.title}</h1><div class="styles_jd-header-comp-name__x"><a>Beta Ltd</a></div>
    <section class="styles_job-desc-container__x">Beta Ltd needs a ${j.title}: C#, ASP.NET, Azure. Minimum 4 years. Pune, work from office.</section>`
  const action =
    j.kind === 'applied'
      ? '<button id="already-applied" disabled>Applied</button>'
      : j.kind === 'company'
        ? '<button id="company-site-button">Apply on company site</button>'
        : '<button id="apply-button">Apply</button>'
  const questions =
    j.kind === 'chatbot'
      ? [
          { q: 'Are you comfortable working from office in Pune?', type: 'radio', options: ['Yes', 'No'] },
          { q: 'What is your notice period (in days)?', type: 'text' },
        ]
      : j.kind === 'chatbot-unknown'
        ? [{ q: 'Describe your favourite project in one line.', type: 'text' }]
        : []
  const script = `
    const questions = ${JSON.stringify(questions)}; const answers = {}; let i = 0; let drawer;
    const btn = document.getElementById('apply-button'); if (btn) btn.onclick = apply;
    function done() {
      fetch('/__submit', { method: 'POST', body: JSON.stringify({ platform: 'Naukri', job: '${id}', values: answers }) }).then(() => {
        if (drawer) drawer.remove();
        document.body.insertAdjacentHTML('beforeend', '<span class="apply-message">You have successfully applied to ${j.title}</span>');
      });
    }
    function apply() {
      if (!questions.length) return done();
      drawer = document.createElement('div'); drawer.className = 'chatbot_DrawerContentWrapper'; document.body.appendChild(drawer); ask();
    }
    function ask() {
      const q = questions[i];
      drawer.insertAdjacentHTML('beforeend', '<div class="botMsg"><span>' + q.q + '</span></div>');
      drawer.querySelectorAll('.answer-area').forEach(e => e.remove());
      const area = q.type === 'radio'
        ? '<div class="answer-area ssrc__radio-btn-container">' + q.options.map((o, k) => '<input type="radio" name="r' + i + '" id="r' + i + k + '" value="' + o + '"><label for="r' + i + k + '">' + o + '</label>').join('') + '</div>'
        : '<div class="answer-area"><div class="textArea" contenteditable="true"></div></div>';
      drawer.insertAdjacentHTML('beforeend', area + '<div class="answer-area sendMsg" role="button">Save</div>');
      drawer.querySelector('.sendMsg').onclick = save;
    }
    function save() {
      const q = questions[i];
      const v = q.type === 'radio' ? (drawer.querySelector('input:checked') || {}).value : drawer.querySelector('[contenteditable]').innerText.trim();
      if (!v) return;
      answers['q' + i] = v; i++;
      if (i < questions.length) ask(); else done();
    }`
  return page(j.title, header + action, script)
}

// ---------------------------------------------------------------- LinkedIn people search and profiles (JD circle, outreach)

/** Search cards in LinkedIn's older (entity-result classes) layout. */
const oldCard = (slug: string | null, name: string, degree: string, headline: string, summary = '') => `
  <li class="reusable-search__result-container"><div class="entity-result">
    <span class="entity-result__title-text">${slug
      ? `<a class="app-aware-link" href="https://www.linkedin.com/in/${slug}?miniProfileUrn=urn%3Ali%3Afs_miniProfile%3AACoAA"><span dir="ltr"><span aria-hidden="true">${name}</span><span class="visually-hidden">View ${name}’s profile</span></span></a>`
      : `<span aria-hidden="true">${name}</span>`}
      <span class="entity-result__badge"><span class="entity-result__badge-text"><span aria-hidden="true">• ${degree}</span><span class="visually-hidden">${degree} degree connection</span></span></span></span>
    <div class="entity-result__primary-subtitle">${headline}</div>
    <div class="entity-result__secondary-subtitle">Pune, Maharashtra, India</div>
    ${summary ? `<p class="entity-result__summary">${summary}</p>` : ''}
    <button>Connect</button></div></li>`

/** Search cards in the newer layout: generated class names, so only the link and the visible lines are stable. */
const newCard = (slug: string, name: string, degree: string, headline: string, summary = '') => `
  <div data-view-name="search-entity-result-universal-template"><div class="x9a1">
    <a class="q7z" href="/li/in/${slug}/"><span aria-hidden="true">${name}</span></a>
    <div class="b3x">• ${degree}</div>
    <div class="k8c">${headline}</div>
    <div class="m2p">Pune</div>
    ${summary ? `<div class="s4t">${summary}</div>` : ''}
    <button>Message</button></div></div>`

function linkedInPeopleSearch(keywords: string, network: string | null) {
  let cards = ''
  if (network === '["F"]')
    cards = newCard('meera-iyer', 'Meera Iyer', '1st', 'Software Engineer at Northwind') +
      newCard('old-colleague', 'Old Colleague', '1st', 'Consultant', 'Past: Developer at Northwind')
  else if (/recruiter/.test(keywords))
    cards = oldCard('asha-rao', 'Asha Rao', '2nd', 'Senior Technical Recruiter at Northwind') +
      oldCard('other-recruiter', 'Kim Lee', '2nd', 'Recruiter at Contoso') +
      oldCard(null, 'LinkedIn Member', '3rd+', 'Talent Acquisition at Northwind')
  else if (/hiring manager/.test(keywords))
    cards = newCard('ravi-menon', 'Ravi Menon', '3rd+', 'Engineering Manager, .NET Platform', 'Current: Engineering Manager at Northwind') +
      oldCard('asha-rao', 'Asha Rao', '2nd', 'Senior Technical Recruiter at Northwind')
  return page('People', `<nav id="global-nav">nav</nav><main><div class="search-results-container">${cards ? `<ul class="reusable-search__entity-result-list">${cards}</ul>` : '<h2>No results found</h2>'}</div></main>`)
}

const PROFILES: Record<string, { name: string; degree: string; kind: 'connect' | 'connect-in-more' | 'connected' | 'pending' | 'follow-only' }> = {
  'asha-rao': { name: 'Asha Rao', degree: '2nd', kind: 'connect' },
  'ravi-menon': { name: 'Ravi Menon', degree: '3rd+', kind: 'connect-in-more' },
  'meera-iyer': { name: 'Meera Iyer', degree: '1st', kind: 'connected' },
  'pending-person': { name: 'Priya Shah', degree: '2nd', kind: 'pending' },
  'follow-only': { name: 'Famous Person', degree: '3rd+', kind: 'follow-only' },
}

function linkedInProfile(slug: string) {
  if (slug === 'captcha-person') return page('Security', '<main><h1>Let\'s do a quick security check</h1><iframe id="captcha-internal" src="about:blank"></iframe></main>')
  const p = PROFILES[slug]
  if (!p) return null
  const actions = {
    connect: `<button aria-label="Invite ${p.name} to connect" class="artdeco-button--primary" id="connect">Connect</button>
      <button aria-label="More actions">More</button>`,
    'connect-in-more': `<button aria-label="Follow ${p.name}">Follow</button><button aria-label="More actions" id="more">More</button>
      <div class="artdeco-dropdown__content" id="menu" hidden><div role="button" aria-label="Invite ${p.name} to connect" id="connect">Connect</div></div>`,
    connected: `<button aria-label="Message ${p.name}" id="message">Message</button><button aria-label="More actions">More</button>`,
    pending: `<button aria-label="Pending, click to withdraw invitation sent to ${p.name}">Pending</button>`,
    'follow-only': `<button aria-label="Follow ${p.name}">Follow</button>`,
  }[p.kind]
  const body = `<nav id="global-nav">nav</nav><main><section class="artdeco-card pv-top-card">
      <h1 class="text-heading-xlarge">${p.name}</h1><span class="dist-value">${p.degree}</span>
      <div class="text-body-medium">Works at Northwind</div>
      <div class="pvs-profile-actions" id="actions">${actions}</div></section>
      <div id="toasts"></div><div id="overlay"></div></main>`
  const script = `
    const slug = ${JSON.stringify(slug)}, name = ${JSON.stringify(p.name)};
    const post = (job, values) => fetch('/__submit', { method: 'POST', body: JSON.stringify({ platform: 'LinkedIn', job, values }) });
    document.getElementById('more')?.addEventListener('click', () => { document.getElementById('menu').hidden = false; });
    document.getElementById('connect')?.addEventListener('click', () => {
      document.getElementById('menu') && (document.getElementById('menu').hidden = true);
      const d = document.createElement('div');
      d.setAttribute('role', 'dialog');
      d.innerHTML = '<h2>You can customize this invitation</h2><button aria-label="Dismiss">×</button>' +
        '<button id="add-note">Add a note</button><button aria-label="Send without a note">Send without a note</button>';
      document.body.appendChild(d);
      d.querySelector('[aria-label="Dismiss"]').onclick = () => d.remove();
      d.querySelector('[aria-label="Send without a note"]').onclick = () => { post('invite:' + slug, { note: '' }); d.remove(); };
      d.querySelector('#add-note').onclick = () => {
        d.innerHTML = '<button aria-label="Dismiss">×</button><label for="custom-message">Add a note</label>' +
          '<textarea id="custom-message" name="message" maxlength="300"></textarea><button aria-label="Send invitation">Send</button>';
        d.querySelector('[aria-label="Dismiss"]').onclick = () => d.remove();
        d.querySelector('[aria-label="Send invitation"]').onclick = async () => {
          await post('invite:' + slug, { note: d.querySelector('textarea').value });
          d.remove();
          document.getElementById('actions').innerHTML = '<button aria-label="Pending, click to withdraw invitation sent to ' + name + '">Pending</button>';
          document.getElementById('toasts').textContent = 'Invitation sent';
        };
      };
    });
    document.getElementById('message')?.addEventListener('click', () => {
      const o = document.getElementById('overlay');
      o.innerHTML = '<div class="msg-overlay-conversation-bubble"><button aria-label="Close your conversation with ' + name + '">×</button>' +
        '<div class="msg-s-message-list"></div><form><div class="msg-form__contenteditable" contenteditable="true" role="textbox" aria-label="Write a message…"></div>' +
        '<button type="submit" class="msg-form__send-button">Send</button></form></div>';
      o.querySelector('[aria-label^="Close your conversation"]').onclick = () => {
        if (o.querySelector('[role=textbox]').innerText.trim()) post('draft-left:' + slug, { text: o.querySelector('[role=textbox]').innerText });
        o.innerHTML = '';
      };
      o.querySelector('form').onsubmit = async (e) => {
        e.preventDefault();
        const box = o.querySelector('[role=textbox]');
        const text = box.innerText;
        await post('message:' + slug, { text });
        const m = document.createElement('p'); m.className = 'msg-s-event-listitem__body'; m.textContent = text;
        o.querySelector('.msg-s-message-list').appendChild(m);
        box.innerHTML = '';
      };
    });`
  return page(p.name, body, script)
}

// ---------------------------------------------------------------- server

export async function startMock(): Promise<{ origin: string; submissions: Submission[]; close: () => Promise<void> }> {
  const submissions: Submission[] = []
  const server: Server = createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://x')
    const send = (status: number, html: string) => {
      res.writeHead(status, { 'Content-Type': 'text/html; charset=utf-8' })
      res.end(html)
    }
    if (req.method === 'POST' && url.pathname === '/__submit') {
      let body = ''
      req.on('data', (c) => (body += c))
      req.on('end', () => {
        submissions.push(JSON.parse(body))
        res.writeHead(204).end()
      })
      return
    }
    const p = url.pathname
    // A logged-out LinkedIn redirects every page to its login wall.
    if (p.startsWith('/wall/') && p !== '/wall/login') {
      res.writeHead(302, { Location: `/wall/login?session_redirect=${encodeURIComponent(p)}` }).end()
      return
    }
    if (p === '/wall/login') return send(200, page('Sign in', '<form><input name="session_key"></form>'))
    if (p === '/li/feed/') return send(200, page('Feed', '<nav id="global-nav">feed</nav>'))
    if (p === '/li/jobs/search/') return send(200, linkedInSearch(Number(url.searchParams.get('start') ?? 0)))
    if (p === '/li/checkpoint/challenge/') return send(200, page('Security check', '<main><h1>Security verification</h1></main>'))
    if (p === '/li/search/results/people/') {
      const keywords = url.searchParams.get('keywords') ?? ''
      if (/checkpoint/i.test(keywords)) {
        res.writeHead(302, { Location: '/li/checkpoint/challenge/' }).end()
        return
      }
      return send(200, linkedInPeopleSearch(keywords.toLowerCase(), url.searchParams.get('network')))
    }
    const profile = p.match(/^\/li\/in\/([^/]+)\/$/)
    if (profile) {
      if (profile[1] === 'checkpoint-person') {
        res.writeHead(302, { Location: '/li/checkpoint/challenge/' }).end()
        return
      }
      const html = linkedInProfile(profile[1])
      return html ? send(200, html) : send(404, 'gone')
    }
    const li = p.match(/^\/li\/jobs\/view\/(\d+)\/$/)
    if (li) {
      const html = linkedInJob(li[1])
      return html ? send(200, html) : send(404, 'gone')
    }
    if (p === '/nk/mnjuser/homepage') return send(200, page('Naukri home', '<div>home</div>'))
    const nk = p.match(/^\/nk\/job\/(\d+)$/)
    if (nk) {
      const html = naukriJob(nk[1])
      return html ? send(200, html) : send(404, 'gone')
    }
    if (/^\/nk\/[a-z0-9-]+-jobs/.test(p)) return send(200, naukriSearch(p))
    send(404, 'not found')
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const { port } = server.address() as AddressInfo
  return {
    origin: `http://127.0.0.1:${port}`,
    submissions,
    close: () => new Promise((resolve) => server.close(() => resolve())),
  }
}

// ---------------------------------------------------------------- fake OpportunityPilot API (agent endpoints only)

export interface FakeOutboxItem {
  draftId: string
  opportunityId: string
  version: number
  profileUrl: string
  body: string
  fitsConnectionNote: boolean
  organization: string
  title: string
  personName: string | null
}

export interface FakeApi {
  url: string
  circleRequests: { opportunityId: string; mode: string; organization: string; title: string; location: string | null; requestedAt: string }[]
  circles: { opportunityId: string; people: { name: string; title: string | null; profileUrl: string; relation: string; degree: number | null }[] }[]
  outbox: FakeOutboxItem[]
  /** Every start/confirm/fail call, in order, as the gateway saw it. */
  executions: { draftId: string; step: 'start' | 'confirm' | 'fail'; body: Record<string, unknown>; reused?: boolean }[]
  postings: { campaignId: string; body: { platform: string; queueResearch: boolean; items: { externalId: string; description: string | null }[] } }[]
  reports: { key: string | undefined; items: { externalJobId: string; status: string; opportunityId?: string }[] }[]
  shortlist: { opportunityId: string; campaignId: string; platform: string; externalId: string; url: string; title: string; organization: string }[]
  close: () => Promise<void>
}

export async function startFakeApi(campaigns: unknown[]): Promise<FakeApi> {
  const api = { postings: [], reports: [], shortlist: [], circleRequests: [], circles: [], outbox: [], executions: [] } as unknown as FakeApi
  // The gateway's at-most-once rule: one open or successful execution per draft; a failed one allows a new start.
  const state = new Map<string, 'pending' | 'succeeded' | 'failed'>()
  const server = createServer((req, res) => {
    let body = ''
    req.on('data', (c) => (body += c))
    req.on('end', () => {
      const url = new URL(req.url ?? '/', 'http://x')
      const json = (status: number, data: unknown) => res.writeHead(status, { 'Content-Type': 'application/json' }).end(JSON.stringify(data))
      if (req.headers['x-agent-key'] !== 'opk_test') return json(401, {})
      if (url.pathname === '/api/v1/agent/campaigns') return json(200, campaigns)
      if (url.pathname === '/api/v1/agent/shortlist') return json(200, api.shortlist.filter((s) => s.platform === url.searchParams.get('platform')))
      const posting = url.pathname.match(/^\/api\/v1\/agent\/campaigns\/([^/]+)\/postings$/)
      if (posting) {
        const parsed = JSON.parse(body)
        api.postings.push({ campaignId: posting[1], body: parsed })
        return json(200, { accepted: parsed.items.length, sourceId: 's1', jobId: parsed.queueResearch ? 'j1' : null })
      }
      if (url.pathname === '/api/v1/agent/circle-requests') return json(200, api.circleRequests)
      if (url.pathname === '/api/v1/agent/circle' && req.method === 'POST') {
        const parsed = JSON.parse(body)
        api.circles.push(parsed)
        api.circleRequests = api.circleRequests.filter((r) => r.opportunityId !== parsed.opportunityId)
        return json(200, { accepted: parsed.people.length, added: parsed.people.length, updated: 0, total: parsed.people.length })
      }
      if (url.pathname === '/api/v1/agent/linkedin-outbox')
        return json(200, api.outbox.filter((o) => !['pending', 'succeeded'].includes(state.get(o.draftId) ?? '')))
      const step = url.pathname.match(/^\/api\/v1\/agent\/linkedin-outbox\/([^/]+)\/(start|confirm|fail)$/)
      if (step) {
        const [, draftId, name] = step as unknown as [string, string, 'start' | 'confirm' | 'fail']
        const parsed = JSON.parse(body || '{}')
        const current = state.get(draftId)
        if (name === 'start') {
          const reused = current === 'pending' || current === 'succeeded'
          if (!reused) state.set(draftId, 'pending')
          api.executions.push({ draftId, step: name, body: parsed, reused })
          return json(200, { id: `e-${draftId}`, provider: 'LinkedInAgent', state: reused && current === 'succeeded' ? 'Succeeded' : 'AwaitingManualConfirmation', reused })
        }
        if (current !== 'pending') return json(409, { title: 'No LinkedIn send is in progress for this draft.' })
        state.set(draftId, name === 'confirm' ? 'succeeded' : 'failed')
        api.executions.push({ draftId, step: name, body: parsed })
        return json(200, { id: `e-${draftId}`, provider: 'LinkedInAgent', state: name === 'confirm' ? 'Succeeded' : 'Failed', reused: false })
      }
      if (url.pathname === '/api/v1/applications/report') {
        api.reports.push({ key: req.headers['x-agent-key'] as string, items: JSON.parse(body).items })
        return json(200, { accepted: 1 })
      }
      json(404, {})
    })
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  api.url = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  api.close = () => new Promise((resolve) => server.close(() => resolve()))
  return api
}
