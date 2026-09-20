using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Terrain.Tests
{
    public class IslandGeneratorTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Seed = 7;
            // The generator's own defaults, at a smaller map: the shelf and the mountain are given
            // in cells, so they are scaled to keep the same shape of island.
            _settings.ShelfCells = 20;
            _settings.ChannelCells = 4;
            _settings.MountainRadius = 35f;
            _settings.ValleyRadius = 28f;
            _settings.CoastNoiseCells = 8f;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        TerrainGrid NewGrid() =>
            new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);

        [Test]
        public void TheRimIsUnderTheSeaAndTheMiddleIsAboveIt()
        {
            var grid = NewGrid();
            IslandGenerator.Generate(grid, _settings);

            var centre = Size / 2;
            Assert.That(grid.GetSurfaceHeight(centre, centre), Is.GreaterThan(World.SeaLevel),
                "the middle of the island is land");

            // Just inside the disc, all the way round.
            var radius = TerrainGenerator.DiscRadius(grid) - 1f;
            for (var degrees = 0; degrees < 360; degrees += 15)
            {
                var radians = degrees * Mathf.Deg2Rad;
                var x = Mathf.RoundToInt(centre + Mathf.Cos(radians) * radius);
                var z = Mathf.RoundToInt(centre + Mathf.Sin(radians) * radius);
                if (!grid.IsGround(x, z))
                    continue;
                Assert.That(grid.GetSurfaceHeight(x, z), Is.LessThan(World.SeaLevel),
                    $"the rim at {degrees}° is under the sea");
                Assert.That(grid.IsWater(x, z), Is.True);
            }
        }

        [Test]
        public void TheCoastlineIsNotACircle()
        {
            var grid = NewGrid();
            IslandGenerator.Generate(grid, _settings);

            var centre = new Vector2(Size * 0.5f, Size * 0.5f);
            var shortest = float.MaxValue;
            var longest = 0f;
            for (var degrees = 0; degrees < 360; degrees += 5)
            {
                var radians = degrees * Mathf.Deg2Rad;
                var direction = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
                for (var r = 0f; r < Size * 0.5f; r += 1f)
                {
                    var point = centre + direction * r;
                    var x = Mathf.RoundToInt(point.x);
                    var z = Mathf.RoundToInt(point.y);
                    if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) >= World.SeaLevel)
                        continue;
                    shortest = Mathf.Min(shortest, r);
                    longest = Mathf.Max(longest, r);
                    break;
                }
            }

            Assert.That(longest - shortest, Is.GreaterThan(4f),
                "the shore should wander in and out by several cells, not trace a circle");
        }

        [Test]
        public void TheRiverOnlyEverDescendsAndEndsInTheSea()
        {
            var grid = NewGrid();
            var island = IslandGenerator.Generate(grid, _settings);

            Assert.That(island.River.Count, Is.GreaterThan(4), "there is a river");
            for (var i = 1; i < island.River.Count; i++)
                Assert.That(island.River[i].y, Is.LessThanOrEqualTo(island.River[i - 1].y + 1e-4f),
                    $"point {i} runs uphill");

            Assert.That(island.River[0].y, Is.GreaterThan(World.SeaLevel), "it starts on the mountain");
            Assert.That(island.River[island.River.Count - 1].y, Is.LessThan(World.SeaLevel),
                "it ends below sea level");
        }

        [Test]
        public void LandNeverStepsMoreThanOneMetreToANeighbour()
        {
            var grid = NewGrid();
            IslandGenerator.Generate(grid, _settings);

            for (var z = 1; z < Size - 1; z++)
            {
                for (var x = 1; x < Size - 1; x++)
                {
                    if (!grid.IsGround(x, z) || grid.IsWater(x, z))
                        continue;
                    var height = grid.GetSurfaceHeight(x, z);
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        if (!grid.IsGround(x + dx, z + dz) || grid.IsWater(x + dx, z + dz))
                            continue;
                        var neighbour = grid.GetSurfaceHeight(x + dx, z + dz);
                        Assert.That(Mathf.Abs(height - neighbour), Is.LessThanOrEqualTo(1f + 1e-3f),
                            $"cell ({x}, {z}) steps to ({x + dx}, {z + dz})");
                    }
                }
            }
        }

        [Test]
        public void EverySurfaceLandsOnTheHeightStep()
        {
            var grid = NewGrid();
            IslandGenerator.Generate(grid, _settings);

            for (var z = 0; z < Size; z += 3)
            {
                for (var x = 0; x < Size; x += 3)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    var height = grid.GetSurfaceHeight(x, z);
                    Assert.That(Mathf.Abs(height - Mathf.Round(height)), Is.LessThan(1e-3f), $"cell ({x}, {z})");
                }
            }
        }

        [Test]
        public void EveryColumnStandsOnBedrockFromTheDatum()
        {
            var grid = NewGrid();
            IslandGenerator.Generate(grid, _settings);

            for (var z = 0; z < Size; z += 5)
            {
                for (var x = 0; x < Size; x += 5)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    Assert.That(grid.GetLayerCount(x, z), Is.GreaterThan(0), $"cell ({x}, {z}) has layers");
                    Assert.That(grid.GetLayer(x, z, 0).Material, Is.EqualTo(MaterialTable.Bedrock),
                        $"cell ({x}, {z}) stands on bedrock");
                }
            }
        }

        [Test]
        public void TheSameSeedGivesTheSameIsland()
        {
            var first = NewGrid();
            var firstIsland = IslandGenerator.Generate(first, _settings);
            var second = NewGrid();
            var secondIsland = IslandGenerator.Generate(second, _settings);

            for (var z = 0; z < Size; z += 7)
                for (var x = 0; x < Size; x += 7)
                    Assert.That(second.GetSurfaceHeight(x, z), Is.EqualTo(first.GetSurfaceHeight(x, z)).Within(1e-4f),
                        $"cell ({x}, {z})");

            Assert.That(secondIsland.River.Count, Is.EqualTo(firstIsland.River.Count));
            Assert.That(secondIsland.Peak, Is.EqualTo(firstIsland.Peak));
        }

        [Test]
        public void ADifferentSeedGivesADifferentIsland()
        {
            var first = NewGrid();
            IslandGenerator.Generate(first, _settings);

            _settings.Seed = 8;
            var second = NewGrid();
            IslandGenerator.Generate(second, _settings);

            var differences = 0;
            for (var z = 0; z < Size; z += 7)
                for (var x = 0; x < Size; x += 7)
                    if (Mathf.Abs(second.GetSurfaceHeight(x, z) - first.GetSurfaceHeight(x, z)) > 0.5f)
                        differences++;

            Assert.That(differences, Is.GreaterThan(50), "a new seed should be a new island");
        }

        [Test]
        public void ThereIsSandAroundSeaLevelAndTopsoilWellAboveIt()
        {
            var grid = NewGrid();
            IslandGenerator.Generate(grid, _settings);

            var sandNearTheSea = 0;
            var topsoilInland = 0;
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    var height = grid.GetSurfaceHeight(x, z);
                    var top = grid.GetTopMaterial(x, z);
                    if (Mathf.Abs(height - World.SeaLevel) <= _settings.SandBand && top == MaterialTable.Sand)
                        sandNearTheSea++;
                    if (height > World.SeaLevel + _settings.SandBand + 4f && top == MaterialTable.Topsoil)
                        topsoilInland++;
                }
            }

            Assert.That(sandNearTheSea, Is.GreaterThan(100), "the coast is sand");
            Assert.That(topsoilInland, Is.GreaterThan(500), "the land above it is grass");
        }
    }
}
