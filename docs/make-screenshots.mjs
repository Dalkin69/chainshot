// SPDX-License-Identifier: MIT
// Regenerates the illustrations of the README from a real run:
// the extension captures two neutral demo pages, chainshot.py seals the case and
// builds the HTML report, and Playwright screenshots the popup and the report.
//
//   CHAINSHOT_CHROMIUM=/path/to/chrome node docs/make-screenshots.mjs

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
const OUT = path.join(ROOT, 'docs', 'img');
const PYTHON = process.env.PYTHON || 'python3';
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'chainshot-shots-'));
const ext = path.join(tmp, 'ext');
const downloads = path.join(tmp, 'dl');
const config = path.join(tmp, 'config'); // state.json of chainshot.py, used as the browser link host
const appCase = path.join(tmp, 'DEMO-01');

// Neutral demo content (example.org style), no real person or organisation.
const PAGES = {
  '/docs/getting-started': ['Example Docs - Getting started', '#f4f1ea', '#1d1d1b',
    '<h1>Getting started</h1><p>This is a demo page used to illustrate a ChainShot web capture.</p>'],
  '/news/2026/10/demo': ['Example News - Demo article', '#1b1f27', '#e6e6e6',
    '<h1>Demo article</h1><p>Published 16/10/2026 - a second page for the sample case.</p>'],
};

fs.cpSync(path.join(ROOT, 'extension'), ext, { recursive: true });
const manifest = JSON.parse(fs.readFileSync(path.join(ext, 'manifest.json'), 'utf8'));
manifest.host_permissions = ['<all_urls>'];
fs.writeFileSync(path.join(ext, 'manifest.json'), JSON.stringify(manifest));
fs.mkdirSync(path.join(tmp, 'profile', 'Default'), { recursive: true });
fs.mkdirSync(downloads);
fs.mkdirSync(OUT, { recursive: true });
fs.writeFileSync(path.join(tmp, 'profile', 'Default', 'Preferences'), JSON.stringify({
  download: { default_directory: downloads, prompt_for_download: false, directory_upgrade: true },
}));

const py = (...args) => spawnSync(PYTHON, [path.join(ROOT, 'python', 'chainshot.py'), ...args],
  { encoding: 'utf8', env: { ...process.env, CHAINSHOT_CONFIG_DIR: config } });
py('register-browser', '--dir', path.join(tmp, 'profile', 'NativeMessagingHosts'));

const server = createServer((req, res) => {
  if (req.url === '/portal/activity') {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(fs.readFileSync(path.join(ROOT, 'docs', 'demo', 'activity.html')));
    return;
  }
  const [title, bg, fg, body] = PAGES[req.url] || ['Not found', '#fff', '#000', '<h1>404</h1>'];
  res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  res.end(`<!doctype html><title>${title}</title><body style="margin:48px;background:${bg};color:${fg};font:24px system-ui,sans-serif">${body}</body>`);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}`;

const context = await chromium.launchPersistentContext(path.join(tmp, 'profile'), {
  channel: 'chromium', headless: true, executablePath: process.env.CHAINSHOT_CHROMIUM || undefined,
  viewport: { width: 1100, height: 620 },
  env: { ...process.env, CHAINSHOT_CONFIG_DIR: config },
  args: [`--disable-extensions-except=${ext}`, `--load-extension=${ext}`],
});
const page = context.pages()[0] || await context.newPage();
await (await context.newCDPSession(page)).send('Browser.setDownloadBehavior', { behavior: 'default' });
const isOurs = (w) => w.url().endsWith('/background.js');
const sw = context.serviceWorkers().find(isOurs) || await context.waitForEvent('serviceworker', { predicate: isOurs });
for (let i = 0; i < 100 && !(await sw.evaluate(() => Boolean(globalThis.chainshot))); i++) await new Promise((r) => setTimeout(r, 50));

await sw.evaluate(() => globalThis.chainshot.newCase({ caseName: 'DEMO-2026-01 Sample case', examiner: 'Analyst' }));
let n = 0;
for (const url of Object.keys(PAGES)) {
  await page.goto(base + url);
  await sw.evaluate((tag) => globalThis.chainshot.setSettings({ tag }), `P0${++n}`);
  await sw.evaluate((note) => globalThis.chainshot.capture({ note }), `Demo capture ${n}`);
}

const id = new URL(sw.url()).host;
const popup = await context.newPage();
await popup.setViewportSize({ width: 390, height: 470 });
await popup.goto(`chrome-extension://${id}/popup.html`);
await popup.waitForFunction(() => document.getElementById('caseLine').textContent.includes('entries'));
await popup.screenshot({ path: path.join(OUT, 'extension-popup.png') });

// Browser link: the same extension saving into the case of the app (chainshot.py as host here).
py('init', appCase, '--case', 'DEMO-01 Workstation PC-042', '--examiner', 'Analyst');
py('use', appCase);
await sw.evaluate(() => globalThis.chainshot.setSettings({ destination: 'app', tag: 'P03' }));
await page.setViewportSize({ width: 1000, height: 560 });
await page.goto(`${base}/portal/activity`);
await page.bringToFront();
await sw.evaluate(() => globalThis.chainshot.capture({ note: 'Sign-in history of the user account' }));
await popup.bringToFront();
await popup.reload();
await popup.waitForFunction(() => document.getElementById('caseLine').textContent.includes('ChainShot app'));
await popup.screenshot({ path: path.join(OUT, 'extension-popup-app.png') });
const linked = fs.readdirSync(path.join(appCase, 'captures')).sort()[0];
fs.copyFileSync(path.join(appCase, 'captures', linked), path.join(OUT, 'sample-capture-app.png'));
await sw.evaluate(() => globalThis.chainshot.setSettings({ destination: 'downloads' }));

const caseDir = path.join(downloads, 'ChainShot', fs.readdirSync(path.join(downloads, 'ChainShot'))[0]);
py('seal', caseDir);
py('report', caseDir);
const report = await context.newPage();
await report.setViewportSize({ width: 1280, height: 900 });
await report.goto('file://' + path.join(caseDir, 'report', 'index.html'));
await report.screenshot({ path: path.join(OUT, 'html-report.png'), fullPage: true });

const firstCapture = fs.readdirSync(path.join(caseDir, 'captures')).sort()[0];
fs.copyFileSync(path.join(caseDir, 'captures', firstCapture), path.join(OUT, 'sample-capture.png'));

await context.close();
server.close();
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`Illustrations written to ${OUT}`);
