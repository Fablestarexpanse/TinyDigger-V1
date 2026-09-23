# NEXT

Working notes for the autonomous run Ronan asked for on 2026-09-22: *"test a wide variety of
digging and ramps and laying roads, digging through hills, smoothing, how to get the material to
move like dirt and rock, how to show material being dumped … find improvements and uses for
digging we haven't figured out, and work on them while I am gone."*

Rulings and findings go to `DECISIONS.md` as they happen; this file is only the breadcrumb — what
is done, what is running, what comes next.

## NEXT

**Road cut (Ronan's current ask) — working, two things left.**

`TinyDiggers/Road Cut Capture` lays forty cells of road at width five, held **level** through a 3 m
soil rise, with a 441-cell tip capped three metres over its own ground and a 169-cell quarry. The
spoil now reads as tipped dirt: a rounded mound of bare material, nothing over its cap, and the
crew works to about a quarter of the designations left.

1. **A digger and its dumper lose each other.** `[4 Full: waiting for a hauler]` beside
   `[5 Idle: no digger to serve]`. That is trial F arriving on its own — look at
   `JobDispatcher.AssignDigger` / `HaulerFor` / `DiggerFor` and what clears a pairing.
2. **The heap terraces on the half-metre step grid** rather than rounding. Loose spoil at its angle
   of repose wants a slope of about 0.35 m a cell and the grid cannot express less than 0.5, so
   `AngleOfReposeSimulator` can never settle the last step. Presentation, most likely, not physics.

The slump pacing (`TerrainView.SlumpTilesPerSecond`, ninety, unscaled) needs judging live — a still
cannot show it.

**Pit (earlier thread, still open).** A unit is told to start again every tick; see below and
DECISIONS.md. Not the road's problem — the road crew's paths advance normally.

### Done since the last note

- The wedge is fixed. "Moved" now means a quarter of a cell rather than any twitch, so the escape
  hatch (stand still long enough and the keep-apart radius stops applying for one step) actually
  fires. The pair at a 3 m haul went from 0.775 m³/min and never finishing to **7.998 and done in
  118 s**, with the mech digging 58% of its life instead of 6%.
- `LoadSizingTests` is the bench for unit sizes: m³ a game minute at a given haul, printed every
  run. Ladder as it stands — robot 0.80 / 0.45, mech alone 1.48 / 0.91, **pair 8.00 / 5.79** at
  3 m / 10 m.

## Plan

| # | Trial | What it is really asking | State |
| --- | --- | --- | --- |
| A | Deep pit, robots only | Does the auto-ramp cut a way in? | Two faults found and fixed, two named above |
| B | Cut through a hill | Does a corridor through a rise complete, or stall on its own spoil? | Written in `SiteTrials`, not yet run |
| C | Smoothing | Level a bumpy patch: how flat is flat, and does it terminate? | Written in `SiteTrials`, not yet run |
| D | Dirt against rock | Time, bulking and heap shape: does rock behave like rock? | To do |
| E | The heap | What a tipped load looks like, and whether it slumps to a sane angle | To do |
| F | Haulage balance | One dumper per digger is known to starve the digger; measure it | To do |

## How to run a trial

**Behaviour questions go in edit-mode tests, not play mode.** `TinyDiggers/Run EditMode Tests`,
then `grep -a "TESTS\|two-step pit:\|deep pit:" Logs/Editor.log | tail`. A test answers in a second
and answers the same every time.

**Play mode is for pictures only.** `TinyDiggers/Site Trials`, or one trial at a time
(`Site Trial A (deep pit)`, `B (cut a hill)`, `C (smooth)`); shots land in `Screenshots/Trials`.

## Standing traps (learned the hard way, do not re-learn)

- **An editor behind another window does not tick.** `EditorApplication.timeSinceStartup` moved 33
  seconds across 19 minutes of wall clock; the game clock only advanced while the MCP bridge poked
  it. Never budget or measure a play-mode run in real seconds — use `Time.time`.
- `Time.maximumDeltaTime` is a third of a second, so twenty times speed is about the ceiling at
  sixty frames a second unless something raises it. `CrewView` now steps the crew in tenth-second
  slices whatever the frame was, so raising it is safe.
- **A hand-built fixture is not the game.** A `JobDispatcher` has `Benching` and `AutoRamp` **off**;
  a `CrewUnit` reaches **half as far** as one in the scene. The scene sets both every frame. A test
  that skips them is testing a different crew.
- **A serialized field keeps the value baked into the scene**, whatever the class default says. When
  a constant like `UnitLoads` changes, the scene's own copy has to be changed too.
- Editing anything under `Assets/` during a play capture triggers a domain reload and kills the run.
- A test grid must use the sandbox's half-metre cells: at a metre a single cut is 1 m³, more than
  any unit can carry, and units stall in ways that look like pathing bugs.
- **A load must hold at least one tip step plus one cut**, or a unit ends up with a remnant it can
  neither tip nor dig on top of. See `UnitLoads`.
