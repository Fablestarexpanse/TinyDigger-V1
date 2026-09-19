# Terrain Reference

Design intent for the terrain system. Read this before touching anything under
`Terrain/`. The target is the *feel* of Captain of Industry's terrain (MaFi Games),
adapted to a cosy, RTS-distance sandbox with small machines.

Primary sources:
- Captain's Diary #35 (terrain revamp): https://coigame.com/Blog/cd-35
- CoI wiki: Designations, Retaining Wall, Mega Excavator pages
- Community threads on mining, ramps, and designation granularity

## 1. What "like Captain of Industry" means

CoI terrain reads as **smooth rolling land with terraced cuts**, not as blocks.
The reasons, in order of importance:

1. The surface is a **displaced heightfield** — a continuous mesh whose vertices
   are pushed to per-tile heights — not stacked boxes. Steps between tiles become
   steep slopes, not vertical walls.
2. Heights are on a **discrete level grid** (integer levels), so cuts and fills
   form clean terraces that span many tiles. Natural land is quantised too, but the
   noise features are large, so plains read as broad gentle plateaus.
3. **Fine material detail comes from textures, not geometry.** Rock and dirt have
   small-scale grain from triplanar-mapped textures (255 material textures in CoI).
   The mesh itself is coarse.
4. **Per-vertex normals are smooth**, so lighting rolls across the land instead of
   faceting per tile.

Our current walls renderer violates 1 and 3; flat per-cell vertex colours with no
normals violate 4. That is why it reads as "voxel". The fix is rendering, not data.

## 2. Data model (already matches CoI — keep it)

- Rectangular fixed-size map. Every property in flat arrays indexed
  `z * width + x`. No chunk lookup in the data path. (CoI: 2–10× faster than
  chunked lookup.)
- Per-cell **stack of material layers** with thickness. Surface height = sum.
- Heights and edits are quantised to `HeightStep`. Data stays float internally
  for volume/slump maths.
- Cell size: **1 m** (CoI tiles are ~2 m per community measurement; we go finer
  because our machines are smaller relative to the land).

### Materials (to add)
- **Disturbed variants are separate materials**, not a flag:
  `Rock -> RockLoose`, `Dirt -> DirtLoose`, `Topsoil -> Dirt` (when dug or driven
  over). Loose variants have lower angle of repose. Conversions may be asymmetric.
- **Bulking factor** per material: `Remove()` yields more loose volume than the
  solid volume removed (rock ~1.5x, dirt ~1.25x, sand ~1.1x). CoI: one 4×4×1
  designation block yields 24–42 loose units depending on material.

## 3. Physics (target behaviour)

- Angle of repose per material, evaluated against neighbours in an
  **8-neighbourhood** (4-way produces pyramid piles).
- **Per-tick budget** of processed cells (CoI uses 1000/tick); carry remainder
  to the next tick. Big landslides take several ticks; no frame spikes.
- **Thin-layer bias**: a thin layer of loose material sticks to a slope instead
  of sliding to the bottom. Bias the effective collapse angle by layer thickness.
- Digging straight down without stepping the edges causes walls to slump onto
  the digger. This is intended gameplay.
- Later: retaining walls that stop slump across their footprint; structures with
  a base plate that fail if enough foundation cells are removed; groundwater
  below a depth.

## 4. Designations and vehicles (target behaviour)

- Two designation types: **Dig to height H** and **Fill to height H**, each
  Flat or Ramp. H defaults to the cursor cell height; Q/E adjusts by one level.
- Designate **per cell** (CoI uses 4×4 blocks and its community dislikes it).
- Units have a **dig reach**: can only dig cells within ±N levels of where they
  stand (CoI ~2). This is what makes ramps emerge as gameplay.
- Ramps are built by digging up into a slope or dumping up onto it. CoI players
  hand-build ~4-tile ramps; the most popular CoI mod auto-generates ramps and
  switchback corridors. We auto-ramp by default, player can override.
- Units have a slope limit and a scoop size. Later: mixed cargo (one load holds
  several materials) so haulers don't run half-empty.
- Dumping into water makes land.

## 5. Rendering plan

### Now (slice 0/1)
- Smoothed heightfield mesh per chunk, vertices at cell corners, corner height =
  average of the four surrounding cells. Keep `HeightStep` quantisation so
  terraces still read.
- **Smooth per-vertex normals** computed from neighbouring heights (include
  cells across chunk borders so seams don't show).
- Colour from top material, with a **steepness blend**: on faces steeper than
  ~40°, blend toward the colour of the layer exposed at that height, so a cut
  through topsoil shows brown-over-grey.
- **Procedural grain in the shader**: 2–3 octaves of world-space noise
  modulating the material colour ±8%, scaled at ~0.25 m so dirt and rock look
  granular at RTS distance. No texture assets yet.
- Optional: a subtle darker line where the top material changes between
  neighbouring cells (reads as an edge without being a wall).

### Later (when map size or GPU demands it)
- Move heights + material ids into a GPU texture; displace a shared grid mesh in
  the vertex shader; distance LOD; single instanced draw. (CoI: 9× faster
  rendering, 4× less memory.)
- Triplanar-mapped material textures; weathered vs freshly-cut rock variants.

## 6. Things we deliberately do NOT copy
- 4×4 designation blocks.
- Mine Control Tower as a hard requirement — in the sandbox the crew itself is
  the dispatcher. A tower/hub may come later as an upgrade, not a gate.
- Realistic modern machinery scale. Our machines are sci-fi and small.
