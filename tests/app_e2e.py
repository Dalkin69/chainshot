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
SKIPPED = set()
TSA_URL = None


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
    global TSA_URL  # the server stays up for the demo case (daemon thread)
    if not pytests.OPENSSL:
        print("  (skipped: OpenSSL >= 1.1.1 not found)")
        SKIPPED.add("seal_with_local_rfc3161_tsa")
        return
    tsa = OUT / "tsa"
    tsa.mkdir(exist_ok=True)
    pytests.TsaHandler.cfg = pytests.make_tsa(tsa)
    pytests.TsaHandler.workdir = tsa
    server = http.server.HTTPServer(("127.0.0.1", 0), pytests.TsaHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = TSA_URL = f"http://127.0.0.1:{server.server_port}/tsr"
    code, out = run("seal", CASE, "--tsa", url)
    assert "RFC 3161 timestamp:" in out, out
    code, out = run("verify", CASE)
    assert "imprint OK" in out, out
    ts_entry = [json.loads(x) for x in (CASE / "journal.jsonl").read_text(encoding="utf-8").splitlines()][-1]
    assert ts_entry["type"] == "timestamp" and "(local clock " in ts_entry["note"], ts_entry
    # The token signature is checked by the reference implementation.
    assert py_verify(CASE, "--tsa-ca", str(tsa / "ca.crt")) == 0
    code, out = run("seal", CASE, "--tsa", "http://127.0.0.1:9/tsr", ok=(1,))
    assert "timestamp failed" in out, out


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


def native(*messages, timeout=60):
    """Talk to ChainShot.exe as the browser does (Chrome native messaging framing)."""
    import struct
    data = b"".join(struct.pack("<I", len(b)) + b for b in (json.dumps(m).encode() for m in messages))
    p = subprocess.run([str(EXE), "chrome-extension://dcccfnbiglhpbcdhbmokfilfgpfnmikf/", "--parent-window=0"],
                       input=data, capture_output=True, timeout=timeout, env=ENV)
    assert p.returncode == 0, p.stderr
    out, replies = p.stdout, []
    while out:
        (n,) = struct.unpack("<I", out[:4])
        replies.append(json.loads(out[4:4 + n]))
        out = out[4 + n:]
    return replies


@test
def browser_link_host_protocol():
    import base64
    from test_v12 import tiny_png
    d = OUT / "case-link"
    run("new-case", d, "--case", "LINK", "--examiner", "Analyst")
    png = base64.b64encode(tiny_png()).decode()
    now = cs.fmt_utc(cs.utc_now())
    hello, tag, cap, stale, bad, note, ver = native(
        {"type": "hello"},
        {"type": "set_tag", "tag": "W7"},
        {"type": "capture", "png_base64": png, "utc": now, "expected_seq": 2, "url": "https://example.org/",
         "title": "Example é", "tag": "W7", "note": "from the browser", "source": "chainshot-ext/1.2.0",
         "extra": {"viewport": "4x3", "user_agent": "UA"}},
        {"type": "capture", "png_base64": png, "utc": now, "expected_seq": 2},
        {"type": "capture", "png_base64": "bm90IGEgcG5n", "expected_seq": 3},
        {"type": "note", "text": "noted in the browser"},
        {"type": "verify"},
    )
    [arch] = native({"type": "capture", "format": "mhtml", "url": "https://example.org/", "tag": "W7",
                     "data_base64": base64.b64encode(b"From: <Saved by Blink>\r\nMIME-Version: 1.0\r\n\r\nx").decode()})
    assert arch["ok"] and arch["entry"]["file"].endswith("_W7.mhtml"), arch
    assert hello["ok"] and hello["case"]["next_seq"] == 2 and hello["case"]["name"] == "LINK", hello
    assert tag["tag"] == "W7", tag
    assert cap["ok"] and cap["entry"]["seq"] == "2" and cap["entry"]["utc"] == now, cap
    assert stale["ok"] is False and stale["retry"] is True and stale["case"]["next_seq"] == 3, stale
    assert bad["ok"] is False and "PNG" in bad["error"], bad
    assert note["ok"] and note["entry"]["seq"] == "3", note
    assert ver["intact"] is True and ver["entries"] == 3, ver
    entries = [json.loads(x) for x in (d / "journal.jsonl").read_text(encoding="utf-8").splitlines()]
    assert entries[1]["type"] == "browser" and entries[1]["title"] == "Example é" and entries[1]["tag"] == "W7", entries[1]
    assert entries[1]["source"] == "chainshot-ext/1.2.0" and entries[1]["extra"]["via"].startswith("chainshot-app/"), entries[1]
    assert entries[2]["tag"] == "W7", "a note without tag gets the current tag"
    assert py_verify(d) == 0
    assert hello["protocol"] == 2, hello


@test
def browser_link_firefox_launch():
    import struct
    data = json.dumps({"type": "hello"}).encode()
    p = subprocess.run([str(EXE), str(CONFIG / "com.chainshot.host.firefox.json"), "chainshot@dalkin69"],
                       input=struct.pack("<I", len(data)) + data, capture_output=True, timeout=60, env=ENV)
    (n,) = struct.unpack("<I", p.stdout[:4])
    assert json.loads(p.stdout[4:4 + n])["ok"] is True, p.stdout


@test
def browser_link_registration():
    import winreg
    code, out = run("register-browser", "--extension-id", "abcdefghijklmnopabcdefghijklmnop")
    try:
        for key in (r"Software\Google\Chrome", r"Software\Microsoft\Edge", r"Software\BraveSoftware\Brave-Browser"):
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key + r"\NativeMessagingHosts\com.chainshot.host") as k:
                manifest = Path(winreg.QueryValueEx(k, "")[0])
        m = json.loads(manifest.read_text(encoding="utf-8"))
        assert Path(m["path"]) == EXE, m
        assert "chrome-extension://dcccfnbiglhpbcdhbmokfilfgpfnmikf/" in m["allowed_origins"], m
        assert "chrome-extension://ejbdnnlgcnogepeclpknfagfhehkhgap/" in m["allowed_origins"], "Chrome Web Store ID"
        assert "chrome-extension://abcdefghijklmnopabcdefghijklmnop/" in m["allowed_origins"], m
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Mozilla\NativeMessagingHosts\com.chainshot.host") as k:
            ff = json.loads(Path(winreg.QueryValueEx(k, "")[0]).read_text(encoding="utf-8"))
        assert ff["allowed_extensions"] == ["chainshot@dalkin69"] and Path(ff["path"]) == EXE, ff
    finally:
        run("register-browser", "--remove")
    try:
        winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Google\Chrome\NativeMessagingHosts\com.chainshot.host")
        raise AssertionError("key still present after --remove")
    except FileNotFoundError:
        pass


@test
def evidence_package():
    import zipfile
    out = OUT / "packages" / "app-case.zip"
    code, text = run("package", CASE, "--out", out, "--seal")
    assert out.exists(), text
    with zipfile.ZipFile(out) as z:
        names = z.namelist()
        assert "chainshot.py" in names and "VERIFY.txt" in names and "SHA256SUMS.txt" in names, names
        assert not any(n.endswith(".journal.lock") for n in names), names
        for line in z.read("SHA256SUMS.txt").decode().splitlines():
            sha, name = line.split("  ", 1)
            assert cs.sha256_hex(z.read(name)) == sha, name
        assert z.read("chainshot.py") == (REPO / "python" / "chainshot.py").read_bytes(), "embedded verifier is the repo's"
        x = OUT / "package-extracted"
        z.extractall(x)
    p = subprocess.run([sys.executable, str(x / "chainshot.py"), "verify", str(x / CASE.name)], capture_output=True, text=True)
    assert p.returncode == 0, p.stdout + p.stderr
    last = json.loads((CASE / "journal.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    assert last["tag"] == "PACKAGE" and cs.file_hashes(out)[0] in last["note"], last


@test
def demo_case_screenshot():
    """Clean cases for the README screenshots (docs/img/app-*.png)."""
    import time
    d = OUT / "demo"
    run("new-case", d, "--case", "DEMO-01 Workstation PC-042", "--examiner", "Analyst")
    image = OUT / "PC-042.E01"
    image.write_bytes(b"ChainShot demo - not a real disk image\n" * 2000)
    run("add", image, "--copy", "--tag", "P00", "--note", "Disk image received from the client (demo file)")
    run("note", "Write blocker attached, acquisition hash checked against the chain of custody form", "--tag", "P01")
    run("capture", "--rect", "0,0,480,300", "--tag", "P02", "--note", "Desktop at the start of the analysis")
    # A neutral web page in an Edge window, captured as a window like an examiner would.
    edge = next((p for p in (Path(os.environ.get("ProgramFiles(x86)", "")) / "Microsoft/Edge/Application/msedge.exe",
                             Path(os.environ.get("ProgramFiles", "")) / "Microsoft/Edge/Application/msedge.exe") if p.exists()), None)
    browser = None
    if edge:
        page = (REPO / "docs" / "demo" / "activity.html").resolve().as_uri()
        browser = subprocess.Popen([str(edge), f"--app={page}", "--window-size=900,560", "--window-position=60,60",
                                    "--no-first-run", "--disable-features=msEdgeSidebarV2", f"--user-data-dir={OUT / 'edge-profile'}"])
        time.sleep(6)
    try:
        code, out = run("capture", "--mode", "window" if edge else "screen", "--tag", "P03",
                        "--note", "Sign-in history of the user account", ok=(0, 2))
        if code != 0:
            run("capture", "--mode", "screen", "--tag", "P03", "--note", "Sign-in history of the user account")
    finally:
        if browser:
            browser.kill()
    run("note", "New device and password change at 02:41 UTC: to be confirmed with the user", "--tag", "P04")
    if pytests.OPENSSL and TSA_URL:
        run("seal", d, "--tsa", TSA_URL)
    else:
        run("seal", d)
    run("note", "Report delivered to the client", "--tag", "END")
    run("tag", "P05")
    _, out = run("tag")
    assert out.strip().endswith("P05"), out
    shots = OUT / "shots"
    png = OUT / "app-demo.png"
    code, out = run("--smoke-test", png, "--case", d, "--shots", shots, timeout=120)
    info = json.loads(out.strip().splitlines()[-1])
    assert "intact" in info["chip"], info  # the full verification finished and passed
    assert info["preview"] is True, info
    assert (shots / "app-new-case.png").exists() and (shots / "app-tray-menu.png").exists()
    # The same case after a capture was edited: what the examiner (or the client) sees.
    bad = OUT / "demo-altered"
    shutil.copytree(d, bad)
    shot = sorted((bad / "captures").glob("*_P03.png"))[0]
    with open(shot, "r+b") as fh:  # change pixels, not just the size
        fh.seek(200)
        fh.write(b"\x00" * 64)
    code, out = run("--smoke-test", OUT / "app-altered.png", "--case", bad, timeout=120)
    info = json.loads(out.strip().splitlines()[-1])
    assert "FAILED" in info["chip"] or "ALTERED" in info["chip"], info


@test
def uninstall_removes_what_the_app_added():
    import winreg
    run("register-browser")
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as k:
        winreg.SetValueEx(k, "ChainShot", 0, winreg.REG_SZ, f'"{EXE}" --minimized')
    code, out = run("uninstall")
    assert "Case folders were not touched" in out, out
    for key in (r"Software\Google\Chrome\NativeMessagingHosts\com.chainshot.host",
                r"Software\Mozilla\NativeMessagingHosts\com.chainshot.host"):
        try:
            winreg.OpenKey(winreg.HKEY_CURRENT_USER, key)
            raise AssertionError(f"{key} still present")
        except FileNotFoundError:
            pass
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as k:
        try:
            winreg.QueryValueEx(k, "ChainShot")
            raise AssertionError("Run value still present")
        except FileNotFoundError:
            pass
    assert not (CONFIG / "state.json").exists(), "settings removed"
    assert (CASE / "journal.jsonl").exists(), "cases untouched"


failed = [(n, tb) for n, tb in results if tb]
skips = [name for name, tb in results if not tb and name in SKIPPED]
if skips:
    print(f"::notice title=ChainShot.exe e2e skipped::{', '.join(skips)}")
print(f"::notice title=ChainShot.exe e2e::passed={len(results) - len(failed)} failed={len(failed)}")
for name, tb in failed:
    print(f"::error title=app e2e {name}::{esc(tb[-3500:])}")
sys.exit(1 if failed else 0)
