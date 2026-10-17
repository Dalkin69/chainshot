# SPDX-License-Identifier: MIT
"""
End-to-end test of the Firefox build of the extension (Selenium + geckodriver):
capture, full page capture, journal on disk verified by chainshot.py, popup, and the
browser link with chainshot.py registered as Firefox native messaging host.

    python tools/build_extension.py --out dist
    python tests/firefox_e2e.py dist/extension-firefox

Needs Firefox 140+, geckodriver and `pip install selenium`. Failures are printed as
GitHub annotations.
"""
import http.server
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from pathlib import Path

from selenium import webdriver
from selenium.webdriver.firefox.options import Options
from selenium.webdriver.firefox.service import Service

REPO = Path(__file__).resolve().parents[1]
PY = REPO / "python" / "chainshot.py"
GECKO_ID = "chainshot@dalkin69"
UUID = "6b1d2c7e-9a3f-4c1e-8d2b-0c5e7f9a1b23"   # fixed moz-extension:// host for the test
SRC = Path(sys.argv[1]).resolve()
TMP = Path(tempfile.mkdtemp(prefix="chainshot-ff-"))
DL = TMP / "downloads"
CFG = TMP / "config"
APP_CASE = TMP / "app-case"
ENV = dict(os.environ, CHAINSHOT_CONFIG_DIR=str(CFG))
results = []


def esc(t):
    return t.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def py(*args, ok=(0,)):
    p = subprocess.run([sys.executable, str(PY), *map(str, args)], capture_output=True, text=True, env=ENV)
    assert p.returncode in ok, p.stdout + p.stderr
    return p.returncode, p.stdout + p.stderr


def test(fn):
    try:
        fn()
        results.append((fn.__name__, None))
        print(f"PASS {fn.__name__}")
    except Exception:  # noqa: BLE001
        tb = traceback.format_exc()
        results.append((fn.__name__, tb))
        print(f"FAIL {fn.__name__}\n{tb}")


class Pages(http.server.BaseHTTPRequestHandler):
    def do_GET(self):  # noqa: N802
        if self.path == "/long":
            bands = "".join(f'<div style="height:300px;background:hsl({i * 36},70%,50%)">Section {i + 1}</div>' for i in range(10))
            body = ("<!doctype html><title>Long page</title><body style='margin:0'>"
                    "<div style='position:fixed;top:0;height:40px;width:100%;background:#000;color:#fff'>Fixed header</div>"
                    f"{bands}</body>")
        else:
            body = "<!doctype html><title>Firefox evidence é</title><body style='background:#2b4c7e;color:#fff'><h1>Demo page</h1></body>"
        data = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


# --------------------------------------------------------------------------- setup
def setup():
    global ext, server, BASE, driver, page_window, popup_window
    ext = TMP / "extension"
    shutil.copytree(SRC, ext)
    manifest = json.loads((ext / "manifest.json").read_text(encoding="utf-8"))
    manifest["host_permissions"] = ["<all_urls>"]   # captures without a user gesture, for the test only
    manifest["permissions"].append("tabs")
    (ext / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    DL.mkdir()
    # Browser link: Firefox looks for host manifests in ~/.mozilla/native-messaging-hosts.
    py("register-browser", "--firefox-dir", Path.home() / ".mozilla" / "native-messaging-hosts")

    server = http.server.HTTPServer(("127.0.0.1", 0), Pages)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    BASE = f"http://127.0.0.1:{server.server_port}"

    opts = Options()
    opts.add_argument("-headless")
    opts.set_preference("extensions.webextensions.uuids", json.dumps({GECKO_ID: UUID}))
    opts.set_preference("browser.download.folderList", 2)
    opts.set_preference("browser.download.dir", str(DL))
    opts.set_preference("browser.download.useDownloadDir", True)
    opts.set_preference("browser.download.alwaysOpenPanel", False)
    opts.set_preference("browser.download.manager.showWhenStarting", False)
    opts.set_preference("extensions.originControls.grantByDefault", True)
    driver = webdriver.Firefox(options=opts, service=Service(env=ENV))
    driver.set_window_size(1100, 760)
    driver.install_addon(str(ext), temporary=True)
    driver.set_script_timeout(90)
    print("Firefox", driver.capabilities.get("browserVersion"))

    driver.get(BASE + "/evidence")
    page_window = driver.current_window_handle
    driver.switch_to.new_window("window")
    driver.get(f"moz-extension://{UUID}/popup.html")
    popup_window = driver.current_window_handle




try:
    setup()
except Exception:  # noqa: BLE001 - report why the browser could not start
    print(f"::error title=firefox setup::{esc(traceback.format_exc()[-3500:])}")
    sys.exit(1)
print(f"::notice title=Firefox::{driver.capabilities.get('browserVersion')}")


def call(cmd, **args):
    """Message to the background script, from the extension page (like the popup does)."""
    driver.switch_to.window(popup_window)
    r = driver.execute_async_script("""
        const done = arguments[arguments.length - 1];
        browser.runtime.sendMessage(arguments[0]).then(done, (e) => done({ ok: false, error: String(e) }));
    """, {"cmd": cmd, **args})
    if not r or not r.get("ok"):
        raise AssertionError(f"{cmd}: {r}")
    return r["result"]


def page_tab(path):
    driver.switch_to.window(page_window)
    driver.get(BASE + path)
    time.sleep(0.5)
    driver.switch_to.window(popup_window)
    tabs = driver.execute_async_script("""
        const done = arguments[arguments.length - 1];
        browser.tabs.query({ url: arguments[0] }).then((t) => done(t.map((x) => x.id)), (e) => done(String(e)));
    """, BASE + path)
    assert isinstance(tabs, list) and tabs, tabs
    return tabs[0]


def case_dir():
    base = DL / "ChainShot"
    for _ in range(50):
        if base.exists() and any(base.iterdir()):
            return next(base.iterdir())
        time.sleep(0.2)
    raise AssertionError(f"no case folder in {DL}: {list(DL.rglob('*'))}")


def wait_lines(journal, n):
    for _ in range(50):
        if journal.exists() and len(journal.read_text(encoding="utf-8").splitlines()) >= n:
            return [json.loads(x) for x in journal.read_text(encoding="utf-8").splitlines()]
        time.sleep(0.2)
    raise AssertionError(f"{journal}: fewer than {n} lines")


def png_size(p):
    b = p.read_bytes()
    return int.from_bytes(b[16:20], "big"), int.from_bytes(b[20:24], "big")


# --------------------------------------------------------------------------- tests
@test
def capture_into_downloads_case():
    st = call("newCase", caseName="FIREFOX-01", examiner="Bot")
    assert st["case"]["entries"] == 1, st
    call("setSettings", tag="F1")
    tab = page_tab("/evidence")
    e = call("capture", tabId=tab, note="captured in Firefox")
    assert e["type"] == "browser" and e["tag"] == "F1" and e["title"] == "Firefox evidence é", e
    d = case_dir()
    lines = wait_lines(d / "journal.jsonl", 2)
    assert lines[1]["file"] == e["file"]
    w, h = png_size(d / e["file"])
    sw, sh = map(int, e["extra"]["viewport"].split("x"))
    assert w == sw and h > sh, (w, h, e["extra"])
    code, out = py("verify", d)
    assert "ALL EVIDENCE INTACT" in out, out


@test
def full_page_capture():
    tab = page_tab("/long")
    e = call("capture", tabId=tab, fullPage=True)
    assert e["extra"]["mode"] == "fullpage" and int(e["extra"]["parts"]) >= 2, e["extra"]
    d = case_dir()
    wait_lines(d / "journal.jsonl", 3)
    w, h = png_size(d / e["file"])
    css_w, css_h = map(int, e["extra"]["page"].split("x"))
    assert css_h == 3000 and h >= 3000 * w / css_w - 2, (w, h, e["extra"])
    code, out = py("verify", d)
    assert "ALL EVIDENCE INTACT" in out, out


@test
def mhtml_is_not_offered():
    st = call("status")
    assert st["mhtml"] is False, st


@test
def browser_link_with_chainshot_py():
    py("init", APP_CASE, "--case", "FF-APP", "--examiner", "Analyst")
    py("use", APP_CASE)
    st = call("setSettings", destination="app", tag="FA")
    assert st["app"]["connected"] and st["app"]["case"]["name"] == "FF-APP", st["app"]
    tab = page_tab("/evidence")
    e = call("capture", tabId=tab, note="linked")
    assert e["seq"] == "2" and e["destination"] == "app", e
    call("addNote", text="note from Firefox")
    v = call("verify")
    assert v["ok"] and v["count"] == 3, v
    lines = [json.loads(x) for x in (APP_CASE / "journal.jsonl").read_text(encoding="utf-8").splitlines()]
    assert lines[1]["source"].startswith("chainshot-ext/") and lines[1]["tag"] == "FA", lines[1]
    code, out = py("verify", APP_CASE)
    assert "ALL EVIDENCE INTACT" in out, out
    driver.switch_to.window(popup_window)
    driver.refresh()
    for _ in range(50):
        text = driver.find_element("id", "caseLine").text
        if "ChainShot app" in text:
            break
        time.sleep(0.2)
    assert "FF-APP - 3 entries (ChainShot app)" in text, text
    call("setSettings", destination="downloads")


driver.quit()
server.shutdown()
failed = [(n, tb) for n, tb in results if tb]
print(f"::notice title=Firefox extension e2e::passed={len(results) - len(failed)} failed={len(failed)}")
for name, tb in failed:
    print(f"::error title=firefox {name}::{esc(tb[-3500:])}")
sys.exit(1 if failed else 0)
