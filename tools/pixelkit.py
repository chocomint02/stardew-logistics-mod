"""Shared drawing kit and palette for the mod's sprite generators.

Every sprite in the mod is drawn from this one palette, taken from the Wireless Terminal: a graphite casing with a
dark outline, lit from above, and a single accent colour per device. Standard library only.
"""

import struct
import zlib

CLEAR = (0, 0, 0, 0)

# Casing, from the Wireless Terminal.
OUT = (40, 34, 48, 255)
CASE_HI = (170, 176, 194, 255)
CASE_L = (132, 138, 156, 255)
CASE = (92, 96, 112, 255)
CASE_D = (64, 66, 80, 255)
CASE_DD = (50, 50, 62, 255)

# Screens and lights.
GLASS = (24, 30, 42, 255)
CYAN_HI = (206, 250, 255, 255)
CYAN = (92, 222, 240, 255)
CYAN_D = (60, 150, 170, 255)
CYAN_G = (34, 74, 92, 255)
AMBER_HI = (255, 238, 176, 255)
AMBER = (240, 190, 70, 255)
AMBER_D = (184, 124, 40, 255)
AMBER_G = (72, 54, 30, 255)
GREEN_HI = (206, 255, 196, 255)
GREEN = (120, 230, 110, 255)
GREEN_D = (70, 160, 72, 255)
GREEN_G = (30, 64, 42, 255)

# The harvester's window.
SOIL = (116, 76, 46, 255)
SOIL_D = (84, 54, 34, 255)
LEAF = (124, 214, 92, 255)
LEAF_D = (72, 152, 62, 255)
SEED = (214, 170, 96, 255)


def alpha(colour, a):
    """A colour at a fraction of its opacity."""
    return colour[:3] + (int(colour[3] * a),)


def mix(a, b, t):
    """A colour part way from one to another."""
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(4))


class Canvas:
    """A grid of RGBA pixels, drawn on with rectangles and lines."""

    def __init__(self, width, height):
        self.w = width
        self.h = height
        self.px = [[CLEAR for _ in range(width)] for _ in range(height)]

    def set(self, x, y, c):
        if 0 <= x < self.w and 0 <= y < self.h:
            if len(c) == 4 and c[3] < 255 and c[3] > 0:
                # Blend over what's there, so glows and fades sit on the art beneath.
                base = self.px[y][x]
                a = c[3] / 255
                if base[3] == 0:
                    self.px[y][x] = c
                else:
                    self.px[y][x] = tuple(int(c[i] * a + base[i] * (1 - a)) for i in range(3)) + (max(base[3], c[3]),)
            else:
                self.px[y][x] = c

    def get(self, x, y):
        return self.px[y][x] if 0 <= x < self.w and 0 <= y < self.h else CLEAR

    def rect(self, x0, y0, x1, y1, c):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                self.set(x, y, c)

    def pair(self, x, y, c, axis=15):
        """Sets a pixel and its mirror across the sprite's centre, so the art stays symmetric."""
        self.set(x, y, c)
        self.set(axis - x, y, c)

    def prect(self, x0, y0, x1, y1, c, axis=15):
        """A rectangle and its mirror."""
        self.rect(x0, y0, x1, y1, c)
        self.rect(axis - x1, y0, axis - x0, y1, c)

    def paste(self, other, ox, oy):
        for y in range(other.h):
            for x in range(other.w):
                c = other.px[y][x]
                if c[3]:
                    self.px[oy + y][ox + x] = c

    def copy(self):
        c = Canvas(self.w, self.h)
        c.px = [row[:] for row in self.px]
        return c

    def is_symmetric(self, x0=0, x1=None, y0=0, y1=None):
        x1 = self.w - 1 if x1 is None else x1
        y1 = self.h - 1 if y1 is None else y1
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                if self.px[y][x] != self.px[y][x0 + x1 - x]:
                    return False
        return True


def write_png(path, canvas):
    """Writes a canvas as an RGBA PNG."""
    raw = b"".join(b"\x00" + b"".join(struct.pack("BBBB", *canvas.px[y][x]) for x in range(canvas.w)) for y in range(canvas.h))
    _write(path, canvas.w, canvas.h, 6, raw)


def write_preview(path, canvas, scale=8, background=((60, 60, 70), (85, 85, 95))):
    """Writes an enlarged copy on a checkerboard, so transparency shows."""
    w, h = canvas.w * scale, canvas.h * scale
    rows = []
    for y in range(h):
        line = bytearray(b"\x00")
        for x in range(w):
            r, g, b, a = canvas.px[y // scale][x // scale]
            shade = background[0] if ((x // scale // 2) + (y // scale // 2)) % 2 == 0 else background[1]
            f = a / 255.0
            line += bytes(int(c * f + s * (1 - f)) for c, s in zip((r, g, b), shade))
        rows.append(bytes(line))
    _write(path, w, h, 2, b"".join(rows))


def _write(path, w, h, colour_type, raw):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, colour_type, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as handle:
        handle.write(png)
