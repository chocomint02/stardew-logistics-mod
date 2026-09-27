#!/usr/bin/env python3
"""Generates the terminal's own UI icons, in the same style as the devices.

These exist because picking source rectangles out of the game's shared `mouseCursors` sheet is guesswork unless
you can see the texture, and a wrong guess renders as a meaningless crop of whatever sits at those coordinates.
Drawing our own 16x16 icons makes the result predictable.

Layout is a single 64x16 row, so sprite index n is simply (n * 16, 0):

    0  hammer   toggle: show craftable only
    1  sort     (spare) a sort order
    2  deposit  deposit everything
    3  spare

Usage:
    python tools/make_ui_icons.py [--preview]
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelkit import *  # noqa: E402,F401,F403

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "ui-icons.png")

TILE = 16


def outlined(icon):
    """Rings every drawn pixel with the dark outline, as the devices are."""
    out = icon.copy()
    for y in range(TILE):
        for x in range(TILE):
            if icon.get(x, y)[3]:
                continue
            if any(icon.get(x + dx, y + dy)[3] for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0))):
                out.set(x, y, OUT)
    return out


def hammer():
    """A hammer: a graphite head, lit on top, on an amber handle."""
    s = Canvas(TILE, TILE)
    s.prect(3, 3, 7, 6, CASE)
    s.prect(3, 3, 7, 3, CASE_L)
    s.prect(3, 6, 7, 6, CASE_D)
    s.pair(3, 3, CASE_HI)
    s.prect(7, 7, 7, 13, AMBER)
    s.prect(7, 13, 7, 13, AMBER_D)
    s.set(7, 7, AMBER_D)
    return outlined(s)


def sort():
    """Sorting: bars shortening downwards, with an arrow."""
    s = Canvas(TILE, TILE)
    for i, (y, width) in enumerate(((3, 8), (7, 6), (11, 4))):
        s.rect(2, y, 1 + width, y + 1, CASE_L)
        s.rect(2, y + 1, 1 + width, y + 1, CASE)
    s.rect(12, 2, 12, 9, CYAN)
    s.rect(10, 9, 14, 9, CYAN)
    s.rect(11, 10, 13, 10, CYAN)
    s.set(12, 11, CYAN_HI)
    return outlined(s)


def deposit():
    """Deposit everything: an arrow down into a tray."""
    s = Canvas(TILE, TILE)
    # Tray.
    s.prect(2, 9, 7, 13, CASE)
    s.prect(2, 9, 2, 13, CASE_L)
    s.prect(3, 12, 7, 13, CASE_D)
    s.prect(4, 9, 7, 10, CASE_DD)
    # Arrow, pointing down into it.
    s.prect(7, 1, 7, 4, CYAN)
    s.prect(5, 5, 7, 5, CYAN)
    s.prect(6, 6, 7, 6, CYAN)
    s.prect(7, 7, 7, 7, CYAN_HI)
    return outlined(s)


if __name__ == "__main__":
    sheet = Canvas(TILE * 4, TILE)
    for i, icon in enumerate((hammer(), sort(), deposit())):
        sheet.paste(icon, i * TILE, 0)
    write_png(SHEET_PATH, sheet)
    print("wrote", os.path.relpath(SHEET_PATH, ROOT))

    if "--preview" in sys.argv:
        write_preview(os.path.join(ROOT, "tools", "ui-icons-preview.png"), sheet, scale=10, background=((232, 200, 140), (222, 188, 128)))
        print("wrote tools/ui-icons-preview.png")
