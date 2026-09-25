using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// Ore underground (slice 10): every ore is laid down, only ever in place of stone, never
    /// changing the land's shape, buried under grassland, at its own depth, the same way every time.
    /// </summary>
    public class OreDepositTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Seed = 7;
            _settings.Shape = LandShape.Continent;
            _settings.RimWaterCells = 14;
            _settings.LandFeatureSize = 80f;
            _settings.RidgeWidth = 34f;
            _settings.PlateauRadius = 26f;
            _settings.ShallowCells = 6;
            _settings.ChannelCells = 4;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        TerrainGrid Generate(bool ores)
        {
            _settings.Ores = ores;
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
            IslandGenerator.Generate(grid, _settings);
            return grid;
        }

        static Dictionary<MaterialId, float> OreVolumes(TerrainGrid grid)
        {
            var volumes = new Dictionary<MaterialId, float>();
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    for (var i = 0; i < grid.GetLayerCount(x, z); i++)
                    {
                        var layer = grid.GetLayer(x, z, i);
                        if (!MaterialTable.IsOre(layer.Material))
                            continue;
                        volumes.TryGetValue(layer.Material, out var v);
                        volumes[layer.Material] = v + layer.Thickness;
                    }
            return volumes;
        }

        [Test]
        public void EveryOreIsLaidDownOnTheIsland()
        {
            var volumes = OreVolumes(Generate(ores: true));
            foreach (var ore in MaterialTable.Ores)
                Assert.That(volumes.ContainsKey(ore) && volumes[ore] > 50f, Is.True,
                    $"{ore.Value} should have more than 50 m³ on the island");
        }

        [Test]
        public void OreOnlyReplacesStoneAndNeverChangesTheLand()
        {
            var plain = Generate(ores: false);
            var withOre = Generate(ores: true);

            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    Assert.That(withOre.GetSurfaceHeight(x, z), Is.EqualTo(plain.GetSurfaceHeight(x, z)).Within(1e-3f),
                        $"({x}, {z}) moved");
                    Assert.That(withOre.GetTopMaterial(x, z) == plain.GetTopMaterial(x, z)
                        || MaterialTable.IsOre(withOre.GetTopMaterial(x, z)) && MaterialTable.IsStone(plain.GetTopMaterial(x, z)),
                        Is.True, $"({x}, {z}) changed its top from something other than stone");
                }
            }
        }

        [Test]
        public void GrasslandKeepsItsGrassOverTheOre()
        {
            // Ore only ever replaces stone, so grass stays grass; what matters is that the ore is
            // really there under it.
            var grid = Generate(ores: true);
            var survey = new OreSurvey(grid);
            var buried = 0;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (grid.IsGround(x, z) && grid.GetTopMaterial(x, z) == MaterialTable.Topsoil && !survey.At(x, z).IsNone)
                        buried++;
            survey.Dispose();
            Assert.That(buried, Is.GreaterThan(100), "some ore should lie under the grass");
        }

        [Test]
        public void IronLiesInsideItsDepthWindow()
        {
            var grid = Generate(ores: true);
            var spec = _settings.IronOre;
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var surface = grid.GetSurfaceHeight(x, z);
                    var baseHeight = grid.Datum;
                    for (var i = 0; i < grid.GetLayerCount(x, z); i++)
                    {
                        var layer = grid.GetLayer(x, z, i);
                        var top = baseHeight + layer.Thickness;
                        if (layer.Material == MaterialTable.IronOre)
                        {
                            var depth = surface - top;
                            Assert.That(depth, Is.GreaterThanOrEqualTo(spec.DepthMin - 1e-3f), $"({x}, {z}) iron {depth} m down");
                            Assert.That(depth, Is.LessThanOrEqualTo(spec.DepthMax + 1e-3f), $"({x}, {z}) iron {depth} m down");
                        }

                        baseHeight = top;
                    }
                }
            }
        }

        [Test]
        public void TheSameSeedLaysTheSameOre()
        {
            var a = OreVolumes(Generate(ores: true));
            var b = OreVolumes(Generate(ores: true));
            foreach (var ore in MaterialTable.Ores)
                Assert.That(b[ore], Is.EqualTo(a[ore]).Within(1e-3f));
        }

        [Test]
        public void AColumnNeverOverflowsWhenOreSplitsIt()
        {
            var column = new Layer[TerrainGrid.MaxLayersPerCell];
            var count = 0;
            for (var i = 0; i < column.Length; i++)
                column[count++] = new Layer(i % 2 == 0 ? MaterialTable.Rock : MaterialTable.Clay, 1f);

            OreDeposits.Convert(column, ref count, 0f, 2.2f, 2.8f, MaterialTable.IronOre);

            Assert.That(count, Is.EqualTo(column.Length), "a full column is left alone rather than overflowing");
        }
    }
}
