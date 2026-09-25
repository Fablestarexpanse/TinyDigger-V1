# TinyDiggers — style bible

Status: **approved by Ronan on 2026-09-21** ("Ok try it"), including the de-lit 3D-input variant in §8.3. Vehicle scale (§5) is still open until Phase 2 reaches the vehicles. Nothing in Phase 1 starts until this
is approved. Any change to the prompt block below is a bible edit that Ronan approves; it is never
a per-asset tweak.

The references are the six images Ronan supplied on 2026-09-21: a flower and ground-cover sheet,
a sunny birch meadow, a broadleaf tree sheet, and three conifer-forest views with red-earth paths
and mossy boulders. The test sheet made with the prompt block is `STYLE_TEST_SHEET.png` next to
this file. Earlier attempts are `Concepts~/STYLE_TEST_SHEET_v1.png` and `_v2.png`; see "How the block got
here".

---

## 1. Palette

Twelve working swatches. Hex values were sampled from the reference images (region crops,
median-cut) and then rounded to a clean, usable set. Water is the exception: no reference shows
water, so those two swatches come from our Slice 9 water, which already reads right.

| # | Name | Hex | Role | Taken from |
|---|------|-----|------|------------|
| 1 | Meadow Sun | `#86B83A` | ground: sunlit grass | sunlit grass, forest refs (`#659B12`–`#83AD50`), meadow (`#BEC158`) |
| 2 | Meadow Shade | `#1E5A40` | ground: grass in shadow | meadow shade (`#145444`, `#04453D`) |
| 3 | Dry Grass | `#E9A945` | ground: dry and sunburnt patches | meadow foreground (`#F7B74B`, `#D4913B`) |
| 4 | Path Earth | `#A65A30` | ground: trails, dug soil, cut banks | the red-earth paths in the forest refs |
| 5 | Leaf Light | `#C2D55E` | foliage: sunlit canopy tops | birch sheet, meadow trees |
| 6 | Leaf Mid | `#4F9A3E` | foliage: body of the canopy | conifers (`#589F38`, `#71BB59`) |
| 7 | Leaf Deep | `#14482E` | foliage: shade between clumps | conifers (`#103C0E`, `#225B33`) |
| 8 | Canopy Teal | `#5E9F8C` | foliage: the cool, old-tree variant | oak row of the tree sheet |
| 9 | Stone | `#A3A6A4` | rock: cool light grey, lit face | mossy boulders |
| 10 | Bark | `#9C5A3C` | wood: terracotta red-brown | oak trunks, tree sheet (`#9C6048`) |
| 11 | Water Shallow | `#33A9A1` | water: shallows | Slice 9 (derived, not from refs) |
| 12 | Water Deep | `#0D3B5C` | water: open sea | Slice 9 (derived, not from refs) |

**Accents.** For flowers and machines only; together they cover no more than 2% of any frame.

| Name | Hex | Use |
|------|-----|-----|
| Poppy | `#EE6A35` | orange flowers |
| Blossom | `#D757AE` | pink/magenta flowers |
| Cornflower | `#7EA2E4` | blue flowers |
| Buttercup | `#F2C12E` | yellow flowers |
| Daisy | `#F3EEE3` | white flowers, foam |
| Machine Yellow | `#E8B33A` | crew vehicles |

**Light references** (not surface colours): Sky `#B7C6E6` and Shadow Tint `#2C4E5E`. In the
references, shadows are cool blue-teal, never grey or black.

## 2. Form language

- **Rounded, not faceted.** Canopies, boulders and machines are soft lumps with broad bevels. No
  hard low-poly facets and no sharp-cornered boxes.
- **Foliage is clumps.** A canopy is 3–6 big rounded clumps, each of which reads as one smooth
  lumpy blob from a distance. The leaves are painted into the texture, never modelled one by one.
  Conifers are stacked tiers of clumps with a point on top.
- **Proportions are slightly toy-like.** Trunks are slender and a little curved; canopies are big
  for their trunks; machines are chunky, with few, large parts.
- **Detail at RTS distance.** At the home camera (450 m, 40° field of view) one metre is about
  4.4 px at 1440p, so a 10 m tree is about 44 px: only the silhouette and two tones survive. At a
  working zoom (about 80 m) one metre is about 25 px. Detail beyond "two-tone blob with a readable
  trunk" is wasted, and anything finer than about 0.5 m will shimmer.
- **Edges and outlines.** No outlines and no ink lines. Edges read by value contrast
  (light-against-dark clumps) and by the shadow underneath.

## 3. Surface

- **Matte.** No wet or glossy specular on foliage, stone, wood or ground; smoothness ≤ 0.2 on
  everything except water.
- **Texture density:** a 512² albedo per prop is plenty. Painterly brush variation at the scale of a
  clump or a stone face — not photographic grain, bark pores or noise.
- **Painted variation over flat colour:** each surface is its swatch plus gentle hand-painted
  variation (±10% value, a small hue drift toward the neighbouring swatch). Never a flat fill, and
  never a photo.
- **The two tones on foliage come from light, not paint.** The references' yellow-green tops and
  teal undersides are the warm sun and the cool sky acting on one green. The albedo therefore
  carries the mid green with mild painted variation, and Unity's lighting makes the two tones. See
  the open point on de-lighting (§8).

## 4. Lighting and mood

Warm key light with cool fill, high saturation, strong but soft-edged shadows, and a clear hue
shift between lit and shaded surfaces: yellow-green in sun, blue-teal in shade.

Compared with our current `LightingSettings` and `Grade` (after the haze fix):

| | Now | References want | Change (in the ground-and-water pass, not before) |
|---|---|---|---|
| Sun colour | 4800 K | warm, slightly golden | 4500 K |
| Sun intensity | 1.7 | bright | keep |
| Shadow strength | 0.75 | deep, soft | 0.85 |
| Ambient sky | (0.62, 0.72, 0.86) × 1.1 | cool blue-teal fill | cooler and more saturated: about (0.45, 0.62, 0.78) |
| Ambient equator / ground | pale warm grey / brown | lower; the shade is teal | equator down about 20% |
| Saturation (grade) | +12 | clearly higher | +22 to +28 |
| Contrast (grade) | +6 | medium-high | +10 |
| Tonemap | Neutral | filmic, saturated | try ACES against Neutral on the disc shot |
| SSAO | 0.25 | soft contact darkening | keep (it now actually reaches the terrain, since Slice 9) |
| Fog / haze | off | refs have forest-depth haze | **keep off on the play area** (Ronan asked for the haze gone). Any aerial tint only beyond the disc. |

## 5. Scale

The world is 1 unit = 1 m = one terrain cell. Target heights:

| Asset | Height | Footprint |
|-------|--------|-----------|
| Broadleaf tree | 7–12 m | canopy 5–9 m across |
| Conifer | 9–15 m | 3–5 m across |
| Shrub | 1–2.5 m | 1.5–3 m |
| Dead tree | 6–9 m | 3–5 m |
| Boulder | 1.5–5 m | 2–6 m |
| Tall grass tuft | 0.6–1 m | 0.8 m |
| Crew robot (human scale) | 0.43 m to the top, ball 0.285 m across, floating 0.125 m up | 0.4 m with arms |

**Open question for Ronan — vehicles.** The crew placeholders are 0.7 × 0.55 × 0.95 m, one cell.
Beside real-size trees a 10 m tree is 14 diggers tall, which reads as toy diggers in a real
forest. That may be exactly "Tiny Diggers", or the machines may need to grow to about 2.5 × 4 m
(which changes pathing and footprint). Trees and rocks will be built at the metres above either
way. The vehicle size is decided before Phase 2 reaches the vehicles.

## 6. DO / DON'T

- **DO:** build canopies from 3–6 big rounded clumps, so the silhouette reads at 44 px.
- **DO:** keep bark terracotta red-brown (`#9C5A3C`), never grey-brown.
- **DO:** keep stone cool light grey, with moss only on the top faces, where rain sits.
- **DO:** let the sun and sky make the two-tone foliage; paint the albedo mid-green.
- **DO:** keep saturation high; the references are fresh, not muted.
- **DO:** keep shadows cool (teal-blue) and soft-edged.
- **DO:** give every asset its pivot at the base, +Y up, 1 unit = 1 m.
- **DON'T:** use photoreal bark, rock or leaf textures; the grain shimmers at RTS distance and
  breaks the painted look.
- **DON'T:** draw outlines or ink lines.
- **DON'T:** use faceted low-poly flat shading; the references are soft.
- **DON'T:** make anything glossy except water.
- **DON'T:** model individual leaves or needles; they alias and blow the triangle budget.
- **DON'T:** use pure black or neutral grey in shadows.
- **DON'T:** let an accent colour cover more than a sprinkle; flowers are seasoning.
- **DON'T:** let one asset out-saturate or out-detail the rest of the set; it fails even if it
  matches the references on its own.

## 7. Image generation — the fixed block

Every concept image in this project is **subject + category clause + core**, in that order,
verbatim. Only the subject sentence changes per asset.

**Core** (every image):

> Stylized hand-painted 3D game asset for a cosy strategy game. Only this one object, standing
> alone on bare ground, with nothing else anywhere in the image. Soft rounded chunky forms and a
> clean simple silhouette. Saturated fresh colours. Soft warm sunlight from the upper left, soft
> cool shade. Matte surfaces with subtle painterly brush variation, no outlines, not
> photographic, not realistic, not pixel art. The whole object is in frame, isolated and centred
> on a plain flat pale warm-grey studio background with a soft neutral contact shadow,
> three-quarter view from slightly above.

**PLANT clause** (trees, shrubs, grass):

> Foliage: a few big soft rounded clumps, each made of hundreds of tiny painted leaves, two-tone:
> vivid warm yellow-green where the sun hits, cool deep teal-green in the shade between clumps.
> Bright spring greens and terracotta red-brown wood.

**STONE clause** (rocks, cliffs):

> Stone: cool light grey rock with broad soft bevels, a few simple cracks, a little painterly
> colour variation, and moss only where rain would sit. No plants or bushes anywhere.

**VEHICLE clause** (crew machines):

> Vehicle: simple rounded panels like a sturdy wooden toy, few parts, big readable shapes, paint
> slightly worn at the edges. No plants or bushes anywhere.

**ROBOT clause** (crew robots; added with Ronan's OK on 2026-09-21):

> Robot: a small hovering round robot about the size of a football, clearly floating in the air
> a hand's width above its soft shadow, with a gentle glow underneath where it floats. Smooth
> rounded shell panels like a sturdy toy, few parts, big readable shapes, a simple friendly
> face, paint slightly worn at the edges. No plants or bushes anywhere.

**TERRAIN TILE block** (ground textures; added with Ronan's OK on 2026-09-24, after the first
bible-style tiles were "too cartoon"). Terrain is the one place the bible goes natural: a tile is
**subject + this block**, and the object core above is not used.

> Seamless tileable ground texture, viewed straight down, even from edge to edge, no focal point, no
> horizon, no perspective, no cast shadows. Painted realistic style like a modern city-builder or
> strategy game: natural colours, true-to-life shapes and proportions, fine soft detail, subtle not
> saturated, matte, soft even overcast daylight, not cartoon, no chunky shapes, no outlines, no
> text, no border.

The subject sentence names the material and ends with "randomly scattered with no rows, no grid and
no repeating motif" — without it the model lays tufts and stones out in rows. Rock faces say "seen
straight on" instead of "viewed straight down". The colour grade in game supplies the saturation.

**Negative block.** Kept for the record and for any non-distilled model:

> photorealistic, photo, realistic bark texture, noise, film grain, black outlines, ink lines,
> cel-shaded outlines, pixel art, faceted low-poly flat shading, glossy wet specular, text,
> watermark, frame border, cropped, multiple objects, ground clutter, dark background, harsh
> black shadows

**Krea2 turbo runs at CFG 1.0 and ignores negative prompts completely** (the negative is zeroed
conditioning). So every "don't" that matters is also written into the positive core in plain
words ("no outlines, not photographic…"). That is the only lever that works on this model.

**Fixed settings.** Saved in ComfyUI as the workflow `TinyDiggers_Style_Krea2`:

| Setting | Value |
|---------|-------|
| Diffusion model | `Krea2\krea2_turbo_bf16.safetensors` |
| Text encoder | `qwen3vl_4b_fp8_scaled.safetensors`, type `krea2` |
| VAE | `Qwen_Image-VAE.safetensors` |
| Resolution | 1024 × 1024, batch 1 |
| Sampler | euler / simple, 8 steps, CFG 1.0, denoise 1.0 |
| Negative | ConditioningZeroOut of the positive |

- **Seed strategy:** fixed, never randomised. Bible tests use 7100. Asset *n* in the Phase order
  uses base 7100 + 100·*n* (tree_a = 7200). Its three candidates are base, base+1 and base+2.
  The winning seed is recorded in the asset log.
- **Files:** renders go to ComfyUI `output/TinyDiggers/` and are copied to
  `Assets/TinyDiggers/Art/Concepts~/`. The trailing `~` keeps Unity from importing them.

## 8. Decisions I need before Phase 1

1. **Approve the palette, the block and the settings above** (or edit them).
2. **Vehicle scale** (§5).
3. **De-lighting for 3D.** The concept images have warm-top and teal-bottom lighting painted in.
   If Trellis2 bakes that into the albedo, Unity's sun lights it a second time, and the asset goes
   too dark underneath and too yellow on top. I propose a **3D-input variant** of the core, used
   only for the image fed to Trellis2. It swaps "Soft warm sunlight from the upper left, soft
   cool shade" for "soft even overcast studio light from all sides, almost no shading". The
   Blender step then flattens any remaining gradient before the palette is quantised.

## How the block got here

- **v1.** A single paragraph using the saved `PW_Krea2_Basic_Batch` model
  (`myKrea2UnlockedInt8_v10`, 11 steps).
  - The leaves came out cabbage-sized.
  - The background had blocky glitch artefacts.
  - Switching to the official `krea2_turbo_bf16` at 8 steps gave a clean background, so that model
    is now fixed.
- **v1 sheet.** The foliage sentence leaked: the boulder and the digger were surrounded by bushes.
- **v2** made the foliage conditional ("where it has foliage") and pushed the greens brighter. The
  greens improved, but the bushes still appeared.
- **Final.** Split into core + category clauses, with the foliage wording only in the PLANT clause
  and an explicit "no plants" in the STONE and VEHICLE clauses. All four subjects came out
  isolated and consistent (`STYLE_TEST_SHEET.png`).

## 9. Additions since approval (need Ronan's OK)

- **Trees are leaf cards.** This follows from Ronan's choice on 2026-09-21. The canopy is
  200–300 alpha-clipped, double-sided cards carrying a leaf-cluster atlas. Card normals lean out
  from the canopy centre, so the two-tone comes from our light. Trunk and branches are tapered
  tubes along the generated wood. No voxel remesh on any prop; only the terrain is cells.
- **SPRITE core** (leaf-cluster sprites for the atlas; used with a subject sentence and the plant
  clause's even-green wording):

  > Stylized hand-painted game texture for a cosy strategy game. Only this one leaf cluster,
  > flat and centred, on a plain flat pure white background, with no shadow and no ground. Soft
  > even overcast light from all sides, almost no shading. Matte surfaces with subtle painterly
  > brush variation, no outlines, not photographic, not realistic, not pixel art.

  Saved as the ComfyUI workflow `TinyDiggers_Sprite_Krea2` (the style workflow plus a birefnet
  cut-out). Its alpha comes out inverted, and `Art/Tools/leaf_atlas.py` flips it back.
