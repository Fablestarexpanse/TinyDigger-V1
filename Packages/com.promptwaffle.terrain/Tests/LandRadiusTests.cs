using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// LandRadius: the island is laid out in a frame of its own, so a bigger disc with the same
    /// seed and the same LandRadius grows open sea round the same island, and nothing past the
    /// radius is land.
    /// </summary>
    public class LandRadiusTests
    {
        const int Small = 256;
        const int Big = 448;

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
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        TerrainGrid Generate(int size)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
            IslandGenerator.Generate(grid, _settings);
            return grid;
        }

        [Test]
        public void TheSameIslandSitsInTheMiddleOfABiggerDisc()
        {
            // The small disc's whole radius, so its island is the one it always made.
            _settings.LandRadius = TerrainGenerator.DiscRadius(new TerrainGrid(Small, Small, MaterialTable.CreateDefault(), 1f, _settings.Datum));
            var small = Generate(Small);
            var big = Generate(Big);
            var shift = (Big - Small) / 2;

            int land = 0, differ = 0;
            for (var z = 0; z < Small; z++)
                for (var x = 0; x < Small; x++)
                {
                    if (!small.IsGround(x, z) || small.IsWater(x, z))
                        continue;
                    land++;
                    var a = small.GetSurfaceHeight(x, z);
                    var b = big.GetSurfaceHeight(x + shift, z + shift);
                    if (Mathf.Abs(a - b) > 1e-3f || big.IsWater(x + shift, z + shift) || small.GetTopMaterial(x, z) != big.GetTopMaterial(x + shift, z + shift))
                        differ++;
                }

            Assert.That(land, Is.GreaterThan(Small * Small / 20), "there is an island to compare");
            Assert.That(differ, Is.EqualTo(0), $"{differ} of {land} land cells differ on the bigger disc");
        }

        [Test]
        public void NothingPastTheLandRadiusIsLand()
        {
            _settings.LandRadius = 100f;
            var big = Generate(Big);
            var centre = new Vector2(Big * 0.5f, Big * 0.5f);
            int land = 0, outside = 0;
            for (var z = 0; z < Big; z++)
                for (var x = 0; x < Big; x++)
                {
                    if (!big.IsGround(x, z) || big.IsWater(x, z))
                        continue;
                    land++;
                    if (Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), centre) > 100f)
                        outside++;
                }

            Assert.That(land, Is.GreaterThan(0));
            Assert.That(outside, Is.EqualTo(0), "land past the land radius");
        }
    }
}
