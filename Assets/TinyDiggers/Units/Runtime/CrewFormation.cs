using System;
using System.Collections.Generic;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Where a group goes when the player sends it to one cell: each unit gets its own cell, as
    /// near the clicked one as there are cells to stand on, so they do not all pile onto it.
    /// </summary>
    public static class CrewFormation
    {
        /// <summary>
        /// Up to <paramref name="count"/> distinct cells nearest <paramref name="centre"/> (by
        /// straight-line distance, then row, then column) that pass <paramref name="canStand"/>,
        /// searching no further than <paramref name="maxRadius"/> cells out. Nearest first.
        /// </summary>
        public static List<Vector2Int> Targets(TerrainGrid grid, Vector2Int centre, int count,
            Func<int, int, bool> canStand, int maxRadius = 12)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (canStand == null)
                throw new ArgumentNullException(nameof(canStand));

            var candidates = new List<Vector2Int>();
            for (var dz = -maxRadius; dz <= maxRadius; dz++)
                for (var dx = -maxRadius; dx <= maxRadius; dx++)
                {
                    var x = centre.x + dx;
                    var z = centre.y + dz;
                    if (dx * dx + dz * dz <= maxRadius * maxRadius && grid.InBounds(x, z) && canStand(x, z))
                        candidates.Add(new Vector2Int(x, z));
                }

            candidates.Sort((a, b) =>
            {
                var da = (a - centre).sqrMagnitude;
                var db = (b - centre).sqrMagnitude;
                if (da != db)
                    return da.CompareTo(db);
                return a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x);
            });
            if (candidates.Count > count)
                candidates.RemoveRange(count, candidates.Count - count);
            return candidates;
        }

        /// <summary>
        /// Which target each unit takes: the unit nearest the group's goal picks first, taking the
        /// target nearest itself, and so on. Returns an index into <paramref name="targets"/> per
        /// unit, or -1 for a unit left without one (more units than targets).
        /// </summary>
        public static int[] Assign(IReadOnlyList<Vector2> units, IReadOnlyList<Vector2Int> targets, Vector2 goal)
        {
            var order = new int[units.Count];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;
            Array.Sort(order, (a, b) => (units[a] - goal).sqrMagnitude.CompareTo((units[b] - goal).sqrMagnitude));

            var taken = new bool[targets.Count];
            var result = new int[units.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = -1;
            foreach (var unit in order)
            {
                var best = -1;
                var bestDistance = float.MaxValue;
                for (var t = 0; t < targets.Count; t++)
                {
                    if (taken[t])
                        continue;
                    var distance = (units[unit] - (Vector2)targets[t]).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = t;
                    }
                }

                if (best < 0)
                    break;
                taken[best] = true;
                result[unit] = best;
            }

            return result;
        }
    }
}
