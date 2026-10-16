# SPDX-License-Identifier: MIT
"""
End-to-end tests of ChainShot.exe (Windows), driven from Python:

  * the C# implementation matches the shared vectors (selftest);
  * cases written by the exe verify with chainshot.py and with the PowerShell module;
  * cases written by chainshot.py verify with the exe;
  * real screen captures with the banner; tampering is detected;
  * seal + RFC 3161 timestamp against a local OpenSSL TSA (when OpenSSL >= 1.1.1 is present);
  * the window opens (smoke test) and a screenshot of it is saved.

    python tests/app_e2e.py <path/to/ChainShot.exe> [--out <dir>]

Failures are printed as GitHub annotations.
"""
import http.server
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import traceback
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "python"))
sys.path.insert(0, str(REPO / "python" / "tests"))
import chainshot as cs  # noqa: E402
import test_chainshot as pytests  # noqa: E402  (local TSA helpers)

EXE = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[sys.argv.index("--out") + 1]).resolve() if "--out" in sys.argv else Path(tempfile.mkdtemp(prefix="cs-app-"))
OUT.mkdir(parents=True, exist_ok=True)
CONFIG = OUT / "appdata" / "ChainShot"   # isolated settings: shared by the exe and the module
CONFIG.mkdir(parents=True, exist_ok=True)
ENV = dict(os.environ, CHAINSHOT_CONFIG_DIR=str(CONFIG), APPDATA=str(CONFIG.parent))
PWSH = shutil.which("pwsh") or shutil.which("powershell")

results = []


def esc(t: str) -> str:
    return t.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def run(*args, ok=(0,), timeout=120):
    p = subprocess.run([str(EXE), *map(str, args)], capture_output=True, timeout=timeout, env=ENV)
    out = p.stdout.decode("utf-8", "replace") + p.stderr.decode("utf-8", "replace")
    if ok is not None and p.returncode not in ok:
        raise AssertionError(f"ChainShot.exe {' '.join(map(str, args))} -> exit {p.returncode}\n{out}")
    return p.returncode, out


def py_verify(case, *extra):
    return cs.main(["verify", str(case), *extra])


def ps(script: str):
    if not PWSH:
        return None
    module = REPO / "windows" / "ChainShot" / "ChainShot.psd1"
    p = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", f"Import-Module '{module}'; {script}"],
                       capture_output=True, timeout=180, env=ENV)
    return p.stdout.decode("utf-8", "replace").strip() + p.stderr.decode("utf-8", "replace").strip()


def test(fn):
    try:
        fn()
        results.append((fn.__name__, None))
        print(f"PASS {fn.__name__}")
    except Exception:  # noqa: BLE001
        tb = traceback.format_exc()
        results.append((fn.__name__, tb))
        print(f"FAIL {fn.__name__}\n{tb}")
    return fn


@test
def selftest_vectors():
    code, out = run("selftest", REPO / "tests" / "vectors" / "canonical-vectors.json")
    assert "vectors OK" in out, out


@test
def version_and_help():
    _, out = run("--version")
    assert out.strip().startswith("chainshot-app/"), out
    _, out = run("--help")
    assert "new-case" in out and "capture" in out, out
    code, _ = run("no-such-command", ok=(2,))


CASE = OUT / "case-app"


@test
def case_written_by_exe_verifies_everywhere():
    run("new-case", CASE, "--case", "TEST-APP é", "--examiner", "Tester")
    state = json.loads((CONFIG / "state.json").read_text(encoding="utf-8-sig"))
    assert Path(state["CasePath"]) == CASE, state
    ev = CASE / "files" / "evidence.txt"
    ev.parent.mkdir(exist_ok=True)
    ev.write_text("evidence", encoding="utf-8")
    outside = OUT / "outside.bin"
    outside.write_bytes(b"\x00\x01binary")
    run("add", ev, "--tag", "P01", "--note", 'quote " backslash \\ tab \t')
    run("add", outside, "--copy", "--tag", "P02")
    assert (CASE / "files" / "outside.bin").exists()
    run("note", "multi\r\nline note ü", "--tag", "P03")
    # Real captures on the runner's desktop.
    run("capture", "--rect", "0,0,320,200", "--tag", "P04", "--note", "fixed rectangle")
    run("capture", "--mode", "screen", "--tag", "P05")
    run("capture", "--mode", "all")
    run("capture", "--mode", "window", ok=(0, 2))  # a runner may have no foreground window
    run("seal")
    run("note", "after the seal")
    code, out = run("verify")
    assert "ALL EVIDENCE INTACT" in out, out
    assert py_verify(CASE) == 0
    entries = [json.loads(line) for line in (CASE / "journal.jsonl").read_text(encoding="utf-8").splitlines()]
    shot = next(e for e in entries if e["tag"] == "P04")
    assert shot["extra"]["bounds"] == "0,0,320,200", shot
    assert shot["source"].startswith("chainshot-app/"), shot
    assert shot["file"].startswith("captures/0005_"), shot["file"]
    res = ps(f"Test-ChainShotCase -CasePath '{CASE}' -Quiet")
    if res is not None:
        assert res.endswith("True"), res


@test
def shared_state_with_powershell_module():
    res = ps("(Get-ChainShotStatus).CasePath")
    if res is None:
        return
    assert Path(res.splitlines()[-1]) == CASE, res
    ps("Set-ChainShotTag -Tag 'PS-TAG' | Out-Null")
    run("note", "tag comes from the module")
    last = json.loads((CASE / "journal.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    assert last["tag"] == "PS-TAG", last
    # And entries written by the module verify with the exe.
    ps(f"Add-ChainShotNote -CasePath '{CASE}' -Text 'from the module' | Out-Null")
    code, out = run("verify", CASE)
    assert "ALL EVIDENCE INTACT" in out, out


@test
def capture_has_banner_below_pixels():
    from PIL import Image
    entries = [json.loads(line) for line in (CASE / "journal.jsonl").read_text(encoding="utf-8").splitlines()]
    shot = next(e for e in entries if e["tag"] == "P04")
    img = Image.open(CASE / shot["file"])
    assert img.width == 320 and img.height > 200, img.size
    # The banner area is black with yellow text.
    banner = img.convert("RGB").crop((0, 200, 320, img.height))
    colors = banner.getcolors(1 << 20)
    assert any(c == (0, 0, 0) for _, c in colors), "no black banner"
    assert any(r > 200 and g > 200 and b < 80 for _, (r, g, b) in colors), "no yellow banner text"


@test
def case_written_by_python_verifies_with_exe():
    d = OUT / "case-py"
    assert cs.main(["init", str(d), "--case", "PYCASE", "--examiner", "Py"]) == 0
    assert cs.main(["note", str(d), "from python", "--tag", "X"]) == 0
    assert cs.main(["seal", str(d)]) == 0
    code, out = run("verify", d)
    assert "ALL EVIDENCE INTACT" in out, out


@test
def tampering_is_detected():
    d = OUT / "case-tamper"
    shutil.copytree(CASE, d)
    code, out = run("verify", d, "--json", ok=(0,))
    assert json.loads(out)["ok"] is True
    j = d / "journal.jsonl"
    lines = j.read_text(encoding="utf-8").splitlines(keepends=True)
    j.write_text("".join(lines[:3] + [lines[3].replace("multi", "MULTI")] + lines[4:]), encoding="utf-8")
    code, out = run("verify", d, ok=(1,))
    assert "CHAIN BROKEN" in out, out
    j.write_text("".join(lines), encoding="utf-8")
    (d / "files" / "evidence.txt").write_text("changed", encoding="utf-8")
    code, out = run("verify", d, ok=(1,))
    assert "FILE HASH MISMATCH" in out, out
    code, out = run("seal", d, ok=(2,))
    assert "refusing to seal" in out, out


@test
def seal_with_local_rfc3161_tsa():
    if not pytests.OPENSSL:
        print("  (skipped: OpenSSL >= 1.1.1 not found)")
        return
    tsa = OUT / "tsa"
    tsa.mkdir(exist_ok=True)
    pytests.TsaHandler.cfg = pytests.make_tsa(tsa)
    pytests.TsaHandler.workdir = tsa
    server = http.server.HTTPServer(("127.0.0.1", 0), pytests.TsaHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        url = f"http://127.0.0.1:{server.server_port}/tsr"
        code, out = run("seal", CASE, "--tsa", url)
        assert "RFC 3161 timestamp:" in out, out
        code, out = run("verify", CASE)
        assert "imprint OK" in out, out
        # The token signature is checked by the reference implementation.
        assert py_verify(CASE, "--tsa-ca", str(tsa / "ca.crt")) == 0
        code, out = run("seal", CASE, "--tsa", "http://127.0.0.1:9/tsr", ok=(1,))
        assert "timestamp failed" in out, out
    finally:
        server.shutdown()
        server.server_close()


@test
def html_report():
    code, out = run("report", CASE)
    html = (CASE / "report" / "index.html").read_text(encoding="utf-8")
    assert "All evidence verified" in html and "TEST-APP é" in html, out
    assert 'src="../captures/' in html, "thumbnails should use paths relative to the report"


@test
def gui_smoke_test():
    png = OUT / "app-window.png"
    code, out = run("--smoke-test", png, "--case", CASE, timeout=90)
    info = json.loads(out.strip().splitlines()[-1])
    assert int(info["rows"]) >= 10, info
    assert int(info["menu_items"]) > 15, info
    assert png.exists() and png.stat().st_size > 10000, png
    print("  smoke:", info)
    if info["hotkeys_failed"]:
        print(f"::warning title=Hotkeys::not registered: {info['hotkeys_failed']}")


failed = [(n, tb) for n, tb in results if tb]
print(f"::notice title=ChainShot.exe e2e::passed={len(results) - len(failed)} failed={len(failed)}")
for name, tb in failed:
    print(f"::error title=app e2e {name}::{esc(tb[-3500:])}")
sys.exit(1 if failed else 0)
