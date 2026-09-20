# TinyDiggers — decisions and findings

Append-only log of choices that the code cannot explain by itself. Git records what changed;
this records why.

---

**NEXT:** Vertical Slice 5 (loader and dump truck) is done, green (230/230) and pushed on `main`,
with three corrections from Ronan watching it run (diggers hauling, haulers leaving part loaded,
units overlapping). Nothing is in flight. Terrain rendering stays closed. Still owed: a frame-time
check on a mid-range machine (the ~9 ms region rebuild after a change in open ground is the thing
to watch); then Ronan's call on the next slice.

Design intent lives in `TERRAIN_REFERENCE.md`; read it before changing terrain code.

Run the suite with the menu item **TinyDiggers > Run EditMode Tests**; it prints a single
`TESTS PASS ...` / `TESTS FAIL ...` line to the console, with one line per failure.

---

## 2026-09-17 — Vertical Slice 0: terrain data model

Starting state: the repo was the bare Unity 6000.6.1f1 URP template, one commit, no gameplay
code. Nothing from earlier sessions existed to review.

### Terrain model

Captain of Industry style column stacks, not a voxel grid: a 2D grid where each cell holds an
ordered list of `(MaterialId, thickness)` layers, bottom to top, and the surface height is their
sum. No overhangs or tunnels are representable, which is the point — it keeps the mesh a
heightfield and the digging arithmetic one-dimensional.

### Decisions

- **Cells are 1x1 metres, so one unit of volume is one metre of thickness.** Every volume in the
  API is therefore directly a height, and no cell-area factor has to be threaded through digging,
  hauling or the debug readout later.
- **Bedrock is not diggable.** `MaterialDefinition.IsDiggable` is false for it, and `Remove`
  stops when it reaches a layer that is not diggable, reporting less than was asked for. The
  world gets a floor without the grid hardcoding a material id. Callers must therefore treat the
  volume they got back as the truth, not the volume they requested.
- **`Remove` reports into a caller-owned `Span<MaterialVolume>`**, with a `List<MaterialVolume>`
  overload for call sites where a span is awkward. Digging happens per cell per brush per frame,
  so the hot path must not allocate; the list overload fills from a `stackalloc` buffer and
  reuses the list's capacity.
- **`Remove` refuses to dig material it has no room to report.** If the destination span fills
  up, it stops rather than removing material that would then vanish from the world. Consecutive
  layers of the same material merge into one entry, so `MaxLayersPerCell` entries always suffice.
- **Digging leaves no slivers.** A layer within `1e-5` of being consumed is popped whole, so
  repeated digging cannot accumulate a stack of near-zero layers that eats the 8-layer budget.
- **Layers live in one flat `Layer[]` of `width * height * 8`**, alongside a `byte[]` of layer
  counts and a cached `float[]` of surface heights, rather than a list per cell. Mutation
  allocates nothing and a chunk rebuild walks contiguous memory — which matters at the 512x512
  target.
- **Surface height is recomputed from the layers on every mutation**, not tracked incrementally.
  Eight adds is nothing next to the mesh rebuild it triggers, and the cached height can never
  drift away from the layers it describes.
- **`Color32` lives on `MaterialDefinition`.** The data model stays free of MonoBehaviour and is
  unit-testable in EditMode, but it does reference `UnityEngine`. Chosen for convenience over a
  fully engine-free model, because the renderer wants a vertex colour and nothing else would
  own it.
- **Out-of-bounds access throws** rather than returning a default. Brush editing near the grid
  edge must clamp against `InBounds` itself; a silent zero would show up as a mysterious flat
  spot instead of a stack trace.
- **`SetColumn` exists for generation**, which would otherwise fire one `CellChanged` per layer
  per cell. It validates the whole column before writing any of it, so a bad layer cannot leave
  a cell half replaced.
- **`CellChanged` fires only on real mutations.** A no-op `Add`, or a `Remove` that bedrock
  refuses, raises nothing, so the renderer does not rebuild chunks that did not move.

### Tooling

`TinyDiggers/Run EditMode Tests` was added under `Assets/TinyDiggers/Editor` because the Unity
MCP bridge exposes no test-runner tool. It drives `TestRunnerApi` and writes the result to the
console, which the bridge *can* read, so test runs are verifiable from outside the editor.

## 2026-09-19 — Vertical Slice 0: chunked renderer and test terrain

### Renderer

`ChunkedMeshTerrainRenderer` is plain C# behind `ITerrainRenderer`; `TerrainView` is the only
MonoBehaviour, and it only wires the grid, generator and renderer together and calls `Rebuild`
from `LateUpdate`.

- **One mesh per 32x32 chunk; heights at cell corners, as the brief asked.** A corner's height
  is the average surface height of the up to four cells that touch it, so the surface is
  continuous without skirts.
- **Each cell gets its own four vertices** instead of sharing corners with its neighbours. That
  costs 4 verts per cell rather than ~1, but it is what lets each cell carry its own top-material
  colour and a flat per-quad normal. Shared vertices would blend colours across material
  boundaries and smooth away the low-poly look. 32x32x4 = 4096 verts per chunk, so the index
  buffer stays 16-bit; `MaxChunkSize` is 127 for the same reason.
- **Editing a cell dirties every chunk that holds one of its eight neighbours.** The cell sets
  the four corners around it, and those corners belong to its neighbours' quads too. Missing
  this would leave seams along chunk borders.
- **The index buffer is written once per chunk.** Topology depends only on the chunk's size,
  so a rebuild uploads positions, normals and colours only. Bounds are computed while building
  rather than by `RecalculateBounds`.
- **Rebuilding allocates nothing on the managed heap:** scratch vertex, normal, colour and
  corner arrays are sized for a full chunk and reused by every build.
- **Chunks are `HideFlags.DontSave`** so they can never end up serialised into the scene.
- **No colliders.** Clicking will pick against the grid's heights directly; 256 MeshColliders
  that rebake on every dig would be the expensive way to do it.

**Known consequence of corner averaging:** raising or lowering a single isolated cell moves
each of its corners by only a quarter of the change, so a one-cell dig reads as a shallow
dimple. Brush digging is unaffected in its interior, where every corner is surrounded by dug
cells. The grid holds the true heights and the readout will show them. If single-cell edits
need to look exact, the fix is per-cell flat tops with vertical walls, which is a renderer-only
change behind the same interface.

### Shader

`TinyDiggers/Terrain Vertex Color` is hand-written URP HLSL: main-light N·L plus spherical
harmonic ambient, vertex colour, no textures, no shadows. It converts the vertex colours from
sRGB to linear itself, because Unity passes vertex colours through unconverted and the project
renders in linear space; without that the palette washes out. It has a `DepthOnly` pass so the
terrain appears in the depth texture. Shader Graph was not used, because a hand-written shader
diffs and reviews as text.

### Generator

`TerrainGenerator` stays in the pure data assembly and is deterministic per seed. Columns are
bedrock 10 m, then granite in hill cores, rock, dirt, and a 0.3 m topsoil cap. The soil thins
from 2.5 m on the plains to 0.4 m on hilltops, so digging on a hill reaches rock within a click
or two.

The first tuning (hills 8–12 m high over 6–12% of the map) was invisible from RTS camera
height. It now uses 16–24 m over 4–7%, which reads clearly.

### Measured, 2026-09-19

- 512x512 cells, 256 chunks: generation 45 ms, initial meshing 52 ms.
- Scripted edit in play mode (a 10-cell-radius, 3 m pit on the highest peak plus a dirt mound
  40 cells south): the pit went through the soil into rock; 3 of 256 chunks rebuilt in 0.49 ms.
- Frame time over 2000 play-mode frames in the editor: median 1.83 ms (~547 fps), p95 2.30 ms.
  **This is on an RTX 4090, not the mid-range target**, so it proves there is headroom rather
  than proving the target. The load is 256 draw calls, ~0.5M triangles and ~1M vertices. The
  shader is SRP-Batcher compatible. A mid-range check is still owed.

### Tooling finding

The Unity MCP camera-capture tool only grabs the Scene View, so game-camera screenshots are
taken by rendering `Camera.main` into a RenderTexture from `Unity_RunCommand`. The
RunCommand compiler does not reference `System.dll`, so `System.Diagnostics.Stopwatch` is
unavailable there; use `Time.realtimeSinceStartupAsDouble` instead.

## 2026-09-19 — Renderer switched to walled columns; camera, brush and readout

### Ruling from Ronan: flat-topped columns with vertical walls

Corner averaging made a one-cell edit render as a quarter-depth dimple (see the previous
entry). Ronan ruled that edits must render at their true height, so the renderer now draws each
column as it is stored:

- **A flat top at the cell's surface height**, coloured by its top material.
- **A vertical wall wherever a column stands above its neighbour**, and down to zero along the
  world border so the layer cake shows at the map edge.
- **Walls banded by layer.** Each wall is split at the layer boundaries of the taller column, and
  each band takes its layer's colour: the top band is the top material, and the bands below are
  whatever the column holds at that height. A cut through the soil into rock shows dirt over
  grey rock. The topsoil band is green, because the palette colours topsoil green. That is a
  palette choice, not a rule.
- **Each cell builds the walls on its +x and +z edges**, taking colours from whichever side is
  taller. The grid border's -x and -z edges belong to the edge cells. So editing a cell dirties
  its own chunk plus the chunks of its -x and -z neighbours, not the 3x3 set that corner averaging
  needed.
- Vertex counts now vary with the terrain, so the index buffer switches to 32-bit when a chunk
  passes 65,535 vertices, and `MaxChunkSize` is only a sanity bound.

### Triangle count and performance, measured 2026-09-19 (RTX 4090, editor play mode)

| | Corner-averaged | Walled columns |
|---|---|---|
| Triangles, 512x512 test map | ~524,000 | 1,608,896 |
| Initial meshing | 52 ms | 157 ms |
| Local edit rebuild (pit plus mound, 2–3 chunks) | 0.49 ms | 1.47 ms |
| Camera render, 2560x1440, isolated and GPU-synced | not measured | 0.54 ms (0.48 ms with terrain hidden) |
| Frame time shown by the in-game readout | — | 1.3 ms (~754 fps) |

The generated map's gentle noise puts a thin wall between almost every pair of cells, so about
two thirds of the triangles are slivers only centimetres tall. That is the cost of honest heights.
On this GPU it costs about 0.05 ms, so nothing needs doing now. If a mid-range GPU struggles, the
first lever is merging coplanar tops and runs of same-coloured wall bands. Distant slivers also
shimmer (aliasing), which MSAA would fix. Neither is in scope for this slice.

### Camera, brush, readout

All three follow the thin-MonoBehaviour rule. The logic lives in plain C# with tests:

- **`TerrainPicker`** (Terrain assembly) walks the ray cell by cell against flat-topped columns,
  so the picked cell is exactly the one drawn under the cursor. It uses no colliders.
- **`TerrainBrush`** digs or fills a disc of cells (radius 0 is one cell; 2 is 13 cells).
  Dig totals come back per material id in a caller-owned `Span<float>`, so it does not allocate.
- **`TerrainCellReport`** formats the hovered cell. It is called only when the hovered cell or
  its contents change.
- **`RtsCameraRig`** holds the pivot, yaw, fixed pitch and distance. Pan speed scales with
  distance so panning feels the same at every zoom. Zoom is multiplicative. The pivot height eases
  toward the ground so the camera rides over hills. Digging under the pivot therefore lowers the
  camera slightly, which is intended.
- **`TerrainView.BrushRadius`** is a public field, per Ronan, so it can be tuned in the
  inspector during play. Volume per click (1 m per cell) is a serialised field on
  `TerrainEditTool`. Clicks act once per press, and holding the button does not repeat.
- The camera uses perspective with a 30° field of view and 55° pitch. That is the
  "orthographic-ish" look, while keeping depth cues on walls.
- Input reads `Keyboard.current` and `Mouse.current` directly (the project is Input System only).
  Scroll zooms one step per frame of scrolling, because scroll units differ between Input System
  versions (±1 or ±120 per notch).
- The readout is IMGUI with a monospaced font, and its text is rebuilt only on change.

### Findings from driving play mode over MCP

- **Play mode freezes when Unity is not the foreground app**, because the project's
  `runInBackground` is false. Only two game frames ran in about 30 seconds. The earlier "85 ms
  frames" were the editor idling, not the terrain. For scripted verification, set
  `Application.runInBackground = true` at runtime; the project setting was left unchanged.
- **With the Input System's default `PointersAndKeyboardsRespectGameViewFocus`, simulated
  keyboard input does not reach the game** while the Game view is unfocused. For scripted runs,
  switch the in-memory `InputSystem.settings.editorInputBehaviorInPlayMode` to
  `AllDeviceInputAlwaysGoesToGameView`, then restore it. It is not a saved asset in this project.
- Input events queued from `EditorApplication.update` step by step, one step per game frame,
  work for multi-frame sessions. The real OS mouse still moves the hover when it is over the
  Game view.
- `Unity_RunCommand` blocks `System.Reflection`.
- The test runner menu now refuses to run in play mode, and unregisters the previous run's
  callbacks. An aborted run no longer makes every later run report twice.

## 2026-09-19 — Course correction after Captain of Industry diary #35

Source: [Captain's diary #35: Terrain revamp & more](https://coigame.com/post/cd-35) (the old
captain-of-industry.com/post/cd-35 URL redirects there).

Ronan compared our terrain with the diary. **The data model matches theirs**: one flat array per
property over a rectangular map, with materials as layers per tile. **The rendering did not.**
They render a displaced heightfield with steep faces, textured triplanar-style, not vertical
walls. This reverses the walls ruling from earlier today. The rulings:

### 1. Smoothed heightfield is the default renderer again

- `SmoothedTerrainRenderer` (corner-averaged heights, one flat-shaded quad per cell) is the
  default. `WalledTerrainRenderer` stays in the repo behind the same `ITerrainRenderer`, selected
  by `TerrainView`'s renderer field. It is useful for inspecting what the grid really holds.
- Both now share `ChunkedTerrainRenderer` (chunks, dirty set, palette, upload) and
  `TerrainMeshBuilder`, and differ only in which chunks an edit dirties and what geometry a chunk
  gets.
- **Steep faces show the layer they cut.** Each quad carries its top material as the vertex
  colour and an "exposed" colour in UV1. The shader blends from the first to the second as the
  face goes from 40° to 50°, a cheap stand-in for triplanar, with no textures yet. The exposed
  layer is read from the tallest column in the cell's 3x3 neighbourhood (the one the face is cut
  into), at the height of the quad's centre, via the new `TerrainGrid.GetMaterialAt`. The walls
  renderer puts the same colour in both channels, because its walls already show their layer.
- Because the exposed colour reads neighbours' layers, the smoothed renderer's 3x3 dirty
  neighbourhood also covers recolouring, and a test covers it.
- The renderer-agnostic lesson from the walls episode still stands. The grid holds the true
  heights, and a single-cell edit renders at a quarter of its depth. Brush digs, which are what
  the game does, render correctly inside.

### 2. Height step, 1.0 m

Ronan asked to "keep" `HeightStep` quantisation. **It did not exist before this change**; heights
were continuous floats. It is now `TerrainGrid.HeightStep`, set to 1.0 m by
`TerrainView.HeightStep`, and:
- generation snaps each surface to the step, and the rock layer absorbs the difference;
- `Add` and `Remove` round every volume to whole steps, so surfaces that start on the grid stay
  on it;
- slumps move one step at a time.
With heights on a 1 m grid, the smoothed mesh shows terraces as intended.

A bug that came up here: the generator used to drop layers thinner than 5 cm, which knocked the
snapped surface off the grid (one cell landed at 14.9989 m). Thin granite is now omitted before
the rock is computed, and thin rock is folded into the dirt above it.

### 3. Disturbed materials

cd-35 says the normal and disrupted variants are separate layers with their own properties.
Following that, each `MaterialDefinition` has a `Disturbed` counterpart:
Rock → RockLoose, Dirt → DirtLoose, Topsoil → Dirt (as specified), and Granite → RockLoose
(my addition, since granite is also rock). Clay and sand have none.
- `Remove` reports what comes out in its disturbed form. The ground left behind stays
  undisturbed.
- `Add` places the disturbed form of whatever it is given. Only `SetColumn`, which generation
  uses, writes undisturbed ground.
- Consequence: dug topsoil is reported as Dirt, but tipping Dirt back places DirtLoose. The
  mapping is applied on each hop, not composed once.
- Angles of repose: loose variants are shallower (RockLoose 38°, DirtLoose 32°, Sand 34°).
  Undisturbed ground is steep (Rock 80°, Clay 60°, Dirt and Topsoil 50°, Granite and Bedrock
  never slump). Undisturbed angles sit at or above what a 1 m step over one cell can produce, so
  generated terrain does not slide on its own.

### 4. Slump: `AngleOfReposeSimulator`

- **8-neighbour, not lowest-neighbour-only**, as in cd-35: each examined cell ranks all eight
  neighbours by slope (diagonals at √2 distance) and sheds one height step to each, steepest
  first, while the drop still exceeds its effective angle.
- **Budgeted.** Only cells near edits are examined: each `CellChanged` queues the cell and its 8
  neighbours. `Tick()` examines at most `MaxTilesPerTick` (public, default 1000; exposed as
  `TerrainView.SlumpTilesPerTick`) and carries the rest over to the next tick. The readout shows
  the queue length.
- **Thickness bias**, as in cd-35: a top layer thinner than `ThinLayerDepth` (2 m) has its
  collapse angle raised towards vertical in proportion to how thin it is. A 0.5 m skin of loose
  dirt clings to a 3 m drop that 3 m of loose dirt slides down.
- **Never inverts a slope.** Moving one step lowers the source and raises the target by a step
  each, so it only moves when the drop is at least two steps. As a result, 1 m-per-cell slopes
  (45° along the axes) are always stable at the 1 m step, whatever the material angle says.
  Material angles only matter for drops of 2 m or more.
- **Never loses material.** Before moving, it checks that the target's layer stack has room for
  everything the step would turn into, and skips that neighbour if not. Tests cover conservation
  of total height.
- **Deterministic**: FIFO queue, fixed neighbour order. The fixed order breaks ties in the same
  direction every time. A pile is therefore slightly lopsided: 8 m of loose dirt spreads over 8
  of its 9 cells, and the missing corner is always the same one. Tipped piles grow short
  "fingers" along the tie-break directions. Rotating the start of the neighbour order per cell
  would even this out if it bothers anyone.

### Measured, 2026-09-19 (RTX 4090, editor play mode)

| | Walls (previous default) | Smoothed (default again) |
|---|---|---|
| Triangles, 512x512 | 1,608,896 | 524,288 |
| Initial meshing | 157 ms | 109 ms (52 ms before steep-face colours) |
| Camera render, 2560x1440 isolated | 0.54 ms (0.48 without terrain) | 0.51–0.53 ms (0.45 without terrain) |

A scripted run on the highest peak dug a radius-5 pit 6 m deep: 454 m³ of loose rock, 24 m³ of
dirt (from topsoil) and 8 m³ of loose dirt came out. Its steep faces show grey rock under a
brown band of dirt. Tipping 50 m³ of dirt as an 11 m column on the plain slumped within a
couple of frames into a mound about 3 m high, topped with loose dirt.

## 2026-09-19 — `TERRAIN_REFERENCE.md` added; its section 5 "Now" plan

Ronan added `TERRAIN_REFERENCE.md` (repo root; `CLAUDE.md` points at it) as the design intent
for the terrain. Its diagnosis (section 1): the render read as voxels because of rendering, not
data. CoI terrain is a displaced heightfield on a discrete level grid with large noise features,
fine grain from textures, and smooth per-vertex normals. Flat per-quad normals broke rule 4.

Items 1 (smoothed renderer as default, walls kept unselected), the 1.0 m `HeightStep`, the
disturbed variants and the 8-neighbour budgeted slump were already in from the previous entry.
This round added what was missing.

### Smooth per-vertex normals
- One normal per grid corner: central differences of the corner heights around it,
  one-sided at the world edge. Each cell is still its own quad (colours stay per cell), but the
  four quads meeting at a corner share its normal, so light rolls across the land.
- **No seams:** each chunk caches corner heights one ring beyond its own border, and the maths
  runs in grid coordinates. `NormalsAtChunkEdgesMatchAnUnchunkedMesh` compares every vertex on
  both sides of the x=32 and z=32 borders against the same grid meshed as one 127-wide chunk.
- **The dirty region grew from 3x3 to 5x5**, because a cell's height feeds corners that feed the
  normals of corners one step further out. A test covers editing a cell two away from a chunk
  border.

### Shader
- Steepness blend now starts at 40° (complete by 50°), on the interpolated smooth normal.
- **Grain:** three octaves of 3D world-space value noise at 0.25, 0.5 and 1 m, weighted 0.5,
  0.3 and 0.2, scale the colour by ±8%. It is 3D so vertical cut faces get grain, not streaks.
  The finest octave stops at 0.25 m: at the default camera distance a pixel is about 0.13 m, so
  anything finer would shimmer.
- **Edge line (the optional item, done):** UV2 flags each quad side whose neighbour has a
  different top material, and UV0 gives the fragment's position in its cell. A line 4% of a cell
  wide (at least a pixel, via `fwidth`) is darkened 12%.
- Cost: camera render at 2560x1440 is 0.60 ms with terrain against 0.50 ms without (RTX 4090),
  so terrain plus grain costs about 0.1 ms.

### Test terrain (reference 1.2)
- Plateau noise at 0.006 cycles per cell (a rise every ~170 cells), 4 m range, plus a 0.015
  octave of 1.5 m so plateau edges wander. **The small-scale detail noise is gone**: it frayed
  terrace edges into single-cell speckle.
- 2–4 hills, **4–8 m high** (4–8 terraces) and 8–15% of the map wide.
- Test at game scale (256x256, three seeds): neighbours never differ by more than one step, so
  fresh terrain can never slump, and more than 80% of cells are terrace tops.
- At RTS height the hills now read as concentric terrace contours on smooth land, which is what
  section 1 describes.

### Bulking (reference section 2)
- `MaterialDefinition.BulkingFactor`: Rock and Granite 1.5, Clay 1.3 (my pick; the reference
  does not list clay), Dirt and Topsoil 1.25, Sand 1.1, loose variants 1.0. Values below 1 are
  rejected.
- `Remove` takes an in-place volume (how far the surface drops) and reports loose volume. The
  brush reports both: "Dug 486 m³ -> 721 m³ loose".
- **Slump does not bulk** (`Remove(..., bulk: false)`). A 1 m slide would otherwise produce
  1.25 m, and `Add` rounds that to 1 m, silently losing material. Bulking happens once, when
  ground is dug. This is a deliberate deviation for the height grid.

### Thin-layer bias: loose material only (reference section 3)
The reference says the bias is for a thin layer of **loose** material, and that digging
straight down without stepping the edges makes the walls slump in. The previous build applied
the bias to any thin top layer, so the 0.3 m topsoil cap held up every cut wall. Only
`IsLoose` materials (loose rock, loose dirt, sand) get the bias now. The test
`DiggingStraightDownMakesTheWallsSlumpIn` digs an unstepped 3 m pit in dirt and checks that the
rim comes down into it.

### Palette
Rock, granite and loose rock are about 25% darker. In the first capture, a full sun plus
ambient rendered them near-white, and a cut no longer read as rock. This is a rendering
correction, not final art.

### The same pit as before
Same procedure: highest peak, radius 5, six 1 m digs, same camera offset. The peak is now 24 m
on 14–17 m plains. 486 m³ dug in place came out as 721 m³ loose (680 loose rock, 30 dirt from
topsoil, 10 loose dirt). The rock walls (80°) held; the topsoil and dirt at the rims slumped in.

**Known close-up artefacts, not fixed:**
- **Sawtooth rims:** where a quad's top colour (green) and exposed colour (grey) differ, the
  per-fragment steepness blend follows the interpolated normal across the quad's two triangles,
  giving triangular teeth along pit edges. At RTS distance this reads as a blocky outline. The
  fixes would be blending on a per-corner steepness weight, or moving to texture-based
  triplanar (reference "Later").
- **Edge lines inside pits** trace every change of top material (rock, loose rock, loose dirt)
  across the floor. That is correct by the rule, but busy.

## 2026-09-19 — Wandering contours, softer creases, slump frame cost

Ronan closed terrain rendering after this round. The two pit artefacts above stay as they are
until triplanar textures.

### Generator: domain warp and a medium octave
The hills produced perfect concentric terrace rings, a bullseye. Two changes, both before
quantising:
- **Domain warp:** every height sample, hills included, is taken at a position displaced by a
  second Perlin field with ~30 m features, by up to ±6 m.
- **Medium octave:** ~40 m features with a 1.2 m range, for texture between terraces.
The new random draws come after the hills, so hill placement per seed is unchanged. The 3-seed
game-scale test (neighbours never differ by more than 1 m; more than 80% of cells are terrace
tops) still passes: the warp stretches slopes by at most ~2x, and hill slopes were ~0.17 per cell.

### Crease shading halved
The dark terrace lines were plain N·L on risers turned away from the sun. The shader now also
computes the lighting flat ground would get, and where a face is darker than that it gives back
`_CreaseSoftening` = 0.5 of the shortfall. Faces lit brighter than flat are untouched, so relief
still reads.

### Slump perf: profiled, and the hypothesis did not hold
Ronan expected per-tile `MarkDirty` to be triggering chunk rebuilds every frame. Profiler markers
(`TinyDiggers.SlumpTick`, `TinyDiggers.TerrainRebuild`, `TinyDiggers.ChunkBuild`,
`TinyDiggers.ChunkUpload`) on a 1,045-tile queue (a radius-17, 8 m heap of loose dirt) showed:
- Each dirty chunk was **already rebuilt at most once per frame**: the renderer kept a
  deduplicated chunk set and rebuilt in `LateUpdate`, after the slump tick in `Update`.
- The heap settled in 8 frames, rebuilding 4–6 chunks each frame. Mean self time per slump frame:
  `ChunkBuild` 2.27 ms, `EditorLoop` 1.58 ms (editor, not game), `SlumpTick` 0.64 ms, profiler
  overhead 0.50 ms, `ChunkUpload` 0.33 ms. **The cost was rebuilding each chunk from scratch**
  (~0.45 ms, ~440 ns per cell), not how often it happened.

Changes:
- **The dirty-cell set Ronan asked for.** `MarkDirty` now only records the cell, O(1) and
  deduplicated. `Rebuild` expands each dirty cell into chunks once, then rebuilds each chunk once.
  This saves the 5x5 chunk fan-out on every repeated touch of a cell within a tick. It is a small
  win (slump tick 0.31–0.66 down to 0.28–0.52 ms).
- **The actual fix: a cheaper chunk build.**
  - `TerrainGrid` caches each cell's top material next to its height, and exposes both as
    read-only spans.
  - The smoothed renderer copies the chunk plus a two-cell halo into local arrays once, then
    builds without bounds checks or grid calls.
  - Flat ground skips the exposed-layer lookup (the answer is its own top).
  - The builder writes plain arrays instead of `List`s.
  - The index buffer is not re-uploaded when the quad count is unchanged, which for the smoothed
    renderer is always.
  - Corner heights are summed in the same order as the public `CornerHeight`, so the
    chunk-border normal test still matches bit for bit.

Same 1,045-tile heap, before and after (RTX 4090, editor play mode):

| Per busy frame | Before | After |
|---|---|---|
| Main thread | 4.4–7.9 ms | **3.5–5.4 ms** |
| Terrain rebuild (4–6 chunks) | 1.65–2.74 ms | 0.87–1.40 ms |
| Per chunk | ~0.45 ms | ~0.23 ms |
| Slump tick | 0.31–0.66 ms | 0.28–0.52 ms |

The editor alone accounts for ~1.6 ms of an idle main-thread frame here. Initial meshing of the
whole map went from 140 ms to 89 ms, and the EditMode suite from 1.8 s to 0.8 s. The 10.6 ms
Ronan saw in the readout was taken during a 2560x1440 `ScreenCapture`, not during normal play.

If slump frames need to be cheaper still, the next lever is rebuilding only the dirty rows of a
chunk rather than the whole chunk.

## 2026-09-19 — Housekeeping, then Vertical Slice 1: spoil has to go somewhere

### Housekeeping
- Ronan's pending working-tree changes were committed as `chore: strip template files, project
  settings`. That covers the deleted URP template TutorialInfo/Readme and the Hub helper, and
  Unity's own ProjectSettings edits: the cloud project link, package define symbols and
  QualitySettings serialisation. It also adds the AI Assistant package settings file, which was
  checked first and holds no secrets.
- `terrain/vertical-slice-0` was fast-forwarded into `main` without a checkout, so Unity never
  saw files vanish, and `main` was pushed to origin. **Work now happens on `main`; commit and
  push after every green slice.**
- `.gitignore` is GitHub's standard Unity template. It covers Library/, Temp/, Logs/,
  UserSettings/, obj/, *.csproj and *.sln, and none of those are tracked.

### Slice 1 design
Digging no longer deletes material: it goes into a crew's load and has to be tipped somewhere.
New assembly `TinyDiggers.Units` (plain C#). It is not named `TinyDiggers.Crew`, because a class
`Crew` inside a namespace `TinyDiggers.Crew` cannot be referenced from sibling namespaces: C#
resolves the name to the namespace first.

- **`MaterialInventory`:** loose m³ per material up to a capacity (default 5), with a per-material
  dictionary plus a LIFO stack in dig order. Adding the same material as the top merges into it.
  It has `Add` (throws when over capacity), `TryAdd` (all or nothing), `Remove(material)`
  (newest first, from anywhere in the stack), and `TryPeekTop`/`RemoveFromTop` for tipping. A
  `Version` counter lets the readout redraw only on change.
- **`Crew`** owns one inventory. The scene has a placeholder `Crew` object (`CrewView`) with no
  body and no movement.

**Digging (left click)** goes cell by cell, the clicked cell first, then outward by distance.
- Each cell is previewed with the new read-only `TerrainGrid.PeekRemove`, which applies the same
  rounding and rules as `Remove`. The cell is dug only if its loose (bulked) output fits in the
  space left.
- Cells that would overflow are skipped whole, never part-dug, so material cannot vanish.
- If no cell fits, the click does nothing and the readout says `Full: 1.1 m³ free, next dig
  needs 1.3 m³`. "Full" means "nothing fits", which can happen with space still free.
- Otherwise the readout says, for example, `Dug 3.0 m³ solid → 3.9 m³ loose (Rock, Dirt,
  Topsoil); 10 cells did not fit`. The names are the materials as they were in the ground.

**Tipping (right click)** empties the load onto the clicked cell in one go:
- **In whole height steps of the total load.** The 1 m step means only whole metres can land.
  Tipping per inventory entry was tried first and failed in play: a plains dig loads alternating
  pieces of 0.375 dirt and 0.875 loose dirt, each under a step, so nothing would ever tip. Steps
  are now built from the top of the load downward, and one step can mix pieces. The new
  `TerrainGrid.AddStack` places several pieces at once and only requires their sum to be a whole
  number of steps. It is all or nothing. Whatever is left under one step stays in the load (the
  oldest material) and goes out with the next load.
- **One tip mixes into one layer per landed material,** in the order each first appears, so the
  newest material still lands lowest. This was also found in play: a hilltop load alternated loose
  dirt and loose rock six times, and one layer per piece overflowed the 8-layer cap on a
  generated 5-layer column. A tip now adds only as many layers as it has distinct materials,
  usually two. If even that does not fit, nothing is tipped and the load is kept.
- Tipped material lands in its disturbed form (dug topsoil is carried as dirt and lands as loose
  dirt). Slump then runs as usual.

**Readout:** it shows the load top first, marked "tips first", with fill level and free space,
or "- Full". It is rebuilt only when the inventory's `Version` changes.

### The acceptance test, and what "≤ the RockLoose angle" can mean
`DugRockBulksIntoTheLoadAndTippedRockSlumpsToItsAngle`:
- Digging 3 m³ of rock into an empty crew gives 4.5 m³ of RockLoose.
- All of it is tipped on flat ground, and the terrain gains exactly 4.5 m³, before and after
  slumping.
- The pile settles so that every drop big enough to move is within RockLoose's effective angle.

Two deliberate details:
- It runs at a **0.5 m step**. At the game's 1 m step only 4 of the 4.5 m³ can land, and the
  0.5 stays in the load. A separate test covers that case and checks nothing is lost.
- "Within the angle" is the **effective** angle: 38° for thick loose rock, steeper for loose
  skins thinner than 2 m (the thin-layer bias from reference section 3). A plain "every slope
  ≤ 38°" is impossible by design at our step sizes: a one-step drop never moves, and thin skins
  are meant to cling.

### Verified in play, through the real mouse path
Clicks were queued through the Input System, with runInBackground and input routing on for the
run only.
1. On a hilltop: `Dug 3.0 m³ solid → 3.9 m³ loose (Rock, Dirt, Topsoil); 10 cells did not fit`.
2. Clicking again: `Full: 1.1 m³ free, next dig needs 1.3 m³`.
3. Right click elsewhere: `Tipped 3.0 m³ (Loose rock, Loose dirt); 0.90 m³ kept (under one 1 m
   step)`. The slump queue then emptied.

Terrain plus load gained exactly 0.90 m³, the bulking; nothing was lost.

Out of scope, as specified: units moving, pathfinding, designations and hardness gating.

## 2026-09-19 — Vertical Slice 2: one digger that has to get there

### What was built
- **`CrewUnit`** (plain C#, `TinyDiggers.Units`). It owns the `MaterialInventory`, has a
  position in cell units and moves at 3 cells/s. It turns toward travel at 360°/s and follows the
  surface height with an exponential blend. Its body sits on the bilinear surface from the new
  `TerrainSurface`, which uses the same corner averages as the smoothed renderer, bit for bit.
  `CrewUnitView` is a thin MonoBehaviour: a yellow 1 × 0.8 × 2 box and the path as a line. The
  clickable Slice 1 `CrewView` is gone, and `Crew` became the static `Excavation` (Dig/Tip).
- **`GridPathfinder`**: A*, 8-connected.
  - A step is allowed only if the height difference is at most `MaxStepHeight` (1.0).
  - A diagonal is allowed only if both orthogonal corner cells are steppable from the start and
    into the goal (no corner cutting).
  - Cost = distance × (1 + heightDelta × `SlopeCostFactor`).
  - It has `TryFindPathToAdjacent`, a Dijkstra `TryFindNearest(isGoal)` that the job loop uses
    so "nearest" means nearest by path cost rather than straight line, and `FloodReachable`.
  - It uses generation stamps, so there is no per-search clear.
- **`DesignationMap`**: one Dig or Fill target height per cell. It listens to `CellChanged` and
  clears a designation the moment it is met, which is how "persist until met" works.
- **Input.** LMB/drag designates dig to H and RMB/drag fill to H. MMB click clears under the
  brush; MMB drag rotates the camera, having moved off Q/E. H follows the hovered cell; Q/E nudge
  it one step and lock it; R unlocks. The preview is a sheet at H with an "H x m" label. The
  overlay is red for dig and blue for fill, drawn with a new unlit overlay shader.

### Job loop rules, and why
1. Dig the nearest reachable dig cell, one step at a time, until it is met or the load is full.
2. When full, or when no dig is reachable, fill the nearest reachable fill cell. It tips only
   whole steps and never more than the target, and never above stand + reach.
3. Otherwise, **and only when full**, dump at the nearest reachable cell that:
   - is not the unit's own cell;
   - is at least as high as the stand;
   - has no designation in its 3×3 neighbourhood;
   - has had no cell in its 3×3 dug or filled by this unit (a `_worked` bitmap).

   The first version dumped whenever it held anything and had no dig. In tests it refilled
   finished pit cells and the lowest terrace it had just cut, undoing its own work. Holding a
   part load while idle is deliberate; tipping it would only waste a step.
4. Nothing reachable: state Unreachable, and the readout says `UNREACHABLE: dig (x, z) to H m (+N
   more)` for the nearest one.
- **Reach**: a cell can be worked only from an adjacent stand cell within ±`DigReachLevels` (2)
  height steps.
- **The unit never stands on a cell that is still dig-designated.** Without this rule it happily
  dug the ground from under itself, and on a terrace it could cut down its own route back.
  With it, the upper terraces of a mound are *properly* unreachable until the player leaves a
  step, which is what the slice asks for.
- **Re-path** happens when a cell on the path, or a diagonal corner cell of it, changes height.
  Changes elsewhere are ignored (tested both ways). Slump runs after tips as before.

### Slump bug found in play and fixed
The first play run built the mound as soil-capped rock. The walls slumped, every terrace became
reachable and all 58 designations completed. That is the behaviour the reference intends for
soil, but it exposed a bug. One slide carries a whole step, which can span several layers,
yet only the top layer's angle was checked. A 0.3 m topsoil cap therefore dragged rock down at
soil's angle.

The effective angle is now the maximum over every layer the slide would carry. New test:
`ASoilCapSlidesOffButTheRockUnderItHolds`.

### Verified in play (rock outcrop, screenshots in the session scratchpad)
- **Setup.** Flat ground at 20 m, and a bare-rock 7×7 mound with terraces at 24/23/22/21. Dig to
  20 was designated over the whole mound, and fill to 20 over a 3×3 dip 2 m deep.
- **Start:** `UNREACHABLE designations 25`, the two inner rings.
- **After 186 s (game time, ×4).** Outer rings dug to 20; the ring-3 spoil went into the dip,
  whose fill was met; the rest was dumped clear of the work. Then the unit stopped with `UNREACHABLE:
  dig (265, 255) to 20 m (+8 more)`: ring 1 is at 23, three levels above anywhere it can stand.
- **Stepped path.** A player-designated stair (fill (263,256) to 21 and (264,256) to 22), with
  the top 3×3 re-designated dig to 22. The unit built the stair from its load, climbed it, benched
  the top to 22, and after a final dig-everything-to-20 took the mound to the ground. Max height
  inside the old footprint was 21.00, where a later dump landed. Every designation was met.

Out of scope, as specified: multiple units, auto-ramping, hardness gating, vehicle stats, economy
and art.

## 2026-09-19 — Vertical Slice 3: the crew carves its own ramp

Goal from Ronan: UNREACHABLE should be rare. When a dig cannot be reached, the unit works toward
it on its own.

### The 340 ms frame from Slice 2, found and fixed
It was measured in play before any change.
- **Cause.** While UNREACHABLE the unit rethinks every second. Each rethink flooded all 262,135
  cells of the map for the unreachable count, which took 17 ms. It then ran a nearest-first
  Dijkstra search for each job kind it tried, and each search expanded every reachable cell
  (the whole map) and found nothing, at 105 ms per search.
- **The frame.** With a part load that was a dig search plus a fill search: about 185 ms at
  timeScale 1, and more at ×4.

The fix has three parts:
- **`ReachabilityCache`.** One flood from the unit's cell, stored as a set.
  - Any `CellChanged` marks it stale, and it re-floods lazily on the next query.
  - Moving inside the set never re-floods, because every cell of a connected area reaches the
    same area.
  - "Is this reachable" is now an array lookup.
- **A set-lookup precheck before every nearest-first search** (`AnyCandidate`). If no designated
  cell has a reachable, standable neighbour it could be worked from, no search runs.
- **`FloodReachable` reads the height array directly.** This took the full-map flood from 17 ms
  to 8 ms.

**After** (play mode, same stuck scenario):
- Rethink with nothing changed: 0.01 ms.
- Rethink right after a terrain change: 7.8 ms, which is one flood.

Still owed: the 8 ms flood recurs on the first rethink after any dig. If that shows up on a
mid-range GPU/CPU check, the next step is an incremental or local re-flood.

### The spec's ramp alone could not do the mound: benching added
Taken literally, the ramp rule is to dig the first too-steep corridor cell to one step above the
cell before it, when nothing is reachable. That cannot finish the 4-terrace mound:
- Nearest-first digging takes the two outer rings to 20 m first, because they are reachable.
- That leaves the inner 3×3 at 23–24 m behind a 3 m face.
- With dig reach 2, a 3 m face cannot be cut from below. No sequence of Auto steps fixes that.
- The ramp only triggers once it is already too late.

So `AutoRamp` also turns on **benching** (`CrewUnit.DigFloor`):
- **Floor.** A dig cell is never taken below one climbable step under its highest neighbouring
  dig cell. A designated hill therefore comes down in 1 m layers and stays a staircase the unit
  can drive.
- **Standing on benched cells.** A dig cell sitting at its floor is "benched" (nothing can be dug
  there yet), and the unit may stand on it.
- **Pit guard.** A benched cell is not a stand if any undesignated neighbour is higher. Benching
  is for taking hills down; in a pit it would let the unit dig itself in.
- **Counting.** A cell held up by its floor is "waiting", not UNREACHABLE.

**Corridor Auto ramps** are built as specified. They handle cliffs in *undesignated* ground,
such as a dig on top of a plateau with 2 m faces:
- **Corridor.** A* from the unit to the nearest unreachable dig (straight-line nearest), with no
  step limit and a penalty of 20 per metre over the climb limit. It is 4-connected, so the
  finished ramp needs no corner cuts. It may only cross undesignated or already-reachable cells.
- **Placing a step.** At the first too-steep step, the higher cell gets an Auto dig to one step
  above the lower cell, worked from the cell before it. This covers drops as well as climbs.
  When the step is met, the corridor is re-planned and the next step placed.
- **Ending.** The ramp ends when the target can be worked, and any Auto step left is removed.
- **Blocked.** No fixable step (the cliff is out of reach, it would cut into a player designation,
  or there is no route) means blocked. The target is left alone for 10 s and the status says
  `ramp blocked: …`.
- **Never over player cells.** Auto designations never go on player-designated cells; they carry
  an Auto flag in `DesignationMap`.
- **Cancelling.** The player cancelling one (MMB, or designating over it) raises
  `AutoCancelled`, and the unit holds off that target for `RampCancelSeconds` (10).
- **Toggle.** `CrewUnitView.autoRamp` in the inspector. Off gives exact Slice 2 behaviour; the
  Slice 2 terrace test now runs with it off.

### Dump heaps: 2 m heaps walled the unit in
Found by the headless mound test. Heaping spoil up to stand + dig reach (2 m) built a closed ring
of 2 m heaps around the work. The unit then could not drive out, and with a full load had
nowhere left to dump. Heaps (the undesignated dump and Dump Zones alike) now go only one
climbable step above the stand, so a heap can always be climbed and heaped further.

### Dump Zones
- **Marking.** Shift + RMB drag marks green Dump Zone cells, a separate layer in
  `DesignationMap`. They never auto-clear and do not count as work. MMB clears them.
- **Tipping.** With a full load and no reachable fill, the unit tips on the nearest reachable
  zone cell. Cells below the stand come first (filling them level with it), then heaps one step
  up. Only with no reachable zone cell does it fall back to the old nearest-undesignated rule.
- **Part loads.** With nothing reachable to dig, a part load is tipped at the zone and the unit
  idles. It can only tip whole height steps, so under 1 m³ can stay in the bucket. That is not
  quite the spec's "idle empty", and it is unavoidable with 1 m steps.

### Readout
The readout adds:
- the target (job, cell and stand) and `ON AUTO RAMP`;
- whether auto-ramping is on;
- the Auto designation count and the ramp target;
- the ramp planner's last note;
- the Dump Zone cell count;
- the new control.

### Verified in play
**Mound.** The Slice 2 rock mound: 7×7, bare rock, terraces 24/23/22/21 on 20 m ground.
- Setup: dig to 20 over all of it, Dump Zone on the 3×3 dip (2 m deep), `autoRamp` on, hands off.
- Finished in 800 s game time (×4). The mound is flat at 20.00 and it was **never UNREACHABLE**
  (longest 0.0 s).
- It never needed an Auto step. Benching kept the terraces drivable: it drove up and took the top
  down first (screenshot at t = 3 s, standing at 22.7 m).
- All spoil went to the Dump Zone. It filled the dip, then heaped the zone to 23 m, and slump
  spread the heap past the zone's edges. The unit ended idle and empty.

**Ramp.** 2 m tiers of undesignated rock (20, 22, 24 m) with a 3×3 dig on the plateau.
- The unit cut a hatched Auto step (264, 251) to 23 m from the band.
- The readout showed `ON AUTO RAMP` with the ramp target.
- It then drove up and finished the dig in 21 s.
- Only one Auto step was needed, because the unit started on the 22 m band. The two-tier,
  two-step case is covered by `CutsAClimbableRampUpTwoTiersToReachItsTarget`.

### Tests (211/211 green)
New tests:
- Reachability cache: re-floods only after a height change; re-floods when asked from outside its
  set.
- An UNREACHABLE unit runs no search and floods once.
- Auto ramps:
  - the ramp climbs two tiers;
  - the corridor is monotonic and never more than a step once cut;
  - Auto steps are removed when the target becomes reachable;
  - cancelling holds off the target for the timeout, then it comes back;
  - designating over an Auto step counts as cancelling;
  - a 4 m cliff is reported, not ramped;
  - with AutoRamp off, no Auto steps are made.
- Mound: the headless 4-terrace mound finishes, never UNREACHABLE for 3 s or more.
- Dump Zones: spoil lands only in the zone; with no zone, a part load is kept.

## 2026-09-20 — Rulings, then Vertical Slice 4: a crew

### Ronan's rulings on Slice 3
- **Benching and auto-ramping are now separate switches.** `JobDispatcher.Benching` takes
  designated ground down in drivable layers; `AutoRamp` cuts corridor ramps. Both default on and
  both are on the Crew object in the scene. Slice 2's terrace test runs with both off, which is
  exactly the Slice 2 behaviour.
- **Dump Zones have a cap.** `DesignationMap.SetDumpZone` takes a cap height, and nothing is
  tipped above it. The tool marks a zone at H + `zoneCapAbove` (3 m by default), so Q and E, which
  already move H, move the cap; the cursor label shows it while Shift is held.
- **A full Dump Zone is reported,** on the unit (`DumpZoneFull`) and in the readout, and tipping
  falls back to the old rule (nearest cell that is no lower than the stand, clear of designations
  and of the crew's finished work).

### Slice 4: what a crew changes
**`JobDispatcher`** is new and owns everything that has to be decided once for the whole crew:
the designations, claims, reachability, benching, ramps, and who is standing and driving where.
A `CrewUnit` now keeps only its own body, load and job. The old single-unit constructor still
works and quietly makes a dispatcher of its own, so the Slice 1-3 tests are unchanged.

- **Claims.** A unit claims the cell it is about to work; a claim is released when it changes
  job, gives up, or is disposed of. Designations claimed by another unit are invisible to a
  unit's job search, so no two units take the same cell.
- **Ramps.** The dispatcher plans the crew's one ramp; a unit that can reach nothing asks for it.
  The Auto step is an ordinary designation, so whichever unit is nearest cuts it. Two units can
  no longer plan competing ramps.
- **Finished-work memory is the crew's.** This was a bug found by the new mound test: with one
  memory per unit, one unit dumped spoil on ground another had just finished, and a mound cell
  ended 1 m proud. `JobDispatcher.MarkWorked` / `NearWork` are shared now.

**`RegionMap`** replaces the per-unit reachability flood. The map is labelled into connected
regions, so "can this unit reach that cell" is a comparison of two labels, shared by the crew.
A change re-labels only the regions it touches: their cells are unlabelled and re-flooded, which
splits a region that has been cut, and a re-flood that runs into an untouched region swallows it,
which joins regions a fill has connected.

Measured in play with 4 units (512x512 map):
- A change inside a walled-off 9x9 region: **0.08 ms, 81 cells re-labelled**.
- A change in the open: **9.5 ms, 262,144 cells** — on this map the walkable ground is one region,
  so "only the region that changed" is the whole map. That is the honest ceiling of this design;
  the win is that it is one rebuild for the crew instead of one flood per unit.

**Traffic.** Units never share a cell. A unit whose next cell is taken waits 1 s
(`TrafficWaitSeconds`), then re-paths with the occupied cells blocked. If there is no way round
it gives its job up and drives to the nearest cell that is neither occupied nor on another unit's
path (state Yield), which is what breaks a one-cell-corridor deadlock: without it, two units met
head-on, both waited, both re-planned, and neither moved.

**Tipping with a crew.** No unit tips where another is standing, or on any cell on another unit's
current path (the dispatcher keeps each unit's remaining path cells).

**Selection.** A left click on a unit selects it (the body turns white) instead of designating;
Escape clears it. The readout lists the crew one line each, and gives the selected unit (or unit
0) its state, load, job and how many path cells are still ahead.

### Verified in play: 4 units, hands off
Same rock mound as Slice 3 (7x7, terraces 24/23/22/21 on 20 m ground), dig to 20 over all of it,
Dump Zone on the dip capped at 21 m, `autoRamp` and `benching` on, nothing else done.

- **Finished in 102 s** of game time, with the mound flat at 20.00.
- **Never UNREACHABLE** (0.0 s) and no Auto ramp was needed, as in Slice 3.
- **The same scenario with one unit took 345 s**, so the crew of four is 3.4x faster. (Slice 3's
  800 s is not the number to compare with: its Dump Zone had no cap, so the single unit hauled
  every load to the dip.)
- **Idle with work available: 18.5 s** summed over 4 units across 102 s, about 4.5% of their time,
  in gaps between jobs.
- **Longest a unit waited for another: 1.0 s**, its traffic timeout.
- The zone filled to its cap and the units fell back to open ground, which the readout reported.

**A cap is a tipping rule, not a wall.** The dip ended at 22 m against a 21 m cap: units tip only
up to the cap, but slump can still carry material over it from heaps beside the zone.

### Tests (218/218 green)
New: a designation is never given to two units; a claim is released when its unit goes; units
never share a cell and never tip under one another; two units sharing a one-cell gap finish
without deadlocking and never wait more than a few seconds; four units take a mound down without
sitting idle; regions rebuild only after a change and only touch the region that changed; spoil
stops at the Dump Zone cap; a full zone is reported and the old rule takes over.

## 2026-09-20 — Vertical Slice 5: loader and dump truck

Two rulings from Ronan drove this: nothing is ever tipped on undesignated ground, and the crew
splits into two roles. `TERRAIN_REFERENCE.md` section 4 now carries the tipping rule and the
roles.

### Nowhere to tip is a question, not a fault
The open-ground fallback is gone. A load can only go into a hauler, a Fill designation or a Dump
Zone. A unit that is loaded with nowhere it may tip stops in `NeedsSomewhereToTip` and says
"Needs a Dump Zone or Fill designation"; the crew list carries a warning line while any unit is
in that state. With no Dump Zone, `WithNowhereToTipEveryUnitStopsAndTheGroundOutsideIsUntouched`
checks that the ground outside the dig is exactly as it was.

The worked-cell memory and the "not beside open work" dump rules went with the fallback: there is
no longer anywhere for a unit to put spoil that it has to be kept away from.

### Tipping from the rim
To tip into an area, a unit stands on a cell beside *any* cell of it and tips onto that cell. The
cell can be any depth below; dig reach only limits tipping up. Rims above the cell come first, so
the material runs in. The rest is slump's job.

Three rules keep that from going wrong, each found by a test or a play run:
- **A heap is never left more than one climbable step above the unit.** Tipping up to dig reach
  (2 m) built heaps that could not be driven onto, and a Dump Zone walled itself off after one
  pass round its edge.
- **A unit never stands on ground that is to be filled.** Otherwise a hauler drove into the pit
  it was filling and worked from inside it.
- **Standing on a Dump Zone heap, a unit only tips level with itself or higher.** Tipping downhill
  from up there buried the way it had come, and the unit ended stranded on its own spoil. Tipping
  *into* a zone from outside is still unrestricted downward, which is what fills a dip.

A Fill designation may be heaped one step above its target (`FillCap`): that step is what slump
spreads into the cells no rim touches. Without it an area is only ever filled around its edge.

**Met means settled.** A single tip could take a cell to H and slump could take it back below in
the same frame, and the designation had already cleared itself. `DesignationMap.Prune` now drops
met designations one tick later, from `JobDispatcher.Tick`, after the last frame's slumping.

### Roles
- **`UnitRole.Digger`:** dig reach as before, 5 m³ scoop. Digs; empties into a hauler beside it at
  `TransferRate` (5 m³/s, so a full scoop reads as a second of work); tips into a Fill or Dump
  Zone itself only when there is no hauler worth waiting for.
- **`UnitRole.Hauler`:** never digs or fills the ground; 20 m³ of mixed material. Serves the
  reachable digger with the fullest load that has no hauler, parks by it, and leaves when full,
  when the digger has nothing left to give, or after `ParkPatience` (10 s) with nothing tipped in.
- `Excavation.Transfer` moves material from the top of one inventory into another, conserving
  volume and material.
- The dispatcher holds the links: one hauler per digger, one digger per hauler, released when
  either finishes or is removed. A digger only waits for a hauler that has room and is not itself
  stuck for somewhere to tip.

**Haulers park as near as there is room, and the digger walks out to them.** Parking beside the
digger, as specced, deadlocked the first play run: benching takes diggers onto the designated
ground, where every neighbouring cell is designated and no cell is a legal park. Haulers now park
within `ParkRadius` (3) and a full digger walks the last few cells to meet one.

### Readout
Each crew line carries the unit's role, load and capacity. A selected digger shows its hauler and
how long it has waited for one; a selected hauler shows the digger it serves and how much it has
carried. The warning line sits above the crew list.

### Verified in play (screenshots in the session scratchpad)
Rock mound, dig to 20 m, Dump Zone in a dip **40 cells away**, `autoRamp` and `benching` on, hands
off, 2 diggers + 2 haulers.
- **Finished in 554 s** of game time, mound flat at 20.00.
- **The same job with 4 diggers and no haulers took 672 s**, so the loader-and-truck crew is about
  18% quicker, and it is the haul length that makes the difference: in Slice 4, with the zone 12
  cells away, four diggers did it in 102 s.
- **Haulers carried 86 m³** of the roughly 126 m³ dug. The rest the diggers took themselves, when
  both haulers were away.
- **Diggers waited 72.6 s in total over 45 waits**, about 6.5% of the run: that is how often a
  hauler was not yet back when a scoop filled.
- **No spoil landed anywhere but the Dump Zone and its slump overspill** (0 stray cells), and no
  unit ever stopped for want of somewhere to tip.

### Tests (228/228 green)
New: transfer conserves volume and empties into a hauler beside the digger; a hauler leaves when
full and when its patience runs out; a digger with a full scoop and no hauler in reach waits and
changes nothing; two haulers never serve one digger; a hauler fills a 2 m deep 5×5 pit from the
rim without standing on ground it is filling; a 2+2 crew takes a mound down; with nowhere to tip
every unit stops and the ground outside is untouched.

Several older tests now need somewhere for the spoil to go, and the mound tests run the slump
simulator, because with rim tipping it is slump that moves material into the middle of a zone.
The Slice 2 terrace test runs *without* slump on purpose: its point is terraces that stay put
until a unit cuts them, and dirt terraces 3 m proud do not stay put.

## 2026-09-20 — Slice 5 corrections, from watching it run

Three things Ronan spotted in play, each fixed and covered by a test.

### Diggers were hauling
A digger fell through to carrying its own spoil whenever no hauler was *usable at that moment*,
which on the 40-cell haul meant most of the time: both haulers were away, so both diggers drove
off too. A digger now waits whenever the crew has **any** hauler that is not itself stuck, rather
than one that happens to be free. Only a crew with no haulers at all does its own hauling.

`ADiggerLeavesTheHaulingToTheHaulers` pins it: with a hauler in the crew and the Dump Zone across
the map, the digger never enters Tipping.

### Haulers went back half full
The hauler loop served a digger whenever it was not *completely* full. So a hauler tipped one
metre into the zone, stopped being full, and drove all 40 cells back to its digger with 19 of
20 m³ still in the bed. A load is now delivered in full before a hauler goes back to serving
(`AHaulerEmptiesItsBedBeforeGoingBackToItsDigger`).

That one change took the same job from **842 s to 147 s**.

### Units drove through each other
Cell occupancy only changes as a unit crosses a cell's edge, so two units could overlap on a
boundary or clip past each other on a diagonal. Two attempts did not survive contact:
- **Reserving the cell being driven into** deadlocked the crew: reservations are visible to the
  planning checks too, so parked haulers blocked the stands and paths their own diggers needed.
- **Refusing diagonal steps past an occupied corner** deadlocked a unit against a parked hauler:
  the path stayed the same, so it was blocked, re-planned the same route, and blocked again.

What works is a plain clearance test on the movement itself: a unit will not move to a position
that closes to within `Clearance` (0.7 cells) of another unit. Units already parked closer than
that may still move, as long as they are not getting closer, so a hauler can sit beside its
digger. 0.7 is just under the 0.707 of a corner-to-corner pass, so units can still drive past one
another diagonally, and nothing closer is allowed. Blocked units use the existing wait, re-path
and stand-aside machinery.

Placeholder bodies now fit inside a single cell (0.95 long). Anything longer overlaps its
neighbour when two units work side by side, which is exactly what looked wrong.

### Verified in play again (2 diggers + 2 haulers, zone 40 cells away)
- **147 s**, against 554 s before these fixes and 672 s for four diggers with no haulers.
- **Closest approach between units: 0.71 cells** — the diagonal-pass limit, so no overlap.
- **Diggers never tipped** (0 frames in Tipping) and never left the cut.
- Haulers carried **117 m³** of the ~126 m³ dug; diggers waited 165.6 s in total, which is the
  cost of two trucks on a 40-cell round trip.
