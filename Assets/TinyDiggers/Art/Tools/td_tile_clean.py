"""Turns a ComfyUI ground render into a terrain tile: albedo and normal, seamless, even.

Ronan, 2026-09-24: the ground's textures are painted-natural tiles from ComfyUI. A render straight out
of the model is not a tile yet:

- it has blotches — patches a whole shade lighter or darker, a tenth of the image across — which
  repeat as a visible grid once the tile is laid down hundreds of times; they are divided out, keeping
  the colour and the fine detail (the procedural generator flattens its low frequencies for the same
  reason);
- its edges do not meet: it is crossfaded with a copy of itself shifted half a tile, so the copy's
  continuous middle covers the seams at the edges and the original covers the copy's seam in the
  middle;
- it has no normal map: one is taken from the brightness, wrapped round so it tiles as well.

- its colour is the model's, usually olive and grey: --mean moves the tile's average colour onto a
  target (per channel, so the detail keeps its contrast), which is how the tiles are matched to the
  terrain references Ronan picked on 2026-09-24 (vivid lime grass, cool blue-grey rock).

Usage: python td_tile_clean.py render.png out_stem [--size 1024] [--flatten 0.85] [--normal 2.0] [--mean r,g,b]
Writes out_stem_albedo.png, out_stem_normal.png and out_stem_preview.png (3 x 3, to eyeball repeats).
"""
import argparse

import numpy as np
from PIL import Image
from scipy import ndimage


def luminance(rgb):
    return rgb[..., 0] * 0.299 + rgb[..., 1] * 0.587 + rgb[..., 2] * 0.114


def flatten(rgb, strength, scale):
    """Divides out brightness drift wider than `scale` of the tile, wrapped so the tile stays whole."""
    n = rgb.shape[0]
    lum = luminance(rgb)
    broad = ndimage.gaussian_filter(lum, n * scale, mode="wrap")
    gain = (lum.mean() / np.maximum(broad, 1e-3)) ** strength
    return np.clip(rgb * gain[..., None], 0, 1)


def seamless(rgb):
    """Crossfades with a half-shifted copy: the copy owns the edges, the original the middle."""
    n = rgb.shape[0]
    shifted = np.roll(rgb, (n // 2, n // 2), axis=(0, 1))
    t = np.sin(np.pi * (np.arange(n) + 0.5) / n) ** 2
    weight = np.sqrt(np.outer(t, t))[..., None]
    return rgb * weight + shifted * (1 - weight)


def normal_from(rgb, strength):
    """OpenGL (green up) normal from brightness as height, wrapped so it tiles."""
    height = ndimage.gaussian_filter(luminance(rgb), 1.0, mode="wrap")
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5 * strength
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5 * strength
    nx, ny, nz = -dx, dy, np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.stack([nx, ny, nz], axis=-1) / length[..., None] * 0.5 + 0.5


def main():
    p = argparse.ArgumentParser()
    p.add_argument("render")
    p.add_argument("out_stem")
    p.add_argument("--size", type=int, default=1024)
    p.add_argument("--flatten", type=float, default=0.85, help="0 keeps the blotches, 1 removes all of them")
    p.add_argument("--scale", type=float, default=0.08, help="blotch size to flatten, as a share of the tile")
    p.add_argument("--normal", type=float, default=2.0, help="normal strength")
    p.add_argument("--mean", default="", help="target average colour, linear 0..1 sRGB values: r,g,b")
    a = p.parse_args()

    img = Image.open(a.render).convert("RGB").resize((a.size, a.size), Image.LANCZOS)
    rgb = np.asarray(img, dtype=float) / 255.0
    rgb = seamless(flatten(rgb, a.flatten, a.scale))
    if a.mean:
        target = np.array([float(v) for v in a.mean.split(",")])
        rgb = np.clip(rgb * (target / np.maximum(rgb.mean(axis=(0, 1)), 1e-3)), 0, 1)
    albedo = Image.fromarray((rgb * 255).round().astype(np.uint8))
    albedo.save(a.out_stem + "_albedo.png")
    Image.fromarray((normal_from(rgb, a.normal * a.size / 256.0) * 255).round().astype(np.uint8)).save(a.out_stem + "_normal.png")

    preview = Image.new("RGB", (a.size * 3 // 2, a.size * 3 // 2))
    small = albedo.resize((a.size // 2, a.size // 2))
    for y in range(3):
        for x in range(3):
            preview.paste(small, (x * a.size // 2, y * a.size // 2))
    preview.save(a.out_stem + "_preview.png")
    lum = luminance(rgb)
    print(f"{a.out_stem}: mean {rgb.mean(axis=(0, 1)).round(3)}, blotch spread {ndimage.gaussian_filter(lum, a.size * 0.08, mode='wrap').std():.4f}")


if __name__ == "__main__":
    main()
