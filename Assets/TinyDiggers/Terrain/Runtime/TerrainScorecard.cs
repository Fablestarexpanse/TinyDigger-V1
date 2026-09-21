using System;
using System.Threading.Tasks;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// How natural a generated landscape is, in numbers, so the terrain can be tuned against
    /// targets rather than by eye alone (Ronan, 2026-09-21: the land "feels like swiss cheese").
    /// Everything is measured over land: ground cells at or above sea level.
    /// </summary>
    public readonly struct TerrainScorecard
    {
        /// <summary>Slope bands, in degrees, measured over one metre either side of a cell.</summary>
        public const float FlatBelow = 3f;
        public const float GentleBelow = 15f;
        public const float SteepAbove = 35f;

        public int LandCells { get; }
        public float LandSquareMetres { get; }
        public float Highest { get; }

        /// <summary>Shares of land by slope: under 3°, 3–15°, 15–35°, over 35°. They add up to 1.</summary>
        public float Flat { get; }
        public float Gentle { get; }
        public float Moderate { get; }
        public float Steep { get; }

        /// <summary>Land cells lower than all eight neighbours: hollows water would sit in.</summary>
        public int Pits { get; }

        /// <summary>Pits whose lowest neighbour is at least a metre above them.</summary>
        public int DeepPits { get; }

        /// <summary>Share of land stepping more than a metre to a neighbour.</summary>
        public float CliffSteps { get; }

        /// <summary>Land cells beside the sea.</summary>
        public int CoastCells { get; }

        /// <summary>
        /// Share of the coast that is beach: no more than a metre above the sea and stepping no
        /// more than one height step to any land neighbour, so the land runs into the water.
        /// </summary>
        public float BeachCoast { get; }

        public float PitsPerThousand => LandCells > 0 ? Pits * 1000f / LandCells : 0f;

        TerrainScorecard(int land, float area, float highest, float flat, float gentle, float moderate, float steep,
            int pits, int deepPits, float cliffSteps, int coast, float beach)
        {
            LandCells = land;
            LandSquareMetres = area;
            Highest = highest;
            Flat = flat;
            Gentle = gentle;
            Moderate = moderate;
            Steep = steep;
            Pits = pits;
            DeepPits = deepPits;
            CliffSteps = cliffSteps;
            CoastCells = coast;
            BeachCoast = beach;
        }

        public static TerrainScorecard Measure(TerrainGrid grid)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            var width = grid.Width;
            var depth = grid.Height;
            var step = grid.HeightStep > 0f ? grid.HeightStep : 1f;
            var reach = Mathf.Max(1, Mathf.RoundToInt(1f / grid.CellSize)); // cells in one metre
            var run = 2f * reach * grid.CellSize;
            var heights = new float[width * depth];
            var ground = new bool[width * depth];
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    ground[cell] = grid.IsGround(x, z);
                    heights[cell] = ground[cell] ? grid.GetSurfaceHeight(x, z) : float.NaN;
                }
            });

            var rows = new long[depth, 10];
            var highs = new float[depth];
            Parallel.For(0, depth, z =>
            {
                long land = 0, flat = 0, gentle = 0, moderate = 0, steep = 0, pits = 0, deep = 0, cliffs = 0, coast = 0, beach = 0;
                var high = float.MinValue;
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!ground[cell] || heights[cell] < World.SeaLevel)
                        continue;
                    var h = heights[cell];
                    land++;
                    high = Mathf.Max(high, h);

                    var lowest = float.MaxValue;
                    var biggestStep = 0f;
                    var biggestLandStep = 0f;
                    var bySea = false;
                    var neighbours = 0;
                    for (var dz = -1; dz <= 1; dz++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0)
                                continue;
                            var nx = x + dx;
                            var nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= width || nz >= depth || !ground[nz * width + nx])
                                continue;
                            var n = heights[nz * width + nx];
                            neighbours++;
                            if (n < World.SeaLevel)
                                bySea = true;
                            lowest = Mathf.Min(lowest, n);
                            biggestStep = Mathf.Max(biggestStep, Mathf.Abs(n - h));
                            if (n >= World.SeaLevel)
                                biggestLandStep = Mathf.Max(biggestLandStep, Mathf.Abs(n - h));
                        }

                    if (neighbours == 8 && lowest > h + 1e-3f)
                    {
                        pits++;
                        if (lowest >= h + 1f - 1e-3f)
                            deep++;
                    }

                    if (biggestStep > 1f + 1e-3f)
                        cliffs++;
                    if (bySea)
                    {
                        coast++;
                        if (h <= World.SeaLevel + 1f + 1e-3f && biggestLandStep <= step + 1e-3f)
                            beach++;
                    }

                    var degrees = Slope(heights, ground, width, depth, x, z, reach, run);
                    if (degrees < FlatBelow)
                        flat++;
                    else if (degrees < GentleBelow)
                        gentle++;
                    else if (degrees <= SteepAbove)
                        moderate++;
                    else
                        steep++;
                }

                rows[z, 0] = land;
                rows[z, 1] = flat;
                rows[z, 2] = gentle;
                rows[z, 3] = moderate;
                rows[z, 4] = steep;
                rows[z, 5] = pits;
                rows[z, 6] = deep;
                rows[z, 7] = cliffs;
                rows[z, 8] = coast;
                rows[z, 9] = beach;
                highs[z] = high;
            });

            var total = new long[10];
            var highest = float.MinValue;
            for (var z = 0; z < depth; z++)
            {
                for (var i = 0; i < 10; i++)
                    total[i] += rows[z, i];
                highest = Mathf.Max(highest, highs[z]);
            }

            float Share(long n, long of) => of > 0 ? (float)n / of : 0f;
            var landCells = total[0];
            return new TerrainScorecard((int)landCells, landCells * grid.CellArea, landCells > 0 ? highest : 0f,
                Share(total[1], landCells), Share(total[2], landCells), Share(total[3], landCells), Share(total[4], landCells),
                (int)total[5], (int)total[6], Share(total[7], landCells), (int)total[8], Share(total[9], total[8]));
        }

        /// <summary>
        /// Slope in degrees by central differences <paramref name="reach"/> cells either side,
        /// falling back to the cell itself on a side that is off the ground.
        /// </summary>
        static float Slope(float[] heights, bool[] ground, int width, int depth, int x, int z, int reach, float run)
        {
            float At(int ax, int az)
            {
                ax = Mathf.Clamp(ax, 0, width - 1);
                az = Mathf.Clamp(az, 0, depth - 1);
                var cell = az * width + ax;
                return ground[cell] ? heights[cell] : heights[z * width + x];
            }

            var sx = (At(x + reach, z) - At(x - reach, z)) / run;
            var sz = (At(x, z + reach) - At(x, z - reach)) / run;
            return Mathf.Atan(Mathf.Sqrt(sx * sx + sz * sz)) * Mathf.Rad2Deg;
        }

        public override string ToString() =>
            $"land {LandSquareMetres / 1e6f:0.000} km², highest {Highest:0.0} m; " +
            $"slope flat {Flat:P0}, gentle {Gentle:P0}, moderate {Moderate:P0}, steep {Steep:P0}; " +
            $"pits {Pits} ({PitsPerThousand:0.0}/1000, {DeepPits} ≥1 m); cliff steps {CliffSteps:P1}; " +
            $"coast {CoastCells} cells, beach {BeachCoast:P0}";
    }
}
