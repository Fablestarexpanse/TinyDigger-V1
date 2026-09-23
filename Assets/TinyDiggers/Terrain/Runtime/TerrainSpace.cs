using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Cells to metres and back, in the terrain's local space (x and z in metres, y is height).
    /// Everything that places something on a cell, or finds the cell under a point, goes through
    /// here, so the cell size lives in one place (<see cref="TerrainGrid.CellSize"/>).
    /// </summary>
    public static class TerrainSpace
    {
        /// <summary>The centre of cell (x, z) at height <paramref name="y"/>.</summary>
        public static Vector3 CellCentre(TerrainGrid grid, int x, int z, float y = 0f) =>
            new Vector3((x + 0.5f) * grid.CellSize, y, (z + 0.5f) * grid.CellSize);

        /// <summary>The centre of cell (x, z) on its surface.</summary>
        public static Vector3 OnSurface(TerrainGrid grid, int x, int z) =>
            CellCentre(grid, x, z, grid.GetSurfaceHeight(x, z));

        /// <summary>Corner (i, j) of the cell lattice, which runs 0..Width by 0..Height.</summary>
        public static Vector3 Corner(TerrainGrid grid, float i, float j, float y) =>
            new Vector3(i * grid.CellSize, y, j * grid.CellSize);

        /// <summary>The cell under a local point; may be out of bounds.</summary>
        public static Vector2Int CellAt(TerrainGrid grid, Vector3 local) =>
            new Vector2Int(Mathf.FloorToInt(local.x / grid.CellSize), Mathf.FloorToInt(local.z / grid.CellSize));

        /// <summary>A local point in fractional cell coordinates (for smooth things like the camera).</summary>
        public static Vector2 ToCells(TerrainGrid grid, Vector3 local) =>
            new Vector2(local.x / grid.CellSize, local.z / grid.CellSize);

        /// <summary>Cells in <paramref name="metres"/>, at least 1.</summary>
        public static int Cells(TerrainGrid grid, float metres) => Mathf.Max(1, Mathf.RoundToInt(metres / grid.CellSize));

        /// <summary>
        /// The ground under a point given in cells: the surface of the cell it falls in, clamped to
        /// the map, and sea level where there is no ground — which is what a tool wants when the
        /// cursor runs off the edge of the island.
        ///
        /// The nearest cell, not a blend between four. Something drawing a smooth cursor wants the
        /// blend and has its own; a tool placing a node on a cell wants the cell it is on.
        /// </summary>
        public static float GroundAt(TerrainGrid grid, Vector2 cells)
        {
            if (grid == null)
                return 0f;
            var x = Mathf.Clamp(Mathf.FloorToInt(cells.x), 0, grid.Width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(cells.y), 0, grid.Height - 1);
            return grid.IsGround(x, z) ? grid.GetSurfaceHeight(x, z) : World.SeaLevel;
        }
    }
}
