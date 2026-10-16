# SPDX-License-Identifier: MIT
"""
CI helper: turn test output into GitHub annotations so that failures can be read
through the GitHub API (annotations) without downloading the raw logs.

    python tests/ci_annotate.py failures <logfile>      # unittest FAIL/ERROR blocks
    python tests/ci_annotate.py thumbnail <png> <title> # small JPEG preview as base64 notice
"""
import base64
import io
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


def thumbnail(png_path: str, title: str) -> None:
    # Pillow is not needed on Windows: this branch is only called where it is installed.
    from PIL import Image
    im = Image.open(png_path).convert("RGB")
    im.thumbnail((640, 640))
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=55)
    b64 = base64.b64encode(buf.getvalue()).decode()
    print(f"::notice title={esc(title)} ({im.width}x{im.height}, {len(b64)} b64 chars)::{b64}")


if __name__ == "__main__":
    {"failures": failures, "thumbnail": thumbnail}[sys.argv[1]](*sys.argv[2:])
