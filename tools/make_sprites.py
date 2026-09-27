#!/usr/bin/env python3
"""Generates the mod's big-craftable spritesheet, animation frames included.

The sheet is 128 pixels wide: eight 16x32 sprites to a row, indexed by the ``SpriteIndex`` values in
``Integrations/ContentInjector.cs`` and ``Integrations/DeviceAnimations.cs``.

    row 0      the still sprites, shown in menus and the inventory:
               0 auto-harvester   1 (unused)   2 storage terminal   3 crafting terminal
               4 wireless transmitter   5 wireless receiver
    rows 1-10  two rows per device, in the order below: its boot sequence, played once when it's placed,
               then its running loop. Eight frames each.

Every device shares one design: a graphite casing 12 pixels wide with a dark outline and rounded corners, lit
along its top edge, standing on the same plinth with a status light in its middle. Each is symmetric about its
centre -- the drawing helpers mirror everything -- and has one accent colour of its own.

Usage:
    python tools/make_sprites.py              # write src/StardewLogistics/assets/craftables.png
    python tools/make_sprites.py --preview    # also write enlarged previews to tools/
"""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelkit import *  # noqa: E402,F401,F403

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEET_PATH = os.path.join(ROOT, "src", "StardewLogistics", "assets", "craftables.png")

SW, SH = 16, 32
COLUMNS = 8
FRAMES = 8

# The order devices' animation rows come in. Must match DeviceAnimations.cs.
DEVICES = ["harvester", "terminal", "crafting", "transmitter", "receiver"]
STILL_INDEX = {"harvester": 0, "terminal": 2, "crafting": 3, "transmitter": 4, "receiver": 5}

# Which running frame each still sprite shows: the one that says best what the device does.
STILL_FRAME = {"crafting": 4, "transmitter": 2, "receiver": 6}


# ---- shared parts ---------------------------------------------------------------------------------------------
def plinth(s, led):
    """The foot every device stands on, with its status light."""
    s.prect(3, 28, 7, 31, OUT)
    s.prect(4, 28, 7, 30, CASE_D)
    s.prect(4, 28, 7, 28, CASE_DD)  # shadow under the body
    s.pair(3, 31, CLEAR)            # rounded foot
    s.prect(7, 29, 7, 29, led)


def body(s, top, bottom, x0=2):
    """A casing panel: outline with rounded corners, lit top edge, darker base."""
    s.prect(x0 + 1, top, 7, top, OUT)
    s.prect(x0 + 1, bottom, 7, bottom, OUT)
    s.prect(x0, top + 1, x0, bottom - 1, OUT)
    s.prect(x0 + 1, top + 1, 7, bottom - 1, CASE)
    s.prect(x0 + 1, top + 1, 7, top + 1, CASE_L)
    s.pair(x0 + 1, top + 1, CASE_HI)
    s.prect(x0 + 1, bottom - 1, 7, bottom - 1, CASE_D)


def screen(s, x0, y0, x1, y1, glass):
    """A recessed screen: a dark bezel line round a pane of glass."""
    s.rect(x0 - 1, y0 - 1, x1 + 1, y1 + 1, OUT)
    s.rect(x0, y0, x1, y1, glass)


def ring(s, cx, cy, radius, colour, width=0.55, keep=None):
    """A circle of pixels at a radius; ``keep`` decides which of them to draw."""
    for y in range(SH):
        for x in range(SW):
            d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
            if abs(d - radius) < width and (keep is None or keep(x + 0.5 - cx, y + 0.5 - cy)):
                s.set(x, y, colour)


def crt(s, x0, y0, x1, y1, stage, lit, dim, glow):
    """The first frames of a screen powering on: dark, a line across the middle, the line opening, a flash."""
    mid = (y0 + y1) // 2
    if stage == 0:
        return
    if stage == 1:
        s.rect(x0 + 2, mid, x1 - 2, mid + 1, lit)
        return
    if stage == 2:
        s.rect(x0, mid - 2, x1, mid + 3, glow)
        s.rect(x0, mid, x1, mid + 1, lit)
        return
    s.rect(x0, y0, x1, y1, dim)
    s.rect(x0 + 1, y0 + 1, x1 - 1, y1 - 1, lit)


# ---- storage terminal: a console with a list of stock scrolling up its screen --------------------------------
def terminal(boot=None, frame=0):
    s = Canvas(SW, SH)
    body(s, 7, 27)
    plinth(s, GREEN if boot is None or boot >= 7 else (AMBER if boot % 2 else CASE_DD))

    # Crest: a rounded cap with a light bar, lit once it's running.
    s.prect(5, 3, 7, 3, OUT)
    s.prect(4, 4, 4, 6, OUT)
    s.prect(5, 4, 7, 6, CASE)
    s.prect(5, 4, 7, 4, CASE_L)
    s.prect(6, 5, 7, 5, CYAN if boot is None or boot >= 3 else CASE_DD)

    # Speaker grille under the screen.
    for x in (4, 6):
        s.pair(x, 24, CASE_D)
        s.pair(x, 25, CASE_DD)

    x0, y0, x1, y1 = 4, 10, 11, 21
    if boot is not None and boot < 4:
        screen(s, x0, y0, x1, y1, GLASS)
        crt(s, x0, y0, x1, y1, boot, CYAN_HI, CYAN_D, alpha(CYAN, 0.35))
        return s

    screen(s, x0, y0, x1, y1, CYAN_G)
    if boot is not None:
        # The network's mark -- three linked nodes -- then a loading bar.
        s.prect(7, 12, 7, 13, CYAN)
        s.prect(4, 17, 5, 18, CYAN)
        s.prect(6, 14, 6, 16, CYAN_D)
        s.prect(6, 18, 7, 18, CYAN_D)
        if boot >= 5:
            s.prect(5, 20, 7, 20, CYAN_D)
            fill = {5: 1, 6: 2, 7: 3}[boot]
            s.prect(8 - fill, 20, 7, 20, CYAN_HI)
        return s

    # Running: a header, and rows of stock scrolling up a pixel a frame.
    s.prect(4, 10, 7, 10, CYAN)
    pattern = [(5, CYAN), (5, CYAN_D), None, (6, CYAN_D), None, (4, CYAN), (4, CYAN_D), None]
    for y in range(12, 22):
        row = pattern[(y - 12 + frame) % 8]
        if row:
            s.prect(row[0], y, 7, y, row[1])
    return s


def cog(s, cx, cy, hub):
    """A small cog: a disc with eight teeth, outlined, with a lit hub."""
    shape = set()
    for y in range(SH):
        for x in range(SW):
            dx, dy = x + 0.5 - cx, y + 0.5 - cy
            d = math.hypot(dx, dy)
            angle = math.atan2(dy, dx) % (math.pi / 4)
            tooth = min(angle, math.pi / 4 - angle) < 0.22
            if d <= 2.6 or (tooth and d <= 3.7):
                shape.add((x, y))
    for (x, y) in shape:
        edge = any((x + dx, y + dy) not in shape for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))
        s.set(x, y, OUT if edge else (CASE_L if y < cy else CASE))
    s.prect(7, int(cy) - 1, 7, int(cy), hub)


# ---- crafting terminal: a crafting grid filling in, then its product ------------------------------------------
def crafting(boot=None, frame=0):
    s = Canvas(SW, SH)
    body(s, 7, 27)
    plinth(s, GREEN if boot is None or boot >= 7 else (AMBER if boot % 2 else CASE_DD))

    # Crest: a cog, eight teeth round a lit hub.
    lit = boot is None or boot >= 3
    cog(s, 8, 4.0, AMBER if lit else CASE_DD)

    for x in (4, 6):
        s.pair(x, 24, CASE_D)
        s.pair(x, 25, CASE_DD)

    x0, y0, x1, y1 = 4, 10, 11, 21
    if boot is not None and boot < 4:
        screen(s, x0, y0, x1, y1, GLASS)
        crt(s, x0, y0, x1, y1, boot, AMBER_HI, AMBER_D, alpha(AMBER, 0.35))
        return s

    screen(s, x0, y0, x1, y1, GLASS)
    empty = AMBER_G
    cells = {(cx, cy): empty for cx in (4, 7, 10) for cy in (11, 14, 17)}

    if boot is not None:
        # Boot: the grid draws itself in, row by row, then the output slot.
        rows = {4: [11], 5: [11, 14], 6: [11, 14, 17], 7: [11, 14, 17]}[boot]
        for (cx, cy) in cells:
            if cy in rows:
                s.rect(cx, cy, cx + 1, cy + 1, empty)
        if boot == 7:
            s.prect(6, 20, 7, 21, empty)
        return s

    # Running: ingredients go in from the centre outwards, the product appears, and the grid clears.
    order = [[], [(7, 14)], [(7, 11), (7, 17)], [(4, 14), (10, 14)], [(4, 11), (10, 11), (4, 17), (10, 17)]]
    filled = set()
    for step in order[:min(frame, 4) + 1]:
        filled.update(step)
    if frame >= 6:
        filled = set()
    for (cx, cy), colour in cells.items():
        on = (cx, cy) in filled
        s.rect(cx, cy, cx + 1, cy + 1, (AMBER_HI if frame == 5 else AMBER) if on else colour)

    s.prect(7, 19, 7, 19, AMBER_D if frame >= 5 else empty)
    output = {5: AMBER, 6: AMBER_HI, 7: AMBER}.get(frame, empty)
    s.prect(6, 20, 7, 21, output)
    return s


# ---- the wireless pair's shared cabinet ----------------------------------------------------------------------
def cabinet(s, boot, accent, accent_dim, glass, frame):
    body(s, 18, 27)
    plinth(s, GREEN if boot is None or boot >= 7 else (AMBER if boot % 2 else CASE_DD))
    screen(s, 5, 21, 10, 24, GLASS if boot is not None and boot < 1 else glass)

    # A signal meter: bars from the middle out, rising and falling.
    if boot is not None and boot < 1:
        return
    heights = [2, 3, 4, 3, 2, 3, 4, 3] if boot is None else [1, 1, 2, 2, 3, 3, 4, 4]
    level = heights[frame % 8] if boot is None else heights[boot]
    for i, x in enumerate((7, 6, 5)):
        h = max(0, level - i)
        if h:
            s.prect(x, 25 - h, x, 24, accent if i == 0 else accent_dim)


# ---- wireless transmitter: a mast with a beacon, sending rings out ---------------------------------------------
def transmitter(boot=None, frame=0):
    s = Canvas(SW, SH)
    cabinet(s, boot, CYAN, CYAN_D, CYAN_G, frame)

    # Mast and struts.
    s.prect(6, 8, 6, 17, OUT)
    s.prect(7, 8, 7, 17, CASE_L)
    for i, y in enumerate(range(12, 18)):
        x = 5 - (i // 2)
        s.pair(x, y, OUT)
    s.prect(6, 11, 7, 11, OUT)
    s.prect(6, 14, 7, 14, OUT)

    # Lights up the mast, climbing as it boots.
    if boot is not None and 2 <= boot:
        for y in [16, 13, 10][:boot - 1]:
            s.prect(7, y, 7, y, CYAN)

    # Beacon.
    s.prect(6, 3, 7, 3, OUT)
    s.prect(5, 4, 5, 6, OUT)
    s.prect(6, 7, 7, 7, OUT)
    on = boot is None or boot >= 5
    pulse = boot is None and frame in (0, 1)
    s.prect(6, 4, 7, 6, (AMBER_HI if pulse else AMBER) if on else CASE_D)
    s.pair(6, 4, AMBER_HI if on else CASE_L)

    # Rings going out from the beacon, each fading as it widens; two at a time, so the signal never stops.
    if boot is None:
        for r in (2.5 + (frame % 8) * 0.9, 2.5 + ((frame + 4) % 8) * 0.9):
            fade = max(0.0, 1.0 - (r - 2.5) / 7.5)
            if fade > 0.05:
                ring(s, 8, 5, r, alpha(CYAN, fade), keep=lambda dx, dy: abs(dy) <= abs(dx) * 0.9 and abs(dx) > 2)
    elif boot >= 6:
        ring(s, 8, 5, 2.5 + (boot - 6) * 1.2, CYAN, keep=lambda dx, dy: abs(dy) <= abs(dx) * 0.9 and abs(dx) > 2)
    return s


# ---- wireless receiver: a dish, with rings arriving at its feed ------------------------------------------------
def receiver(boot=None, frame=0):
    s = Canvas(SW, SH)
    cabinet(s, boot, GREEN, GREEN_D, GREEN_G, frame)

    # Post.
    s.prect(6, 13, 6, 17, OUT)
    s.prect(7, 13, 7, 17, CASE)

    # The dish: a shallow bowl seen a little from above, its dark inner face showing inside a lit rim.
    s.prect(2, 7, 7, 7, OUT)
    s.pair(1, 8, OUT)
    s.prect(2, 8, 7, 8, CASE_HI if boot is None or boot >= 3 else CASE_L)
    s.pair(1, 9, OUT)
    s.prect(2, 9, 7, 9, CASE_DD)
    s.pair(2, 10, OUT)
    s.prect(3, 10, 7, 10, CASE_L)
    s.pair(3, 11, OUT)
    s.prect(4, 11, 7, 11, CASE)
    s.prect(4, 12, 7, 12, OUT)
    s.prect(6, 12, 7, 12, CASE_D)

    # Two struts from the rim up to the feed, and its receiving light.
    for x, y in ((3, 7), (4, 6), (5, 5)):
        s.pair(x, y, OUT)
    s.prect(6, 2, 7, 2, OUT)
    s.pair(5, 3, OUT)
    s.prect(6, 4, 7, 4, OUT)
    lit = boot is None or boot >= 4
    arriving = boot is None and frame == 7
    s.prect(6, 3, 7, 3, (GREEN_HI if arriving or boot == 7 else GREEN) if lit else CASE_DD)

    # Rings closing in on the feed from above, brightening as they arrive.
    if boot is None:
        r = 8.5 - frame * 0.85
        ring(s, 8, 3.5, r, alpha(GREEN, 0.25 + 0.75 * (1 - r / 8.5)), keep=lambda dx, dy: dy < 0 and abs(dx) > 1.5)
    elif boot >= 5:
        r = 7.5 - (boot - 5) * 2.2
        ring(s, 8, 3.5, r, GREEN, keep=lambda dx, dy: dy < 0 and abs(dx) > 1.5)
    return s


# ---- auto-harvester: a seed hopper over a grow window ----------------------------------------------------------
def harvester(boot=None, frame=0):
    s = Canvas(SW, SH)
    body(s, 15, 27)
    plinth(s, GREEN if boot is None or boot >= 7 else (AMBER if boot % 2 else CASE_DD))

    # Hopper: a funnel full of seed, narrowing into a chute.
    s.prect(2, 4, 7, 4, OUT)
    s.prect(2, 5, 2, 6, OUT)
    s.prect(3, 5, 7, 5, CASE_L)
    s.pair(3, 5, CASE_HI)
    for y, x in ((6, 3), (7, 3), (8, 4), (9, 4), (10, 5), (11, 5)):
        s.pair(x - 1 if y > 6 else 2, y, OUT) if y > 6 else None
        s.prect(x, y, 7, y, CASE)
        s.pair(x, y, CASE_L)
    s.prect(2, 6, 2, 6, OUT)
    s.pair(2, 7, OUT)
    s.prect(4, 6, 7, 6, SEED)
    s.prect(5, 6, 5, 6, SOIL)
    s.prect(6, 12, 7, 14, CASE_D)
    s.pair(5, 12, OUT)
    s.pair(5, 13, OUT)
    s.pair(5, 14, OUT)

    # A seed dropping down the chute while it runs.
    if boot is None and frame in (1, 2, 3):
        s.prect(7, 11 + frame, 7, 11 + frame, SEED)

    # Grow window.
    x0, y0, x1, y1 = 4, 18, 11, 25
    screen(s, x0, y0, x1, y1, GLASS if boot is not None and boot < 1 else GREEN_G)
    if boot is not None and boot < 1:
        return s

    # The grow light along its top, gently pulsing.
    glow = 0.55 + 0.45 * (0.5 + 0.5 * math.cos(frame / 8 * 2 * math.pi)) if boot is None else (1.0 if boot >= 2 else 0.5)
    s.prect(4, 18, 7, 18, mix(GREEN_D, GREEN_HI, glow))
    for y in range(19, 22):
        s.prect(4, y, 7, y, alpha(GREEN, 0.10 * glow * (22 - y)))

    # Soil.
    if boot is None or boot >= 2:
        s.prect(4, 24, 7, 25, SOIL)
        s.prect(4, 25, 7, 25, SOIL_D)
        s.pair(5, 24, SOIL_D)

    # The sprout: planted, growing, then swaying as it runs.
    growth = 5 if boot is None else max(0, boot - 2)
    if growth >= 1:
        s.prect(7, 23, 7, 23, SEED)
    if growth >= 2:
        s.prect(7, 22, 7, 23, LEAF_D)
    if growth >= 3:
        s.prect(7, 21, 7, 23, LEAF_D)
    if growth >= 4:
        # Leaves out to either side; while it runs, their tips lift and settle as if in a breeze.
        s.prect(5, 20, 6, 20, LEAF)
        s.pair(5, 21, LEAF_D)
        s.prect(7, 20, 7, 20, LEAF)
        tip = 19 if boot is None and frame in (2, 3, 4) else 20
        s.pair(4, tip, LEAF)
    if growth >= 5:
        s.prect(6, 19, 7, 19, LEAF)
    return s


DRAW = {"terminal": terminal, "crafting": crafting, "transmitter": transmitter, "receiver": receiver, "harvester": harvester}


def build_sheet():
    rows = 1 + len(DEVICES) * 2
    sheet = Canvas(SW * COLUMNS, SH * rows)

    for name, index in STILL_INDEX.items():
        still = DRAW[name](frame=STILL_FRAME.get(name, 0))
        sheet.paste(still, (index % COLUMNS) * SW, (index // COLUMNS) * SH)

    for d, name in enumerate(DEVICES):
        for f in range(FRAMES):
            sheet.paste(DRAW[name](boot=f), f * SW, (1 + (d * 2)) * SH)
            sheet.paste(DRAW[name](frame=f), f * SW, (2 + (d * 2)) * SH)

    return sheet


def check_symmetry():
    """Every still sprite and every frame must be symmetric; the drawing helpers are meant to guarantee it."""
    problems = []
    for name in DEVICES:
        for f in range(FRAMES):
            for kind, sprite in (("still" if f == 0 else "run", DRAW[name](frame=f)), ("boot", DRAW[name](boot=f))):
                if not sprite.is_symmetric():
                    problems.append(f"{name} {kind} frame {f}")
    return problems


if __name__ == "__main__":
    sheet = build_sheet()
    write_png(SHEET_PATH, sheet)
    print("wrote", os.path.relpath(SHEET_PATH, ROOT), f"({sheet.w}x{sheet.h})")

    asymmetric = check_symmetry()
    if asymmetric:
        print("NOT SYMMETRIC:", ", ".join(asymmetric))

    if "--preview" in sys.argv:
        write_preview(os.path.join(ROOT, "tools", "preview.png"), sheet, scale=6)

        # The still sprites side by side, as they'd stand in a row on the farm.
        row = Canvas(SW * len(DEVICES) + 4 * (len(DEVICES) - 1), SH)
        for i, name in enumerate(["terminal", "crafting", "transmitter", "receiver", "harvester"]):
            row.paste(DRAW[name](frame=STILL_FRAME.get(name, 0)), i * (SW + 4), 0)
        write_preview(os.path.join(ROOT, "tools", "preview-still.png"), row, scale=10)
        print("wrote tools/preview.png and tools/preview-still.png")
