# SPDX-License-Identifier: MIT
"""Checks that every component declares the same version (run in CI and by the release).

    python tests/check_versions.py           # exit 1 and list the differences
    python tests/check_versions.py --print   # print the common version
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def grab(path, pattern):
    m = re.search(pattern, (ROOT / path).read_text(encoding="utf-8"), re.M)
    return m.group(1) if m else None


found = {
    "python/chainshot.py": grab("python/chainshot.py", r'^__version__ = "([^"]+)"'),
    "python/pyproject.toml": grab("python/pyproject.toml", r'^version = "([^"]+)"'),
    "windows/ChainShot/ChainShot.psd1": grab("windows/ChainShot/ChainShot.psd1", r"ModuleVersion\s*=\s*'([^']+)'"),
    "windows/ChainShot/ChainShot.psm1": grab("windows/ChainShot/ChainShot.psm1", r"^\$script:Version\s*=\s*'([^']+)'"),
    "extension/manifest.json": json.loads((ROOT / "extension/manifest.json").read_text(encoding="utf-8"))["version"],
    "app/ChainShot.csproj": grab("app/ChainShot.csproj", r"<Version>([^<]+)</Version>"),
    "app/src/AppInfo.cs": grab("app/src/AppInfo.cs", r'Version = "([^"]+)"'),
    "windows/ChainShot/ChainShotTray.cs": (grab("windows/ChainShot/ChainShotTray.cs", r'AssemblyFileVersion\("([^"]+)\.0"\)') or ""),
    "app/app.manifest": (grab("app/app.manifest", r'assemblyIdentity version="([^"]+)\.0"') or ""),
}
versions = set(found.values())
if len(versions) != 1:
    for k, v in found.items():
        print(f"{v!s:10} {k}")
    print("::error::version numbers differ between components")
    sys.exit(1)
print(versions.pop() if "--print" in sys.argv else f"all components at version {versions.pop()}")
