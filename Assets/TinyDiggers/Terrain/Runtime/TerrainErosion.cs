using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Natural terrain, phase 3 (Ronan, 2026-09-21): the land is weathered rather than left as
    /// raw noise, and nowhere holds a hollow water could not drain out of.
    /// - <see cref="Droplets"/>: hydraulic erosion. Raindrops run downhill, pick up soil where
    ///   they speed up and drop it where they slow, so valleys join up and slopes grow gullies.
    /// - <see cref="Thermal"/>: slopes steeper than their resting angle shed soil to the cells
    ///   below them, in all eight directions at once, so nothing is left too steep and no
    ///   direction is favoured.
    /// - <see cref="FillDepressions"/>: every hollow on land is filled to the level it would
    ///   spill over at, so water always has a way to the sea or a lake.
    /// - <see cref="EuclideanDistance"/>: exact straight-line distance to a set of cells. A
    ///   four-way flood measures city-block distance, whose equal-distance lines are diamonds,
    ///   and anything shaped by it comes out in straight lines and chevrons.
    /// All of it is plain arithmetic over flat arrays, so it is tested without a grid.
    /// </summary>
    public static class TerrainErosion
    {
        [Serializable]
        public struct DropletSettings
        {
            public int Lifetime;
            public float Inertia;
            public float Capacity;
            public float MinCapacity;
            public float Erode;
            public float Deposit;
            public float Evaporate;
            public float Gravity;
            public int Radius;

            public static DropletSettings Default => new DropletSettings
            {
                Lifetime = 40, Inertia = 0.05f, Capacity = 4f, MinCapacity = 0.01f,
                Erode = 0.3f, Deposit = 0.3f, Evaporate = 0.02f, Gravity = 4f, Radius = 3,
            };
        }

        const float MaxSpeed = 4f;

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
        static readonly float[] Run = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

        // --- hydraulic ------------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="count"/> raindrops over the heightfield (metres, one value per cell
        /// of one unit). Drops start on cells that are not <paramref name="fixedCells"/> and stop
        /// when they reach one; fixed cells never change. Deterministic for a seed.
        /// </summary>
        public static void Droplets(float[] heights, int width, int depth, bool[] fixedCells, int count, int seed, DropletSettings s)
        {
            var random = new System.Random(seed);
            var starts = new List<int>();
            for (var cell = 0; cell < heights.Length; cell++)
            {
                var x = cell % width;
                var z = cell / width;
                if (!fixedCells[cell] && x > 0 && z > 0 && x < width - 1 && z < depth - 1)
                    starts.Add(cell);
            }

            if (starts.Count == 0)
                return;

            // The brush: cells within the radius, weighted by closeness, weights adding to 1.
            var brush = new List<(int dx, int dz, float w)>();
            var total = 0f;
            for (var dz = -s.Radius; dz <= s.Radius; dz++)
                for (var dx = -s.Radius; dx <= s.Radius; dx++)
                {
                    var distance = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distance > s.Radius)
                        continue;
                    var w = 1f - distance / (s.Radius + 1f);
                    brush.Add((dx, dz, w));
                    total += w;
                }

            for (var i = 0; i < brush.Count; i++)
                brush[i] = (brush[i].dx, brush[i].dz, brush[i].w / total);

            for (var drop = 0; drop < count; drop++)
            {
                var start = starts[random.Next(starts.Count)];
                var px = start % width + (float)random.NextDouble();
                var pz = start / width + (float)random.NextDouble();
                float dirX = 0f, dirZ = 0f, speed = 1f, water = 1f, sediment = 0f;

                for (var life = 0; life < s.Lifetime; life++)
                {
                    var nodeX = (int)px;
                    var nodeZ = (int)pz;
                    var fx = px - nodeX;
                    var fz = pz - nodeZ;
                    var (height, gx, gz) = HeightAndGradient(heights, width, px, pz);

                    dirX = dirX * s.Inertia - gx * (1f - s.Inertia);
                    dirZ = dirZ * s.Inertia - gz * (1f - s.Inertia);
                    var length = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
                    if (length < 1e-6f)
                        break;
                    dirX /= length;
                    dirZ /= length;
                    px += dirX;
                    pz += dirZ;

                    var nx = (int)px;
                    var nz = (int)pz;
                    if (nx < 1 || nz < 1 || nx >= width - 2 || nz >= depth - 2 || fixedCells[nz * width + nx])
                        break;

                    var (newHeight, _, _) = HeightAndGradient(heights, width, px, pz);
                    var fall = newHeight - height;
                    var capacity = Mathf.Max(-fall * speed * water * s.Capacity, s.MinCapacity);

                    if (sediment > capacity || fall > 0f)
                    {
                        // Uphill: fill the hollow it came from with what it can. Otherwise drop the excess.
                        var amount = fall > 0f ? Mathf.Min(fall, sediment) : (sediment - capacity) * s.Deposit;
                        sediment -= amount;
                        Add(heights, fixedCells, width, nodeX, nodeZ, (1 - fx) * (1 - fz) * amount);
                        Add(heights, fixedCells, width, nodeX + 1, nodeZ, fx * (1 - fz) * amount);
                        Add(heights, fixedCells, width, nodeX, nodeZ + 1, (1 - fx) * fz * amount);
                        Add(heights, fixedCells, width, nodeX + 1, nodeZ + 1, fx * fz * amount);
                    }
                    else
                    {
                        // Never take more than the drop, or it would dig a pit behind itself.
                        var amount = Mathf.Min((capacity - sediment) * s.Erode, -fall);
                        foreach (var (dx, dz, w) in brush)
                        {
                            var bx = nodeX + dx;
                            var bz = nodeZ + dz;
                            if (bx < 0 || bz < 0 || bx >= width || bz >= depth)
                                continue;
                            var cell = bz * width + bx;
                            if (fixedCells[cell])
                                continue;
                            // Never below where the drop now is: digging past that is how a single
                            // drop drills a pit it can never climb out of.
                            var take = Mathf.Min(amount * w, Mathf.Max(0f, heights[cell] - newHeight));
                            heights[cell] -= take;
                            sediment += take;
                        }
                    }

                    // The standard form (fall is negative going down), capped: an uncapped speed
                    // let capacity grow without bound and drops drilled the land to -50 km.
                    speed = Mathf.Min(MaxSpeed, Mathf.Sqrt(Mathf.Max(0f, speed * speed + fall * s.Gravity)));
                    water *= 1f - s.Evaporate;
                }
            }
        }

        static void Add(float[] heights, bool[] fixedCells, int width, int x, int z, float amount)
        {
            var cell = z * width + x;
            if (!fixedCells[cell])
                heights[cell] += amount;
        }

        static (float height, float gx, float gz) HeightAndGradient(float[] heights, int width, float px, float pz)
        {
            var x = (int)px;
            var z = (int)pz;
            var fx = px - x;
            var fz = pz - z;
            var cell = z * width + x;
            var nw = heights[cell];
            var ne = heights[cell + 1];
            var sw = heights[cell + width];
            var se = heights[cell + width + 1];
            var gx = (ne - nw) * (1 - fz) + (se - sw) * fz;
            var gz = (sw - nw) * (1 - fx) + (se - ne) * fx;
            var height = nw * (1 - fx) * (1 - fz) + ne * fx * (1 - fz) + sw * (1 - fx) * fz + se * fx * fz;
            return (height, gx, gz);
        }

        // --- thermal --------------------------------------------------------------------------

        /// <summary>
        /// Settles slopes to their resting angle. <paramref name="talus"/> is, per cell, the most
        /// it may stand above a side neighbour (a diagonal is allowed √2 of it). Each pass, every
        /// cell sheds a quarter of its excess over each lower neighbour, all cells at once from
        /// the same heights, so no sweep direction shows. Material is conserved; fixed cells
        /// neither give nor take.
        /// </summary>
        public static void Thermal(float[] heights, int width, int depth, bool[] fixedCells, float[] talus, int iterations)
        {
            var next = new float[heights.Length];
            for (var pass = 0; pass < iterations; pass++)
            {
                Parallel.For(0, depth, z =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var cell = z * width + x;
                        var h = heights[cell];
                        if (fixedCells[cell])
                        {
                            next[cell] = h;
                            continue;
                        }

                        var change = 0f;
                        for (var n = 0; n < 8; n++)
                        {
                            var ax = x + DX[n];
                            var az = z + DZ[n];
                            if (ax < 0 || az < 0 || ax >= width || az >= depth)
                                continue;
                            var other = az * width + ax;
                            if (fixedCells[other])
                                continue;
                            var hn = heights[other];
                            // A pair is judged by the talus of its higher cell, so both cells
                            // work out the same transfer and what one loses the other gains.
                            if (h > hn)
                            {
                                var excess = h - hn - talus[cell] * Run[n];
                                if (excess > 0f)
                                    change -= excess * 0.125f;
                            }
                            else
                            {
                                var excess = hn - h - talus[other] * Run[n];
                                if (excess > 0f)
                                    change += excess * 0.125f;
                            }
                        }

                        next[cell] = h + change;
                    }
                });
                Array.Copy(next, heights, heights.Length);
            }
        }

        // --- depressions ----------------------------------------------------------------------

        /// <summary>
        /// Raises every <paramref name="land"/> cell that sits in a hollow to the level at which
        /// the hollow spills, working up from the <paramref name="outlets"/> (the sea, lakes, the
        /// edge) by a priority flood over height levels of <paramref name="step"/>. Heights are
        /// expected to be on those levels, and stay on them. Cells that are neither land nor
        /// outlet are walls. Returns how many cells were raised.
        /// </summary>
        public static int FillDepressions(float[] heights, int width, int depth, bool[] land, bool[] outlets, float step)
        {
            var cells = heights.Length;
            var min = int.MaxValue;
            var max = int.MinValue;
            for (var cell = 0; cell < cells; cell++)
            {
                if (!land[cell] && !outlets[cell])
                    continue;
                var level = Level(heights[cell], step);
                min = Math.Min(min, level);
                max = Math.Max(max, level);
            }

            if (min > max)
                return 0;

            var buckets = new List<int>[max - min + 1];
            for (var i = 0; i < buckets.Length; i++)
                buckets[i] = new List<int>();
            var visited = new bool[cells];
            for (var cell = 0; cell < cells; cell++)
            {
                if (!outlets[cell])
                    continue;
                visited[cell] = true;
                buckets[Level(heights[cell], step) - min].Add(cell);
            }

            var raised = 0;
            for (var b = 0; b < buckets.Length; b++)
            {
                var bucket = buckets[b];
                // The bucket grows while it is worked through: cells raised to this level join it.
                for (var i = 0; i < bucket.Count; i++)
                {
                    var cell = bucket[i];
                    var x = cell % width;
                    var z = cell / width;
                    for (var n = 0; n < 8; n++)
                    {
                        var ax = x + DX[n];
                        var az = z + DZ[n];
                        if (ax < 0 || az < 0 || ax >= width || az >= depth)
                            continue;
                        var other = az * width + ax;
                        if (visited[other] || !land[other])
                            continue;
                        visited[other] = true;
                        var level = Level(heights[other], step) - min;
                        if (level < b)
                        {
                            heights[other] = (b + min) * step;
                            raised++;
                            bucket.Add(other);
                        }
                        else
                        {
                            buckets[level].Add(other);
                        }
                    }
                }
            }

            return raised;
        }

        static int Level(float height, float step) => Mathf.RoundToInt(height / step);

        // --- distance -------------------------------------------------------------------------

        /// <summary>
        /// Exact Euclidean distance, in cells, from each cell to the nearest <paramref name="seed"/>
        /// cell (Felzenszwalb and Huttenlocher's two-pass transform). Cells further than
        /// <paramref name="reach"/>, or with no seed at all, get float.MaxValue.
        /// </summary>
        public static float[] EuclideanDistance(bool[] seed, int width, int depth, float reach = float.MaxValue)
        {
            const float Far = 1e12f;
            var squared = new float[seed.Length];
            // Down each column.
            Parallel.For(0, width, () => new Scratch(Math.Max(width, depth)), (x, _, scratch) =>
            {
                for (var z = 0; z < depth; z++)
                    scratch.F[z] = seed[z * width + x] ? 0f : Far;
                Transform(scratch, depth);
                for (var z = 0; z < depth; z++)
                    squared[z * width + x] = scratch.D[z];
                return scratch;
            }, _ => { });

            // Along each row.
            var distance = new float[seed.Length];
            var reachSquared = reach >= float.MaxValue ? float.MaxValue : reach * reach;
            Parallel.For(0, depth, () => new Scratch(Math.Max(width, depth)), (z, _, scratch) =>
            {
                for (var x = 0; x < width; x++)
                    scratch.F[x] = squared[z * width + x];
                Transform(scratch, width);
                for (var x = 0; x < width; x++)
                {
                    var d = scratch.D[x];
                    distance[z * width + x] = d >= Far * 0.5f || d > reachSquared ? float.MaxValue : Mathf.Sqrt(d);
                }

                return scratch;
            }, _ => { });
            return distance;
        }

        sealed class Scratch
        {
            public readonly float[] F;
            public readonly float[] D;
            public readonly int[] V;
            public readonly float[] Z;

            public Scratch(int n)
            {
                F = new float[n];
                D = new float[n];
                V = new int[n];
                Z = new float[n + 1];
            }
        }

        /// <summary>One-dimensional squared distance transform of F into D (lower envelope of parabolas).</summary>
        static void Transform(Scratch s, int n)
        {
            var k = 0;
            s.V[0] = 0;
            s.Z[0] = float.NegativeInfinity;
            s.Z[1] = float.PositiveInfinity;
            for (var q = 1; q < n; q++)
            {
                float Intersect(int p) => (s.F[q] + (float)q * q - (s.F[p] + (float)p * p)) / (2f * q - 2f * p);
                var at = Intersect(s.V[k]);
                while (at <= s.Z[k])
                {
                    k--;
                    at = Intersect(s.V[k]);
                }

                k++;
                s.V[k] = q;
                s.Z[k] = at;
                s.Z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < n; q++)
            {
                while (s.Z[k + 1] < q)
                    k++;
                var d = q - s.V[k];
                s.D[q] = (float)d * d + s.F[s.V[k]];
            }
        }
    }
}
