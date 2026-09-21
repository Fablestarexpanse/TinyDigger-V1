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

            field.Milliseconds = (float)clock.Elapsed.TotalMilliseconds;
            return field;
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
