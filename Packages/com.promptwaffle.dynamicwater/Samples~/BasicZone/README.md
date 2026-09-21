# Basic Zone

A 64 × 64 m water zone at 0.5 m cells over a procedural basin, with every kind of effector.

Open `BasicZone.unity` and press Play.

## Controls

- **Hold left mouse:** pour water where you point (20 m³/s).
- **Right click:** dig a crater. The water finds its way in.
- **R:** refill the zone from its start level.

## What is in the scene

| Object | Shows |
| --- | --- |
| **Basin** (`ProceduralBasinGround`) | Writing a ground provider: heights built in code, plus `RaiseChanged` when it is dug. The basin has an upper and a lower pool, a sill with a notch between them, an island and a spillway notch in the rim. |
| **Water Zone** (`WaterZone` + `WaterZoneRenderer`) | The zone fills both pools to 0 m on start. It uses `BasicZoneWaves` for a light swell. |
| **Spring** | A Source, 4 m³/s, into the upper pool. The upper pool rises until it spills through the sill's notch at 0.1 m. |
| **Drain** | A Drain, 0.5 m³/s, in the lower pool. |
| **Current** | A Force, 0.1 m/s², pushing east across the lower pool. |
| **Spillway** | An Overflow at the rim notch: crest 0.4 m, 6 m weir. It lets out only water above the crest. |
| **Buoys** (`SampleFloater`) | Reading the water from script. `WaterZone.TrySampleAny` gives the surface (simulation plus swell) and the current. |

## Needs

- **URP, with Opaque Texture on in the URP asset.** The water shader refracts the ground through it.
- **Input System or the legacy Input Manager.** Both work; the sample picks whichever is installed.
