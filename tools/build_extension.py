#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Builds the browser extension packages from extension/:

  dist/extension/            Chromium, unpacked (same as the source folder, with its key)
  dist/extension-store/      Chrome Web Store / Edge Add-ons: no "key" (the store assigns the ID)
  dist/extension-firefox/    Firefox: background script instead of service worker, gecko ID,
                             data collection declaration, single classic script (no module import)

    python tools/build_extension.py [--out dist] [--zip VERSION]
"""
import argparse
import json
import re
import shutil
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "extension"
GECKO_ID = "chainshot@dalkin69"   # also in app/src/BrowserLink.cs and python/chainshot.py
FIREFOX_MIN = "140.0"             # data_collection_permissions (Firefox built-in consent)


def copy_tree(dst: Path) -> None:
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(SRC, dst, ignore=shutil.ignore_patterns("README.md"))
    shutil.copy2(ROOT / "LICENSE", dst / "LICENSE")


def write_manifest(dst: Path, manifest: dict) -> None:
    (dst / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def bundle_background(dst: Path) -> None:
    """Firefox: inline lib/chain.js into background.js as one classic script."""
    chain = (SRC / "lib" / "chain.js").read_text(encoding="utf-8")
    chain = re.sub(r"^export\s+", "", chain, flags=re.M)
    bg = (SRC / "background.js").read_text(encoding="utf-8")
    bg, n = re.subn(r"^import\s*\{[^}]*\}\s*from\s*'\./lib/chain\.js';\n", "", bg, flags=re.M)
    if n != 1:
        raise SystemExit("background.js: expected exactly one import from ./lib/chain.js")
    body = "// Built by tools/build_extension.py for Firefox: lib/chain.js inlined.\n" + chain + "\n" + bg
    (dst / "background.js").write_text("(() => {\n" + body + "\n})();\n", encoding="utf-8")


def build(out: Path) -> dict:
    base = json.loads((SRC / "manifest.json").read_text(encoding="utf-8"))
    dirs = {}

    d = out / "extension"
    copy_tree(d)
    dirs["chromium"] = d

    d = out / "extension-store"
    copy_tree(d)
    m = dict(base)
    m.pop("key", None)
    write_manifest(d, m)
    dirs["store"] = d

    d = out / "extension-firefox"
    copy_tree(d)
    m = dict(base)
    for k in ("key", "minimum_chrome_version"):
        m.pop(k, None)
    m["background"] = {"scripts": ["background.js"]}
    # pageCapture (MHTML) does not exist in Firefox.
    m["optional_permissions"] = [p for p in m.get("optional_permissions", []) if p != "pageCapture"]
    if not m["optional_permissions"]:
        m.pop("optional_permissions")
    m["browser_specific_settings"] = {"gecko": {
        "id": GECKO_ID, "strict_min_version": FIREFOX_MIN,
        "data_collection_permissions": {"required": ["none"]},
    }}
    write_manifest(d, m)
    bundle_background(d)
    dirs["firefox"] = d
    return dirs


def zip_dir(src: Path, target: Path) -> None:
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(src.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(src).as_posix())


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="dist")
    ap.add_argument("--zip", metavar="VERSION", help="also write the release zips, e.g. v1.3.0")
    a = ap.parse_args()
    out = Path(a.out)
    dirs = build(out)
    if a.zip:
        names = {"chromium": "chainshot-extension", "store": "chainshot-extension-store", "firefox": "chainshot-extension-firefox"}
        for key, d in dirs.items():
            zip_dir(d, out / f"{names[key]}-{a.zip}.zip")
    for key, d in dirs.items():
        print(f"{key:9} {d}")


if __name__ == "__main__":
    main()
