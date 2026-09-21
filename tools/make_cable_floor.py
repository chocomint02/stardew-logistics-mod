#!/usr/bin/env python3
"""Generates the cable floor tilesheet.

Cables are a custom Flooring rather than a placed object, so a machine or chest can sit on the same tile. That
means the sprite has to supply all sixteen connection variants, laid out as a 4x4 block of 16x16 tiles.

Which variant goes where is not a free choice: Flooring.populateDrawGuide() builds a Dictionary<byte,int> mapping
a neighbour bitmask to a tile index, and the game indexes the sheet as Corner + (index % 16, index / 16). Both the
mask bits and the table below were read out of Stardew Valley 1.6.15 by reflection rather than guessed:

    direction bits   up = 1, right = 2, down = 4, left = 8
    draw guide       0:0  6:1  14:2  12:3  4:16  7:17  15:18  13:19
                     5:32 3:33 11:34  9:35  1:48  2:49  10:50   8:51

Usage:
    python3 tools/make_cable_floor.py              # write the sheet
    python3 tools/make_cable_floor.py --preview    # also write an 8x preview
"""

import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "cable-floor.png")

TILE = 16
W = H = TILE * 4

# Read from Flooring.drawGuide at runtime; see the module docstring.
DRAW_GUIDE = {
    0: 0, 6: 1, 14: 2, 12: 3,
    4: 16, 7: 17, 15: 18, 13: 19,
    5: 32, 3: 33, 11: 34, 9: 35,
    1: 48, 2: 49, 10: 50, 8: 51,
}

UP, RIGHT, DOWN, LEFT = 1, 2, 4, 8

OUT = (34, 30, 44, 255)
MID = (108, 115, 136, 255)
LIGHT = (156, 162, 178, 255)
CYAN = (92, 222, 240, 255)
CYAND = (44, 140, 168, 255)
CLEAR = (0, 0, 0, 0)

px = [[CLEAR for _ in range(W)] for _ in range(H)]


def tile_origin(mask):
    """Top-left pixel of the tile holding this neighbour mask."""
    index = DRAW_GUIDE[mask]
    return (index % 16) * TILE, (index // 16) * TILE


def draw_tile(mask):
    ox, oy = tile_origin(mask)

    # 1. Build the conduit silhouette: a central hub plus an arm toward each connected edge.
    solid = [[False] * TILE for _ in range(TILE)]

    def fill(x0, y0, x1, y1):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                solid[y][x] = True

    fill(4, 4, 11, 11)                     # hub
    if mask & UP:
        fill(4, 0, 11, 7)
    if mask & DOWN:
        fill(4, 8, 11, 15)
    if mask & LEFT:
        fill(0, 4, 7, 11)
    if mask & RIGHT:
        fill(8, 4, 15, 11)

    # 2. Fill, then outline only where the conduit meets empty ground inside the tile. Skipping neighbours that
    #    fall outside the tile is what lets an arm run seamlessly into the next cable.
    for y in range(TILE):
        for x in range(TILE):
            if solid[y][x]:
                px[oy + y][ox + x] = MID

    for y in range(TILE):
        for x in range(TILE):
            if not solid[y][x]:
                continue
            for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < TILE and 0 <= ny < TILE and not solid[ny][nx]:
                    px[oy + y][ox + x] = OUT
                    break

    # 3. A highlight along the top of the hub gives the flat conduit some depth.
    for x in range(5, 11):
        if solid[4][x] and px[oy + 4][ox + x] != OUT:
            px[oy + 4][ox + x] = LIGHT

    # 4. The glowing core, running the length of every arm and meeting in the middle.
    def core(x0, y0, x1, y1, colour):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                if solid[y][x]:
                    px[oy + y][ox + x] = colour

    core(7, 7, 8, 8, CYAN)
    if mask & UP:
        core(7, 0, 8, 8, CYAN)
        core(6, 0, 6, 8, CYAND)
        core(9, 0, 9, 8, CYAND)
    if mask & DOWN:
        core(7, 7, 8, 15, CYAN)
        core(6, 7, 6, 15, CYAND)
        core(9, 7, 9, 15, CYAND)
    if mask & LEFT:
        core(0, 7, 8, 8, CYAN)
        core(0, 6, 8, 6, CYAND)
        core(0, 9, 8, 9, CYAND)
    if mask & RIGHT:
        core(7, 7, 15, 8, CYAN)
        core(7, 6, 15, 6, CYAND)
        core(7, 9, 15, 9, CYAND)

    # An isolated stub reads better as a small plate with a lit centre than as a bare square.
    if mask == 0:
        core(6, 6, 9, 9, CYAND)
        core(7, 7, 8, 8, CYAN)


for m in DRAW_GUIDE:
    draw_tile(m)


def encode(pixels, width, height, rgba=True):
    if rgba:
        raw = b"".join(
            b"\x00" + b"".join(struct.pack("BBBB", *pixels[y][x]) for x in range(width))
            for y in range(height)
        )
        colour_type = 6
    else:
        raw = b"".join(
            b"\x00" + b"".join(struct.pack("BBB", *pixels[y][x]) for x in range(width))
            for y in range(height)
        )
        colour_type = 2

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, colour_type, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


os.makedirs(os.path.dirname(SHEET_PATH), exist_ok=True)
with open(SHEET_PATH, "wb") as handle:
    handle.write(encode(px, W, H))
print("wrote", os.path.relpath(SHEET_PATH, ROOT), "-", W, "x", H)

if "--preview" in sys.argv:
    SCALE = 8
    ow, oh = W * SCALE, H * SCALE
    rows = []
    for y in range(oh):
        line = []
        for x in range(ow):
            r, g, b, a = px[y // SCALE][x // SCALE]
            # Tint each 16x16 tile's backdrop so the variant grid is visible.
            tx, ty = (x // SCALE) // TILE, (y // SCALE) // TILE
            shade = (58, 74, 52) if (tx + ty) % 2 == 0 else (70, 88, 62)
            f = a / 255.0
            line.append(tuple(int(c * f + s * (1 - f)) for c, s in zip((r, g, b), shade)))
        rows.append(line)

    preview_path = os.path.join(ROOT, "tools", "cable-floor-preview.png")
    with open(preview_path, "wb") as handle:
        handle.write(encode(rows, ow, oh, rgba=False))
    print("wrote", os.path.relpath(preview_path, ROOT))
