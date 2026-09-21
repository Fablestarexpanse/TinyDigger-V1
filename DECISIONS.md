# TinyDiggers — decisions and findings

Append-only log of choices that the code cannot explain by itself. Git records what changed;
this records why.

---

**NEXT:** Slice 10 (ore underground) is built, green (309/309) and pushed. Waiting on Ronan's look at
`Screenshots/Slice10/`. Open:
- seams don't show as bands on pit walls (the shader has no strata-by-height);
- deep stone pit walls collapse into loose rock and bury a seam;
- the crew is too slow to reach ore 5–7 m down in a short test, and small deep pits block the
  auto-ramp.
The next CoI step after ore is stockpiles (count what the haulers bring).

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
