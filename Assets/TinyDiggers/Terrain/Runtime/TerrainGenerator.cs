using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Temporary test terrain: broad plateaus from large, low-frequency noise, plus a few wide
    /// hills 4-8 m high, so that at a 1 m height step the land reads as wide terraces
    /// (TERRAIN_REFERENCE.md section 1.2). There is deliberately no small-scale noise: it would
    /// fray the terrace edges into single-cell speckle. Layered bedrock, then granite in the hill
    /// cores, then rock, then a dirt and topsoil cap. The soil thins over the hills so that
    /// digging there reaches rock quickly.
    ///
    /// Deterministic for a given seed and grid size. Surfaces land on the grid's
    /// <see cref="TerrainGrid.HeightStep"/> when it has one; layer boundaries inside a column
    /// do not.
    /// </summary>
    public static class TerrainGenerator
    {
        public const float BedrockThickness = 10f;
        public const float TopsoilThickness = 0.3f;

        /// <summary>Layers thinner than this are left out rather than spent on a near-invisible band.</summary>
        const float MinLayerThickness = 0.05f;

        /// <summary>Plain surface sits this far above the bedrock before noise and hills.</summary>
        const float BaseRockDepth = 6f;

        /// <summary>Cycles per cell of the plateau noise: one broad rise every ~170 cells.</summary>
        const float PlateauFrequency = 0.006f;

        /// <summary>Peak-to-peak metres of the plateau noise: three or four terrace levels across the map.</summary>
        const float PlateauRange = 4f;

        /// <summary>A second, smaller octave so plateau edges wander instead of following smooth ovals.</summary>
        const float EdgeFrequency = 0.015f;

        const float EdgeRange = 1.5f;

        const float SoilOnPlains = 2.5f;
        const float SoilOnHilltops = 0.4f;

        /// <summary>Hill height at which the soil has thinned all the way to <see cref="SoilOnHilltops"/>.</summary>
        const float FullyHillyAt = 4f;

        /// <summary>Fraction of each hill's height that is granite core rather than ordinary rock.</summary>
        const float GraniteShareOfHill = 0.5f;

        struct Hill
        {
            public float X;
            public float Z;
            public float InverseRadiusSquared;
            public float Height;
        }

        public static void Generate(TerrainGrid grid, int seed)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            var random = new System.Random(seed);
            // Kept modest: Mathf.PerlinNoise loses precision far from the origin.
            var offsetX = (float)random.NextDouble() * 1000f;
            var offsetZ = (float)random.NextDouble() * 1000f;

            var shortSide = Math.Min(grid.Width, grid.Height);
            var hills = new Hill[2 + random.Next(3)];
            for (var i = 0; i < hills.Length; i++)
            {
                // Wide and low: 4-8 terraces at the 1 m step, each terrace ring several cells deep.
                var radius = shortSide * Lerp(0.08f, 0.15f, (float)random.NextDouble());
                hills[i] = new Hill
                {
                    X = grid.Width * Lerp(0.2f, 0.8f, (float)random.NextDouble()),
                    Z = grid.Height * Lerp(0.2f, 0.8f, (float)random.NextDouble()),
                    InverseRadiusSquared = 1f / (radius * radius),
                    Height = Lerp(4f, 8f, (float)random.NextDouble()),
                };
            }

            Span<Layer> column = stackalloc Layer[5];
            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var hill = 0f;
                    foreach (var h in hills)
                    {
                        var dx = x - h.X;
                        var dz = z - h.Z;
                        hill += h.Height * (float)Math.Exp(-(dx * dx + dz * dz) * h.InverseRadiusSquared);
                    }

                    var plateau = (Mathf.PerlinNoise(offsetX + x * PlateauFrequency, offsetZ + z * PlateauFrequency) - 0.5f) * PlateauRange;
                    var edges = (Mathf.PerlinNoise(offsetZ + x * EdgeFrequency, offsetX + z * EdgeFrequency) - 0.5f) * EdgeRange;
                    var surface = BedrockThickness + BaseRockDepth + plateau + edges + hill;
                    // Snap to the grid's height step; the rock layer absorbs the difference, so
                    // the soil thicknesses stay as designed and only the surface moves.
                    if (grid.HeightStep > 0f)
                        surface = (float)Math.Round(surface / grid.HeightStep) * grid.HeightStep;

                    var hilliness = Math.Min(hill / FullyHillyAt, 1f);
                    var soil = Lerp(SoilOnPlains, SoilOnHilltops, hilliness);
                    var dirt = soil - TopsoilThickness;
                    var granite = hill * GraniteShareOfHill;
                    if (granite < MinLayerThickness)
                        granite = 0f;
                    var rock = surface - soil - BedrockThickness - granite;
                    // A band too thin to keep goes into the dirt above it rather than vanishing;
                    // dropping it would pull the surface off the height step.
                    if (rock < MinLayerThickness)
                    {
                        dirt += rock;
                        rock = 0f;
                    }

                    var count = 0;
                    column[count++] = new Layer(MaterialTable.Bedrock, BedrockThickness);
                    if (granite >= MinLayerThickness)
                        column[count++] = new Layer(MaterialTable.Granite, granite);
                    if (rock >= MinLayerThickness)
                        column[count++] = new Layer(MaterialTable.Rock, rock);
                    column[count++] = new Layer(MaterialTable.Dirt, dirt);
                    column[count++] = new Layer(MaterialTable.Topsoil, TopsoilThickness);

                    grid.SetColumn(x, z, column.Slice(0, count));
                }
            }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
