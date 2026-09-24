using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>One cell inside an outline, and how far in it lies.</summary>
    public readonly struct InsideCell
    {
        public InsideCell(int cell, float distance, float edgeGround)
        {
            Cell = cell;
            Distance = distance;
            EdgeGround = edgeGround;
        }

        /// <summary>The cell, as <c>z * grid.Width + x</c>.</summary>
        public int Cell { get; }

        /// <summary>Cells from here to the nearest cell outside the outline.</summary>
        public float Distance { get; }

        /// <summary>The ground height at that nearest outside cell, so a profile can follow the land.</summary>
        public float EdgeGround { get; }
    }

    /// <summary>
    /// Turning a drawn outline into cells. Three steps, and every landform is built from them:
    ///
    /// - <see cref="Fill"/>: which cells lie inside the outline.
    /// - <see cref="Inside"/>: how deep into the shape each of those cells lies, and the ground at
    ///   the nearest point outside it. A heap's batter, a pit's benches and an edge's falloff are
    ///   all that one number.
    /// - <see cref="Perimeter"/>: the cells on the rim, which is all the ground outside a shape can
    ///   possibly be battered by (see <see cref="LandformPlanner"/>).
    ///
    /// Plain C#, so a shape's arithmetic is testable without a scene.
    /// </summary>
    public static class LandformRaster
    {
        /// <summary>
        /// The cells whose centres lie inside the closed polygon <paramref name="outline"/> (in
        /// cells; the last point joins the first), by an even-odd scanline. Concave outlines work;
        /// an outline that crosses itself leaves the crossing as a hole, which is even-odd doing
        /// what it says rather than a fault. Cells off the map or in the void are left out.
        /// </summary>
        public static void Fill(TerrainGrid grid, IReadOnlyList<Vector2> outline, List<int> into)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            Scan(outline, (x, z) =>
            {
                if (x >= 0 && z >= 0 && x < grid.Width && z < grid.Height && grid.IsGround(x, z))
                    into.Add(z * grid.Width + x);
            });
        }

        /// <summary>
        /// The scanline itself, with no map behind it: calls back with every cell whose centre lies
        /// inside the closed polygon, wherever it is. <see cref="Fill"/> is this plus "and is part of
        /// the world"; a worksite's area is this plus "and remember the bounds".
        ///
        /// It lives on its own because the two of them had it written out twice, line for line — the
        /// same crossing test, the same span arithmetic — which is a disagreement waiting to happen
        /// between the ground a shape claims and the ground a site claims (2026-09-24).
        /// </summary>
        public static void Scan(IReadOnlyList<Vector2> outline, Action<int, int> onCell)
        {
            if (onCell == null)
                throw new ArgumentNullException(nameof(onCell));
            if (outline == null || outline.Count < 3)
                return;

            var minZ = float.MaxValue;
            var maxZ = float.MinValue;
            foreach (var point in outline)
            {
                if (point.y < minZ) minZ = point.y;
                if (point.y > maxZ) maxZ = point.y;
            }

            var crossings = new List<float>();
            for (var z = Mathf.FloorToInt(minZ - 0.5f); z <= Mathf.CeilToInt(maxZ); z++)
            {
                // The line through the middle of the row: cells are tested by their centres, so a
                // shape drawn exactly along a cell boundary takes the cells it covers, not both.
                var y = z + 0.5f;
                crossings.Clear();
                for (var i = 0; i < outline.Count; i++)
                {
                    var a = outline[i];
                    var b = outline[(i + 1) % outline.Count];
                    // Half-open in y, so a vertex exactly on the line is counted once, not twice.
                    if (a.y <= y == b.y <= y)
                        continue;
                    var t = (y - a.y) / (b.y - a.y);
                    crossings.Add(a.x + t * (b.x - a.x));
                }

                if (crossings.Count < 2)
                    continue;
                crossings.Sort();

                for (var pair = 0; pair + 1 < crossings.Count; pair += 2)
                {
                    var from = Mathf.CeilToInt(crossings[pair] - 0.5f);
                    var to = Mathf.CeilToInt(crossings[pair + 1] - 0.5f) - 1;
                    for (var x = from; x <= to; x++)
                        onCell(x, z);
                }
            }
        }

        /// <summary>
        /// The cells of a disc <paramref name="radius"/> across, centred on one, that are part of
        /// the world. What a brush press covers, whichever brush is asking — the designation brush
        /// in <c>BrushPlan</c> calls this too, so there is one disc and not two.
        /// </summary>
        public static void Disc(TerrainGrid grid, int centreX, int centreZ, int radius, List<Vector2Int> into)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            radius = Mathf.Max(0, radius);
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dz * dz > radius * radius)
                        continue;
                    var x = centreX + dx;
                    var z = centreZ + dz;
                    if (grid.IsGround(x, z))
                        into.Add(new Vector2Int(x, z));
                }
            }
        }

        /// <summary>
        /// How far into the shape each of <paramref name="cells"/> lies, and the ground height at
        /// the nearest cell outside it, by a two-pass chamfer (1 straight, √2 diagonal) — the same
        /// walk the water field uses for its distance to the shore. A cell on the rim comes out at
        /// 1, which is what makes a batter start at the ground rather than a step above it.
        /// </summary>
        public static void Inside(TerrainGrid grid, IReadOnlyList<int> cells, List<InsideCell> into)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            if (cells == null || cells.Count == 0)
                return;

            // A local box, padded by one so the ring just outside the shape is in it.
            var minX = int.MaxValue;
            var minZ = int.MaxValue;
            var maxX = int.MinValue;
            var maxZ = int.MinValue;
            foreach (var cell in cells)
            {
                var x = cell % grid.Width;
                var z = cell / grid.Width;
                if (x < minX) minX = x;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (z > maxZ) maxZ = z;
            }

            minX = Mathf.Max(0, minX - 1);
            minZ = Mathf.Max(0, minZ - 1);
            maxX = Mathf.Min(grid.Width - 1, maxX + 1);
            maxZ = Mathf.Min(grid.Height - 1, maxZ + 1);
            var wide = maxX - minX + 1;
            var tall = maxZ - minZ + 1;

            const float Far = 1e9f;
            const float Straight = 1f;
            const float Diagonal = 1.41421356f;
            var distance = new float[wide * tall];
            var ground = new float[wide * tall];
            var inside = new bool[wide * tall];
            for (var i = 0; i < distance.Length; i++)
                distance[i] = Far;

            foreach (var cell in cells)
            {
                var x = cell % grid.Width - minX;
                var z = cell / grid.Width - minZ;
                if (x < 0 || z < 0 || x >= wide || z >= tall)
                    continue;
                inside[z * wide + x] = true;
            }

            for (var z = 0; z < tall; z++)
            {
                for (var x = 0; x < wide; x++)
                {
                    var slot = z * wide + x;
                    if (inside[slot])
                        continue;
                    distance[slot] = 0f;
                    ground[slot] = grid.IsGround(minX + x, minZ + z)
                        ? grid.GetSurfaceHeight(minX + x, minZ + z)
                        : 0f;
                }
            }

            void Look(int slot, int from, float step)
            {
                var candidate = distance[from] + step;
                if (candidate >= distance[slot])
                    return;
                distance[slot] = candidate;
                ground[slot] = ground[from];
            }

            for (var z = 0; z < tall; z++)
            {
                for (var x = 0; x < wide; x++)
                {
                    var slot = z * wide + x;
                    if (distance[slot] == 0f)
                        continue;
                    if (x > 0) Look(slot, slot - 1, Straight);
                    if (z > 0)
                    {
                        Look(slot, slot - wide, Straight);
                        if (x > 0) Look(slot, slot - wide - 1, Diagonal);
                        if (x < wide - 1) Look(slot, slot - wide + 1, Diagonal);
                    }
                }
            }

            for (var z = tall - 1; z >= 0; z--)
            {
                for (var x = wide - 1; x >= 0; x--)
                {
                    var slot = z * wide + x;
                    if (distance[slot] == 0f)
                        continue;
                    if (x < wide - 1) Look(slot, slot + 1, Straight);
                    if (z < tall - 1)
                    {
                        Look(slot, slot + wide, Straight);
                        if (x < wide - 1) Look(slot, slot + wide + 1, Diagonal);
                        if (x > 0) Look(slot, slot + wide - 1, Diagonal);
                    }
                }
            }

            foreach (var cell in cells)
            {
                var x = cell % grid.Width - minX;
                var z = cell / grid.Width - minZ;
                if (x < 0 || z < 0 || x >= wide || z >= tall)
                    continue;
                var slot = z * wide + x;
                into.Add(new InsideCell(cell, distance[slot] >= Far ? 0f : distance[slot], ground[slot]));
            }
        }

        /// <summary>
        /// The rim of a filled shape: every cell with a neighbour, straight or diagonal, that is not
        /// in the shape. Ground outside a shape is only ever battered by the nearest cell of it, and
        /// the nearest cell of a shape is always on its rim, so this is the whole of what has to be
        /// settled — which is what keeps a hundred-metre pad from costing millions of probes.
        /// </summary>
        public static void Perimeter(TerrainGrid grid, IReadOnlyList<int> cells, List<int> into)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            if (cells == null || cells.Count == 0)
                return;

            var set = new HashSet<int>(cells);
            foreach (var cell in cells)
            {
                var x = cell % grid.Width;
                var z = cell / grid.Width;
                var rim = false;
                for (var dz = -1; dz <= 1 && !rim; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        var nx = x + dx;
                        var nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= grid.Width || nz >= grid.Height
                            || !set.Contains(nz * grid.Width + nx))
                        {
                            rim = true;
                            break;
                        }
                    }
                }

                if (rim)
                    into.Add(cell);
            }
        }
    }
}
