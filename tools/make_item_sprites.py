"""Generates the mod's object spritesheet (16x16 items).

Index 0: the Wireless Terminal, a handheld unit with a screen and an antenna.
Run from the repository root: python tools/make_item_sprites.py
"""
import struct
import zlib

W, H = 16, 16
SPRITES = 1
pixels = [[(0, 0, 0, 0) for _ in range(W * SPRITES)] for _ in range(H)]

OUTLINE = (40, 34, 48, 255)
BODY = (92, 96, 112, 255)
BODY_LIGHT = (132, 138, 156, 255)
BODY_DARK = (64, 66, 80, 255)
SCREEN = (40, 90, 110, 255)
SCREEN_LIT = (92, 222, 240, 255)
SCREEN_DIM = (60, 150, 170, 255)
BUTTON = (220, 180, 60, 255)
LED = (120, 230, 110, 255)


def p(x, y, c):
    pixels[y][x] = c


def rect(x0, y0, x1, y1, c):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            p(x, y, c)


# A small handheld, centred in its tile with room around it, so it sits in a slot like the game's own items.
# antenna, top right
rect(10, 2, 10, 4, OUTLINE)
p(10, 2, LED)

# body with outline: 8 wide, 10 tall
rect(4, 4, 11, 13, OUTLINE)
rect(5, 5, 10, 12, BODY)
rect(5, 5, 10, 5, BODY_LIGHT)
rect(5, 5, 5, 12, BODY_LIGHT)
rect(10, 6, 10, 12, BODY_DARK)
rect(5, 12, 10, 12, BODY_DARK)

# screen: a small grid of stored items
rect(6, 6, 9, 9, OUTLINE)
rect(7, 7, 8, 8, SCREEN)
p(7, 7, SCREEN_LIT)
p(8, 8, SCREEN_LIT)

# buttons
p(6, 11, BUTTON)
p(8, 11, BUTTON)
p(9, 11, LED)


def chunk(tag, data):
    out = struct.pack(">I", len(data)) + tag + data
    return out + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


raw = b"".join(b"\x00" + b"".join(bytes(px) for px in row) for row in pixels)
png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", W * SPRITES, H, 8, 6, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(raw, 9))
png += chunk(b"IEND", b"")

with open("src/StardewLogistics/assets/items.png", "wb") as f:
    f.write(png)
print("wrote src/StardewLogistics/assets/items.png")
