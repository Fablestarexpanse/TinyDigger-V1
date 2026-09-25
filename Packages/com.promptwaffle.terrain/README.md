# PromptWaffle Terrain

Editable terrain for games that dig, fill and reshape the ground. Each cell of a grid holds a column
of material layers (bedrock, dirt, sand, rock, ...). The terrain can be dug and filled, stamped with
heightmaps, and slumps loose material to its angle of repose. It also provides surface heights,
erosion, picking and cell reports. Plain C#, with no rendering and no game rules.

Extracted from TinyDiggers (2026-09-25), which is still where it is developed. Planned companion
packages:

| Package | What |
|---|---|
| `com.promptwaffle.terrain` | this: grid, layers, material table, dig/fill, slump, erosion, surface, stamps runtime |
| `com.promptwaffle.terrain.generation` | island, rivers, seabed, ore deposits (optional) |
| `com.promptwaffle.terrain.urp` | smoothed renderer, triplanar/height-blend shader, texture sets, hollows, grass |
| `com.promptwaffle.terrain.stamps` | stamp importer, heightmap cleaning tool, god-mode stamp tool |
| `com.promptwaffle.terrain.water` | bridge to `com.promptwaffle.dynamicwater`: ground feed, settling, springs |

Status: step 1 of the extraction. The code has moved and been renamed, and it still carries
TinyDiggers' material list and island generator, which later steps split out.
