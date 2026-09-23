# TinyDiggers — decisions and findings

Append-only log of choices that the code cannot explain by itself. Git records what changed;
this records why.

---

**NEXT:** Slices 14 (sea floor), 15 (load) and 16 (terrain LOD) are done: start-up about 5.3 s, frames 9.5 ms median; waiting on Ronan's look. PromptWaffle Dynamic Water has a simulation, a URP surface, a TinyDiggers bridge and a
swell, spillway overflows (a safety valve at sea level), game logic reading it and a Basic Zone sample; waiting on Ronan's look. Open:
- start-up is about 5.3 s at 3104², nearly all generation (5.0 s);

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

## 2026-09-20 — Vertical Slice 6: camera, toolbar, and the turntable

No new simulation rules. This slice is about being able to play the thing: a camera that goes
where you look, a toolbar of tools with modes, and a world that reads as a model on a table.

### The world is a disc
`TerrainGrid` gains a per-cell **void** flag. The grid stays square and flat-array indexed; the
cells outside the disc are simply off the map. Voiding a cell empties its column, and void cells
are:
- not drawn (both renderers skip them);
- impassable (`GridPathfinder` and `RegionMap` never step into or label them);
- not designable (Dig, Fill and Dump Zone all refuse);
- not slumped into or out of.

The generator masks everything outside `min(width, height) / 2 - 2` cells of the middle, and eases
the last 6 cells of land to one rim height with a smoothstep, so the edge is a clean cut instead
of noise falling off a cliff. `TerrainSurface.CornerHeight` ignores void neighbours, so a rim
corner takes the height of the land beside it.

**The table.** `TableView` builds a plinth (a cylinder 6 cells wider than the disc and 26 m deep,
with a darker band at the top) and swaps the skybox for a flat two-colour gradient. Two details
that were wrong at first and are worth remembering:
- The plinth top must sit *below* the rim (0.75 m here). Level with it, the two surfaces fight for
  the same depth and the flats come out striped.
- A custom skybox shader must not tag its pass `LightMode = UniversalForward`, or URP never draws
  it and you get the camera's clear colour.
The plinth is also wider than the land on purpose: it hides the stepped edge of the circle mask,
so from above the world reads as a clean disc.

### The camera
`RtsCameraRig` is plain C# and holds the arithmetic — pan, zoom, pitch, the pivot and its clamp to
the disc — so it is unit tested. `RtsCamera` reads input into it and eases the camera toward it
with SmoothDamp.
- WASD or the arrow keys pan on the ground in screen-relative directions, and the speed scales
  with zoom, so a keypress covers a similar share of the screen at any distance.
- Q/E and middle-drag turn about the pivot. Fully zoomed out, that is the lazy Susan.
- The wheel zooms in even fractions toward the ground under the cursor: the pivot slides toward it
  by as much of the way as the zoom closed.
- Pitch follows the zoom (35° close, 60° far), R and F nudge it ±10°, Home flies back to the
  middle of the map fully zoomed out, and edge panning is off unless asked for.
- The pivot is clamped to the disc, so the land cannot be shoved off the table.

Q and E used to move the designation height; that is now PageUp and PageDown, shown in the
toolbar.

### The toolbar and the tools
`PlayerTools` is the player's hands: one mode at a time, left button applies, right button cancels
or clears, Escape goes back to Select, hotkeys 1-7, `[` and `]` resize the brush or the road.
`ToolbarView` builds one Canvas in code with a button per tool and a line that says what the tool
will do.
- **Select**, **Dig**, **Fill** as before, with the brush now on the tool rather than the terrain.
- **Dump Zone**, **Level** and **Clear** are dragged rectangles.
- **Level** designates every cell in the rectangle to H, digging or filling whichever each one
  needs (`Blueprints.PlanLevel`).
- **Road** takes clicked control points, each at the ground height where it was clicked, and lays a
  ribbon 3-7 cells wide whose height is interpolated along each leg. The ghost shows the whole
  ribbon with the cut and fill it would cost, in m³, before anything is committed; the ghost lies
  on the road bed, or on the ground where the road would cut into it, so it always reads as one
  band. A leg steeper than 0.25 m per cell turns the ribbon orange and refuses to confirm.

The debug readout is still there, behind F3, and now defaults to hidden.

`Blueprints` (plain C#) holds the planning arithmetic for both tools, so the ghost, the cost and
the designations all come from the same code, and it is tested without a scene.

### Verified in play (screenshots in the session scratchpad)
- **(a)** The whole disc on its plinth against the gradient, 202,744 cells of land out of 262,144,
  turning as Q/E would turn it.
- **(b)** A five-cell road across a 22 m hill, level at 20 m: the ghost ribbon with
  "cut 579 m³, fill 33 m³" in the toolbar.
- **(c)** The same road under construction: red dig designations along the route, the first
  stretch already graded, two diggers working it and two haulers running to the Dump Zone.

### Tests (238/238 green)
New: void cells hold nothing and refuse every designation; units cannot path, flood or region
across the void; a rim corner takes its height from the land beside it; levelling a slope cuts the
high side and fills the low and leaves the void alone; a road interpolates between three control
points and is the width it says; a road is refused above the max grade and allowed at it; a road
over a ridge costs what it says. The camera rig tests were rewritten for the new rig: panning
scales with zoom and follows the heading, the pivot stays on the disc, zoom steps evenly and
toward the cursor, pitch follows zoom and the nudges, and Home re-centres.

The generator tests now skip void cells, and the "most of the land is terrace tops" threshold came
down from 80% to 55% of the square grid, because the disc only covers about 78% of it.


---

## Slice 7 — material detail (2026-09-20)

Ground now reads as gravel, soil and grass instead of flat colour, entirely in the fragment
shader. The terrain data and the mesh are untouched.

### How it is put together
- `TerrainTextureSet` (ScriptableObject) names, per material, an albedo, a normal and optionally a
  "cut" pair for freshly exposed faces. **TinyDiggers > Generate Placeholder Terrain Textures**
  writes tileable 1024² procedural PNGs and links them. Dropping real PNGs in over them needs no
  code change.
- Deviation from the brief: the textures live in `Assets/TinyDiggers/Terrain/Textures/`, not
  `Assets/Terrain/Textures/`, to match where everything else in the project lives.
- `TerrainMaterialAtlas` packs the set into two `Texture2DArray`s (albedo sRGB, normal linear),
  slice = material id, cut variants appended after the last id. One material for every chunk, so
  the map is still one draw call per chunk whatever it is made of.
- `TerrainCellMap` is one texel per cell: red the material on top, green what a cut would expose.
  Only changed cells are rewritten and at most one upload happens per frame.
- `TerrainTriplanar.shader` blends the four nearest cells' materials, samples triplanar from world
  position, adds a fine detail normal over the albedo and a slow ~12 m mottle, darkens steep faces
  and takes their exposed/cut material, and lights it with URP's own `UniversalFragmentPBR`.
- `TerrainDetail` holds the three together and hands `TerrainView` one material; the tuning
  (repeat scales, detail and mottle strength, blend width) is public on `TerrainView`.

### Two things that were wrong and are worth remembering
- **Texture reads inside the four-cell loop had no derivatives.** A texture sample in branchy flow
  cannot pick a mip, which flattened whole surfaces to a single colour and made the rest crawl.
  The derivatives are now taken once, in uniform flow, and the samples use `SAMPLE_*_GRAD`.
- **The blend has to widen with distance.** A half-cell transition is narrower than a pixel once
  the camera is far enough away, which is exactly when the sawtooth appears. The blend width is
  now `max(_BlendWidth, fwidth(cell) * 1.5)`.

### A defect found while shooting the screenshots: the plinth's lid
The Slice 6 plinth was a solid cylinder whose top sat 0.75 m under the rim. Anything below that —
a pit floor, or simply low-lying ground — was drawn in plinth cream instead of terrain. It had
been read as terrain colour until a deep pit made it obvious. The plinth is now a ring: an outer
wall, a collar across the top from the ragged edge of the land out to the clean circle, and a
floor. Nothing of the plinth appears inside the land any more, however deep the digging goes.

### The sawtooth rim item
The material-boundary blend closes the part of it that was material edges: at every distance a
boundary between two materials is now a soft line across the ground rather than a stair of cell
edges (compare the disc shot before and after). What is left on a pit rim is the mesh itself
stepping by one height step — geometry, not shading — and that is not this slice's to fix.

### Perf at 2560x1440 (this machine, editor play mode, still scene)
| view | frame | worst | draw calls | triangles | set-pass |
|------|-------|-------|-----------|-----------|----------|
| the Slice 0 pit, 70 m out | 2.00 ms | 2.62 ms | 51 | 65,090 | 12 |
| a cut face, 16 m out | 1.74 ms | 2.48 ms | 33 | 11,938 | 14 |
| the whole disc, 620 m out | 1.99 ms | 3.36 ms | 243 | 408,824 | 12 |

243 draw calls for 240 visible chunks: one per chunk, as intended. Measured by
**TinyDiggers > Slice 7 Capture** (play mode), which also writes the three screenshots to
`Screenshots/Slice7/`.

### Tests (245/245 green)
New: the cell map is one texel per cell, red is the material on top, green is what a cut exposes
under a thin top layer and the top material itself under a thick one, a changed cell is uploaded
on the next flush, a flush with nothing changed uploads nothing, and void cells hold no material.


---

## Slice 7 follow-up — the texture set to Ronan's brief (2026-09-20)

Ronan wrote a texture brief: 1024² seamless, one tile covers 0.5 m, matte and muted (Tiny Glade,
not photoreal), no shadow baked into the albedo because the lighting comes from the normal map,
and even luminance so a repeat grid does not show. Ten named looks: grass, topsoil, dirt,
dirt_loose, sand, rock, rock_loose, rock_cut, granite, bedrock.

**Ruling: the generator stays procedural** rather than moving to AI image generation — it costs
nothing, is deterministic, regenerates on demand, and the ScriptableObject slots still take real
PNGs later without a code change. **Ruling: `rock_cut` is a cut variant of Rock**, not its own
material id, so steep freshly-dug faces pick it up automatically and nothing in the sim changes.

How the brief turned into code:
- Feature sizes are written in pixels at 0.5 m a tile (`Feature(px)`), so "a pebble is 10–30 px"
  and "a clod is 20–60 px" are in the recipes rather than in a comment.
- The albedo moves the material's colour by about ±10% and no more; relief lives in the normal map.
- Every pattern is flattened before use: hard blur, subtract, put the mean back. That is what
  keeps luminance even across a tile and stops the repeat showing at RTS distance.
- A second colour is flecked through each look where it belongs — clover in the turf, roots in the
  topsoil, small stones in the dirt, lichen in the hollows of weathered rock, dark minerals in the
  granite.
- Rubble and fresh fractures use a square-metric Worley (`Angular`) so they read as broken stone
  rather than pebbles.

Two senses were inverted on the first pass and are worth remembering: Worley is **0 at a feature
point**, so a lump (a clod, a boulder, a piece of rubble) is `1 - Worley`, and the shadowed gap
between pieces is where Worley is **high**. The first run had rubble with sunken centres and proud
gaps, which reads as a stamped pattern rather than as stone.

The material ids' own colours in `MaterialTable` are left alone: those are what a material reads
as in one flat pixel (designations, a minimap later); the brief's colours are what it reads as
under a metre of texture.

Clay is not in the brief but the material exists, so it gets a look rather than the flat-colour
fallback.

Menu item renamed **TinyDiggers > Generate Terrain Textures** (it is no longer writing
placeholders), and it now deletes PNGs it no longer produces, so the rename from `dirtcut`/
`rockcut` left no litter. 11 looks for 9 materials. Tests still 245/245 green; frame time at 1440p
is unchanged (1.87–1.88 ms).


---

## Camera — free tilt, lens, and presets (2026-09-20)

Ronan's ruling: the pitch is the player's, not the zoom's.

- **Tilt is free between 10° and 89°.** R and F tilt while held rather than nudging in steps, and
  a middle-button drag now tilts with vertical motion as well as turning with horizontal. Dragging
  up pulls the camera over the map.
- **The old zoom-driven pitch curve is a toggle**, `RtsCameraRig.PitchFollowsZoom`, off by
  default. With it on, the camera behaves exactly as it did before, `PitchOffset` and all; with it
  off, the zoom only changes how far back the camera sits.
- **The lens is adjustable**: Shift and the wheel moves the field of view between 15° and 60°. A
  narrow lens from further back is where the tilt-shift feel comes from, without any post effect.
- **Presets.** `CameraPreset` is a ScriptableObject holding pitch, FOV, zoom distance and the
  pitch-follows-zoom flag — a way of looking at the map, deliberately not a place on it, so the
  pivot and heading are not in it. F5 saves the live camera to
  `Assets/TinyDiggers/Camera/Default.asset`, F6 loads it back, Home re-centres using it, and it is
  what the game starts with. The default is pitch 50°, FOV 40°, 450 m, which frames most of the
  disc.
- **The F3 readout** now carries pitch, yaw, FOV and zoom distance, refreshed with the frame-time
  line twice a second rather than every frame.
- Saving writes an asset, which only the editor can do; in a build F5 keeps the preset for the
  session and says so rather than pretending.

Nothing else about the camera changed: panning, turning, zoom-toward-cursor, the disc clamp and
the easing are as they were.

Tests (250/250 green): the zoom leaves the pitch alone by default and drives it when told to, the
tilt clamps at 10° and 89°, the lens clamps at 15° and 60°, a preset carries the way the camera
looks but not where it is, and Home takes the preset when there is one.

Worth remembering for play-mode checks: `EditorApplication.ExitPlaymode()` does not take effect
until the end of the frame, so a command that exits play and immediately reads the scene is still
reading the old session. Two readings of the camera looked like a bug that was not there.


---

## Slice 8 — island, sea and depth (2026-09-20)

### The packages
The project had only the **River Modeler extension** of Stylized Water 3, not the base asset: its
assembly definition referenced `sc.stylizedwater3.runtime`, which did not exist, its own extension
file carries a hard `#error` saying so, and it wanted `com.unity.splines` and VFX Graph, neither of
which was in the manifest. Its river source is a Unity `SplineContainer`, not a polyline, and it
asks the terrain for nothing — it builds its own mesh from the spline. Ronan removed it rather than
buy the rest of the chain. **Ruling: we write our own water**, which for a flat sheet seen from an
RTS camera is a smaller job than wiring someone else's.

### Depth and datum
Sea level is 0 m and is what every height is read against. A grid now carries a **datum** — the
height the bottom of every column sits at, -45 m on the island — so a seabed can be under the sea
and still be layers, and the bedrock base has somewhere to be. The layer cap went from 8 to 16,
because an island column is bedrock, granite, rock, clay, dirt, sand and topsoil before anything is
dug.

### Water in the sim
A cell under the sea is impassable and undiggable but still ground: fills and dump zones are
accepted on it, because reclamation is the point. Land starts one whole step above the sea, so
tipping into the shallows has to break the surface. The pathfinder, the flood fill and the regions
read one cached array of "void or water" rather than two, so this cost nothing.

### The island generator
Domain-warped fBm at 120 m, a mountain and a valley or two placed by seed, a radial mask perturbed
by noise for an irregular coastline, a shelf falling to a channel at the rim, a river traced from
the peak to the coast, and strata that follow the land. Every number is on `TerrainGenSettings`;
F2 shows the seed and a Regenerate button. The old plateau generator stays, and its tests still
use it.

Three things that had to be worked out:
- **The neighbour-step rule has to be enforced, not hoped for.** Relaxing with a naive sweep moves
  one cell a pass, which needs hundreds of sweeps on a 46 m mountain and quietly gave up at the
  cap. Four directional passes a sweep — a chamfer distance transform — settles it in a handful.
  It runs again after the river is cut, because the channel's own banks leave steps where the
  river bends.
- **A river must be stopped from digging itself under the sea.** Cutting two and a half metres
  below the ground each step, with the floor held monotonically descending, takes the channel below
  sea level halfway across the island: what should be a river becomes an inlet. The floor now
  stops at the waterline while there is still land around it, and the river ends where the land
  does, with its last point under the surface.
- **Keying materials to the steepest single step gives a checkerboard.** On quantised land a gentle
  slope is a row of one-metre steps with flats between them, so the worst step alternates cell by
  cell. Steepness is now averaged over a three by three window.

### Water rendering
`WaterView` builds two sheets and `TinyDiggers/Water` draws them. Depth over each vertex is baked
into the mesh when the sheet is built, so the shallows are pale, the channel is dark and the
shoreline foams with no scene-depth read; distance down the channel is baked in too, so a river
runs downstream. The sheets rebuild a moment after the land last changed.

### Tests (266/266 green)
New: the rim is under the sea and the middle above it; the coastline wanders rather than tracing a
circle; the river only ever descends and ends below sea level; land never steps more than a metre
to a neighbour; every surface lands on the height step; every column stands on bedrock; the same
seed gives the same island and a different seed a different one; there is sand around sea level and
topsoil well above it. Plus the water rules: depth, passability, the pathfinder and regions
refusing water, dig refused and fill and dump zone accepted, filling a water cell into land, and
cutting land back to sea level giving it to the sea.


---

## Slice 8b — landmasses and slope-readable lighting (2026-09-20)

### The land
The radial mask is gone. Land is twice-warped fBm above a threshold, which is what gives bays,
headlands and inlets; only the outer ring of the disc is forced to sea. Five archetypes bias that
field — Continent, Crescent, Twin, Archipelago, Lagoon — and a seed picks one unless the settings
asset says otherwise. Relief is a ridge along a curve with ridged multifractal noise on it, benches
in its lee, and valleys cut by one D8 flow-accumulation pass, with the rivers put in the wettest of
those valleys rather than traced downhill and hoped over.

Three things worth remembering:
- **A guarantee about the mask is not a guarantee about the land.** Culling small patches before
  the heights are final leaves specks behind, because relaxing, beaching and carving all move cells
  across the waterline afterwards. The cleanup now runs again on the finished heights, and once
  more after the last relax, dropping specks only.
- **Land has to be defined the same way everywhere.** The generator was culling patches at "above
  sea level" while the sim calls a cell land only a whole step above it. The two definitions have
  to agree or the cleanup passes over exactly the cells that will be holes.
- **A column's height is a sum of floats over a datum, and it drifts.** A cell built to exactly one
  step above the sea came out a few millionths under, which made it water — one such cell in the
  middle of a field is a hole the crew cannot cross. The land test carries a millimetre of
  tolerance now.

### The lighting
The sun is placed relative to the camera's heading, so the light always comes over the player's
left shoulder however they turn the map: one face of every terrace lit, the other not. Ambient is a
three-colour gradient rather than a flat colour, SSAO is on, and the terrain shader tints steep
faces toward a cool grey independently of the light, so slope reads even on the shadowed side.
Contour lines every 5 m and 1 m are drawn in the shader from world height, toggled with F4. All of
it lives on a `LightingSettings` asset.

Two things that were wrong and are worth remembering:
- **Shadow bias is not a detail on a stepped heightfield.** A one-metre step seen from 450 m out is
  the worst case for shadow acne: with a small bias the island shadowed itself everywhere and read
  as dark, dirty rock. The bias is now on the settings asset, set generously, with a note saying
  why.
- **SSAO's radius is in world metres.** At 0.8 m and full strength it shaded the whole island from
  far out instead of its creases. Gentled to 0.5 intensity at 0.6 m.

Also: keying soil to a hard steepness threshold drew a line across every hillside and, with the
new rugged relief, left the entire island bare. Soil now thins with slope and stops when it is too
thin to be turf, which puts grass on the rolling land and bare ground on the ridges.

### Tests (272/272 green)
New: every archetype generates for five seeds; the coastline has bays in it; no patch of land is
smaller than the minimum unless it is an archipelago; every river ends below sea level and only
descends; there are beaches on the gentle coasts and bare ground on the steep ones; and a 512²
map generates in under half a second.


---

## Slice 8c — colour and light (2026-09-20)

### The height fix that came first
A column's height was a sum of floats over a datum tens of metres below it, so it drifted by a few
millionths, and a cell built to exactly one step above the sea could come out a hair under — which
made it water, and one of those in the middle of a field is a hole the crew cannot cross. Slice 8b
papered over it with a millimetre of tolerance in the land test.

**Ruling: snap the cached surface height instead.** It is now rounded to the millimetre on every
write, and the tolerance is gone from the land test. Deviation from the brief, deliberately:
snapping to the **height step** would be wrong, because a cell part way through a slump is
legitimately between steps and rounding it would make material appear or vanish. The millimetre is
fine enough that nothing real moves — the slump's own moves are quarter-metres — and coarse enough
that float drift cannot decide land from sea. One test that compared the cached height with a raw
sum of layers now allows half a millimetre, with a note saying why.

### The colour and light pass
Judged on the Continent seed from the saved camera, zoomed out and at RTS zoom.

- **Ambient was the single biggest lever**, as the brief said: a pale warm blue sky, a warm
  off-white equator and a warm sand ground at 1.5 intensity. A fully shadowed slope now reads as
  green or brown rather than grey.
- Sun warmed to 4800 K and dropped to 1.05 so nothing clips; shadow strength 0.65.
- SSAO halved to 0.25 at the same radius: a soft crease rather than dirt.
- **The material palette was re-tuned as a set**, in `MaterialTable` and in the texture recipes
  that track it: sage-olive grass, warm ochre dirt, light warm grey rock, pale cream sand, all
  within about two stops of each other and noticeably higher in value. The old palette was chosen
  to look right in a swatch, which is why it read as dirt in game.
- Slope tint down to 12% and toward a warm grey: a cool tint on a warm palette reads as wet slate.
- Water: milky teal shallows over pale sand, deeper blue offshore, and a longer depth ramp so the
  shelf reads as shallow.
- A Volume with Neutral tonemapping, +0.1 EV, a little saturation and contrast; no bloom, no
  vignette. SMAA on the camera.
- Plinth to a warm mid-grey and the backdrop to a paler warm grey, so the table sits with the land
  rather than against it.

Slope still reads: the ridge-and-valley shot is legible at RTS zoom with the contours off, which
was the condition for the whole pass.

Before and after pairs of the same three shots (disc, ridge, coastline) are in
`Screenshots/Slice8c/`. Tests 272/272 green.


---

## Slice 8d — material assignment (2026-09-20)

Three faults, one cause and one cure.

The faults: every slope banded along its contours, single cells of the wrong material speckled the
whole island, and sand climbed mountains. The cause, in every case, was that a column decided what
it was made of on its own, from its own steepness, while it was being built.

**Surface material is now its own pass** (`SurfaceMaterials`), run over the finished heights, and
`BuildColumn` just does as it is told. In that pass:

- **Slope is measured on a smoothed heightfield** (5×5, run twice). This is the whole fix for
  contour banding: on quantised land a uniform hillside is a staircase, so a per-cell slope
  alternates row by row and paints the hill in stripes that follow the contours. Smoothed, a
  uniform hillside is one slope and gets one material.
- Thresholds are 15°, 30° and 45°, with a noise field about twenty metres across worth ±7°, so the
  boundaries wander rather than tracing a threshold.
- Patches under twelve cells are absorbed into whatever surrounds them most, the coastline's own
  blob cleanup run over materials, twice.
- **Sand is coastal by definition**: below +3 m and within ten cells of water, and the rule is
  enforced again after every cleanup, because absorbing a speck can otherwise carry a beach up a
  hillside. The cells that leaves alone are joined to their neighbours afterwards.

Two bugs the tests found on the way:
- **Clay was surfacing.** On a bare-rock cell there is no soil for it to be under, so the clay band
  became the top layer — wrong in itself, and the source of most of the single-cell islands. Clay
  is now only added when there is something above it.
- **A cleanup pass can undo the rule that ran before it.** Order matters: despeckle, enforce,
  despeckle, enforce, then repair whatever is left alone.

Also in this pass: the ridge noise is warped again at about 25 m with a medium octave over its
flanks, which breaks up the big diagonal facets the lattice was giving it; and the water reads its
depth from a two-cell blur of the seabed, so the shallows are a gradient rather than a staircase of
colour bands, with two layers of ripple at different scales and angles instead of one, which takes
out the diagonal banding at grazing angles.

### Tests (279/279 green)
New: a uniform slope well inside a band is one material; a uniform 30° slope does not alternate
along its contours; steeper ground gets a barer material; no patch survives below the minimum; sand
is never above the beach or inland of it; and the island itself has no high sand and no speckle to
speak of — "to speak of" being one cell in a thousand, because a one-cell knoll standing a metre
above a beach is legitimately not sand.


---

## Slice 8e — let mountains be mountains (2026-09-20)

Ronan's diagnosis, confirmed: the flat facets were the one-metre neighbour rule clamping every
steep face to forty-five degrees. Noise could not fix that, because the relaxation undid whatever
the noise did.

### Cliffs
Rock may now stand in a step of up to `MaxCliffStep` (3 m) to a neighbour of rock; soil keeps the
one-metre rule, because soil slumps and rock does not. The relaxation cannot ask the materials
which cells are rock — they are decided afterwards — so it works from a **cliff mask** taken off the
slope of the unrelaxed field: where the land already wants to stand up, it is allowed to. After the
materials are assigned, **both sides of every step taller than a metre are promoted to rock**, which
is the same rule from the other end and keeps the two consistent without a second material pass.
That second pass was tried and cost the half-second budget (504 ms); promoting the faces instead
brought it back to 450 ms.

### The crew and cliffs
- A cliff is a wall: the pathfinder already refused any step over the climb limit, and there is a
  test that says so for a six-metre face.
- **But a wall must not make a hill unworkable.** Two rules made a cliffed hill a deadlock:
  1. Dig reach was symmetrical, so a unit at the foot of a face could not touch anything six metres
     above it. A digger may now work a **rock** face up to `CliffReachLevels` (6) above its stand,
     taking the top step off at a time — the way a machine works a high face from below. Soil is
     not worked this way; a soil face that tall cannot exist.
  2. **Benching** held every dig cell within one climbable step of its highest dig neighbour, so
     the face could not come down until the cells behind it did, and those could not be reached
     until the face came down. Rock neighbours now only have to stay within `CliffWorkDepth` (6 m).
  With both, two diggers and a hauler take a bite out of a six-metre rock cliff from the plain,
  hands off, and there is a test that says so.

### Relief, blend and mottle
Peaks 35–45 m, ridge flanks twice as wide, and a fine 8 m octave over every slope so no face is a
plane. The material blend is two cells wide with its edge pushed about by noise, and the mottle is
at 11.3 m so it no longer beats against the one-metre grid.

### Slump
A freshly generated island, every cell queued, settles without a single cell moving — rock's angle
of repose already holds a cliff up, and there is a test that says so.

Generation: **450 ms** for 512², inside the budget.

### Tests (283/283 green)
New: only rock stands in a cliff and none is taller than the limit; a freshly generated cliff is
stable once the slump has settled; a cliff is a wall to the crew; a crew takes a cliffed hill down
from the bottom. The old "land never steps more than a metre" test now says the rule as it is:
soil never steps more than a metre, rock never more than a cliff.

## More green — what was making the island rock (2026-09-20)

Ronan asked to lower `RidgeHeight` and loosen `SlopeGrass` so more of the island is green. Measured
on Continent seed 11 at 512², those two alone moved grass only from 22% to 26% (ridge 40→30 m,
grass threshold 15°→22°), because 77% of the land sat above 25° of smoothed slope. The slope
histogram showed the materials doing exactly what they were told; the land was steep everywhere,
not only on the ridge.

The cause was `BaseHeight`, 14 m: every shore is a bank that tall relaxed down to the sea, and an
island's coastline is long and ragged, so the banks were most of the steep ground on the map.
`ValleyCut` 9 added more. `BaseRelief` barely mattered (26→12 changed almost nothing on its own) and
raising `CliffSlope` to 70 cut cliff steps from 7974 to 619 but rock only from 59% to 52%.

Measured, all with ridge 30, grass 25°, mixed 38°, cliff slope 55°:

| BaseHeight | ValleyCut | BaseRelief | grass | rock | peak | generate |
|---|---|---|---|---|---|---|
| 14 | 9 | 26 | 35% | 56% | 38 m | 405 ms |
| 7 | 9 | 26 | 46% | 37% | 31 m | 383 ms |
| 7 | 5 | 26 | 51% | 34% | 31 m | 384 ms |
| **7** | **5** | **16** | **53%** | **33%** | **32 m** | **368 ms** |
| 5 | 4 | 14 | 56% | 27% | 30 m | 385 ms |

Chosen: the bold row. It is the one with rock still reading as a ridge from the disc view; 5 m base
begins to flatten the lowlands into one plain. Set in both the code defaults and
`IslandSettings.asset`: RidgeHeight 40→30, SlopeGrass 15→25, SlopeMixed 30→38, CliffSlope 44→55,
BaseHeight 14→7, ValleyCut 9→5, BaseRelief 26→16. In the scene the land generates in 435 ms.

Cost: peaks drop to about 32 m, below the 8e target of 35–45. Before/after shots are in
`Screenshots/Green/`.

## Haze (2026-09-20)

Two causes, both in the light rather than in fog (fog is off):

1. **The grade was never applied.** `Grade.asset` had two component slots pointing at nothing
   (`fileID: 0`): the Tonemapping and ColorAdjustments made in 8c were added to the profile in
   memory but never saved as sub-assets, so the volume did nothing from the moment the editor
   restarted. Rebuilt with `AddObjectToAsset`; the file now lists two real fileIDs.
2. **The ambient was a wash.** Trilight at intensity 1.5 with a pale blue sky colour put
   ~1.0–1.3 of flat blue-white fill on every surface, which lifts the shadows until nothing has
   contrast. That is what read as haze.

Now: AmbientIntensity 1.5 → 1.1 with a slightly deeper sky/ground (sky 0.62/0.72/0.86, equator
0.70/0.68/0.64, ground 0.50/0.44/0.36); sun 1.05 → 1.7; shadow strength 0.65 → 0.75. Grade: Neutral
tonemap, +0.25 EV, contrast +6, saturation +12. A test at ambient 0.85 went too dark and the grass
went olive, so the middle value was kept.

Left for the water pass: the shallow band is an opaque milky cyan (`_Shallow` alpha 0.45, very
pale) and reads as fog on the sea. Shots: `Screenshots/Haze/` (light_ =
ambient 0.85, mid_ = chosen).

## Slice 9 — water (2026-09-21)

Approved by Ronan on 2026-09-20. Refraction: yes. Wave height: "random, a mix".

**What was built.** `WaterField` bakes depth, shore distance and shore direction per cell (29–57 ms
on 512², in-scene). `WaveSet` makes a seeded Gerstner mix from `WaterSettings`. The first wave is
always 85–100% of the height range and the second 0–15%, the rest lean small, and each is capped
at 1/14 of its length. The sum of Q·k·A is held at `Steepness` (≤ 1), so no crest ever loops. The
shader does the swell with calm and rough patches (`GustSize` 140 m, calmest 30%). The swell dies
in the shallows and near the shore. Beach waves are bands on the shore distance. The seabed is
refracted, absorbed per channel (0.45 / 0.16 / 0.11 per metre) with scatter, caustics, Fresnel
sky, sun glints, and four kinds of foam. Tests: 8 new, 291/291.

**Found on the way: the terrain was missing from the depth texture.** The SSAO renderer feature is
set to Depth Normals, so URP draws the DepthNormals pass to build the depth texture, and neither
terrain shader had one. So SSAO has never shaded the terrain since 8b, and the first water run saw
the sea as bottomless: navy right to the beach, with foam lines floating offshore. Both terrain
shaders now have a DepthNormals pass. Rule: every opaque shader needs one.

**Tuning after the first shots.**
- Offshore beach-wave bands looked like painted lines, so they now foam only within 40% of
  `ShoreReach` of the shore.
- Foam lit by the 4800 K sun read pink over blue water, so foam is lit by the sun's brightness only.
- Ripples at 0.55 speckled white close up, so they are now 0.35, and whitecaps need a higher ripple.
- Refraction at 0.025 dragged beach colour into the water, so it is now 0.015 and scaled by a
  quarter of the thickness.

**Sea mesh.** Vertex every 2 cells, not 4, so the shortest waves (7 m) have enough vertices:
99,334 triangles on the Continent map.

**Frame time, honestly.** The capture times the coastline view with the water hidden and shown,
through `FrameTimingManager`. In the editor it reports about 1.6 ms GPU either way, which is not
believable for a 2560×1440 frame. Treat it as "no measurable cost in the editor", not as a budget
figure. The mid-range machine check is still owed.

**Not done.** The river shot lands on a round pool at the river's midpoint on this seed rather than
on a running channel, so rapids foam is untested by eye. Unit wakes and waterfalls are not built.
Rebuilding the field after digging costs up to ~57 ms in one frame; if that hitches, bake only the
dirty region.

## Art pipeline, Phase 0 — style bible (2026-09-21)

Ronan's brief: a style bible from six reference images, then one asset end to end
(Krea2 → Trellis2 → Blender → Unity), with an evaluation loop scored against the bible, stopping
for approval after each phase. The bible is `Assets/TinyDiggers/Art/STYLE.md`; its "How the block
got here" section records the prompt iterations. Facts worth keeping outside it:
- `krea2_turbo_bf16` at 8 steps, not the saved `myKrea2UnlockedInt8_v10`, which drew blocky
  artefacts into the background.
- Krea2 turbo runs at CFG 1 and ignores negatives, so every "don't" has to be in the positive.
- Any foliage wording in a shared block makes the model add bushes to rocks and machines, so
  foliage lives only in the PLANT clause.
- With Unity open, ComfyUI ran out of VRAM once (1.6 GB free) and a render stalled. Unloading the
  models fixed it; queue with wait=false and watch the output folder.

## Art pipeline, Phase 1 — tree_a (2026-09-21)

The pipeline now runs end to end: Krea2 (de-lit variant) → ComfyUI `TinyDiggers_Trellis2` →
`Art/Tools/clean_export.py` → FBX + GLB + 512² albedo → URP Lit prefab → `TinyDiggers/Art Capture`.
The loop-by-loop record is in `Art/ASSET_LOG.md`. Facts a fresh session needs:
- **Trellis2 canopies are a thousand loose leaf shells.** Decimate them directly and you get
  confetti; a voxel remesh (0.02 × height) plus a volume-preserving Laplacian smooth turns them into
  the clumps STYLE wants.
- **Trunks don't survive decimation.** They are rebuilt as a tapered tube through the wood's
  slice centres (wood is split from foliage by base-colour red > green).
- **The STYLE swatches are lit colours** sampled from rendered references. As albedo under our
  1.7 sun they go neon, so the albedo is swatch × 0.72.
- **Importing an asset while in play mode unbinds the terrain's textures** (grey land). Evaluation
  shots must come from a fresh play session: reimport in edit mode, then enter play.

## Art workflow v2 — Blender as hub, leaf-card trees (2026-09-21)

Ronan's rulings:
- Blender is the hub for cleaning, converting and animating Trellis items.
- Props are never voxel-remeshed; only the terrain is cells.
- Tree canopies are leaf cards (chosen over a higher budget with LODs).

**Pipeline modules.** The stages live in `Art/Tools/td_pipeline.py` and `td_cards.py`, and the
atlas builder is `leaf_atlas.py`. They run both headless and in the live Blender session through
the Blender MCP. `clean_export.py` is the Phase 1 record, not the current path.

**Facts a fresh session needs:**
- **The Blender MCP runs code with no window context** (`bpy.context.screen` is None). Operators
  then fail their poll or act on the GUI selection, and `transform_apply` silently left a ×9 scale.
  The pipeline therefore applies modifiers by depsgraph evaluation, bakes transforms into mesh
  data, and wraps every other operator in `td_pipeline.run()`, which adds a window/3D-view
  override.
- **Trellis2 geometry is thin, open shells**, both the leaves and the wood.
  - Voxel remesh of them gives almost nothing (about 100 triangles).
  - Collapse decimation floors at about 32k.
  - GPU QEM at 1.5k, 4k and 15k shatters.
  - Hence cards for leaves and tubes for wood.
- **The Trellis2 workflow's own postprocess** (`TinyDiggers_Trellis2`: RemeshMesh udf 768 →
  DecimateMesh → Unwrap → BakeTextureFromVoxel with back-projection) can make a low mesh inside
  ComfyUI. It is useful for solid props (rocks, vehicles) whose shells aren't thin; untested on
  them yet.
- **A Unity editor dialog appeared** when one RunCommand did a texture import, material creation,
  FBX reimport and asset deletes together ("User interactions are not supported"). Split into
  steps without `DeleteAsset`, it ran; leftovers are removed through git.

## Grass and wind (2026-09-21)

Ronan asked for grass next "so we can really see wind".

**Asset.** `grass_a` is 3 crossed cards, 12 triangles, 1 × 0.8 m.
- Atlas: 4 Krea2 tuft sprites (seeds 7300–7303, SPRITE core), pulled to a grass green between
  Meadow Sun and Leaf Mid.
- Normals point straight up, so a tuft lights like the ground under it.
- Wind weight is vertex colour R: 0 at the root, 1 at the tip.

**Shader.** `TinyDiggers/Foliage` does alpha clip, is double-sided, wraps diffuse with a little
translucency, and takes SSAO.
- Sway: a gusting lean down the wind with a phase per plant, plus a flutter across the wind.
- The sway runs identically in the ForwardLit, ShadowCaster, DepthOnly and DepthNormals passes.
- Globals `_TDWind` / `_TDGust` come from `WindSettings` (`Presentation/Settings/Wind.asset`)
  through `WindView`.
- tree_a's leaves now use it too (WindScale 1.4), so grass and trees move as one wind.

**Placement.** `GrassField` is plain C# with 5 tests.
- Grass grows on dry topsoil only, 1.5 tufts per cell, seeded per cell, in 32-cell chunks.
- A chunk is rebuilt when a cell in it changes, so digging takes the grass.
- 42,998 tufts on Continent seed 11. About 6k are drawn from the close pose (220 m draw
  distance, frustum-culled per chunk).

**Gotcha.** `Graphics.RenderMeshInstanced` called in LateUpdate never reached a camera rendered by
hand (the capture tools' `camera.Render()`), so the first capture showed no grass at all.
`GrassView` now draws in `RenderPipelineManager.beginCameraRendering`, per camera.

**Motion check.** 24 frames 0.1 s apart. On average 8.2% of pixels change between frames.

## Terrain look pass — diorama references (2026-09-21)

**The big one: the sun never reached the terrain.** The renderer is Forward+ with light layers on
(it has been since the first commit), and the terrain, water and foliage shaders were not compiled
with `_LIGHT_LAYERS` / `_CLUSTER_LIGHT_LOOP`. So URP skipped the main light for them, and
everything we have ever tuned as "lighting" was really the trilight ambient. A URP Lit cube beside
the terrain lit fine, which proved it. The fix is the same multi_compile set URP's Lit uses. Rule:
every custom lit shader copies URP Lit's light keyword pragmas.

**Look changes, measured against the references** (lit grass #B4BF23, rock #3F4A3A, background
#3B454F):
- Sun 34° elevation, 6200 K, intensity 2.3, shadow 0.95. Ambient 0.9, with a cool sky and a green
  equator and ground. Slope tint off. Grade: saturation +28, contrast +16, +0.1 EV.
- Texture recipes:
  - grass: yellow-green (0.50, 0.60, 0.13) with yellow flecks;
  - rock: dark green-grey;
  - dirt: dark olive, with dark flecks (the pale stone flecks had made dirt average a cream
    #A8A293);
  - granite and loose rock: in the rock family;
  - sand: toned down to (0.70, 0.62, 0.44).
- Grass on steeper ground: SlopeMixed 38→44 and SlopeBare 45→50, in the code and the asset.
- Terrain BlendWidth 0.5→1.0 (the softest the four-cell blend can go) and MottleStrength 0.18.
- Table: dark plinth #2C2729-ish, navy gradient sky.

**Workflow gotcha.** Running "Generate Terrain Textures" inside a RunCommand timed out the MCP, and
Unity restarted, losing the unsaved changes. Now: save settings in one command, trigger the
generator through ManageMenuItem, and watch the log for "Terrain textures: wrote".

## Smooth rock edges (2026-09-21)

**Cell map channels.** B is now a tent-weighted 5×5 share of stone around each cell, recomputed
over the 5×5 around any changed cell. A is 255 when the cell's top material is stone.

**Shader.** The terrain shader blends stone cells and soil cells separately across the four
neighbours, then mixes the two along the 0.5 contour of the bilinear-filtered stone field, pushed
about by two octaves of noise, with a ±0.12 soft band. Before this, any grass-to-rock edge was drawn
as the one-metre cell staircase.

**Dirt colour.** Dirt and loose dirt are now a dry-grass olive, so the dirt band the slope rules
put between grass and rock reads as sparse turf instead of a khaki outline.

**Test.** One new cell-map test: the stone flag, plus a stone field that falls off with distance.

## Slice 10 — ore underground (2026-09-21)

Ronan chose "ore underground" as the next Captain of Industry step, ahead of stockpiles,
foundations and retaining walls. The plan is in `C:/Users/Brian/.claude/plans/synthetic-munching-frog.md`.

**Materials** (ids 10–17): Coal, Iron ore, Copper ore and Limestone, each with its own loose form.
- In place, each stands like rock; `IsStone` includes them, so cliffs and the smooth rock edges
  treat them as stone.
- Dug, each becomes its loose form, bulking 1.4–1.5, angle of repose 36–38°. So a load of iron is
  still iron in the hauler.
- The atlas needs 20 of its 24 slices.

**Generation** (`OreDeposits`, run after `BuildColumn` in the strata pass):
- Ore converts rock, granite **and bedrock** inside each ore's patch mask and depth window,
  splitting layers.
- Bedrock is included because the rock under grassland is only about 4 m thick; with rock and
  granite only, coverage was 1–2%.
- The surface never moves: a sliver under 5 cm now joins the layer below instead of being dropped.
  Dropping it moved heights off the step and broke three existing tests.
- Offsets are drawn last off the island's random stream, so existing seeds keep their land.
- Coverage on Continent seed 11 (share of land cells whose nearest ore within 20 m is this one):
  coal 15%, iron 5%, copper 7%, limestone 7%.
- Cost: about 20–30 ms warm (in play, 415–442 ms with ores against 392 ms without). Cheap because
  cells under the sea are skipped, the fine noise octave is only computed where the coarse one can
  reach the threshold, and the depth wander is computed lazily.

**Ore view:** F7 (`OreOverlayView` over the plain-C# `OreSurvey`).
- It shows the nearest ore within 20 m, coloured by ore; deeper deposits are fainter.
- It updates as cells change.

**Tally:** `MiningLedger` on `JobDispatcher` records every dig's in-place volumes, and the toolbar
shows "Dug: Iron ore 12 m³ · …".

**Tests:** 12 new (OreDeposit, OreSurvey, OreMining).
- The 512² timing test is now best of three: a single run in a busy editor swung 430–560 ms. The
  500 ms budget is unchanged.

**Not proven in play** (said plainly):
1. The crew digging all the way into an iron lens. It started on a 10×10 pit, but 5–7 m of rock
   is more than a short time box allows; a 4×4 pit blocked the auto-ramp ("nowhere to stand
   beside"). The unit tests prove dug ore goes into the load and the ledger.
2. A coal seam seen on a pit wall. The terrain shader colours faces by the column's top and next
   layer, not by strata at that height, and a 7 m stone wall collapsed into loose rock over the
   seam anyway.

## Slice 11 — half-metre cells (2026-09-21)

Ronan asked for a finer dirt surface: "can we make the dirt surface even smaller". He chose the
same ~512 m island at **1024² cells of 0.5 m**, with the **height step halved to 0.5 m**, so one
step per cell is still 45° and the slump, cliff and slope rules keep their meaning. He also
approved moving the generation budget from 500 ms at 512² to **≤ 1.2 s at 1024²**.

**Approach.** `TerrainGrid.CellSize` (default 1, so every existing test and caller is unchanged)
and `CellArea`. Grid, generation and crew logic stay in cell indices; only the places that turn
cells into metres multiply by `CellSize`. There is no scale on the terrain root, because it would
squash the camera orbit and metre-sized props. Where each conversion lives:
- **Slopes:** `AngleOfReposeSimulator` neighbour distances, `SurfaceMaterials.SlopeDegrees`
  (new `cellSize` argument), `GridPathfinder` step cost and heuristic (metres),
  `GridPathfinder.MaxStepHeight` (defaults to one cell's width, a 45° step), and
  `Blueprints.LegGrade`/`SteepestGrade` (metres per metre; `PlayerTools.MaxGrade` 0.25 m/m).
- **Volumes:** `Excavation.Dig` takes a depth in metres and fills the load with thickness ×
  `CellArea` m³. `Excavation.Tip` takes m³ and lays m³ ÷ `CellArea` of thickness.
  `CrewUnit.StepVolume` is one step on one cell. `Blueprints.Volumes` takes the cell area.
- **Generation:** `TerrainGenSettings` stay in metres, as tuned. `IslandGenerator.Generate` makes
  a copy scaled into cells (`ScaledForCells`): lengths ×1/cell, cell counts rounded, areas ×1/cell²,
  and `MaxCliffStep`/`BeachMaxSlope` per cell. The ore depth wander noise is scaled too.
- **Rendering:** chunk GameObjects get `localScale (cell, 1, cell)`. Meshes are still built in
  cells, and Unity's inverse-transpose normal transform keeps the per-cell normals right per metre.
  The shader reads `_TerrainOrigin.z` = cells per metre. `BlendWidth` is now metres. The stone-field
  reach in `TerrainCellMap` is 2 m (4 cells at 0.5 m); a fixed 2 cells had halved the rock-edge
  smoothing in metres.
- **Water, overlays, camera and picking:** `WaterView`/`WaterField` build in cells and scale to
  metres, and shore distances are metres. `GrassField` density is per m². `DesignationsView`
  tiles take the cell size. `TerrainView.DiscCentre`/`DiscRadius` are now metres (camera, table,
  sea). `TerrainPicker.TryPick` takes and returns metres. `RtsCamera` pivot is metres.
- **Crew:** `CrewUnit.Speed` is m/s. `CrewView.digReach` 2 m and `cliffReach` 6 m become levels
  through `HeightStep`. A unit is still a one-cell machine, so its body is scaled to the cell:
  diggers now read as about 0.35 × 0.48 m. Brush and road width stay in cells, so the tools got
  finer too.

**Performance.** 1024² started at 1801 ms. Per-cell passes now run a row per task
(`Parallel.For`): the land mask, heights, material pick, `SurfaceMaterials.Smooth`, and strata/ore
columns built in 64-row bands, then written to the grid in series, because writing raises the
grid's events. Output is identical run to run (checksum and ore count compared).
- 1024² at 0.5 m: 760–980 ms. Stages are now recorded on `IslandMap.Stages`.
- 512² at 1 m: still under 500 ms.
- `TerrainCellMap` now catches the stone field up once per flush, deduplicated, and redoes the
  whole map in parallel past an eighth of it. A 9×9 kernel redone per changed cell would have
  made a regenerate take minutes.

**Measured in play (Continent seed 11, editor, `TinyDiggers/Slice 11 Capture`):**
- Start-up: 1024 chunks, 1.63 M triangles, generated plus settled in 1142 ms, meshed in 297 ms.
- Frame time on the disc pose: 3.7–4.0 ms.
- A 4×4-cell dig: under 1 ms to apply; the next frame rebuilt 1 chunk in 4.8–8.9 ms.
- Regenerate in play: 1.4–1.5 s, then a 2.1–2.3 s frame (every chunk rebuilt, every listener
  notified). It is a hitch, not a loop, but worth a look.
- The crew cleared a 6 m square (12×12 cells) designation 2 m deep: 0 designations left,
  ~30 m³ dug.

**Same island in metres** (seed 11, scene settings): top-material shares 1 m vs 0.5 m are topsoil
61.5/59.7%, rock 24.2/26.0%, sand 14.2/14.1%. `CellSizeTests` also checks land area and peak
within 10%.

**Tests:** 13 new (`CellSizeTests`, `CellSizeCrewTests`); 322/322 pass. They cover:
- a 45° step standing at 0.5 m, and the same 1 m step slumping only on 0.5 m cells;
- slope as rise over metres, and a ray in metres picking the right cell;
- dig and tip volumes; path cost; road grade;
- the same island at both sizes, with iron inside its depth window;
- 1024² generated under 1.2 s.

**Found along the way:** the capture tool's close-ups first came out at 700 m. `RtsCamera`'s edge
pan and wheel zoom read the real mouse over the Game view mid-capture, so the tool now switches
the RtsCamera off and places the camera itself. The Slice 8–10 capture tools still pose in cells
and will frame wrongly at 0.5 m.

**Not good yet** (said plainly):
1. Stepped slopes show fine stripes: each half-metre terrace reads as a band.
2. The crew-pit shots are weak. No flat grass was found near the crew, so the pit was dug in a
   valley side and does not read well.
3. Crew bodies are tiny at cell size.


## Camera: zoom out to the whole disc, steadier pivot (2026-09-21)

Ronan: "my camera controls are all jenky i cant zoom far enough out to see entire disk". In play,
the camera had drifted to a pivot at (407, −15, 404), near the rim and down on the seabed (centre
256, 256), looking straight down. There were three causes:
1. **Zoom-to-cursor never came back.** Zooming in pulls the pivot toward the cursor, and zooming
   out left it there, so the far side of the disc never came back on screen. Now each notch out
   slides the pivot back toward the disc centre by the share of the remaining range it opened,
   and fully out is always the centre. This replaces the old "zooming out leaves the pivot"
   test, which was my own choice, not a ruling.
2. **Max zoom was a fixed 2.6 × radius + 40 m (700 m).** That fits the disc at a tilt but not
   looking straight down or through a narrow Shift-wheel lens. The limit is now worked out every
   frame from the disc radius × `FitMargin` 1.2 over the tangent of the narrower half-angle of
   the view: 841 m at FOV 40 and 16:9. `MaxDistance` is only an upper bound now (3000 m), and the
   far clip grows to reach the far rim.
3. **The pivot rode the single ground point under it.** It dropped 15 m over every bay and
   stepped on every half-metre terrace. It now rides the mean of nine samples over a footprint of
   8% of the zoom distance (at least 2 m), never below sea level, still eased by the existing
   smoothing.

Checked in play: zooming 12 notches toward the rim, then all the way out, returns the pivot to
(256, 256) at 841 m; the straight-down shot (`Screenshots/Camera/zoomed_out_top.png`) shows the
whole disc with margin. 323/323 tests pass.

## Dam rim — choices (2026-09-21, design awaiting approval)

Ronan wants a concrete shell round the world that looks like a huge dam holding the world in:
circular (the real ones are concave), with spillways dotted round it, a good concrete texture
at scale, and reading as what holds the world together. References: an arch-buttress dam, a
gated spillway, a stylised hydro plant, and a gravity dam with chutes.

His answers:
- **Build:** a Blender kit of modular pieces, arrayed round the circle procedurally in Unity.
- **Outside:** the dam is the edge of the world. Its downstream face drops into the dark, and
  the spillways pour water off into the void. There is no ground outside.
- **Scale:** a modest face, about 20 m.

**2026-09-21 11:00:46: GPU driver timeout (TDR) during a play session.** Windows logged
`nvlddmkm` event 153 and Unity shut down ("Failed to present D3D11 swapchain"). Unity's log stops
with no error before it. The cause is not identified. Nothing in the game is heavy on the GPU (about
4 ms frames, 1.6 M triangles on an RTX 4090), and nothing else was holding GPU memory just after
the restart. If it recurs, note what else was running (ComfyUI, Blender, a capture) and the time,
and profile a play session.

**Approved** ("go forward", 2026-09-21). Build order: (1) the Blender kit, shown as renders;
(2) layout and ring in Unity, plain concrete; (3) concrete textures and shader; (4) spillway
water. Stop after each step.

Kit convention: pieces are modelled straight. X runs along the wall, Y points outward
(downstream), Z is up, and the origin is on the inner face at sea level, centred on the piece.
Unity bends each piece onto the circle (angle from x over the inner radius, radius = inner + y),
so every piece's outer end meets its neighbour's exactly. Placed as straight chords, the
downstream face opened gaps of 0.5–1 m between bays, because the outer face is longer than the
inner.

**Step 1 done: the Blender kit** (`Art/Tools/td_dam.py`; `dam_preview.py` renders an arc of it
headless; source `Art/Blender~/dam_kit.blend`; FBX in `Art/Props/Dam/`).
- `dam_bay` (12 m, 360 triangles): a vertical inner wall; a 6 m crest walkway with parapets; a
  battered downstream face from the crest down to −18 m, with a shallow cast panel; one
  triangular buttress on its +x joint with a capped post; a stepped footing to −21.5 m.
- `dam_spillway` (36 m, ~2.6 k triangles):
  - four piers with rounded noses, rising to an 8 m gantry with a railed service bridge and a
    hoist house on each pier;
  - three radial gates between them, raised, with ribs and arms;
  - an ogee weir at −2.5 m (below the sea, so the spillways always run) into a chute with
    training walls and a flip-bucket lip.
- `dam_spillway_water` (300 triangles): a sheet under the gates and down the chute, then a curtain
  thrown off the lip. Its vertex alpha fades out 22 m below the footing.
- `dam_tower` (~300 triangles): an intake tower standing 8 m off the wall, 18 m above the sea,
  with slit windows and a railed bridge to the crest.

Found along the way:
1. The first gates sat entirely under the sea and read as a yellow log. The piers now rise to a
   gantry (the reference photos) so the gates stand clear.
2. Bevelling the zero-thickness gate skins and the thin rails threw long spikes across the scene.
   A piece is now a bevelled body plus trim added after the bevel.

**Kit restyled: sci-fi brutalism** (Ronan: "can you give it more of a scifi brutalism look").
The first kit was a plain civil-engineering dam: railings, radial gates and a smooth batter.
Now:
- **Bay** (540 triangles):
  - a 9 m crest deck cantilevered 2.5 m out over a downstream face that steps down in four
    terraces;
  - solid chamfered crest walls instead of parapets and railings;
  - a lit slot in each terrace and one along the inner face above the waterline;
  - a 2 m wedge fin on every joint, rising 4 m above the crest, with lit slots down its sides.
- **Spillway** (~1.4 k triangles):
  - four chamfered gate towers under one monolithic lintel 3.5 m deep, with a lit band;
  - a control cabin cantilevered off the lintel over the sea;
  - slab lift gates with ribs;
  - a chute that steps down in 1.5 m risers to the lip, with angular training walls.
- **Tower** (~500 triangles): a 7 m shaft with a lit slot, under a corbelled head cantilevered out
  to 12 m, with lit bands and a beacon mast.
- **Light** material slot: emissive strips at the back of the slots. The preview colour is a pale
  cyan; the real colour is a Unity shader setting.

## Dam rim: ship terminals and landing pads (2026-09-21)

Ronan: the disc is **floating in space**. Supply ships dock at the rim and unload for the island.
On each side of the dam he wants giant terminals for capital ships and smaller pads for landing
ships. His answers:
- **Four capital-ship terminals**, one per side (the compass points). Spillways and pads go
  between them.
- **Cantilevered docking arms:** massive gantries jut outward from the dam's outer face into the
  void, with a berth, clamps and cranes between them. The ship hangs alongside, and cargo is
  lifted over the crest to the island.
- **Small pads on outriggers** off the outer face, between spillways.
- **Docks now, ships later.** Ship models, arrivals and the supply gameplay are separate steps.

Kit pieces:
- `dam_terminal`: 60 m, five bays wide, replaces bays in the ring.
- `dam_pad`: 24 m, two bays.

Both carry their own wall section and the +x fin, like a bay.

**Built:**
- **`dam_terminal`** (84 m, 7 bays, ~3.3 k triangles):
  - two docking arms 10 m wide, cantilevered 100 m out into the void either side of a 54 m
    berth, 16 m deep at the root and thinning to a lit head;
  - each arm has three lit clamps reaching into the berth, guide lights along its top edges, and
    two raking braces back to the terraced face;
  - three gantry cranes straddle the berth on 22 m legs, each with a trolley and hoist;
  - the terminal building straddles the crest: 30 m monolith with a stepped top, lit bands,
    cargo doors and a mast, and a control bridge cantilevered toward the berth;
  - a covered conveyor runs from the berth up to the building.
  - The first pass was 60 m wide with 72 m arms and a 36 m berth. That was too small to read as
    a capital-ship dock beside a 510 m island.
- **`dam_pad`** (24 m, 2 bays, ~960 triangles): an octagonal pad 22 m across, 3 m below the crest,
  on a faceted inverted cone with a raking brace back to the wall. It has a lit landing ring, a
  touchdown mark, four corner beacons and a ramp up to the crest.

**Step 2 done: the ring in Unity** (`Presentation/Dam/`):
- **Layout** (`DamLayout.Build`, plain C#): four terminals centred on the compass points. Each gap
  holds pad, spillway, tower, spillway, pad, spread evenly, jittered by up to a bay by the seed,
  never two features touching. Every piece in a gap is stretched by the same factor, within half
  a bay over the gap, so the ring closes exactly. At radius 259 m: 88 pieces round 1627 m.
- **Bending** (`DamBend`, in `DamLayout.cs`): the angle comes from the along-wall position over the
  inner radius, and the radius is the inner radius plus the outward distance. Unity's FBX import
  turns Blender (x, y, z) into (−x, z, −y), so in the imported mesh "along" is −x and "out" is −z.
- **View** (`DamView`, `DamKit`, `DamSettings`): each piece is bent and merged into 16 sector meshes,
  one submesh per surface (concrete, dark, steel, light) plus a transparent water mesh per sector:
  24 meshes in all. Step 2 used plain URP Lit materials.
- **Other changes:**
  - `TableView._plinth` is off, because the dam replaces the plinth.
  - `RtsCamera.FitBeyondDisc` is 110 m, so full zoom-out fits the docking arms.
  - A **floor** disc at −24 m closes the world from underneath (the disc floats in space).
- Tests: 9 new (layout and bend); 332/332 pass.

Found along the way:
1. **A renderer bug the plinth had been hiding.** `SmoothedTerrainRenderer.CachedCornerHeight`
   counted void cells, whose surface sits at the datum, so every rim corner was dragged down into
   a comb of blades under the disc edge. The uncached `TerrainSurface.CornerHeight` skipped them,
   and the code claimed the two matched bit for bit. The cache now treats void as off the map; a
   regression test covers it.
2. **Probing renderers in play:** with the GPU Resident Drawer on, disabling a renderer or changing
   its layer does not take effect within the same `RunCommand`. Change it in one call and render
   in the next.
3. **The URP Lit `_EMISSION` keyword did not persist** when set on a new material asset in the
   same call that created it. Set it again and save the asset explicitly.
4. **A script whose meta was written mid-import** (`DamBend.cs`) was left out of the compile. It
   was merged into `DamLayout.cs`.
5. **Since the 11:00 crash, Unity logs to `%LOCALAPPDATA%/Unity/Editor/Editor.log`**, not the
   project's `Logs/Editor.log`. Read the `TESTS` line there.

**Step 3 done: concrete.**
- **Detail tile** (`Editor/DamTextureGenerator.cs`, menu *TinyDiggers > Generate Dam Textures*):
  1024² over 4 m (4 mm a pixel), tileable by construction; an albedo, a normal and a mask (R
  cavity, G air void, B roughness). It holds only close-up grain: paste blotches, sand, aggregate
  and air voids. Nothing in it is big enough to show a repeat. Code-generated rather than Krea2,
  because a generated image is not guaranteed to tile. The periodic noise was moved into
  `Editor/TileableNoise.cs`, shared with `TerrainTextureGenerator` (same functions, so the terrain
  output is unchanged).
- **Shader** (`TinyDiggers/Concrete`): the tile, triplanar in world space, plus weathering drawn
  from position, so none of it repeats:
  - 2.4 × 1.2 m formwork panels with joints, four tie holes each and a tone per panel. They follow
    the curve because `DamView` writes metres-along-the-ring and metres-out into uv0 (and a tone
    per piece into uv0.w); on faces looking along the ring the panels run outward.
  - Rain streaks, grime on ledges, a sheltered underside, a slow mottle round the ring.
  - A wet band and algae at the waterline, on the inner face only (outside is void).
  - It has the same passes and light keywords as the terrain shader (ShadowCaster, DepthNormals,
    DepthOnly).
- **Found tuning in play:**
  1. Joints and tie holes at full strength read as a tiled bathroom wall from 100 m. They now fade
     out past 3.5 cm a pixel, leaving the panel tones at a distance.
  2. Faces out of the sun came out olive-green: the navy sky's ambient, through the +28 saturation
     grade. The concrete now takes its ambient mostly desaturated (`_AmbientSaturation` 0.25), so
     shade reads grey.
  3. The light strips blew out to white at 3× emission. They are now cyan × 1.6.

**Step 4 done: spillway water** (`TinyDiggers/Spillwater`, transparent, two-sided).
- **Kit change:** the water sheets are five columns wide. Vertex red is "how far into the sheet
  from its edge" (0 at the edges, 1 in the middle) so the edges thin raggedly; alpha keeps the
  fade below the footing. The curtain has twice as many rows so it can wobble.
- **Flow:** one downstream coordinate, metres out minus height (it grows down the chute and
  down the fall), scrolls everything. The curtain speeds up with free fall.
- **The chute:** blue water carrying long, sharpened streaks, and a white burst just below
  every step nose (every 2 m out from 2.5 m), settling before the next.
- **The curtain:** it leaves the lip glassy, whitens as it aerates, sways outward and back more
  the further it has fallen, and tears into strands and then spray until it is gone 30 m down.
- **First pass:** the water was white paint (too much foam, round blotchy noise). Deeper blue,
  sharpened streaks and the step bursts fixed it.

## Space backdrop (2026-09-21)

Ronan: "add a starfield space backdrop". `TinyDiggers/Space Sky` (Art/Sky/SpaceSky.mat) is
assigned through the new `TableView._sky` slot; left empty, the old two-colour gradient is used.
It is all procedural from the view direction, so nothing tiles or seams:
- three layers of stars (rare bright, medium, a faint dust thickest in the band), each with its
  own colour temperature, the fainter two twinkling slowly;
- a galaxy band on a tilted great circle, with dark dust lanes;
- faint violet and teal nebulae, strongest near the band.

Stars are held at least about a pixel wide (by `fwidth`), so they don't flicker as the camera
turns. The island's lighting is unchanged: the ambient comes from LightingView's trilight colours,
not from the sky.


## Pivot: PromptWaffle Dynamic Water System (2026-09-21)

Ronan: "we need to pivot and create a plugin for unity to help with water long term". He
pasted KWS2's (Kripto289) store page as the feature benchmark. We build our own, per the
standing water ruling (no bought assets), and copy none of its code, shaders or wording. His
answers:
- **What:** a reusable package, TinyDiggers first. Its own UPM package that the game consumes,
  generic enough to sell later, with features prioritised by the game's needs.
- **Pipelines:** URP first (Unity 6, RenderGraph) over a pipeline-agnostic core (simulation,
  data and API in plain C# plus compute). HDRP and Built-in become adapters later.
- **Milestone 1:** a shallow-water simulation zone on a heightfield: sources, drains, flow and
  flooding, with water following the terrain.
- **Name:** PromptWaffle Dynamic Water System.

Status: package layout and API proposed; waiting for approval before code.

**Approved** ("approve", 2026-09-21): an embedded package at
`Packages/com.promptwaffle.dynamicwater/`, with the virtual-pipes model and the API as proposed.

**M1a: simulation core** (package 0.1.0):
- `CpuShallowWater` is the reference model; `ShallowWater.compute` mirrors it step for step
  (Flux, Depth, Velocity, Fill kernels, in place, no ping-pong).
- `WaterSimulation` owns the buffers and a private copy of the compute shader (two zones sharing
  the asset would share parameters), publishes a float `State` texture (surface, depth, vx, vz),
  and uploads ground by dirty rectangle.
- `WaterQuery`: async readback snapshots for gameplay sampling.
- Components: `WaterZone`, `WaterEffectorComponent` (Source, Drain, Level, Force), and
  `WaterGroundProvider` plus `TerrainWaterGround`.
- Effectors are normalised over the cells they actually cover, so a source adds exactly its rate.
- `WaterSimulationDesc.Validate` refuses a step above the pipe model's stable limit,
  √(cell ÷ 4g).
- **Tests (14, in the package; `testables` added to the manifest):**
  - water is conserved (CPU dam break, and GPU 1024² over 600 steps, both within 1e-4);
  - still water stays still;
  - water settles at a sill;
  - walls hold;
  - sources and drains move their exact rate (±1%);
  - a drain never goes negative; level holds; force pushes;
  - the GPU matches the CPU to under 2 mm over 240 steps (ground, a wall, a source and a drain);
  - digging a trench floods it;
  - 60 steps of 1024² take 4.6–21 ms including a readback, so a step costs 0.08–0.35 ms.
- **Test mistakes found and fixed** (the model was right each time):
  1. equal basins level out above the sill, so the test was changed to use a deep receiving basin;
  2. a drain on a thin sheet can't take its full rate, so it is now tested in a full pool;
  3. a force surge travels about 2.2 m/s, so the test samples nearer the force.

**M1b: URP surface and the TinyDiggers bridge.**
- **`WaterZoneRenderer`** (package, URP): a flat grid in 64 × 64-quad chunks, one vertex per 2
  cells.
  - Chunks are persistent child renderers. Their vertices are world positions (the shader
    ignores the object matrix) and their world bounds are set explicitly, so the zone's transform
    can't skew or mis-cull the water.
  - The first version drew with `Graphics.RenderMesh` from `LateUpdate`. That reaches only the
    cameras rendering that frame, so the capture tool's manual `camera.Render()` never saw the
    water; the "olive sea" in those shots was the seabed.
- **`DynamicWaterSurface.shader`:**
  - vertices lifted to the simulated surface, and sunk under dry ground;
  - normal from the state texture's slope, with shore neighbours standing in for dry ones;
  - refraction through URP's opaque texture with per-channel absorption (red first). The first
    version multiplied the seabed by the shallow colour, which turned sand olive;
  - foam from speed and shallowness, carried along the flow with a two-phase flow map;
  - Fresnel sky reflection and a sun highlight.
- **TinyDiggers bridge:**
  - `TerrainGridWaterGround` reads the column grid (void cells become walls) and gathers each
    frame's `CellChanged` into one rectangle.
  - `DynamicWaterBridge` sizes the zone to the grid (1024², 0.5 m), fills it to sea level,
    rebuilds it on regenerate, and hides the old sea through the new `WaterView.Visible`. The old
    sea's sheets are parented to the Terrain object, so hiding the Water object's renderers
    missed them.
  - `WaterZone` now builds its simulation lazily, once `WaterGroundProvider.IsReady`: its
    `OnEnable` ran before `TerrainView.Awake` built the grid.
- **Measured in play** (`TinyDiggers/Dynamic Water Capture`, a 5-cell trench 25 m from the sea to a
  basin at −1.6 m):
  - the basin reached 0.57 m after 2 s, 1.32 m after 5 s and 1.40 m after 12 s;
  - 3.1 ms a frame with the 1024² zone running;
  - 1.59 M m³ of sea.
- **Also found:** a trench cut between tall banks fills with slumped loose dirt before the sea
  arrives, which is the game's own slump rule working. The capture now picks low ground.


## PromptWaffle Dynamic Water: swell (2026-09-21)
The old static sea's Gerstner waves are now in the package, drawn on top of the simulated water.
- **`WaterWaveSettings`** (ScriptableObject): seed, wave count (up to 8), height range, wavelength
  range, wind direction and spread, steepness, speed scale, gust patches, damping depth and the
  whitecap threshold. TinyDiggers uses `Presentation/Settings/DynamicWaterWaves.asset`, copied from
  the old `Water.asset` (seed 7, 6 waves, 0.05–0.45 m, 7–45 m), so the sea keeps its character.
- **`WaterWaves`** turns the settings into a wave set and evaluates it on the CPU exactly as the
  shader does, so `WaterZone.TrySample` returns the height the player sees. Rules it keeps (and
  `SwellTests` refuses to break):
  - the set always holds one big wave and one small one, and the rest lean small;
  - no crest stands more than a fourteenth of its wavelength above still water;
  - the per-wave sharpness sums to at most 1 across the set, so a Gerstner crest never folds.
- **The swell is visual.** It moves no water in the simulation. It dies away under `DampDepth`
  (4 m) of water, so the shallows lie flat and no wave climbs a beach, and it is scaled by
  rough/calm wind patches.
- **Glare.** The first render facing the sun showed broad, blown-out white blotches. Those were
  not foam (sim velocity was 0 there). They were the sun's highlight on a long, smooth swell.
  - The fix adds wind ripples: two octaves of small noise slope on the normal, faded out in
    water under 0.5 m deep.
  - The highlight is now a tight lobe (`900 × smoothness`) weighted by Fresnel, so it breaks into
    a glitter path.
  - New material knobs: `_WindRipples`, `_WindRippleSize` and `_SunGlint`.

## Spillways drain the dynamic water; rim curtains fixed (2026-09-21)
- **Ruling (Ronan): the spillways are a safety valve.** They let out only water standing above
  sea level, so nothing runs today until something lifts the sea (later: rain, pumps, sources).
  The other two options were a steady river inflow with constant spillway flow, and gates the
  player opens.
- **Package:** a new effector, `WaterEffectorKind.Overflow` / `WaterEffector.Overflow(position,
  radius, crestLevel, weirWidth)`.
  - It is a broad-crested weir: Q = 1.7 × width × head^1.5 m³/s, where head is each cell's own
    depth over the crest, so the water draws down toward the outlet.
  - It never adds water and never takes a cell below the crest.
  - The CPU reference and ShallowWater.compute share the rule. Tests: it leaves water under the
    crest alone, it drains a pool to the crest and no further at about the weir rate, and it is
    part of the GPU = CPU check.
- **TinyDiggers:** `DamSpillways` puts one Overflow at each spillway in `DamView.Slots`. Each sits
  8 m inside the disc (wholly over cells, so none of its reach is spent on the void), with its
  crest at sea level and a weir as wide as the spillway (36 m). There are 8 outlets.
- **Measured in play:** the whole sea was raised 0.5 m (+68 400 m³). It then lost 941 m³ in
  10.5 s, about 90 m³/s. The ideal is 173 m³/s at the full 0.5 m head; the difference is the
  0.22 m drawdown at the outlet. At that rate a 0.5 m surge takes well over 10 minutes to run off.
  The draw-down is too small to see yet. The falling spillway water in the dam kit still pours
  whether or not the outlet runs.
- **Leak under the map (Ronan saw it):** this was the surface mesh, not the simulation. Vertices
  over the void (wall cells) were dropped to y = −10 000, so every triangle along the rim
  stretched into a curtain hanging under the disc, and half of each still counted as wet. A wall
  vertex now takes the highest open neighbour's surface, one vertex step away (`_WaterTexel.w` =
  cells per vertex), and folds out of sight like dry ground.

## Spillway water follows the real flow (2026-09-21)
- **How:** each spillway's falling sheet carries its spillway index in uv1.x. `Spillwater.shader`
  reads the global `_DamSpillFlow[32]` (0 = dry, 1 = full sheet). `DamView.SetSpillwayFlow` sets
  it; `DamView.Build` fills it with 1s, so without the dynamic water the spillways pour as they
  did before.
- **`DamSpillways.Follow`:**
  - It samples the raw simulated surface (no swell) at each outlet each frame and takes the head
    over the crest.
  - Fullness is `(head / 0.4 m)^0.75`, the square root of the weir flow, so a trickle still
    shows. Under 5 mm of head counts as dry, which ignores simulation noise.
  - It eases toward the target over about 1 s.
- **Look:**
  - dry: the sheet collapses to a point in the vertex shader and is never drawn;
  - low flow: only the middle of the sheet stays (the kit's edge-distance colour is thresholded
    by 1 − fullness), so it reads as a rope, and it tears sooner in the fall;
  - full: the old full sheet.
- **Measured in play:**
  - calm sea: all 8 at 0.00, chutes dry;
  - after a 0.5 m surge: all 8 at 0.79 (the head at the outlet is drawn down below 0.5 m);
  - after a 6 cm rise: thin ropes.
  - Tests: `SpillwaySheetTests` (3).

## Game logic reads the simulated water (2026-09-21)
- **Ruling (Ronan):**
  - Water blocks digging once it is 0.5 m deep.
  - The crew can't walk through water deeper than a wading depth: the pathfinder routes around
    it, and a crew caught by a flood climbs out.
  - The hover readout shows the real depth.
- **API:** Terrain gets `IWaterMap.DepthAt(x, z)`, held by the grid.
  - The default keeps today's rule: under sea level counts as water.
  - TinyDiggers plugs in a map backed by the simulation, so Terrain and Units never depend on
    the water package.
- **As built, the proposal changed from pull to push.** A `DepthAt` interface the grid asked on
  demand could not say when passability changed, and the regions and paths cache it. So the grid
  now takes the water:
  - `TerrainGrid.SetWaterSurfaces(span)` stores one water surface height per cell (negative
    infinity when dry), recomputes the blocked flags, and raises the new `WaterChanged(x, z)` only
    for cells that flip.
  - `ClearWaterSurfaces()` goes back to the sea-level rule. `DeepWater` is 0.5 m.
  - It keeps the surface, not the depth, so a cell filled above the water is dry at once, before
    the next readback.
  - RegionMap and CrewUnit listen to `WaterChanged` as they do to `CellChanged`.
- **Crew:**
  - A new job, `CrewJobKind.Escape`: a unit whose cell turns to water gives its job up and takes
    `GridPathfinder.TryFindWayOutOfWater`. That path wades through water but never over the void
    or up a step it can't climb, and ends at the nearest dry cell. With no way out it retries
    every second.
  - `CanDigStep` refuses a cell under deep water, so a flooded dig site waits until it drains.
- **Hover readout:** "Water (depth x m)" for deep water, "Shallow water (x m)" from 5 cm.
- **Package:** `WaterQuery.Snapshot` exposes the whole readback.
- **`GridWaterFeed`** (TinyDiggers): on each fresh snapshot, it copies the surfaces into the grid,
  in the terrain's own heights. It refuses (logs once, then keeps the sea-level rule) if the zone
  is not laid one cell to one cell over the grid.
- **Measured in play:**
  - an update every 0.2 s costs 4.7 ms, so about 1 ms a frame on average, in one 4.7 ms spike;
  - the 2 318 cells at exactly sea level, which the old rule called water, are dry land now;
  - after the flood capture the trench and basin read as water 0.95–1.46 m deep, dig
    designations there are refused, and dry land beside it can still be dug.
  - The climb-out has been verified only in tests (`LiveWaterTests`, 8), not with a real flood
    in play.

## Grid water feed spread over frames (2026-09-21)
- The first snapshot still goes into the grid whole: 5.4 ms, once, at start. A half-fed map
  would read its unfed half as dry, and `TerrainGrid.SetWaterRows` refuses to run before one
  whole `SetWaterSurfaces`.
- After that `GridWaterFeed` feeds 128 rows a frame (`_rowsPerFrame`). A 1024-row sweep takes
  8 frames. A sweep starts only when a snapshot newer than the one the last sweep ended on has
  arrived, and each band reads whichever snapshot is newest.
- **Measured in play** (24 s, including the flood-trench capture):
  - worst frame 1.18 ms (the flood's cell flips firing region and crew updates), typical 0.56 ms,
    down from a single 4.7 ms spike every 0.2 s;
  - 114 sweeps, about 4.8 a second, keeping up with the readback;
  - the trench still reads 0.94–1.46 m deep and blocked.
- Tests: `LiveWaterTests` +2. A band changes only its rows, and rows are refused before the
  whole map, as are partial rows and rows past the end.

## Crew climbing out of a flood, watched in play (2026-09-21)
- **Tool:** `TinyDiggers/Crew Flood Capture`.
  - It levels a 21×21-cell rock platform at the crew's height. The crew starts on the central
    mountain, and a first try cut the bowl straight into the 16.5–26 m slope, where the spring
    simply ran off downhill.
  - It carves a stepped bowl (middle 1 m down, ring 0.5 m down, every step climbable) and opens a
    3 m³/s spring in the middle for 25 s.
  - It logs every status change of every unit in the bowl, and shoots dry, escaping, out and end.
- **First real run found a flaw.** The escape aimed for the nearest cell under 0.5 m. In a rising
  pool that was the next cell at 0.49 m, so the water caught the unit again: unit 0 hopped four
  times in 0.75 s and was twice "out of the water" standing in 0.50–0.54 m.
- **Fix:** `GridPathfinder.ClearOfWater` = 0.5. A way out now ends where the water is under half
  the deep-water depth (0.25 m). Test: `TheWayOutGoesPastWaterJustUnderTheLimit`.
- **After the fix, in play:**
  - the water reached 0.5 m over the crew 1.96 s after the spring opened;
  - each of the 4 units (2 diggers, 2 haulers) made one escape, straight to the ring;
  - all were out by 2.78 s, standing in 0.02–0.03 m;
  - the haulers then went back to their diggers.

## PromptWaffle Dynamic Water: Basic Zone sample (2026-09-21)
- It follows the approved design's `package.json` entry: "a procedural basin with a source, a
  drain and a force". It also shows what the package gained since then: an overflow, the swell,
  and `TrySample` (a floating buoy).
- **Contents:**
  - `ProceduralBasinGround`, a ground provider plus a matching mesh: an upper and a lower pool,
    a sill with a notch between them, an island, and a spillway notch in the rim;
  - effectors: a spring, a current (force), a drain, and an overflow at the notch;
  - `BasicZoneDemo`: hold left mouse to pour, right mouse to dig a crater (shows
    `RaiseChanged`), R to refill, and a readout;
  - `SampleFloater`.
- **Input:** Input System when it is installed (via a version define), legacy input otherwise.
- **Authoring:** Samples~ is hidden from Unity's import. The sample is built and checked in
  `Assets/_PWSampleDev/BasicZone`, then moved with its .meta files (so its GUIDs hold) into
  `Packages/com.promptwaffle.dynamicwater/Samples~/BasicZone`. To edit it later: import it with
  Package Manager > Samples, change it, and copy it back.
- **Built and checked in play** (from the dev folder):
  - both pools fill to 0 m and hold level;
  - the hose poured at 20 m³/s on the dry west rim ran down into the lower pool, 17.6 m/s on
    the steep rim, 1.7 m deep at its foot;
  - a crater dug 2.5 m deep beside the lower pool filled to 1.54 m from it;
  - the buoys ride 0.25 m under the surface.
- **Current retuned:** the first scene's force was 1.5 m/s², as strong as a 15% slope. After
  3 s the lower pool read -0.69 m, with buoys sitting in a trough. It is now 0.1 m/s².
- **Imported the way a user would** (`Sample.Import`, into `Assets/Samples/PromptWaffle Dynamic
  Water System/0.1.0/Basic Zone`): 37 components, none missing; it plays the same. The test copy
  was then deleted.
- Needs URP's Opaque Texture on (the water refracts through it); the README says so.

## Slice 12: a bigger disc (2026-09-21)
- **Ronan: "the map is just too small, expand the disk by 3x"** means 3x the area: about 890 m
  across instead of 512 m, with 0.5 m cells kept.
- Turned down:
  - 3x the width (9.4 M cells, needs terrain LOD and streaming first);
  - 3x the width at 1 m cells (gives up Slice 11's half-metre detail).
- **Built:** the scene's grid goes from 1024² to **1792²** cells: 896 m across, 3.06x the area,
  a whole number of 32-cell chunks. Everything else already sizes from the grid:
  - the disc radius (447 m);
  - the island generator, whose features are in metres, so the bigger disc gets more coast,
    bays, lakes and offshore islands, not a stretched copy;
  - the water zone (1792²) and the grid feed;
  - the dam, now 188 pieces round 2.8 km: still 4 terminals, 8 spillways and 8 pads, with more
    bays between them;
  - the camera's zoom limit and view distance.
- **Only fix needed:** the crew spawned at the fixed cell (512, 512), yesterday's centre.
  `CrewSpawn.TryFind` now starts the crew on the nearest level, dry 3×3 block to the disc centre
  (plus an optional offset in metres, `CrewView._spawnOffset`). At 1792² that is (895–897,
  895–897) at 15 m. Tests: `CrewSpawnTests` (4).
- **Big landmarks are still fixed counts:** one mountain chain, up to 2 plateaus and 3 valleys.
  On the first look the continent doesn't read as sparse, so they are left alone unless Ronan
  wants more ranges.
- **Measured in play**, compared with 1024²:
  - generated in 3613 ms (was 760–980 ms);
  - meshed in 946 ms (was 297 ms);
  - 3136 chunks, 5.02 M triangles (was 1.63 M);
  - frames average 6.2 ms since start;
  - water feed: the first whole pass takes 21 ms, once; bands are 0.85–2.1 ms a frame;
  - managed heap 1.0 GB; the layer store alone is 1792² × 16 × 8 B = 411 MB.
- **Not changed:** the 1.2 s generation budget in `LandShapeTests` still measures 1024². Start-up
  on the real map is now about 4.6 s (generate plus mesh), so this is the next thing to speed
  up if it bothers.

## Slice 13: open sea round the island (2026-09-21)
- **Ronan:** "the land mass seems good for now but i think the disk can go another x3 but then we
  can have more open water between the land mass and the dam face will give more options for
  game".
  - The disc grows another 3x in area (about 1.55 km across, 3104² cells at 0.5 m).
  - The island stays its present size; the new ring is open sea.
- **Ruling: a fully workable seabed.** Every metre out to the dam can be dug, filled and
  reclaimed, with simulated water everywhere. Turned down: a cheap unworkable "far sea" ring, and
  a workable coastal band with far sea beyond.
- **`TerrainGenSettings.LandRadius`** (metres, 0 = the whole disc):
  - The island is laid out in a frame of its own: a square LandRadius + 2 cells either side of
    its middle, set in the middle of the disc.
  - The land noise, ridge, benches, height noise, material noise and ores are all sampled in the
    frame. Distances and floods stay in grid coordinates, where the frame's position doesn't
    matter.
  - The same seed gives the same island on any size of disc, and past LandRadius it is sea.
  - `IslandSettings.asset` has LandRadius 447 m, the old disc's radius, so at 1792² the frame
    sits exactly where the grid did.
  - Tests (`LandRadiusTests`): the same island comes out cell for cell (heights, water, top
    material) on a 256² and a 448² disc; no land past the radius.
- **The grid is 3104²** (1552 m across, 3.0x the area of 1792²). The ring between the island and
  the dam is open sea on a flat seabed 15 m down (the generator's ChannelDepth), all of it
  workable ground.
- **Measured at start-up:** generated in 7.0 s, meshed in 2.7 s, 9409 chunks, 15.1 M triangles.
  The dam is 360 pieces round 4.9 km. The crew starts at (1551, 1551).
- **Frame spikes found and fixed in the package.**
  - At 3104² about 4–8% of frames ran over 16.6 ms, the worst at 42 ms. The profiler showed the
    main thread in `WaitForLastPresentation`, waiting on the GPU. The cause was `WaterQuery`
    reading the whole 154 MB state back five times a second; with the readback made rare, no
    frame ran over.
  - `WaterQuery` now sweeps the zone a band of rows at a time: at most `BandBytes` (4 MB, 84
    rows at 3104 wide) a request and `MaxInFlight` (2) at once. A sweep starts at most every
    `Interval`. A small zone is still one band. `HasData` needs one whole sweep, since a failed
    band in the first sweep would leave zeros.
  - A full sweep at 3104² now takes about 0.4 s, and the grid feed keeps pace.
  - Test: `TheQueryReadsABigZoneInBandsAndGetsItAll` (13 bands, every row matches).
- **Clean 20 s profile at 3104²** (no editor commands in the window): all 1880 frames under
  16.6 ms, median 10.5 ms CPU and 6.0 ms GPU, worst 16.0 ms. That is near the budget; the terrain
  mesh (15 M triangles, mostly flat seabed) is the obvious thing to thin if it gets tight.
- **Memory:** managed heap about 2.7 GB (the layer store is 3104² × 16 × 8 B = 1.23 GB); the
  machine has 64 GB.

## Slice 14: a shaped open sea floor (2026-09-21)
- **Asked for:** shoals, sandbanks or reefs, so the open sea isn't one flat 15 m floor, giving
  reclaiming and routes more to work with.
- **`SeabedShaper`** (on with `TerrainGenSettings.OpenSeaFloor`; the scene's asset has it on)
  shapes the sea past the island's shelf:
  - a broad ±5 m rise and fall of the deep floor (220 m features);
  - sandbanks: the upper part of a folded noise stretched along 35°, 500 × 170 m, the crests to
    −1.5 m. How high a bank rises varies along its length;
  - shoals: broad swells following a noise all the way up, 160 m, crests to −1 m;
  - reefs: rough rock crowns, only on banks and shoals already shallower than 6 m, to about
    −0.6 m. Their cells are rock (`SurfaceMaterials` takes a reef mask).
- **Rules the tests hold it to** (`SeabedTests`, 4):
  - nothing comes above −SeabedClearDepth (0.5 m), so the sea stays open;
  - features fade in 60 m out from the shore and fade out 50 m in from the dam, so the island's
    shelf is as it was and the terminals keep deep water;
  - the pass has its own random stream: turning it on leaves every island cell as it was.
- **Two failed looks first:**
  - Hard thresholds on the noise gave sheer-sided rock pillars (reefs) and knife-edge banks.
  - A wide soft threshold never reached the top of the Perlin range, so there were no shallows
    at all.
  - Now each feature's rise follows its noise from the coverage level up to the top of the
    noise's range, and a bank or shoal is sized so its sides stay under about 20°.
- **Measured on the 3104² map** (open ring, every 4th cell): 0–2 m 6.3%, 2–4 m 4.0%,
  4–12 m 18.4%, over 12 m 71.3%; shallow reef rock 2.4%. Generation went from 7.0 to about
  7.6 s.
- **Seen, not from this slice:** a thin strip along the dam's inner face where the water stops
  short. It is the 4 m between the disc's edge and the dam face (`DamSettings.InnerOffset`).
- `WaterTests.BakingAFullMapIsCheap` (a 60 ms timing check) failed once at 63 ms on a busy
  editor and passed on the rerun. It is flaky.

## Slice 15: faster load (2026-09-21)
- **Measured first** (3104² disc, editor, stage by stage): mask 437, heights 68, valleys 284,
  coast 893, relax 1142, rivers+shores 905, materials 1259, strata+ore 819, then a settle of
  1501 ms with every one of the 9.6 M cells queued, then a mesh build of about 3000 ms.
- **What changed.** Each change either gives the same result or only changes the deep sea floor:
  - **Mesh build, run in parallel** (`ChunkedTerrainRenderer.RebuildInParallel`). A rebuild of
    64 chunks or more builds its geometry on up to 16 worker threads, 256 chunks a batch, and
    uploads on the main thread. `SmoothedTerrainRenderer`'s scratch moved into a per-worker
    `Workspace`. Meshing went from 3.4 s to 1.7 s; what is left is mostly the upload.
  - **Settle pre-scan** (`AngleOfReposeSimulator.DropSettled`). The queue keeps only cells with
    a neighbour at least two steps down, steeper than the top material's angle of repose (a
    lower bound on the angle Settle uses). A dropped cell is re-queued as soon as a neighbour
    changes. It keeps 271 k of 9.6 M cells in 57 ms, and the settle went from 1501 to 641 ms.
    Test: `DroppingSettledCellsKeepsOnlyWhatCanSlideAndSettlesTheSame`.
  - **Material clean-up limited to land and the shallow sea** (`SurfaceMaterials.CleanArea`,
    above ShelfFarDepth). The deep floor is all rock, so there is nothing to despeckle, but it
    was one patch of millions of cells flooded five times. Materials went from 1259 to 544 ms.
  - **Distance floods seed only edge cells, and stop at the reach the coast reads.** The results
    within reach are the same. Coast went from 893 to 634 ms.
  - **Relax sweeps each row only across its land span**, in the same order, so the result is
    identical. Relax went from 1142 to 840 ms and rivers+shores from 905 to 476 ms.
- **Result in play:** "Generated in 5367 ms, meshed in 1726 ms", down from 7623 + 3443 ms.
  378/378 pass, and the island-untouched tests (`LandRadiusTests`, `SeabedTests`) still hold
  cell for cell.
- **Left:** the mesh upload (about 1.7 s; the terrain LOD should shrink it); strata+ore (0.77 s,
  a serial SetColumn per cell raising events); two shore clean-ups that each flood the whole sea.

## Slice 16: terrain levels of detail (2026-09-21)
- **Asked for:** simplify the mesh far from the camera to cut the frame cost (15.1 M triangles,
  mostly flat seabed).
- **`TerrainLod`** (TerrainView: `_levelsOfDetail` on; distances 120 / 300 / 650 m; 48 builds a
  frame). A chunk is drawn at level 0 (one quad a cell), 1 (2×2 cells), 2 (4×4) or 3 (8×8),
  chosen by the distance from the camera to its middle, with 10% hysteresis.
  - A level is built only when first wanted, nearest first, up to the budget a frame. Until
    then a chunk keeps what it shows. A level already built and still fresh swaps in with no
    build.
  - An edit rebuilds the level on screen and marks the chunk's other levels stale.
  - Start-up builds each chunk once, at the level the camera wants it.
- **`SmoothedTerrainRenderer.BuildCoarse`:**
  - Corners are the grid's own corner heights every 2^level corners, averaged over in-world
    cells only; with none it gives NaN and the quad is left out, so the disc edge doesn't sag.
  - Each quad takes the colour of its middle cell, and its steep-face colour from what lies a
    metre under that cell.
  - Skirts (0.75 + 1.5 × s × cell metres deep, both faces) hang from every chunk edge to cover
    the cracks where a coarse edge meets a finer neighbour.
  - The terrain shader colours from the cell map by world position, so the textures hold at
    every level.
- Without a `TerrainLod` the renderer is level 0 only, as before, and the existing renderer tests
  are unchanged.
- **Tests (`TerrainLodTests`, 4):**
  - the first build makes each chunk at its wanted level;
  - a moving viewer refines within the budget and swaps back without a build;
  - an edit rebuilds the shown level and stales the rest;
  - coarse corners sit on the grid's corner heights with no NaN.
- **Measured in play at 3104²:**
  - "Generated in 5014 ms, meshed in 279 ms" (meshing was 1726 ms);
  - start-up 782 k triangles (was 15.1 M); 1.3–2.1 M in game views; 703 k for the whole disc;
  - clean 20 s profile: all frames under 16.6 ms, median 9.5 ms CPU and 4.5 ms GPU (was 10.5
    and 6.0), worst 12.1 ms (was 16.0);
  - no cracks seen at the level boundaries on a low oblique view.
- `WaterTests.BakingAFullMapIsCheap` (60 ms) failed again at 60.5 ms on a loaded editor. It
  measures the old static sea's bake, which the dynamic water has replaced; its margin is too
  thin for an editor holding a 3 GB map.

## Water meets the dam face (2026-09-21)
- **The strip:** the dam's inner face stood `DamSettings.InnerOffset` (4 m) out from the disc's
  edge. The water ends at the disc's last cells, a staircase of 0.5 m squares, so a band of
  nothing showed between the sea and the concrete all the way round. No reason for the 4 m was
  on record; it predates the dynamic water.
- **Fix:** InnerOffset is now −1 m (negative allowed). The face sits a metre inside the disc
  edge, so the concrete overlaps the outermost water cells and hides the stepped edge. The dam
  is laid out at radius 774 m (356 pieces). Nothing else moved: the spillway outlets sit 8 m
  inside the disc edge, still in open water, and the terminals and pads follow the ring.
- **Seen in play:** the view where the strip showed is clean, and a low close-up along the
  face has the water lapping the concrete round the curve. 382/382 pass.

## Removed the flaky water-bake timing test (2026-09-21)
- `WaterTests.BakingAFullMapIsCheap` asserted that baking the old static sea's `WaterField` on a
  512² map took under 60 ms. It failed at 60.5–63 ms twice today on an editor holding the
  3104² map. It measured code the dynamic water has replaced: `WaterView` is hidden while the
  simulation runs.
- Ronan's call: remove it rather than loosen it. The other WaterTests (what the bake produces,
  and the swell rules) stay. 381/381 pass.

## The crew unit: a floating ball robot (2026-09-21)
- **Ronan:** the smallest unit, one person in the game, is a small floating ball robot. He asked
  for 8 concepts to find its style, and picked the first, **`crew_0_lens_8000`**
  (`Art/Concepts~/crew_0_lens_8000_00001_.png`):
  - a cream-white shell of rounded panels with a sunny yellow band round the middle;
  - one big glossy cyan lens for an eye;
  - two small stubby arms on round shoulder joints;
  - a soft glow under its belly where it hovers.
- **How it was made:** the fixed `TinyDiggers_Style_Krea2` workflow, seed 8000. The prompt was
  the subject sentence, then a new **ROBOT clause**, then the core verbatim. The clause is not in
  STYLE.md yet (block changes are Ronan's call):
  > Robot: a small hovering round robot about the size of a football, clearly floating in the air
  > a hand's width above its soft shadow, with a gentle glow underneath where it floats. Smooth
  > rounded shell panels like a sturdy toy, few parts, big readable shapes, a simple friendly
  > face, paint slightly worn at the edges. No plants or bushes anywhere.
- **Also made before the pick** (in Concepts~ for reference): worker (hard-hat, 8001),
  brutalist (concrete panels and cyan slits, 8002), two-tone (cream over teal with a face screen,
  8003). The fifth (rotor, 8004) stalled in ComfyUI on a full GPU (23.5 of 24.5 GB with Unity
  open), and the rest were not run once Ronan picked.

### The crew unit model: rigged and animated (2026-09-21)
- **Ronan:** "make sure to animate and rig it in blender as well".
- **Pipeline.**
  1. The picked concept was re-rendered de-lit (the same prompt with even studio light;
     `Art/Concepts~/crew_lens_delit_8000_00001_.png`).
  2. It went through `TinyDiggers_Trellis2`, with the reduction done **inside ComfyUI**: the
     `DecimateMesh` node set to 20,000 faces in **qem** placement, then the texture baked onto the
     low mesh. The raw GLB is kept as `Art/Blender~/crew_lens_q20k.glb`.
  3. `Art/Tools/td_crew.py` (headless Blender) finds the parts, rigs, animates and exports. Its
     source file is `Art/Blender~/crew_unit.blend`.
- **What refused on the way, and the fix:**
  - Blender collapse decimation of the 690k Trellis2 shell shattered it into a crumpled mess, and
    so did ComfyUI's decimation to 8k in **midpoint** mode. QEM at 20k keeps the shape.
  - Face windings came out about half wrong (6,976 of 14,301 faces). In URP they showed as dark
    mirror-like patches. `orient_outward` turns each face away from its part's centre, the ball
    or an arm; after that the material is back-face culled.
  - The lens is dark teal, not bright cyan, and Trellis2 painted a second lens on the back it
    never saw. The lens is now found by face colour (green and blue above red), keeping only the
    biggest group of faces pointing one way.
  - The ball fit (shrink toward the median) came out at 0.139 m. A trimmed least-squares sphere
    fit gives 0.283 m.
  - Arm swing turned about the side axis, but Trellis2's arms stick out sideways, so the swing
    only twisted each arm about its own length. The turns are now about axes square to the arm.
  - The FBX option `bake_space_transform` flattened the clips in Unity; Blender itself marks it
    broken with armatures. It is off, and the Unity importer bakes the axis conversion instead.
  - A front on Blender −Y imported facing Unity **−Z**. The FBX axis options cannot change that,
    because Unity reads the declared axes and undoes them. The robot is placed facing Blender +Y,
    which imports as Unity +Z.
- **Result, as measured in Unity:**
  - 14,301 triangles, one 2048² albedo, bones Root/Body/Arm.L/Arm.R, every vertex rigid to one
    bone;
  - the ball is 0.283 m in radius, its centre 0.53 m up, the bottom about 0.25 m off the ground.
  - `crew_unit.prefab`: Animator with `crew_unit.controller`, default state Idle.

    | Clip | Length | Body turns up to | Arm turns up to |
    |------|--------|------------------|-----------------|
    | Idle | 2.0 s | 13.6° | 14.1° |
    | Move | 1.0 s | 11.5° | 46.7° |
    | Work | 1.2 s | 11.2° | 55.3° |
    | Carry | 1.0 s | 7.5° | 50.3° |

  - Sheets: `Screenshots/Art/Crew/crew_contact_sheet.png` (Blender) and `crew_unity_sheet.png`.
- **Open:**
  - The band came out orange, not the concept's sunny yellow.
  - There is a second lens on the back.
  - The arms are rather big.
  - There is no hover glow mesh yet.
  - `CrewView` still draws primitive crew; putting the prefab in is the next step.

### The crew robot fixed: back lens, arms, glow (2026-09-21)
- **Ronan:** "Fix robot first the band isn't important". The orange band stays.
- **Back lens.** Trellis2 painted a second lens on the back.
  - `cap_back_lens` finds the densest group of teal faces facing away from the front.
  - Its cone is the 95th-percentile angle plus 6°, which comes to 28.6°. A first try used the
    widest teal face and painted 4,213 faces; stray flecks had widened the cone.
  - The socket's vertices are pushed out onto the sphere, and 1,312 faces are painted the
    median shell colour of the ring just outside the cone.
- **Arms.** Each arm is its own mesh piece in Trellis2's output (557 and 470 vertices). The rig
  had tied only the part past 1.12 r to the arm, which is about half. The shoulder half stayed
  on Body, so the arm would tear at the socket when it swung. Arms are now whole pieces; the
  distance rule is kept as a fallback.
- **Arm size.** Kept as it is: the concept's arms are about this size.
- **Glow.**
  - `CrewGlow` is a 24-segment disc with a radius of 0.42 r, facing down, riding Body, 3 mm under
    the lowest point of the mesh near the axis.
  - That lowest point is Trellis2's dark hover pad, which hangs about 2.4 cm below the sphere. A
    disc under the sphere itself was hidden inside the pad.
  - In Unity it uses `crew_glow.mat`: URP Particles/Unlit, additive, no culling, no shadow,
    with a 64² smooth radial falloff texture `crew_glow.png`.
- **Open:** the glow reads whitish-yellow, not the concept's warm yellow, because the HDR colour
  saturates. It is a material tweak.

### The crew robot's eye glows (2026-09-21)
- **Ronan:** "he needs a glow in his eye".
- **Eye.** `td_crew.eye_emission` writes `crew_unit_emission.png`, on the same UVs as the albedo.
  - Only the 239 front-lens faces are lit, in cyan (0.35, 1.0, 0.95). The value falls from full
    at the lens centre to zero at its rim: (1 − x²)^1.5, where x is the angle from the centre over
    the 90th-percentile lens angle.
  - A flat fill, tried first, showed the jagged outline of the lens faces.
  - `crew_unit.mat` has emission on, with that map and an emission colour of white × 2.2.
- **Hover glow.** Its colour changed from (1, 0.7, 0.28) × 4 to (1, 0.62, 0.18) × 1.6. At × 4
  every channel saturated and the glow read white; at × 1.6 it stays warm yellow.

### The crew robot in the game, and its size (2026-09-21)
- **Ronan:** "add to game but we need to see various size scales of him in it to pick the one
  that fits".
- **CrewView.**
  - With `_bodyPrefab` set (`crew_unit.prefab` in TerrainSandbox), each unit's body is the robot
    instead of the placeholder box. With it empty, the boxes are still used.
  - The robot's origin is the ground; it hovers by itself. `bodyScale` is read every frame; 1 is
    as modelled.
  - The clip comes from `CrewAnimation.StateFor(state, loaded)`:
    - Digging, Tipping or Transferring plays Work;
    - otherwise a loaded unit plays Carry;
    - an empty unit on the move plays Move;
    - anything else plays Idle.
  - Clips cross-fade over 0.15 s.
  - Selection tints the body (not the glow) through a property block, and a sphere collider
    round the model takes the clicks.
- **Roles look the same for now.** The boxes coloured diggers yellow and haulers blue.
- **Size shots:** `TinyDiggers/Crew Scale Capture`, in play, stands a row at 0.5×, 0.75×, 1×,
  1.5×, 2× and 3× in front of the crew. It shoots them from 12 m, 30 m and 70 m, at pitches of
  35°, 45° and 55°. The shots are in `Screenshots/Crew/crew_scale_*.png`.

  | Scale | Ball across | Tall, with the hover |
  |-------|-------------|----------------------|
  | 0.5× | 0.29 m | 0.43 m |
  | 1× | 0.57 m | 0.85 m |
  | 2× | 1.14 m | 1.70 m |
  | 3× | 1.71 m | 2.55 m |

- **Tests:** 389/389 pass, 8 of them new in `CrewAnimationTests`.
- **Waiting on:** Ronan to pick the scale.

### The crew robot remade in Blender, at human scale (2026-09-21)
- **Ronan:** "the smallest ball will be our smallest unit, these represent our human scale". He
  asked for the model to be remade completely in Blender, "much better and more detailed".
- **Scale.** The 0.5× robot from the lineup is now the model's true size: a ball 0.285 m across,
  floating 0.125 m up, 0.43 m to the top. `CrewView.bodyScale` 1 is human scale.
- **Model.** `Art/Tools/td_crew_model.py` builds it from primitives, exact booleans and bevels,
  after the concept:
  - a cream shell with recessed panel seams: two meridians, and latitude rings at +0.62 r and
    −0.58 r;
  - a raised sunny-yellow band with rounded rims. The Trellis2 one was orange; the concept's is
    yellow.
  - the eye: a socket cut through shell and band, a dark bezel, a domed glass lens with a darker
    iris ring, and a highlight. The eye is tilted up 8°.
  - dark shoulder sockets with four bolts each;
  - egg-shaped arms with dark tips on a shoulder hub, hanging down and 15° forward;
  - a dark pad ring round a glowing belly pad.
- **Size and materials.** 17,720 triangles and 7 flat materials: CrewShell, CrewTrim, CrewBand,
  CrewLens, CrewIris, CrewHighlight, CrewPad. There are no textures, except a 64² radial
  emission map for the lens, whose UVs are planar across its face.
- **Rig and clips.** td_crew's rig, clips, hover-glow disc, export and contact sheet, unchanged.
  td_crew gained `LENGTH`, which scales the clips' lifts and the sheet camera with the model, and
  `SHEET_COLOUR`. In Unity, Work swings the arm 54.7° and pitches the body 11.2°, as before.
- **Unity import.** The importer maps the FBX materials by name onto `crew_*.mat`: URP Lit,
  emissive for the lens, iris, highlight and pad. The old Trellis2 albedo, emission map and
  `crew_unit.mat` are gone. The Trellis2 GLB stays in `Blender~` as history.
- **Selection tint.** It now tints each material slot by its own colour, and only when the
  selection changes. One `_BaseColor` for the whole renderer would have painted every part one
  colour.
- **Lessons.**
  - Blender's primitives come with a UV layer. New UVs must go into that layer: a second layer
    is not the one exported. The lens emission came out as a ring until this was fixed.
  - The contact sheet uses Workbench with MATERIAL colour, since there are no textures.
- **Tests:** 389/389 pass.

## RTS controls and the worker robot (2026-09-21) — agreed design
- **Ronan:** "these guys are not our diggers and haulers, they are the first unit you would get,
  they can dig and haul but very very small amounts like a human with a wheelbarrow". They need
  RTS controls: drag over them to select, and click to order them to move.
- **Rulings** (AskUserQuestion; the recommended option each time):
  - After a move order, a robot **holds** where it was sent and ignores designated work.
    Right-clicking a dig, fill or dump area orders it back to work there.
  - It carries **0.1 m³** (one barrow) and moves at **1.5 m/s**.
  - The game starts with **4** of them.
- **Design:**
  - A new `UnitRole.Worker`: it digs and carries its own load, as a digger does when the crew
    has no haulers. Digger and Hauler stay, for the machines later.
  - Select tool: left-drag for a box (shift adds), left-click selects one, right-click on ground
    sends the selection there, spread over nearby free cells, and Escape clears.
  - Markers: a ring under each selected robot, and a short-lived ring at the ordered target.
  - Plain tested logic: the move order and hold, the spreading, and the box maths. CrewView and
    PlayerTools stay thin.
- **Barrow size changed to 0.19 m³** (Ronan, AskUserQuestion). The crew digs a whole step of one
  cell per scoop, 0.5 × 0.5 × 0.5 m = 0.125 m³ of ground, which swells by ×1.1 to ×1.5 once dug
  loose. A 0.1 m³ barrow could never take a scoop. At 0.19 m³ every trip is one scoop of
  anything, and nothing in how digging works changes. The other option, thinner 0.1 m scoops,
  would leave ground between the 0.5 m levels mid-dig, which the benching, slope and pathing
  rules assume never happens.
- **Built:**
  - `UnitRole.Worker` and `CrewUnit.Digs`. A worker never waits for or hands to a hauler.
  - `CrewUnit.OrderMoveTo(x, z, workOnArrival)`, `Holding`, `OrderTarget` and `ReleaseHold`, with
    a new `CrewJobKind.Go`. A holding unit takes no work. It picks an unreached order back up
    after standing aside, escaping water or a closed path, and refuses a cell off the map or
    one it has no way to.
  - `CrewFormation.Targets` and `.Assign`: the nearest distinct cells, then the nearest unit to
    the goal picks first.
  - `SelectionBox`, the drag threshold, box and GUI maths.
  - `CrewView`:
    - 4 workers (0.19 m³, 1.5 m/s) and no diggers or haulers in TerrainSandbox;
    - a multi-selection with `SelectInScreenRect`, `TrySelectAt(ray, add)` and
      `OrderSelectedTo`. An order onto a dig, fill or dump designation works on arrival;
      anywhere else holds.
    - rings under the selected units and a shrinking ring where they were sent, in
      `crew_select.mat`, URP Unlit;
    - paths drawn only for selected units, 0.06 m wide.
  - `PlayerTools`, Select tool: a left-drag box drawn with IMGUI, click to select, shift to add
    or drop, and right-click on ground to send.
- **Tipping leaves a part-step in the barrow.** Tipping goes a whole step of one cell (0.125 m³),
  so a load of 0.156 m³ tips 0.125 m³ and carries 0.031 m³ on to the next trip, as any digger
  does. The worker test first expected an empty barrow and failed on that.
- **Checked in play:** selected all 4 through `SelectInScreenRect` with the game camera, sent them
  about 5 m away and back, and got 4 of 4 each time. Each went to its own cell next to the one
  clicked and held there (`Screenshots/Crew/rts_move_1.png`). The live mouse (drag and
  right-click) is wired, but only the code under it was driven in this check.
- **Tests:** 401/401.

## Natural terrain: the baseline (2026-09-21)
- **Ronan:** the land "feels like swiss cheese". He wants a more natural landscape:
  - smooth hills and flat sections;
  - beaches that are not cliffs;
  - some cliffs, and mountains.
- **Measured** on the TerrainSandbox island (seed 11, Continent, 3104² cells of 0.5 m, 0.24 km² of
  land, highest point 34.5 m). Slope is taken over 2 m. Shots are in
  `Screenshots/Terrain/Baseline/`.

  | Measure (land only) | Value |
  |---|---|
  | Flat, under 3° | 6% |
  | 3–15° | 14% |
  | 15–35° | 34% |
  | Over 35° | 46% |
  | Enclosed pits (all 8 neighbours higher) | 4,089 (4.3 per 1,000 land cells), 235 of them 1 m deep or more |
  | Cells stepping more than 1 m to a neighbour | 8.1% |
  | Coast cells stepping more than 0.5 m to a neighbour | 92% |

- **Why it looks like this:**
  - The same noise stack runs over the whole island: 16 m over 120 m features with four fBm
    octaves, plus 1.5 m every 40 m and 1.1 m every 8 m. So nowhere is plain, and every slope
    is crumpled.
  - Nothing fills hollows, so the lumps and the valley cuts leave pits.
  - The land starts about 7 m up at the waterline, so the coast is mostly a step.
- **Rulings** (AskUserQuestion):
  - **Land mix: random with every generation.** Ronan's words: "each generation should be
    random". The seed draws the mix of plains, hills and mountains, instead of one fixed split.
  - **Erosion: yes, at 2 m**, then scaled up to the grid.
  - **Phase by phase:** preview tool and scorecard, then land types, then erosion and pit filling,
    then coasts, then surfaces. Each phase is shown with shots and numbers before the next.

### Natural terrain, phase 1: the preview and the scorecard (2026-09-21)
- **`TerrainScorecard.Measure(grid)`** (Terrain runtime, tested) measures the land:
  - area and highest point;
  - slope bands over 1 m either side: under 3°, 3–15°, 15–35°, over 35°;
  - pits (lower than all 8 neighbours), and deep pits (1 m or more);
  - cells stepping more than 1 m;
  - coast cells, and the beach share of them: at most 1 m above the sea and at most one step
    to any land neighbour.
- **`TinyDiggers/Terrain Preview`** (Editor, `TerrainPreviewMenu.cs`) builds the scene's grid for
  the scene seed plus seeds 23, 37 and 58, the way TerrainView does (generate, then settle the
  slump). For each it writes a shaded map, a slope map with pits in magenta, and the scorecard to
  `Screenshots/Terrain/Preview/`, plus 2×2 contact sheets. About 5.5–6 s a seed.
- **Baseline, all four seeds:**

  | Measure | Range |
  |---|---|
  | Flat | 5–7% |
  | Gentle | 12–15% |
  | Moderate | 33–35% |
  | Steep | 43–49% |
  | Pits | 4,089–4,427 (4.1–4.9 per 1,000 land cells) |
  | Cliff steps | 6.6–8.1% |
  | Beach coast | 20–25% |

  The slope maps are orange and red almost everywhere, lowlands included. So the problem is the
  generator, not seed 11.
- **Unity quirk:** a new script written from outside while the editor was busy was imported as a
  MonoScript, but left out of its assembly's source list. Neither Refresh, nor a forced
  reimport, nor a clean compile added it. Renaming it with `AssetDatabase.MoveAsset` did.

### Natural terrain, phase 2: land types (2026-09-21)
- **Built:**
  - `LandTypes` (tested) and `LandMix`: each seed draws its mix, as Ronan asked ("each
    generation should be random"):
    - plains between 25% and 55%;
    - mountains between 8% and 30%;
    - hills take the rest, never less than 15%. If the two ranges ask for too much, they
      shrink in proportion.
  - A type field: fBm over 260 m, plus 0.35 × closeness to the ridge line, minus 0.3 × a
    60 m fade from the sea. It is cut at quantiles of a sample of the land, so the shares
    come out as drawn, and blended over ±0.06 across each border.
  - Heights per type (`IslandGenerator.BuildTypedHeights`, on by `UseLandTypes`):
    - plains: 1 m at the shore, +5 m rising over 120 m inland, ±1.5 m swells every 180 m;
    - hills: the plain plus mounds up to 8 m every 140 m. The first try, 12 m every 110 m, was
      still orange and red on the slope map.
    - mountains: the hills plus the ridged crest (stronger on the ridge line), and the medium
      and fine octaves, which now apply only here.
  - Benches only pull land that is already at least 60% of their height, so they no longer
    lift tables out of plains.
  - The valley cut is ×0.2 on plains, rising to ×1 in the uplands.
  - A test that took topsoil to mean "above 10 m" now measures from the sand's limit (3 m) plus
    2 m, since most land is now low plain.
- **Scorecard**, same four seeds:

  | Measure | Baseline | Phase 2 |
  |---|---|---|
  | Flat | 5–7% | 22–32% |
  | Gentle | 12–15% | 17–21% |
  | Steep | 43–49% | 22–36% |
  | Pits | 4,089–4,427 | 364–905 |
  | Pits 1 m deep or more | 235–384 | 4–98 |
  | Cliff steps | 6.6–8.1% | 2.9–6.1% |
  | Beach coast | 20–25% | 61–71% |

  Seed 37 drew the most mountains (28%) and is the steepest, as it should be. Maps are in
  `Screenshots/Terrain/Preview/phase2_*`; in-game shots in `Screenshots/Terrain/Phase2/`.
- **Seen, left for later phases:**
  - Streaks and contour stripes where gentle ground is rounded to 0.5 m levels (phase 3).
  - Sandbanks stepping straight into the water in places (phase 4).
  - Sand patches on inland low ground (phase 5).
  - The sea looks olive in the in-game shots. It may be the shallow shelf showing through;
    to check in phase 4.
- **Tests:** 409/409.

### Natural terrain, phase 3: straight-line distance, erosion, settling, filled hollows (2026-09-21)
- **Ronan:** move to phase 3, and look at the vertical banding stripes. Full-resolution crops
  showed three separate causes:
  1. **Diamonds, straight lines and chevrons** on the beaches, the plains and the shelf. The
     distance-to-shore was a four-way flood, which measures city-block distance, and its
     equal-distance lines are diamonds. Replaced by an exact Euclidean distance transform
     (`TerrainErosion.EuclideanDistance`, Felzenszwalb–Huttenlocher, parallel over rows and
     columns).
  2. **Straight one-cell grooves**, and starbursts into ponds. The D8 valley cut lowered every
     cell along flow lines that only run in eight directions. It is skipped when erosion is on;
     the accumulation is still computed for valley clay.
  3. **Vertical curtains** on steep hillsides. The fine roughness was steeper than one step a
     cell, and the relaxation cut it back a row or a column at a time.
     `IslandGenerator.SettleSlopes` now settles the land first: 16 passes at full resolution in
     eight directions, to 0.9 of a height step a cell (soil) or of a cliff step (the mountains),
     so the relaxation has little left to cut.
- **Erosion** (`TerrainErosion`, tested; `ErodeIsland`):
  - droplet erosion on a 2 m grid over the land's bounding box: 400,000 drops per km², 40 steps
    each, a brush of radius 3;
  - then 40 passes of thermal settling: soil to 38°, the mountains to 75°;
  - only the change is scaled back up (bilinear), so the fine shape underneath is kept. The sea
    is fixed, and land never goes below one height step above the sea.
- **Hollows:** `TerrainErosion.FillDepressions`, a priority flood over height-level buckets from
  the water. It fills every land hollow to its spill level on the levels, and lakes are kept.
- **Bugs found on the way:**
  - **Runaway drops.** With speed uncapped, and the sign turned so drops sped up going downhill,
    capacity grew without bound and drops drilled the land to −50 km: a 30 m cone came out as
    ±50 km. Fixed with the standard speed form (`speed² + fall·gravity`), a cap of 4, and no
    brush cell dug below the drop's new height. The test now asserts no cell moves 4 m or more.
  - **Lowlands drowned at 1 m steps.** Eroded land was floored at 0.3 m, which rounds to the sea
    with 1 m steps. It is floored at one height step now (land starts one step up), and the
    cell-size test passes again.
  - **Cliffs worn down.** A rock talus of 62° wore them away, and the cliff and rocky-shore
    tests fell just short. It is 75° now.
- **Scorecard** (same four seeds):

  | Measure | Baseline | Phase 2 | Phase 3 |
  |---|---|---|---|
  | Flat | 5–7% | 22–32% | 44–53% |
  | Gentle | 12–15% | 17–21% | 17–18% |
  | Steep | 43–49% | 22–36% | 11–18% |
  | Pits | 4,089–4,427 | 364–905 | 4–7 |
  | Beach coast | 20–25% | 61–71% | 66–80% |
  | Cliff steps | 6.6–8.1% | 2.9–6.1% | 0.8–1.7% |

  Erosion moves 0.43–0.66 m on average. Generation takes about 6 s a seed (it was 5.5–6).
- **Evidence:** `Screenshots/Terrain/Phase3/stripes_before_after.png` (left before, right
  after), plus `phase3_*` preview maps and in-game shots.
- **Left for phase 4 (coasts):** the lake beds still show straight-edged underwater contours;
  small round ponds read as sandy craters; the sea looks olive in game; there are few cliffs on
  the coasts.
- **Tests:** 414/414.

### Natural terrain, phase 4: coasts (2026-09-21)
- **The olive sea was a false alarm.** The in-game shots were taken the moment play began, before
  the water had spun up. Rendered a few seconds in, with or without MSAA, the sea is blue. Any
  capture should wait about 6 s into play.
- **Cliff coasts:**
  - Each seed draws a share of 15–35% and a height of 5–10 m (`IslandMap.CliffCoastShare` and
    `CliffCoastHeight`).
  - A 180 m field along the coast, plus 0.4 × upland, is cut at quantiles so the drawn share
    of the shore is cliff, and blended wide (`CliffCoastBlend` 0.3).
  - There the land stands at the cliff's height to the sea, and eases back over 35 m inland.
- **Four fixes before the cliffs looked right:**
  1. **The flat cliff top was pulled onto a beach.** The coast pass took its gentle top for a
     beach; it now skips cliff coasts.
  2. **The coast was dragged down to the shelf.** The relaxation pulled land toward the water a
     soil step a cell. `MarkCliffCoast` now marks the cliff land, and the sea 4 m in front of
     it, as cliff for the relaxation.
  3. **A rampart round the lake, and walls inland.** Cliffs are now measured from the open sea
     only (`OpenSea`: water joined to the disc's rim). Before, a narrow blend switched a cliff
     stretch on within metres and left a wall running inland from its end.
  4. **A face combed into teeth.** `ShapeCliffFaces` now sets the face first: at most the shelf
     plus 0.9 of a cliff step a cell, by straight-line distance to the open sea. Before, the
     relaxation cut the 10 m drop back a row at a time.
- **Crater ponds are gone.** `MinWaterPocket` went from 12 to 800 (m² at 1 m cells), so ponds a
  few metres across become land. Real lakes stay.
- **Scorecard**, same four seeds:

  | Measure | Phase 3 | Phase 4 |
  |---|---|---|
  | Beach coast | 66–80% | 48–61%; the rest is cliff and rocky shore |
  | Cliff steps | 0.8–1.7% | 3.2–4.8% |
  | Pits | 4–7 | 4–9 |
  | Flat | 44–53% | 46–55% |

  The land within 2 m of the sea that stands 4 m or higher went from 0% to about 8–10%. About
  6.9 s a seed.
- **New test:** `LandShapeTests.CliffCoastsStandWhereTheSeedDrawsThem`. Half the coast drawn as
  cliff stands high by the sea: 147 cells against 69 with none. The bar is at least 1.5× plus 20.
- **Ronan asked whether the mountains had got smaller.** Their height hasn't changed: the peak
  is 32–36 m, against 30.5–34.5 m at the start. They cover only the drawn 13–20% of the land now,
  and erosion rounds them. Taller peaks are offered as an option.
- **Tests:** 415/415.
- **Mountains taller, drawn per seed** (Ronan, AskUserQuestion: "40–60 m, per seed").
  - With land types the crest's height is drawn per seed from `MountainCrestMin`–`Max`, 35–70 m
    (`IslandMap.MountainCrest`). `RidgeHeight` stays the old single value.
  - Peaks do not follow the crest one for one: a crest of 47 m gave 50 m, 62 m gave 57.5 m. The
    first range, 45–70, put every seed at 50 m or more, so it was widened. Peaks are now 45,
    42.5, 55 and 42.5 m on the four seeds, against 32–36 m before.
  - Steep ground is 13–19%, cliff steps 4.5–9%, pits 1–7, and generation about 7.3–8.2 s a seed.
- **Unity gotcha:** a new field's default does not reach a settings asset that never saved it.
  The loaded asset kept an in-memory value from an earlier build (crest minimum 45 where the code
  said 35) until it was set and saved (`SetDirty` + `SaveAssets`). New settings now go into the
  asset.

### Much bigger mountains, faster generation, new budgets (2026-09-21)
- **Ronan:** "I still think mountains could be much larger and taller".
- **The asset now draws:**
  - mountains as 18–38% of the land (was 8–30%);
  - the crest at 80–130 m (was 35–70 m);
  - a ridge band 170 m wide (was 110 m).
- **Result, seeds 11, 23, 37 and 58:**

  | Measure | Value |
  |---|---|
  | Peaks | 88.5, 77.5, 102 and 79 m |
  | Mountains | 23–29% of the land |
  | Steep ground | 26–32% |
  | Cliff steps | 15–20% (the mountains' rock faces) |
  | Pits | 3–9 |

- **Faster:** heights no longer compute the hill and mountain noise where their weight is zero,
  which was most of the land; that took heights from about 240 ms to about 110 ms at 1024². The
  thermal passes swap two buffers instead of copying back. The flow accumulation sorts a key array
  instead of using a comparison delegate.
- **Budgets raised** with Ronan's OK (AskUserQuestion: "Raise to 1.6 s"). The 1024² map at 0.5 m
  went from 1.2 s to 1.6 s: in the suite it was measuring 1,204–1,239 ms, and about 1,110 ms
  alone. The 512² map went from 500 ms to 650 ms: it measured 532 ms before the speed-ups,
  443–457 ms after.
- **Rivers and creeks, agreed design** (AskUserQuestion; the recommended option each time):
  - 1–3 rivers and 4–10 creeks, random per seed;
  - rivers block the crew (deeper than wading), and creeks wade;
  - the water is simulated from springs at each head, not drawn as ribbons.

### Rivers and creeks, step 1: channels in the generator (2026-09-21) — agreed plan
- **Ronan approved the design** ("yes go ahead with step 1"). Step 1 is the generator only.
  Springs and pre-filling are step 2; the crew crossing test is step 3.
- **Correction to the entry above.** Its speed-up numbers were read while the speed-up edit had
  not compiled: a CS0136 duplicate local in `FlowAccumulation` left the old dll in place. The
  fixed build gives the same land to the cell, and the suite is green at 415/415. The timings are
  re-measured below.
- **The plan:**
  - `CarveRivers`, the D8 upstream walk, is replaced by `RiverChannels` (Terrain/Runtime):
    1. Heads are picked from high ground with water running into them: rivers from the upper
       ground, creeks from the middle. River heads are at least 150 m apart; creek heads are at
       least 60 m from any other head. A small island could not fit 13 heads 150 m apart, so
       creeks use the tighter spacing. If a map cannot fit the drawn count at that spacing, the
       spacing is halved once.
    2. Each head is routed to water by A*. A step costs its length, plus a heavy penalty per
       metre of climb. The heuristic is the straight-line distance to water.
    3. The route is smoothed (Chaikin), and a meander is added that grows as the ground flattens
       and tapers to zero at the head and the mouth.
    4. The channel is cut with a rounded bed and 0.5 m-per-metre banks. The floor is held
       monotonically descending, and while it runs over land it stops 0.5 m under sea level.
  - Rivers are cut first; a creek may end where it meets a river.
  - Counts are drawn from their own random stream (the seed mixed with a constant), so no
    existing seed's ores or anything else drawn later move.
  - `IslandMap.Channels` is the new record. `Rivers` stays filled from the river channels, so
    `WaterView` and the old tests keep working.
- **Built, and what changed from the plan while building it:**
  - **Routes follow the pit fill's flow, not A*.** A* with a climb penalty and a noise cost
    needed 250–570 ms at 1024², and half its searches ran out of budget a few cells from the
    shore. `TerrainErosion.FillDepressions` now optionally records, for every cell, the neighbour
    the flood reached it from. Following that from a head runs down the valley water would
    really take, it costs only the route's own length, and creeks run into rivers the way the
    drainage does. Channels are therefore cut after the pit fill, followed by one relaxation and
    one more speck cleanup, because a mouth cut through a narrow neck can strand a scrap of coast.
    The noise-cost settings were removed.
  - **The bed is cut from the lowest ground beside it, not the ground under it.** Across a
    hillside the downhill bank stood below the bed's rim and the water would have run out. A
    levee pass was tried and dropped: the slope relaxation shaved the raised bank back down.
  - **Kinks and loops are cut out.** A meander laid round a tight bend folded back on itself,
    leaving a bed right beside its own downstream reach with no bank between them. Where the
    line comes back within a bed width, the loop is cut out, as a river cuts off a meander. The
    line is then averaged over a bed and a half, so no bend is tighter than the bed is wide.
  - **Heads are picked in the island's frame.** The same island on a bigger disc gets the same
    channels, which `LandRadiusTests` requires.
  - **Fallback for small land.** If the drawn count doesn't fit, the second pass uses half the
    spacing and a quarter of the minimum length, and river heads may be as low as 15% of the
    peak height. The 256 m test maps need this.
- **Result, preview seeds 11, 23, 37 and 58:**
  - every river and creek drawn was cut: rivers 3, 3, 1 and 3; creeks 7, 7, 8 and 9;
  - rivers run 76–262 m and creeks 35–356 m; one or two creeks per map join a river;
  - the land scorecard is unchanged: pits 3–9, beaches 51–63%;
  - preview maps take about 7.2 s each, the same as before channels.
- **Budget:** at 1024² and 0.5 m the channels take about 100 ms and the whole map 1,141–1,169 ms
  on its own. The suite passes the 1.6 s budget, and all 420 tests pass.
- **Still open:** a creek bed 0.5 m deep is a single height step, so its "rounded" floor is
  flat. It is shallow enough to wade, as agreed.

### Rivers and creeks, step 2: springs and pre-fill (2026-09-21)
- **Ronan:** "yes go ahead with step 2".
- **`ChannelSprings`** (Presentation/DynamicWater) is a thin MonoBehaviour on the Dynamic Water
  object:
  - It puts one Source effector at every channel head.
  - When the zone builds a simulation, it reads the depths, raises each channel's bed cells to
    their running depth (`Prefill`), and writes the depths back.
  - A river runs at 0.75 of its bed's depth, so 0.75–1.1 m, over the 0.5 m wading depth. A creek
    runs at 0.25 m.
  - Spring rate = bed width × running depth × flow speed × √(catchment ÷ reference), with the
    catchment factor kept within 0.5–1.25.
- **Tuned in play**, on the scene's own island: 3 rivers, 7 creeks, about 10,000 bed cells
  pre-filled.
  - At 0.4 m/s and a 20,000 m² reference, the springs could not keep the beds full: upper
    reaches drained, and every catchment clamped to the minimum. Heads sit at the tops of their
    valleys, so what drains to one is only 27–308 m².
  - At 1.5 m/s, a 100 m² reference and a 2× cap, the big river overflowed a flat basin in the
    mountains. About 3,700 m² of land flooded outside the beds, and it was still growing.
  - Final: 0.8 m/s, 100 m² reference, 1.25× cap. After 90 s, 920 m² is wet outside the beds and
    only 2 m² of that is too deep to wade.
- **Result after 90 s:**

  | Channel | Wet | Too deep to wade |
  |---|---|---|
  | The two larger rivers | 81% and 93% | 60% and 85% |
  | The steep river (51 m of fall in 76 m) | 65% | 3%: a torrent runs fast and thin |
  | The longer creeks | 50–74% | 15–32% |
  | The shortest creeks | 14–24% | none |

  The creeks' deep points are pools at 0.53–0.56 m, only just over wading depth.
- **Evidence:** `TinyDiggers/River Capture` writes `Screenshots/Rivers/`.
- **Not right yet:**
  - The water reads bright green, because the beds are still grass under clear water; channel
    beds need a gravel or sand surface (phase 5).
  - Thin films show pale on flat ground.
  - The close-up camera poses frame badly.

### Channel beds are gravel (2026-09-22)
- **Ronan:** "do the gravel beds first then step 3". Grass beds under clear running water read as
  bright green water.
- **The bed material is the existing "Loose rock", not a new one.** It is grey-beige gravel that
  settles at 38°, so there's no new material and no palette change for STYLE. A bed's floor is
  nearly flat, and the topsoil banks beside it don't slump, so a loose top only on bed cells holds.
- **How it's built:**
  - `RiverChannels` keeps its bed mask on the map as `IslandMap.ChannelBeds`.
  - After the surface pass and its tidying, bed cells get Loose rock on top, except cells that
    are already stone. Where a channel drops through a rock step, that step is a cliff face and
    must stay rock; the cliff tests caught gravel standing as a cliff.
  - `BuildColumn` lays a gravel layer `ChannelGravelThickness` thick (0.4 m) over dirt.
  - The bed mask is marked at least 0.75 of a cell either side of the line, or a creek one or two
    cells wide missed cells the line crossed between points.
- **Result:** in play the rivers read teal-blue over grey gravel instead of green. Water depths are
  unchanged from step 2. A new test fails if a bed is neither gravel nor rock, or if less than
  three quarters of it is gravel. All 425 tests pass.

### Rivers and creeks, step 3: the crew and the channels (2026-09-22)
- **`ChannelCrossingTests`** (Presentation.Tests, which now references Units):
  1. Each generated island has its channels filled the way `ChannelSprings` fills them.
  2. The grid is given those water surfaces.
  3. The crew's `GridPathfinder` is asked to go from bank to bank at points along every river and
     creek, clear of the sea and of other channels.
- **What the tests require:**
  - A river bed is deep water, and no path crosses the river directly: it fails, or detours more
    than twice the direct distance.
  - A creek bed has water in it but is wadeable, and at least three quarters of creek crossings
    are waded straight over. A creek cut through a steep hillside can leave a bank too high to
    climb; the water is never what stops the crew.
- **The test found a pre-fill bug.** The pre-fill drew each bed's floor as a line between path
  points two cells apart. On a creek dropping 4 m a point, that line stood a metre over the ground
  near the lower point, and the creek came out 1.25 m deep there. Taking the lower point's floor
  instead left steep river stretches shallow enough to cross. The pre-fill now gives every bed
  cell its channel's fill over its own ground: 0.75–1.1 m for rivers, 0.25 m for creeks. A unit
  test now covers the steep reach.
- **Open question for Ronan:** the tests hold for the water as it starts. Once the simulation
  settles, steep river reaches run fast and thin, as in the step 2 play run: the torrent was only
  3–4% too deep, and the two larger rivers 59% and 85%. So in play the crew can ford rivers where
  they run shallow. Either keep that (fords are natural and the rule stays "deep water blocks"),
  or make river beds impassable to the crew whatever their depth.
- **Ruling (Ronan, 2026-09-22, AskUserQuestion): "Rivers always block".** River beds are
  impassable to the crew however shallow they run; creeks still go by depth.
  - `TerrainGrid.SetRiverBeds` marks the river bed cells. With live water, a marked cell holding
    any water at all counts as water: blocked for walking, and not dug.
  - A river bed that drains completely dry is ground again, so a dammed or diverted river opens up.
  - The generator marks river beds (not creek beds) when it builds the grid, and clears the marks
    when it regenerates the same grid.
  - Tests:
    - `RiverBedTests`: 10 cm of river blocks while 10 cm elsewhere is waded; a dried bed is
      ground again, and the change is reported to passability caches.
    - `ChannelCrossingTests` now also runs with rivers filled to only a tenth of their depth.
- **Crossing a river (Ronan, 2026-09-22): "they will have to fill them in or dig them out".**
  `RiverCrossingTests` puts a worker on a flat field cut by a river bed two cells wide, carrying
  0.3 m of water: a river, so it blocks. It passes the existing crew code unchanged:
  - Nothing crosses the river before it is filled.
  - Given a Fill designation three rows wide, the worker tips into the river from the bank, a
    step at a time. It builds the crossing up level with the banks, and the path then crosses
    only there; the rest of the river still blocks.
  - Fill designations were already allowed on water (only Dig is refused), and building a cell
    above the water dries it at once.
  - A worker left holding less than one tip step (0.125 m³) doesn't tip it and goes idle.

## Slice 17: mouse-first toolbar, then spline roads (2026-09-22) — Part A design, proposed
- **Ronan's brief is called "Slice 9"**, but that number already belongs to water in this log, and
  the last one used is 16. It is logged here as Slice 17.
- **Part A design, awaiting approval** (the working style is to propose the API before writing
  integration code). The plain C# pieces are unit-tested without a scene:
  - `Interaction/DesignationHistory` (plain C#): undo and redo for designation edits, 50 steps.
    Each edit records the cells' state before it (kind, target, auto, dump zone, cap):
    `Begin(label)`, `Record(x, z)`, `Commit()`, `Undo()`, `Redo()`, `CanUndo`, `CanRedo`.
    `DesignationMap` gets `CellState Snapshot(x, z)` and `Restore(x, z, CellState)`.
    Crew work (digging, filling, auto designations) is never recorded.
  - `Interaction/BrushPlan` (plain C#): the cells under a circular brush of radius 1–8, and the
    cut, fill and net m³ it would designate at H. The panel readout and the ghost both use it.
  - `Interaction/ToolIcons`: a 256×256 sprite sheet, 8×8 cells of 32 px. It is drawn by a Python
    script (`Art/Tools/td_tool_icons.py`, supersampled, flat white glyphs) and committed as a PNG.
    It is not SVG, because importing SVG needs the vector graphics package.
  - `ToolbarView`: rebuilt as groups of icon buttons with hover tooltips (label + hotkey, after
    0.4 s): Select | Dig Fill Level | Road | Dump Zone | Clear | spacer | Seed | Settings (F2) |
    Debug (F3).
  - `ToolPanelView`: the context panel above the bar. Brush radius slider; H with −/+ buttons and
    a field; an "H follows cursor" toggle; the Alt-click eyedropper readout; cut/fill/net under
    the brush; the Dump Zone cap; Clear checkboxes (dig, fill, dump zone).
  - `BrushCursorView`: the brush ring draped on the ground and coloured by tool (red dig, blue
    fill, purple level, green dump), and a translucent ghost disc at H.
  - `CrewPanelView`: top left, one row per unit (role icon, state, load bar). Click selects,
    double-click also focuses the camera (a new `RtsCamera.FocusOn(Vector3)`). A yellow banner
    shows while any unit needs a Dump Zone. The F3 debug readout stays, off by default.
  - `PlayerTools` keeps all its hotkeys. Escape still returns to Select. It gains Ctrl+Z/Ctrl+Y,
    Alt-click to pick H, and pipes every designation edit through `DesignationHistory`.
  - No new packages: uGUI 2.6.0 and the Input System are already in the manifest.
- **Part A built (approved: "Continue").**
  - `DesignationHistory`: `DesignationMap` raises `Changing` before any change, and the history
    snapshots each cell the first time an open edit touches it. Edits are dig/fill strokes,
    rectangles, roads, and the right-click and Clear-tool clears. The crew's work (a met
    designation, a unit's own ramp step) happens outside any recording and is never undone.
    Ctrl+Z undoes; Ctrl+Y or Ctrl+Shift+Z redoes.
  - `BrushPlan` gives the brush cells (a disc, radius 1–8) and their cut/fill. The panel readout
    and the ghost both use it.
  - The icons are drawn by `Art/Tools/td_tool_icons.py` into `Interaction/Resources/ToolIcons.png`:
    17 glyphs at 64 px, readable at 32. On a lit button the icon turns dark, because white on
    amber barely showed.
  - `ToolbarView` is rebuilt as icon groups with 0.4 s tooltips (name, hotkey, what it does). Seed
    needs a second click within 2.5 s, since it throws the island away. Settings opens the F2
    panel; Debug toggles the F3 readout.
  - `ToolPanelView`: the rows each tool needs.
    - Brush radius slider (Dig and Fill only; Level stays a dragged rectangle until Part B gives
      it slopes).
    - H with −/+ and a field; "H follows cursor"; the eyedropper line.
    - Cut/fill/net readout; Dump Zone cap; road width 3/5/7; Clear checkboxes.
  - `BrushCursorView`: the ring on the ground and the ghost disc at H, in tool colours.
  - `CrewPanelView`: the rows, load bars and yellow banner. Double-click glides the camera
    (`RtsCamera.FocusOn`).
  - `PlayerTools`:
    - Clicks over the UI no longer reach the ground (they used to: clicking a toolbar button also
      applied the tool behind it).
    - Hotkeys wait while a text field is focused.
    - Alt-click picks H.
    - `PointerOverride` lets scripted captures aim the tool without clicking.
    - Clear honours the checkboxes, via a new `DesignationMap.CancelDesignation`, which leaves a
      Dump Zone alone.
  - `TinyDiggers/Slice 17 Capture` writes `Screenshots/Slice17/`: the toolbar with Dig and its
    panel, the ring and ghost disc on a slope, and the crew panel with the warning. Tooltips
    follow the real mouse, so no shot shows one.
  - 441 tests green (7 new: undo/redo, the crew's work not recorded, 50-step cap, brush cells
    and volumes).

### Slice 17 Part B: spline roads — the model (built) and the integration (proposed)
- **Ronan approved Part A ("Continue").** The road model is plain C# and tested, so it is built;
  wiring it into the game waits for approval of the design below, as the working style asks.
- **Built** (Units/Runtime/Roads):
  - `RoadNetwork`: serialisable (JsonUtility) nodes and segments. A road is the chain of
    segments drawn in one go, 3/5/7 cells wide. Each node has a height, `LockToGround` and an
    optional tangent handle. `AddRoad` joins a road's ends to any node within 1.5 cells, which
    is how roads meet and junctions form. `MoveNode`, `SetHeight` (unlocks the node from the
    ground), `SetHandle`, `RemoveRoad` (drops orphaned nodes), and `Changed(road)`.
  - `RoadSpline`: a cubic Hermite through the nodes, the same curve as a Bézier with its handles
    a third of the tangent out. Tangents are automatic (Catmull-Rom) unless dragged. It is 3D:
    the height runs along it smoothly but never beyond a segment's end heights, so it never
    humps. `Sample` gives points evenly spaced by arc length. `Grade` is a segment's steepest
    stretch, not its average, because the height eases in and out at nodes and the steepest
    part is what the crew climbs.
  - `RoadPlanner`:
    - `Footprint`: road bed at the spline height across the full width, then a 0.5-cell
      shoulder blended into the ground, rounded to the height step.
    - `Settle`: the cut faces (at the ground's angle of repose) and embankments (at the spoil's)
      that slump will leave, for the ghost.
    - `Judge`: fine, steep over the 12% limit, refused over twice it.
  - A new material `Road` (id 18): packed gravel, (158, 150, 136), diggable back to loose rock.
  - `GridPathfinder.RoadCost = 0.7`: a step from one Road cell to another costs 0.7 of its
    distance and nothing for its slope. The octile heuristic is scaled by the same factor, so
    it stays admissible and the search still finds the cheapest way.
  - 8 new tests (449 green): samples evenly spaced and only forwards; grade is rise over run
    and judged against the limit; the steepest stretch is at least the average; the footprint
    has no holes across the full width; a straight road is exactly its width; drawing from a
    road's end joins it and a third road makes a junction; the network round-trips through
    JSON; units take a longer road route when it is cheaper at 0.7.
- **Proposed integration:**
  - `RoadDraft` (Interaction, plain C#, tested): the road being drawn or edited.
    - Nodes are placed by click. A node or its handle is dragged. Backspace drops the last
      node; double-click or Enter commits; Escape cancels.
    - Snapping: to a road's end or node (join); to 45° from the last node (a toggle); to the
      ground height by default.
    - Per-node height: scroll or PageUp/PageDown in 1 m steps (0.25 m with Shift), plus a "lock
      to ground" toggle.
    - Commit is refused while any segment is over twice the max grade.
  - `RoadGhostView`: a translucent ribbon with a centre line, coloured per segment (green,
    orange over the limit, red over twice). Cut faces and embankments from `Settle` are drawn
    in their own tint. Nodes show as discs and handles as dots. Each segment has a grade label
    in screen space. The panel shows cut/fill/net m³ (road and faces), each segment's %, max
    grade (field, default 12%), width 3/5/7, the 45° snap and the node lock.
  - `RoadBuilder` (Units, plain C#, tested): watches the designations. When a road-bed cell's
    designation is met, its top 0.25 m becomes Road: the column's top layer is split, so the
    height doesn't change. Deleting a road turns its Road layer back to Dirt. Editing a built
    road re-plans it: cells whose target moved get new designations, and cells left out of the
    footprint lose theirs and their Road layer.
  - `RoadNetworkHost` on the terrain holds the network. It is cleared when the island is
    regenerated. With the Road tool and no draft, clicking a road selects it for editing.
  - The terrain palette and the material atlas get a gravel recipe for Road.
  - Level: a rectangle with an optional "ramp" toggle, H at the edge the drag starts from and
    H2 at the far edge, interpolated across (`Blueprints.PlanLevel` gains the slope).
  - The old straight-leg road (`Blueprints.PlanRoad`, and `MaxGrade` 0.25 m/m in `PlayerTools`)
    is retired.
- **Part B integration built (approved: "Continue").**
  - `RoadsHost` holds the network, the builder and the draft. It takes the Road tool's mouse and
    keys and draws the ghost:
    - a ribbon coloured per segment by grade, on the road bed or on the ground where the road
      cuts in (drawn at the bed's height it was buried and invisible in the first run);
    - a centre line, cut faces on the ground they take and embankments where they stand;
    - nodes, handles, and each segment's grade in a screen label.
  - Clicking a built road with no draft picks it up to edit; Delete removes it. Scrolling over a
    node raises it (`RtsCamera.ScrollCaptured` stops the camera zooming).
  - `RoadDraft` is plain C# and tested: snap 45°, join, raise/lock, grade refusal, and edits
    moving the real nodes and junctions.
  - `RoadBuilder` is plain C# and tested: it designates, paves the top 0.25 m in place when built,
    re-plans deltas, removes, and keeps shared junction cells Road.
    `TerrainGrid.CopyLayers` is top first while `SetColumn` is bottom first, which the first
    `Pave` got backwards.
  - `RoadNetwork.ReplaceRoad` redraws a road and keeps its id.
  - The road texture comes from a new `Road` recipe in the texture generator. Regenerating the set
    left every other texture byte-identical.
  - Level has a "Ramp to" height at the far edge (`Blueprints.PlanRamp`).
  - The old straight-leg road is gone from `PlayerTools` (`Blueprints.PlanRoad` stays for its
    tests).
  - 460 tests green.
- **Evidence** (`TinyDiggers/Slice 17 Road Capture`, `Screenshots/Slice17/road_*`):
  - A road up a hillside near the crew, with grades 10.6%, 16.6% (orange) and 10.4%; cut 81 m³
    and fill 63 m³.
  - The first run had no Dump Zone: the crew stopped with loads, and the banner said so. Laying a
    road with spoil and no zone now says so at once.
  - With a zone, at 40× time for 300 s, the crew finished the fill and paved the lower part.
    The rest of the cut, up to 6.5 m into the hill, they report unreachable, and the auto-ramp
    did not open a way in. The "built" shot shows exactly that: gravel at the bottom and the
    remaining digs above.
  - A second road from the first one's middle node makes a junction (one node, three segments).
- **Ronan (mid-run):** "you may also need a dig zone / quarry area for them to get material to
  fill low spots … you cant make it appear out of thin air it has to come from someplace". A fill
  can only be met with spoil from somewhere. Today that is only another Dig designation, so a
  road that is mostly fill would leave the crew idle. A Quarry zone is proposed to Ronan below.

## 2026-09-22 — Slice 17: the Quarry zone

Ronan approved the design ("Yes let's test it"): material for a Fill has to be dug from somewhere.

- **`DesignationMap` gained a quarry layer**, like Dump Zones and separate from designations:
  `SetQuarry(x, z, on, floor)`, `IsQuarry`, `QuarryFloor`, `QuarryCells`, `QuarryCount`,
  `QuarryLeft` (m3 still above the floor). A quarry is not work of its own, so it does not count
  towards `Count` and never clears itself. `Cancel` and `ClearAll` take it off with everything
  else, and it is carried in `DesignationCellState`, so undo/redo covers it.
- **`FillCount`** tracks outstanding Fill designations: what a quarry would be dug for.
- **The crew digs a quarry only on demand.** `CrewUnit` plans `CrewJobKind.Quarry` after Dig and
  only while `FillCount > 0`; `CanQuarryStep` refuses a cut below the cell's floor, under water,
  or out of dig reach. When a Fill is waiting, the unit has nothing in the barrow and no quarry is
  marked, it goes to the new `CrewUnitState.NeedsMaterial`: "Nothing to fill with: mark a Quarry
  to dig from". The crew panel shows that as a yellow banner.
- **The tool** is hotkey 8, dragged out as a rectangle like a Dump Zone, with H as the floor it is
  never dug past. Its panel shows H, "H follows cursor", the eyedropper and how much the marked
  quarries still hold. Clear has a "Quarries" checkbox. A Fill's volume readout turns yellow and
  says "needs N m3 from a quarry" when the marked quarries cannot cover it.
- **Tests** (`QuarryTests`): the layer is its own and survives clear; `FillCount` tracks Fills; a
  Fill with no source idles with the message and moves no ground; with a quarry the fill completes
  and the quarry goes down; the floor is never cut past; nothing is quarried while nothing needs
  filling.
- **Caught in play, not in test:** a unit that had gone to `NeedsMaterial` never re-planned, so
  marking a quarry beside it changed nothing. `CrewUnit.Tick` only rearmed its rethink timer for
  Idle, Unreachable and NeedsSomewhereToTip. NeedsMaterial joins them, and `QuarryTests` has a
  case for it.
- **Note to self:** the first capture run died because I edited a .cs file while it was playing.
  That forced a domain reload mid-run; the NullReferenceExceptions from `TerrainView.Update` and
  `CrewView.Update` were mine, not the game's.

## 2026-09-22 — Water settles while the island loads

**Ronan:** "still having water issue … i realize you get flooding thats part of game but not at
start, maybe the game should have a section of load that waits for the streams to run to normal
then any surface water gets zapped". His rulings: zap once at load *and* dry films for ever;
settle until steady with a budget of 1.0 s.

- **The package gained a generic soak term** (it stays game-agnostic): `WaterSimulationDesc`
  carries `SoakDepth` and `SoakRate`, `WaterSimulation.SetSoakMask` takes one flag a cell, and the
  Depth kernel drains water shallower than `SoakDepth` at `SoakRate` where the mask allows it.
  Both default to 0, so a zone that says nothing behaves exactly as before.
- **`WaterSettle`** (TinyDiggers, pure and tested) builds the keep mask — the sea's basin and the
  channel beds — counts the stray water standing anywhere else, judges steadiness and zaps.
- **`ChannelSprings.Settle`** runs after the pre-fill: batches of 60 steps until the stray count
  stops changing or the budget runs out, then one zap, then the soak mask (soak where the water is
  not kept). The scene's zone soaks films under 0.15 m at 0.05 m/s: gone in about 3 seconds.
  Anything the player floods is deeper than that and stays.
- **Steadiness is stray cells, not volume.** The first version watched total volume and never
  steadied: the springs feed the sea for as long as the island exists, so the volume climbs for
  ever. The play run of 2026-09-22 showed it — 840 steps, whole budget spent, "(budget)" — while
  the zap itself worked: 65.9 m³ of standing water off 2005 cells.
- 473 tests green.
- **The keep mask gained a one-cell margin.** A stream wanders a little outside the band the
  island marked its bed in, and with no margin the soak took that water. It did not, in the end,
  explain the creeks (below), but it is right on its own terms.
- **Play evidence (seed of the day):** the settle steadied in 180 steps over 0.35 s and took
  94.4 m³ of standing water off 3988 cells. A minute in, the water standing off the beds was 192
  cells / 22.9 m³ and still shrinking (161 cells / 20.3 m³ half a minute later) instead of
  spreading. The beds held: 46% of bed cells wet at t = 116 s, 44% at t = 154 s.
- **Open, for Ronan:** creeks run thin on this island — rivers are 66% of their bed cells wet
  (830 m³), creeks 27% (82 m³), and three short creeks read 0% in the River Capture. Turning the
  bed margin on changed none of those numbers, so this is not the soak: the creek springs have
  never kept their beds full (the flow-speed note of 2026-09-21 says as much). Tuning them is its
  own step.

## 2026-09-22 — Why the quarry stopped after one cut (open)

Two capture runs ended the same way: the crew took one step off the quarry, then stood idle with
"Idle: nothing it can do yet", 0.8 m³ moved and the pad untouched. Ronan read it as depth
("it appears they took top leevel off then stopped"), and he was right about where to look.

The ground under the quarry sites reads: topsoil 0.3 m, dirt 2.2 m, then stone. A robot Worker
cannot dig stone, and both captures set the quarry floor 2 m down — at or below the rock line. So
the crew dug what it could and then had nothing it was allowed to dig, with nothing on screen to
say so. Everything else checked out: the cells were dry, unclaimed, reachable, above their floor,
and the barrow had room.

Proposed to Ronan, not yet built:
- `QuarryLeft` counts only diggable ground — down to the floor or the rock, whichever comes first.
- Marking a quarry warns when its floor is in rock, with the volume that is actually available.
- A unit with a Fill waiting and a quarry dug out to rock says so instead of going idle.

## 2026-09-22 — Two machines: the digger and the hauler

Ronan brought two Meshy scans — an orbital excavator and a tipper walker, both in the crew robot's
family (cream shell, yellow band, cyan lens, four clawed legs) — and asked for them "finished
cleaned up and rigged in blender ... they need animations for the legs walking and scooping and
anything else they might need".

**Rulings (AskUserQuestion):** the machines stand **1.8 m to the top of the shell** (the crew robot
is 0.43 m and sets human scale, so a machine is about four robots high); **keep the baked look but
ease the rust**; and they need **walk, idle, turn in place, the digger's scoop and the hauler's
tip**.

**What the scans were:** one mesh each, about 10k triangles, one baked material with three JPEGs,
no rig, no parts — and roughly 750 disconnected shells apiece, 600 of them shards of a few
vertices.

**`Art/Tools/td_machines.py`** does the work in stages so a re-scan can be re-run rather than
hand-fixed:
- **clean:** weld at 2.5 mm (750 shells -> 121 and 110), drop shards under 20 vertices, dissolve
  degenerates, normals out, smooth shading, scale so the ball's top stands 1.8 m over the feet,
  centre it and stand it on z = 0. Digger 9959 triangles, hauler 9796.
- **skeleton:** turn the machine to look along +Y (Unity's +Z), then build the armature from
  joints measured off the cleaned mesh: body, four legs of upper, lower and foot, and then the
  digger's turret, boom, stick and bucket or the hauler's tray. 18 and 15 bones.
- **skin:** rigid weights, one bone per **welded shell**. A shell is a panel: it belongs to one
  part and turns with it.

**Three things bit, all recorded in the file:**
- `object.transform_apply` does nothing over the MCP bridge (no window, no selection), so the
  machines stayed at scale 3.2 and every measurement after it was in scan units. The transform is
  baked into the mesh by hand now.
- Nearest-bone weighting gave the body ball away to whichever leg or turret bone was closer, and
  the first posed frame tore the machine to pieces. The body claims its own shell first, by the
  ball's radius.
- Deciding per vertex split long panels: the hauler's tray came apart into slabs, half following
  the tray and half staying put. Weighting decides per shell now.
- **Clips (stage 4):** walk, idle and turn in place for both, the digger's scoop and the hauler's
  tip. Opposite legs swing together so two feet are always down; the body bobs 5 cm over a stride.
  Eight actions at 30 fps, kept by a fake user.
- **The tray rests level now.** The scan was made tipped up, which is a poor rest pose. The tray is
  measured by its **rim** — a hopper's floor is a V, and fitting that gave nonsense — and turned
  back 4.8 degrees about its hinge, leaving 0.7 degrees of slope.
- **Out to Unity (stage 5):** `digger.fbx` and `hauler.fbx` under `Art/Units`, with
  `Art/Units/Textures`, `Materials` and `Prefabs` beside them. Digger 9959 triangles at
  3.14 x 3.24 m, hauler 9796 at 2.42 x 2.85 m; four named, looping-where-they-should clips each.
- **The rust is eased** (`Art/Tools/td_ease_rust.py`): orange-brown, duller, darker pixels that
  differ from the paint around them are pulled 65% towards a median-filtered copy. About 4 to 5% of
  each colour map was rust. The raw maps are kept beside them as `*_raw.png`.
- **Export traps, all recorded in the tool:** `FBX_SCALE_NONE` brought the machines in at a
  hundredth of their size; embedding textures put Unity into an endless re-import loop; muted NLA
  strips gave Unity no takes at all; and "all actions" means *all* of them, so each machine is now
  exported from a scene of its own or the digger carries the hauler's walk.
- **Materials:** glTF packs occlusion, roughness and metallic into one map, so it is repacked once
  for URP (metallic in R, smoothness in A). The multipliers are pulled back to metal 0.12 and
  smoothness 0.35 — at full strength the shell read as chrome beside the terrain.

**Not done yet:** the machines are art and prefabs only. Nothing in the game spawns them, and the
`Digger` and `Hauler` roles still run with the crew robot's look and numbers.

## 2026-09-22 — The dumper, one machine at a time

Ronan pulled the first two machines ("lets remove them and do one at a time the animations are
poor") and brought a dumper that was already auto-rigged, asking to see a video of it moving. His
notes as it went: it is "a 4 legged crab like mech so it should walk like one and the dumper dumps
like a dump truck"; then "the walk looks backwards"; then "the dump goes the other direction it
should tilt backwards the eyes are the front"; then "the legs are missing joints".

The digger and hauler assets are deleted. `Art/Tools/td_dumper.py` does this one:

- **The scan:** 15 456 triangles, no UVs, no materials, a UniRig armature of 37 bones — a tray
  bone that was also the root, a body, four legs, one leg cut into two rival chains, a claw left
  hanging off the body, and a dozen one-centimetre stubs.
- **Facing:** the machine is turned 180 degrees on the way in, so it looks along +Y (Unity's +Z).
  Without it the gait pushed it out of its own back.
- **Every joint is kept.** The stubs are the hinge knuckles — two thousand vertices apiece on the
  back legs — and they are folded into the *nearest leg bone*, not their parent: folding them into
  the parent welded the knuckle to the shell, which is the missing hinge. The dangling claw is
  re-parented onto the end of its leg. Legs now drive three to five segments each.
- **Weights are hardened**: one bone a vertex. The auto-rigger blended them like flesh, so the
  tray came up at half the angle its bone turned.
- **The tray hinges at its rear bottom edge**, pointing forward, so tipping lifts the front and
  the load runs off over the tail — a dump truck. The rotation's sign is worked out by trying it
  and watching the tray's front, because the auto-rigger's bone axes are arbitrary.
- **Clips:** walk, idle, turn on the spot and tip, with the feet **planted** — a two-bone solve
  puts the ankle where it needs to be and everything past it is aimed at the contact point, so a
  foot on the ground stays on the ground while the machine rolls over it.
- Videos land in `Screenshots/Units`.
- **The dumper's clip set (2026-09-22).** Ronan's rulings: one turn clip, mirrored in code; videos
  longer and **not orbiting**. Ten clips: walk, idle, turn, tip, walk_loaded, idle_loaded,
  take_load, start, stop, stuck.
  - Loaded walk and idle carry a full bed: the shell sits 7 cm lower on its legs, the stride
    shortens to 0.32 m and the roll grows, so a loaded hauler reads as loaded across the site.
  - `take_load` is the bucketful landing: the machine drops, the bed shudders, it settles.
  - `start` and `stop` lean into and out of the stride, so it does not snap between standing and
    full pace.
  - `stuck` paws the ground and rocks — something to look at while the crew panel explains itself.
  - Videos write under their final name: ending the render path with `.mp4` stops Blender
    appending the frame range, which had been confusing the tidy-up afterwards.
- **Sizing the dumper (2026-09-22).** The crew robot is appended beside it in the blend, standing
  on the same floor, because it sets human scale. `build(height=...)` scales mesh and rest
  skeleton and then lays the clips with the same factor on every distance in them, so a smaller
  machine takes smaller steps. Tried at 1.0 m ("still feels too big") and now **0.7 m to the top
  of the shell**: 0.79 m over all, 0.74 by 0.95 m on the ground, about 1.7 robots tall, with a bed
  holding roughly 0.1 m³ — half a Worker's barrow.
- **Painted and delivered (2026-09-22).** No UVs and no maps came with the scan, so the machine
  wears flat materials in the crew robot's palette, read out of `crew_unit.blend`: cream shell,
  near-black trim and claws, cyan lenses, steel ram, pale bed. Colours are decided per welded
  piece — per polygon the band smeared across the hips and the eye was never found.
  - **Ronan's call left to me:** the band goes round the **bed's rim** rather than the ball's
    waist, because the game looks down on these machines and the bed hides the waist; and both of
    the scan's front ports are lit, which reads as a face.
  - `deliver()` exports `Art/Units/dumper.fbx` (2.1 MB, ten clips) with the crew rig's rules:
    bake_space_transform off, already facing +Y, FBX_SCALE_UNITS. Unity: generic rig,
    `bakeAxisConversion`, clips named and looped where they should be, six URP materials matching
    the palette, and `Art/Units/Prefabs/dumper.prefab`. It arrives 0.74 x 0.95 x 0.79 m.
  - **Spotted in passing:** the crew robot's own shell material is missing on its importer, so it
    renders magenta in the sandbox scene. Raised as its own task.

## 2026-09-22 — The digger, and a shared tool for the machines

Ronan brought the excavator: "same size as the dumper ... the bucket arm does not have the correct
rigging to work like a real excavator, it will walk and move like the dumptruck but you may have
to fix its legs as well". The scans now live in `Art/Blender~/Scans` under their own names.

- **`td_dumper.py` became `td_walker.py`**, which knows nothing about which machine it is working
  on: `load(name)` sets that, and everything else reads it. `td_digger.py` holds only what is
  different about the digger.
- **The arm is rebuilt from scratch.** The auto-rigger gave the whole arm one bone and some stubs
  at the bucket. An excavator needs four joints, so the arm's **spine** is measured — a graph over
  its own vertices walked out from the shoulder, since the arm is sixty-odd separate pieces — and
  turret, boom, stick and bucket are fitted to where that spine bends hardest.
- **Which way each joint digs is worked out, not typed in.** The bones are fitted to whatever the
  scan's geometry happened to be, so each joint is tried both ways and judged on what it does to
  the bucket: the boom must lower it, the stick push it out, the bucket curl it in.
- **Facing:** the digger is turned a quarter the other way from the dumper (-90 degrees), because
  an excavator faces the way it digs and this scan carries its arm along -X.
- **The legs were re-found**: the generic naming had the arm down as a fourth leg and folded a
  real leg into it. A chain counts as the arm when its *geometry* climbs above the shell — the arm
  bones themselves sit low.
- **Two traps worth remembering:** renaming a bone renames its vertex group, but deleting one
  leaves the group behind holding weights, and Unity then calls those vertices unweighted and pins
  them to the first bone (1347 of the digger's rode a leg). `settle_weights` folds any group with
  no bone into the nearest one. And reloading the tool resets which machine it thinks it is on, so
  video and export take the name from the rig in hand.
- Seven clips (walk, idle, turn, start, stop, stuck, dig), 11 315 triangles, 0.37 x 0.68 x 0.70 m —
  the same 0.7 m shell as the dumper — with the crew palette and a prefab.
- **Corrected the same day.** Ronan: "not correct at all ... the arm is three joints at bucket at
  next joint up and at end of last arm, it should not move side to side ... also the walking is
  distorting the main body like a blob".
  - **Three joints, no swing.** The turret is gone: boom, stick and bucket, each hinging across
    the machine so the arm works in its own plane. It turns its whole body to dump, the way a crab
    would.
  - **The arm is fitted by clustering, not by walking it.** Folded back on itself, a walk through
    the vertices jumps the fold and comes out in nonsense order — that is how the first fit ended
    with two joints bunched at the back and a bucket half the arm long. The three parts are found
    as clusters seeded where they must be (shoulder, the bend, the far tip), and each joint is the
    midpoint of the closest pair between neighbouring parts. The fit now puts them where the eye
    does: boom up, stick forward, bucket hanging at the front.
  - **The shell is one rigid piece.** `shell_to_body` gives the body's welded shell to the body
    bone outright; with a leg bone holding part of it the machine wobbled like a blob as it
    walked. Measured across a walk cycle the shell's bounds no longer change at all.


## Crew robot rendered magenta — the importer was not the cause (2026-09-22)

The magenta shell on the crew robot in TerrainSandbox was not a missing material remap on
`crew_unit.fbx`. The importer's external-object map already carried `CrewShell -> crew_shell.mat`,
and both the FBX and `crew_unit.prefab` resolve all seven slots (shell, trim, band, lens, iris,
highlight, pad) plus the separate `CrewGlow` renderer.

The null was a *scene* override. A stray root-level `crew_unit` instance in TerrainSandbox — a
direct instance of the FBX, not of the prefab, sitting at the origin — overrode
`m_Materials.Array.data[0]` with material guid `bf084033337db454da6493596bbbf57e`, which exists
nowhere in the project. A deleted material leaves the override behind as a null reference, and
Unity draws that as magenta. Reverting the property override on that instance drops it back to the
FBX's remapped `crew_shell`.

Two things this cost time on, worth remembering:

- **Read the live importer, not the guess.** `.meta` on disk and `GetExternalObjectMap()` agreed;
  the wrong slot number in the report sent the search to the importer instead of the scene. A scan
  for renderers whose `sharedMaterials` entry is null or whose shader is the internal error shader
  finds these in one pass, across scene and assets.
- **`ModelImporter.sourceMaterials` throws** in this Unity (6000.6.1f1) through the MCP run-command
  harness — the script dies with no logs. Enumerate `GetExternalObjectMap()` instead.

Also closed the one genuine importer gap while in there: `CrewGlow` had no remap, so the glow
renderer was relying on a per-instance override in both the prefab and the scene to reach
`crew_glow.mat`. It is now remapped on the importer like the other seven; the overrides are
redundant but harmless.

The stray `crew_unit` instance is still in the scene and TerrainSandbox has not been saved.

## 2026-09-22 — Digger arm: joints found by the pinch, hull locked before the arm

Ronan's excavator diagram (O1 boom pivot low on the body, O2 at the top of the fold, O3 at the
bucket pin, O4 at the teeth) did not match the fit, which was taking a fixed share of the limb's
length and giving the boom the whole folded arm.

The joints now come off the scan's own landmarks. O2 is the highest point of the arm; **O3 is the
pinch** — the thinnest slice across the descending limb, because a joint is a pin and this scan's
bucket pin narrows to 12 mm where the arm around it is 90 mm; O4 is the far end of the bucket
below the pin. The fit now reads like the diagram: boom 0.484 m off the hull at z 0.382, stick
0.267 m, bucket 0.089 m. The arm is three hinges on one axis, with no swing.

The underside was still deforming during the walk because `shell_to_body` — the pass that locks
the hull and everything tucked inside it to the body bone — was never in the digger's build. It is
now, run before the arm is rebuilt (the arm is a separate welded shell, well clear of the hull).
The belly is 100% body-weighted afterwards, against 14 vertices riding leg bones before.

Two traps fixed along the way: actions were being stamped `dumper|...` because reloading the
module resets `MACHINE`, so `_new_action` now takes the machine's name from the rig in hand; and
videos cut between three fixed angles (`cut_angles`) rather than orbiting, per Ronan's ruling.

## 2026-09-22 — Digger arm, third pass: the bucket was never in the arm

Ronan: "the bucket and arm are not correct". Colour-coding each bone's geometry and rendering it
turned out to be worth more than any amount of measuring: three faults showed up at once.

**The bucket was not part of the arm.** The arm was gathered as "everything standing above the
hull", and the scoop hangs *below* the top of the hull, so it was never in the arm at all — it
sat on whichever bone the settle pass folded it into, and could not curl. Which geometry is the
arm now comes from the scan's own rig (UniRig's arm chain), which also stops the antennae and the
boxes on the machine's back being flung about by the boom.

**The scan's bones are in the wrong place.** UniRig put its arm bones low on the body, below
everything they hold, so their joints are useless. Its vertex *groups* are the only thing worth
having from it; the joints are still measured.

**The scoop is welded into the same piece as the stick**, so no piece-level rule separates them.
The bucket is now cut at the pin, vertex by vertex, down the line of the stick — which is exactly
what a real bucket pivot does. Boom and stick stay piece-level, where welded parts must not be
torn apart. Fitted: boom 1.116 m from the mount on the hull's crown, stick 0.652 m, bucket
0.647 m, 601/520/409 vertices.

The dig cycle itself was rewritten off Cat's and SANY's operator guides rather than invented:
stick out about 40 degrees, bucket floor into the surface at about 45 degrees, the stick crowding
back to vertical while the bucket curls and the boom eases up to hold grade, bucket full by the
time the stick stands upright, and the bucket never opening between the fill and the dump.
Sources: https://www.cat.com/en_US/articles/for-owners/tips-for-digging-and-trenching-with-your-excavator.html
and https://www.sanyglobal.com/blog/how-to-operate-excavator/

## 2026-09-22 — Digger: parts of the bucket were on a front leg, and faces spanned the joints

Ronan: "still parts of the bucket that are not attached and maybe connected to front leg".
Measuring found 127 vertices of the scoop held by `leg_front_left_foot`, so the front foot walked
off with a piece of the bucket; and 37 faces spanning the bucket/stick join, stretching up to ten
times their rest length as the bucket curled — the green spikes across the scoop.

Both come from the same mistake: going by the scan's own groups, and by vertices. It is now done
by **faces, whole**:

* Every welded piece goes to whichever bone is actually nearest it — arm, leg or hull — so the
  scoop's rim cannot stay on a foot and two bumps of the shell cannot ride on the stick.
* The one piece that must be split is the stick-and-scoop, which the scan welds together; it is
  cut at the pin, which is what a real bucket pivot does. The bucket's own rams and links straddle
  that pin and go whole, or they end up stretched across the machine like spikes.
* Then the mesh is **cut open** along every join: each face belongs to one bone, and every edge
  between faces of different bones is split. A real machine has a seam at each joint. Claim, cut,
  claim again, until nothing needs cutting — cutting changes which piece is which.

Verified by measuring every edge's length against its rest length across the dig: zero edges
stretch by more than twice, at frames 26, 50, 62 and 80. Before: 37 to 62 of them, all
bucket-to-stick.

## 2026-09-22 — Digger: the arm's pivots were collapsing when the machine was resized

Marking each joint with a coloured ball and rendering it side-on showed the arm's pivots sitting
in mid-air beside the hull, not on the machine. The bones were built in the right places and then
moved: `stick` and `bucket` were **connected** bones, whose head is slaved to the parent's tail,
so `set_height` scaled each one again on top of its already-scaled parent. The stick's pivot
landed at 0.288 m instead of 0.700 and the bucket's at 0.180 instead of 0.438, and the arm swung
about points inside the hull — which is why the dig looked wrong however well the geometry was
divided up.

They are parented now but not connected. Each bone's head still sits exactly on its parent's tail;
it simply is not recomputed from it. Checked after scaling: boom 0.413, stick 0.700, bucket 0.438,
teeth 0.158, all as fitted.

Rendering for Ronan to draw on is orthographic and dead side-on, so anything marked on the image
maps straight back onto the rig.

## 2026-09-22 — Digger: the arm's joints are Ronan's marks, not a measurement

Ronan drew the three joints on a side-on orthographic render: the boom's pivot on the hull, the
top of the fold, and the bucket's pin. Read off that image they came out 74 mm higher, 84 mm lower
and **164 mm** lower than the fitted ones — the pin especially, which the pinch kept finding at the
top of the fold instead of at the scoop. They live in `JOINTS` as fractions of the machine's own
box, so they hold at whatever size it is built, and land within 0.3 mm of his marks.

Measuring for them was tried four ways over as many passes and never held. It is not a thing worth
measuring on a scan: the machine tells you which geometry is the arm, and a person tells you where
its joints are.

Two smaller rules came out of the same picture. A piece the scan does not call arm joins the arm
only if it stands **in front of the hull**, where nothing else reaches: the aerial box sits over
the hull and rode the boom otherwise, swinging about as the machine dug, while the scoop's loose
panels are well out in front and belong to the bucket.

## 2026-09-22 — Digger: a turret, and the arm carried while walking

Two rulings from Ronan (2026-09-22):

**The arm travels up.** "When walking his bucket and boom should be in up position." Every clip
that walks or stands — walk, idle, turn, start, stop, stuck — now holds the arm in a `TRAVEL`
pose: boom up 20 degrees, stick in 18, bucket shut 62. They only ever keyed legs and body before,
so the arm hung wherever the rest pose left it, dragging along the ground.

**The whole arm turns a full circle.** "The entire boom can turn 360 to dump into a vehicle or bot
near him without having to move." There is a `turret` bone now at the boom's pivot, standing up
out of the hull, holding no geometry: body → turret → boom → stick → bucket. This does not undo
the earlier ruling that the arm must not swing sideways — that is about its three hinges, which
still work in one plane. The machine slews on its turret, as a real excavator does on its house.

New `slew` clip: a full turn, feet planted, arm carried. Keyed a quarter at a time — one key from
nought to three hundred and sixty is no rotation at all to an interpolator, and anything past half
a turn goes back the short way. The dig uses the turret for its own swing as well: cut, lift,
swing 110 degrees, dump there, swing back.

## 2026-09-22 — Digger: the fittings had to come down, and the cut reaches farther

The turret turns a full circle and the machine's own aerial masts and boxes stood in its path: the
boom's underside passes about 56 mm above the hull's shoulder at their radius and they reached
68 mm higher, so the arm swept straight through them. They cannot turn with the arm (Ronan), so
`clear_the_swing` drops them instead — whole welded pieces, 2 to 7 cm, until nothing sits above
the boom's sweep. Six pieces moved; the worst overlap afterwards is nought. A machine that slews
does not carry masts in its own swing. **Ronan's ruling: keep them dropped.**

Also his: "the arm can reach out farther in its scoop". The cut now starts with the stick out
58 degrees rather than 40, and the boom 6 degrees lower, which is about a third more reach in
front of the machine.

On the box he thought was turning with the arm: in this rig it is not. Measured through the whole
slew, every vertex of it is on the body bone and moves nought millimetres. What he was looking at
was the stale FBX in Unity, from the first delivery, when the arm's weights were quite different.
Unity has the current one now: 8 clips, walk/idle/turn/stuck/slew looping, prefab rebuilt.

## 2026-09-22 — Scale: the digger was half a machine, and is now the bigger one

Lining the three units up side by side showed the digger was not the dumper's size at all. Both
read "0.70 m", but the digger's was measured over its folded arm: its hull stood 0.53 m and was
0.37 m wide against the dumper's 0.79 and 0.74 — half the machine.

`set_height` now takes the groups to ignore when measuring, so each machine is sized to the top of
its **hull**, ignoring whatever it carries above that: the dumper's raised bed, the digger's
folded arm. The digger's own fittings are dropped clear of the swing before it is sized, too —
they were the tallest thing on it, so sizing first measured masts that were about to come down.

**Ronan's ruling: the digger is slightly bigger than the dumper.** Its hull stands 0.86 m against
the dumper's 0.79, it is 0.59 m wide by 1.08 deep, and 1.12 m to the top of its arm when carried.
The crew robot is 0.41 m. Lineup: `Screenshots/Units/units_scale_lineup.png`.

## 2026-09-22 — Loads, and who cuts stone

Ronan's numbers (2026-09-22): three of the digger's scoops fill the dumper, and the starter robot
takes six barrow loads to do the same. They live in `UnitLoads` as one set rather than three
capacities in three places, because it is the ratio that matters and a ratio kept in three places
drifts: `Barrow = 0.19 m³`, `Scoop = 2 × Barrow`, `Bed = 6 × Barrow`. On the sandbox's half-metre
cells one cut is 0.125 m³ in place, so a barrow is about a cut and a half. The scene's old numbers
— a 5 m³ digger and a 20 m³ hauler — were left from when these were full-size machines, and are
corrected to 0.38 and 1.14.

**Stone.** The digger is built for it and cuts rock at the same rate as soil. The starter robot
can too, but slowly (Ronan's ruling: "can, but slowly"): `StepSeconds` charges a unit without a
cutter by the material's own hardness, `WorkInterval × (1 + hardness × StoneEffort)`, so rock
costs it about three and a half times a soil step and granite four and a half. Nothing hard-stops,
and no second table of numbers: the hardness was already on every material and unused until now.

A test grid has to use the sandbox's half-metre cells. At a metre, one cut is 1 m³ — more than any
unit in the game can lift — and a unit digs once, fills, and stalls with nowhere to tip.

## 2026-09-22 — The machines are in the game

`CrewView` had one body prefab for every unit, so a digger and a hauler were both drawn as the
crew robot. It takes one per role now — `_diggerPrefab`, `_haulerPrefab` — falling back to the
robot, and to a box when there is no model at all.

Each machine has an `AnimatorController` beside its FBX mapping the four state names the crew
logic asks for onto its own clips, because `CrewAnimation` crossfades by state name:

| | Idle | Move | Work | Carry |
| --- | --- | --- | --- | --- |
| digger | idle | walk | dig | walk |
| dumper | idle | walk | tip | walk_loaded |

Two things this pass taught. Materials must not be set by slot: the importer already maps each
machine's own materials onto the palette, and assigning them in order put the band colour over the
whole digger. And the machines start further apart than the crew does (`MachineSpacing`), because
every unit is one cell to the crew logic while a machine is over a metre long on half-metre cells,
so two a cell apart start inside one another.

`TinyDiggers/Units Capture` takes the picture from play:
`Screenshots/Units/units_in_game.png`.

## 2026-09-22 — Footprint: every unit carries the room it takes

Ronan: "footprint will be important", and of the three ways up, the keep-apart radius (2026-09-22).
Each unit has a `Radius` in cells and two of them keep the sum of theirs between their centres.
The crew's 0.35 reproduces exactly the 0.7 that was hard-coded before, so nothing about the crew
changes; the machines are set from their models — a digger is 2.2 cells long, so 1.1 either side.

**A role is what a unit does, not what it is.** Setting the size and the rock cutter from
`UnitRole.Digger` broke nine tests at a stroke: plenty of units dig without being the digger
machine, and sizing them all as machines jammed every crew in the game solid. A unit is crew-sized
and no rock cutter until something calls `AsMachine()`, and the thing that knows is whatever
spawns it — in the game, `CrewView`.

**A digger and its hauler have to nest.** Loading puts the bed under the scoop, and at the sum of
two machines' radii a hauler could never reach a cell beside its digger, so nothing would ever be
loaded — the suite missed this because those tests use crew-sized units. Partners keep
`JobDispatcher.LoadingGap` (three quarters of the larger radius) instead. Anyone else stays out.

Not done, and worth knowing: a machine is still one cell to the crew logic, so it can walk down a
one-cell trench and clip the walls. True multi-cell occupancy waits until something in the game
needs a machine kept out of tight ground.

## 2026-09-22 — Dirt moves: what it took, and what stopped it

`TinyDiggers/Dig Capture` marks a block, gives it a Dump Zone, lets the crew work and counts the
cubic metres. The first run moved nothing at all, and the reason was not the machines:

**Benching stalls a flat block after one ring.** A cell may only be cut to within a climb of its
Dig neighbours, so the rim comes down one step and then nothing can be reached from outside —
while the rule against standing on a Dig cell kept everybody out of the cut. Standing a bench
below the ground outside is *how* a cut is worked, so that rule now allows it, refusing only
where the ground outside stands more than a climb above: get in where you cannot climb out and
you are stuck (Ronan: "eventually it will need a way down ... at a point you can't reach").

Three rulings from Ronan went in alongside it:

* **Reach down.** `DigDepthLevels`, how far below its feet a unit cuts: two for a robot, four for
  the machine, which is its arm. A machine on the rim takes a three-step cut the robots cannot.
* **Dig into a face.** `FaceReachLevels`: the machine works a mound of any material from the foot
  of it rather than climbing on top. The old rule allowed that for rock only.
* **Reach out.** `WorkReachCells`: one cell for a robot, two for the machine — three was too far
  for the arm it has. Stands are searched out to that ring, so the machine can stand back.
* **Work a face, not a circle.** The planner took whichever cell came first and walked a unit
  round and round a block; the cut beside the last one now wins, so a unit eats into one side.

With those in, a 24 m³ block: **7 m³ moved** in one run, dug, loaded, hauled and tipped, the heap
standing 1.5 m proud. After the face rule it was 3.75 m³ in the same time — the digger fills its
scoop and waits, because one hauler cannot keep up with one digger. That is a balance question
(more haulers, or a bigger bed), not a fault.

The workers are unreachable throughout, which is right: the cut is three steps and their reach is
two. They need a ramp, and the auto-ramp says "nowhere to stand" on open ground — still to chase.

## 2026-09-22 — Why four workers stood in front of a job doing nothing

Measured in play: a worker could work **27 of the 60** remaining dig cells and still sat in
Unreachable. Not the ramp, not reach — a deadlock of its own.

A tip raises a cell by a whole step, so **a load smaller than one step cannot be put down
anywhere**. A unit whose scoop filled, tipped most of it and kept a remnant was left latched full
with 0.06 m³ against a 0.125 m³ step: too full to dig, too light to tip. It stood there for ever.
The latch now lets go for exactly that case — a load too small to tip tops itself up — and
otherwise holds, which is what stops a unit taking part cuts for ever without emptying. Clearing
it whenever there was simply room for another cut broke seven tests: that is the loop the latch
exists to prevent.

Standing in a cut is now a **machine's** privilege (`WorksFromInside`). A machine steps down a
bench at a time and can climb back out; a robot with a barrow keeps to the older rule — work a dig
area from outside, never from inside — and waits for a way down. Both of Ronan's readings hold,
and the rule the tests were written to survives.

## 2026-09-22 — Names, and who hauls

Ronan's names (2026-09-22): **starter robot**, **digger mech**, **dumper mech**. They live in
`UnitNames` and are what the crew panel and the readout show. The `UnitRole` values keep their own
names in the code, because a role says what a unit *does* — plenty of things dig without being the
digger mech.

And his rule: "the smallest ones have to dig and haul their own material, they can't dump into the
dump truck bot". That was already true, but only because two separate checks happened to agree —
`TryTransferToAdjacentHauler` is for diggers and `AssignDigger` only offers them. Two tests hold it
now: a dumper mech asked to serve a crew of starter robots finds nobody, and a starter robot with a
dumper mech standing by still walks its own barrow to the heap and never waits for one.

## 2026-09-22 — Work takes as long as the animation, and a bed leaves full

**Timings.** A cut used to be a flat 0.4 s tick whatever was swinging. `CrewUnit.DigSeconds` and
`TipSeconds` now hold what the job takes, and `CrewView` fills them in from the clips the body
actually plays (`TimeWorkToTheClips`), because the crew logic is plain C# and knows nothing about
Unity's animator — it is *told* how long the work looks. A body with no clips keeps the old
interval, which is why nothing in the tests moved. Rock still costs a unit without a cutter its
hardness on top of the swing. Throughput barely changed: 4.38 m³ against 3.75 the run before.

**A bed leaves full** (Ronan, 2026-09-22: "the dumper mech moves before he is all the way full, he
shouldn't go until full"). A parked hauler used to give up after `ParkPatience` with nothing tipped
in. The patience is on its *digger* now, not on the clock: while that one is still working there is
more coming, however long the cut takes, and it only gives up on a digger that has stopped. The
old patience test still holds, because a digger with nothing to dig is stopped.

Also seen in that run, and worth recording: with the part-load deadlock fixed, **the four starter
robots were digging** rather than standing about.

## 2026-09-22 — Topography: the ground sets the limit, said as an angle

Ronan chose the slope reading (2026-09-22): no per-unit climbing, **the ground decides and it
decides the same for everyone**, and anything steeper wants a ramp or a road.

`GridPathfinder.MaxSlope` and `MaxSlopeDegrees` are the knob now. They are the old
`MaxStepHeight` said in a way that does not depend on the map's resolution: half a metre of rise
across a half-metre cell is forty-five degrees whatever size the cells are, and the same angle on a
coarser grid allows a proportionally bigger step. `CrewView.maxSlopeDegrees` sets it, and nought
leaves the old metre-based field alone.

**Set to 45 degrees**, which is exactly what the sandbox was already doing (0.5 m over 0.5 m
cells) — the number was simply never expressed as an angle. Checked in play: the crew works as
before, robots digging, mech loading, dumper tipping.

**35 degrees strands this island.** Every unit, including the digger mech, reports UNREACHABLE and
the ramp planner finds no route: the spawn itself lands somewhere boxed in, and the crew never
reaches a site at all. Steeper ground can be made to matter, but the roads and ramps have to be
able to answer it first, and today's auto-ramp cannot. The knob is there for when they can.

Worth knowing for whoever tunes it: `JobDispatcher.Climb` — how deep a bench may be cut — is
derived from the same limit and is floored at one height step, so below 45 degrees a crew can cut
a bench it then cannot walk onto.

## 2026-09-22 — A bed tips in one, and loads are whole steps

Ronan: "the dumper mech should dump entire load in one tip". A tip was capped at what the unit
could heap within a climb, so a full bed went down nine times over. `TipsWholeLoad` — true for the
dumper mech, false for a robot with a barrow, which places its load rather than dropping it — puts
the lot down in one go, held back only by a fill's cap. The heap it makes slumps to its angle of
repose like any other spoil, which is what a real tipper leaves behind.

That turned up a quiet mismatch. The ground only takes material a **whole step at a time**
(`Excavation.Tip` floors to steps), so a bed of 1.14 m³ was 9.12 steps: it tipped nine and kept
0.015 m³ for ever, then reported nowhere to tip. The loads are whole steps now — the barrow is a
step and a half (0.1875 m³) rather than a round 0.19, which keeps Ronan's ratios exactly (scoop
two barrows, bed six) and makes them three steps and nine. Nothing is left in the bed.

## 2026-09-22 — A road, built

`TinyDiggers/Road Capture` lays a road between two points near the crew, gives them somewhere to
tip and something to fill with, and watches them build it.

Twenty cells at width 3 came to **24 designations**, all of them cleared in 18 seconds of work at
twenty times: 2.5 m of height moved along the line and **62 cells of road surface** laid. The
dumper mech was driving on the finished road at the end of it.

The first run was the more interesting one. With no quarry the crew stopped dead and the banner
said "5 units have nothing to fill with: mark a Quarry" — a road across a dip is mostly *fill*,
and fill has to come from somewhere. That is the Slice 17 loop doing exactly what it was built to
do, and the capture marks a quarry now so the road actually goes in.

## 2026-09-22 — Site trials: a harness that ends on progress, not on a stopwatch

`TinyDiggers/Site Trials` (and one menu item per trial) runs the crew against the live island and
reports numbers rather than impressions.

The first trial A run looked like a failure: "36 designations left, 0 auto ramp". It was not. Every
robot was still digging, a spoil heap had grown beside the cut, and the 120-second budget had
simply run out. The harness now ends a trial on **progress**, not on a clock: `Work` measures the
cubic metres outstanding across every live designation and stops on done, on stalled (25 real
seconds with no measurable progress), or on a 300-second cap, and says in the log which it was.
A count of cells is no use as a progress measure — a deep cell is worked step by step and the count
does not move for minutes.

### The real finding: excavation throughput is far too low

Trial A, rerun with the honest harness: a 7x7 pit three metres deep, **36.75 m³**, six units, a
dump zone five cells away.

| real s at 20x | m³ outstanding |
| --- | --- |
| 31 | 34.38 |
| 61 | 33.13 |
| 91 | 32.13 |
| 121 | 30.88 |
| 152 | 29.38 |

That is a flat **1.25 m³ per 30 real seconds — 600 game seconds — or about 0.002 m³ per game
second for the whole crew.** It never stalls; it crawls. The pit would take some fifteen minutes of
real time at twenty times speed, five game hours, for a hole three and a half metres square.

Back of the envelope for what it should be: a worker moves at 1.5 m/s, its barrow holds 0.1875 m³
(a step and a half), the haul is about five metres each way, and a step of digging costs the length
of the dig clip. That is roughly twenty game seconds a barrow, so four robots should shift about
0.03 m³/s — **fifteen to twenty times what is actually happening.** So the crew is working
something like five per cent of the time, and the rest is going somewhere else: waiting on each
other, unreachable, or re-planning.

Next: instrument the harness with a per-unit state histogram in game seconds, so the lost time can
be named instead of guessed at. Note also that 20x is the practical ceiling for these runs —
`Time.maximumDeltaTime` is a third of a second, so at sixty frames a second the clock cannot go
faster without breaking the movement stepping.

### Correction: the crew was not slow, the clock was

The "throughput is fifteen times too low" finding above is wrong, and the way it was wrong is
worth keeping. The harness budgeted and measured in **real** seconds and assumed `timeScale = 20`
meant twenty game seconds a second. It does not. An editor that has lost focus ticks at a few
frames a second, and `timeScale` multiplies each *frame*, not each second — so the game clock was
running at some small fraction of what the wall clock implied, and every rate computed from it was
out by that factor.

Two things came of it. The harness now budgets, stalls, traces and reports in `Time.time` — game
seconds — and quotes real seconds only as an aside; and the runner sets
`Application.runInBackground = true` so play mode keeps ticking while the editor is behind
another window. The per-unit state histogram stays: knowing where the crew's time actually goes is
worth having whatever the clock says.

Numbers worth keeping from the run itself: the trial site is **Topsoil**, a starter robot pays
**0.4 s a step** (its `WorkInterval` — it has no clip named `dig`, so nothing is timing it to an
animation yet, which is still open against Ronan's "every dig needs a matching animation"), and
the digger mech pays **2.97 s a step**, timed to its dig clip.

## 2026-09-22 — The deep pit, and two faults it turned up

Trial A in play mode said the same thing every run: a seven by seven pit three metres deep stalls
with four fifths of it still standing, every unit reading *Digging*, and **not one auto ramp cut**.
Instrumenting the crew (`CrewUnit.LandedCuts`, `RefusedCuts`, `OffStand`, `DigTicks`, reported by
the trial harness) gave the shape of it: 70% of the crew's time in Digging, 6908 unit-seconds of
it, **11,284 "cuts" against 7 m³ moved** — 57 real cuts' worth. Almost every cut was digging
nothing at all.

### Play mode is the wrong place for a behaviour trial

Before any of that could be read, the runs had to be believable. An editor behind another window
does not tick: `EditorApplication.timeSinceStartup` moved 33 seconds across 19 minutes of wall
clock, and the game clock only advanced while the MCP bridge poked it. A twenty-minute run said
less than a one-second test would.

So the battery moves to EditMode tests (`Assets/TinyDiggers/Units/Tests/DeepPitTests.cs`): the same
site built out of plain objects — grid, designations, dispatcher, units — and ticked as fast as the
processor likes. It answers in a second, answers the same every time, and stays as a regression
test. `TinyDiggers/Site Trials` keeps its place for pictures of the live island, and now budgets in
game seconds with `Application.runInBackground` set, but it is no longer where a question gets
answered.

Two things the fixture taught, both of which had quietly made earlier runs meaningless: a
`JobDispatcher` built by hand has **`Benching` and `AutoRamp` off**, which the scene turns on every
frame, and a `CrewUnit` built by hand reaches **half as far** as one in the scene (class defaults
`DigReachLevels = 2`, `CliffReachLevels = 6`; the scene sets 2 m and 6 m over a half-metre step, so
4 and 12). A fixture that skips either is testing a different crew.

### Fault one: the cut that does not fit, tried for ever

A load's room is loose m³; a cut is measured in place and comes up bulked — 1.25 for dirt, 1.5 for
rock. `ChooseDiggerJob` asked "is there a step of room?", which is the wrong question: a barrow can
have room for a step and still not take one cell's worth. `Excavation.Dig` then skips the cell
rather than part-digging it, the unit thinks again, plans the same cut, walks back, waits out its
interval and digs nothing — for ever, looking busy the whole time.

Fixed by believing the ground: when a cut is refused for want of room, `DigReport.SmallestMisfit`
says exactly how much it wanted, and `CrewUnit.NeedsRoom` holds that figure until a cut lands. The
fullness test asks for `max(step, NeedsRoom)`.

### Fault two: the barrow was too small to be emptied

With that fixed the crew got further and stopped again — eight cuts between four robots, two each.
The arithmetic: a tip puts down one whole step of a cell, 0.125 m³ of the load; a cut of dirt is
0.15625. Dig, tip a step, dig, tip a step, and the robot is left holding 0.0625 — too little to tip
(nothing less than a step lands) and too little room left for another cut (0.125 of room against
0.15625 needed). Too full to dig, too light to tip, standing in front of a job it could do.

The old barrow was a step and a half. **A load has to hold at least one step plus one cut**: the
remnant is under a step and the worst cut is one and a half, so two and a half steps is the floor
and three is the round number above it. `UnitLoads` now reads Barrow = 3 steps, Scoop = 2 barrows,
Bed = 6 barrows — Ronan's ratios exactly as before, twice the size. The reasoning is in the file so
nobody shaves it back.

That doubled the crew's reach into the block: eight cuts became twenty-four, the whole outer ring.

### Still open: a pit is not a hill

Where it stops now is a genuine design gap, and there is a failing test named after it
(`DeepPitTests.AShallowPitIsDug`, ignored with the reason on it). The outer ring of a designated
block gets cut; the inner twenty-five cells report **UNREACHABLE**, and the ramp planner answers
**"nowhere to stand beside"**.

The rule doing it is in `CrewUnit.CanStandHere`: with benching on, a unit may stand on a dig cell
only if that cell is already at its floor, so that it cannot dig the ground out from under itself.
Taking a hill down that is exactly right — the staircase stays drivable. In a pit it locks the
door: to reach the middle of a block you have to stand on cells that are still to be dug, and they
never qualify until something has already stood there.

What a real crew does is work a face: it stands *in* the cut on the level it has finished and takes
the next lift off in front of it, and the thing it must not do is dig a hole round itself. That is
a rule about the cell it is standing on and its neighbours, not about whether the cell it is
standing on is at its final floor. That is the next piece of work, and it is what the auto ramp has
been waiting for: no ramp is ever cut because no unit is ever short of a *job*, only short of a way
to the middle.

## 2026-09-22 — A bench is not a step

The deep-pit fault above had a single line behind it, and the fast tests found it in a minute once
each refusal said why: **"(9, 13) is at its bench floor: 5 m would be under 5.5 m"**.

`JobDispatcher.DigFloor` held a soil cell within **one climbable step** of its highest
dig-designated neighbour. On a half-metre grid at forty-five degrees a climb *is* one step —
`GridPathfinder` derives `MaxStepHeight` from the slope, so setting 45° turns a metre of step into
half a metre — so every cell in a marked block was pinned within half a metre of every other one.
A flat block therefore came down in lock-step: the outer ring was cut once, could go no further
because the ground inside it was higher, and the middle could not be worked from anywhere a unit
was allowed to stand. Four robots, twenty-four cuts, stop.

Benching is meant to keep a hill coming down in **benches** a unit can work a face off, not to hold
a block flat. So the depth is now its own number, `JobDispatcher.BenchDepth`, a metre — two steps —
and rock keeps its own deeper `CliffWorkDepth`.

Measured, on the two-step pit in `DeepPitTests`:

| | before | after |
| --- | --- | --- |
| cells cleared of forty-nine | 0 | **16** |
| cuts landed | 23 | **32** |
| cuts refused on arrival | 23 | **0** |
| rim height, wanted 5 m | 5.5 m | **5 m** |

Suite green at 512 throughout.

### What stops it now, and it is two things

The ring is a metre below the ground outside — more than a climb — so nothing can drive in to work
the next ring. That is the state the auto ramp exists for, and **no ramp is ever cut**. Two reasons,
both worth their own piece of work:

1. **A unit only asks for a ramp when it has nothing else to do.** `ChooseDiggerJob` reaches
   `RequestRamp` after `TryPlan(Dig)` fails, and a crew with spoil in its barrows always has
   something else to do: it shuttles to the tip and back. Work it cannot reach ought to be able to
   ask for a way in while the crew is still busy.
2. **Units wait on each other for ever.** Three of the four finish the run saying "Waiting for a
   unit at (14, 21)" near the dump zone, and they are still saying it four hundred seconds later.
   There is no way out of `Waiting` when the unit ahead is itself waiting.

### Correction: they are not waiting on each other

"Units wait on each other for ever" was a misreading of a snapshot. A stuck timer that could not be
reset by re-planning was written, the suite stayed green at 512, and the pit trial came out
**byte for byte identical** — same four cubic metres, same thirty-two cuts, same thirty-three cells
left. So it fixed nothing, and it went back out again rather than sitting in the crew logic as an
unproven change. The roll that suggested it had one unit on its way to the tip, which is a crew
going round in circles, not a crew jammed.

What is actually happening is one thing, not two: the ring is a metre below the ground outside, so
nothing can drive in to work the next ring, and **no ramp is ever asked for**. `ChooseDiggerJob`
only reaches `RequestRamp` after `TryPlan(Dig)` fails, and a crew carrying spoil always has
somewhere else to be. That is the single next piece of work.

### Asking for a ramp while still busy, and what that showed

`ChooseJob` now asks for a ramp whenever there is work it cannot reach, not only when it has
nothing else to plan. That is right on its own terms — a crew shuttling spoil should still be
cutting itself a way in — and it immediately turned up a latent crash: `PlaceRampStep` indexed
`_corridor[Count - 2]` without checking there were two, which only happened once a unit could ask
about a cell it was standing on. Guarded, suite green at 512.

It did not, though, get a ramp cut, and the reason is worth writing down because it is the real
shape of the problem:

**The ramp planner reasons about height steps. What blocks the crew here is not a step.** The
corridor it plans from a unit to an unreachable cell runs straight across the top of the block —
the undug middle is at the same height as the ground outside, so there is no step anywhere along it
— and the planner, finding nothing too steep to fix, answers "nowhere to stand beside". What
actually stops the unit is the rule that it may not *stand* on ground that is still to be dug, and
the corridor search knows nothing about that: its passability test is about designations and
regions, not about where a unit may put its feet.

So the next piece of work is to make the corridor honest: a cell the unit may not stand on is not
passable, which forces the corridor round to the ring it has already cut, where there *is* a metre
step to take down — and that is the ramp.

## 2026-09-22 — A ramp is aimed at somewhere to stand, not at the work

`PlaceRampStep` planned its corridor to the cell that could not be reached and looked along it for
the first step too steep to drive. In a pit there is no such step: the undug middle stands level
with the ground outside, so the corridor runs straight over the top of the block and the planner
answers "nowhere to stand beside". The way in is barred at the edge of what has *already* been cut
— which is where a unit would have to put its feet.

So a ramp now aims at a **stand**: `CrewUnit.TryFindStand` is `CanWorkFromSomewhereReachable`
with the "can drive to it" part taken out, returning the nearest place the unit could work the cell
from if only it could get there. Where there is no such place yet, the work itself is still the
guess.

The two-step pit, four robots, 12.25 m³:

| | before the bench fix | after it | with the ramp aimed at a stand |
| --- | --- | --- | --- |
| m³ moved | 2.88 | 4 | **9.75** |
| cells left of 49 | 49 | 33 | **12** |
| cuts landed | 23 | 32 | **79** |
| middle of the pit | 6 m | 6 m | **5 m** (what it was asked for) |

`AutoRampTests.TheRampCorridorClimbsMonotonicallyOnceCut` asserted the corridor ended exactly on
the target; it now ends on the target or beside it, which is what the test was really about.

### What is left: the last twelve cells

All four robots finish in pairs by the dump zone saying "Waiting for a unit", and are still saying
it six hundred seconds later. A stuck timer — a traffic wait that re-planning could not reset —
was written **twice** and changed the result not one byte either time, so whatever holds them is
not the wait in `CrewUnit.Move`. That is where to pick it up: find out what a unit in that state is
actually doing each tick before writing another fix for it.

## 2026-09-22 — The heap walls the tip in (trial E, answered by accident)

The last twelve cells of the pit are not a traffic bug. Six ticks of every unit's state, position
and load — `DeepPitTests.TraceTicks`, which is why it exists — show two pairs of robots shuffling
back and forth a tenth of a cell at a time at the mouth of the dump zone, one going in with a load
and one coming out for the next cut, each resetting the other's wait. That is a livelock, not a
deadlock, which is why two goes at a stuck timer changed nothing: the units *do* move, just not
anywhere.

What makes a one-cell mouth out of an open plain is the spoil:

> **the heap: 2.5 m at its highest, 0.56 m mean over the tip and its edges, from 9.75 m³ tipped.**

Nine and three quarter cubic metres spread over the forty-nine cells of the dump zone would stand
**0.8 m**. It stands **2.5 m** — five height steps, where a climb is one — because tipping keeps
picking the same few cells: it fills the lowest cell in reach, and from on top of the growing heap
it may only tip level or higher, so it walks up its own pile and drops another load on the summit.
The zone ends up a tower with a moat of untouched cells round it, and the only way on or off is the
one cell the crew came up.

So this is Ronan's trial E answered sideways, and it is a design question, not a pathing one: **a
tip should build out before it builds up.** Spoil that slumps to its angle of repose is already in
the model; what is missing is a preference for the *flattest* reachable cell over the lowest one,
and a cap that keeps a heap within a climb of the ground round it until the zone's floor is full.

That is the next piece of work, and it is worth more than the twelve cells it unblocks: it is what
a tipped load is supposed to look like.

### Build out before up: the heap comes down from 2.5 m to 1 m

A tip is capped a climb above **where the unit stands**, which is the right cap and the wrong
reference: the unit walks up onto its own heap and the cap goes up with it. A tip now also has to
land within a climb of the **floor of the zone** (`CrewUnit.ZoneFloor`, the lowest cell of any dump
zone that is not itself designated), so the floor fills before anything rises.

Measured on the same trial: **2.5 m tall becomes 1 m**, mean over the tip and its edges 0.56 m
becomes 0.38 m. That is what a tipped load should look like, and it is trial E's answer.

Honest note on the cost: the same run moved **6.88 m³ instead of 9.75**, with 23 cells left rather
than 12. The crew walks further to reach the bottom of the heap, and the jam that stops the trial is
still there. So this is kept for the heap, not for the throughput.

And the jam is *not* the heap, which is the second guess in a row about it to be wrong. The units
end up round (21, 20) to (22, 23) — **south of the dump zone, not on it** — so whatever holds them
is on the way between the pit and the tip, not on the spoil. The next person to look at this should
start by tracing those four cells rather than believing either of the last two explanations.

### Found it: four units in a two-by-two, each blocked by the other's radius

With the status line in the trace, the jam is plain:

```
[0 at (22.45, 22.55) cell (22, 22) :: Waiting for a unit at (21, 23)]
[2 at (21.77, 22.77) cell (21, 22) :: Waiting for a unit at (22, 23)]
[1 at (21.80, 21.20) cell (21, 21) :: Waiting for a unit at (22, 20)]
[3 at (22.47, 21.47) cell (22, 21) :: Waiting for a unit at (21, 20)]
```

Four units packed into a two-by-two block, each wanting to step diagonally out of it — and **every
one of the four cells they name is empty.** Nobody is standing on (21, 23), (22, 23), (22, 20) or
(21, 20). So the message is wrong about its own cause: what stops them is the other half of the
test, `JobDispatcher.CanMoveTo(..., Radius)`. A crew robot's radius is 0.35, so two of them need
0.7 between their centres; units 0 and 2 are **0.68** apart. Each one's probe clips the other, each
gives way, and none of them ever gets out.

The wait does run out and they do re-plan — and re-form the same square. A livelock, and its engine
is the keep-apart radius (Ronan, 2026-09-22: "footprint will be important"), not the traffic rule
and not the heap. That is the third explanation for these twelve cells and the first with evidence
that names itself.

Two ways out, for whoever picks it up:

1. **Say the right thing.** A unit blocked by a radius rather than by an occupied cell should say
   so. Half the trouble here was a status line confidently naming an empty cell.
2. **Let a unit move apart.** The radius check refuses any move that would put two units too close;
   it should allow a move that *increases* the distance between them, which is exactly what all
   four of these are trying to do. A cluster would then unwind itself in one step.

## 2026-09-22 — What a unit should carry, and the metric to set it by

Ronan: *"we should also figure out the best amount of material to dig and carry for each unit that
will make gameplay fun but not too fast, and we can use as a metric to scale up when we add bigger
units."*

`Assets/TinyDiggers/Units/Tests/LoadSizingTests.cs` is the bench for it: one unit, a strip of face
it can always reach, a tip a measured distance away, ten game minutes. It prints **cubic metres a
game minute at a given haul** and where the time went, every test run.

The first cut of it measured the site instead of the unit — a five by five block two metres deep
read "Unreachable 82%", which is true of the block and says nothing about a barrow. A strip three
cells wide fixes it: every cell of it is next to open ground.

| unit | load | 3 m haul | 10 m haul | digging | moving |
| --- | --- | --- | --- | --- | --- |
| starter robot | 3 cuts (0.375 m³) | 0.80 m³/min | 0.45 m³/min | 4–8% | 86–93% |
| digger mech | 6 cuts (0.75 m³) | 1.48 m³/min | 0.91 m³/min | 8–14% | 75–85% |

Both paying the same 0.4 s a cut, since nothing in a test times a unit to an animation clip.

### What the numbers say

**The load is not what makes it feel wrong. The cut time is.** A unit spends between a twenty-fifth
and a seventh of its life digging and nearly all the rest walking. Whatever the barrow holds, the
crew reads as commuters rather than diggers. Those are two separate knobs and they should be turned
for two separate reasons:

- **Load size sets throughput.** Roughly linear at a fixed haul: double the load, halve the trips.
- **Cut time sets the feel.** It is the whole of the dig/haul split and barely touches throughput
  while digging is under a tenth of the cycle.

So: keep the loads where they are — 1 : 2 : 6, barrow : scoop : bed, which is Ronan's own ruling and
holds together — and buy "fun but not too fast" with the cut time. For a robot's digging to be a
third of its cycle at a 3 m haul it wants about **a second a cut** rather than 0.4. That is also
what pays off "every dig needs a matching animation the time to scoop and load truck": the digger
mech's dig clip is already 2.97 s, and the starter robot has no clip named `dig` at all, which is
why it falls back to the interval.

### The ladder, and what is still missing

The mech is only **twice** the robot here, which is a thin step up for a machine. But this bench
measures it **hauling its own spoil**, which is the one thing it is not meant to do: a digger mech
is supposed to stand at the face and load a parked dumper, never walking at all. Its real advantage
is reach and not hauling, and neither shows up in a solo measurement.

So the number that should set the ladder is **a digger and dumper working as a pair**, and that is
the next thing to measure. Expect the step up to come from the pair, not from the scoop.

For a new unit: put it on the bench, read its m³ a game minute at 10 m, and set its cut time so
digging is a third of its cycle. One number to place it, one to make it feel right.

### The pair is the step up, and it fixes the feel by itself

Measuring the digger mech **with its dumper**, which is the way it is meant to work, settles it. The
bench also stops when the face runs out now rather than when the clock does — the pair cleared the
whole strip inside the window, so the first pair figure was a floor, not a rate.

| | load | m³ a game minute at 10 m | digging |
| --- | --- | --- | --- |
| starter robot | 3 cuts | 0.45 | 4% |
| digger mech, on its own | 6 cuts | 0.91 | 8% |
| **digger mech + dumper** | 6 + 18 cuts | **5.76** | **41%** |

The pair cleared 15.75 m³ in 164 game seconds. That is **six times** the mech working alone and
**thirteen times** a starter robot — where the scoop on its own is only twice the barrow.

Two things follow, and both answer Ronan's question without touching a load.

**The ladder comes from pairing, not from load size.** Ronan's 1 : 2 : 6 holds; what makes a machine
feel like a machine is that it never walks. A future tier should be placed the same way — measured
as a working pair — and to feel like the same size of jump again it wants to land somewhere near
20–25 m³ a game minute.

**The dig/haul split fixes itself.** A solo unit digs between a twenty-fifth and a seventh of its
life and reads as a commuter. A paired mech digs **41%** of the time. That is the shape worth having
— you watch it dig rather than watch it walk — and it arrives from the design rather than from
tuning a number. So the earlier recommendation to slow the cut down stands only for the **starter
robot**, which really does haul its own barrow and really is a commuter: about a second a cut rather
than 0.4, and a clip named `dig` so something times it.

**And a machine needs room.** At a 3 m haul the pair manages 0.775 m³ a minute — *worse than the
mech alone* — because the two of them get in each other's way: "Full: waiting for a hauler" beside
"Waiting for a unit". Same congestion family as the pit's last twelve cells. A short haul should be
the easy case, and it is the worst one.

## 2026-09-22 — "Moved" means got somewhere, not twitched

The short-haul pair — the case that should be easiest and was worst — traced in four lines:

```
mech   WaitingForHauler load 0.688/0.75 :: Full: waiting for a hauler
dumper Waiting          load 2.25/2.25  :: Waiting for a unit at (13, 36)   apart 0.87
dumper Moving                                                               apart 0.87
dumper Waiting          :: Waiting for a unit at (13, 36)                   apart 0.87
```

A **full** dumper, trying to leave for the tip, sat 0.87 cells from its mech while the mech waited
for it to come and be loaded. Neither is wrong about what it wants; the dumper simply cannot get
out. Going round a partner starts by closing the gap before it opens, and `CanMoveTo` only ever
allows opening it. The pair is just outside the nesting gap (0.825), so the "already nested" case
does not save them either.

The escape hatch — a unit that has stood still long enough stops applying the radius for one step —
was already in and did not fire, because the dumper was let forward **seven hundredths of a cell**
and sent back, over and over, and any reset on bare movement handed it a fresh timer every time.
So "moved" now means **a quarter of a cell**, not any change at all.

| | before | after |
| --- | --- | --- |
| pair, 3 m haul | 0.775 m³/min, never finished | **7.998 m³/min, done in 118 s** |
| the mech's share of time spent digging | 6% | **58%** |

A short haul is now the best case rather than the worst, which is what it always should have been,
and the mech digs for more than half its life.

### And the pit cut its first ramp

Same run, the two-step pit: **"1 auto ramp … cutting ramp at (14, 13) toward (12, 13)"**. That is
the whole chain working for the first time — a bench deep enough to cut to, a ramp aimed at
somewhere to stand, asked for while the crew was still busy, by units that can actually move. It
still does not finish (6 m³ of 12.25, and one robot reports "no room it can reach in any Dump
Zone", which is reachability rather than a full zone), but nothing is standing still any more.

### The tip was never the problem

Tracing the dump zone rather than guessing at it:

> **Tip: 49 open cells of 49, 6–7 m, 0 of them more than a climb (0.5 m) above every neighbour.**

Every cell of the zone is open, the heap terraces properly, and nothing on it is stranded. So
"no room it can reach in any Dump Zone" is not about room at all — the unit saying it is **down in
the cut**, under a face a metre high, and from where it stands there is genuinely nowhere it can
get to. That is the state the ramp being cut in the same run exists to fix; the trial simply ends
before the ramp is finished.

Two things to do with that, neither urgent:

- **The message is misleading.** A unit that cannot reach a tip because it cannot get out of the
  hole should say so — "stranded: waiting for a way out" — not blame the dump zone. The same is
  true of "waiting for a unit" naming an empty cell. Status lines that name the wrong cause have
  cost this session more time than any of the actual faults.
- **The trial stops too early.** It gives up 400 game seconds after the last cubic metre moves, and
  cutting a ramp takes longer than that while nothing else can progress. Waiting on a ramp is not
  the same as being stuck.

### A trap worth remembering

`TESTS PASS passed=518` was printed **three times** while the test assembly had not compiled —
a name clash (`Tip` the field against `Tip()` the new method) meant Unity kept running the previous
assembly, and the new trace simply never appeared in the log. A green line is not proof the code
under it ran. After editing a test, check for `error CS` in the log before believing the result.

### Saying the right thing, and what that uncovered

Two status lines that named the wrong cause, both fixed:

- **"No room it can reach in any Dump Zone"** covered two different troubles. `AnyRoomInZones`
  now tells them apart: *"no room left in any Dump Zone"* only when the zones really are at their
  caps, and *"cannot reach anywhere to tip from where it stands"* when there is room and the unit
  simply cannot get to it. They want opposite things from the player — another tip, or a ramp.
  (`CrewTests.AFullDumpZoneIsReportedAndTheUnitStops` caught the first attempt, which had thrown
  the true case away with the false one.)
- **"Waiting for a unit at (21, 23)"** was printed about cells nobody stood on. A unit held up by
  another one's body rather than by an occupied cell now says *"Squeezing past a unit"*.

The trial also stops calling a ramp a stall — but only for twice its patience, because a ramp still
"being cut" after that long is not being cut. That is exactly what it found:

> **`[0: Moving to ramp (14, 13) to 5.5 m, toward (11, 14)]` — for 1337 game seconds.**

The ramp is planned, the step is designated, and the unit that should cut it never arrives. That is
the next fault, and it is a clean one: a single unit with a single job that it does not finish.

### The ramp is never cut because the unit never gets there

`CrewUnit.PathLeft` / `PathLength` in the trace, and the ramp fault is down to one line:

```
tick 5: [0 Moving Dig at (15.50, 21.39) cell (15, 21) job (14, 13) from (15, 14)
         load 0.25 waited 0.0 path 7/8 :: Moving to ramp (14, 13) to 5.5 m]
```

Eight waypoints, **seven still ahead**, `waited 0.0` — so it is not blocked by anything — and its
position alternates between z 21.46 and 21.39 tick after tick. It has a good path to the ramp and
walks the first hundredth of it for ever.

A path that never advances past its first waypoint is a path being **re-planned every tick**:
`Replan` puts `_pathIndex` back and the unit starts again from where it stands. What sets `_repath`
that often is the thing to find — the suspects are the designation `Changed` event (the ramp step
being re-placed each rethink) and `OnCellChanged`, neither of which should be firing on a site
where nothing is being dug.

Next session: log `_repath` and `_pathIndex` alongside the path length. One run should name it.

### Confirmed: the unit is told to start again every tick

```
tick 5: [0 Moving at (15.50, 21.39) cell (15, 21) job (14, 13) from (15, 14)
         waited 0.0 path 1/8 RETHINK :: Moving to ramp (14, 13) to 5.5 m]
```

Not `_repath` — **`_rethink`**, set on every single tick. So the unit re-chooses its job every
tick, `TryPlan` builds the same eight-waypoint path to the ramp, `_pathIndex` goes back to nought,
it walks one waypoint, and starts again. `path 1/8` for ever, at a hundredth of a cell a time, with
nothing blocking it.

That is the whole of the ramp fault, and it is very likely the whole of the "congestion" that was
chased three times and blamed on the traffic rule, the heap and the keep-apart radius in turn: **a
unit whose job is re-chosen faster than it can walk looks exactly like a unit that is stuck.**

What sets `_rethink` that often is the last question. `OnCellChanged` should be quiet — nothing is
being dug on that site — which leaves `OnDesignationChanged`, and the likeliest churn is the auto
ramp step being cleared by `UpdateRamp` and re-placed by `RequestRamp` in the same tick, now that
every digger asks for a ramp on every rethink.

The fix is probably not to stop the churn but to stop it mattering: **a unit should not throw away
a path it is walking because a designation changed somewhere else.** A rethink belongs when the
change touches this unit's own target, its stand or its path — not when anything anywhere moves.
That is a core change to how the crew reacts, so it wants its own run with the suite watched
closely, not a tired half hour at the end of a long one.

### Narrowing it: the ramp step is the unit's own target

Both change handlers are already careful, which is what makes the answer clear:

```csharp
void OnDesignationChanged(int x, int z)
{
    if (State == Idle || State == Unreachable || State == NeedsSomewhereToTip)
        _rethink = true;
    else if (x == JobTarget.x && z == JobTarget.y && Job != CrewJobKind.Serve)
        _rethink = true;
}
```

Unit 0 is **Moving**, so only the second branch can fire — and its `JobTarget` **is** the ramp step
at (14, 13). So the thing being changed every tick is the auto step itself, and the unit walking to
cut it is told to start again each time.

Which makes the suspect narrow and testable, though **not yet confirmed**: `JobDispatcher.
UpdateRamp` runs every tick and calls `EndRamp` the moment any unit reports it
`CanWorkFromSomewhereReachable(target)`; `RequestRamp` then re-plans the ramp on the next rethink,
and now that every digger asks on every rethink (the change earlier today that let a busy crew ask
for a ramp) the two can trade the same designation back and forth indefinitely.

If that is it, the fix is narrower than "rework when a rethink happens": **a ramp should not be
ended while a unit is on its way to cut its step.** Hysteresis on one decision rather than a change
to how every unit hears about the world. Confirm first — log `EndRamp` and `PlaceRampStep` for a
few ticks — because this is the fourth explanation for these cells and the first three were wrong.

## 2026-09-22 — Ronan: smoothing is a unit's job, and the next trial is a road cut

Two rulings.

**Smoothing comes off the list.** *"dont worry about smoothing i will use a new unit for that"* —
so trial C goes, and levelling ground is a job for a machine built to do it rather than something
the general crew logic has to be good at.

**The next trial is a straight road cut.** *"lets try and get the dirt feeling like its actually
dumping and spilling, lets test a big long road section cutting through a hill straight not sloped
with a large quarry and a large dump site."*

So the thing being judged is **how the material looks and behaves**, not whether the job completes:
a long straight corridor held at one level through a rise, so there is a real cut face and real
spoil, with a quarry big enough to feed the fill and a dump big enough to take the surplus. The
road is held level rather than following the ground on purpose — a sloped road hides the cut.

## 2026-09-22 — Why a tip did not look like a tip

`TinyDiggers/Road Cut Capture`: a forty-cell road at width five, held **level** at 6.5 m through a
3 m soil rise, with a 225-cell tip and a 169-cell quarry. The cut reads well — there is a face, and
the mech works it — and the tip reads as nothing at all. Two separate causes, neither of them the
angle of repose, which was there and working the whole time.

**The slump was over before it could be seen.** `TerrainView.SlumpTilesPerTick` was a thousand
cells a frame, so a tipped load finished finding its angle in the same frame it landed. There is
now a `SlumpTilesPerSecond` (ninety) budgeted in **unscaled** seconds, so a barrow's cascade takes
about half a second to run whether the clock is at one times or twenty — what is being paced is the
eye, not the work. Nought turns the limit off, which is what generation wants.

**And the heap was being deliberately flattened — by my own fix.** "Build out before up", added
this morning to stop a tip growing a 2.5 m tower that walled the crew in, spreads each load onto
the lowest cells of the zone. Over 225 cells that takes a road cutting's spoil as a film. Right for
pathing, wrong for the eye. The cap is now three steps proud of the zone floor rather than one
climb, which builds a mound with a slumped apron and still stops well short of the tower.

The first attempt at the trial is worth keeping too: it took the biggest rise it could find, which
was a **twenty-metre rock mountain**, and marked a road across the top of it. True to the letter and
no use. The search is banded now — soil only, one and a half to six metres, aiming for three.

### Two ways the trial was lying about the heap

**The overlay was standing on the thing being looked at.** The coloured sheet over designated and
zoned ground is a child object with its own renderer, so turning the `DesignationsView` component
off does nothing — and it is drawn exactly where the spoil is. Every picture of a tip so far has
been a picture of bright green. `DesignationsView.ShowOverlay` turns it off properly, and the two
spoil shots take it down.

**And the heap measurement was measuring the hillside.** "The heap stands 7 m at its highest" for a
heap of about a metre: it took the highest and lowest ground in a seventeen-cell window, most of
which was the natural slope the tip sits on. It now records what the ground was **before** anything
was tipped and reports how deep the spoil lies and over how many cells — which is what a heap is.

Worth noting what was already right and simply invisible: tipping converts material to its
disturbed form (`Excavation` uses `GetDisturbed`), so tipped topsoil lands as bare dirt and a heap
really is a different colour from the grass round it. That was working the whole time, under the
overlay, on a pile too flat to catch the light.

### Correction: the road crew is not in the rethink loop

Asked the crew directly, mid-run, rather than inferring from a status line:

```
[0 Digging path 0/0] [1 Waiting path 18/24] [2 Moving path 5/18]
[3 Waiting path 2/22] [4 Digging path 0/0] [5 Parked by digger 4]
```

**No RETHINK on anybody, and the paths are advancing** — eighteen waypoints of twenty-four, five of
eighteen. So the "Waiting for a unit" in the road shots is ordinary queueing where two units
converge on the face, not the pathology the pit has. Saying otherwise on the strength of a matching
status line was exactly the mistake this session has made four times already: **the status line is
not the diagnosis.**

The rethink loop remains real and remains the pit's problem. It is not the road's.

### The heap cap ratchets, and the new metric is what caught it

The honest spoil measurement earned its keep on its first run:

> **the spoil stands 7 m deep at its deepest over 286 cells** — and three robots reporting
> *"UNREACHABLE … ramp blocked: nowhere to stand beside"*.

Seven metres, from a cap that was supposed to hold it to one and a half. The rule reads "no more
than three steps proud of the **floor of the zone**", and the floor is the lowest *open* cell —
which rises as the heap fills in. The ceiling climbs with the floor. It is the two-and-a-half-metre
tower from this morning all over again, arriving more slowly and with a rule in place that looked
like it should have stopped it.

The fix is the mechanism that was already there and unused: `DesignationMap.SetDumpZone` takes a
**per-cell cap**, defaulting to infinity. The trial now sets it to a metre and a half over the
ground each cell starts at, so the ceiling is fixed to ground that cannot move. The zone-floor rule
stays as the thing that spreads a load out; the cap is the thing that stops a tower.

Two lessons, both already learned today and both re-learned: a limit measured against something
that moves is not a limit, and **the measurement is what finds this** — the old metric would have
said "7 m" as well, for the wrong reason, and been ignored.

### A capped tip fills up, which is the right behaviour and needs a bigger tip

With the per-cell cap in, the spoil came out at **1.5 m deep over 274 cells — not one cell over its
cap**, against 7 m before. The heap is a long rounded mound of bare dirt and it reads as tipped
material.

And the crew then stopped with the road three quarters built, every unit saying *"needs a Dump Zone
or a Fill to tip into"*. That is not a fault: 225 cells at a metre and a half is about 84 m³, and
the cutting makes about 75 — so the tip genuinely filled, and slump carried some of it outside the
zone as well. A capped tip fills up. That is what makes where to put the dump a decision rather
than a formality, and the banner says so plainly.

For the trial it just means the dump has to be worth the name: **441 cells at three metres**, some
330 m³, which is room to watch the whole job rather than the first three quarters of it.

### The larger tip unblocks it, and a pairing bug shows up on its own

Queried live, mid-run: **52 designations left of 202, 441 tip cells, 0 over cap**, and the whole
crew working — two hauling, two heading for the face. The sequence across the three runs:

| | designations left | the spoil |
| --- | --- | --- |
| no cap | 32 | **7 m tower**, crew walled in, ramp planner stuck |
| cap, 225-cell tip | 73 | 1.5 m, tip full, crew stopped and said so |
| cap, 441-cell tip | **52 and still working** | 1.5 m, nothing over cap |

Which is the shape worth having: a tip with a real capacity, spoil that builds a mound inside it,
and a game that says "this is full" rather than growing a tower through its own crew.

The same roll turned up something else, unasked:

> `[4 WaitingForHauler :: Full: waiting for a hauler] [5 Idle :: Idle: no digger to serve]`

The digger mech is full and waiting for its dumper; the dumper believes it has no digger to serve.
They have lost each other. That is trial F — haulage balance — arriving on its own, and it is a
pairing bug rather than anything to do with dirt: look at `JobDispatcher.AssignDigger` /
`HaulerFor` / `DiggerFor` and what clears a pairing.

### Correction, and a real finding: a level cut divides the site

The "pairing bug" is not one. Asked the dispatcher directly:

```
[4 Digger cell (1585,1512) haulerFor=-1 diggerFor=-1 :: Full: waiting for a hauler]
[5 Hauler cell (1569,1528) haulerFor=-1 diggerFor=-1 :: Idle: no digger to serve]
 dumper->digger reachable=False
```

**Every pairing is clean** — nothing is desynced. The dumper simply **cannot get to its digger**.
And three of the four robots are saying the same thing in different words: *"UNREACHABLE … ramp
blocked: no ramp route to (1598, 1510)"*.

So the crew has cut the road so well that it has **cut the site in two**. A road held level through
a rise is a trench, and a trench divides the ground either side of it. The digger is in the cut,
the dumper is out by the tip, the remaining work is on the far side, and there is no way across.

That is not a fault to patch — it is the thing a real earthworks crew spends its life managing, and
it is what the road is *for*: once it is finished it is the way through. The problem is the order
of work. The crew takes the middle of the cutting out first, which makes a pit before it makes a
through-cut, and a pit with no ends open strands whatever is inside it.

Worth thinking about rather than patching:

- **Cut from the ends in**, so the corridor is a through route at every stage rather than only at
  the end. That is how a real cutting is driven, and it needs no new rule — only an ordering.
- **Or let the unfinished corridor be a haul road**: the cut floor is level and drivable along its
  length; what is missing is a way down into it, which is exactly the auto ramp's job, and the
  ramp planner is currently answering "no ramp route" instead.

Either way the lesson is the same one this session keeps finding: **the status line is not the
diagnosis.** "Waiting for a hauler" beside "no digger to serve" reads like a pairing bug and is
nothing of the kind.

### And a correction to the correction: the ends are connected

Measured the corridor rather than inferring from it:

```
centre line z=1511: 1557:5.5 … 1589:5.5 1591:7D 1593:9D 1595:9.5D 1597:10D 1599:9.5D 1601:11
 | ends connected=True
```

The western two thirds is cut to its 5.5 m and the eastern third is still original ground rising to
11 m — and **the two ends of the line are connected**. So "the crew has cut the site in two" was
too strong: what failed was reachability between one digger and one dumper at two particular
points, not the site as a whole.

The ordering observation still stands — the crew works the cutting from one end and the far end is
untouched — but the trench-divides-the-site story was a reach. Three corrections in a row from the
same habit: reading a story into a status line and then arguing from the story.

## 2026-09-22 — Refining the road tool: showing the depth

Ronan: *"refine the road tool like Cities Skylines 2 … the road is above the ground, say you want
it high up, its sides need to taper down to support it like a real road. The same goes for going
down — we need a way to see what our depth is."*

**The taper is already built.** `RoadPlanner.Settle` cuts the ground above a road back to its own
angle of repose and builds the ground below it up to the angle the spoil holds, and `RoadsHost`
already plans those faces, counts their volume into Cut and Fill, and draws them in the ghost. So a
road lifted onto a bank already grows its embankment, and one driven into a rise already has its
cutting battered back. What was missing was every number that would let you *see* it.

So this is readout, and nothing about how a road is planned or built has changed:

- **Each node says what it is doing to the ground** — `+2.4 m` on a bank (blue), `-1.8 m` in a
  cutting (amber), or `at grade`. `RoadsHost.NodeOverGround` is the road's height at the node
  against the ground under it.
- **Each segment label gains a second line** under its grade: `fill 1.6 m` or `cut 2.2 m`,
  whichever the segment does more of. Measured along the centre line, sample by sample.
- **A tally across the top**: `cut X m³  fill Y m³  deepest cut A m  highest fill B m`, and
  "— too steep to build" when the grade refuses it.

`DeepestCut` and `HighestFill` are on the host for anything else that wants them.

What this does **not** yet do, and is the next question rather than an oversight: set a node to a
target height or a target grade directly. At the moment a node is raised by a nudge and you read
the result; Cities Skylines lets you say "ten metres up" and holds it. That is a bigger change to
`RoadDraft` and wants its own pass.

---

## 2026-09-22 — Road tool, part C: curves, ramps and the numbers that go with them

Ronan: *"refine the road tool like city skyline 2 … you need to be able to find a slope or height
or depth curves etc"*, then *"Use best choice you will also need all the smooth turn tools as well
to make curves ramps etc"*. The three questions left open in part B were delegated, so they were
answered here rather than asked again.

**Curves already existed and were not the gap.** The road is a cubic Hermite spline through its
nodes, each tangent automatic (Catmull-Rom) unless a handle has been dragged. Three clicks already
gave a smooth curve. What was missing was any way to *judge* a bend and any way to shape one
except dragging by eye.

**Turn radius is the missing reading.** `RoadSpline.TurnRadius` walks a segment and takes the
circumradius of each three consecutive points; the tightest is the segment's bend, in metres. A
straight road returns infinity rather than dividing by a zero area, so "bigger is gentler" holds
everywhere and a minimum radius is a plain floor. `RoadPlanner.JudgeTurn` grades it exactly as a
slope is graded (over the minimum fine, under half of it refused) and the default minimum is 4 m —
a little over five of the half-metre cells, the tightest bend a 0.7 m dumper rounds without
reversing. It is **reported, not refused at commit**: the grade rules already decide what can be
built, and a second gate would silently block roads the player has good reason to draw.

**Smoothing had to be taught where to look.** `SmoothTo(node, radius)` widens the node's handle
along the way the road already runs, in steps up to the shorter adjacent chord. Three wrong
measures before the right one, all on the same dog-leg (two near-right-angle bends, 13 m legs,
drawn bend 2.2 m):

- the whole of both adjoining segments → **2.5 m**. The neighbouring node's own sharp bend
  dominated the minimum, so every handle length scored the same and the tool kept the shortest.
- the near 45% either side → **3.4 m**, but the stretch in the middle of the shared segment then
  belonged to no node's reading, so the nodes each claimed over 4 m while the road turned in 3.4.
- half a segment either side (every stretch owned by exactly one bend) → **3.4 m** again, because
  the tight spot had moved next to the *end* nodes.
- the whole of both segments bar the 15% nearest each neighbour, **ends included**, two sweeps →
  **3.6 m**. An end node's automatic tangent is a whole chord, twice what an inside node gets, and
  the swing that puts in the first and last stretches was the tightest bend left on a road whose
  corners had all been rounded.

**It stops at 3.6 m and says so.** Handle length alone cannot make that dog-leg turn in 4 m with
the nodes where they are; a road wanting a wider arc needs another node, not a longer handle. So
`SmoothTo` returns the radius it actually reached and the tool says "widened to 3.6 m — as far as
these nodes allow", and the tally goes on showing "tightest bend 3.6 m". A tool that claimed the
radius it was asked for would be worse than no tool. The test asserts the opening-out and the
owning-up, not a number the code cannot promise.

**A held grade has to aim below the grade it wants.** `ApplyGrade` lays every node after the first
at one slope, each stretch keeping the way it already ran (up stays up, flat stays flat). Laid at
exactly 12% it read back as **12.4% / 12.6%** — over the limit and orange — because the height
eases in and out at each node, so a segment is steeper in its middle than end to end, and `Grade`
reports the steepest point. A second pass at `grade × grade / worst` brings it to **11.7% / 12%**.
`GradeLock` does the same while drawing: each node takes the last node's height plus the slope
over the run, climbing or falling whichever way the ground goes.

**"Hold N metres above the ground" is one field, not a mode.** `RoadNode.GroundOffset` is metres
held above the land while the node still follows it. Nothing else changed: a causeway is a road
whose nodes are locked to the ground two metres up. Carried through `Place`, `Move`, `SetLocked`
and `Commit`.

**Evidence** (`Screenshots/RoadCurve/`, one road, four states): drawn, tightest bend 2.2 m with
every segment labelled; smoothed, 3.6 m and visibly a drivable S; the grade held, 11.7% / 0% /
12%, 126.4 m³ of fill; and carried 2 m up, 384.1 m³ of fill with the embankment tapering down to
the land on both sides — which is the thing Ronan asked for in as many words ("its sides need to
taper down to support it like a real road"). Suite green: 535 passed, 1 skipped.

**Keys** (Road tool): C smooths the bend at the active node, Shift+C the whole road; X makes a
hard corner, Shift+X back to automatic; G holds the grade while drawing, Shift+G re-cuts the road
already drawn to it; H carries the node half a metre higher over the ground, Shift+H lower.

---

## 2026-09-22 — Cutting corners into arcs, and a readout that was wrong everywhere but the origin

Part C left one shaping job undone: a bend that the nodes themselves are in the way of. Pulling a
handle opened a right-angle dog-leg from 2.2 m to about 3.7 m and no further, which is the honest
limit of moving tangents around while the nodes stay put.

**`RoadDraft.Fillet` cuts the corner out.** The corner node goes, and two nodes take its place a
tangent distance back along each leg (`d = R·tan(Δ/2)` for a deflection Δ), each handled
`4/3·tan(Δ/4)·R` along its leg — the standard cubic approximation of a circular arc. `Round` pulls
the handle first and cuts only if pulling cannot reach; `RoundAll` sweeps the chain backwards, so
a corner becoming two nodes does not shift the ones still to be looked at. The arc is cut down to
whatever the shorter leg allows and the radius actually laid is returned, as `SmoothTo` does.

**Then the capture and the test disagreed, and the test was right.** The same chain, the same
code: 3.98 m in an edit-mode test at the origin, 3.7 m in play mode. Two wrong explanations were
written down and thrown out before the measurement — that the trailing `SmoothAll` was
re-pulling the arc's handles (it is not: `SmoothTo` only ever keeps the widest, so sweeping again
changes nothing), and that the fillet was being clamped by leg length (it was not: the tangent
distances logged out at exactly `R·tan(Δ/2)` for R = 4 m). Logging the same chain translated to
the origin settled it in one run: **4 m at the origin, 3.7 m at (1565, 1547)**.

**The bend readout was losing the world's coordinates.** A radius is taken from three points about
a tenth of a cell apart; the road sits fifteen hundred cells out; and in float those two facts
leave almost no significant digits in the triangle's area. Rebasing the circumradius on the middle
point and working in double got 3.7 to 3.85 — not enough, because `Evaluate` had already thrown
the digits away before the radius was computed. The fix is to sample the curve *relative to the
segment's own first node* (`FlatNear`), so every term stays small: **4 m out in the world, and
straights that used to read "156.9 m" now read tens of thousands.**

This was not only a cosmetic error. Every bend on the island read about 7% tight, so the tool
warned about turns it had just built to order — a readout that is wrong everywhere the player
actually builds, and right only in a test. `JudgeTurn` now also allows a hundredth of the radius
of slack, since an arc cut to exactly the minimum measures a hair under it.

Evidence: one dog-leg, drawn at 2.2 m, rounded to **4 m with both corners cut to arcs**, judged
Fine. Suite green: 541 passed, 1 skipped.

---

## 2026-09-22 — The crew builds a shaped road

Every picture of the curve tools so far had been the ghost. The capture now lays the smoothed road
and lets the crew build it, which is the only way to find out whether an arc survives being
rasterised into designations and dug.

It does. **30 designations, 0 left and 0 road cells unpaved after 265 game seconds**, and the
paving follows the two 4 m arcs without a kink. Nothing about the fillet upsets `RoadPlanner.
Footprint` — the concern was that a 4 m radius on half-metre cells with a three-cell road might
fold its inner edge over itself, and at this radius it does not.

**The first run built four cells and stopped**, with every unit saying *"Nothing to fill with:
mark a Quarry"*. That was the trial's fault, not the crew's: the road needed 4 m³ of fill and
there was nowhere to get it. Worth writing down because the status line named the missing thing
exactly, and the fix was to mark a quarry, not to touch any code. A tip alone is not a site.

---

## 2026-09-22 — The pit's "rethink loop" does not exist

The deep pit has been carrying an open note for two sessions: `path 1/8 RETHINK` in the status
line, blamed on the auto-ramp and `RequestRethink` trading a designation back and forth. It was
never measured. It is now, and it is not what was happening.

**A counting test rather than a guess** (`DeepPitTests.TheCrewIsNotToldToThinkAgainOverAndOver`):
four robots, a six-step pit, 240 s of work, watching `DesignationMap.AutoCancelled` and each
unit's `Rethinking`. Result: **0 Auto cancels, units wanting a rethink on 0.6% of ticks, and
30.75 m³ still outstanding.** There is no loop. `RETHINK` in a status line is a unit about to
choose a job, which is the ordinary thing for it to be doing.

Two candidate mechanisms were checked and both ruled out:

- **Ending a ramp is not mistaken for the player cancelling one.** `EndRamp` clears its Auto step
  with `DesignationMap.Clear`, which deliberately does not raise `AutoCancelled` — only `Cancel`
  and `CancelDesignation` do. A test now holds that line down
  (`AutoRampTests.ARampDroppedBecauseItIsDoneIsNotTreatedAsCancelled`), and it passed the moment
  it was written, which is how the idea was dropped.
- **The `ramp would cut into designated` guard is not what blocks the pit.** Skipping it for a Dig
  whose target is already at or below the ramp's step changed nothing at all, so that change was
  reverted rather than kept as an unproven branch.

**What is actually wrong.** `JobDispatcher.PlaceRampStep` walks the corridor `for (i = 1; i <
_corridor.Count - 1; i++)`, so the last step is never examined — and in a pit the last step is the
only steep one there is: the drop off the plateau into the cut. The planner falls out of the loop
and reports `nowhere to stand beside (9, 18)`, which is true and beside the point. The crew never
gets a way in, two units end up inside the cut holding barrows they cannot carry out, and the pit
stops at four fifths dug.

Fixing it means deciding what a ramp step cut *into the target cell itself* should take down, and
by how much, without eating the player's own order for that cell. That is a design question for
Ronan, not a patch, so it is written down here and the reproduction is `[Ignore]`d beside
`AShallowPitIsDug` with the same numbers in it.

**Worth keeping as a rule:** the status line is not the diagnosis, and this is the third time on
this one bug. Counting the thing being blamed took one test and one run; the two sessions before
it were spent reading status text.

---

## 2026-09-23 — Taking on more units while the game is running

Ronan: *"Add a way for me to add more of any of the three units I want."* The counts were three
serialized fields read once in `CrewView.Start`, so trying a second dumper meant stopping the
game, editing the inspector and starting over — no way to find out how many of a thing a site
wants, which is the whole question the throughput ladder is for.

`CrewView.Hire(UnitRole)` is the per-unit half of that start-up loop lifted out whole: the unit,
its capacity, `AsMachine()` for the two machines, the body from its prefab, the animator, the work
times taken from the clips, and its path line. Nothing was rewritten for it, so a unit taken on
mid-game is the same unit the crew starts with, by construction rather than by inspection. Start
now just calls it in a loop.

Two things it does that the loop did not have to:

- **It looks for clear ground.** The yard fills up as the crew grows, and the old loop's
  side-by-side arithmetic would drop the ninth unit on top of the third. `CrewSpawn.TryFind` from
  the spot the arithmetic picks takes the nearest clear cell instead.
- **It sets the speed.** `Update` assigns speeds every frame anyway, but a unit that spends its
  first frame at the class default is a unit that behaves differently from its neighbours for a
  frame, and there is no reason to allow it.

The panel row is under the crew list, where the crew it adds to is: `Take on  [Robot] [Digger]
[Dumper]`. The buttons carry `UnitNames.Short` — one word each — because the full names ("Starter
robot", "Digger mech", "Dumper mech") ran into one another at 330 px and read as a single word.
The full name is in the tooltip.

Verified in play: 6 units to 9 and 9 to 12, each with the right capacity (0.375 barrow, 0.75
scoop, 2.25 bed), each on its own cell, each with a body and an animator, and the spare dumper
pairing itself off — it says "no digger to serve", which is the right thing for a third dumper on
a two-digger site to say. Suite 543 passed, 2 skipped.

---

## 2026-09-23 — Letting a unit go, and the stale icon it uncovered

The hire row only went one way, so `CrewView.Dismiss(int)` is the other half: the unit leaves the
dispatcher (which releases its job, unpairs whatever it was working with and frees its cell), its
body, ring and path line are destroyed, and the ten lists the view keeps per unit drop the same
slot together. The selection holds indices too, so entries past the gap come down one and the
dismissed one goes.

**Those ten parallel lists are the whole risk.** One list missed and a unit is quietly wearing
another's body, with nothing to say so. They are removed in one block for that reason, and the
check in play mode was not "does it look right" but *which unit is each body actually standing
on*: after dismissing from the middle of the list, every body was still on its own unit, and the
selection `1, 4, 5` came back as `1, 3, 4` — the shift, exactly.

**Whatever it was carrying goes with it.** A unit is not a container the site can get its spoil
back out of, and dismissing a full dumper to save it a trip to the tip should cost what it was
holding.

**The row icon was set once and never again.** It never mattered while roles could not change at a
given row, but after a dismissal everything below the gap moves up, and a row read "Dumper mech 1"
beside a robot's icon. It is now set every frame beside the name. A real bug, found by looking at
the picture rather than the numbers — the numbers were all correct.

**Where the button is.** A × on the row of the unit going, not a Dismiss button elsewhere acting
on "the selected unit", which is a button that can be pressed by accident with the wrong thing
selected.

Checked in play: dismissing a digger that a dumper was parked by leaves the dumper saying "Idle:
no digger to serve" on the next tick — the status read in the same frame is one tick stale, which
is the readout catching up, not the pairing failing. Suite 543 passed, 2 skipped.

---

## 2026-09-23 — Can the road tool be the ramp to a lower level?

Ronan: *"What if user needs to use road tools to make ramp to lower level?"* A road already is a
grade-limited, battered, crew-built cut, so it is a fair question, and it was answered by
measuring rather than by arguing. Two tests, both on the six-step pit.

**A road drawn below the ground is dig work, and the crew starts on it.** Planning a chain from
the plateau to the pit floor and turning its footprint into designations the way `RoadBuilder`
does gives 12 cells of cut straight away. So the mechanism works: there is nothing to build.

**But a road at the road tool's own grade does not fit.** Three metres down wants **25 m of run at
12%**, and the run from the plateau to the pit edge is **1.5 m**. Drawn as it stands, the chain
comes out at **200%** and the tool would refuse it — correctly. For a big quarry with a long haul
road in, 12% is right; for a pit a few metres across it is geometrically impossible.

**Drawn steeper it helps, and it is still not enough.** 12% is the default, not a law: Max grade
is a field in the panel, and the crew's own limit is far more permissive than a haul road's — a
metre of step across a half-metre cell is 200%, and the pathfinder allows 45°. A ramp drawn at
**100%**, which fits in six cells, moved **10.75 of 42.38 m³** against **about 8 of 39** with no
ramp at all. Better, and nothing like a dug pit.

**Why it stops in the same place.** The deep end of a drawn ramp is itself ground that has to be
cut before it can be walked on, and cutting it means reaching over the same wall the auto ramp
cannot get through. A drawn ramp changes who decides where the way in goes; it does not change
whether the crew can cut the last step of it.

**So the answer to Ronan's question is yes, with a caveat worth having in the game**: the road
tool can make a ramp to a lower level, and for anything with room — a quarry, a terrace, a haul
road down a hillside — it is the right tool and already works. It is not a way round the pit
fault, and the pit fault still wants the ruling: what a ramp step cut into the target cell should
take down.

Both are tests now, not notes. `ARoadRampIntoAPitNeedsMoreRunThanAPitHas` holds the geometry, and
`ASteepRoadRampGetsTheCrewIntoThePit` is a characterization test that asserts the pit is *not*
mostly dug — it fails, loudly and on purpose, the day the ramp fault is fixed.

---

## 2026-09-23 — Size fixes the geometry, not the crew

Ronan, on the road-ramp finding: *"it would depend how big you make it, and if you selected a hill
and below you can dig out the hill for fill — that's part of the game."* Quite right about the
size: the pit in `DeepPitTests` is seven cells across, three and a half metres, and measuring a
haul road against it was measuring the fixture. So the site got built at the size the game is
played at — `BigSiteTests`, a hundred and twenty cells square, with a forty-metre cut in the
middle of it.

**At that size the road tool does fit, exactly as he said.** Three metres down at 12% wants
twenty-five metres of run, and a forty-metre cut has that inside it. Drawn from fourteen cells
outside the rim to the far side, the haul road comes out at **11.1%** and `RoadPlanner.Judge` calls
it Fine. (The first attempt drew it six cells out, got 13% and was refused — which is the tool
doing its job, and worth keeping as the reason the number in the test is what it is.)

**And the crew still do not get down it.** Same site, haul road drawn at 11.1% and marked as dig
work the way `RoadBuilder` marks it: **16.5 m³ of 1200 moved in 900 game seconds, the deepest any
unit stood being 1 m of 3 m**, with `ramp blocked: nowhere to stand beside (53, 55)`. They cut the
top of the ramp and stop.

**So the size question is settled and the fault is not about size.** A drawn ramp is ground that
has to be cut before it can be walked on, and cutting its next step means standing somewhere that
is itself still to be cut — the same `PlaceRampStep` blind spot as in the seven-cell pit, which
never examines the last step of its corridor. Making the hole bigger gives the ramp room; it does
not give the crew a way to cut the step in front of them.

Both are tests. `ABigCutHasRoomForARealHaulRoadDownIntoIt` holds the geometry Ronan was right
about. `AHaulRoadDrawnIntoABigCutTakesTheCrewToTheFloor` is a characterization test asserting the
crew *do not* reach the floor, so it fails the day the ramp fault is fixed.

**Still not tried, and the other half of what Ronan said:** *"if you selected a hill and below you
can dig out the hill for fill"* — cutting a rise down to feed a fill elsewhere, rather than digging
a hole in a plain. That is Trial B, still unrun, and it is a different shape of problem: the crew
start on top of the material and work down from above, which is the case the reach rules are
happiest with. Worth running before any ramp ruling, because it may show the digging is fine and
only the descent is broken.

---

## 2026-09-23 — Correction: the big cut was never stalled, and the face model is already in

Ronan: *"How can we set where they start digging from the front, where they can reach, first
what's above ground, then dig into the ground — isn't this how Captains of Industry does it?"*
That is the right model, and answering it properly meant going back and checking the previous
entry, which turned out to be wrong.

**The correction.** The entry above says the crew "cut the top of the ramp and stop" in the
forty-metre cut. They do not. Sampling what they move per hundred game seconds gives **2.88, 2,
2.13, 1.88, 1.75, 1.5 m³** — declining gently as the haul to the tip lengthens, never stopping.
That is about a cubic metre a minute, which is exactly what four starter robots with barrows move
(the throughput ladder says 1.575 m³/min for a pair). The hole is **1200 m³**. They are working
normally on a site that would take them thirteen game hours.

I read `RampNote` and called it a stall. That is the third time on this bug that a status line has
been taken for a diagnosis, and the second time in two days. The rate took one helper and one run.

**"Deepest a unit stood: 1 m" is benching working, not a crew stuck on a rim.** `DigFloor` already
holds every dig cell within one `BenchDepth` of its highest dig-designated neighbour, so a
designated block comes down a metre at a time and nobody ever stands deeper than the bench they
have finished. At one per cent into the first bench of a forty-metre cut, a metre is exactly where
they should be.

**So the face model Ronan describes is in and working.** Cells come off in benches from the ground
the crew can already stand on, the cut advances into the block rather than dropping a cliff, and
the comment on `DigFloor` says as much: *"keeps a designated hill coming down in benches rather
than as a cliff, so its upper cells always have somewhere within reach to be worked from."*

**What is left is the small-block case, and it is now properly narrow.** A bench needs room to
lie down in. Seven cells across wanting three metres of depth cannot hold one, so nothing can come
off until a ramp is cut, and the ramp planner has the blind spot (`PlaceRampStep` never examines
the last step of its corridor). That is a real fault, and it is a fault about *small* cuts, not
about digging in general — which is a much smaller and less urgent thing than two days of notes
made it look.

The test is renamed `ABigCutComesDownInBenchesRatherThanStalling` and now asserts what it actually
shows: the crew keep working and the first bench comes down.

---

## 2026-09-23 — Trial B: a cutting through a hill jams on its own width

Run in play mode with Ronan watching. A through-cut is marked across a rise near the crew — the
classic road cutting, floored level with the ground on the low side — and the crew let at it at
20× time.

**42 cells through a 5 m rise, 32.63 m³. Moved 19.25 m³, then stalled with 13.38 m³ left** after
1701 game seconds, at 0.0113 m³ a game second (0.68 m³/min).

**Where the time went, which is the finding:**

```
Waiting 41%   Moving 30%   Parked 15%   Digging 14%
171 cuts landed, 28 refused on arrival, 0 waited off its own stand
```

Four of the six units finished on *"Waiting for a unit at (1523, 1562)"* — two cells between them.
Nothing was out of reach, no ramp was wanted, nothing was refused for want of somewhere to stand
(`0 waited off its own stand`). They were queueing.

**The cutting is three cells wide — a metre and a half — and there are six units in it**, one of
them a 0.7 m digger with a dumper parked behind it. At 0.68 m³/min against the 1.0 the same crew
manage in the open forty-metre cut, a third of their output goes on standing in each other's way.

**This is a gameplay finding, not a bug.** A working face has to be wide enough for the crew sent
at it, and choosing between a wider cut and a smaller gang is exactly the decision the player
should be making. What the game does not yet do is *tell* them: six units queueing on two cells
reads as "Waiting for a unit", which says what a unit is doing and not that the site is too narrow
for the crew. A crew panel that said "four units are queueing — this face is too narrow" would
turn a stall into a decision.

Worth keeping in mind against the throughput ladder: the ladder's m³/min were measured on a strip
three cells wide chosen to measure the unit rather than the site. Six units do not get six times
one unit's rate in a cutting that width, and the ladder should not be read as if they do.

**Also noted:** the trial's own camera ended up inside the hill for the "worked" shot, so the
picture shows the crew from within the spoil. Cosmetic, in the trial harness rather than the game.
