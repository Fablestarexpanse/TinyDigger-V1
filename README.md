# TinyDiggers

A little earthmoving game. You mark out the ground you want — dig it down, fill it up, cut a road
through it, draw the shape a whole site should take — and a crew of small robots goes and moves the
material. Nothing happens by magic: every cubic metre that leaves a hole is carried somewhere and
tipped, and the ground slumps to the angle its material actually stands at.

The world is an island floating in space: a 3,104 × 3,104 grid of half-metre cells, each a stack of
material layers, ringed by a brutalist dam wall.

## Running it

Unity **6000.6.1f1**, URP. Open the project and load
`Assets/TinyDiggers/Scenes/TerrainSandbox.unity` — that is the game, and the only scene in the build
settings. Press Play; the island generates from a seed, which takes a moment.

### Controls

Tools are on the toolbar and on the number keys:

| Key | Tool | |
|---|---|---|
| 1 | Select | click or drag over units; right-click sends them |
| 2 | Dig | paint ground to be dug down to H |
| 3 | Fill | paint ground to be filled up to H |
| 4 | Dump Zone | drag out where spoil may be tipped |
| 5 | Level | drag a pad to H, or a ramp |
| 6 | Road | click points, Enter to lay it |
| 7 | Clear | drag over marks to take them off |
| 8 | Quarry | drag where the crew may dig for fill material |
| 9 | Terraform | draw the shape you want the ground to be |

**H** is the target height every tool works to: it follows the cursor until PageUp/PageDown moves it,
which locks it. `[` and `]` resize a brush. Alt-click samples the ground height into H. **M** hides
the coloured marks so you can see what the crew actually built. **F2** opens the settings panel.
Ctrl+Z and Ctrl+Y undo and redo what you have marked.

Inside the Terraform tool: **A** area, **H** heap, **P** pit, **R** ribbon, **B** brush (press again
to change what the brush does). Click corners, Enter commits, right-click takes a whole shape away.
With units selected, right-clicking a committed shape posts them to it — they then work that site
and nothing else.

## How the code is laid out

Four runtime assemblies, each with its own tests beside it, depending only downwards:

```
Terrain      the grid, the material table, the island generator, slump physics
  Units      designations, the crew, jobs, pathfinding, roads, landforms
    Presentation   meshing the terrain, water, grass, the dam
      Interaction  tools, the toolbar, panels, the camera
```

- **`Terrain/Runtime`** — `TerrainGrid` is the model: columns of `Layer`s, a surface height per
  cell, and events when a cell changes. `MaterialTable` holds what each material weighs, bulks and
  stands at. Nothing here knows about the crew.
- **`Units/Runtime`** — `DesignationMap` is what the player has asked for, per cell: dig to a height,
  fill to a height, a dump-zone cap, a quarry floor, a site. `CrewUnit` is one machine and its job
  loop; `JobDispatcher` hands out and claims work. `Roads/` and `Landforms/` turn drawn shapes into
  designations.
- **`Presentation`** — draws the model and never changes it.
- **`Interaction`** — `PlayerTools` is the hands: one tool active, the left button applies it. Tools
  with their own state get a host component (`RoadsHost`, `TerraformHost`).

The rule that keeps this straight: **a plan is a statement of intent, not an edit.** Drawing a road
or a landform writes designations; the crew are the only thing that moves material.

## Tests

608 edit-mode tests pass, across Units, Terrain, Presentation and Interaction. Run them from the
menu:

**TinyDiggers → Run EditMode Tests**

It prints one line to the console: `TESTS PASS passed=N failed=0 …`. Most of the game's rules are
plain C# with no scene behind them, on purpose, so they can be tested this way.

Other menu entries: **Terrain Preview**, the two texture generators, **Feel Capture** (ten seconds of
a digger and a dumper working, for judging whether material looks like a machine moved it), and the
**Site Trials**, which run a crew against a set brief and report how much they shifted.

## The written record

- **`DECISIONS.md`** — what was built, why, and what was tried and rejected. Read it before
  reopening a settled question; several are settled for reasons that are not obvious from the code.
- **`TERRAIN_REFERENCE.md`** — the design intent for the terrain. Read before touching
  `Terrain/` or the renderers.
- **`NEXT.md`** — what is in flight and what is parked, with the reason it is parked.

## Known, and deliberate

- **Nothing persists.** The world is generated from a seed each time you press Play, and roads,
  designations and landforms live only in that session. There is no save system, and which layers
  deserve one is an open design question rather than an oversight.
- `CrewUnit.cs` is 2,300 lines and wants splitting along its job kinds.
