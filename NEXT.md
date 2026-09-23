# NEXT

Working notes for the autonomous run Ronan asked for on 2026-09-22: *"test a wide variety of
digging and ramps and laying roads, digging through hills, smoothing, how to get the material to
move like dirt and rock, how to show material being dumped … find improvements and uses for
digging we haven't figured out, and work on them while I am gone."*

Rulings and findings go to `DECISIONS.md` as they happen; this file is only the breadcrumb — what
is done, what is running, what comes next.

## NEXT

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

**Still open from the digging run:** heap terracing on the half-metre step grid, and trials B, D
and F unrun.
