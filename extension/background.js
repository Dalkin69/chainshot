// SPDX-License-Identifier: MIT
// ChainShot browser extension - service worker.
//
// A case is a folder under the browser's download directory:
//   <Downloads>/ChainShot/<case-slug>-<timestamp>/
//       chainshot-case.json, journal.jsonl, captures/NNNN_<UTC>_<tag>.png
// which is a regular ChainShot case: `python chainshot.py verify <folder>` checks it.
// The journal of record is kept in chrome.storage.local and rewritten to disk
// after every entry (downloads API, conflictAction "overwrite").
//
// Destination "app": captures go instead into the case open in ChainShot.exe (or the one
// selected with `chainshot.py use`), through Chrome native messaging. The banner is drawn
// here; the app hashes, names and chains the file. Protocol: docs/browser-link.md.

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
  return { settings: { tag: '', nextNote: '', activeFolder: '', destination: 'downloads', ...settings }, cases };
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
  // Firefox's background page can make blob: URLs (and refuses data: downloads);
  // Chrome's service worker cannot, so it uses a data: URL.
  const blobUrl = typeof URL.createObjectURL === 'function' ? URL.createObjectURL(new Blob([bytes], { type: mime })) : null;
  const url = blobUrl || `data:${mime};base64,${bytesToBase64(bytes)}`;
  let id;
  try {
    id = await chrome.downloads.download({ url, filename: relativePath, conflictAction: 'overwrite', saveAs: false });
    await waitForDownload(id);
  } finally {
    if (blobUrl) setTimeout(() => URL.revokeObjectURL(blobUrl), 10000);
  }
  // Remove the entry from the browser's download list (the file stays on disk):
  // the journal is rewritten after every entry and would flood the list.
  await chrome.downloads.erase({ id }).catch(() => {});
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
async function addBanner(source, lines) {
  const input = typeof source === 'string' ? await (await fetch(source)).blob() : source;
  const shot = await createImageBitmap(input);
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

// ------------------------------------------------------ full page and archive ---
const MAX_PAGE_PX = 30000; // device pixels: canvas limits and file size
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/* Functions injected in the page (chrome.scripting): they must be self-contained. */
function pageMetrics() {
  const d = document.documentElement;
  return {
    width: window.innerWidth, height: window.innerHeight, dpr: window.devicePixelRatio || 1,
    scrollHeight: Math.max(d.scrollHeight, document.body ? document.body.scrollHeight : 0),
    scrollX: window.scrollX, scrollY: window.scrollY,
  };
}

function pageScroll(y, hideFixed) {
  const d = document.documentElement;
  if (!window.__chainshot) {
    window.__chainshot = { behavior: d.style.scrollBehavior, hidden: [] };
    d.style.scrollBehavior = 'auto';
  }
  if (hideFixed && window.__chainshot.hidden.length === 0) {
    // Headers and banners that follow the scroll would otherwise appear in every part.
    for (const el of document.querySelectorAll('body *')) {
      const pos = getComputedStyle(el).position;
      if (pos === 'fixed' || pos === 'sticky') {
        window.__chainshot.hidden.push([el, el.style.getPropertyValue('visibility'), el.style.getPropertyPriority('visibility')]);
        el.style.setProperty('visibility', 'hidden', 'important');
      }
    }
    window.__chainshot.hidden.push([null]);
  }
  window.scrollTo(0, y);
  return window.scrollY;
}

function pageRestore(x, y) {
  const st = window.__chainshot;
  if (st) {
    for (const [el, value, prio] of st.hidden) {
      if (el) { if (value) el.style.setProperty('visibility', value, prio); else el.style.removeProperty('visibility'); }
    }
    document.documentElement.style.scrollBehavior = st.behavior;
    delete window.__chainshot;
  }
  window.scrollTo(x, y);
}

async function inPage(tab, func, args = []) {
  const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func, args });
  return res?.result;
}

/**
 * Captures the tab: the visible area, or the whole page by scrolling and stitching parts.
 * Returns { blob, mode, parts, pageSize, truncated }.
 */
async function grabTab(tab, fullPage) {
  if (!fullPage) {
    const dataUrl = await captureVisible(tab.windowId);
    return { blob: await (await fetch(dataUrl)).blob(), mode: 'visible', parts: 1 };
  }
  let m;
  try {
    m = await inPage(tab, pageMetrics);
  } catch (err) {
    throw new Error(`Full page capture is not possible on this page (${err?.message || err}). Use the visible area.`);
  }
  const limitCss = Math.floor(MAX_PAGE_PX / m.dpr);
  const total = Math.min(m.scrollHeight, limitCss);
  if (total <= m.height + 1) {
    const dataUrl = await captureVisible(tab.windowId);
    return { blob: await (await fetch(dataUrl)).blob(), mode: 'fullpage', parts: 1, pageSize: `${m.width}x${m.scrollHeight}` };
  }
  const positions = [];
  for (let y = 0; y < total; y += m.height) positions.push(Math.min(y, total - m.height));
  const shots = [];
  try {
    for (let i = 0; i < positions.length; i++) {
      const actual = await inPage(tab, pageScroll, [positions[i], i > 0]);
      await sleep(i === 0 ? 150 : 550); // let the page paint; Chrome allows two captures per second
      const bitmap = await createImageBitmap(await (await fetch(await captureVisible(tab.windowId))).blob());
      shots.push({ y: actual, bitmap });
    }
  } finally {
    await inPage(tab, pageRestore, [m.scrollX, m.scrollY]).catch(() => {});
  }
  const scale = shots[0].bitmap.width / m.width; // device pixels per CSS pixel, as captured
  const canvas = new OffscreenCanvas(shots[0].bitmap.width, Math.round(total * scale));
  const ctx = canvas.getContext('2d');
  for (const s of shots) ctx.drawImage(s.bitmap, 0, Math.round(s.y * scale));
  return {
    blob: await canvas.convertToBlob({ type: 'image/png' }), mode: 'fullpage', parts: shots.length,
    pageSize: `${m.width}x${m.scrollHeight}`, truncated: m.scrollHeight > total ? total : 0,
  };
}

function captureLine(g) {
  if (g.mode !== 'fullpage') return '';
  let line = `Capture: full page ${g.pageSize} CSS px, ${g.parts} part${g.parts > 1 ? 's' : ''}`;
  if (g.truncated) line += `, cut at ${g.truncated} px`;
  return line;
}

const mhtmlAvailable = () => (chrome.runtime.getManifest().optional_permissions || []).includes('pageCapture');

/** MHTML archive of the page (Chromium; needs the optional pageCapture permission). */
async function pageArchive(tab) {
  if (!chrome.pageCapture) throw new Error('Page archive: allow it in the popup first (MHTML option).');
  // The blob is backed by a temporary file that Chrome sometimes removes before it is read
  // (NotFoundError): save the page again in that case.
  for (let attempt = 0; ; attempt++) {
    try {
      const blob = await chrome.pageCapture.saveAsMHTML({ tabId: tab.id });
      return new Uint8Array(await blob.arrayBuffer());
    } catch (err) {
      if (attempt >= 4 || err?.name !== 'NotFoundError') throw err;
      await sleep(200);
    }
  }
}

// ------------------------------------------------- link with the ChainShot app ---
const HOST = 'com.chainshot.host';

async function hostCall(message) {
  let reply;
  try {
    reply = await chrome.runtime.sendNativeMessage(HOST, { protocol: 1, ...message });
  } catch (err) {
    throw new Error('ChainShot app not reachable. Start ChainShot.exe once (Windows) or run '
      + '"chainshot.py register-browser", then try again. Details: ' + (err?.message || err));
  }
  if (!reply) throw new Error('No answer from the ChainShot app.');
  return reply;
}

async function appStatus() {
  try {
    const r = await hostCall({ type: 'hello' });
    return { connected: true, tool: r.tool, host: r.host, tag: r.tag || '', case: r.case, warning: r.warning || '' };
  } catch (err) {
    return { connected: false, error: String(err?.message || err) };
  }
}

function bannerLines({ seq, shotNo, caseName, tag, now, examiner, host, tab, text, extraLine }) {
  const stamp = now.toISOString().replace('T', ' ').slice(0, 19);
  const lines = [
    `#${seq} (capture ${shotNo}) | ${caseName} | ${tag} | ${stamp} UTC | ${examiner}${host ? ` @ ${host}` : ''}`,
    `URL: ${tab.url || ''}`,
    `Title: ${tab.title || ''}`,
  ];
  if (extraLine) lines.push(extraLine);
  if (text) lines.push(`Note: ${text}`);
  return lines;
}

async function captureToApp(state, tab, grab, now, text) {
  let info = await hostCall({ type: 'hello' });
  if (!info.case) throw new Error(info.warning || 'No case is open in ChainShot.');
  const tag = info.tag || ''; // the tag of the ChainShot app, shared with its window and the module
  for (let attempt = 0; attempt < 3; attempt++) {
    const c = info.case;
    const lines = bannerLines({
      seq: Number(c.next_seq), shotNo: Number(c.next_capture), caseName: c.name, tag, now,
      examiner: c.examiner, host: info.host, tab, text, extraLine: captureLine(grab),
    });
    const { bytes, width, height } = await addBanner(grab.blob, lines);
    const reply = await hostCall({
      type: 'capture', png_base64: bytesToBase64(bytes), utc: formatUtc(now), expected_seq: Number(c.next_seq),
      url: tab.url || '', title: tab.title || '', tag, note: text, source: SOURCE,
      extra: { viewport: `${width}x${height}`, user_agent: navigator.userAgent, mode: grab.mode },
    });
    if (reply.ok) {
      return { ...reply.entry, type: 'browser', tag, url: tab.url || '', title: tab.title || '', note: sanitize(text),
        extra: { viewport: `${width}x${height}`, mode: grab.mode }, destination: 'app', case: c.name };
    }
    if (!reply.retry) throw new Error(reply.error || 'The ChainShot app refused the capture.');
    info = reply; // another tool added an entry meanwhile: redraw the banner with the new number
  }
  throw new Error('The journal keeps changing; capture again.');
}

async function captureTab({ note, fullPage, archive } = {}) {
  return serial(async () => {
    const state = await loadState();
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab) throw new Error('No active tab.');
    const full = fullPage ?? state.settings.fullPage === true;
    const withArchive = (archive ?? state.settings.archive === true) && mhtmlAvailable();
    const text = (note ?? state.settings.nextNote) || '';
    const now = new Date();
    const grab = await grabTab(tab, full);
    const mhtml = withArchive ? await pageArchive(tab) : null;
    let entry;
    if (state.settings.destination === 'app') {
      entry = await captureToApp(state, tab, grab, now, text);
      if (mhtml) {
        const r = await hostCall({
          type: 'capture', format: 'mhtml', data_base64: bytesToBase64(mhtml), utc: formatUtc(now),
          url: tab.url || '', title: tab.title || '', tag: entry.tag, note: `Page archive (MHTML) of #${entry.seq}`, source: SOURCE,
          extra: { mode: 'mhtml' },
        });
        if (!r.ok) throw new Error(`The capture is saved (#${entry.seq}) but not the page archive: ${r.error}`);
        entry.archive = r.entry;
      }
    } else {
      const c = activeCase(state);
      const tag = state.settings.tag || '';
      const seq = c.entries.length + 1; // journal entry number (entry 1 is the case creation note)
      const shotNo = c.entries.filter((e) => e.type === 'screenshot' || e.type === 'browser').length + 1;
      const lines = bannerLines({ seq, shotNo, caseName: c.meta.case, tag, now, examiner: c.meta.examiner, host: '', tab, text, extraLine: captureLine(grab) });
      const { bytes, width, height } = await addBanner(grab.blob, lines);
      const safeTag = tag.replace(/[^\w-]/g, '_') || 'untagged';
      const file = `captures/${String(seq).padStart(4, '0')}_${fileStamp(now)}_${safeTag}.png`;
      await writeFile(`${state.settings.activeFolder}/${file}`, 'image/png', bytes);
      const extra = { viewport: `${width}x${height}`, user_agent: navigator.userAgent, mode: grab.mode };
      if (grab.mode === 'fullpage') Object.assign(extra, { parts: String(grab.parts), page: grab.pageSize || '', truncated: String(grab.truncated || '') });
      // Copies: the returned objects must not alias the stored journal entries.
      entry = { ...await appendEntry(state, {
        _date: now, type: 'browser', tag, file, size: String(bytes.length), sha256: await sha256Hex(bytes),
        url: tab.url || '', title: tab.title || '', note: text,
      }, extra) };
      if (mhtml) {
        const aFile = `captures/${String(seq + 1).padStart(4, '0')}_${fileStamp(now)}_${safeTag}.mhtml`;
        await writeFile(`${state.settings.activeFolder}/${aFile}`, 'application/octet-stream', mhtml);
        entry.archive = { ...await appendEntry(state, {
          _date: now, type: 'browser', tag, file: aFile, size: String(mhtml.length), sha256: await sha256Hex(mhtml),
          url: tab.url || '', title: tab.title || '', note: `Page archive (MHTML) of #${seq}`,
        }, { mode: 'mhtml' }) };
      }
    }
    if (state.settings.nextNote) { state.settings.nextNote = ''; await saveState(state); }
    flashBadge('OK', '#1f7a4d');
    return entry;
  }).catch((err) => { flashBadge('ERR', '#b3261e'); throw err; });
}

/** Chrome allows two captures per second: wait and retry instead of failing. */
async function captureVisible(windowId) {
  for (let attempt = 0; ; attempt++) {
    try {
      return await chrome.tabs.captureVisibleTab(windowId, { format: 'png' });
    } catch (err) {
      if (attempt >= 4 || !/MAX_CAPTURE_VISIBLE_TAB_CALLS_PER_SECOND/.test(String(err?.message || err))) throw err;
      await new Promise((r) => setTimeout(r, 600));
    }
  }
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
  const app = state.settings.destination === 'app' ? await appStatus() : null;
  if (app?.connected) state.settings.tag = app.tag; // the app owns the tag in this mode
  return {
    version: VERSION,
    mhtml: mhtmlAvailable(),
    app,
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
    if (state.settings.destination === 'app') {
      const r = await hostCall({ type: 'note', text: m.text, source: SOURCE }); // the app applies its tag
      if (!r.ok) throw new Error(r.error);
      return { ...r.entry, type: 'note', destination: 'app' };
    }
    return appendEntry(state, { type: 'note', tag: state.settings.tag, note: m.text });
  }),
  setSettings: async (m) => {
    const state = await loadState();
    for (const k of ['tag', 'nextNote']) if (k in m) state.settings[k] = String(m[k]);
    for (const k of ['fullPage', 'archive']) if (k in m) state.settings[k] = Boolean(m[k]);
    if (m.destination === 'app' || m.destination === 'downloads') state.settings.destination = m.destination;
    if (m.activeFolder && state.cases[m.activeFolder]) state.settings.activeFolder = m.activeFolder;
    await saveState(state);
    if ('tag' in m && state.settings.destination === 'app') {
      await hostCall({ type: 'set_tag', tag: state.settings.tag }).catch(() => {});
    }
    return status();
  },
  verify: async () => {
    const state = await loadState();
    if (state.settings.destination !== 'app') return verify();
    const r = await hostCall({ type: 'verify' });
    if (!r.ok) throw new Error(r.error);
    return { ok: r.intact, count: Number(r.entries), problems: r.problems || [], destination: 'app' };
  },
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
