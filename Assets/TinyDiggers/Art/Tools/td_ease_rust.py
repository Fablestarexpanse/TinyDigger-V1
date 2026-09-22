"""Eases the rust on the machines' baked colour maps.

Ronan's ruling of 2026-09-22 was "keep the baked look, ease the rust": the scans read as the same
family as the crew robot, but the rust speckle is grittier than the game's flat materials and the
terrain around it.

Rust is found by colour rather than by hand: orange-brown pixels (hue about 10 to 45 degrees) that
are darker and duller than the yellow band, which sits at a higher hue and a much higher value and
so is left alone. Those pixels are pulled towards a median-filtered copy of the map — the paint
around the speckle — by `strength`, which fades the specks without touching panel lines, lettering
or the band.

    python td_ease_rust.py                  # every Units/Textures/*_0*.png, at the default strength
    python td_ease_rust.py 0.8 map.png ...  # a strength and the maps to treat
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

# How far a rust pixel is pulled towards the paint around it. 1.0 wipes the rust out altogether,
# which loses the worn look Ronan wanted to keep.
DEFAULT_STRENGTH = 0.65

# What counts as rust: hue in degrees, and the saturation and value it sits under.
HUE = (8.0, 45.0)
MIN_SATURATION = 0.22
MAX_VALUE = 0.82

# The median filter that stands in for "the paint around the speck".
MEDIAN = 9

HERE = os.path.dirname(os.path.abspath(__file__))
TEXTURES = os.path.join(os.path.dirname(HERE), "Units", "Textures")


def _hsv(rgb):
    """Hue in degrees, saturation and value in 0..1, from a float RGB array."""
    top = rgb.max(axis=2)
    bottom = rgb.min(axis=2)
    span = top - bottom
    red, green, blue = rgb[..., 0], rgb[..., 1], rgb[..., 2]

    hue = np.zeros_like(top)
    safe = span > 1e-6
    with np.errstate(invalid="ignore", divide="ignore"):
        is_red = safe & (top == red)
        is_green = safe & (top == green) & ~is_red
        is_blue = safe & ~is_red & ~is_green
        hue[is_red] = ((green - blue)[is_red] / span[is_red]) % 6.0
        hue[is_green] = ((blue - red)[is_green] / span[is_green]) + 2.0
        hue[is_blue] = ((red - green)[is_blue] / span[is_blue]) + 4.0
    hue *= 60.0

    saturation = np.zeros_like(top)
    saturation[top > 1e-6] = span[top > 1e-6] / top[top > 1e-6]
    return hue, saturation, top


def ease(path, strength=DEFAULT_STRENGTH, median=MEDIAN):
    """Eases the rust on one map in place, and reports how much of it was rust."""
    image = Image.open(path).convert("RGB")
    rgb = np.asarray(image, dtype=np.float32) / 255.0
    paint = np.asarray(image.filter(ImageFilter.MedianFilter(median)), dtype=np.float32) / 255.0

    hue, saturation, value = _hsv(rgb)
    rust = ((hue >= HUE[0]) & (hue <= HUE[1])
            & (saturation >= MIN_SATURATION)
            & (value <= MAX_VALUE))

    # Only where the speck actually differs from the paint around it, so a whole rusty panel is
    # left as painted and only the flecks are eased.
    difference = np.abs(rgb - paint).max(axis=2)
    rust &= difference > 0.04

    blend = np.where(rust[..., None], rgb * (1.0 - strength) + paint * strength, rgb)
    Image.fromarray(np.clip(blend * 255.0, 0, 255).astype(np.uint8)).save(path)
    return {"path": path, "rust_share": round(float(rust.mean()), 4)}


def main(argv):
    strength = DEFAULT_STRENGTH
    maps = []
    for arg in argv:
        try:
            strength = float(arg)
        except ValueError:
            maps.append(arg)
    if not maps:
        maps = [os.path.join(TEXTURES, f) for f in sorted(os.listdir(TEXTURES))
                if f.lower().endswith(".png") and "_0" in f]
    for path in maps:
        report = ease(path, strength)
        print(f"[td_ease_rust] {os.path.basename(report['path'])}: "
              f"{report['rust_share'] * 100:.1f}% of it was rust, eased by {strength:.2f}")


if __name__ == "__main__":
    main(sys.argv[1:])
