# SPDX-License-Identifier: MIT
"""Tests for the browser link (native messaging host), evidence packages, shared state
and the clock offset recorded with RFC 3161 timestamps."""
import base64
import datetime as dt
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
import chainshot as cs  # noqa: E402
from test_chainshot import run  # noqa: E402

SCRIPT = HERE.parent / "chainshot.py"


def tiny_png(width=4, height=3) -> bytes:
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    raw = b"".join(b"\x00" + b"\xff\x00\x00" * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


class WithConfig(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="chainshot-v12-"))
        self.cfg = self.tmp / "config"
        self._old = os.environ.get("CHAINSHOT_CONFIG_DIR")
        os.environ["CHAINSHOT_CONFIG_DIR"] = str(self.cfg)
        self.case = self.tmp / "case"
        self.assertEqual(run("init", self.case, "--case", "LINK-01", "--examiner", "Tester"), 0)

    def tearDown(self):
        if self._old is None:
            os.environ.pop("CHAINSHOT_CONFIG_DIR", None)
        else:
            os.environ["CHAINSHOT_CONFIG_DIR"] = self._old
        shutil.rmtree(self.tmp, ignore_errors=True)

    def host(self, *messages):
        """Run the native host as the browser does: framed JSON on stdin/stdout."""
        data = b"".join(struct.pack("<I", len(b)) + b for b in (json.dumps(m).encode() for m in messages))
        env = dict(os.environ, CHAINSHOT_CONFIG_DIR=str(self.cfg))
        p = subprocess.run([sys.executable, str(SCRIPT), "chrome-extension://dcccfnbiglhpbcdhbmokfilfgpfnmikf/"],
                           input=data, capture_output=True, env=env, timeout=60)
        self.assertEqual(p.returncode, 0, p.stderr.decode())
        out, replies = p.stdout, []
        while out:
            (n,) = struct.unpack("<I", out[:4])
            replies.append(json.loads(out[4:4 + n]))
            out = out[4 + n:]
        return replies


class TestState(WithConfig):
    def test_use_and_tag_are_shared_through_state_json(self):
        self.assertEqual(run("use", self.case), 0)
        self.assertEqual(run("tag", "P17"), 0)
        st = json.loads((self.cfg / "state.json").read_text(encoding="utf-8"))
        self.assertEqual(Path(st["CasePath"]), self.case.resolve())
        self.assertEqual(st["Tag"], "P17")
        self.assertEqual(run("use", self.tmp), 2)  # not a case


class TestNativeHost(WithConfig):
    def test_hello_without_case(self):
        [r] = self.host({"type": "hello", "protocol": 1})
        self.assertTrue(r["ok"])
        self.assertIsNone(r["case"])
        self.assertIn("warning", r)

    def test_full_session_and_verification(self):
        run("use", self.case)
        png = tiny_png()
        now = cs.fmt_utc(cs.utc_now())
        hello, tag, cap, stale, bad, note, ver = self.host(
            {"type": "hello"},
            {"type": "set_tag", "tag": "P05"},
            {"type": "capture", "png_base64": base64.b64encode(png).decode(), "utc": now, "expected_seq": 2,
             "url": "https://example.org/", "title": "Example", "tag": "P05", "note": "first",
             "source": "chainshot-ext/1.2.0", "extra": {"viewport": "4x3", "user_agent": "UA", "evil": "x"}},
            {"type": "capture", "png_base64": base64.b64encode(png).decode(), "utc": now, "expected_seq": 2},
            {"type": "capture", "png_base64": base64.b64encode(b"not a png").decode(), "expected_seq": 3},
            {"type": "note", "text": "observed the page", "tag": "P05"},
            {"type": "verify"},
        )
        self.assertEqual(hello["case"]["next_seq"], 2)
        self.assertEqual(hello["case"]["next_capture"], 1)
        self.assertEqual(tag["tag"], "P05")
        self.assertTrue(cap["ok"], cap)
        self.assertEqual(cap["entry"]["seq"], "2")
        self.assertEqual(cap["entry"]["utc"], now)  # the capture time is kept
        self.assertFalse(stale["ok"])
        self.assertTrue(stale["retry"])
        self.assertEqual(stale["case"]["next_seq"], 3)
        self.assertFalse(bad["ok"])
        self.assertIn("PNG", bad["error"])
        self.assertEqual(note["entry"]["seq"], "3")
        self.assertTrue(ver["intact"], ver)
        entries = cs.Case(self.case).entries()
        e = entries[1]
        self.assertEqual((e["type"], e["url"], e["title"], e["source"]), ("browser", "https://example.org/", "Example", "chainshot-ext/1.2.0"))
        self.assertTrue(e["file"].startswith("captures/0002_") and e["file"].endswith("_P05.png"))
        self.assertNotIn("evil", e["extra"])
        self.assertEqual(e["extra"]["via"], cs.TOOL_ID)
        self.assertEqual((self.case / e["file"]).read_bytes(), png)
        self.assertEqual(run("verify", self.case), 0)

    def test_mhtml_page_archive(self):
        run("use", self.case)
        mhtml = b"From: <Saved by Blink>\r\nSubject: Example\r\nMIME-Version: 1.0\r\n\r\nbody"
        ok, bad = self.host(
            {"type": "capture", "format": "mhtml", "data_base64": base64.b64encode(mhtml).decode(),
             "url": "https://example.org/", "title": "Example", "tag": "P1", "note": "Page archive (MHTML) of #1"},
            {"type": "capture", "format": "mhtml", "data_base64": base64.b64encode(b"<html>").decode()},
        )
        self.assertTrue(ok["ok"], ok)
        self.assertTrue(ok["entry"]["file"].endswith("_P1.mhtml"))
        self.assertEqual((self.case / ok["entry"]["file"]).read_bytes(), mhtml)
        self.assertFalse(bad["ok"])
        self.assertIn("MHTML", bad["error"])
        self.assertEqual(run("verify", self.case), 0)

    def test_browser_clock_far_off_is_ignored(self):
        run("use", self.case)
        old = "2001-01-01T00:00:00.000Z"
        [r] = self.host({"type": "capture", "png_base64": base64.b64encode(tiny_png()).decode(), "utc": old, "expected_seq": 2})
        self.assertNotEqual(r["entry"]["utc"], old)

    def test_register_browser_writes_manifest_and_launcher(self):
        if os.name == "nt":
            self.skipTest("registry variant is exercised by the Windows app tests")
        d = self.tmp / "NativeMessagingHosts"
        self.assertEqual(run("register-browser", "--dir", d, "--extension-id", "abcdefghijklmnopabcdefghijklmnop"), 0)
        m = json.loads((d / "com.chainshot.host.json").read_text())
        self.assertEqual(m["name"], "com.chainshot.host")
        self.assertIn("chrome-extension://dcccfnbiglhpbcdhbmokfilfgpfnmikf/", m["allowed_origins"])
        self.assertIn("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", m["allowed_origins"])
        self.assertTrue(os.access(m["path"], os.X_OK))
        self.assertEqual(run("register-browser", "--dir", d, "--remove"), 0)
        self.assertFalse((d / "com.chainshot.host.json").exists())


class TestPackage(WithConfig):
    def test_package_contains_everything_and_verifies(self):
        ev = self.tmp / "evidence.bin"
        ev.write_bytes(b"data")
        run("add", self.case, ev, "--copy", "--tag", "P1")
        out = self.tmp / "out" / "pkg.zip"
        self.assertEqual(run("package", self.case, "-o", out, "--seal"), 0)
        with zipfile.ZipFile(out) as z:
            names = set(z.namelist())
            self.assertIn("chainshot.py", names)
            self.assertIn("VERIFY.txt", names)
            self.assertIn("case/journal.jsonl", names)
            self.assertIn("case/files/evidence.bin", names)
            self.assertNotIn("case/.journal.lock", names)
            verify_txt = z.read("VERIFY.txt").decode()
            sums = z.read("SHA256SUMS.txt").decode().splitlines()
            for line in sums:
                sha, name = line.split("  ", 1)
                self.assertEqual(cs.sha256_hex(z.read(name)), sha, name)
            x = self.tmp / "x"
            z.extractall(x)
        seal = [e for e in cs.Case(self.case).entries() if e["type"] == "seal"][-1]
        self.assertIn(seal["sha256"], verify_txt)
        p = subprocess.run([sys.executable, str(x / "chainshot.py"), "verify", str(x / "case")], capture_output=True, text=True)
        self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
        last = cs.Case(self.case).entries()[-1]
        self.assertEqual(last["tag"], "PACKAGE")
        self.assertIn(cs.file_hashes(out)[0], last["note"])

    def test_package_refuses_a_broken_case(self):
        ev = self.case / "e.txt"
        ev.write_text("a")
        run("add", self.case, ev)
        ev.write_text("b")
        self.assertEqual(run("package", self.case, "-o", self.tmp / "p.zip"), 2)
        self.assertFalse((self.tmp / "p.zip").exists())


class TestClock(unittest.TestCase):
    def test_offset(self):
        now = dt.datetime(2026, 10, 17, 8, 0, 1, 500000, tzinfo=dt.timezone.utc)
        self.assertEqual(cs.clock_offset("2026-10-17T08:00:00Z", now), 1.5)
        self.assertEqual(cs.clock_offset("2026-10-17T08:00:02.250Z", now), -0.8)
        self.assertIsNone(cs.clock_offset("garbage", now))
        self.assertEqual(cs.timestamp_note("T", -0.8), "RFC 3161 time T (local clock -0.8 s)")


if __name__ == "__main__":
    unittest.main()
