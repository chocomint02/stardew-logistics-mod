#!/usr/bin/env python3
"""Generates the cable floor tilesheet, and the data pulses that flow along it.

Cables are a custom Flooring rather than a placed object, so a machine or chest can sit on the same tile. That
means the sprite has to supply all sixteen connection variants, laid out as a 4x4 block of 16x16 tiles.

Which variant goes where is not a free choice: Flooring.populateDrawGuide() builds a Dictionary<byte,int> mapping
a neighbour bitmask to a tile index, and the game indexes the sheet as Corner + (index % 16, index / 16). Both the
mask bits and the table below were read out of Stardew Valley 1.6.15 by reflection rather than guessed:

    direction bits   up = 1, right = 2, down = 4, left = 8
    draw guide       0:0  6:1  14:2  12:3  4:16  7:17  15:18  13:19
                     5:32 3:33 11:34  9:35  1:48  2:49  10:50   8:51

The pulse sheet is the same 4x4 layout eight times over, one block per frame, top to bottom. Each block holds only
the bright packets on the cable's core; the mod draws it over the floor. Packets travel right along horizontal
runs and down along vertical ones, a tile's width apart, so they flow unbroken from one cable into the next.

Usage:
    python tools/make_cable_floor.py              # write both sheets
    python tools/make_cable_floor.py --preview    # also write enlarged previews
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelkit import *  # noqa: E402,F401,F403

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "cable-floor.png")
PULSE_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "cable-pulse.png")

TILE = 16
FRAMES = 8

DRAW_GUIDE = {
    0: 0, 6: 1, 14: 2, 12: 3,
    4: 16, 7: 17, 15: 18, 13: 19,
    5: 32, 3: 33, 11: 34, 9: 35,
    1: 48, 2: 49, 10: 50, 8: 51,
}

UP, RIGHT, DOWN, LEFT = 1, 2, 4, 8


def origin(mask):
    index = DRAW_GUIDE[mask]
    return (index % 16) * TILE, (index // 16) * TILE


def conduit(mask):
    """Draws one variant, returning it and the pixels of its core: the horizontal line and the vertical one."""
    t = Canvas(TILE, TILE)

    # The silhouette: a rounded hub, and an arm six pixels wide toward each connected edge.
    solid = set()

    def fill(x0, y0, x1, y1):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                solid.add((x, y))

    fill(4, 4, 11, 11)
    for corner in ((4, 4), (11, 4), (4, 11), (11, 11)):
        solid.discard(corner)
    if mask & UP:
        fill(5, 0, 10, 7)
    if mask & DOWN:
        fill(5, 8, 10, 15)
    if mask & LEFT:
        fill(0, 5, 7, 10)
    if mask & RIGHT:
        fill(8, 5, 15, 10)

    # Casing, outlined only where it meets open ground inside the tile, so an arm runs on into the next cable.
    for (x, y) in solid:
        t.set(x, y, CASE)
    for (x, y) in solid:
        for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < TILE and 0 <= ny < TILE and (nx, ny) not in solid:
                t.set(x, y, OUT)
                break

    # Lit along the top of every horizontal run and of the hub, like the devices' casings.
    for (x, y) in solid:
        if t.get(x, y) == CASE and (x, y - 1) in solid and t.get(x, y - 1) == OUT:
            t.set(x, y, CASE_L)

    # Clamp bands near each end of an arm.
    if mask & LEFT:
        t.rect(2, 6, 2, 9, CASE_L)
    if mask & RIGHT:
        t.rect(13, 6, 13, 9, CASE_L)
    if mask & UP:
        t.rect(6, 2, 9, 2, CASE_L)
    if mask & DOWN:
        t.rect(6, 13, 9, 13, CASE_L)

    # The core: a dim line down every arm, meeting in a lit hub.
    horizontal, vertical = set(), set()
    if mask & LEFT:
        horizontal |= {(x, y) for x in range(0, 8) for y in (7, 8)}
    if mask & RIGHT:
        horizontal |= {(x, y) for x in range(8, 16) for y in (7, 8)}
    if mask & UP:
        vertical |= {(x, y) for x in (7, 8) for y in range(0, 8)}
    if mask & DOWN:
        vertical |= {(x, y) for x in (7, 8) for y in range(8, 16)}
    if mask & (LEFT | RIGHT):
        horizontal |= {(x, y) for x in range(6, 10) for y in (7, 8)}
    if mask & (UP | DOWN):
        vertical |= {(x, y) for x in (7, 8) for y in range(6, 10)}

    for (x, y) in horizontal | vertical:
        t.set(x, y, CYAN_G if (x, y) not in {(7, 7), (8, 7), (7, 8), (8, 8)} else CYAN_D)

    # Hub: bolts in its corners and a lit centre.
    for x, y in ((5, 5), (10, 5), (5, 10), (10, 10)):
        t.set(x, y, CASE_HI)
    t.rect(7, 7, 8, 8, CYAN)
    if mask == 0:
        t.rect(6, 6, 9, 9, CYAN_D)
        t.rect(7, 7, 8, 8, CYAN)

    return t, horizontal, vertical


def pulses(mask, horizontal, vertical, frame):
    """The bright packets on a variant's core in one frame."""
    t = Canvas(TILE, TILE)
    shift = frame * 2

    def packet(position):
        phase = (position - shift) % TILE
        if phase in (0, 1):
            return CYAN_HI
        if phase in (14, 15):
            return alpha(CYAN, 0.7)
        if phase in (12, 13):
            return alpha(CYAN_D, 0.5)
        return None

    for (x, y) in horizontal:
        c = packet(x)
        if c:
            t.set(x, y, c)
    for (x, y) in vertical - horizontal:
        c = packet(y)
        if c:
            t.set(x, y, c)

    # A lone cable just breathes.
    if mask == 0 and frame in (0, 1, 2):
        t.rect(7, 7, 8, 8, alpha(CYAN_HI, 0.8 if frame == 1 else 0.45))
    return t


if __name__ == "__main__":
    sheet = Canvas(TILE * 4, TILE * 4)
    pulse = Canvas(TILE * 4, TILE * 4 * FRAMES)

    for mask in DRAW_GUIDE:
        tile, horizontal, vertical = conduit(mask)
        ox, oy = origin(mask)
        sheet.paste(tile, ox, oy)
        for f in range(FRAMES):
            pulse.paste(pulses(mask, horizontal, vertical, f), ox, oy + f * TILE * 4)

    write_png(SHEET_PATH, sheet)
    write_png(PULSE_PATH, pulse)
    print("wrote", os.path.relpath(SHEET_PATH, ROOT), "and", os.path.relpath(PULSE_PATH, ROOT))

    if "--preview" in sys.argv:
        # A little network: a loop with a spur, and the same with its pulses at one frame.
        layout = [
            "..........",
            ".#######..",
            ".#.....#..",
            ".#..####..",
            ".#.....#..",
            ".#######..",
            "....#.....",
            "....#..#..",
            "..........",
        ]
        rows, cols = len(layout), len(layout[0])
        grass = ((78, 104, 60, 255), (88, 116, 68, 255))

        def at(x, y):
            return 0 <= y < rows and 0 <= x < cols and layout[y][x] == "#"

        for frame, name in ((None, "cable-floor-preview.png"), (2, "cable-pulse-preview.png")):
            scene = Canvas(cols * TILE, rows * TILE)
            for y in range(rows):
                for x in range(cols):
                    scene.rect(x * TILE, y * TILE, x * TILE + TILE - 1, y * TILE + TILE - 1, grass[(x + y) % 2])
            for y in range(rows):
                for x in range(cols):
                    if not at(x, y):
                        continue
                    mask = (UP if at(x, y - 1) else 0) | (RIGHT if at(x + 1, y) else 0) | (DOWN if at(x, y + 1) else 0) | (LEFT if at(x - 1, y) else 0)
                    tile, horizontal, vertical = conduit(mask)
                    for py in range(TILE):
                        for px_ in range(TILE):
                            c = tile.get(px_, py)
                            if c[3]:
                                scene.set(x * TILE + px_, y * TILE + py, c)
                    if frame is not None:
                        over = pulses(mask, horizontal, vertical, frame)
                        for py in range(TILE):
                            for px_ in range(TILE):
                                c = over.get(px_, py)
                                if c[3]:
                                    scene.set(x * TILE + px_, y * TILE + py, c)
            write_preview(os.path.join(ROOT, "tools", name), scene, scale=5)
        print("wrote tools/cable-floor-preview.png and tools/cable-pulse-preview.png")
