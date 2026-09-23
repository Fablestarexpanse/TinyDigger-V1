# NEXT

Working notes for the autonomous run Ronan asked for on 2026-09-22: *"test a wide variety of
digging and ramps and laying roads, digging through hills, smoothing, how to get the material to
move like dirt and rock, how to show material being dumped … find improvements and uses for
digging we haven't figured out, and work on them while I am gone."*

Rulings and findings go to `DECISIONS.md` as they happen; this file is only the breadcrumb — what
is done, what is running, what comes next.

## NEXT

One fault left, reproducible in a one-second test (`DeepPitTests.AShallowPitReportsWhereItStops`,
which prints its diagnosis every run):

**Make the ramp corridor honest about where a unit may put its feet.**
`JobDispatcher.PlaceRampStep` plans a corridor from the unit to the cell it cannot reach and looks
along it for the first step too steep to drive. In a pit there is no such step: the undug middle
stands at the same height as the ground outside, so the corridor runs straight over the top of the
block and the planner answers "nowhere to stand beside". What actually stops the unit is
`CrewUnit.CanStandHere` — it may not stand on ground still to be dug — and the corridor's
passability test knows nothing about that (it is about designations and regions).

Make a cell the unit may not stand on impassable to the corridor, and it is forced round to the
ring already cut, where there *is* a metre step to take down. That is the ramp. Mind the two
endpoints: the target is itself a cell the unit cannot stand on, and so is the high side of the
step it is about to cut, so the rule has to apply to the middle of the corridor rather than its
ends.

Fix that and the two-step pit should finish; then carry on down the trial list below.

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
