# SPDX-License-Identifier: MIT
"""
CI helper: turn test output into GitHub annotations so that failures can be read
through the GitHub API (annotations) without downloading the raw logs.

    python tests/ci_annotate.py failures <logfile>      # unittest FAIL/ERROR blocks
    python tests/ci_annotate.py thumbnail <png> <title> [full]
        small JPEG preview as base64 notices: the bottom strip (banner) or the whole image
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


def thumbnail(png_path: str, title: str, mode: str = "strip", part: str = "1/1") -> None:
    """Emit a JPEG preview as base64 chunks: the bottom strip of a capture (where the banner
    is) or, with mode "full", the whole image.

    Annotation messages are cut at 4096 characters and a step keeps at most 10 notices,
    so the image is sent as numbered chunks of 4000 characters, at most 9 per step. A large
    image can be spread over several steps with part "1/3", "2/3", "3/3".
    """
    from PIL import Image  # installed by the workflow step right before this call
    k, parts = (int(x) for x in part.split("/"))
    budget = 9 * parts * 4000
    full = Image.open(png_path).convert("RGB")
    if mode == "full":
        widths = [w for w in (full.width, 900, 760, 640, 520) if w <= full.width]
        candidates = [full if w == full.width else full.resize((w, round(full.height * w / full.width)), Image.LANCZOS) for w in widths]
    else:
        candidates = [full.crop((0, max(0, full.height - 100), min(full.width, 1000), full.height))]
    b64 = ""
    for img in candidates:
        for quality in (85, 70, 55, 40, 25):
            buf = io.BytesIO()
            img.save(buf, "JPEG", quality=quality)
            b64 = base64.b64encode(buf.getvalue()).decode()
            if len(b64) <= budget:
                break
        if len(b64) <= budget:
            break
    chunks = [b64[i:i + 4000] for i in range(0, len(b64), 4000)][:9 * parts]
    if k == 1:
        print(f"::notice title={esc(title)} size::{full.width}x{full.height}")
    for i in range((k - 1) * 9, min(k * 9, len(chunks))):
        print(f"::notice title={esc(title)} chunk {i + 1}/{len(chunks)}::{chunks[i]}")


if __name__ == "__main__":
    {"failures": failures, "thumbnail": thumbnail}[sys.argv[1]](*sys.argv[2:])
