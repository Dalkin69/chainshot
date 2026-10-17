// SPDX-License-Identifier: MIT
// Builds the browser store images (Chrome Web Store, Edge Add-ons) from the README
// illustrations in docs/img: three 1280x800 screenshots and the 440x280 promo tile.
//
//   node docs/make-store-assets.mjs        (run docs/make-screenshots.mjs first)

import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const IMG = path.join(ROOT, 'docs', 'img');
const OUT = path.join(ROOT, 'docs', 'store');
fs.mkdirSync(OUT, { recursive: true });

const img = (name) => `data:image/png;base64,${fs.readFileSync(path.join(IMG, name)).toString('base64')}`;
const icon = `data:image/png;base64,${fs.readFileSync(path.join(ROOT, 'extension', 'icons', 'icon128.png')).toString('base64')}`;

const css = `
  *{box-sizing:border-box} body{margin:0;width:1280px;height:800px;overflow:hidden;font-family:"Segoe UI",system-ui,sans-serif;
  background:linear-gradient(135deg,#1f2a44 0%,#2b4c7e 100%);color:#fff}
  .wrap{display:flex;gap:40px;padding:56px 56px;height:100%;align-items:center}
  .text{width:360px;flex:none} h1{font-size:40px;line-height:1.15;margin:0 0 18px} p{font-size:19px;line-height:1.5;color:#d6deee;margin:0 0 14px}
  .gold{color:#e8b94a} ul{padding-left:20px;margin:0;font-size:18px;line-height:1.7;color:#d6deee}
  .shot{border-radius:10px;box-shadow:0 18px 50px rgba(0,0,0,.45);display:block;max-width:100%}
  .stack{position:relative;flex:1;height:100%} .stack .a{position:absolute;left:0;top:40px;width:720px}
  .stack .b{position:absolute;right:0;bottom:30px;width:300px;border:1px solid #e3e0d8}
  .brand{display:flex;align-items:center;gap:12px;margin-bottom:26px;font-weight:700;font-size:22px} .brand img{width:44px}
`;

const slides = [
  ['store-1-capture.png', `
    <div class="wrap"><div class="text"><div class="brand"><img src="${icon}">ChainShot</div>
      <h1>Web captures that <span class="gold">hold up</span> as evidence</h1>
      <p>One click captures the visible page with a banner below it: entry number, case, tag, UTC time, URL and title.</p>
      <p>Every capture is hashed (SHA-256) and chained to the previous one.</p></div>
    <div class="stack"><img class="shot a" src="${img('sample-capture-app.png')}"><img class="shot b" src="${img('extension-popup.png')}"></div></div>`],
  ['store-2-verify.png', `
    <div class="wrap"><div class="text"><div class="brand"><img src="${icon}">ChainShot</div>
      <h1>Anyone can <span class="gold">verify</span> the case</h1>
      <ul><li>append-only journal, hash chain</li><li>seals with RFC 3161 timestamps</li><li>HTML report with thumbnails</li>
      <li>independent verifier: one Python file</li></ul></div>
    <div class="stack"><img class="shot" style="width:820px;height:640px;object-fit:cover;object-position:top" src="${img('html-report.png')}"></div></div>`],
  ['store-3-app.png', `
    <div class="wrap"><div class="text"><div class="brand"><img src="${icon}">ChainShot</div>
      <h1>Straight into your <span class="gold">ChainShot case</span></h1>
      <p>With the free ChainShot app for Windows (or chainshot.py), web captures join the screen captures, files and notes of the same case.</p>
      <p>No account, no telemetry, open source (MIT).</p></div>
    <div class="stack"><img class="shot a" style="width:760px" src="${img('app-window.png')}"><img class="shot b" src="${img('extension-popup-app.png')}"></div></div>`],
];

const browser = await chromium.launch({ executablePath: process.env.CHAINSHOT_CHROMIUM || undefined });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
for (const [name, body] of slides) {
  await page.setContent(`<!doctype html><html><head><style>${css}</style></head><body>${body}</body></html>`);
  await page.screenshot({ path: path.join(OUT, name) });
}
await page.setViewportSize({ width: 440, height: 280 });
await page.setContent(`<!doctype html><html><head><style>${css}
  body{width:440px;height:280px} .tile{display:flex;flex-direction:column;justify-content:center;height:100%;padding:28px 32px}
  .tile h1{font-size:34px;margin:12px 0 6px} .tile p{font-size:16px;margin:0}</style></head>
  <body><div class="tile"><img src="${icon}" width="64"><h1>ChainShot</h1><p>Timestamped, hash-chained web captures for forensic reports</p></div></body></html>`);
await page.screenshot({ path: path.join(OUT, 'promo-small-440x280.png') });
await browser.close();
console.log(`Store images written to ${OUT}`);
