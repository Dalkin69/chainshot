# SPDX-License-Identifier: MIT
"""Draws the ChainShot logo and writes every icon used by the project.

    python tools/make_icon.py
Outputs: extension/icons/icon{16,32,48,128}.png and windows/ChainShot/chainshot.ico
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]


def logo(size: int) -> Image.Image:
    s = 512
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    navy, gold = (43, 76, 126, 255), (232, 183, 38, 255)
    d.rounded_rectangle((24, 120, 488, 456), radius=64, fill=navy)      # camera body
    d.rounded_rectangle((168, 64, 344, 150), radius=28, fill=navy)      # viewfinder
    d.ellipse((150, 176, 362, 388), outline=gold, width=40)             # lens ring
    d.ellipse((222, 248, 290, 316), fill=gold)                          # lens
    d.rounded_rectangle((392, 160, 452, 196), radius=12, fill=(255, 255, 255, 200))
    return im.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    for n in (16, 32, 48, 128):
        logo(n).save(ROOT / "extension" / "icons" / f"icon{n}.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    logo(256).save(ROOT / "windows" / "ChainShot" / "chainshot.ico", sizes=[(n, n) for n in sizes])
    print("icons written")
