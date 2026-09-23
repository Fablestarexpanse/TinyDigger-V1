# NEXT

Working notes for the autonomous run Ronan asked for on 2026-09-22: *"test a wide variety of
digging and ramps and laying roads, digging through hills, smoothing, how to get the material to
move like dirt and rock, how to show material being dumped … find improvements and uses for
digging we haven't figured out, and work on them while I am gone."*

Rulings and findings go to `DECISIONS.md` as they happen; this file is only the breadcrumb — what
is done, what is running, what comes next.

## Plan

| # | Trial | What it is really asking |
| --- | --- | --- |
| A | Deep pit, robots only | Does the auto-ramp cut a way in? (Known suspect: "nowhere to stand".) |
| B | Cut through a hill | Does a corridor through a rise complete, or stall on its own spoil? |
| C | Smoothing | Level a bumpy patch: how flat is flat, and does it terminate? |
| D | Dirt against rock | Time, bulking and heap shape: does rock behave like rock? |
| E | The heap | What a tipped load looks like, and whether it slumps to a sane angle |
| F | Haulage balance | One dumper per digger is known to starve the digger; measure it |

## State

- **NEXT:** trial A is chasing one question — a 7x7 pit three metres deep stalls with about 29 of
  its 36.75 m³ still standing, every unit reading *Digging*, and **no auto ramp ever placed**. The
  histogram says Digging 71%, Waiting 11%, Parked 15%, Moving 2%, and only 70 cuts refused on
  arrival out of 5548 unit-seconds of digging — so the units are not thrashing, they are sitting
  in Digging and almost never landing a cut. The run in flight counts cuts landed and dig ticks
  (`CrewUnit.LandedCuts` / `DigTicks`) to say which. Menu: `TinyDiggers/Site Trial A (deep pit)`;
  read the verdict with `grep -a "Trial A ended" Logs/Editor.log`.
- Standing suspicion, to test once the counters land: as the pit deepens the ground outside stands
  more than a climb above the floor, `CanStandHere` stops letting anything stand inside, and from
  the rim a robot only reaches two steps down — so the pit can never go past about a metre. The
  ramp planner is never asked because the units still think they have a job.
- **Earlier:** A, second run. The first run reported "36 designations left, 0 auto ramp" and looked like
  the auto-ramp fault — but the crew were all still digging happily and a spoil heap had grown;
  the 120-second budget had simply run out. Lesson, now built into the harness: **a trial ends on
  progress, not on a stopwatch.** `Work` measures cubic metres outstanding across every live
  designation and stops on done / stalled (25 s with no progress) / cap (300 s), and says which.
- Trial menu items are now per-trial as well: `TinyDiggers/Site Trial A (deep pit)`, `B (cut a
  hill)`, `C (smooth)`, plus `Site Trials` for the lot.
- Suite green at 509 before this run began (`TinyDiggers/Run EditMode Tests`, look for `TESTS`).
- Captures: `TinyDiggers/Dig Capture`, `Road Capture`, `Units Capture`; shots in `Screenshots/Units`.

## Standing traps (learned the hard way, do not re-learn)

- Editing anything under `Assets/` during a play capture triggers a domain reload and kills the run.
- A test grid must use the sandbox's half-metre cells: at a metre a single cut is 1 m³, more than
  any unit can carry, and units stall in ways that look like pathing bugs.
- Loads must be whole height steps or a remnant can never be tipped.
