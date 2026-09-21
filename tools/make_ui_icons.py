#!/usr/bin/env python3
"""Generates the terminal's own UI icons.

These exist because picking source rectangles out of the game's shared `mouseCursors` sheet is guesswork unless
you can see the texture, and a wrong guess renders as a meaningless crop of whatever sits at those coordinates.
Drawing our own 16x16 icons makes the result predictable.

Layout is a single 64x16 row, so sprite index n is simply (n * 16, 0):

    0  hammer   toggle: show craftable only
    1  sort     cycle the sort order
    2  chest    deposit everything
    3  spare

Usage:
    python3 tools/make_ui_icons.py [--preview]
"""

import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "ui-icons.png")

TILE = 16
COUNT = 4
W, H = TILE * COUNT, TILE

OUT = (34, 30, 44, 255)
WOOD = (150, 96, 54, 255)
WOOD_D = (108, 66, 36, 255)
STEEL = (168, 174, 190, 255)
STEEL_D = (104, 112, 132, 255)
STEEL_L = (214, 220, 232, 255)
CYAN = (92, 222, 240, 255)
GOLD = (246, 192, 92, 255)
CLEAR = (0, 0, 0, 0)

px = [[CLEAR for _ in range(W)] for _ in range(H)]


def rect(ox, x0, y0, x1, y1, c):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            if 0 <= ox + x < W and 0 <= y < H:
                px[y][ox + x] = c


def outline(ox):
    """Draws a dark border around every filled pixel of one 16x16 icon."""
    filled = [[px[y][ox + x][3] > 0 for x in range(TILE)] for y in range(TILE)]
    for y in range(TILE):
        for x in range(TILE):
            if filled[y][x]:
                continue
            for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < TILE and 0 <= ny < TILE and filled[ny][nx]:
                    px[y][ox + x] = OUT
                    break


# ---- 0: hammer ----------------------------------------------------------
o = 0
# head, angled slightly so it reads as a hammer rather than a mallet
rect(o, 3, 2, 12, 5, STEEL)
rect(o, 3, 2, 12, 2, STEEL_L)
rect(o, 3, 5, 12, 5, STEEL_D)
rect(o, 2, 3, 2, 4, STEEL_D)
rect(o, 13, 3, 13, 4, STEEL_D)
# claw notch on the left, which is what separates a hammer from a block
px[3][o + 3] = CLEAR
px[4][o + 3] = CLEAR
# handle
rect(o, 7, 6, 9, 14, WOOD)
rect(o, 9, 6, 9, 14, WOOD_D)
outline(o)

# ---- 1: sort (bars + descending arrow) ----------------------------------
o = 16
rect(o, 2, 3, 10, 4, STEEL)
rect(o, 2, 7, 8, 8, STEEL)
rect(o, 2, 11, 6, 12, STEEL)
# arrow pointing down the list
rect(o, 12, 3, 13, 10, CYAN)
rect(o, 10, 9, 15, 10, CYAN)
rect(o, 11, 11, 14, 11, CYAN)
rect(o, 12, 12, 13, 12, CYAN)
outline(o)

# ---- 2: chest (deposit everything) --------------------------------------
o = 32
rect(o, 2, 5, 13, 13, WOOD)
rect(o, 2, 5, 13, 6, WOOD_D)
rect(o, 2, 9, 13, 9, WOOD_D)
rect(o, 7, 9, 8, 11, GOLD)
# open lid, hinting "put things in"
rect(o, 3, 2, 12, 3, WOOD_D)
outline(o)

# ---- 3: spare (left blank on purpose) -----------------------------------

raw = b"".join(
    b"\x00" + b"".join(struct.pack("BBBB", *px[y][x]) for x in range(W))
    for y in range(H)
)


def chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
       + chunk(b"IDAT", zlib.compress(raw, 9))
       + chunk(b"IEND", b""))

os.makedirs(os.path.dirname(SHEET_PATH), exist_ok=True)
with open(SHEET_PATH, "wb") as handle:
    handle.write(png)
print("wrote", os.path.relpath(SHEET_PATH, ROOT), "-", W, "x", H)

if "--preview" in sys.argv:
    SCALE = 10
    ow, oh = W * SCALE, H * SCALE
    rows = []
    for y in range(oh):
        line = []
        for x in range(ow):
            r, g, b, a = px[y // SCALE][x // SCALE]
            shade = (228, 194, 140) if ((x // SCALE // 2) + (y // SCALE // 2)) % 2 == 0 else (238, 206, 152)
            f = a / 255.0
            line.append(tuple(int(c * f + s * (1 - f)) for c, s in zip((r, g, b), shade)))
        rows.append(line)

    praw = b"".join(b"\x00" + b"".join(struct.pack("BBB", *p) for p in row) for row in rows)
    out = os.path.join(ROOT, "tools", "ui-icons-preview.png")
    with open(out, "wb") as handle:
        handle.write(b"\x89PNG\r\n\x1a\n"
                     + chunk(b"IHDR", struct.pack(">IIBBBBB", ow, oh, 8, 2, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(praw, 9))
                     + chunk(b"IEND", b""))
    print("wrote", os.path.relpath(out, ROOT))
