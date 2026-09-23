using System.Diagnostics;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// What the water shader needs to know about the land, per cell, baked once when the land
    /// changes rather than worked out every frame:
    ///
    /// - <see cref="Depth"/>: metres of water over the cell, smoothed a little so the quantised
    ///   seabed does not show as bands of colour. Negative on dry land.
    /// - <see cref="ShoreDistance"/>: metres through the water to the nearest dry cell. Waves run in
    ///   along this, and the swell dies away as it falls.
    /// - <see cref="ShoreDirection"/>: which way the nearest shore lies, so a wave can face it.
    ///
    /// Plain C#, so the rules can be tested without a scene.
    /// </summary>
    public sealed class WaterField
    {
        /// <summary>Distance given to water that no shore can be reached from.</summary>
        public const float Open = 10000f;

        public readonly int Width;
        public readonly int Height;
        public readonly float[] Depth;
        public readonly float[] ShoreDistance;
        public readonly Vector2[] ShoreDirection;

        /// <summary>How long the bake took, for the perf report.</summary>
        public float Milliseconds { get; private set; }

        WaterField(int width, int height)
        {
            Width = width;
            Height = height;
            Depth = new float[width * height];
            ShoreDistance = new float[width * height];
            ShoreDirection = new Vector2[width * height];
        }

        /// <summary>
        /// Bakes the field for <paramref name="grid"/>. Cells past the edge of the land (not ground)
        /// count as open water <paramref name="rimDepth"/> deep, so the edge of the disc stays dark.
        /// </summary>
        public static WaterField Bake(TerrainGrid grid, int blur = 2, float rimDepth = 12f)
        {
            var clock = Stopwatch.StartNew();
            var width = grid.Width;
            var height = grid.Height;
            var field = new WaterField(width, height);

            var raw = new float[width * height];
            for (var z = 0; z < height; z++)
                for (var x = 0; x < width; x++)
                    raw[z * width + x] = grid.IsGround(x, z)
                        ? World.SeaLevel - grid.GetSurfaceHeight(x, z)
                        : rimDepth;

            BoxBlur(raw, field.Depth, width, height, Mathf.Max(0, blur));
            ShoreDistances(raw, field.ShoreDistance, width, height);
            Directions(field.ShoreDistance, field.ShoreDirection, width, height);

            // Distances were walked in cells; the shader and the swell rule want metres.
            var cellSize = grid.CellSize;
            if (!Mathf.Approximately(cellSize, 1f))
                for (var i = 0; i < field.ShoreDistance.Length; i++)
                    if (field.ShoreDistance[i] < Open)
                        field.ShoreDistance[i] *= cellSize;

            field.Milliseconds = (float)clock.Elapsed.TotalMilliseconds;
            return field;
        }

        /// <summary>
        /// Re-bakes one window of an existing field, for a dig that only moved the waterline in one
        /// place. A full <see cref="Bake"/> of the island is a quarter of a second; a hundred-metre
        /// window is a fraction of a millisecond, and the crew work in one corner at a time.
        ///
        /// <paramref name="window"/> is the cells that changed; <paramref name="margin"/> cells are
        /// worked either side of it. The chamfer inside the margin is seeded from the distances
        /// already in the field at its border, so a cell's distance is exactly what a full bake
        /// would give it as long as its nearest shore is inside the margin. The shader reads shore
        /// distance no further out than _ShoreReach (16 m) and the swell damping no further than
        /// _DampDistance (6 m), so the default margin of 48 cells — 24 m — covers everything that
        /// is ever looked at.
        ///
        /// The one thing it cannot see is a change that moves the nearest shore of a cell *outside*
        /// the margin, which needs a spit narrower than the margin to be dug through. That shows as
        /// a stale wave band beyond the working area until the next full bake, and never as wrong
        /// water.
        /// </summary>
        public void Rebake(TerrainGrid grid, RectInt window, int margin = 48, int blur = 2, float rimDepth = 12f)
        {
            var clock = Stopwatch.StartNew();
            blur = Mathf.Max(0, blur);
            margin = Mathf.Max(blur + 1, margin);

            // The window that is rewritten, and the wider one that is read to rewrite it: the blur
            // and the gradient both reach a little past what they write.
            var x0 = Mathf.Clamp(window.xMin - margin, 0, Width - 1);
            var z0 = Mathf.Clamp(window.yMin - margin, 0, Height - 1);
            var x1 = Mathf.Clamp(window.xMax + margin, 0, Width - 1);
            var z1 = Mathf.Clamp(window.yMax + margin, 0, Height - 1);
            var readX0 = Mathf.Max(0, x0 - blur);
            var readZ0 = Mathf.Max(0, z0 - blur);
            var readX1 = Mathf.Min(Width - 1, x1 + blur);
            var readZ1 = Mathf.Min(Height - 1, z1 + blur);

            var wide = readX1 - readX0 + 1;
            var tall = readZ1 - readZ0 + 1;
            var raw = new float[wide * tall];
            for (var z = readZ0; z <= readZ1; z++)
                for (var x = readX0; x <= readX1; x++)
                    raw[(z - readZ0) * wide + (x - readX0)] = grid.IsGround(x, z)
                        ? World.SeaLevel - grid.GetSurfaceHeight(x, z)
                        : rimDepth;

            // Depth: the same box blur, done the plain way. A summed-area table pays for itself over
            // nine million cells and not over a few thousand.
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var sum = 0f;
                    var count = 0;
                    var az0 = Mathf.Max(readZ0, z - blur);
                    var az1 = Mathf.Min(readZ1, z + blur);
                    var ax0 = Mathf.Max(readX0, x - blur);
                    var ax1 = Mathf.Min(readX1, x + blur);
                    for (var az = az0; az <= az1; az++)
                        for (var ax = ax0; ax <= ax1; ax++)
                        {
                            sum += raw[(az - readZ0) * wide + (ax - readX0)];
                            count++;
                        }

                    Depth[z * Width + x] = count == 0 ? 0f : sum / count;
                }
            }

            // Shore distance, walked in cells inside the window and seeded in cells from the field's
            // metres at its border.
            var cellSize = grid.CellSize;
            var inverse = cellSize <= 0f ? 1f : 1f / cellSize;
            var windowWide = x1 - x0 + 1;
            var windowTall = z1 - z0 + 1;
            var distance = new float[windowWide * windowTall];
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    distance[(z - z0) * windowWide + (x - x0)] =
                        raw[(z - readZ0) * wide + (x - readX0)] <= 0f ? 0f : Open;

            // What lies just outside, as the field already has it, in cells.
            float Outside(int x, int z)
            {
                if (x < 0 || z < 0 || x >= Width || z >= Height)
                    return Open;
                var already = ShoreDistance[z * Width + x];
                return already >= Open ? Open : already * inverse;
            }

            float At(int x, int z) =>
                x < x0 || x > x1 || z < z0 || z > z1
                    ? Outside(x, z)
                    : distance[(z - z0) * windowWide + (x - x0)];

            const float Straight = 1f;
            const float Diagonal = 1.41421356f;

            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var slot = (z - z0) * windowWide + (x - x0);
                    var d = distance[slot];
                    if (d == 0f)
                        continue;
                    d = Mathf.Min(d, At(x - 1, z) + Straight);
                    d = Mathf.Min(d, At(x, z - 1) + Straight);
                    d = Mathf.Min(d, At(x - 1, z - 1) + Diagonal);
                    d = Mathf.Min(d, At(x + 1, z - 1) + Diagonal);
                    distance[slot] = Mathf.Min(d, Open);
                }
            }

            for (var z = z1; z >= z0; z--)
            {
                for (var x = x1; x >= x0; x--)
                {
                    var slot = (z - z0) * windowWide + (x - x0);
                    var d = distance[slot];
                    if (d == 0f)
                        continue;
                    d = Mathf.Min(d, At(x + 1, z) + Straight);
                    d = Mathf.Min(d, At(x, z + 1) + Straight);
                    d = Mathf.Min(d, At(x + 1, z + 1) + Diagonal);
                    d = Mathf.Min(d, At(x - 1, z + 1) + Diagonal);
                    distance[slot] = Mathf.Min(d, Open);
                }
            }

            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    var d = distance[(z - z0) * windowWide + (x - x0)];
                    ShoreDistance[z * Width + x] = d >= Open ? Open : d * cellSize;
                }

            // Directions, over the window only. They read a neighbour either side, which is now
            // written for the inner cells and still whatever it was on the margin's own edge.
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var i = z * Width + x;
                    if (ShoreDistance[i] <= 0f || ShoreDistance[i] >= Open)
                    {
                        ShoreDirection[i] = Vector2.zero;
                        continue;
                    }

                    var left = ShoreDistance[z * Width + Mathf.Max(0, x - 1)];
                    var right = ShoreDistance[z * Width + Mathf.Min(Width - 1, x + 1)];
                    var down = ShoreDistance[Mathf.Max(0, z - 1) * Width + x];
                    var up = ShoreDistance[Mathf.Min(Height - 1, z + 1) * Width + x];
                    var gradient = new Vector2(right - left, up - down);
                    ShoreDirection[i] = gradient.sqrMagnitude > 1e-6f ? -gradient.normalized : Vector2.zero;
                }
            }

            Milliseconds = (float)clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// How much of the swell survives here: none in the shallows or at the shore, all of it out
        /// in deep water. The shader applies the same rule, so no wave ever floods a beach.
        /// </summary>
        public static float SwellDamping(float depth, float shoreDistance, float dampDepth, float dampDistance) =>
            Mathf.Clamp01(depth / Mathf.Max(0.01f, dampDepth)) * Mathf.Clamp01(shoreDistance / Mathf.Max(0.01f, dampDistance));

        public float DepthAt(int x, int z) => Depth[Index(x, z)];

        public float ShoreDistanceAt(int x, int z) => ShoreDistance[Index(x, z)];

        public Vector2 ShoreDirectionAt(int x, int z) => ShoreDirection[Index(x, z)];

        int Index(int x, int z) =>
            Mathf.Clamp(z, 0, Height - 1) * Width + Mathf.Clamp(x, 0, Width - 1);

        /// <summary>Box blur by summed-area table, so the cost does not grow with the radius.</summary>
        static void BoxBlur(float[] source, float[] target, int width, int height, int radius)
        {
            if (radius == 0)
            {
                System.Array.Copy(source, target, source.Length);
                return;
            }

            var stride = width + 1;
            var sums = new double[stride * (height + 1)];
            for (var z = 0; z < height; z++)
            {
                double row = 0;
                for (var x = 0; x < width; x++)
                {
                    row += source[z * width + x];
                    sums[(z + 1) * stride + x + 1] = sums[z * stride + x + 1] + row;
                }
            }

            for (var z = 0; z < height; z++)
            {
                var z0 = Mathf.Max(0, z - radius);
                var z1 = Mathf.Min(height, z + radius + 1);
                for (var x = 0; x < width; x++)
                {
                    var x0 = Mathf.Max(0, x - radius);
                    var x1 = Mathf.Min(width, x + radius + 1);
                    var sum = sums[z1 * stride + x1] - sums[z0 * stride + x1] - sums[z1 * stride + x0] + sums[z0 * stride + x0];
                    target[z * width + x] = (float)(sum / ((x1 - x0) * (z1 - z0)));
                }
            }
        }

        /// <summary>
        /// Metres through the water to the nearest dry cell, by a two-pass chamfer (1 straight,
        /// the square root of 2 diagonal). Dry cells are 0.
        /// </summary>
        static void ShoreDistances(float[] raw, float[] distance, int width, int height)
        {
            const float Straight = 1f;
            const float Diagonal = 1.41421356f;

            for (var i = 0; i < raw.Length; i++)
                distance[i] = raw[i] <= 0f ? 0f : Open;

            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = z * width + x;
                    var d = distance[i];
                    if (d == 0f)
                        continue;
                    if (x > 0) d = Mathf.Min(d, distance[i - 1] + Straight);
                    if (z > 0)
                    {
                        d = Mathf.Min(d, distance[i - width] + Straight);
                        if (x > 0) d = Mathf.Min(d, distance[i - width - 1] + Diagonal);
                        if (x < width - 1) d = Mathf.Min(d, distance[i - width + 1] + Diagonal);
                    }

                    distance[i] = d;
                }
            }

            for (var z = height - 1; z >= 0; z--)
            {
                for (var x = width - 1; x >= 0; x--)
                {
                    var i = z * width + x;
                    var d = distance[i];
                    if (d == 0f)
                        continue;
                    if (x < width - 1) d = Mathf.Min(d, distance[i + 1] + Straight);
                    if (z < height - 1)
                    {
                        d = Mathf.Min(d, distance[i + width] + Straight);
                        if (x < width - 1) d = Mathf.Min(d, distance[i + width + 1] + Diagonal);
                        if (x > 0) d = Mathf.Min(d, distance[i + width - 1] + Diagonal);
                    }

                    distance[i] = Mathf.Min(d, Open);
                }
            }
        }

        /// <summary>Downhill on the distance field is towards the shore.</summary>
        static void Directions(float[] distance, Vector2[] direction, int width, int height)
        {
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = z * width + x;
                    if (distance[i] <= 0f || distance[i] >= Open)
                    {
                        direction[i] = Vector2.zero;
                        continue;
                    }

                    var left = distance[z * width + Mathf.Max(0, x - 1)];
                    var right = distance[z * width + Mathf.Min(width - 1, x + 1)];
                    var down = distance[Mathf.Max(0, z - 1) * width + x];
                    var up = distance[Mathf.Min(height - 1, z + 1) * width + x];
                    var gradient = new Vector2(right - left, up - down);
                    direction[i] = gradient.sqrMagnitude > 1e-6f ? -gradient.normalized : Vector2.zero;
                }
            }
        }
    }
}
