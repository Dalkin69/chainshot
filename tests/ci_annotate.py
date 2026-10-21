# SPDX-License-Identifier: MIT
"""CI helper: turns unittest / node:test failures into GitHub annotations.

    python tests/ci_annotate.py failures <logfile>
"""
import re
import sys


def esc(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def failures(log_path: str) -> None:
    text = open(log_path, encoding="utf-8", errors="replace").read()
    blocks = re.findall(r"^={20,}\n((?:FAIL|ERROR):.*?)(?=^={20,}|^-{20,}\nRan )", text, flags=re.S | re.M)
    if not blocks:
        blocks = [text[-6000:]]
    for b in blocks[:9]:
        title = b.splitlines()[0][:120]
        print(f"::error title={esc(title)}::{esc(b[-6000:])}")


if __name__ == "__main__":
    failures(sys.argv[2] if len(sys.argv) > 2 and sys.argv[1] == "failures" else sys.argv[1])
