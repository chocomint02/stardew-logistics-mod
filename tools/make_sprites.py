#!/usr/bin/env python3
"""Generates the mod's big-craftable spritesheet.

The sheet is 128x32: six 16x32 sprites laid out left to right, indexed by the
``SpriteIndex`` values in ``Integrations/ContentInjector.cs``:

    0 cable   1 controller   2 terminal   3 crafting terminal   4 import bus   5 export bus

Edit the drawing calls below and re-run. Uses only the standard library, so it
needs no Pillow or other image dependency.

Usage:
    python3 tools/make_sprites.py              # write src/StardewLogistics/assets/craftables.png
    python3 tools/make_sprites.py --preview    # also write an 8x preview to tools/preview.png
"""

import os
import sys

# Write relative to the repo root, wherever the script is run from.
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "craftables.png")

import zlib
import struct

W, H = 128, 32
px = [[(0,0,0,0) for _ in range(W)] for _ in range(H)]

OUT   = (34, 30, 44, 255)
LIGHT = (156, 162, 178, 255)
MID   = (108, 115, 136, 255)
DARK  = (72, 78, 96, 255)
CYAN  = (92, 222, 240, 255)
CYAND = (44, 140, 168, 255)
AMBER = (246, 192, 92, 255)
AMBRD = (196, 132, 44, 255)
GREEN = (112, 220, 124, 255)
ORANGE= (242, 150, 68, 255)
SCREEN= (26, 42, 60, 255)
COPPER= (196, 124, 72, 255)

def p(ox, x, y, c):
    X = ox + x
    if 0 <= X < W and 0 <= y < H:
        px[y][X] = c

def rect(ox, x0, y0, x1, y1, c):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            p(ox, x, y, c)

def box(ox, x0, y0, x1, y1, fill, light, dark):
    """A bevelled metal panel with an outline."""
    rect(ox, x0, y0, x1, y1, OUT)
    rect(ox, x0 + 1, y0 + 1, x1 - 1, y1 - 1, fill)
    rect(ox, x0 + 1, y0 + 1, x1 - 1, y0 + 1, light)
    rect(ox, x0 + 1, y0 + 1, x0 + 1, y1 - 1, light)
    rect(ox, x0 + 1, y1 - 1, x1 - 1, y1 - 1, dark)
    rect(ox, x1 - 1, y0 + 1, x1 - 1, y1 - 1, dark)

# ---- 0: Logistics Cable -------------------------------------------------
o = 0
# a vertical branch first, so the horizontal run draws over its join
box(o, 6, 13, 10, 24, MID, LIGHT, DARK)
rect(o, 8, 14, 8, 23, CYAN)
# horizontal conduit spanning the whole tile
box(o, 0, 20, 15, 28, MID, LIGHT, DARK)
# glowing core running end to end
rect(o, 1, 24, 14, 24, CYAN)
rect(o, 1, 23, 14, 23, CYAND)
# collars at the two ends only, so the run stays readable
for cx in (1, 13):
    rect(o, cx, 19, cx + 1, 29, OUT)
    rect(o, cx, 20, cx + 1, 28, COPPER)

# ---- 1: Logistics Controller -------------------------------------------
o = 16
box(o, 1, 6, 14, 31, MID, LIGHT, DARK)
# recessed core
rect(o, 4, 11, 11, 22, OUT)
rect(o, 5, 12, 10, 21, CYAND)
rect(o, 6, 13, 9, 20, CYAN)
rect(o, 7, 15, 8, 18, (230, 252, 255, 255))
# vents
for vy in (25, 27, 29):
    rect(o, 3, vy, 12, vy, DARK)
# corner bolts
for bx, by in ((3, 8), (12, 8)):
    p(o, bx, by, LIGHT)

# ---- 2: Storage Terminal ------------------------------------------------
o = 32
# pedestal
box(o, 3, 24, 12, 31, MID, LIGHT, DARK)
rect(o, 5, 31, 10, 31, OUT)
# angled screen housing
box(o, 1, 7, 14, 25, MID, LIGHT, DARK)
rect(o, 3, 9, 12, 21, OUT)
rect(o, 4, 10, 11, 20, SCREEN)
# rows of "items" on the screen
for ry in (12, 15, 18):
    for rx in (5, 7, 9):
        rect(o, rx, ry, rx + 1, ry + 1, CYAN)
# status light
p(o, 13, 23, CYAN)

# ---- 3: Crafting Terminal ----------------------------------------------
o = 48
box(o, 3, 24, 12, 31, MID, LIGHT, DARK)
rect(o, 5, 31, 10, 31, OUT)
box(o, 1, 7, 14, 25, MID, LIGHT, DARK)
rect(o, 3, 9, 12, 21, OUT)
rect(o, 4, 10, 11, 20, SCREEN)
# a crafting grid rather than a stock list
for ry in (11, 14, 17):
    for rx in (5, 8):
        rect(o, rx, ry, rx + 1, ry + 1, AMBER)
rect(o, 5, 20, 10, 20, AMBRD)
p(o, 13, 23, AMBER)

# ---- 4: Import Bus ------------------------------------------------------
o = 64
box(o, 1, 12, 14, 31, MID, LIGHT, DARK)
rect(o, 3, 15, 12, 27, OUT)
rect(o, 4, 16, 11, 26, SCREEN)
# arrow pointing in (downward)
rect(o, 7, 17, 8, 22, GREEN)
rect(o, 5, 21, 10, 22, GREEN)
rect(o, 6, 23, 9, 23, GREEN)
rect(o, 7, 24, 8, 24, GREEN)
# intake collar on top
box(o, 5, 8, 10, 13, COPPER, LIGHT, DARK)

# ---- 5: Export Bus ------------------------------------------------------
o = 80
box(o, 1, 12, 14, 31, MID, LIGHT, DARK)
rect(o, 3, 15, 12, 27, OUT)
rect(o, 4, 16, 11, 26, SCREEN)
# arrow pointing out (upward)
rect(o, 7, 19, 8, 24, ORANGE)
rect(o, 5, 19, 10, 20, ORANGE)
rect(o, 6, 18, 9, 18, ORANGE)
rect(o, 7, 17, 8, 17, ORANGE)
box(o, 5, 8, 10, 13, COPPER, LIGHT, DARK)

# ---- encode -------------------------------------------------------------
raw = b"".join(
    b"\x00" + b"".join(struct.pack("BBBB", *px[y][x]) for x in range(W))
    for y in range(H)
)

def chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)

png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
       + chunk(b"IDAT", zlib.compress(raw, 9))
       + chunk(b"IEND", b""))

os.makedirs(os.path.dirname(SHEET_PATH), exist_ok=True)
with open(SHEET_PATH, "wb") as handle:
    handle.write(png)
print("wrote", os.path.relpath(SHEET_PATH, ROOT), "-", len(png), "bytes")


# ---- optional preview ---------------------------------------------------
if "--preview" in sys.argv:
    SCALE = 8
    ow, oh = W * SCALE, H * SCALE
    rows = []
    for y in range(oh):
        line = []
        for x in range(ow):
            r, g, b, a = px[y // SCALE][x // SCALE]
            # checkerboard behind the sprites so transparency is visible
            shade = (60, 60, 70) if ((x // SCALE // 2) + (y // SCALE // 2)) % 2 == 0 else (85, 85, 95)
            f = a / 255.0
            line.append(tuple(int(c * f + s * (1 - f)) for c, s in zip((r, g, b), shade)))
        rows.append(line)

    raw_preview = b"".join(
        b"\x00" + b"".join(struct.pack("BBB", *pixel) for pixel in row)
        for row in rows
    )
    png_preview = (b"\x89PNG\r\n\x1a\n"
                   + chunk(b"IHDR", struct.pack(">IIBBBBB", ow, oh, 8, 2, 0, 0, 0))
                   + chunk(b"IDAT", zlib.compress(raw_preview, 9))
                   + chunk(b"IEND", b""))

    preview_path = os.path.join(ROOT, "tools", "preview.png")
    with open(preview_path, "wb") as handle:
        handle.write(png_preview)
    print("wrote", os.path.relpath(preview_path, ROOT))
