#!/usr/bin/env python3
"""Generates the mod's big-craftable spritesheet.

The sheet is 128x32: six 16x32 sprites laid out left to right, indexed by the
``SpriteIndex`` values in ``Integrations/ContentInjector.cs``:

    0 auto-harvester   1 (unused)   2 terminal   3 crafting terminal   4 wireless transmitter   5 wireless receiver

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

# ---- 0: Auto-Harvester -------------------------------------------------
o = 0
LEAF  = (84, 176, 72, 255)
LEAFD = (52, 120, 52, 255)
# cabinet
box(o, 1, 17, 14, 31, MID, LIGHT, DARK)
rect(o, 3, 31, 12, 31, OUT)
# window with a sprout growing in it
rect(o, 3, 20, 12, 28, OUT)
rect(o, 4, 21, 11, 27, SCREEN)
rect(o, 4, 26, 11, 27, (96, 64, 40, 255))          # soil
rect(o, 7, 22, 8, 25, LEAFD)                        # stem
rect(o, 5, 22, 6, 23, LEAF)                         # left leaf
rect(o, 9, 21, 10, 22, LEAF)                        # right leaf
# seed hopper on top
box(o, 3, 10, 11, 17, COPPER, LIGHT, DARK)
rect(o, 5, 12, 9, 13, (60, 40, 24, 255))            # hopper mouth
p(o, 6, 12, AMBER)
p(o, 8, 13, AMBER)
# scythe arm rising from the right shoulder
rect(o, 12, 6, 13, 17, OUT)
rect(o, 12, 7, 12, 16, LIGHT)
# curved blade sweeping left over the hopper
for (x, y) in ((13, 4), (12, 3), (11, 3), (10, 3), (9, 3), (8, 4), (7, 4), (6, 5), (5, 6)):
    p(o, x, y, OUT)
for (x, y) in ((12, 4), (11, 4), (10, 4), (9, 4), (8, 5), (7, 5), (6, 6)):
    p(o, x, y, (214, 220, 232, 255))
# status light
p(o, 2, 29, GREEN)

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

# ---- 4: Wireless Transmitter -----------------------------------------
o = 64
# cabinet
box(o, 2, 20, 13, 31, MID, LIGHT, DARK)
rect(o, 4, 23, 11, 27, OUT)
rect(o, 5, 24, 10, 26, SCREEN)
rect(o, 6, 25, 9, 25, CYAN)
rect(o, 4, 31, 11, 31, OUT)
# mast
rect(o, 7, 7, 8, 20, OUT)
rect(o, 7, 8, 7, 19, LIGHT)
rect(o, 8, 8, 8, 19, DARK)
# cross braces
for by in (12, 16):
    rect(o, 6, by, 9, by, OUT)
# beacon
rect(o, 6, 3, 9, 6, OUT)
rect(o, 7, 4, 8, 5, AMBER)
p(o, 7, 4, (255, 236, 170, 255))
# broadcast arcs either side of the beacon
for (x, y) in ((4, 3), (3, 4), (3, 5), (4, 6)):
    p(o, x, y, CYAN)
for (x, y) in ((11, 3), (12, 4), (12, 5), (11, 6)):
    p(o, x, y, CYAN)
for (x, y) in ((2, 2), (1, 3), (1, 4), (1, 5), (1, 6), (2, 7)):
    p(o, x, y, CYAND)
for (x, y) in ((13, 2), (14, 3), (14, 4), (14, 5), (14, 6), (13, 7)):
    p(o, x, y, CYAND)

# ---- 5: Wireless Receiver -----------------------------------------------
o = 80
# cabinet
box(o, 2, 20, 13, 31, MID, LIGHT, DARK)
rect(o, 4, 23, 11, 27, OUT)
rect(o, 5, 24, 10, 26, SCREEN)
rect(o, 6, 25, 9, 25, GREEN)
rect(o, 4, 31, 11, 31, OUT)
# post
rect(o, 7, 13, 8, 20, OUT)
rect(o, 7, 14, 7, 19, LIGHT)
# dish: a bowl opening upward, the region between two ellipses below the rim line
def in_ellipse(x, y, cx, cy, rx, ry):
    return ((x + 0.5 - cx) / rx) ** 2 + ((y + 0.5 - cy) / ry) ** 2 <= 1.0
bowl = set()
for y in range(4, 14):
    for x in range(0, 16):
        if y >= 6 and in_ellipse(x, y, 8, 5, 7.3, 8.0) and not in_ellipse(x, y, 8, 3.0, 5.0, 6.0):
            bowl.add((x, y))
for (x, y) in bowl:
    edge = any((x + dx, y + dy) not in bowl for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))
    p(o, x, y, OUT if edge else (LIGHT if y < 10 else MID))
# rim glints
p(o, 1, 6, (214, 220, 232, 255))
p(o, 14, 6, (214, 220, 232, 255))
# feed arm rising from the bowl to the focal point, with a receiving light
rect(o, 7, 5, 8, 10, DARK)
rect(o, 6, 2, 9, 4, OUT)
rect(o, 7, 3, 8, 3, GREEN)
p(o, 7, 3, (190, 255, 196, 255))

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
