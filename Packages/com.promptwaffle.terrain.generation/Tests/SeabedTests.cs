using PromptWaffle.Terrain;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>
    /// The open sea floor:
    /// - turning it on leaves the island exactly as it was;
    /// - nothing breaks the surface;
    /// - the ring has both shallows and deep water;
    /// - the water by the dam stays deep;
    /// - reefs are rock and banks are sand.
    /// </summary>
    public class SeabedTests
    {
        const int Size = 448;
        const float LandRadius = 100f;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Seed = 3;
            _settings.RimWaterCells = 14;
            _settings.LandFeatureSize = 80f;
            _settings.RidgeWidth = 34f;
            _settings.PlateauRadius = 26f;
            _settings.ShallowCells = 6;
            _settings.ChannelCells = 5;
            _settings.LandRadius = LandRadius;
            // Scaled to a small map: a ring about 120 m wide.
            _settings.SeabedFeatureSize = 90f;
            _settings.SandbankLength = 200f;
            _settings.SandbankWidth = 60f;
            _settings.ShoalSize = 60f;
            _settings.ReefSize = 16f;
            _settings.ReefCoverage = 0.3f;
            _settings.SeabedShoreGap = 15f;
            _settings.SeabedRimGap = 15f;
            _settings.SeabedClearDepth = 1f;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        TerrainGrid Generate(bool openSeaFloor)
        {
            _settings.OpenSeaFloor = openSeaFloor;
            var grid = new TerrainGrid(Size, Size, TestOres.CreateTable(), 1f, _settings.Datum);
            IslandGenerator.Generate(grid, _settings, TestOres.Ores);
            return grid;
        }

        static float FromCentre(int x, int z) => Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), new Vector2(Size * 0.5f, Size * 0.5f));

        [Test]
        public void TheIslandIsUntouched()
        {
            var flat = Generate(false);
            var shaped = Generate(true);
            int land = 0, differ = 0;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                {
                    if (!flat.IsGround(x, z) || flat.IsWater(x, z))
                        continue;
                    land++;
                    if (Mathf.Abs(flat.GetSurfaceHeight(x, z) - shaped.GetSurfaceHeight(x, z)) > 1e-3f
                        || flat.GetTopMaterial(x, z) != shaped.GetTopMaterial(x, z))
                        differ++;
                }

            Assert.That(land, Is.GreaterThan(1000));
            Assert.That(differ, Is.EqualTo(0), $"{differ} of {land} island cells changed");
        }

        [Test]
        public void NothingBreaksTheSurfaceAndTheRingHasShallowsAndDeeps()
        {
            var flat = Generate(false);
            var grid = Generate(true);
            var radius = TerrainGenerator.DiscRadius(grid);
            int ring = 0, shallow = 0, deep = 0, broke = 0;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                {
                    // Sea floor only: the island's own beach sits at the waterline.
                    if (!grid.IsGround(x, z) || flat.GetSurfaceHeight(x, z) > -_settings.SeabedClearDepth + 1e-3f)
                        continue;
                    var h = grid.GetSurfaceHeight(x, z);
                    if (h > -_settings.SeabedClearDepth + 1e-3f)
                        broke++;
                    var r = FromCentre(x, z);
                    if (r < LandRadius + 30f || r > radius - 30f)
                        continue;
                    ring++;
                    if (h > -4f)
                        shallow++;
                    if (h < -12f)
                        deep++;
                }

            Assert.That(broke, Is.EqualTo(0), "sea floor above the clear depth");
            Assert.That(ring, Is.GreaterThan(10000));
            // The test ring is narrow, so few of its features reach full height; the real map has
            // about 10% of its open ring under 4 m.
            Assert.That(shallow / (float)ring, Is.GreaterThan(0.01f), $"shallows: {shallow} of {ring}");
            Assert.That(deep / (float)ring, Is.GreaterThan(0.2f), $"deep water: {deep} of {ring}");
        }

        [Test]
        public void TheWaterByTheDamStaysDeep()
        {
            var grid = Generate(true);
            var radius = TerrainGenerator.DiscRadius(grid);
            var shallowest = float.MinValue;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (grid.IsGround(x, z) && FromCentre(x, z) > radius - _settings.SeabedRimGap + 1f)
                        shallowest = Mathf.Max(shallowest, grid.GetSurfaceHeight(x, z));

            Assert.That(shallowest, Is.LessThanOrEqualTo(_settings.ChannelDepth + 1f));
        }

        [Test]
        public void ReefsAreRockAndBanksAreSand()
        {
            var flat = Generate(false);
            var grid = Generate(true);
            var radius = TerrainGenerator.DiscRadius(grid);
            int rock = 0, sand = 0;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                {
                    if (!grid.IsGround(x, z) || !flat.IsWater(x, z))
                        continue;
                    var r = FromCentre(x, z);
                    if (r < LandRadius + 30f || r > radius - 30f || grid.GetSurfaceHeight(x, z) < _settings.ShelfFarDepth)
                        continue;
                    var top = grid.GetTopMaterial(x, z);
                    if (top == MaterialTable.Rock)
                        rock++;
                    else if (top == MaterialTable.Sand)
                        sand++;
                }

            Assert.That(rock, Is.GreaterThan(0), "shallow rock out at sea: the reefs");
            Assert.That(sand, Is.GreaterThan(rock), "most shallows are sand: banks and shoals");
        }
    }
}
