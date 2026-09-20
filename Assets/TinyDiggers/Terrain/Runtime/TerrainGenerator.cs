using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Temporary test terrain: broad plateaus from large, low-frequency noise, plus a few wide
    /// hills 4-8 m high, so that at a 1 m height step the land reads as wide terraces
    /// (TERRAIN_REFERENCE.md section 1.2). There is deliberately no small-scale noise: it would
    /// fray the terrace edges into single-cell speckle. A medium octave (~40 m) adds texture
    /// between terraces, and a domain warp (~30 m features, ~6 m strength) bends every contour so
    /// the hills do not show up as perfect concentric rings. Layered bedrock, then granite in the
    /// hill cores, then rock, then a dirt and topsoil cap. The soil thins over the hills so that
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

        /// <summary>Cells nearer the disc edge than this have their surface eased to the rim height.</summary>
        public const int RimTaper = 6;

        /// <summary>The height the disc's edge is cut to, so the rim reads as one clean line.</summary>
        public static float RimHeight => BedrockThickness + BaseRockDepth;

        /// <summary>Cells from the middle of the grid to the edge of the disc of land.</summary>
        public static float DiscRadius(TerrainGrid grid) =>
            grid == null ? 0f : Math.Min(grid.Width, grid.Height) * 0.5f - 2f;

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

        /// <summary>A medium octave with ~40 m features and a low amplitude, for texture between terraces.</summary>
        const float MediumFrequency = 1f / 40f;

        const float MediumRange = 1.2f;

        /// <summary>
        /// Domain warp: every height sample, hills included, is taken at a position pushed around
        /// by a second noise field with ~30 m features, by up to about <see cref="WarpStrength"/>
        /// metres. The terrace lines wander instead of forming a bullseye around each hill.
        /// </summary>
        const float WarpFrequency = 1f / 30f;

        const float WarpStrength = 6f;

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
                var hillRadius = shortSide * Lerp(0.08f, 0.15f, (float)random.NextDouble());
                hills[i] = new Hill
                {
                    X = grid.Width * Lerp(0.2f, 0.8f, (float)random.NextDouble()),
                    Z = grid.Height * Lerp(0.2f, 0.8f, (float)random.NextDouble()),
                    InverseRadiusSquared = 1f / (hillRadius * hillRadius),
                    Height = Lerp(4f, 8f, (float)random.NextDouble()),
                };
            }

            // Drawn after the hills so hill placement for a given seed is unchanged by the warp.
            var mediumOffset = (float)random.NextDouble() * 1000f;
            var warpOffsetX = (float)random.NextDouble() * 1000f;
            var warpOffsetZ = (float)random.NextDouble() * 1000f;

            // The world is a disc on a table: everything outside it is void, and the last few
            // cells ease to a single rim height so the edge is a cut, not a cliff of noise.
            var radius = DiscRadius(grid);
            var centreX = grid.Width * 0.5f;
            var centreZ = grid.Height * 0.5f;

            Span<Layer> column = stackalloc Layer[5];
            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var toCentre = (float)Math.Sqrt((x + 0.5f - centreX) * (x + 0.5f - centreX) + (z + 0.5f - centreZ) * (z + 0.5f - centreZ));
                    if (toCentre > radius)
                    {
                        grid.SetVoid(x, z, true);
                        continue;
                    }

                    // Warp first: every field below is sampled at the displaced position.
                    var sx = x + (Mathf.PerlinNoise(warpOffsetX + x * WarpFrequency, warpOffsetZ + z * WarpFrequency) - 0.5f) * 2f * WarpStrength;
                    var sz = z + (Mathf.PerlinNoise(warpOffsetZ + x * WarpFrequency, warpOffsetX + z * WarpFrequency) - 0.5f) * 2f * WarpStrength;

                    var hill = 0f;
                    foreach (var h in hills)
                    {
                        var dx = sx - h.X;
                        var dz = sz - h.Z;
                        hill += h.Height * (float)Math.Exp(-(dx * dx + dz * dz) * h.InverseRadiusSquared);
                    }

                    var plateau = (Mathf.PerlinNoise(offsetX + sx * PlateauFrequency, offsetZ + sz * PlateauFrequency) - 0.5f) * PlateauRange;
                    var edges = (Mathf.PerlinNoise(offsetZ + sx * EdgeFrequency, offsetX + sz * EdgeFrequency) - 0.5f) * EdgeRange;
                    var medium = (Mathf.PerlinNoise(mediumOffset + sx * MediumFrequency, mediumOffset + sz * MediumFrequency) - 0.5f) * MediumRange;
                    var surface = BedrockThickness + BaseRockDepth + plateau + edges + medium + hill;
                    var fromRim = radius - toCentre;
                    if (fromRim < RimTaper)
                    {
                        // Smoothstep in, so the land meets the rim without a visible crease.
                        var t = Math.Min(Math.Max(fromRim / RimTaper, 0f), 1f);
                        surface = RimHeight + (surface - RimHeight) * (t * t * (3f - 2f * t));
                    }

                    // Snap to the grid's height step only after warping and summing, so terrace
                    // lines follow the warped field; the rock layer absorbs the difference, so the
                    // soil thicknesses stay as designed and only the surface moves.
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
