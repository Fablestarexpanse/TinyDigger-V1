using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// Cliffs: rock is allowed to stand in a step taller than the crew can climb, soil is not, and
    /// what the generator leaves standing has to still be standing after the slump has settled.
    /// </summary>
    public class CliffTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;
        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Seed = 11;
            _settings.Shape = LandShape.Continent;
            _settings.RimWaterCells = 14;
            _settings.LandFeatureSize = 80f;
            _settings.RidgeWidth = 70f;
            _settings.PlateauRadius = 26f;
            _settings.ShallowCells = 6;
            _settings.ChannelCells = 5;
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        bool IsLand(int x, int z) => _grid.IsGround(x, z) && !_grid.IsWater(x, z);

        [Test]
        public void OnlyRockStandsInACliffAndNoneIsTallerThanTheLimit()
        {
            IslandGenerator.Generate(_grid, _settings);

            var cliffs = 0;
            for (var z = 1; z < Size - 1; z++)
            {
                for (var x = 1; x < Size - 1; x++)
                {
                    if (!IsLand(x, z))
                        continue;
                    var height = _grid.GetSurfaceHeight(x, z);
                    foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                    {
                        if (!IsLand(x + dx, z + dz))
                            continue;
                        var step = Mathf.Abs(height - _grid.GetSurfaceHeight(x + dx, z + dz));
                        if (step <= 1f + 1e-3f)
                            continue;

                        cliffs++;
                        Assert.That(step, Is.LessThanOrEqualTo(_settings.MaxCliffStep + 1e-3f),
                            $"the step at ({x}, {z}) is {step} m");
                        Assert.That(IslandGenerator.IsStone(_grid.GetTopMaterial(x, z)), Is.True,
                            $"({x}, {z}) stands in a cliff but is not rock");
                        Assert.That(IslandGenerator.IsStone(_grid.GetTopMaterial(x + dx, z + dz)), Is.True,
                            $"({x + dx}, {z + dz}) is the foot of a cliff but is not rock");
                    }
                }
            }

            Assert.That(cliffs, Is.GreaterThan(50), "an island with a ridge on it should have some cliffs");
        }

        [Test]
        public void AFreshlyGeneratedCliffIsStableOnceTheSlumpHasSettled()
        {
            IslandGenerator.Generate(_grid, _settings);

            var before = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    before[z * Size + x] = _grid.GetSurfaceHeight(x, z);

            using (var slump = new AngleOfReposeSimulator(_grid))
            {
                // Every cell queued, as if the whole map had just been disturbed.
                for (var z = 0; z < Size; z++)
                    for (var x = 0; x < Size; x++)
                        if (_grid.IsGround(x, z))
                            _grid.Add(x, z, _grid.GetTopMaterial(x, z), 0f);
                slump.RunUntilStable();
            }

            var moved = 0;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (Mathf.Abs(before[z * Size + x] - _grid.GetSurfaceHeight(x, z)) > 1e-3f)
                        moved++;

            Assert.That(moved, Is.Zero, $"{moved} cells slid after the land was generated");
        }
    }
}
