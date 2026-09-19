using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Temporary test terrain: a gently rolling plain with a few hills, layered bedrock, then
    /// granite in the hill cores, then rock, then a dirt and topsoil cap. The soil thins out over
    /// the hills so that digging there reaches rock within a click or two.
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
        const float BaseRockDepth = 5f;

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
            var hills = new Hill[2 + random.Next(2)];
            for (var i = 0; i < hills.Length; i++)
            {
                // Steep enough to read as hills from RTS camera height; gentler ones vanished.
                var radius = shortSide * Lerp(0.04f, 0.07f, (float)random.NextDouble());
                hills[i] = new Hill
                {
                    X = grid.Width * Lerp(0.2f, 0.8f, (float)random.NextDouble()),
                    Z = grid.Height * Lerp(0.2f, 0.8f, (float)random.NextDouble()),
                    InverseRadiusSquared = 1f / (radius * radius),
                    Height = Lerp(16f, 24f, (float)random.NextDouble()),
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

                    var rolling = (Mathf.PerlinNoise(offsetX + x * 0.02f, offsetZ + z * 0.02f) - 0.5f) * 3f;
                    var detail = (Mathf.PerlinNoise(offsetZ + x * 0.09f, offsetX + z * 0.09f) - 0.5f) * 0.6f;
                    var surface = BedrockThickness + BaseRockDepth + rolling + detail + hill;
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
