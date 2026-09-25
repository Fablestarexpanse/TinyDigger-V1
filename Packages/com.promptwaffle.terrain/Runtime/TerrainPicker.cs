using System;
using UnityEngine;

namespace PromptWaffle.Terrain
{
    /// <summary>
    /// Finds the cell a ray hits, walking the grid cell by cell along the ray's footprint and
    /// treating each column as a flat-topped box of its surface height. Needs no colliders, so
    /// digging never waits on a physics rebake. The renderer draws columns the same way, flat
    /// tops and vertical walls, so the picked cell is the one drawn under the cursor.
    /// The ray is in the terrain's local space (metres); the walk is in cells, and the hit point
    /// comes back in metres.
    /// </summary>
    public static class TerrainPicker
    {
        const float Parallel = 1e-8f;

        public static bool TryPick(
            TerrainGrid grid,
            Vector3 origin,
            Vector3 direction,
            out int cellX,
            out int cellZ,
            out Vector3 point,
            float maxDistance = 10000f)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            cellX = cellZ = -1;
            point = default;
            // Into cells: squash x and z, so one unit is one cell and heights stay metres.
            var cellSize = grid.CellSize;
            origin = new Vector3(origin.x / cellSize, origin.y, origin.z / cellSize);
            direction = new Vector3(direction.x / cellSize, direction.y, direction.z / cellSize);
            if (direction.sqrMagnitude < Parallel)
                return false;
            direction.Normalize();

            // Clip the ray to the grid's footprint.
            var tMin = 0f;
            var tMax = maxDistance;
            if (!ClipSlab(origin.x, direction.x, grid.Width, ref tMin, ref tMax) ||
                !ClipSlab(origin.z, direction.z, grid.Height, ref tMin, ref tMax))
                return false;

            var entry = origin + direction * tMin;
            var x = Mathf.Clamp(Mathf.FloorToInt(entry.x), 0, grid.Width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(entry.z), 0, grid.Height - 1);

            var stepX = direction.x > 0f ? 1 : -1;
            var stepZ = direction.z > 0f ? 1 : -1;
            var tDeltaX = Mathf.Abs(direction.x) < Parallel ? float.PositiveInfinity : Mathf.Abs(1f / direction.x);
            var tDeltaZ = Mathf.Abs(direction.z) < Parallel ? float.PositiveInfinity : Mathf.Abs(1f / direction.z);
            var tNextX = Mathf.Abs(direction.x) < Parallel
                ? float.PositiveInfinity
                : ((stepX > 0 ? x + 1 : x) - origin.x) / direction.x;
            var tNextZ = Mathf.Abs(direction.z) < Parallel
                ? float.PositiveInfinity
                : ((stepZ > 0 ? z + 1 : z) - origin.z) / direction.z;

            var tEnter = tMin;
            while (tEnter <= tMax)
            {
                var tExit = Mathf.Min(Mathf.Min(tNextX, tNextZ), tMax);
                var height = grid.GetSurfaceHeight(x, z);
                var yEnter = origin.y + direction.y * tEnter;
                var yExit = origin.y + direction.y * tExit;

                if (yEnter <= height)
                {
                    // Entered through the side of a taller column.
                    return Hit(x, z, origin + direction * tEnter, out cellX, out cellZ, out point, cellSize);
                }

                if (yExit <= height)
                {
                    var tTop = (height - origin.y) / direction.y;
                    return Hit(x, z, origin + direction * tTop, out cellX, out cellZ, out point, cellSize);
                }

                if (tNextX < tNextZ)
                {
                    x += stepX;
                    tEnter = tNextX;
                    tNextX += tDeltaX;
                }
                else
                {
                    z += stepZ;
                    tEnter = tNextZ;
                    tNextZ += tDeltaZ;
                }

                if (!grid.InBounds(x, z))
                    return false;
            }

            return false;
        }

        static bool Hit(int x, int z, Vector3 at, out int cellX, out int cellZ, out Vector3 point, float cellSize)
        {
            cellX = x;
            cellZ = z;
            point = new Vector3(at.x * cellSize, at.y, at.z * cellSize);
            return true;
        }

        /// <summary>Narrows [tMin, tMax] to where the ray lies within [0, size] on one axis.</summary>
        static bool ClipSlab(float origin, float direction, float size, ref float tMin, ref float tMax)
        {
            if (Mathf.Abs(direction) < Parallel)
                return origin >= 0f && origin < size;

            var t0 = (0f - origin) / direction;
            var t1 = (size - origin) / direction;
            if (t0 > t1)
                (t0, t1) = (t1, t0);

            tMin = Mathf.Max(tMin, t0);
            tMax = Mathf.Min(tMax, t1);
            return tMin <= tMax;
        }
    }
}
