// SPDX-License-Identifier: MIT
// ChainShot popup: thin UI over the service worker (all logic lives in background.js).

const $ = (id) => document.getElementById(id);

async function call(cmd, args = {}) {
  const res = await chrome.runtime.sendMessage({ cmd, ...args });
  if (!res?.ok) throw new Error(res?.error || 'No answer from ChainShot.');
  return res.result;
}

function say(text, kind = '') {
  $('message').textContent = text;
  $('message').className = kind;
}

function busy(button, on) {
  button.disabled = on;
}

function renderDestination(dest) {
  for (const b of [$('destDownloads'), $('destApp')]) b.setAttribute('aria-pressed', String(b.dataset.dest === dest));
  for (const el of document.querySelectorAll('.browser-only')) el.hidden = dest === 'app';
  $('caseSelectLabel').hidden = dest === 'app';
  document.body.classList.toggle('app-mode', dest === 'app');
}

function renderApp(st) {
  const app = st.app || { connected: false, error: '' };
  const ready = app.connected && app.case;
  $('newCase').hidden = true;
  $('main').hidden = !ready;
  $('appLink').hidden = Boolean(ready);
  if (!app.connected) {
    $('caseLine').textContent = 'ChainShot app not connected';
    $('appText').textContent = app.error;
  } else if (!app.case) {
    $('caseLine').textContent = 'No case open in ChainShot';
    $('appText').textContent = app.warning || 'Open or create a case in ChainShot, then try again.';
  } else {
    $('caseLine').textContent = `${app.case.name} - ${app.case.entries} entries (ChainShot app)`;
    $('tag').value = app.tag || '';
    $('note').value = st.settings.nextNote || '';
    $('folder').textContent = `Saved in the ChainShot case: ${app.case.path}`;
    $('last').hidden = true;
  }
}

function render(st) {
  const dest = st.settings.destination || 'downloads';
  $('fullPage').checked = st.settings.fullPage === true;
  $('archiveRow').hidden = !st.mhtml;
  $('archive').checked = st.settings.archive === true;
  renderDestination(dest);
  if (dest === 'app') { renderApp(st); return; }
  $('appLink').hidden = true;
  const hasCase = Boolean(st.case);
  $('newCase').hidden = hasCase;
  $('main').hidden = !hasCase;
  $('cancelCase').hidden = !hasCase;
  $('caseLine').textContent = hasCase ? `${st.case.case} - ${st.case.entries} entries` : 'No case: create one to start';
  if (!hasCase) return;
  $('tag').value = st.settings.tag || '';
  $('note').value = st.settings.nextNote || '';
  $('folder').textContent = `Folder: Downloads/${st.case.folder}/`;
  const sel = $('caseSelect');
  sel.replaceChildren(...st.cases.map((c) => {
    const o = document.createElement('option');
    o.value = c.folder; o.textContent = `${c.case} (${c.entries})`; o.selected = c.folder === st.case.folder;
    return o;
  }));
  const last = $('last');
  last.hidden = !st.last;
  if (st.last) {
    last.innerHTML = '';
    const head = document.createElement('div');
    head.textContent = `#${st.last.seq} ${st.last.type} - ${st.last.utc}`;
    const file = document.createElement('div');
    file.className = 'muted';
    file.textContent = st.last.file || '';
    const sha = document.createElement('code');
    sha.textContent = st.last.sha256 ? `SHA-256 ${st.last.sha256}` : '';
    last.append(head, file, sha);
  }
}

async function refresh() {
  render(await call('status'));
}

async function init() {
  try {
    const cmds = await chrome.commands.getAll();
    const c = cmds.find((x) => x.name === 'capture-tab');
    $('shortcut').textContent = c?.shortcut || 'no shortcut';
  } catch { /* commands API unavailable: keep default label */ }

  $('createCase').addEventListener('click', async () => {
    busy($('createCase'), true);
    try {
      render(await call('newCase', { caseName: $('caseName').value, examiner: $('examiner').value }));
      say('Case created.', 'ok');
    } catch (e) { say(e.message, 'bad'); } finally { busy($('createCase'), false); }
  });
  $('cancelCase').addEventListener('click', refresh);
  for (const b of [$('destDownloads'), $('destApp')]) {
    b.addEventListener('click', async () => {
      say('');
      render(await call('setSettings', { destination: b.dataset.dest }));
    });
  }
  $('fullPage').addEventListener('change', () => call('setSettings', { fullPage: $('fullPage').checked }).catch((e) => say(e.message, 'bad')));
  $('archive').addEventListener('change', async () => {
    let on = $('archive').checked;
    // The page archive needs the optional pageCapture permission, asked once, on this click.
    if (on) on = await chrome.permissions.request({ permissions: ['pageCapture'] }).catch(() => false);
    $('archive').checked = on;
    await call('setSettings', { archive: on }).catch((e) => say(e.message, 'bad'));
    if (!on) say('Page archive off.');
  });
  $('retryApp').addEventListener('click', () => refresh().catch((e) => say(e.message, 'bad')));
  $('showNewCase').addEventListener('click', () => { $('main').hidden = true; $('newCase').hidden = false; });

  $('tag').addEventListener('change', () => call('setSettings', { tag: $('tag').value.trim() }).catch((e) => say(e.message, 'bad')));
  $('note').addEventListener('change', () => call('setSettings', { nextNote: $('note').value }).catch((e) => say(e.message, 'bad')));
  $('caseSelect').addEventListener('change', async () => render(await call('setSettings', { activeFolder: $('caseSelect').value })));

  $('capture').addEventListener('click', async () => {
    busy($('capture'), true);
    say('Capturing...');
    try {
      await call('setSettings', { tag: $('tag').value.trim() });
      const e = await call('capture', { note: $('note').value });
      $('note').value = '';
      await refresh();
      const extra = e.archive ? ` Archive #${e.archive.seq}.` : '';
      say((e.destination === 'app' ? `#${e.seq} saved in the ChainShot case.` : `#${e.seq} saved and chained.`) + extra, 'ok');
    } catch (e) { say(e.message, 'bad'); } finally { busy($('capture'), false); }
  });

  $('logNote').addEventListener('click', async () => {
    try {
      const e = await call('addNote', { text: $('note').value });
      $('note').value = '';
      await call('setSettings', { nextNote: '' });
      await refresh();
      say(`#${e.seq} note recorded.`, 'ok');
    } catch (e) { say(e.message, 'bad'); }
  });

  $('verify').addEventListener('click', async () => {
    try {
      const r = await call('verify');
      say(r.ok ? `Chain intact (${r.count} entries).` : `PROBLEM: ${r.problems.join('; ')}`, r.ok ? 'ok' : 'bad');
    } catch (e) { say(e.message, 'bad'); }
  });

  $('rewrite').addEventListener('click', async () => {
    try { await call('rewriteJournal'); say('journal.jsonl rewritten.', 'ok'); } catch (e) { say(e.message, 'bad'); }
  });

  await refresh();
}

init().catch((e) => say(e.message, 'bad'));
