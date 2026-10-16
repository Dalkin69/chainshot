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
let context; let server; let sw; let pageUrl;

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
  fs.writeFileSync(path.join(extDir, 'manifest.json'), JSON.stringify(manifest, null, 2));

  fs.mkdirSync(path.join(profile, 'Default'), { recursive: true });
  fs.mkdirSync(downloads, { recursive: true });
  fs.writeFileSync(path.join(profile, 'Default', 'Preferences'), JSON.stringify({
    download: { default_directory: downloads, prompt_for_download: false, directory_upgrade: true },
    savefile: { default_directory: downloads },
  }));

  server = createServer((_req, res) => {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
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
