# SPDX-License-Identifier: MIT
"""Test suite for chainshot.py (standard library unittest; also runs under pytest)."""
import contextlib
import csv
import hashlib
import io
import http.server
import json
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import chainshot as cs  # noqa: E402

VECTORS = HERE.parents[1] / "tests" / "vectors" / "canonical-vectors.json"


def run(*argv) -> int:
    """Run the CLI quietly and return its exit code."""
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return cs.main([str(a) for a in argv])


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="chainshot-"))
        self.case = self.tmp / "case"
        self.assertEqual(run("init", self.case, "--case", "TEST-01", "--examiner", "Tester"), 0)
        self.ev = self.tmp / "evidence.bin"
        self.ev.write_bytes(b"evidence content\n")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def lines(self):
        return (self.case / cs.JOURNAL_NAME).read_text(encoding="utf-8").splitlines()

    def write_lines(self, lines):
        (self.case / cs.JOURNAL_NAME).write_text("\n".join(lines) + "\n", encoding="utf-8")


class TestVectors(unittest.TestCase):
    def test_vectors_match_reference(self):
        doc = json.loads(VECTORS.read_text(encoding="utf-8"))
        prev = doc["genesis"]
        for v in doc["vectors"]:
            self.assertEqual(cs.canonical_string(v["entry"]), v["canonical"])
            self.assertEqual(cs.entry_hash(v["entry"]), v["entry_hash"])
            prev = cs.next_chain(prev, v["entry"])
            self.assertEqual(prev, v["chain"])

    def test_newlines_are_neutralised(self):
        e = {"note": "a\r\nb"}
        self.assertNotIn("\r", cs.canonical_string(e))
        self.assertIn("note=a  b", cs.canonical_string(e))


class TestJournal(Base):
    def test_add_and_verify(self):
        self.assertEqual(run("add", self.case, self.ev, "--tag", "P01", "--note", "Note é 📸"), 0)
        self.assertEqual(run("note", self.case, "observation", "--tag", "P02"), 0)
        self.assertEqual(run("verify", self.case), 0)
        entries = cs.Case(self.case).entries()
        self.assertEqual([e["seq"] for e in entries], ["1", "2", "3"])
        self.assertEqual(entries[1]["sha256"], hashlib.sha256(b"evidence content\n").hexdigest())

    def test_copy_stores_relative_path(self):
        run("add", self.case, self.ev, "--copy")
        e = cs.Case(self.case).entries()[-1]
        self.assertEqual(e["file"], "files/evidence.bin")

    def test_modified_file_detected(self):
        run("add", self.case, self.ev)
        self.ev.write_bytes(b"tampered\n")
        res = cs.verify_case(cs.Case(self.case))
        self.assertFalse(res.ok)
        self.assertIn("FILE HASH MISMATCH (file modified)", res.rows[1]["problems"])

    def test_missing_file_detected(self):
        run("add", self.case, self.ev)
        self.ev.unlink()
        self.assertEqual(run("verify", self.case), 1)

    def test_edited_note_breaks_chain(self):
        run("note", self.case, "original")
        run("note", self.case, "next")
        lines = self.lines()
        lines[1] = lines[1].replace("original", "forged")
        self.write_lines(lines)
        res = cs.verify_case(cs.Case(self.case))
        self.assertFalse(res.rows[1]["ok"])

    def test_edited_timestamp_breaks_chain(self):
        run("note", self.case, "x")
        lines = self.lines()
        obj = json.loads(lines[1])
        obj["utc"] = "2020-01-01T00:00:00.000Z"
        lines[1] = json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
        self.write_lines(lines)
        self.assertFalse(cs.verify_case(cs.Case(self.case)).ok)

    def test_removed_entry_detected(self):
        for t in ("a", "b", "c"):
            run("note", self.case, t)
        lines = self.lines()
        del lines[2]
        self.write_lines(lines)
        self.assertFalse(cs.verify_case(cs.Case(self.case)).ok)

    def test_reordered_entries_detected(self):
        for t in ("a", "b"):
            run("note", self.case, t)
        lines = self.lines()
        lines[1], lines[2] = lines[2], lines[1]
        self.write_lines(lines)
        self.assertFalse(cs.verify_case(cs.Case(self.case)).ok)

    def test_unknown_type_refused(self):
        with self.assertRaises(cs.ChainShotError):
            cs.Case(self.case).append({"type": "bogus"})

    def test_export_csv(self):
        run("add", self.case, self.ev, "--note", "é")
        self.assertEqual(run("export-csv", self.case), 0)
        rows = list(csv.DictReader(open(self.case / "journal-export.csv", encoding="utf-8-sig")))
        self.assertEqual(len(rows), 2)
        self.assertEqual(rows[1]["note"], "é")

    def test_report(self):
        img = self.case / "captures" / "shot.png"
        img.write_bytes(b"\x89PNG fake")
        run("add", self.case, img, "--tag", "P17")
        self.assertEqual(run("report", self.case), 0)
        page = (self.case / "report" / "index.html").read_text(encoding="utf-8")
        self.assertIn("All evidence verified", page)
        self.assertIn('src="../captures/shot.png"', page)
        img.write_bytes(b"changed")
        self.assertEqual(run("report", self.case), 1)
        self.assertIn("Verification FAILED", (self.case / "report" / "index.html").read_text(encoding="utf-8"))


class TestSeal(Base):
    def test_seal_survives_appends(self):
        run("note", self.case, "before")
        self.assertEqual(run("seal", self.case), 0)
        run("note", self.case, "after")
        res = cs.verify_case(cs.Case(self.case))
        self.assertTrue(res.ok, res.seals)
        self.assertEqual(len(res.seals), 1)
        self.assertEqual(cs.Case(self.case).entries()[2]["type"], "seal")

    def test_rewriting_sealed_journal_detected(self):
        run("note", self.case, "before")
        run("seal", self.case)
        lines = self.lines()
        # A forger rebuilds a whole consistent chain after changing entry 2:
        # the chain verifies again, but the seal still points to the old one.
        entries = [json.loads(x) for x in lines]
        entries[1]["note"] = "forged"
        prev = cs.GENESIS
        for e in entries:
            e["chain"] = cs.next_chain(prev, e)
            prev = e["chain"]
        self.write_lines([json.dumps(e, ensure_ascii=False, separators=(",", ":")) for e in entries])
        res = cs.verify_case(cs.Case(self.case), check_files=False)
        self.assertTrue(all(r["ok"] for r in res.rows))
        self.assertFalse(res.seals[0]["ok"])

    def test_refuses_to_seal_broken_journal(self):
        run("add", self.case, self.ev)
        self.ev.write_bytes(b"x")
        self.assertEqual(run("seal", self.case), 1)


class LegacyTest(unittest.TestCase):
    def test_v0_csv_journal(self):
        tmp = Path(tempfile.mkdtemp())
        try:
            (tmp / "captures").mkdir()
            prev, rows = "0" * 64, []
            for i in (1, 2):
                f = tmp / "captures" / f"c{i}.png"
                f.write_bytes(f"img{i}".encode())
                sha = hashlib.sha256(f.read_bytes()).hexdigest()
                prev = hashlib.sha256((prev + sha).encode()).hexdigest()
                rows.append({"N": str(i), "Type": "Capture ecran", "Point": "P00", "Fichier": f"captures\\c{i}.png".replace("\\", "/"),
                             "HorodatageUTC": "2026-10-13T10:00:00Z", "SHA256": sha, "Note": "", "ChainHash": prev})
            with open(tmp / "journal_preuves.csv", "w", encoding="utf-8-sig", newline="") as fh:
                w = csv.DictWriter(fh, fieldnames=list(rows[0]))
                w.writeheader()
                w.writerows(rows)
            self.assertEqual(run("verify", tmp), 0)
            (tmp / "captures" / "c1.png").write_bytes(b"x")
            self.assertEqual(run("verify", tmp), 1)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


# --------------------------------------------------------------------------- #
# RFC 3161 with a real local TSA built with OpenSSL
# --------------------------------------------------------------------------- #
def _usable_openssl():
    """OpenSSL >= 1.1.1 with the 'ts' command (macOS ships LibreSSL, which lacks it)."""
    exe = shutil.which("openssl")
    if not exe:
        return None
    try:
        out = subprocess.run([exe, "version"], capture_output=True, text=True, timeout=10).stdout
        major, minor, patch = (int("".join(c for c in x if c.isdigit()) or 0) for x in out.split()[1].split(".")[:3])
    except Exception:  # noqa: BLE001
        return None
    if not out.startswith("OpenSSL") or (major, minor, patch) < (1, 1, 1):
        return None
    return exe


OPENSSL = _usable_openssl()


def make_tsa(d: Path) -> Path:
    def sh(*a):
        subprocess.run(a, check=True, capture_output=True, cwd=d)
    sh(OPENSSL, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "ca.key", "-out", "ca.crt",
       "-days", "2", "-subj", "/CN=ChainShot Test CA", "-addext", "basicConstraints=critical,CA:TRUE",
       "-addext", "keyUsage=critical,keyCertSign,cRLSign")
    sh(OPENSSL, "req", "-newkey", "rsa:2048", "-nodes", "-keyout", "tsa.key", "-out", "tsa.csr",
       "-subj", "/CN=ChainShot Test TSA")
    (d / "ext.cnf").write_text("extendedKeyUsage=critical,timeStamping\nkeyUsage=critical,digitalSignature\n")
    sh(OPENSSL, "x509", "-req", "-in", "tsa.csr", "-CA", "ca.crt", "-CAkey", "ca.key", "-CAcreateserial",
       "-out", "tsa.crt", "-days", "2", "-extfile", "ext.cnf")
    (d / "serial").write_text("01\n")
    cfg = d / "tsa.cnf"
    cfg.write_text(f"""[ tsa ]
default_tsa = tsa_config1
[ tsa_config1 ]
dir = {d.as_posix()}
serial = $dir/serial
crypto_device = builtin
signer_cert = $dir/tsa.crt
signer_key = $dir/tsa.key
signer_digest = sha256
default_policy = 1.2.3.4.1
digests = sha256
accuracy = secs:1
ordering = yes
tsa_name = yes
ess_cert_id_alg = sha256
""")
    return cfg


class TsaHandler(http.server.BaseHTTPRequestHandler):
    cfg: Path = None
    workdir: Path = None

    def do_POST(self):  # noqa: N802
        q = self.workdir / "req.tsq"
        r = self.workdir / "resp.tsr"
        q.write_bytes(self.rfile.read(int(self.headers["Content-Length"])))
        subprocess.run([OPENSSL, "ts", "-reply", "-config", str(self.cfg), "-queryfile", str(q), "-out", str(r)],
                       check=True, capture_output=True)
        body = r.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "application/timestamp-reply")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a):
        pass


@unittest.skipUnless(OPENSSL, "OpenSSL >= 1.1.1 with the ts command is required")
class TestRfc3161(Base):
    def setUp(self):
        super().setUp()
        self.tsa_dir = self.tmp / "tsa"
        self.tsa_dir.mkdir()
        TsaHandler.cfg = make_tsa(self.tsa_dir)
        TsaHandler.workdir = self.tsa_dir
        self.server = http.server.HTTPServer(("127.0.0.1", 0), TsaHandler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.url = f"http://127.0.0.1:{self.server.server_port}/tsr"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        super().tearDown()

    def test_der_request_is_valid_for_openssl(self):
        tsq = self.tmp / "x.tsq"
        tsq.write_bytes(cs.build_tsq(hashlib.sha256(b"x").digest(), 12345))
        out = subprocess.run([OPENSSL, "ts", "-query", "-in", str(tsq), "-text"], capture_output=True, text=True)
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("sha256", out.stdout)
        self.assertIn("Certificate required: yes", out.stdout)

    def test_seal_with_timestamp_and_signature(self):
        run("add", self.case, self.ev)
        self.assertEqual(run("seal", self.case, "--tsa", self.url), 0)
        types = [e["type"] for e in cs.Case(self.case).entries()]
        self.assertEqual(types[-2:], ["seal", "timestamp"])
        res = cs.verify_case(cs.Case(self.case), tsa_ca=self.tsa_dir / "ca.crt")
        self.assertTrue(res.ok, res.seals)
        t = res.seals[0]["tsr"]
        self.assertTrue(t["imprint_ok"])
        self.assertEqual(t["signature"], "valid")
        self.assertTrue(t["gen_time"].endswith("Z"))

    def test_tampered_seal_detected_by_timestamp(self):
        run("seal", self.case, "--tsa", self.url)
        seal = next((self.case / "seals").glob("*.json"))
        seal.write_text(seal.read_text().replace('"sealed_by": "Tester"', '"sealed_by": "Mallory"'))
        res = cs.verify_case(cs.Case(self.case), check_files=False)
        self.assertFalse(res.seals[0]["tsr"]["imprint_ok"])
        self.assertFalse(res.ok)

    def test_unreachable_tsa_keeps_local_seal(self):
        self.assertEqual(run("seal", self.case, "--tsa", "http://127.0.0.1:9/none"), 1)
        self.assertEqual(len(list((self.case / "seals").glob("*.json"))), 1)
        self.assertEqual(run("verify", self.case), 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
