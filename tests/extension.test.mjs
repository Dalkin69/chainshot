// SPDX-License-Identifier: MIT
// End-to-end test of the browser extension:
//   real Chromium + the unpacked extension -> capture a page -> files written to
//   disk by the downloads API -> verified by the Python reference implementation.
//
//   npm run test:extension
// Env: CHAINSHOT_CHROMIUM = path to a Chromium binary (optional), PYTHON (default python3).

import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PYTHON = process.env.PYTHON || 'python3';
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'chainshot-ext-'));
const extDir = path.join(tmp, 'extension');
const profile = path.join(tmp, 'profile');
const downloads = path.join(tmp, 'downloads');
const config = path.join(tmp, 'config'); // CHAINSHOT_CONFIG_DIR of the native host (state.json)
const appCase = path.join(tmp, 'app-case');
let context; let server; let sw; let pageUrl;

function py(...args) {
  const r = spawnSync(PYTHON, [path.join(ROOT, 'python', 'chainshot.py'), ...args], {
    encoding: 'utf8', env: { ...process.env, CHAINSHOT_CONFIG_DIR: config },
  });
  assert.equal(r.status, 0, (r.stdout || '') + (r.stderr || ''));
  return r.stdout;
}

function pyVerify(dir) {
  const r = spawnSync(PYTHON, [path.join(ROOT, 'python', 'chainshot.py'), 'verify', dir], { encoding: 'utf8' });
  return { code: r.status, out: (r.stdout || '') + (r.stderr || '') };
}

function pngSize(file) {
  const b = fs.readFileSync(file);
  return { width: b.readUInt32BE(16), height: b.readUInt32BE(20) };
}

before(async () => {
  // Test copy of the extension: <all_urls> lets the test trigger captures without a
  // user gesture (in normal use, activeTab is granted by the popup or the shortcut).
  fs.cpSync(path.join(ROOT, 'extension'), extDir, { recursive: true });
  const manifest = JSON.parse(fs.readFileSync(path.join(extDir, 'manifest.json'), 'utf8'));
  manifest.host_permissions = ['<all_urls>'];
  manifest.permissions.push('pageCapture'); // optional in the real manifest (asked in the popup)
  fs.writeFileSync(path.join(extDir, 'manifest.json'), JSON.stringify(manifest, null, 2));

  fs.mkdirSync(path.join(profile, 'Default'), { recursive: true });
  fs.mkdirSync(downloads, { recursive: true });
  fs.writeFileSync(path.join(profile, 'Default', 'Preferences'), JSON.stringify({
    download: { default_directory: downloads, prompt_for_download: false, directory_upgrade: true },
    savefile: { default_directory: downloads },
  }));

  // Browser link: chainshot.py is the native messaging host (ChainShot.exe on Windows).
  // Chromium reads user-level host manifests from <profile>/NativeMessagingHosts.
  py('register-browser', '--dir', path.join(profile, 'NativeMessagingHosts'));

  server = createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    if (req.url === '/long') {
      // 3000 px tall, with a fixed header that must appear only once in a full page capture.
      const bands = Array.from({ length: 10 }, (_, i) =>
        `<div style="height:300px;background:hsl(${i * 36},70%,50%);font:40px sans-serif">Section ${i + 1}</div>`).join('');
      res.end('<!doctype html><title>Long page</title><body style="margin:0">' +
        '<div style="position:fixed;top:0;left:0;right:0;height:40px;background:#000;color:#fff">Fixed header</div>' + bands + '</body>');
      return;
    }
    res.end('<!doctype html><title>Evidence page é</title><body style="margin:0;background:#2b4c7e;color:#fff;font:40px sans-serif">' +
      '<h1>Suspicious web page</h1></body>');
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  pageUrl = `http://127.0.0.1:${server.address().port}/evidence`;

  context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium',
    headless: true,
    acceptDownloads: true,
    executablePath: process.env.CHAINSHOT_CHROMIUM || undefined,
    viewport: { width: 1000, height: 700 },
    env: { ...process.env, CHAINSHOT_CONFIG_DIR: config },
    args: [`--disable-extensions-except=${extDir}`, `--load-extension=${extDir}`],
  });
  // Give downloads back to the browser and its profile preferences: Playwright would
  // otherwise save them under generated names, which ignores the case folder layout.
  const page = context.pages()[0] || await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Browser.setDownloadBehavior', { behavior: 'default' });
  await page.goto(pageUrl);
  const isOurs = (w) => w.url().endsWith('/background.js');
  sw = context.serviceWorkers().find(isOurs) || await context.waitForEvent('serviceworker', { predicate: isOurs });
  for (let i = 0; i < 100 && !(await sw.evaluate(() => Boolean(globalThis.chainshot))); i++) {
    await new Promise((r) => setTimeout(r, 50)); // module still initialising
  }
});

after(async () => {
  await context?.close();
  server?.close();
  fs.rmSync(tmp, { recursive: true, force: true });
});

function caseDir() {
  const base = path.join(downloads, 'ChainShot');
  const [name] = fs.readdirSync(base);
  return path.join(base, name);
}

test('captures a page into a case folder that chainshot.py verifies', async () => {
  const st = await sw.evaluate(() => globalThis.chainshot.newCase({ caseName: 'E2E Test é', examiner: 'Bot' }));
  assert.equal(st.case.entries, 1);
  await sw.evaluate(() => globalThis.chainshot.setSettings({ tag: 'P22' }));
  const entry = await sw.evaluate(() => globalThis.chainshot.capture({ note: 'line1\nline2' }));
  assert.equal(entry.type, 'browser');
  assert.equal(entry.tag, 'P22');
  assert.equal(entry.url, pageUrl);
  assert.equal(entry.title, 'Evidence page é');
  assert.equal(entry.note, 'line1 line2');
  await sw.evaluate(() => globalThis.chainshot.addNote({ text: 'observation after capture' }));

  const dir = caseDir();
  const journal = fs.readFileSync(path.join(dir, 'journal.jsonl'), 'utf8').trim().split('\n');
  assert.equal(journal.length, 3);
  assert.ok(fs.existsSync(path.join(dir, 'chainshot-case.json')));
  const png = path.join(dir, entry.file);
  // The extension records the size of the raw tab capture; the PNG must have the same
  // width and be taller (banner added below). The headless window size varies, so the
  // test does not assume a fixed viewport.
  const { width, height } = pngSize(png);
  const [shotW, shotH] = entry.extra.viewport.split('x').map(Number);
  assert.equal(width, shotW);
  assert.ok(height > shotH, `banner is added below the page (capture ${shotW}x${shotH}, PNG ${width}x${height})`);

  const ok = pyVerify(dir);
  assert.equal(ok.code, 0, ok.out);
  assert.match(ok.out, /ALL EVIDENCE INTACT/);
});

test('the in-browser chain check passes', async () => {
  const r = await sw.evaluate(() => globalThis.chainshot.verify());
  assert.equal(r.ok, true, JSON.stringify(r.problems));
  assert.equal(r.count, 3);
});

test('a modified capture is detected by chainshot.py', async () => {
  const dir = caseDir();
  const capture = fs.readdirSync(path.join(dir, 'captures'))[0];
  fs.appendFileSync(path.join(dir, 'captures', capture), Buffer.from([0]));
  const bad = pyVerify(dir);
  assert.equal(bad.code, 1);
  assert.match(bad.out, /FILE HASH MISMATCH/);
});

test('the popup renders the active case', async () => {
  const id = new URL(sw.url()).host;
  const popup = await context.newPage();
  await popup.goto(`chrome-extension://${id}/popup.html`);
  await popup.waitForFunction(() => document.getElementById('caseLine').textContent.includes('entries'));
  assert.match(await popup.textContent('#caseLine'), /E2E Test é - 3 entries/);
  await popup.click('#verify');
  await popup.waitForFunction(() => document.getElementById('message').textContent.length > 0);
  assert.match(await popup.textContent('#message'), /Chain intact \(3 entries\)/);
  await popup.close();
});

test('app destination: reports when no case is selected', async () => {
  const st = await sw.evaluate(() => globalThis.chainshot.setSettings({ destination: 'app' }));
  assert.equal(st.app.connected, true, JSON.stringify(st.app));
  assert.equal(st.app.case, null);
  await assert.rejects(sw.evaluate(() => globalThis.chainshot.capture({})), /No case|no current case/i);
});

test('app destination: captures go into the current case of the app', async () => {
  py('init', appCase, '--case', 'APP-LINK', '--examiner', 'Analyst');
  py('use', appCase);
  let st = await sw.evaluate(() => globalThis.chainshot.setSettings({ tag: 'A1' }));
  assert.equal(st.app.case.name, 'APP-LINK');
  assert.equal(st.app.tag, 'A1', 'the tag is sent to the app');
  const entry = await sw.evaluate(() => globalThis.chainshot.capture({ note: 'linked capture' }));
  assert.equal(entry.seq, '2');
  assert.equal(entry.destination, 'app');
  py('note', appCase, 'note written by another tool between two captures');
  const second = await sw.evaluate(() => globalThis.chainshot.capture({}));
  assert.equal(second.seq, '4', 'numbering follows the journal of the app');
  await sw.evaluate(() => globalThis.chainshot.addNote({ text: 'observation from the browser' }));

  const lines = fs.readFileSync(path.join(appCase, 'journal.jsonl'), 'utf8').trim().split('\n').map((l) => JSON.parse(l));
  assert.equal(lines.length, 5);
  const e = lines[1];
  assert.equal(e.type, 'browser');
  assert.equal(e.tag, 'A1');
  assert.equal(e.url, pageUrl);
  assert.equal(e.title, 'Evidence page é');
  assert.equal(e.note, 'linked capture');
  assert.match(e.source, /^chainshot-ext\//);
  assert.match(e.extra.via, /^chainshot-py\//);
  assert.match(e.file, /^captures\/0002_\d{8}T\d{9}Z_A1\.png$/);
  assert.equal(lines[4].tag, 'A1');
  const { width, height } = pngSize(path.join(appCase, e.file));
  const [shotW, shotH] = e.extra.viewport.split('x').map(Number);
  assert.equal(width, shotW);
  assert.ok(height > shotH, 'banner below the page');

  const r = await sw.evaluate(() => globalThis.chainshot.verify());
  assert.equal(r.ok, true, JSON.stringify(r.problems));
  assert.equal(r.count, 5);
  const ok = pyVerify(appCase);
  assert.equal(ok.code, 0, ok.out);

  const id = new URL(sw.url()).host;
  assert.equal(id, 'dcccfnbiglhpbcdhbmokfilfgpfnmikf', 'the manifest key fixes the extension ID');
  const popup = await context.newPage();
  await popup.goto(`chrome-extension://${id}/popup.html`);
  await popup.waitForFunction(() => document.getElementById('caseLine').textContent.includes('ChainShot app'));
  assert.match(await popup.textContent('#caseLine'), /APP-LINK - 5 entries \(ChainShot app\)/);
  assert.equal(await popup.getAttribute('#destApp', 'aria-pressed'), 'true');
  await popup.close();
  st = await sw.evaluate(() => globalThis.chainshot.setSettings({ destination: 'downloads' }));
  assert.equal(st.app, null);
});

test('full page capture: scrolls, stitches and records the page size', async () => {
  const page = context.pages()[0];
  await page.goto(pageUrl.replace('/evidence', '/long'));
  await page.bringToFront();
  const entry = await sw.evaluate(() => globalThis.chainshot.capture({ fullPage: true, note: 'whole page' }));
  assert.equal(entry.extra.mode, 'fullpage');
  assert.ok(Number(entry.extra.parts) >= 2, `several parts (${entry.extra.parts})`);
  const dir = caseDir();
  const { width, height } = pngSize(path.join(dir, entry.file));
  if (process.env.CHAINSHOT_KEEP) fs.copyFileSync(path.join(dir, entry.file), path.join(process.env.CHAINSHOT_KEEP, 'fullpage.png'));
  const [, cssHeight] = entry.extra.page.split('x').map(Number);
  assert.equal(cssHeight, 3000);
  const [, shotH] = entry.extra.viewport.split('x').map(Number);
  assert.ok(shotH >= 3000 * (width / Number(entry.extra.page.split('x')[0])) - 2, `stitched height ${shotH} for ${entry.extra.page}`);
  assert.ok(height > shotH, 'banner below');
  const scrollY = await page.evaluate(() => window.scrollY);
  assert.equal(scrollY, 0, 'scroll position restored');
  const header = await page.evaluate(() => getComputedStyle(document.querySelector('div')).visibility);
  assert.equal(header, 'visible', 'fixed elements restored');
});

test('page archive (MHTML) is saved and chained after the capture', async () => {
  const page = context.pages()[0];
  await page.goto(pageUrl);
  await page.bringToFront();
  const entry = await sw.evaluate(() => globalThis.chainshot.capture({ archive: true }));
  assert.ok(entry.archive, 'archive entry');
  assert.equal(Number(entry.archive.seq), Number(entry.seq) + 1);
  const dir = caseDir();
  const mhtml = fs.readFileSync(path.join(dir, entry.archive.file), 'latin1');
  assert.match(entry.archive.file, /\.mhtml$/);
  assert.match(mhtml, /MIME-Version/);
  assert.match(mhtml, /Snapshot-Content-Location: http:\/\/127\.0\.0\.1:\d+\/evidence/);
  // The journal stays clean (no helper field) and still verifies, except the capture altered earlier.
  const lines = fs.readFileSync(path.join(dir, 'journal.jsonl'), 'utf8').trim().split('\n').map((l) => JSON.parse(l));
  assert.ok(lines.every((l) => !('archive' in l)));
  const r = await sw.evaluate(() => globalThis.chainshot.verify());
  assert.equal(r.ok, true, JSON.stringify(r.problems));
});

test('app destination: full page and archive go to the app case', async () => {
  const page = context.pages()[0];
  await page.goto(pageUrl.replace('/evidence', '/long'));
  await page.bringToFront();
  await sw.evaluate(() => globalThis.chainshot.setSettings({ destination: 'app' }));
  const entry = await sw.evaluate(() => globalThis.chainshot.capture({ fullPage: true, archive: true }));
  assert.equal(entry.extra.mode, 'fullpage');
  assert.ok(entry.archive && entry.archive.file.endsWith('.mhtml'), JSON.stringify(entry));
  const lines = fs.readFileSync(path.join(appCase, 'journal.jsonl'), 'utf8').trim().split('\n').map((l) => JSON.parse(l));
  const last = lines[lines.length - 1];
  assert.equal(last.file, entry.archive.file);
  assert.equal(lines[lines.length - 2].extra.mode, 'fullpage');
  const ok = pyVerify(appCase);
  assert.equal(ok.code, 0, ok.out);
  await sw.evaluate(() => globalThis.chainshot.setSettings({ destination: 'downloads' }));
});
