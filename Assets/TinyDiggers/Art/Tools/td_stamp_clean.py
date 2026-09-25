"""Turns a ComfyUI depth render into a height stamp (Ronan, 2026-09-24: stamps come from ComfyUI).

The workflow renders a top-down aerial image and runs Depth Anything V2 on it: looking straight
down, nearer is higher, so the depth map is a heightmap. It is not clean enough to use as it comes:

- the depth model reads a tilt into any photo (the bottom looks nearer), so a plane is fitted to
  the border ring and taken off, which levels the plain the feature stands on;
- the feature is rarely centred, so the crop is moved to the centre of the raised mass;
- the ground round it goes to zero, and the whole stamp is normalised to 0..1;
- 8-bit depth has visible steps, so it is smoothed a little before being saved as 16-bit.

Negative stamps (a canyon, a river bed, a sinkhole: Ronan, 2026-09-24) go the other way: the plain
round them is the top, so with --negative the stamp stores how far each point lies *below* the plain,
as a positive shape the game places upside down. A canyon crosses the border, so the plain is found
by fitting the border twice, the second time without its lowest third.

Usage: python td_stamp_clean.py depth.png out.png [--size 512] [--blur 1.5] [--border 0.08] [--no-centre] [--negative]
Prints the height at the centre, the peak, and how much of the stamp is off the ground.
"""
import argparse

import numpy as np
from PIL import Image
from scipy import ndimage


def fit_border_plane(h, border):
    """Least-squares plane through the pixels within `border` (a share of the side) of the edge."""
    n = h.shape[0]
    b = max(2, int(n * border))
    mask = np.zeros_like(h, dtype=bool)
    mask[:b, :] = mask[-b:, :] = mask[:, :b] = mask[:, -b:] = True
    ys, xs = np.nonzero(mask)
    a = np.column_stack([xs, ys, np.ones_like(xs)]).astype(float)
    coef, *_ = np.linalg.lstsq(a, h[mask], rcond=None)
    yy, xx = np.mgrid[0:n, 0:n]
    return coef[0] * xx + coef[1] * yy + coef[2]


def fit_plain_below(h, border):
    """The plain a hollow is cut into: a border plane, refitted without the border's lowest third,
    which is where a canyon or a river bed runs out through the edge."""
    n = h.shape[0]
    b = max(2, int(n * border))
    mask = np.zeros_like(h, dtype=bool)
    mask[:b, :] = mask[-b:, :] = mask[:, :b] = mask[:, -b:] = True
    yy, xx = np.mgrid[0:n, 0:n]
    plane = fit_border_plane(h, border)
    keep = mask & (h - plane >= np.percentile((h - plane)[mask], 33))
    ys, xs = np.nonzero(keep)
    a = np.column_stack([xs, ys, np.ones_like(xs)]).astype(float)
    coef, *_ = np.linalg.lstsq(a, h[keep], rcond=None)
    return coef[0] * xx + coef[1] * yy + coef[2], keep


def clean(depth, size, blur, border, centre, negative=False):
    h = depth.astype(float)
    side = min(h.shape)
    h = h[:side, :side]
    if negative:
        plane, plain = fit_plain_below(h, border)
        # How far below the plain, as a positive shape; the plain's own ripple reads as nothing.
        h = plane - h
        h = np.clip(h - np.percentile(h[plain], 75), 0, None)
        return finish(h, side, size, blur, centre)
    h -= fit_border_plane(h, border)
    # The plain is the border's level, with the depth model's ripple on it: lift the zero to the
    # top quarter of the border so that ripple reads as flat ground, not as bumps.
    b = max(2, int(side * border))
    ring = np.concatenate([h[:b].ravel(), h[-b:].ravel(), h[:, :b].ravel(), h[:, -b:].ravel()])
    h = np.clip(h - np.percentile(ring, 75), 0, None)
    return finish(h, side, size, blur, centre)


def finish(h, side, size, blur, centre):
    if centre:
        # Move the centre of the raised mass to the middle.
        cy, cx = ndimage.center_of_mass(h ** 2)
        shift = (side / 2 - cy, side / 2 - cx)
        h = ndimage.shift(h, shift, order=1, mode="constant", cval=0.0)

    h = ndimage.gaussian_filter(h, blur * side / 512.0)
    img = Image.fromarray(h.astype(np.float32), mode="F").resize((size, size), Image.BICUBIC)
    h = np.asarray(img, dtype=float)

    # Fade the outer tenth to nothing so the stamp never has a rim step, whatever its falloff.
    yy, xx = np.mgrid[0:size, 0:size]
    r = np.hypot(xx - (size - 1) / 2, yy - (size - 1) / 2) / (size / 2)
    fade = np.clip((1.0 - r) / 0.1, 0, 1)
    h *= fade * fade * (3 - 2 * fade)

    peak = h.max()
    if peak > 0:
        h /= peak
    return h


def main():
    p = argparse.ArgumentParser()
    p.add_argument("depth")
    p.add_argument("out")
    p.add_argument("--size", type=int, default=512)
    p.add_argument("--blur", type=float, default=1.5)
    p.add_argument("--border", type=float, default=0.08)
    p.add_argument("--no-centre", action="store_true")
    p.add_argument("--negative", action="store_true", help="a hollow cut into the plain: store its depth")
    a = p.parse_args()

    depth = np.asarray(Image.open(a.depth).convert("L"), dtype=float)
    h = clean(depth, a.size, a.blur, a.border, not a.no_centre, a.negative)
    Image.fromarray((h * 65535).round().astype(np.uint16)).save(a.out)
    c = a.size // 2
    print(f"{a.out}: centre {h[c, c]:.2f}, peak 1.00, off the ground {np.mean(h > 0.02):.0%}")


if __name__ == "__main__":
    main()
