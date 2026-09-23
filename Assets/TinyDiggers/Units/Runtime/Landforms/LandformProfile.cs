using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// The shape a heap or a pit is meant to take, worked out from how deep into the outline each
    /// cell lies (<see cref="LandformRaster.Inside"/>).
    ///
    /// This is what stops dump sites and quarries being a rectangle with one number on it (Ronan,
    /// 2026-09-23: they are "primitive"). The data was never the problem: `DesignationMap` has always
    /// stored a **cap and a floor per cell**, `CrewUnit` has always honoured both per cell, and undo
    /// has always carried them. Only the tools were primitive — a dragged rectangle setting the same
    /// number everywhere, so a heap came out a flat-topped slab and a pit a box.
    ///
    /// Both profiles follow the lie of the land rather than one height, because the inside-distance
    /// walk carries the ground at the nearest point outside the outline with it.
    /// </summary>
    public static class LandformProfile
    {
        /// <summary>
        /// The heap a shape describes: rising from its outline at the spoil's angle of repose until
        /// it reaches the crown, so it is a real heap with batter sides rather than a slab.
        ///
        /// **The crown is not a wall** (Ronan's ruling): the zone is written without a hard cap, so
        /// the crew keep tipping and the heap spreads at its own angle once it is full. The profile
        /// is what the ghost draws and what <see cref="Capacity"/> counts — *this is what it holds
        /// before it starts spreading* — not a limit anybody is held to.
        /// </summary>
        public static void Heap(TerrainGrid grid, Landform form, IReadOnlyList<InsideCell> inside,
            Dictionary<int, float> into)
        {
            into.Clear();
            if (grid == null || form == null || inside == null)
                return;

            var slope = Mathf.Tan(grid.Materials.Get(form.Spoil).AngleOfRepose * Mathf.Deg2Rad) * grid.CellSize;
            foreach (var cell in inside)
            {
                var top = Mathf.Min(form.Height, cell.EdgeGround + cell.Distance * slope);
                into[cell.Cell] = Snap(grid, top);
            }
        }

        /// <summary>
        /// The pit a shape describes: benches stepping down from the rim, each
        /// <see cref="Landform.BenchWidth"/> cells wide and <see cref="Landform.BenchHeight"/> metres
        /// deep, and never cut below the floor the player asked for.
        ///
        /// Benching is not decoration. A face the crew cannot stand on is a face they cannot dig,
        /// and a pit taken straight down to its floor is a wall; this is the same reason the
        /// dispatcher benches a big cut by itself.
        /// </summary>
        public static void Pit(TerrainGrid grid, Landform form, IReadOnlyList<InsideCell> inside,
            Dictionary<int, float> into)
        {
            into.Clear();
            if (grid == null || form == null || inside == null)
                return;

            var benchHeight = Mathf.Max(grid.HeightStep, form.BenchHeight);
            var benchWidth = Mathf.Max(1f, form.BenchWidth);
            foreach (var cell in inside)
            {
                // Which bench this cell is on, counting in from the rim. The first cells inside the
                // outline are bench zero and sit at the ground, so a pit starts at the land it is
                // dug into rather than a step below it.
                var bench = Mathf.Floor((cell.Distance - 1f) / benchWidth) + 1f;
                var floor = Mathf.Max(form.Height, cell.EdgeGround - bench * benchHeight);
                into[cell.Cell] = Snap(grid, floor);
            }
        }

        /// <summary>
        /// Cubic metres a heap still holds before it reaches the profile it was drawn as. Falls by
        /// itself as spoil arrives, so one formula answers both "holds 1,840 m³" and "1,190 m³ left".
        /// </summary>
        public static float Capacity(TerrainGrid grid, IReadOnlyDictionary<int, float> caps)
        {
            if (grid == null || caps == null)
                return 0f;
            var total = 0f;
            foreach (var pair in caps)
            {
                var x = pair.Key % grid.Width;
                var z = pair.Key / grid.Width;
                if (!grid.IsGround(x, z))
                    continue;
                total += Mathf.Max(0f, pair.Value - grid.GetSurfaceHeight(x, z));
            }

            return total * grid.CellArea;
        }

        /// <summary>Cubic metres a pit still has in it above its floor.</summary>
        public static float Reserves(TerrainGrid grid, IReadOnlyDictionary<int, float> floors)
        {
            if (grid == null || floors == null)
                return 0f;
            var total = 0f;
            foreach (var pair in floors)
            {
                var x = pair.Key % grid.Width;
                var z = pair.Key / grid.Width;
                if (!grid.IsGround(x, z))
                    continue;
                total += Mathf.Max(0f, grid.GetSurfaceHeight(x, z) - pair.Value);
            }

            return total * grid.CellArea;
        }

        /// <summary>How many benches deep a pit's profile goes, for the readout.</summary>
        public static int Benches(TerrainGrid grid, Landform form, IReadOnlyDictionary<int, float> floors)
        {
            if (grid == null || form == null || floors == null || floors.Count == 0)
                return 0;
            var deepest = 0f;
            foreach (var pair in floors)
            {
                var x = pair.Key % grid.Width;
                var z = pair.Key / grid.Width;
                if (grid.IsGround(x, z))
                    deepest = Mathf.Max(deepest, grid.GetSurfaceHeight(x, z) - pair.Value);
            }

            return Mathf.CeilToInt(deepest / Mathf.Max(grid.HeightStep, form.BenchHeight));
        }

        static float Snap(TerrainGrid grid, float height) =>
            grid.HeightStep > 0f ? Mathf.Round(height / grid.HeightStep) * grid.HeightStep : height;
    }
}
