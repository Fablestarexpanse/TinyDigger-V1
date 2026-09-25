# PromptWaffle Terrain URP

Draws `com.promptwaffle.terrain` in the Universal Render Pipeline:

- **Renderers.** `SmoothedTerrainRenderer` is a smoothed heightfield in culled chunks with levels
  of detail. `WalledTerrainRenderer` draws stepped columns. Both rebuild only the chunks an edit
  touches. `TerrainHeightLag` eases dug or tipped ground into place instead of jumping.
- **Look.** `TerrainDetail` pairs the `PromptWaffle/Terrain Triplanar` shader with a
  `TerrainTextureSet` (one painted tile per material, optional cut-face and lush/dry variants,
  height in the albedo's alpha). The shader:
  - blends materials by height, so stones break through grass;
  - mixes a rotated second scale and a far third scale against tiling;
  - darkens hollows and lifts ridges from `TerrainHollowMap`;
  - reads each cell's materials from `TerrainCellMap`.

The game supplies the texture set (its own tiles) and a `MaterialTable` from the core package.

Extracted from TinyDiggers, 2026-09-25 (step 3). Grass, and TinyDiggers' `TerrainView` component
that ties generation, rendering and water together, are still in the game.
