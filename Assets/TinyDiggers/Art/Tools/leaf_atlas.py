"""
Builds a leaf-card atlas from Krea2 leaf-cluster sprites (ComfyUI workflow TinyDiggers_Sprite_Krea2).

    python leaf_atlas.py --out Assets/TinyDiggers/Art/Props/leaf_atlas_a.png sprite1.png ... sprite4.png

- Alpha: ComfyUI's JoinImageWithAlpha treats the mask as "transparent where 1", so the
  background-removal mask comes out inverted; it is flipped back here.
- Palette: leaf texels (greener than red) and twig texels (redder) are moved onto the STYLE.md
  albedo targets the prop pipeline uses (td_pipeline.FOLIAGE / WOOD), keeping half the painted
  variation.
- Edges: colour is bled outward into the transparent texels, so mipmaps average leaf green with
  leaf green, not with white; that is what stops a white fringe round every card at RTS distance.
- Layout: 2 x 2 cells, cell i at column i % 2, row i // 2 (row 0 at the top of the image).
"""
import argparse

import numpy as np
from PIL import Image, ImageFilter

ALBEDO_VALUE = 0.72
LEAF_MID, CANOPY_TEAL, BARK = (0x4F, 0x9A, 0x3E), (0x5E, 0x9F, 0x8C), (0x9C, 0x5A, 0x3C)


def target(rgb, mix=None, amount=0.0, value=1.0):
    c = np.array(rgb, dtype=np.float32) / 255.0
    if mix is not None:
        c = c + (np.array(mix, dtype=np.float32) / 255.0 - c) * amount
    return c * ALBEDO_VALUE * value


FOLIAGE = target(LEAF_MID, CANOPY_TEAL, 0.30)
WOOD = target(BARK, value=0.78)


def prepare(path, size, keep=0.8):
    image = Image.open(path).convert("RGBA").resize((size, size), Image.LANCZOS)
    data = np.asarray(image, dtype=np.float32) / 255.0
    rgb, alpha = data[:, :, :3], 1.0 - data[:, :, 3]
    solid = alpha > 0.5
    # Colour comes only from fully opaque texels: the cut-out anti-aliases its edge, so texels
    # between 0.5 and 0.95 alpha still carry the white background and drew a cream fringe round
    # every card (tree_a, cards loop 1). The bleed below repaints them from the interior.
    core = alpha > 0.95
    wood = solid & (rgb[:, :, 0] > rgb[:, :, 1] * 1.05)
    leaf = solid & ~wood
    for mask, goal in ((leaf, FOLIAGE), (wood, WOOD)):
        if mask.any():
            rgb[mask] = goal + (rgb[mask] - rgb[mask].mean(0)) * keep
    rgb = np.clip(rgb, 0.0, 1.0)

    # Bleed: repeatedly spread the average of solid neighbours into transparent texels.
    colour = rgb * core[:, :, None]
    weight = core.astype(np.float32)
    for _ in range(24):
        c_img = Image.fromarray((np.clip(colour, 0, 1) * 255).astype(np.uint8))
        w_img = Image.fromarray((weight * 255).astype(np.uint8))
        c_blur = np.asarray(c_img.filter(ImageFilter.BoxBlur(2)), dtype=np.float32) / 255.0
        w_blur = np.asarray(w_img.filter(ImageFilter.BoxBlur(2)), dtype=np.float32) / 255.0
        grow = (weight == 0) & (w_blur > 0.01)
        colour[grow] = c_blur[grow] / w_blur[grow][:, None]
        weight = np.maximum(weight, grow.astype(np.float32))
    colour[weight == 0] = FOLIAGE

    out = np.dstack([colour, alpha])
    return (np.clip(out, 0, 1) * 255).astype(np.uint8)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--size", type=int, default=1024)
    parser.add_argument("sprites", nargs=4)
    args = parser.parse_args()
    cell = args.size // 2
    atlas = np.zeros((args.size, args.size, 4), dtype=np.uint8)
    for i, path in enumerate(args.sprites):
        x, y = (i % 2) * cell, (i // 2) * cell
        atlas[y:y + cell, x:x + cell] = prepare(path, cell)
    Image.fromarray(atlas, "RGBA").save(args.out)
    coverage = (atlas[:, :, 3] > 127).mean()
    print("LEAF_ATLAS %s coverage %.2f" % (args.out, coverage))


main()
