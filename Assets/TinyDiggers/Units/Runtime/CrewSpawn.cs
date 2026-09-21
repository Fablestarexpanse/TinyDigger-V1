using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Where a crew starts: the nearest cell to a wished-for spot whose whole 3 x 3 block is
    /// ground the crew can stand on (not void, not water) and level to within one height step.
    /// Found by searching outward in rings, so it works on any size of map and any island; a
    /// fixed spawn cell was left behind in the sea when the disc grew.
    /// </summary>
    public static class CrewSpawn
    {
        /// <summary>
        /// The nearest good spawn cell to (<paramref name="x"/>, <paramref name="z"/>) within
        /// <paramref name="maxRing"/> cells, or false if there is none.
        /// </summary>
        public static bool TryFind(TerrainGrid grid, int x, int z, int maxRing, out Vector2Int cell)
        {
            for (var ring = 0; ring <= maxRing; ring++)
            {
                // The ring's cells, in a fixed order so the same map always gives the same spot.
                for (var dz = -ring; dz <= ring; dz++)
                {
                    var edgeRow = dz == -ring || dz == ring;
                    for (var dx = -ring; dx <= ring; dx += edgeRow ? 1 : 2 * ring)
                    {
                        if (IsGood(grid, x + dx, z + dz))
                        {
                            cell = new Vector2Int(x + dx, z + dz);
                            return true;
                        }

                        if (ring == 0)
                            break;
                    }
                }
            }

            cell = default;
            return false;
        }

        /// <summary>Whether the 3 x 3 block round (x, z) is all standable, level ground.</summary>
        public static bool IsGood(TerrainGrid grid, int x, int z)
        {
            var step = grid.HeightStep > 0f ? grid.HeightStep : 1f;
            float low = float.MaxValue, high = float.MinValue;
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!grid.IsPassableGround(x + dx, z + dz))
                        return false;
                    var h = grid.GetSurfaceHeight(x + dx, z + dz);
                    low = Mathf.Min(low, h);
                    high = Mathf.Max(high, h);
                }

            return high - low <= step + 1e-4f;
        }
    }
}
