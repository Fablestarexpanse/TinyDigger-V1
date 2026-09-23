# NEXT

Working notes for the autonomous run Ronan asked for on 2026-09-22: *"test a wide variety of
digging and ramps and laying roads, digging through hills, smoothing, how to get the material to
move like dirt and rock, how to show material being dumped … find improvements and uses for
digging we haven't figured out, and work on them while I am gone."*

Rulings and findings go to `DECISIONS.md` as they happen; this file is only the breadcrumb — what
is done, what is running, what comes next.

## NEXT

**A path that is re-planned every tick.** The unit sent to cut the ramp has a good eight-waypoint
path and never gets past the first of them:

```
[0 Moving at (15.50, 21.39) ... waited 0.0 path 7/8 :: Moving to ramp (14, 13) to 5.5 m]
```

Seven waypoints still ahead, nothing blocking it, position alternating between z 21.46 and 21.39
for ever. That is `Replan` putting `_pathIndex` back every tick. **Log `_repath` and `_pathIndex`
next to the path length and one run should name it** — the suspects are the designation `Changed`
event (the ramp step being re-placed on every rethink) and `OnCellChanged`, and neither ought to be
firing on a site where nothing is being dug.

This is the last thing between the two-step pit and finishing, and it is very likely the same cause
as the "waiting" that has been mistaken for congestion three times: a unit whose path is reset
faster than it can walk it looks exactly like a unit that is stuck.

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
