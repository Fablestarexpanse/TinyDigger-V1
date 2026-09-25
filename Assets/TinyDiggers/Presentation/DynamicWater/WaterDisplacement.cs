using System.Collections.Generic;
using PromptWaffle.DynamicWater;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Water over ground that has just been raised, all at once, by a god-mode stamp.
    ///
    /// The simulation keeps each cell's *depth* when the ground under it changes, which is right for
    /// a dig or a tip — the water falls or rises with the bed by a quarter-metre and flows on. It is
    /// wrong for a stamp that lifts land metres out of the sea: the sea came up with it and poured
    /// off the new ground as a flood (2026-09-24, 1,177 of 3,800 samples on a showcase pad still wet
    /// twenty seconds on). Raised land displaces the water instead: the surface stays where it was,
    /// so the depth drops by the rise, and land lifted clear of the surface is dry.
    /// </summary>
    public static class WaterDisplacement
    {
        /// <summary>
        /// Keeps the water surface where the ground rose, in every water zone. Returns how many zone
        /// cells lost water. Reads the depths back from the GPU, so it is for a tool's click, not a
        /// frame loop.
        /// </summary>
        public static int KeepSurface(TerrainView terrain, IReadOnlyList<(int X, int Z, float Change)> changes)
        {
            if (terrain == null || terrain.Grid == null || changes == null || changes.Count == 0)
                return 0;

            var grid = terrain.Grid;
            var rises = new Dictionary<int, float>();
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var (x, z, change) in changes)
            {
                if (change <= 0f)
                    continue;
                rises[z * grid.Width + x] = change;
                minX = Mathf.Min(minX, x);
                minZ = Mathf.Min(minZ, z);
                maxX = Mathf.Max(maxX, x);
                maxZ = Mathf.Max(maxZ, z);
            }

            if (rises.Count == 0)
                return 0;

            var cell = grid.CellSize;
            var worldMin = terrain.transform.TransformPoint(new Vector3(minX * cell, 0f, minZ * cell));
            var worldMax = terrain.transform.TransformPoint(new Vector3((maxX + 1) * cell, 0f, (maxZ + 1) * cell));
            var drained = 0;
            foreach (var zone in Object.FindObjectsByType<WaterZone>())
            {
                var simulation = zone.Simulation;
                if (simulation == null)
                    continue;
                var desc = simulation.Desc;
                var x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(worldMin.x, worldMax.x) - desc.Origin.x) / desc.CellSize));
                var z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(worldMin.z, worldMax.z) - desc.Origin.y) / desc.CellSize));
                var x1 = Mathf.Min(desc.Width - 1, Mathf.CeilToInt((Mathf.Max(worldMin.x, worldMax.x) - desc.Origin.x) / desc.CellSize));
                var z1 = Mathf.Min(desc.Height - 1, Mathf.CeilToInt((Mathf.Max(worldMin.z, worldMax.z) - desc.Origin.y) / desc.CellSize));
                if (x0 > x1 || z0 > z1)
                    continue;

                var depths = simulation.ReadDepthsImmediate();
                var changed = 0;
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var i = z * desc.Width + x;
                        if (depths[i] <= 0f)
                            continue;
                        var centre = desc.CellCentre(x, z);
                        var local = terrain.transform.InverseTransformPoint(new Vector3(centre.x, 0f, centre.y));
                        var at = TerrainSpace.CellAt(grid, local);
                        if (!grid.InBounds(at.x, at.y) || !rises.TryGetValue(at.y * grid.Width + at.x, out var rise))
                            continue;
                        depths[i] = Mathf.Max(0f, depths[i] - rise);
                        changed++;
                    }
                }

                if (changed == 0)
                    continue;
                simulation.SetDepths(depths);
                drained += changed;
            }

            return drained;
        }
    }
}
