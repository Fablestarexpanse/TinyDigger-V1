# TinyDiggers — decisions and findings

Append-only log of choices that the code cannot explain by itself. Git records what changed;
this records why.

---

**NEXT:** Vertical Slice 0 is feature-complete and green (80/80): column-stack grid, flat-topped
walled renderer, RTS camera, brush dig/fill, hover readout, all in
`Assets/TinyDiggers/Scenes/TerrainSandbox.unity`. Nothing is in flight. Still owed: a frame-time
check on a mid-range GPU (the readout's top line shows it), then Ronan's call on what comes next.

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
