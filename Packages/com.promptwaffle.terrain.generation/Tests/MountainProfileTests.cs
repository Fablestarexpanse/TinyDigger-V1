using PromptWaffle.Terrain;
using System;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>
    /// What shape a mountain actually is (Ronan, 2026-09-24: *"there is no hills or buildup to
    /// them, just sheer vertical cliffs"*).
    ///
    /// <see cref="TerrainScorecard"/> already reports slope bands, but its top band is "over 35°",
    /// which is the same number for a walkable hillside and for a wall — which is how land made
    /// entirely of walls passed it. These measure two things it cannot:
    ///
    /// - the slope histogram in 10° bands, so 40° and 75° are told apart;
    /// - the **apron**: for each high cell, how far you have to walk to get well below it. A real
    ///   massif spreads its height over a long footslope; a wall puts it all in two cells.
    /// </summary>
    public class MountainProfileTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Shape = LandShape.Continent;
            _settings.RimWaterCells = 14;
            _settings.LandFeatureSize = 80f;
            _settings.RidgeWidth = 34f;
            _settings.PlateauRadius = 26f;
            _settings.ShallowCells = 6;
            _settings.ChannelCells = 4;
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_settings);

        /// <summary>
        /// Slope at a cell over <paramref name="reach"/> cells either side, in degrees.
        ///
        /// The baseline matters more than it looks. Over one cell, a quantised heightfield can
        /// only ever be flat or a whole step, so on a 1 m step and a 1 m cell the only slopes that
        /// exist are 0° and 26.6° — measured that way, every island showed an empty 10–20° band,
        /// which said nothing about the land and everything about the ruler. Three cells either
        /// side spans several steps, so a gentle grade reads as a gentle grade.
        /// </summary>
        static float SlopeDegrees(TerrainGrid grid, int x, int z, int reach = 3)
        {
            float At(int ax, int az) =>
                grid.IsGround(ax, az) ? grid.GetSurfaceHeight(ax, az) : grid.GetSurfaceHeight(x, z);

            var run = 2f * reach * grid.CellSize;
            var gradient = new Vector2(
                (At(x + reach, z) - At(x - reach, z)) / run,
                (At(x, z + reach) - At(x, z - reach)) / run);
            return Mathf.Atan(gradient.magnitude) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// How far, in metres, from this cell to the nearest land at least <paramref name="drop"/>
        /// below it, searched outward in a square. Beyond <paramref name="reach"/> cells it gives
        /// up and returns that reach, so a broad plateau does not cost a whole-map search.
        /// </summary>
        static float ApronMetres(TerrainGrid grid, int x, int z, float drop, int reach)
        {
            var here = grid.GetSurfaceHeight(x, z);
            for (var r = 1; r <= reach; r++)
            {
                for (var dz = -r; dz <= r; dz++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r)
                            continue;
                        var ax = x + dx;
                        var az = z + dz;
                        if (!grid.IsGround(ax, az))
                            continue;
                        if (here - grid.GetSurfaceHeight(ax, az) >= drop)
                            return r * grid.CellSize;
                    }
                }
            }

            return reach * grid.CellSize;
        }

        [TestCase(7)]
        [TestCase(21)]
        [TestCase(99)]
        public void HowSteepTheLandIsAndHowFarItsMountainsSpread(int seed)
        {
            _settings.Seed = seed;
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
            IslandGenerator.Generate(grid, _settings);

            var bands = new int[9]; // 0-10, 10-20 ... 80-90
            var land = 0;
            var highest = float.MinValue;
            for (var z = 1; z < Size - 1; z++)
                for (var x = 1; x < Size - 1; x++)
                {
                    if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel)
                        continue;
                    land++;
                    highest = Mathf.Max(highest, grid.GetSurfaceHeight(x, z));
                    var band = Mathf.Clamp((int)(SlopeDegrees(grid, x, z) / 10f), 0, 8);
                    bands[band]++;
                }

            Assert.That(land, Is.GreaterThan(0), "there is some land to measure");

            // The apron, over the highest tenth of the land: how far to walk to get 10 m below.
            var threshold = highest * 0.6f;
            var apronTotal = 0f;
            var apronCount = 0;
            var steepTotal = 0f;
            for (var z = 1; z < Size - 1; z++)
                for (var x = 1; x < Size - 1; x++)
                {
                    if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < threshold)
                        continue;
                    apronTotal += ApronMetres(grid, x, z, 10f, 40);
                    steepTotal += SlopeDegrees(grid, x, z);
                    apronCount++;
                }

            var text = $"mountain profile — seed {seed}: {land} land cells, highest {highest:0.0} m\n  slope: ";
            for (var band = 0; band < bands.Length; band++)
                text += $"{band * 10}-{band * 10 + 10}° {100f * bands[band] / land:0.0}%  ";
            var walls = 0;
            for (var band = 6; band < bands.Length; band++)
                walls += bands[band];
            text += $"\n  over 60° (walls): {100f * walls / land:0.0}% of land";
            if (apronCount > 0)
                text += $"\n  high ground (over {threshold:0.0} m, {apronCount} cells): mean slope "
                        + $"{steepTotal / apronCount:0.0}°, mean walk to get 10 m lower {apronTotal / apronCount:0.0} m";
            Debug.Log(text);

            Assert.That(highest, Is.GreaterThan(World.SeaLevel), "the island has high ground at all");
        }
    }
}
