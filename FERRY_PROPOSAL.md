# Landing craft: ferry proposal

Status: **proposal, not built.** Ronan's rulings so far (2026-09-24): the landing craft from
machine-forge is imported as a model now (`Art/Units/Forge/boat`, bobbing, props and ramp clips); the
ferrying gameplay is designed here first, API before code.

## What it is for

Rivers block the crew and creeks are waded (ruling). The island has lakes and offshore islets, and
supplies arrive at the dam terminals by ship. A landing craft moves machines across water that nothing
can drive through:

1. **Across open water**: to an islet, round a headland, over a lake.
2. **Not across rivers**: see "Rivers" below. The boat is too big to sail in one, and it is not a bridge.
3. **Later, from the rim**: a ship docks at a terminal and the landing craft brings its machines ashore.

## The boat, measured

At the game's size (x1.41, the same factor as the machines):

| | |
|---|---|
| Length / beam | 4.9 m (ramp up) / 2.87 m: about 10 x 6 cells |
| Draft | 0.14 m empty, 0.17 m loaded. It needs **0.35 m of water** with margin |
| Ramp | 0.65 m long (1.3 cells), hinged at the bow, lowered only when beached |
| Well | two lanes, 1.2 m wide each: **two machines**, one per lane |
| Loading | vehicles **reverse up the ramp** into a lane, so they drive off forwards (the forge's scene; its manifest text says bow-first, the scene is the one that was checked) |

Crew robots count by lane: proposed 3 robots per lane (0.41 m each).

## Rivers: no bridge (ruling)

**Ronan, 2026-09-24:** *"no bridge. If it can't go up river then it stays at beach; the excavator and
dozer go to it and can be loaded, then the boat can drive around in water and unload them someplace else
if needed."*

So the craft works **open water only**: the sea, lakes, and whatever water is wide and deep enough for its
hull. Rivers are 3 to 6 m wide against a 2.87 m beam and a 4.9 m length, so it never sails up one, and
nothing makes it a bridge. Where the water it can float in ends, it beaches, and the machines come to it.

## How it plays (proposed)

The boat is a **unit you command**, like the crew:

1. Select the landing craft and click a shore: it sails there and beaches. The ghost shows where it will
   beach, with the reason if it cannot (too shallow, too steep, no room for the ramp).
2. Select machines and right-click the beached craft: they drive to the ramp and reverse aboard, a lane
   each. A third waits its turn.
3. Select the craft and click another shore: it raises the ramp, backs off, sails, beaches, lowers the
   ramp, and the machines drive off forwards and stop a few cells inland, where the player gives them work.

**Later (slice E):** a move order to a place no land route reaches uses a craft automatically, if one is
free. That is the Captain of Industry feel, but it hides the boat's cost, so it comes after the manual
version has been played.

## Architecture

It follows the crew stack: plain C# in `TinyDiggers.Units` under test, and thin MonoBehaviours.

```csharp
// Units/Runtime/Ferry/WaterNav.cs
// Cells a hull of this beam and draft can float over: depth >= Draft + Clearance, and every cell within
// half the beam is too (a distance transform over the shallow cells). A* over those, rebuilt on a slow
// clock because live water moves. The land pathfinder is not used: it treats deep water as a wall.
sealed class WaterNav
{
    WaterNav(TerrainGrid grid, float draft, float halfBeamCells);
    bool Floats(int x, int z);
    bool TryFindRoute(Vector2Int from, Vector2Int to, List<Vector2Int> route);
    void Refresh();                        // after the water has settled or the ground changed
}

// Units/Runtime/Ferry/Landing.cs
// Where the craft beaches: the hull cells float, the bow is within ramp's reach of dry land, and the
// ground at the ramp foot is passable and gentle, with room behind it for a machine to line up and
// reverse aboard (the same back-in as the dump truck, PlanBackIn).
readonly struct Landing
{
    Vector2 Hull;        // where the craft's centre sits, in cells
    float Heading;       // bow toward the shore
    Vector2Int RampFoot; // the land cell the ramp rests on
    string Refusal;      // null when good; "too shallow", "bank too steep", "no room to line up"
}
static class LandingFinder
{
    static Landing Find(TerrainGrid grid, WaterNav nav, Vector2Int near, int searchRadius);
}

// Units/Runtime/Ferry/Ferry.cs: the boat as a unit, like CrewUnit
sealed class Ferry
{
    int Id; Vector2 Position; float Heading; float Speed;   // proposed 2 m/s
    FerryState State;    // Idle, Sailing, Beaching, RampDown, Loading, Unloading, RampUp, Leaving
    int[] Lanes;         // unit id in each lane, -1 when empty
    bool SailTo(Vector2Int shore);                 // finds a Landing and a route, or false with a reason
    void Tick(float dt);
}

// Crew side: two new jobs on CrewUnit, dispatched like Serve
//   Board:     drive to the ramp foot, line up, reverse up into the reserved lane
//   Aboard:    no cell, no traffic; Position and Heading follow the lane on the boat
//   Disembark: drive forward off the ramp to a free cell a few cells inland
// JobDispatcher: FerryOf(unitId), lane reservations, and CanMoveTo ignores units aboard.

// Presentation/Interaction
//   FerryView: places the boat prefab on the live water surface at the four float points
//              (machine.json), plays Bob always, Props while it moves, RampDown / RampUp.
//   Commands:  select craft + click shore; select machines + right-click craft.
```

## Slices

Each one tested, run in play, and committed on its own.

- **A: water.** `WaterNav` and `LandingFinder`, pure and under test: a hull will not go where it would
  ground, a landing on a cliff is refused with its reason, a gentle beach is found.
- **B: the boat sails.** `Ferry` and `FerryView`: one craft placed at start, sailing between two landings
  you click, beaching and lowering its ramp. The first thing to watch in play.
- **C: boarding and landing.** Board, Aboard and Disembark, with lane reservations: two machines cross
  a stretch of water and drive off.
- **D: the commands and ghost**, with the refusal reasons on screen.
- **E: automatic ferrying** for move orders with no land route.
- **Later:** ships at the rim terminals hand machines to the craft; a spoil barge variant.

## Tests (edit mode)

- A hull floats only where every cell under its beam is at least draft + clearance deep.
- A landing is refused on a bank steeper than a machine can climb, and says so.
- Two machines board a beached craft, one per lane, both facing the ramp; a third waits.
- A crossing ends with both machines on the far shore, off the ramp, and the craft empty.
- A unit aboard is never another unit's obstacle, and never counts as on the ground it floats over.

## Questions for Ronan

1. ~~Rivers~~: settled, open water only, no bridge (above).
2. **How is it ordered?** Proposed: commanded by hand first (craft to a shore, machines to the craft),
   automatic routing later.
3. **Where does the first one come from?** Proposed: one craft moored off the spawn beach at the start.
4. **Cargo:** proposed machines only (digger, dozer, dump truck) to start; crew robots later.
