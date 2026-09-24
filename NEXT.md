# NEXT

Working notes for the autonomous run Ronan asked for on 2026-09-22: *"test a wide variety of
digging and ramps and laying roads, digging through hills, smoothing, how to get the material to
move like dirt and rock, how to show material being dumped … find improvements and uses for
digging we haven't figured out, and work on them while I am gone."*

Rulings and findings go to `DECISIONS.md` as they happen; this file is only the breadcrumb — what
is done, what is running, what comes next.

## NEXT

**Ground pose, 2026-09-24:** machines now pitch and roll to the ground under their wheels
(`Presentation/GroundPose.cs`, smoothed and clamped in `CrewView.LateUpdate`). Done and green;
still owed from Ronan's brief is the 8-second capture of a dumper driving up a benched hillside and
along a contour, `Screenshots/Feel/pose.mp4`. A still on a drivable grade is in
`Screenshots/Feel/pose-on-slope.png`.


**Worksites, 2026-09-24:** Captain of Industry style — a building and a spline-outlined work area of any shape (tool 0); vehicles
are assigned by right-clicking it and work only there; unassigned vehicles park. Replaces posting to
terraform shapes. See `DECISIONS.md`. Look at it in play mode first.

**Road machines, 2026-09-24:** a Bulldozer and a Paver stand-in (dumper-sized boxes) now grade and
surface roads as crew units; nothing grades or paves on its own any more. Look at them in play.

**Roads graded smooth, 2026-09-24:** earthworks → graded (drawn at the true grade; `AutoGrade`
stands in for the bulldozer) → surfaced (gravel and speed; nothing does it until the paver unit
exists). See `DECISIONS.md`. Next for Ronan's units: `RoadBuilder.NeedsGrading`/`Grade` and
`NeedsSurface`/`LaySurface` are the hooks.

**Road tool, 2026-09-24:** the segment to the cursor, undo, right-click takes a node back, smooth
drags, and the ghost showing the land as built (V) — see `DECISIONS.md`. Tested outside Unity only;
**look at it in play mode first**, then the rest of the list: insert/delete a node mid-road, T-junctions,
starting a road from a built road's node.

**Road tool (Ronan's current ask).** The taper was already built — `RoadPlanner.Settle` batters a
cutting back to the ground's angle of repose and builds an embankment up to the spoil's, and the
ghost draws it. What was missing was the numbers, and those are in now and verified in play mode:
node labels (`at grade` / `+4 m`), a depth line under each segment's grade (`fill 4 m`), and a
tally (`cut 0 m³  fill 315.9 m³  highest fill 4 m  — too steep to build`).

**One question answered from the code rather than asked:** a node already holds either behaviour.
`LockToGround` true means it takes the ground's height as it moves ("Node on ground" in the panel);
`Raise` unlocks it and it then **holds its absolute height**. So "hold a height" exists.

**Part C is in and verified** (see `DECISIONS.md`, 2026-09-22 road tool part C). All three open
questions were answered under "use best choice", plus the smooth-turn tools:

- **Turn radius** read per segment and shown in the labels and the tally; judged against a 4 m
  minimum the way grades are judged, and reported rather than refused at commit.
- **Smooth** (C / Shift+C) rounds a bend by pulling its handle, ends included; it returns the
  radius it actually reached and says so when the nodes cannot hold the arc asked for.
- **Grade lock** (G) while drawing and **Shift+G** to re-cut a drawn road to one slope, aiming
  below the target so the steepest point still reads under it.
- **Ground offset** (H / Shift+H): a node held N metres over the land, still following it — the
  causeway case, with the embankment tapering down both sides.
- **Typed height**: `RoadsHost.SetActiveHeight(metres)` for the panel to call.

Evidence: `Screenshots/RoadCurve/{1_drawn,2_smoothed,3_graded,4_causeway}.png`. Suite 535 passed,
1 skipped.

**Corner cutting is in too** (`Fillet` / `Round` / `RoundAll`): where pulling a handle cannot make
the radius, the corner node is replaced by the two ends of a real arc. C and Shift+C do this
automatically, and the status line says how many corners were cut. The dog-leg that stopped at
3.7 m now rounds to a true 4 m.

**A bend readout bug went with it, and it mattered:** radii were taken in float from the world's
absolute coordinates, so every bend on the island read about 7% tight and the tool warned about
turns it had just built properly. Sampling relative to the segment's own first node fixed it —
see `DECISIONS.md`, 2026-09-22.

**The panel now carries all of it**: a Min bend field, a Smooth button, a Hold the grade toggle, a
typed Node at ... m, an over the ground ... m box, and a Bends line beside the Grades line.
`RoadsHost.SelectNode` picks which node those fields act on without a mouse.

**Nothing is left open on the road tool**, and the crew has now built one: `5_built.png` is a
laid, fully paved road with two 4 m arcs, 0 designations left after 265 game seconds. The capture
marks a quarry as well as a tip, because a road that needs fill and has no quarry stops the crew
dead (they say so plainly: "Nothing to fill with: mark a Quarry").

**The pit's "rethink loop" was a wrong diagnosis and is now closed.** Measured: 0 Auto cancels in
240 s, rethinks on 0.6% of ticks. The real fault is that `PlaceRampStep` never looks at the last
step of its corridor, which in a pit is the only steep one — so no way in is ever cut and the pit
stops at four fifths. Reproduction is `[Ignore]`d in `DeepPitTests` with the numbers; the fix
needs a ruling from Ronan on what a ramp step into the target cell should cut. See `DECISIONS.md`,
2026-09-22.

**Road tools as a ramp to a lower level** (Ronan, 2026-09-23) — measured, see `DECISIONS.md`. Yes
for anywhere with room (3 m down wants 25 m of run at 12%, and Max grade is a field so it can be
drawn steeper), no as a way round the pit fault: a 100% ramp moved 10.75 of 42.38 m³ against about
8 with none, because the deep end of a drawn ramp is itself a cut behind the same wall.

**The big cut was never stalled — that claim was wrong and is corrected** (`DECISIONS.md`,
2026-09-23). Rate per 100 game s: 2.88, 2, 2.13, 1.88, 1.75, 1.5 m³ — normal throughput on a
1200 m³ hole, declining only as the haul lengthens. Benching already does the face model Ronan
described: cells come off a metre at a time from ground the crew can stand on. The ramp fault is
real but narrow: it only bites on a block too small to hold a bench (seven cells wanting three
metres).

**Next, and Ronan's other half:** cutting a hill down for fill rather than digging a hole in a
plain (Trial B). The crew start on top of the material there, which is the case the reach rules
handle best — worth running before any ramp ruling, since it may show only the descent is broken.

**Still open from the digging run:** heap terracing on the half-metre step grid, and trials D and
F unrun.

## Parked: deep-water tiles (2026-09-23)

The sea sheet is 169 tiles at one resolution — a vertex every 2 m across the whole disc, 1.1M
triangles. Honest (the disc is 90% water) but larger than it needs to be: only the water near a
shore needs that spacing, because the short waves and the breaking bands live there. Coarser tiles
out in deep water would cut it several-fold.

The catch, and why it is parked rather than done: the shader displaces the surface per vertex, so a
seam between a 2 m tile and an 8 m one cracks unless the fine edge is stitched to the coarse one or
the displacement is forced to agree along it. Needs care, not a flag.

Everything it would build on is already in place: `WaterView.EnsureTiles` makes the tiles and
`BuildSea(mesh, firstColumn, lastColumn, firstRow, lastRow)` takes a range, so a per-tile step is a
small change to those two.

## Terraform tool — where it stands (2026-09-23)

Slices A–H are in and the tool answers the whole of what Ronan asked for. Verified on the real
island in play, not only in tests: four shapes over 2,803 cells planned in **1.1 ms**, a pit of
1,055 m³ in four benches, a heap holding 418.9 m³, and the crew posted to the pit's site.
`Screenshots/Terraform/` has the pictures.

**Left, and neither is part of the ask:**

- **A pit proposing its own haul road.** The ribbon machinery is there; it would descend the inside
  of the outline at `RoadPlanner.DefaultMaxGrade` and cut a corridor through the benches. It may also
  settle the `PlaceRampStep` blind spot below.
- **Nothing in this game persists.** Not landforms, not roads, not designations, not the terrain —
  the world is generated from a seed each time play starts and `RoadNetwork.ToJson` is used only by
  its own tests. Which layers deserve a save file is Ronan's call, so it has been left alone
  deliberately rather than invented inside a terraform slice.

**Also noted while building:** `Landform.Spoil` is a `MaterialId`, which Unity's serialisation skips
(warning UAC1001). It costs nothing today because nothing is serialised, and would need an int field
the day a save file exists.

## Cleanup pass, 2026-09-23 — what was done and what was left

Done, all with 608 tests green and the game verified running (133 fps, six units working, nothing in
the console):

- **The build shipped the wrong scene.** `Assets/Scenes/SampleScene.unity` — Unity's URP template —
  was the only scene in the build settings. It now ships `TerrainSandbox`.
- **Twenty of twenty-seven Editor files were one-off screenshot scripts** for finished slices, 4,000
  lines, referenced by nothing, and the source of every deprecation warning in the project. Removed;
  git has them. `Feel Capture`, the test runner, the texture generators, the noise, the preview menu
  and the site trials stay.
- Removed `Assets/_Recovery/0.unity` (a crash-recovery snapshot of an older game scene, committed by
  accident) and the sample scene. `Assets/Settings` was left alone — `SampleSceneProfile` is
  referenced by both render pipeline assets and is live despite the name.
- `Landform.Spoil` now says it is not serialised instead of being dropped silently.
- The nearest-cell ground lookup was copied in `RoadsHost` and `TerraformHost`; it is
  `TerrainSpace.GroundAt` now. `BrushCursorView`'s was *not* a third copy — it blends four cells so
  the cursor ring does not stair-step — and is now called `SmoothGroundAt`.
- `PlayerTools.UnlockHeight` removed: a second door to `HeightLocked` that nothing opened.
- `TerrainPreviewMenu.cs` → `TerrainPreview.cs`; every file declares its namesake again.
- Added `README.md`, with every claim checked against the code.

**Left alone deliberately, for Ronan to call:**

- **Two dead APIs that still have tests.** `Terrain/Runtime/TerrainBrush.cs` (superseded by
  `Units/Runtime/Excavation.cs`) and `Blueprints.PlanRoad` / `SteepestGrade` / `LegGrade` / `RoadPoint`
  (superseded by `RoadPlanner` + `RoadSpline`). Both are now documented as superseded so nobody
  starts from them, but deleting them means deleting their tests, and that is not a call to make
  while you are away. Say the word and both go.
- **A handful of members nothing references at all** — `TerrainSpace.OnSurface`/`ToCells`,
  `IslandMap.StageSummary`, `TerrainView.RiverWorldPoints`, `WaterSettle.Batches`,
  `SmoothedTerrainRenderer.CornerNormal`, `RoadNetwork.MoveNode`, `RoadsHost.SelectNode`. Each is
  small and part of an otherwise coherent set, so removing them is tidying rather than fixing.
  `GroundEffects.Collapse` is in that list too and should **stay**: it is part of the dig-and-tip
  brief and is waiting for something to detect a collapse.
- **The game is still called `TinyDigger V1` by `DefaultCompany`.** Product and company name are
  Ronan's to choose, so they are untouched; they show up in the window title, the player prefs path
  and anything built.
- **`CrewUnit.cs` is 2,308 lines**, more than twice anything else, and wants splitting along its job
  kinds. That is a real refactor with real risk, not a cleanup.
