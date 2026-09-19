# TinyDiggers — decisions and findings

Append-only log of choices that the code cannot explain by itself. Git records what changed;
this records why.

---

**NEXT:** `TERRAIN_REFERENCE.md` section 5 "Now" plan done and green (137/137): smooth
per-vertex normals, grain and edge-line shader, plateau/terrace test terrain, bulking, loose-only
thin-layer bias. Nothing is in flight. Still owed: a frame-time check on a mid-range GPU (the
readout's top line shows it); two known close-up artefacts at pit rims (see the 2026-09-19
"reference" entry); then Ronan's call on what comes next.

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
