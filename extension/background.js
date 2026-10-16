// SPDX-License-Identifier: MIT
// ChainShot browser extension - service worker.
//
// A case is a folder under the browser's download directory:
//   <Downloads>/ChainShot/<case-slug>-<timestamp>/
//       chainshot-case.json, journal.jsonl, captures/NNNN_<UTC>_<tag>.png
// which is a regular ChainShot case: `python chainshot.py verify <folder>` checks it.
// The journal of record is kept in chrome.storage.local and rewritten to disk
// after every entry (downloads API, conflictAction "overwrite").

import {
  FORMAT_VERSION, GENESIS, CHAINED_FIELDS, sanitize, nextChain, sha256Hex,
  formatUtc, formatLocal, fileStamp, slugify, verifyEntries,
} from './lib/chain.js';

const VERSION = chrome.runtime.getManifest().version;
const SOURCE = `chainshot-ext/${VERSION}`;
const ENTRY_TYPES = ['screenshot', 'browser', 'file', 'note', 'seal', 'timestamp'];

// ---------------------------------------------------------------- state ---
async function loadState() {
  const { settings = {}, cases = {} } = await chrome.storage.local.get(['settings', 'cases']);
  return { settings: { tag: '', nextNote: '', activeFolder: '', ...settings }, cases };
}

async function saveState(state) {
  await chrome.storage.local.set({ settings: state.settings, cases: state.cases });
}

function activeCase(state) {
  const c = state.cases[state.settings.activeFolder];
  if (!c) throw new Error('No case yet: create one in the ChainShot popup.');
  return c;
}

// All writes go through one queue so entries are never interleaved.
let queue = Promise.resolve();
function serial(task) {
  const run = queue.then(task, task);
  queue = run.catch(() => {});
  return run;
}

// ------------------------------------------------------------ downloads ---
function bytesToBase64(bytes) {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  return btoa(binary);
}

function waitForDownload(id, timeoutMs = 15000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { chrome.downloads.onChanged.removeListener(listener); reject(new Error('download timeout')); }, timeoutMs);
    function listener(delta) {
      if (delta.id !== id || !delta.state) return;
      if (delta.state.current === 'complete' || delta.state.current === 'interrupted') {
        clearTimeout(timer);
        chrome.downloads.onChanged.removeListener(listener);
        if (delta.state.current === 'complete') resolve(id);
        else reject(new Error(`download interrupted: ${delta.error?.current || 'unknown'}`));
      }
    }
    chrome.downloads.onChanged.addListener(listener);
    chrome.downloads.search({ id }).then(([item]) => {
      if (item?.state === 'complete') { clearTimeout(timer); chrome.downloads.onChanged.removeListener(listener); resolve(id); }
    });
  });
}

/** Writes a file inside the Downloads folder (relative path), overwriting it. */
async function writeFile(relativePath, mime, bytes) {
  const url = `data:${mime};base64,${bytesToBase64(bytes)}`;
  const id = await chrome.downloads.download({ url, filename: relativePath, conflictAction: 'overwrite', saveAs: false });
  await waitForDownload(id);
  return id;
}

const utf8 = (text) => new TextEncoder().encode(text);

function journalText(entries) {
  return entries.map((e) => JSON.stringify(e)).join('\n') + (entries.length ? '\n' : '');
}

async function writeJournal(folder, entries) {
  return writeFile(`${folder}/journal.jsonl`, 'application/x-ndjson', utf8(journalText(entries)));
}

// -------------------------------------------------------------- journal ---
async function appendEntry(state, fields, extra) {
  const c = activeCase(state);
  if (!ENTRY_TYPES.includes(fields.type)) throw new Error(`unknown entry type ${fields.type}`);
  const now = fields._date || new Date();
  const prev = c.entries.length ? c.entries[c.entries.length - 1].chain : GENESIS;
  const entry = {};
  for (const k of CHAINED_FIELDS) entry[k] = '';
  Object.assign(entry, {
    v: FORMAT_VERSION, seq: String(c.entries.length + 1), utc: formatUtc(now),
    examiner: c.meta.examiner || '', host: '', source: SOURCE,
  });
  for (const [k, v] of Object.entries(fields)) if (CHAINED_FIELDS.includes(k)) entry[k] = sanitize(v);
  entry.local = formatLocal(now);
  if (extra) entry.extra = extra;
  entry.chain = await nextChain(prev, entry);
  c.entries.push(entry);
  await saveState(state);
  await writeJournal(state.settings.activeFolder, c.entries);
  return entry;
}

async function newCase({ caseName, examiner }) {
  if (!caseName?.trim()) throw new Error('A case name is required.');
  const state = await loadState();
  const now = new Date();
  const folder = `ChainShot/${slugify(caseName)}-${fileStamp(now).slice(0, 15)}Z`;
  const meta = {
    format: 'chainshot-case', v: FORMAT_VERSION, case: caseName.trim(), examiner: (examiner || '').trim(),
    created_utc: formatUtc(now), tool: SOURCE,
  };
  state.cases[folder] = { meta, entries: [] };
  state.settings.activeFolder = folder;
  await saveState(state);
  await writeFile(`${folder}/chainshot-case.json`, 'application/json', utf8(JSON.stringify(meta, null, 2) + '\n'));
  await appendEntry(state, { type: 'note', tag: 'INIT', note: `Case created: ${meta.case}` });
  return status();
}

// -------------------------------------------------------------- capture ---
function fitText(ctx, text, maxWidth) {
  if (ctx.measureText(text).width <= maxWidth) return text;
  let lo = 0; let hi = text.length;
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2);
    if (ctx.measureText(`${text.slice(0, mid)}...`).width <= maxWidth) lo = mid; else hi = mid - 1;
  }
  return `${text.slice(0, lo)}...`;
}

/** Draws the banner BELOW the screenshot so that no captured pixel is hidden. */
async function addBanner(pngDataUrl, lines) {
  const shot = await createImageBitmap(await (await fetch(pngDataUrl)).blob());
  const lineHeight = 22;
  const bannerHeight = 12 + lineHeight * lines.length;
  const canvas = new OffscreenCanvas(shot.width, shot.height + bannerHeight);
  const ctx = canvas.getContext('2d');
  ctx.drawImage(shot, 0, 0);
  ctx.fillStyle = '#000';
  ctx.fillRect(0, shot.height, shot.width, bannerHeight);
  ctx.font = 'bold 13px Consolas, "DejaVu Sans Mono", monospace';
  ctx.textBaseline = 'top';
  lines.forEach((line, i) => {
    ctx.fillStyle = i === 0 ? '#ffd400' : '#dcdcdc';
    ctx.fillText(fitText(ctx, line, shot.width - 20), 10, shot.height + 8 + i * lineHeight);
  });
  const blob = await canvas.convertToBlob({ type: 'image/png' });
  return { bytes: new Uint8Array(await blob.arrayBuffer()), width: shot.width, height: shot.height };
}

async function captureTab({ note } = {}) {
  return serial(async () => {
    const state = await loadState();
    const c = activeCase(state);
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab) throw new Error('No active tab.');
    const now = new Date();
    const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, { format: 'png' });
    const tag = state.settings.tag || '';
    const text = (note ?? state.settings.nextNote) || '';
    const seq = c.entries.length + 1;
    const stamp = now.toISOString().replace('T', ' ').slice(0, 19);
    const lines = [
      `#${seq} | ${c.meta.case} | ${tag} | ${stamp} UTC | ${c.meta.examiner}`,
      `URL: ${tab.url || ''}`,
      `Title: ${tab.title || ''}`,
    ];
    if (text) lines.push(`Note: ${text}`);
    const { bytes, width, height } = await addBanner(dataUrl, lines);
    const sha256 = await sha256Hex(bytes);
    const safeTag = tag.replace(/[^\w-]/g, '_') || 'untagged';
    const file = `captures/${String(seq).padStart(4, '0')}_${fileStamp(now)}_${safeTag}.png`;
    await writeFile(`${state.settings.activeFolder}/${file}`, 'image/png', bytes);
    const entry = await appendEntry(state, {
      _date: now, type: 'browser', tag, file, size: String(bytes.length), sha256,
      url: tab.url || '', title: tab.title || '', note: text,
    }, { viewport: `${width}x${height}`, user_agent: navigator.userAgent });
    if (state.settings.nextNote) { state.settings.nextNote = ''; await saveState(state); }
    flashBadge('OK', '#1f7a4d');
    return entry;
  }).catch((err) => { flashBadge('ERR', '#b3261e'); throw err; });
}

function flashBadge(text, color) {
  chrome.action.setBadgeBackgroundColor({ color });
  chrome.action.setBadgeText({ text });
  setTimeout(() => chrome.action.setBadgeText({ text: '' }), 2500);
}

// ---------------------------------------------------------------- misc ---
async function status() {
  const state = await loadState();
  const c = state.cases[state.settings.activeFolder];
  const last = c?.entries[c.entries.length - 1];
  return {
    version: VERSION,
    settings: state.settings,
    case: c ? { ...c.meta, folder: state.settings.activeFolder, entries: c.entries.length } : null,
    last: last ? { seq: last.seq, type: last.type, file: last.file, sha256: last.sha256, utc: last.utc } : null,
    cases: Object.entries(state.cases).map(([folder, v]) => ({ folder, case: v.meta.case, entries: v.entries.length })),
  };
}

async function verify() {
  const state = await loadState();
  const c = activeCase(state);
  const res = await verifyEntries(c.entries);
  res.journalSha256 = await sha256Hex(utf8(journalText(c.entries)));
  return res;
}

const handlers = {
  status: () => status(),
  newCase: (m) => serial(() => newCase(m)),
  capture: (m) => captureTab(m),
  addNote: (m) => serial(async () => {
    if (!m.text?.trim()) throw new Error('Empty note.');
    const state = await loadState();
    return appendEntry(state, { type: 'note', tag: state.settings.tag, note: m.text });
  }),
  setSettings: async (m) => {
    const state = await loadState();
    for (const k of ['tag', 'nextNote']) if (k in m) state.settings[k] = String(m[k]);
    if (m.activeFolder && state.cases[m.activeFolder]) state.settings.activeFolder = m.activeFolder;
    await saveState(state);
    return status();
  },
  verify: () => verify(),
  rewriteJournal: () => serial(async () => {
    const state = await loadState();
    await writeJournal(state.settings.activeFolder, activeCase(state).entries);
    return status();
  }),
};

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  const h = handlers[msg?.cmd];
  if (!h) return false;
  Promise.resolve(h(msg)).then(
    (result) => sendResponse({ ok: true, result }),
    (err) => sendResponse({ ok: false, error: String(err?.message || err) }),
  );
  return true; // async response
});

chrome.commands.onCommand.addListener((command) => {
  if (command === 'capture-tab') captureTab().catch((e) => console.warn('ChainShot capture failed:', e));
});

// Exposed for automated tests (tests/extension.spec.mjs) through the service worker.
globalThis.chainshot = handlers;
