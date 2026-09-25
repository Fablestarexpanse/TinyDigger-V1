using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// Half-metre cells (slice 11): the same rules in metres at a finer grain. A slope is rise over
    /// metres, not over cells; the generator's settings are metres, so the same settings make the
    /// same island; a ray in metres picks the cell under it.
    /// </summary>
    public class CellSizeTests
    {
        const float Half = 0.5f;

        static TerrainGrid Flat(int size, float cellSize, float heightStep)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault(), heightStep, 0f, cellSize);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f) });
            return grid;
        }

        /// <summary>A one-cell dirt column <paramref name="rise"/> metres above flat bedrock; returns its height after settling.</summary>
        static float SettledColumn(float cellSize, float heightStep, float rise)
        {
            var grid = Flat(9, cellSize, heightStep);
            using var slump = new AngleOfReposeSimulator(grid);
            slump.RunUntilStable();
            grid.SetColumn(4, 4, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, rise) });
            slump.RunUntilStable();
            return grid.GetSurfaceHeight(4, 4);
        }

        [Test]
        public void ACellSizeOfZeroIsRefused()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new TerrainGrid(4, 4, MaterialTable.CreateDefault(), 0.5f, 0f, 0f));
        }

        [Test]
        public void OneStepPerCellStandsAtHalfAMetre()
        {
            // 0.5 m up over 0.5 m across is 45 degrees, under dirt's 50: it stands.
            Assert.That(SettledColumn(Half, Half, 0.5f), Is.EqualTo(2.5f).Within(1e-3f));
        }

        [Test]
        public void AMetreStepStandsOnMetreCellsButSlumpsOnHalfMetreOnes()
        {
            // The same 1 m step: 45 degrees over a metre, 63 over half a metre.
            Assert.That(SettledColumn(1f, Half, 1f), Is.EqualTo(3f).Within(1e-3f), "1 m cells");
            Assert.That(SettledColumn(Half, Half, 1f), Is.LessThan(3f - 1e-3f), "0.5 m cells");
        }

        [Test]
        public void SlopeIsRiseOverMetres()
        {
            const int size = 5;
            var heights = new float[size * size];
            var inDisc = new bool[size * size];
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                {
                    heights[z * size + x] = x * 0.5f;
                    inDisc[z * size + x] = true;
                }

            Assert.That(SurfaceMaterials.SlopeDegrees(heights, inDisc, size, size, 2, 2, 1f), Is.EqualTo(26.57f).Within(0.05f));
            Assert.That(SurfaceMaterials.SlopeDegrees(heights, inDisc, size, size, 2, 2, Half), Is.EqualTo(45f).Within(0.05f));
        }

        [Test]
        public void ARayInMetresPicksTheCellUnderIt()
        {
            var grid = Flat(64, Half, Half);

            var hit = TerrainPicker.TryPick(grid, new Vector3(10.3f, 50f, 20.3f), Vector3.down, out var x, out var z, out var point);

            Assert.That(hit, Is.True);
            Assert.That(new Vector2Int(x, z), Is.EqualTo(new Vector2Int(20, 40)));
            Assert.That(point.x, Is.EqualTo(10.3f).Within(1e-3f), "the hit point comes back in metres");
            Assert.That(point.y, Is.EqualTo(2f).Within(1e-3f));
        }

        [Test]
        public void TheSameSettingsMakeTheSameIslandAtHalfMetreCells()
        {
            var settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            try
            {
                settings.Seed = 3;
                settings.Shape = LandShape.Continent;
                settings.RimWaterCells = 14;
                settings.LandFeatureSize = 80f;
                settings.RidgeWidth = 34f;
                settings.PlateauRadius = 26f;
                settings.ShallowCells = 6;
                settings.ChannelCells = 5;

                var coarse = Measure(settings, 256, 1f);
                var fine = Measure(settings, 512, Half);

                Assert.That(fine.land, Is.EqualTo(coarse.land).Within(coarse.land * 0.1f), $"land m²: {coarse.land:0} at 1 m, {fine.land:0} at 0.5 m");
                Assert.That(fine.peak, Is.EqualTo(coarse.peak).Within(Mathf.Abs(coarse.peak) * 0.1f), $"peak: {coarse.peak:0.0} m at 1 m, {fine.peak:0.0} m at 0.5 m");
                Assert.That(fine.iron, Is.GreaterThan(0f), "ore is laid down at 0.5 m too");
                Assert.That(fine.ironDepthOk, Is.True, "iron lies inside its depth window in metres");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void AGameSizeMapAtHalfMetreCellsIsGeneratedWithinBudget()
        {
            // The scene's map: 1024² half-metre cells over the same ~512 m island. The budget is
            // 1.6 s. It was 500 ms at 512² one-metre cells, then 1.2 s with slice 11, and was raised
            // with Ronan's OK (2026-09-21) for the natural terrain: erosion, cliffs, bigger mountains
            // and rivers.
            var settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            try
            {
                settings.Seed = 5;
                settings.Shape = LandShape.Continent;
                var best = double.MaxValue;
                for (var run = 0; run < 2; run++)
                {
                    var grid = new TerrainGrid(1024, 1024, MaterialTable.CreateDefault(), Half, settings.Datum, Half);
                    best = System.Math.Min(best, IslandGenerator.Generate(grid, settings).Milliseconds);
                }

                Assert.That(best, Is.LessThan(1600d), $"1024² at 0.5 m took {best:0} ms at best of two");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        static (float land, float peak, float iron, bool ironDepthOk) Measure(TerrainGenSettings settings, int size, float cellSize)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault(), cellSize, settings.Datum, cellSize);
            IslandGenerator.Generate(grid, settings);
            var land = 0f;
            var peak = float.MinValue;
            var iron = 0f;
            var ok = true;
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    var surface = grid.GetSurfaceHeight(x, z);
                    peak = Mathf.Max(peak, surface);
                    if (!grid.IsWater(x, z))
                        land += grid.CellArea;

                    var baseHeight = grid.Datum;
                    for (var i = 0; i < grid.GetLayerCount(x, z); i++)
                    {
                        var layer = grid.GetLayer(x, z, i);
                        var top = baseHeight + layer.Thickness;
                        if (layer.Material == MaterialTable.IronOre)
                        {
                            iron += layer.Thickness * grid.CellArea;
                            var depth = surface - top;
                            ok &= depth >= settings.IronOre.DepthMin - 1e-3f && depth <= settings.IronOre.DepthMax + 1e-3f;
                        }

                        baseHeight = top;
                    }
                }
            }

            return (land, peak, iron, ok);
        }
    }
}
