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
- **The world is a disc on a table** (built, slice 6). The grid stays square,
  and the cells outside the disc are marked void: they hold no layers, are not
  drawn, cannot be walked on, designated or slumped into, and belong to no
  region. The last ~6 cells of land ease to one rim height, so the edge reads
  as a cut rather than a ragged cliff, and a plinth under the disc, plus a flat
  gradient instead of a sky, make it a model on a surface rather than a
  landscape.

### Landmasses (built, slice 8b)
- Where land is is decided by **noise, not a radius**: twice-warped fBm above a
  threshold, so the coast has bays, headlands and inlets. Only the outer ~25
  cells of the disc are forced to sea.
- **Archetypes** on the settings asset, chosen by seed unless overridden:
  Continent (one rugged mass), Crescent (a bay bitten out of one side), Twin
  (two masses and a strait), Archipelago (one main island plus islets), Lagoon
  (a ring of land around a shallow lake that opens to the sea).
- Patches of land under 40 cells are dropped into the sea (an archipelago keeps
  its islets) and pockets of water under 12 cells are filled. It runs twice,
  because relaxing the land moves cells across the waterline.
- **Relief**: a ridge along a curve of 2–4 control points with ridged
  multifractal noise on it, benches of flat ground in its lee, and valleys cut
  by **one D8 flow-accumulation pass** — the more water a cell would gather,
  the deeper it is cut, so valleys converge and the rivers go in the wettest of
  them.
- **Coast profile**: a beach (sand, rising to about +2 m over 3–8 cells) where
  the land meets the sea gently; bare ground to the waterline where it is
  steep; a shallow shelf 6–15 cells out before the sea drops away.
- Soil thins with slope rather than switching off at a threshold, so a hillside
  goes from turf to bare ground gradually instead of in a line.
- Generation stays under half a second for 512²: 280–340 ms across the
  archetypes, and a test says so.

### Cliffs (built, slice 8e)
- Rock (and granite and bedrock) may stand in a step of up to 3 m to a
  neighbour of rock. Soil keeps the one-metre rule, because soil slumps.
- To the crew a cliff is a wall. It is still workable: a digger can take the
  top off a rock face up to 6 m above where it stands, and benching lets a rock
  neighbour sit up to 6 m above the cell being cut, so a cliffed hill comes
  down from its foot without being told how.

### Surface materials (built, slice 8d)
- What the ground is made of is decided in **its own pass over the finished
  heights**, not per column as columns are built.
- **Slope for that decision is measured on a smoothed heightfield** (5×5, twice).
  On quantised land a uniform hillside is a staircase, so a per-cell slope
  alternates row by row and paints the hill in stripes along its contours.
- Thresholds: < 15° grass, 15–30° grass with rock, 30–45° dirt with rock,
  > 45° rock, with a ~20 m noise field worth ±7° so boundaries wander.
- Every patch under 12 cells is absorbed into what surrounds it, twice.
- Sand is coastal: below +3 m and within 10 cells of water, enforced after
  every cleanup. Clay is a subsurface band in valleys and never the surface —
  on bare rock there is nothing for it to be under, so there is none.

### Sea, depth and water cells (built, slice 8)
- Heights are metres above **sea level, which is 0 m** and is the datum the
  whole world reads against (`World.SeaLevel`). A grid also has a **datum**:
  the height the bottom of every column sits at, well below the sea, so a
  seabed can be 15 m under water and still be a stack of layers and so the
  bedrock base has somewhere to be. Nothing can be dug through the disc.
- A column holds up to **16 layers**: bedrock, granite, rock, clay, dirt, sand
  and topsoil all fit before anything is dug or tipped on it.
- A cell whose surface is below sea level is **water**: impassable, not
  standable, and not diggable — the pathfinder, the flood fill and the regions
  treat it exactly as they treat the void. It is still ground, so **fill
  designations and dump zones are accepted on it**: reclaiming the shallows is
  the point of having them.
- Land starts **one whole step above the sea**, so tipping into the shallows
  has to break the surface before anything can walk there, and cutting a cell
  back to sea level gives it to the sea again.
- The plinth wall's top stands a metre above the sea, so the water is held
  inside the table rather than running off it.

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
- Units have a slope limit and a scoop size. Mixed cargo is in: one load holds
  several materials, so haulers do not run half-empty.
- Dumping into water makes land.

### Where a load may be tipped (built, slice 5)

- **Nowhere undesignated.** A load only ever goes into a hauler, a Fill
  designation or a Dump Zone. A unit that is loaded with nowhere it may tip
  stops and says "Needs a Dump Zone or Fill designation". That is a prompt for
  the player, not a fault.
- **Tipping is done from the rim.** To tip into an area, a unit stands on a cell
  it can drive to beside *any* cell of that area (8 neighbours) and tips onto
  that cell. It does not need to stand inside the area or beside its lowest
  cell.
- **Down is free, up is limited.** The cell tipped onto may be any depth below
  the unit; dig reach only limits tipping upward, and a heap is never left more
  than one climbable step above the unit, so it can always be driven over.
- **Rims above the cell come first,** so material runs in by itself, and among
  those the nearest by path cost.
- **Slump does the rest.** It carries material on into the area, which is how
  cells no rim touches are filled at all.
- **A Fill designation is met when every cell of it is at or above H once the
  ground has settled,** not when the cell tipped onto reaches H. Met
  designations are therefore dropped a tick later, after slump has had its say.
- **A Dump Zone has a cap** (by default the height it was marked at plus 3 m,
  moved with Q/E). The cap applies to the cell tipped onto; slump may still
  carry material past it. A zone with no room left in reach is reported.
- A unit never stands on ground that is itself to be filled, and while standing
  on a Dump Zone heap it only tips level with itself or higher, so it cannot
  bury its own way down.

### Roles (built, slice 5)

- **Digger:** dig reach as above, a 5 m³ scoop. It digs, and empties into a
  hauler beside it (1 s per 5 m³). It tips into a Fill or Dump Zone itself only
  when there is no hauler worth waiting for.
- **Hauler:** never touches the ground itself; a 20 m³ bed of mixed material. It
  serves the reachable digger with the fullest load that has no hauler yet,
  parks beside it (or as near as there is clear room, and the digger walks out),
  and leaves when full, when its digger has nothing left to give, or after 10 s
  with nothing tipped in.
- One hauler per digger at a time; the dispatcher hands them out.

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

### Water (built, slices 8 and 9)
Ours, not a bought asset. The same sheets as slice 8 (one over the sea, clipped
to the disc; one ribbon down the river), with a real surface since slice 9.
Everything it needs is in `WaterSettings` (`Presentation/Settings/Water.asset`).
- **Baked per cell when the land changes** (`WaterField`): depth (smoothed two
  cells), distance through the water to the nearest dry cell, and which way that
  shore lies. These go into the mesh as UV1 to UV3. The sheets are rebuilt a
  moment after the land last changes, so reclaiming the shallows moves the
  breaking waves too.
- **Swell**: Gerstner waves from `WaveSet`, a seeded random mix of heights
  (Ronan's ruling: never one height). The set always has one big wave and one
  small one, and no wave is taller than 1/14 of its length. It also varies over
  the map in calm and rough patches. The swell dies away in water shallower
  than `DampDepth` and within `DampDistance` of the shore, so no wave floods a
  beach. Rivers have no swell.
- **Waves on the beach**: bands on the shore distance that travel inland,
  broken into sets. They lift the surface and foam only in the breaking zone.
- **Looking into it**: the seabed comes from URP's opaque texture, bent by the
  ripples. It is dimmed per channel by how much water the eye looks through,
  read from the depth texture, and scattered colour builds up in its place.
  That is why the shallows are turquoise and the deep water navy. Faint
  caustics show on shallow seabed.
- **Surface**: two scrolling layers of a generated tileable ripple texture
  (`WaterDetailTexture`), mipmapped so the distance never aliases into a grid.
  The sky is reflected by Fresnel, and the sun makes sharp glints.
- **Foam**: wherever the water meets anything (from the depth texture, so a new
  cut foams too), where waves break, on the tallest crests, and on fast rivers.
- **Needs**: URP depth and opaque textures (on in `PC_RPAsset`, opaque
  downsampling off). Every opaque shader needs a DepthNormals pass, because
  SSAO makes URP build the depth texture from that pass. Anything without one
  is invisible to the water's depth maths.
- Wanted later, and not built: wakes and ripples around units (needs an
  interaction render texture), and waterfalls.

### Material detail (built, slice 7)
Everything is in the fragment shader; the mesh and the terrain data are untouched.
- One texel per cell carries the material on top and what a cut would expose
  (`TerrainCellMap`); only changed cells are rewritten.
- The material set is two texture arrays indexed by material id, with cut
  variants appended (`TerrainMaterialAtlas`), so a chunk is one draw call
  whatever it is made of.
- The four nearest cells' materials are blended over half a cell, widened to the
  fragment's own footprint when a cell is smaller than a pixel. This is what
  keeps a material edge a soft line instead of a stair of cell edges.
- Albedo (~0.5 m) and a finer detail normal (~0.25 m) are sampled triplanar from
  world position; one slow ~12 m mottle varies brightness by ±10%.
- Faces past 40° are a cut: exposed material, its cut variant where there is one,
  darkened 15%.
- Derivatives are taken once in uniform flow: a texture read inside the per-cell
  loop has no mip to pick and flattens whole surfaces to one colour.
- Tuning is public on `TerrainView`; the texture set is a ScriptableObject filled
  by **TinyDiggers > Generate Terrain Textures**, so real PNGs can
  replace the placeholders without a code change.

## 6. Things we deliberately do NOT copy
- 4×4 designation blocks.
- Mine Control Tower as a hard requirement — in the sandbox the crew itself is
  the dispatcher. A tower/hub may come later as an upgrade, not a gate.
- Realistic modern machinery scale. Our machines are sci-fi and small.
