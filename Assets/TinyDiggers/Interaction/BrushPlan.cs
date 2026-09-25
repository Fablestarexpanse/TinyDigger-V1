using System.Collections.Generic;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The round brush Dig and Fill paint with (Slice 17): which cells it covers, and what
    /// designating them to a height would cost. The tool, the panel's readout and the ghost disc
    /// all ask this, so they cannot disagree.
    /// </summary>
    public static class BrushPlan
    {
        public const int MinRadius = 1;
        public const int MaxRadius = 8;

        /// <summary>
        /// The ground cells within <paramref name="radius"/> cells of the centre (a disc, dx² + dz²
        /// at most radius²), into <paramref name="cells"/>, which is cleared first. Cells off the
        /// map or in the void are left out.
        /// </summary>
        public static void Cells(TerrainGrid grid, int centreX, int centreZ, int radius, List<Vector2Int> cells) =>
            // The disc itself lives in LandformRaster, where the plan's freehand brush can reach it
            // too: two brushes drawing different discs would be a bug waiting to be noticed.
            Units.LandformRaster.Disc(grid, centreX, centreZ, radius, cells);

        /// <summary>
        /// Cubic metres of ground above <paramref name="height"/> that digging the cells to it would
        /// take away (when <paramref name="cuts"/>), and of room below it that filling would take
        /// (when <paramref name="fills"/>). A cell under water cannot be dug, so it adds no cut.
        /// </summary>
        public static void Volumes(TerrainGrid grid, IReadOnlyList<Vector2Int> cells, float height, bool cuts, bool fills,
            out float cut, out float fill)
        {
            cut = 0f;
            fill = 0f;
            foreach (var cell in cells)
            {
                var surface = grid.GetSurfaceHeight(cell.x, cell.y);
                if (cuts && surface > height && !grid.IsWater(cell.x, cell.y))
                    cut += surface - height;
                if (fills && surface < height)
                    fill += height - surface;
            }

            cut *= grid.CellArea;
            fill *= grid.CellArea;
        }
    }
}
