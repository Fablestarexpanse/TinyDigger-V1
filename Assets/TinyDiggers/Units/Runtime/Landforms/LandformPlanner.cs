using System.Collections.Generic;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// What the ground around a shape does once the shape is built: a cut face stands back at the
    /// ground's own angle of repose, an embankment runs out at the spoil's.
    ///
    /// The physics is <see cref="RoadPlanner.Settle"/> and is not repeated here. What is here is the
    /// one thing that makes it affordable on an area rather than a ribbon.
    ///
    /// **Only a shape's rim can batter anything.** For a cell outside the shape, the cut it must
    /// come down to is `min(H + run · cutSlope)` over every planned cell, and the fill it must come
    /// up to is `max(H − run · fillSlope)`; both are decided by the *nearest* planned cell, and the
    /// nearest cell of a filled shape is always on its rim. An interior cell can only ever offer a
    /// longer run and therefore a weaker constraint.
    ///
    /// The difference is the whole cost of the tool. `Settle` probes (2·reach+1)² cells for every
    /// footprint cell it is given: at reach 14 that is 841 each, so a sixty by sixty pad is 3.6
    /// million probes a replan and the ghost stutters. Its rim is about 240 cells, and the cost stops
    /// growing with the area of the shape.
    /// </summary>
    public static class LandformPlanner
    {
        /// <summary>Cells a batter is allowed to reach, whatever the height difference.</summary>
        public const int MaxReach = 24;

        /// <summary>
        /// The ground around <paramref name="footprint"/> that would move once it is built, out to a
        /// reach worked out from how far the shape stands from the land it sits in.
        /// </summary>
        public static void Batters(TerrainGrid grid, IReadOnlyList<PlannedCell> footprint, MaterialId spoil,
            List<PlannedCell> faces, List<int> scratch = null)
        {
            faces.Clear();
            if (grid == null || footprint == null || footprint.Count == 0)
                return;

            var rim = scratch ?? new List<int>();
            var cells = new List<int>(footprint.Count);
            foreach (var cell in footprint)
                cells.Add(cell.Z * grid.Width + cell.X);
            LandformRaster.Perimeter(grid, cells, rim);

            var onRim = new HashSet<int>(rim);
            var edge = new List<PlannedCell>(rim.Count);
            var tallest = 0f;
            foreach (var cell in footprint)
            {
                if (!onRim.Contains(cell.Z * grid.Width + cell.X))
                    continue;
                edge.Add(cell);
                tallest = Mathf.Max(tallest, Mathf.Abs(cell.Height - grid.GetSurfaceHeight(cell.X, cell.Z)));
            }

            RoadPlanner.Settle(grid, edge, spoil, Reach(grid, spoil, tallest), faces);

            // Settle uses the footprint it is given for two jobs: as the cells that do the
            // battering, and as the cells that are already spoken for and must not be battered
            // themselves. Handing it the rim alone gets the first right and the second wrong — the
            // shape's own interior stops being planned ground and comes back as faces, 720 of them
            // where the slow way gives 460 (2026-09-23). The insides are dropped here instead, which
            // is exact: Settle works a cell out from its own previous value and the shape's
            // neighbours, never from another outside cell.
            var inside = new HashSet<int>(cells);
            for (var i = faces.Count - 1; i >= 0; i--)
                if (inside.Contains(faces[i].Z * grid.Width + faces[i].X))
                    faces.RemoveAt(i);
        }

        /// <summary>
        /// How far out a batter can possibly reach: the tallest step the shape makes, divided by the
        /// gentlest of the two slopes it could fall at. Deriving it beats a fixed number both ways —
        /// a shallow pad stops probing ground it could never touch, and a deep cut is not clipped
        /// short of where its face really ends.
        /// </summary>
        static int Reach(TerrainGrid grid, MaterialId spoil, float tallest)
        {
            var fill = Mathf.Tan(grid.Materials.Get(spoil).AngleOfRepose * Mathf.Deg2Rad) * grid.CellSize;
            var cut = Mathf.Tan(grid.Materials.Get(MaterialTable.Dirt).AngleOfRepose * Mathf.Deg2Rad) * grid.CellSize;
            var gentlest = Mathf.Max(0.01f, Mathf.Min(fill, cut));
            return Mathf.Clamp(Mathf.CeilToInt(tallest / gentlest) + 1, 1, MaxReach);
        }
    }
}
