// SPDX-License-Identifier: MIT
// ChainShot journal format v1 - browser implementation.
// Must produce exactly the same canonical text, entry hashes and chain values
// as python/chainshot.py and the PowerShell module (see docs/journal-format.md
// and tests/vectors/canonical-vectors.json). Runs in the extension service
// worker and in Node 18+ (used by the tests).

export const FORMAT_VERSION = '1';
export const GENESIS = '0'.repeat(64);
export const CHAINED_FIELDS = [
  'v', 'seq', 'utc', 'type', 'tag', 'file', 'size', 'sha256',
  'examiner', 'host', 'source', 'url', 'title', 'note',
];

/** Values are strings; CR and LF are replaced by one space each. */
export function sanitize(value) {
  if (value === undefined || value === null) return '';
  return String(value).replace(/\r/g, ' ').replace(/\n/g, ' ');
}

/** Canonical text of an entry: one "key=value" line per chained field. */
export function canonicalString(entry) {
  return CHAINED_FIELDS.map((k) => `${k}=${sanitize(entry[k])}`).join('\n');
}

function toHex(buffer) {
  return Array.from(new Uint8Array(buffer), (b) => b.toString(16).padStart(2, '0')).join('');
}

/** SHA-256 of a string (UTF-8) or of bytes, as lowercase hex. */
export async function sha256Hex(data) {
  const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : data;
  return toHex(await crypto.subtle.digest('SHA-256', bytes));
}

export async function entryHash(entry) {
  return sha256Hex(canonicalString(entry));
}

/** chain(n) = SHA-256( chain(n-1) + entryHash(n) ). */
export async function nextChain(previousChain, entry) {
  return sha256Hex(previousChain + (await entryHash(entry)));
}

/** 2026-10-16T08:01:02.345Z (always milliseconds and Z). */
export function formatUtc(date) {
  return date.toISOString();
}

/** 20261016T080102345Z - used in file names. */
export function fileStamp(date) {
  return date.toISOString().replace(/[-:]/g, '').replace('.', '');
}

/** Local time with offset, e.g. 2026-10-16T10:01:02+02:00 (informative, not chained). */
export function formatLocal(date) {
  const pad = (n) => String(Math.trunc(Math.abs(n))).padStart(2, '0');
  const off = -date.getTimezoneOffset();
  const sign = off >= 0 ? '+' : '-';
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:` +
    `${pad(date.getMinutes())}:${pad(date.getSeconds())}${sign}${pad(off / 60)}:${pad(off % 60)}`;
}

/** Folder-safe slug of a case name. */
export function slugify(text) {
  const s = String(text).normalize('NFKD').replace(/[̀-ͯ]/g, '')
    .toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return s.slice(0, 48) || 'case';
}

/**
 * Checks seq numbers and the hash chain of a list of entries.
 * File hashes cannot be re-read from the browser: use chainshot.py verify for that.
 */
export async function verifyEntries(entries) {
  const problems = [];
  let prev = GENESIS;
  for (let i = 0; i < entries.length; i++) {
    const e = entries[i];
    if (e.v !== FORMAT_VERSION) problems.push(`#${i + 1}: unsupported format version`);
    if (e.seq !== String(i + 1)) problems.push(`#${i + 1}: sequence is ${e.seq}`);
    const expected = await nextChain(prev, e);
    if (e.chain !== expected) problems.push(`#${i + 1}: CHAIN BROKEN`);
    prev = e.chain;
  }
  return { ok: problems.length === 0, problems, count: entries.length, lastChain: prev };
}
