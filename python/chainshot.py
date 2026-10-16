#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
ChainShot - tamper-evident evidence journal for forensic screenshots and files.

This single file is the reference implementation of the ChainShot journal
format (see docs/journal-format.md). It only uses the Python standard library
so that an auditor can copy it anywhere and verify a case folder without
installing anything or trusting the tool that produced the evidence.

Commands
--------
  init        create a case folder
  add         hash a file and append it to the journal
  note        append a text-only entry (an action, an observation)
  verify      check every file hash, the hash chain and the seals
  seal        seal the journal (optionally with an RFC 3161 timestamp)
  report      generate a self-contained HTML report
  export-csv  export the journal as CSV (read-only view, never re-imported)
  tsr-info    print the content of an RFC 3161 timestamp response

Exit codes: 0 = everything verified, 1 = verification problem, 2 = usage or I/O error.
"""

from __future__ import annotations

import argparse
import csv
import datetime as _dt
import getpass
import hashlib
import html
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import urllib.request
from pathlib import Path

__version__ = "1.0.0"
TOOL_ID = f"chainshot-py/{__version__}"

# --------------------------------------------------------------------------- #
# Journal format v1 (keep in sync with docs/journal-format.md, the PowerShell
# module and the browser extension: all three must produce identical hashes).
# --------------------------------------------------------------------------- #
FORMAT_VERSION = "1"
JOURNAL_NAME = "journal.jsonl"
CASE_FILE = "chainshot-case.json"
GENESIS = "0" * 64

# Fields protected by the hash chain, in canonical order.
CHAINED_FIELDS = (
    "v", "seq", "utc", "type", "tag", "file", "size", "sha256",
    "examiner", "host", "source", "url", "title", "note",
)
ENTRY_TYPES = ("screenshot", "browser", "file", "note", "seal", "timestamp")

DEFAULT_TSA = "https://freetsa.org/tsr"


def sanitize(value) -> str:
    """Values are strings; CR and LF are replaced by spaces (one per character)."""
    if value is None:
        return ""
    return str(value).replace("\r", " ").replace("\n", " ")


def canonical_string(entry: dict) -> str:
    """Canonical text of an entry: one 'key=value' line per chained field."""
    return "\n".join(f"{k}={sanitize(entry.get(k, ''))}" for k in CHAINED_FIELDS)


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def entry_hash(entry: dict) -> str:
    return sha256_hex(canonical_string(entry).encode("utf-8"))


def next_chain(prev_chain: str, entry: dict) -> str:
    return sha256_hex((prev_chain + entry_hash(entry)).encode("utf-8"))


def utc_now() -> _dt.datetime:
    return _dt.datetime.now(_dt.timezone.utc)


def fmt_utc(dt: _dt.datetime) -> str:
    dt = dt.astimezone(_dt.timezone.utc)
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def file_hashes(path: Path) -> tuple[str, str, int]:
    """Return (sha256, md5, size) of a file, read in chunks."""
    sha, md5, size = hashlib.sha256(), hashlib.md5(), 0
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            sha.update(chunk)
            md5.update(chunk)
            size += len(chunk)
    return sha.hexdigest(), md5.hexdigest(), size


# --------------------------------------------------------------------------- #
# Case folder and journal access
# --------------------------------------------------------------------------- #
class ChainShotError(Exception):
    pass


class Case:
    def __init__(self, root: Path):
        self.root = Path(root)
        self.journal = self.root / JOURNAL_NAME

    # -- metadata ---------------------------------------------------------- #
    def meta(self) -> dict:
        p = self.root / CASE_FILE
        if p.exists():
            return json.loads(p.read_text(encoding="utf-8"))
        return {}

    # -- reading ----------------------------------------------------------- #
    def entries(self) -> list[dict]:
        if not self.journal.exists():
            return []
        out = []
        with open(self.journal, "r", encoding="utf-8-sig") as fh:
            for lineno, line in enumerate(fh, 1):
                line = line.rstrip("\n").rstrip("\r")
                if not line.strip():
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise ChainShotError(f"{JOURNAL_NAME} line {lineno}: invalid JSON ({exc})") from exc
                out.append(obj)
        return out

    def resolve(self, stored: str) -> Path:
        p = Path(stored)
        return p if p.is_absolute() else self.root / p

    # -- writing ----------------------------------------------------------- #
    def append(self, fields: dict, extra: dict | None = None) -> dict:
        """Append an entry, computing seq and chain. Returns the stored entry."""
        existing = self.entries()
        prev = existing[-1]["chain"] if existing else GENESIS
        meta = self.meta()
        now = utc_now()
        entry = {k: "" for k in CHAINED_FIELDS}
        entry.update({
            "v": FORMAT_VERSION,
            "seq": str(len(existing) + 1),
            "utc": fmt_utc(now),
            "examiner": meta.get("examiner", ""),
            "host": socket.gethostname(),
            "source": TOOL_ID,
        })
        entry.update({k: sanitize(v) for k, v in fields.items() if k in CHAINED_FIELDS})
        if entry["type"] not in ENTRY_TYPES:
            raise ChainShotError(f"unknown entry type: {entry['type']}")
        entry["local"] = now.astimezone().isoformat(timespec="seconds")
        if extra:
            entry["extra"] = extra
        entry["chain"] = next_chain(prev, entry)
        line = json.dumps(entry, ensure_ascii=False, separators=(",", ":"))
        with open(self.journal, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(line + "\n")
        return entry

    def journal_prefix_sha256(self, n: int) -> str:
        """SHA-256 of the first n lines of the journal file, as raw bytes."""
        data = self.journal.read_bytes()
        if n == 0:
            return sha256_hex(b"")
        idx, count = -1, 0
        while count < n:
            idx = data.find(b"\n", idx + 1)
            if idx == -1:
                raise ChainShotError(f"journal has fewer than {n} lines")
            count += 1
        return sha256_hex(data[: idx + 1])


# --------------------------------------------------------------------------- #
# Verification
# --------------------------------------------------------------------------- #
class Result:
    def __init__(self):
        self.rows: list[dict] = []
        self.seals: list[dict] = []
        self.errors: list[str] = []
        self.warnings: list[str] = []

    @property
    def ok(self) -> bool:
        return not self.errors and all(r["ok"] for r in self.rows) and all(s["ok"] for s in self.seals)


def verify_case(case: Case, tsa_ca: Path | None = None, check_files: bool = True) -> Result:
    res = Result()
    if not case.journal.exists():
        legacy = case.root / "journal_preuves.csv"
        if legacy.exists():
            return verify_legacy(case.root, legacy)
        res.errors.append(f"no {JOURNAL_NAME} in {case.root}")
        return res
    try:
        entries = case.entries()
    except ChainShotError as exc:
        res.errors.append(str(exc))
        return res

    prev, last_utc, chains = GENESIS, "", []
    for i, e in enumerate(entries, 1):
        problems = []
        if e.get("v") != FORMAT_VERSION:
            problems.append(f"unsupported format version {e.get('v')!r}")
        if e.get("seq") != str(i):
            problems.append(f"sequence is {e.get('seq')!r}, expected {i}")
        expected = next_chain(prev, e)
        if e.get("chain") != expected:
            problems.append("CHAIN BROKEN (entry edited, removed or reordered)")
        utc = e.get("utc", "")
        if last_utc and utc < last_utc:
            res.warnings.append(f"entry {i}: timestamp earlier than previous entry")
        last_utc = utc or last_utc
        file_status = ""
        if e.get("file") and check_files:
            p = case.resolve(e["file"])
            if not p.exists():
                problems.append("FILE MISSING")
                file_status = "missing"
            else:
                sha, _md5, size = file_hashes(p)
                if sha != e.get("sha256"):
                    problems.append("FILE HASH MISMATCH (file modified)")
                    file_status = "modified"
                elif e.get("size") and str(size) != e.get("size"):
                    problems.append("SIZE MISMATCH")
                    file_status = "modified"
                else:
                    file_status = "ok"
        res.rows.append({"seq": i, "entry": e, "ok": not problems, "problems": problems, "file_status": file_status})
        prev = e.get("chain", "")
        chains.append(e.get("chain", ""))

    seal_dir = case.root / "seals"
    if seal_dir.is_dir():
        for sp in sorted(seal_dir.glob("seal-*.json")):
            res.seals.append(verify_seal(case, sp, chains, tsa_ca))
    return res


def verify_seal(case: Case, seal_path: Path, chains: list[str], tsa_ca: Path | None) -> dict:
    info = {"file": seal_path.name, "ok": True, "problems": [], "tsr": None}
    try:
        seal = json.loads(seal_path.read_text(encoding="utf-8"))
        n = int(seal["entries"])
    except Exception as exc:  # noqa: BLE001 - report any malformed seal
        info.update(ok=False, problems=[f"unreadable seal: {exc}"])
        return info
    info["entries"] = n
    info["utc"] = seal.get("utc", "")
    if n > len(chains) or (n > 0 and chains[n - 1] != seal.get("last_chain")):
        info["problems"].append("last_chain does not match the journal")
    else:
        try:
            if case.journal_prefix_sha256(n) != seal.get("journal_prefix_sha256"):
                info["problems"].append("journal prefix hash differs (journal rewritten)")
        except ChainShotError as exc:
            info["problems"].append(str(exc))

    tsr_path = seal_path.with_suffix(".tsr")
    if tsr_path.exists():
        imprint = sha256_hex(seal_path.read_bytes())
        try:
            tst = parse_tsr(tsr_path.read_bytes())
            t = {"gen_time": tst["gen_time"], "imprint_ok": tst["hashed_message"] == imprint,
                 "signature": "not checked"}
            if not t["imprint_ok"]:
                info["problems"].append("timestamp does not certify this seal (imprint mismatch)")
            if tsa_ca:
                ok, msg = openssl_verify_tsr(tsr_path, seal_path, tsa_ca)
                t["signature"] = "valid" if ok else f"INVALID ({msg})"
                if not ok:
                    info["problems"].append("timestamp signature invalid")
            info["tsr"] = t
        except ChainShotError as exc:
            info["problems"].append(f"timestamp unreadable: {exc}")
    info["ok"] = not info["problems"]
    return info


def verify_legacy(root: Path, csv_path: Path) -> Result:
    """Verify the CSV journal of the original Capture-Preuve.ps1 script (format v0)."""
    res = Result()
    res.warnings.append("legacy v0 journal: only file hashes are chained, not timestamps or notes")
    prev = GENESIS
    with open(csv_path, "r", encoding="utf-8-sig", newline="") as fh:
        for i, row in enumerate(csv.DictReader(fh), 1):
            problems = []
            p = Path(row["Fichier"])
            p = p if p.is_absolute() else root / p
            if not p.exists():
                problems.append("FILE MISSING")
            elif file_hashes(p)[0] != row["SHA256"].lower():
                problems.append("FILE HASH MISMATCH (file modified)")
            if sha256_hex((prev + row["SHA256"].lower()).encode("utf-8")) != row["ChainHash"]:
                problems.append("CHAIN BROKEN")
            prev = row["ChainHash"]
            entry = {"seq": row.get("N", str(i)), "utc": row.get("HorodatageUTC", ""), "type": row.get("Type", ""),
                     "tag": row.get("Point", ""), "file": row["Fichier"], "sha256": row["SHA256"],
                     "note": row.get("Note", "")}
            res.rows.append({"seq": i, "entry": entry, "ok": not problems, "problems": problems,
                             "file_status": "" if problems else "ok"})
    return res


# --------------------------------------------------------------------------- #
# Minimal DER encoder/decoder for RFC 3161 (no third-party dependency)
# --------------------------------------------------------------------------- #
def _der_len(n: int) -> bytes:
    if n < 0x80:
        return bytes([n])
    b = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(b)]) + b


def _der(tag: int, content: bytes) -> bytes:
    return bytes([tag]) + _der_len(len(content)) + content


def _der_int(v: int) -> bytes:
    b = v.to_bytes(max(1, (v.bit_length() + 8) // 8), "big", signed=False)
    return _der(0x02, b)


def _der_oid(dotted: str) -> bytes:
    parts = [int(x) for x in dotted.split(".")]
    body = bytes([40 * parts[0] + parts[1]])
    for p in parts[2:]:
        enc = [p & 0x7F]
        p >>= 7
        while p:
            enc.insert(0, 0x80 | (p & 0x7F))
            p >>= 7
        body += bytes(enc)
    return _der(0x06, body)


OID_SHA256 = "2.16.840.1.101.3.4.2.1"
OID_TSTINFO = "1.2.840.113549.1.9.16.1.4"


def build_tsq(digest: bytes, nonce: int) -> bytes:
    """TimeStampReq (RFC 3161 section 2.4.1) for a SHA-256 digest, asking for the TSA certificate."""
    alg = _der(0x30, _der_oid(OID_SHA256) + b"\x05\x00")
    imprint = _der(0x30, alg + _der(0x04, digest))
    return _der(0x30, _der_int(1) + imprint + _der_int(nonce) + b"\x01\x01\xff")


def _tlv(data: bytes, pos: int) -> tuple[int, int, int, int]:
    """Return (tag, content_start, content_end, next_pos) of the TLV at pos."""
    if pos + 2 > len(data):
        raise ChainShotError("truncated DER")
    tag = data[pos]
    ln = data[pos + 1]
    pos += 2
    if ln & 0x80:
        nb = ln & 0x7F
        if nb == 0 or pos + nb > len(data):
            raise ChainShotError("unsupported DER length")
        ln = int.from_bytes(data[pos:pos + nb], "big")
        pos += nb
    if pos + ln > len(data):
        raise ChainShotError("truncated DER")
    return tag, pos, pos + ln, pos + ln


def _children(data: bytes, start: int, end: int) -> list[tuple[int, int, int]]:
    out, pos = [], start
    while pos < end:
        tag, cs, ce, pos = _tlv(data, pos)
        out.append((tag, cs, ce))
    return out


def _decode_oid(b: bytes) -> str:
    parts = [b[0] // 40, b[0] % 40]
    v = 0
    for byte in b[1:]:
        v = (v << 7) | (byte & 0x7F)
        if not byte & 0x80:
            parts.append(v)
            v = 0
    return ".".join(map(str, parts))


def parse_tsr(data: bytes) -> dict:
    """Extract status, genTime, policy, serial and message imprint from a TimeStampResp."""
    _tag, cs, ce, _ = _tlv(data, 0)
    top = _children(data, cs, ce)
    status_int = _children(data, top[0][1], top[0][2])[0]
    status = int.from_bytes(data[status_int[1]:status_int[2]], "big")
    if status not in (0, 1):
        raise ChainShotError(f"TSA refused the request (status {status})")
    if len(top) < 2:
        raise ChainShotError("no timestamp token in response")
    # ContentInfo -> [0] -> SignedData -> encapContentInfo -> [0] -> OCTET STRING (TSTInfo)
    ci = _children(data, top[1][1], top[1][2])
    sd_wrap = _children(data, ci[1][1], ci[1][2])
    sd = _children(data, sd_wrap[0][1], sd_wrap[0][2])
    encap = _children(data, sd[2][1], sd[2][2])
    if _decode_oid(data[encap[0][1]:encap[0][2]]) != OID_TSTINFO:
        raise ChainShotError("token does not contain a TSTInfo")
    octet = _children(data, encap[1][1], encap[1][2])[0]
    tst = data[octet[1]:octet[2]]
    _, ts, te, _ = _tlv(tst, 0)
    f = _children(tst, ts, te)
    imprint = _children(tst, f[2][1], f[2][2])
    alg = _children(tst, imprint[0][1], imprint[0][2])
    gen = tst[f[4][1]:f[4][2]].decode("ascii")
    return {
        "status": status,
        "policy": _decode_oid(tst[f[1][1]:f[1][2]]),
        "hash_alg": _decode_oid(tst[alg[0][1]:alg[0][2]]),
        "hashed_message": tst[imprint[1][1]:imprint[1][2]].hex(),
        "serial": int.from_bytes(tst[f[3][1]:f[3][2]], "big"),
        "gen_time": _gen_time_iso(gen),
    }


def _gen_time_iso(g: str) -> str:
    # GeneralizedTime YYYYMMDDHHMMSS[.fff]Z
    base = f"{g[0:4]}-{g[4:6]}-{g[6:8]}T{g[8:10]}:{g[10:12]}:{g[12:14]}"
    frac = g[14:-1] if len(g) > 15 and g[14] == "." else ""
    return base + frac + "Z"


def request_timestamp(digest: bytes, tsa_url: str, timeout: int = 30) -> bytes:
    req = urllib.request.Request(
        tsa_url, data=build_tsq(digest, secrets.randbits(63)),
        headers={"Content-Type": "application/timestamp-query", "User-Agent": TOOL_ID},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310 - URL chosen by the user
        return resp.read()


def openssl_verify_tsr(tsr: Path, data_file: Path, ca_file: Path) -> tuple[bool, str]:
    exe = shutil.which("openssl")
    if not exe:
        return False, "openssl not found"
    proc = subprocess.run(
        [exe, "ts", "-verify", "-in", str(tsr), "-data", str(data_file), "-CAfile", str(ca_file)],
        capture_output=True, text=True,
    )
    out = (proc.stdout + proc.stderr).strip()
    return proc.returncode == 0 and "OK" in out, out.splitlines()[-1] if out else "no output"


# --------------------------------------------------------------------------- #
# Commands
# --------------------------------------------------------------------------- #
def cmd_init(args) -> int:
    root = Path(args.case_dir)
    if (root / CASE_FILE).exists():
        print(f"Case already exists: {root}", file=sys.stderr)
        return 2
    (root / "captures").mkdir(parents=True, exist_ok=True)
    meta = {
        "format": "chainshot-case", "v": FORMAT_VERSION, "case": args.case,
        "examiner": args.examiner or getpass.getuser(), "created_utc": fmt_utc(utc_now()), "tool": TOOL_ID,
    }
    (root / CASE_FILE).write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    Case(root).append({"type": "note", "tag": "INIT", "note": f"Case created: {args.case}"})
    print(f"Case created: {root.resolve()}")
    return 0


def _require_case(path: str) -> Case:
    case = Case(Path(path))
    if not case.root.is_dir():
        raise ChainShotError(f"not a directory: {case.root}")
    return case


def cmd_add(args) -> int:
    case = _require_case(args.case_dir)
    src = Path(args.file).resolve()
    if not src.is_file():
        raise ChainShotError(f"not a file: {src}")
    target = src
    if args.copy:
        dest_dir = case.root / "files"
        dest_dir.mkdir(exist_ok=True)
        target = dest_dir / src.name
        if target.exists():
            raise ChainShotError(f"already exists in case: {target}")
        shutil.copy2(src, target)
    sha, md5, size = file_hashes(target)
    try:
        # Resolve both sides: temp folders differ once resolved (macOS /private/var,
        # Windows 8.3 short names such as RUNNER~1).
        stored = target.resolve().relative_to(case.root.resolve()).as_posix()
    except ValueError:
        stored = str(target)
    entry = case.append({"type": "file", "tag": args.tag, "file": stored, "size": str(size),
                         "sha256": sha, "note": args.note}, extra={"md5": md5})
    print(f"#{entry['seq']}  {stored}\n    SHA-256 {sha}\n    MD5     {md5}")
    return 0


def cmd_note(args) -> int:
    case = _require_case(args.case_dir)
    entry = case.append({"type": "note", "tag": args.tag, "note": args.text})
    print(f"#{entry['seq']}  note recorded at {entry['utc']}")
    return 0


def _print_result(res: Result) -> None:
    for r in res.rows:
        e = r["entry"]
        state = "OK" if r["ok"] else " / ".join(r["problems"])
        label = e.get("file") or e.get("note", "")
        print(f"{r['seq']:>5}  {e.get('utc', ''):<24}  {e.get('tag', ''):<10}  {state:<10}  {label}")
    for s in res.seals:
        state = "OK" if s["ok"] else " / ".join(s["problems"])
        line = f"Seal {s['file']}: {state}"
        if s.get("tsr"):
            t = s["tsr"]
            line += f"  | RFC 3161 time {t['gen_time']}, imprint {'OK' if t['imprint_ok'] else 'MISMATCH'}, signature {t['signature']}"
        print(line)
    for w in res.warnings:
        print(f"WARNING: {w}")
    for e in res.errors:
        print(f"ERROR: {e}")


def cmd_verify(args) -> int:
    case = Case(Path(args.case_dir))
    res = verify_case(case, Path(args.tsa_ca) if args.tsa_ca else None)
    if args.json:
        print(json.dumps({
            "ok": res.ok, "errors": res.errors, "warnings": res.warnings,
            "entries": [{"seq": r["seq"], "ok": r["ok"], "problems": r["problems"]} for r in res.rows],
            "seals": res.seals,
        }, indent=2))
    else:
        _print_result(res)
        n_bad = sum(not r["ok"] for r in res.rows)
        print(f"\n{len(res.rows)} entries, {n_bad} problem(s), {len(res.seals)} seal(s).")
        if case.journal.exists():
            print(f"SHA-256 of {JOURNAL_NAME}: {sha256_hex(case.journal.read_bytes())}")
        print("RESULT: " + ("ALL EVIDENCE INTACT" if res.ok else "VERIFICATION FAILED"))
    return 0 if res.ok else 1


def cmd_seal(args) -> int:
    case = _require_case(args.case_dir)
    res = verify_case(case)
    if not res.ok:
        _print_result(res)
        print("Refusing to seal a journal that does not verify.", file=sys.stderr)
        return 1
    entries = case.entries()
    n = len(entries)
    stamp = utc_now()
    seal = {
        "format": "chainshot-seal", "v": FORMAT_VERSION, "case": case.meta().get("case", ""),
        "utc": fmt_utc(stamp), "entries": str(n), "last_chain": entries[-1]["chain"] if n else GENESIS,
        "journal_prefix_sha256": case.journal_prefix_sha256(n), "sealed_by": case.meta().get("examiner", ""),
        "tool": TOOL_ID,
    }
    seal_dir = case.root / "seals"
    seal_dir.mkdir(exist_ok=True)
    name = "seal-" + stamp.strftime("%Y%m%dT%H%M%S") + f"{stamp.microsecond // 1000:03d}Z"
    seal_path = seal_dir / f"{name}.json"
    seal_path.write_bytes((json.dumps(seal, indent=2, sort_keys=True) + "\n").encode("utf-8"))
    seal_sha, _, seal_size = file_hashes(seal_path)
    case.append({"type": "seal", "tag": args.tag, "file": f"seals/{seal_path.name}", "size": str(seal_size),
                 "sha256": seal_sha, "note": f"Seal of entries 1-{n}"})
    print(f"Seal written: {seal_path}\n    entries sealed : {n}\n    seal SHA-256   : {seal_sha}")

    if args.tsa:
        try:
            tsr = request_timestamp(bytes.fromhex(seal_sha), args.tsa)
            info = parse_tsr(tsr)
        except Exception as exc:  # noqa: BLE001 - network or TSA errors are reported, the seal stays valid
            print(f"RFC 3161 timestamp FAILED ({exc}). The local seal is still valid; retry later.", file=sys.stderr)
            return 1
        if info["hashed_message"] != seal_sha:
            print("TSA answered for a different hash, response discarded.", file=sys.stderr)
            return 1
        tsr_path = seal_path.with_suffix(".tsr")
        tsr_path.write_bytes(tsr)
        tsr_sha, _, tsr_size = file_hashes(tsr_path)
        case.append({"type": "timestamp", "tag": args.tag, "file": f"seals/{tsr_path.name}", "size": str(tsr_size),
                     "sha256": tsr_sha, "url": args.tsa, "note": f"RFC 3161 time {info['gen_time']}"})
        print(f"RFC 3161 timestamp: {info['gen_time']} (TSA {args.tsa}, serial {info['serial']})")
    print("Write the seal SHA-256 in your signed report or send it to your client separately.")
    return 0


def cmd_export_csv(args) -> int:
    case = _require_case(args.case_dir)
    out = Path(args.output) if args.output else case.root / "journal-export.csv"
    cols = list(CHAINED_FIELDS) + ["local", "chain"]
    with open(out, "w", encoding="utf-8-sig", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        for e in case.entries():
            w.writerow(e)
    print(f"CSV written: {out} (view only: the journal of record is {JOURNAL_NAME})")
    return 0


def cmd_tsr_info(args) -> int:
    info = parse_tsr(Path(args.tsr).read_bytes())
    for k, v in info.items():
        print(f"{k:15} {v}")
    return 0


# --------------------------------------------------------------------------- #
# HTML report
# --------------------------------------------------------------------------- #
REPORT_CSS = """
:root{--bg:#fbfaf7;--fg:#1d1d1b;--muted:#6b6a65;--line:#e3e0d8;--ok:#1f7a4d;--bad:#b3261e;--card:#fff;--accent:#2b4c7e}
@media (prefers-color-scheme:dark){:root{--bg:#161614;--fg:#ecebe6;--muted:#a3a19a;--line:#33322e;--ok:#5cc28f;--bad:#ff8a80;--card:#1f1f1c;--accent:#8fb3e8}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:1200px;margin:0 auto;padding:32px 16px}h1{font-size:28px;margin:0 0 4px}h2{font-size:18px;margin:32px 0 8px}
.muted{color:var(--muted)}.banner{padding:16px 20px;border-radius:8px;margin:20px 0;font-weight:600;border:1px solid}
.banner.ok{color:var(--ok);border-color:var(--ok)}.banner.bad{color:var(--bad);border-color:var(--bad)}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0}dt{color:var(--muted)}dd{margin:0}
table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line)}th,td{padding:8px;border-bottom:1px solid var(--line);vertical-align:top;text-align:left}
th{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}code{font:12px ui-monospace,Consolas,monospace;word-break:break-all}
img.thumb{max-width:220px;max-height:140px;border:1px solid var(--line);border-radius:4px;display:block}
.st-ok{color:var(--ok);font-weight:600}.st-bad{color:var(--bad);font-weight:600}.wrap{overflow-x:auto}
footer{margin-top:40px;font-size:13px;color:var(--muted)}
"""


def render_report(case: Case, res: Result, out_path: Path) -> str:
    meta = case.meta()
    out_dir = out_path.parent.resolve()
    esc = html.escape

    def rel(stored: str) -> str:
        p = case.resolve(stored).resolve()
        try:
            return Path(os.path.relpath(p, out_dir)).as_posix()
        except ValueError:  # different drive on Windows
            return p.as_uri()

    rows = []
    for r in res.rows:
        e = r["entry"]
        thumb = ""
        f = e.get("file", "")
        if f and f.lower().endswith((".png", ".jpg", ".jpeg", ".webp")) and r["file_status"] == "ok":
            href = esc(rel(f))
            thumb = f'<a href="{href}"><img class="thumb" loading="lazy" src="{href}" alt="capture {r["seq"]}"></a>'
        status = '<span class="st-ok">OK</span>' if r["ok"] else '<span class="st-bad">' + esc(" / ".join(r["problems"])) + "</span>"
        ctx = " ".join(x for x in (e.get("url", ""), e.get("title", "")) if x)
        rows.append(
            f"<tr><td>{esc(str(r['seq']))}</td><td><code>{esc(e.get('utc', ''))}</code></td><td>{esc(e.get('tag', ''))}</td>"
            f"<td>{esc(e.get('type', ''))}</td><td>{thumb}<code>{esc(f)}</code><div class='muted'>{esc(ctx)}</div>"
            f"<div>{esc(e.get('note', ''))}</div></td><td><code>{esc(e.get('sha256', ''))}</code></td><td>{status}</td></tr>"
        )
    seal_rows = []
    for s in res.seals:
        t = s.get("tsr") or {}
        ts = f"{esc(t.get('gen_time', ''))}<br>imprint {'OK' if t.get('imprint_ok') else 'MISMATCH'}, signature {esc(t.get('signature', ''))}" if t else "none"
        st = '<span class="st-ok">OK</span>' if s["ok"] else '<span class="st-bad">' + esc(" / ".join(s["problems"])) + "</span>"
        seal_rows.append(f"<tr><td><code>{esc(s['file'])}</code></td><td>{esc(s.get('utc', ''))}</td>"
                         f"<td>{esc(str(s.get('entries', '')))}</td><td>{ts}</td><td>{st}</td></tr>")
    journal_sha = sha256_hex(case.journal.read_bytes()) if case.journal.exists() else ""
    banner = ('<div class="banner ok">All evidence verified: files, hash chain and seals are intact.</div>' if res.ok
              else '<div class="banner bad">Verification FAILED: see the entries marked in red.</div>')
    warn = "".join(f"<p class='st-bad'>{esc(w)}</p>" for w in res.warnings + res.errors)
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ChainShot report</title><style>{REPORT_CSS}</style></head><body><main>
<h1>Evidence report: {esc(meta.get('case', case.root.name))}</h1>
<p class="muted">Generated {esc(fmt_utc(utc_now()))} by {esc(TOOL_ID)}</p>
{banner}{warn}
<dl><dt>Case folder</dt><dd><code>{esc(str(case.root.resolve()))}</code></dd>
<dt>Examiner</dt><dd>{esc(meta.get('examiner', ''))}</dd><dt>Created</dt><dd>{esc(meta.get('created_utc', ''))}</dd>
<dt>Entries</dt><dd>{len(res.rows)}</dd><dt>Journal SHA-256</dt><dd><code>{esc(journal_sha)}</code></dd></dl>
<h2>Seals</h2><div class="wrap"><table><thead><tr><th>Seal</th><th>Sealed (UTC)</th><th>Entries</th><th>RFC 3161</th><th>Status</th></tr></thead>
<tbody>{''.join(seal_rows) or '<tr><td colspan=5 class=muted>No seal yet: run <code>chainshot seal</code>.</td></tr>'}</tbody></table></div>
<h2>Journal</h2><div class="wrap"><table><thead><tr><th>#</th><th>UTC</th><th>Tag</th><th>Type</th><th>Evidence</th><th>SHA-256</th><th>Status</th></tr></thead>
<tbody>{''.join(rows)}</tbody></table></div>
<footer><p>How to check this report independently: copy <code>chainshot.py</code> (standard Python 3.9+, no dependency) and run
<code>python chainshot.py verify &lt;case folder&gt;</code>. Each entry is chained to the previous one with SHA-256; editing,
removing or reordering any entry, file or timestamp breaks the chain. This report is a view: the journal and the files are the evidence.</p></footer>
</main></body></html>
"""


def cmd_report(args) -> int:
    case = _require_case(args.case_dir)
    res = verify_case(case, Path(args.tsa_ca) if args.tsa_ca else None)
    out = Path(args.output) if args.output else case.root / "report" / "index.html"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(render_report(case, res, out), encoding="utf-8")
    print(f"Report written: {out}  ({'all verified' if res.ok else 'VERIFICATION FAILED'})")
    return 0 if res.ok else 1


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="chainshot", description="Tamper-evident evidence journal (ChainShot).")
    p.add_argument("--version", action="version", version=TOOL_ID)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("init", help="create a case folder")
    s.add_argument("case_dir")
    s.add_argument("--case", required=True, help="case name or reference")
    s.add_argument("--examiner", help="examiner name (default: OS user)")
    s.set_defaults(func=cmd_init)

    s = sub.add_parser("add", help="hash a file and append it to the journal")
    s.add_argument("case_dir")
    s.add_argument("file")
    s.add_argument("--tag", default="", help="evidence tag, e.g. P17")
    s.add_argument("--note", default="")
    s.add_argument("--copy", action="store_true", help="copy the file into <case>/files/ first")
    s.set_defaults(func=cmd_add)

    s = sub.add_parser("note", help="append a text-only entry")
    s.add_argument("case_dir")
    s.add_argument("text")
    s.add_argument("--tag", default="")
    s.set_defaults(func=cmd_note)

    s = sub.add_parser("verify", help="verify files, hash chain and seals")
    s.add_argument("case_dir")
    s.add_argument("--tsa-ca", help="CA certificate (PEM) of the TSA, to verify RFC 3161 signatures with openssl")
    s.add_argument("--json", action="store_true", help="machine-readable output")
    s.set_defaults(func=cmd_verify)

    s = sub.add_parser("seal", help="seal the journal, optionally with an RFC 3161 timestamp")
    s.add_argument("case_dir")
    s.add_argument("--tsa", nargs="?", const=DEFAULT_TSA, default=None,
                   help=f"request an RFC 3161 timestamp (default TSA: {DEFAULT_TSA})")
    s.add_argument("--tag", default="SEAL")
    s.set_defaults(func=cmd_seal)

    s = sub.add_parser("report", help="generate an HTML report")
    s.add_argument("case_dir")
    s.add_argument("-o", "--output", help="output file (default: <case>/report/index.html)")
    s.add_argument("--tsa-ca", help="CA certificate (PEM) of the TSA")
    s.set_defaults(func=cmd_report)

    s = sub.add_parser("export-csv", help="export the journal as CSV")
    s.add_argument("case_dir")
    s.add_argument("-o", "--output")
    s.set_defaults(func=cmd_export_csv)

    s = sub.add_parser("tsr-info", help="show an RFC 3161 timestamp response")
    s.add_argument("tsr")
    s.set_defaults(func=cmd_tsr_info)
    return p


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except ChainShotError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    except OSError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
