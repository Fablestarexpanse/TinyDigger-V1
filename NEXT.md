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

**Open, and genuinely Ronan's call:**

1. **A grade lock** — pick 4% and let following nodes work out their own heights, rather than
   setting each node by hand. This is the big one for laying long roads.
2. **A third height mode: hold N metres *above the ground*** — a causeway that follows the terrain
   at a constant clearance. Neither of the two existing modes does this.
3. **Typing a number.** Height is nudged by scrolling a node; there is no way to say "ten metres".

**Worth knowing before designing around raised roads:** a 4 m bank cost **316 m³** of fill, and the
embankment sprawls about twelve cells either side of the road. That is correct at dirt's angle of
repose and it is a lot. If raised roads are meant to be common, they may want a steeper built
batter or a retaining wall rather than a natural slope.

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
