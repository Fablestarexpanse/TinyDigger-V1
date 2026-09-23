"""Draws the toolbar and crew panel icons (Slice 17) into one sprite sheet.

Flat white glyphs on transparency, 64 px a cell in an 8 x 8 grid (512 x 512), drawn at four times
the size and scaled down so the edges are smooth. They are tinted in the UI, so white is all
they need to be. The order here is the order ToolIcons reads them in: add at the end, never
reorder.

    python td_tool_icons.py            writes ../../Interaction/Resources/ToolIcons.png
"""
import math
import os
import sys

from PIL import Image, ImageDraw

CELL = 64
GRID = 8
SS = 4  # supersampling
S = CELL * SS
W = (255, 255, 255, 255)
LINE = 18  # stroke width at supersampled size


def canvas():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def p(x, y):
    """Units of a 64-pixel cell to supersampled pixels."""
    return (x * SS, y * SS)


def poly(d, points, fill=W):
    d.polygon([p(x, y) for x, y in points], fill=fill)


def line(d, points, width=LINE):
    d.line([p(x, y) for x, y in points], fill=W, width=width, joint="curve")
    for x, y in (points[0], points[-1]):
        r = width / 2
        d.ellipse([x * SS - r, y * SS - r, x * SS + r, y * SS + r], fill=W)


def circle(d, cx, cy, r, fill=W, outline=None, width=LINE):
    box = [p(cx - r, cy - r), p(cx + r, cy + r)]
    if fill:
        d.ellipse(box, fill=fill)
    else:
        d.ellipse(box, outline=W, width=width)


def rect(d, x0, y0, x1, y1, radius=0, fill=W, outline=False):
    box = [p(x0, y0), p(x1, y1)]
    if outline:
        d.rounded_rectangle(box, radius=radius * SS, outline=W, width=LINE)
    else:
        d.rounded_rectangle(box, radius=radius * SS, fill=fill)


def clear(d, points):
    poly(d, points, fill=(0, 0, 0, 0))


# --- the glyphs -----------------------------------------------------------------------------

def select(d):
    poly(d, [(18, 8), (18, 52), (29, 42), (37, 58), (44, 55), (36, 39), (50, 39)])


def dig(d):
    # A shovel, blade down, over a ground line.
    line(d, [(46, 8), (30, 32)])
    line(d, [(40, 8), (52, 16)], width=14)
    poly(d, [(22, 28), (36, 38), (26, 52), (14, 44)])
    line(d, [(6, 58), (58, 58)], width=10)


def fill(d):
    # A heap with an arrow dropping onto it.
    poly(d, [(6, 58), (22, 40), (42, 40), (58, 58)])
    line(d, [(32, 6), (32, 26)])
    poly(d, [(22, 22), (42, 22), (32, 34)])


def level(d):
    # A flat bar between two arrows.
    rect(d, 8, 28, 56, 36, radius=2)
    poly(d, [(8, 32), (18, 20), (18, 44)])
    poly(d, [(56, 32), (46, 20), (46, 44)])
    line(d, [(8, 52), (56, 52)], width=8)


def road(d):
    # A road running away, dashed down the middle.
    poly(d, [(24, 6), (40, 6), (58, 58), (6, 58)])
    for y0, y1 in ((10, 18), (26, 36), (44, 56)):
        t0, t1 = y0 / 58, y1 / 58
        clear(d, [(32 - 1 - 2 * t0, y0), (32 + 1 + 2 * t0, y0), (32 + 1 + 2 * t1, y1), (32 - 1 - 2 * t1, y1)])


def dump_zone(d):
    # A tipper: cab, raised bed, wheels.
    poly(d, [(6, 44), (6, 30), (16, 30), (20, 36), (20, 44)])
    poly(d, [(22, 40), (54, 16), (58, 22), (30, 44)])
    rect(d, 6, 42, 58, 48, radius=2)
    circle(d, 14, 52, 6)
    circle(d, 46, 52, 6)


def clear_tool(d):
    # An eraser on the slant.
    poly(d, [(8, 40), (34, 14), (56, 36), (30, 62)])
    clear(d, [(14, 40), (26, 28), (40, 42), (28, 54)])
    line(d, [(30, 58), (58, 58)], width=8)


def seed(d):
    # A die showing five.
    rect(d, 8, 8, 56, 56, radius=10, outline=True)
    for cx, cy in ((22, 22), (42, 22), (32, 32), (22, 42), (42, 42)):
        circle(d, cx, cy, 5)


def settings(d):
    # A gear.
    cx, cy = 32, 32
    teeth = 8
    pts = []
    for i in range(teeth * 2):
        a = math.pi * 2 * i / (teeth * 2)
        r = 27 if i % 2 == 0 else 20
        a0 = a - math.pi / (teeth * 2) * 0.55
        a1 = a + math.pi / (teeth * 2) * 0.55
        pts.append((cx + r * math.cos(a0), cy + r * math.sin(a0)))
        pts.append((cx + r * math.cos(a1), cy + r * math.sin(a1)))
    poly(d, pts)
    circle(d, cx, cy, 9, fill=(0, 0, 0, 0))


def debug(d):
    # A bug.
    circle(d, 32, 36, 14)
    circle(d, 32, 18, 8)
    for y in (28, 38, 48):
        line(d, [(18, y), (6, y - 4)], width=8)
        line(d, [(46, y), (58, y - 4)], width=8)
    line(d, [(28, 12), (22, 4)], width=6)
    line(d, [(36, 12), (42, 4)], width=6)
    line(d, [(32, 26), (32, 50)], width=4)


def undo(d):
    d.arc([p(12, 16), p(56, 56)], start=180, end=90, fill=W, width=LINE)
    poly(d, [(4, 36), (22, 36), (13, 22)])


def redo(d):
    d.arc([p(8, 16), p(52, 56)], start=90, end=0, fill=W, width=LINE)
    poly(d, [(60, 36), (42, 36), (51, 22)])


def eyedropper(d):
    line(d, [(12, 52), (38, 26)])
    rect(d, 34, 10, 54, 30, radius=6)
    poly(d, [(8, 60), (10, 50), (16, 56)])


def warning(d):
    poly(d, [(32, 4), (62, 58), (2, 58)])
    clear(d, [(29, 20), (35, 20), (34, 42), (30, 42)])
    circle(d, 32, 50, 3.5, fill=(0, 0, 0, 0))


def digger(d):
    # An excavator: tracks, cab, boom and bucket.
    rect(d, 4, 46, 40, 56, radius=5)
    rect(d, 8, 30, 32, 46, radius=3)
    line(d, [(30, 34), (46, 14), (58, 30)], width=10)
    poly(d, [(52, 30), (62, 30), (60, 42), (50, 38)])


def hauler(d):
    # A dump truck, bed down.
    rect(d, 4, 22, 40, 44, radius=3)
    poly(d, [(42, 30), (54, 30), (60, 38), (60, 44), (42, 44)])
    circle(d, 14, 50, 7)
    circle(d, 50, 50, 7)


def worker(d):
    # The ball robot: a ball with its eye, two arm stubs, a pad under it.
    circle(d, 32, 28, 18)
    circle(d, 32, 26, 6, fill=(0, 0, 0, 0))
    circle(d, 10, 32, 5)
    circle(d, 54, 32, 5)
    rect(d, 22, 52, 42, 58, radius=3)


def quarry(d):
    # A pickaxe over a stepped bench: the crew digs here for material.
    line(d, [(12, 18), (52, 18)], width=12)
    d.arc([p(10, 10)[0], p(10, 10)[1], p(54, 34)[0], p(54, 34)[1]], start=180, end=360, fill=W, width=14)
    line(d, [(32, 18), (32, 34)], width=10)
    poly(d, [(4, 58), (4, 50), (22, 50), (22, 44), (40, 44), (40, 38), (60, 38), (60, 58)])


def terraform(d):
    # A drawn outline over shaped ground: the corners you drop, and the terrace they describe.
    poly(d, [(4, 58), (4, 46), (24, 46), (24, 36), (44, 36), (44, 26), (60, 26), (60, 58)])
    line(d, [(10, 20), (32, 8), (54, 20), (32, 32), (10, 20)], width=8)
    for x, y in ((10, 20), (32, 8), (54, 20), (32, 32)):
        circle(d, x, y, 6)


GLYPHS = [select, dig, fill, level, road, dump_zone, clear_tool, seed, settings, debug,
          undo, redo, eyedropper, warning, digger, hauler, worker, quarry, terraform]


def main(out):
    sheet = Image.new("RGBA", (CELL * GRID, CELL * GRID), (0, 0, 0, 0))
    for i, glyph in enumerate(GLYPHS):
        img, d = canvas()
        glyph(d)
        small = img.resize((CELL, CELL), Image.LANCZOS)
        # Row 0 at the top of the image; ToolIcons flips to Unity's bottom-left origin.
        sheet.paste(small, ((i % GRID) * CELL, (i // GRID) * CELL))
    os.makedirs(os.path.dirname(out), exist_ok=True)
    sheet.save(out)
    print(f"{len(GLYPHS)} icons -> {out}")


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    default = os.path.join(here, "..", "..", "Interaction", "Resources", "ToolIcons.png")
    main(os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else default))
