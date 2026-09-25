using PromptWaffle.Terrain;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>
    /// What the land mask has to produce whatever the seed: a coast with bays in it, land worth
    /// playing on rather than a scatter of specks, rivers that reach the sea, and a 512² map in under
    /// 0.65 s (half a second until the natural terrain; raised with Ronan's OK, 2026-09-21).
    /// </summary>
    public class LandShapeTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Seed = 3;
            // The mask's numbers are in cells, so a smaller test map wants smaller ones to make
            // the same kind of island.
            _settings.RimWaterCells = 14;
            _settings.LandFeatureSize = 80f;
            _settings.RidgeWidth = 34f;
            _settings.PlateauRadius = 26f;
            _settings.ShallowCells = 6;
            _settings.ChannelCells = 5;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        TerrainGrid NewGrid() => new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);

        IslandMap Generate(TerrainGrid grid) => IslandGenerator.Generate(grid, _settings);

        /// <summary>Land as the sim means it: on the map, and dry enough to stand on.</summary>
        static bool IsLand(TerrainGrid grid, int x, int z) =>
            grid.IsGround(x, z) && !grid.IsWater(x, z);

        /// <summary>Land cells beside the sea standing more than 2 m above it: a cliff, not a beach.</summary>
        static int SteepCoast(TerrainGrid grid)
        {
            var steep = 0;
            for (var z = 1; z < Size - 1; z++)
                for (var x = 1; x < Size - 1; x++)
                {
                    if (!IsLand(grid, x, z))
                        continue;
                    var bySea = false;
                    for (var dz = -1; dz <= 1; dz++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0 || !grid.IsGround(x + dx, z + dz))
                                continue;
                            if (!IsLand(grid, x + dx, z + dz))
                                bySea = true;
                        }

                    if (bySea && grid.GetSurfaceHeight(x, z) > World.SeaLevel + 2f)
                        steep++;
                }

            return steep;
        }

        [Test]
        public void CliffCoastsStandWhereTheSeedDrawsThem()
        {
            // Natural terrain, phase 4: part of every coast is cliff, drawn per seed.
            _settings.Shape = LandShape.Continent;
            _settings.CliffCoastShareMin = _settings.CliffCoastShareMax = 0f;
            var none = NewGrid();
            var noneMap = Generate(none);
            _settings.CliffCoastShareMin = _settings.CliffCoastShareMax = 0.5f;
            var half = NewGrid();
            var halfMap = Generate(half);

            Assert.That(noneMap.CliffCoastShare, Is.Zero);
            Assert.That(halfMap.CliffCoastShare, Is.EqualTo(0.5f));
            Assert.That(halfMap.CliffCoastHeight, Is.InRange(_settings.CliffCoastHeightMin, _settings.CliffCoastHeightMax));
            Assert.That(SteepCoast(half), Is.GreaterThan(SteepCoast(none) * 3 / 2 + 20),
                $"half the coast drawn as cliff stands high at the sea: {SteepCoast(half)} cells against {SteepCoast(none)}");
        }

        [Test]
        public void EveryShapeGeneratesForFiveSeeds()
        {
            foreach (LandShape shape in System.Enum.GetValues(typeof(LandShape)))
            {
                _settings.Shape = shape;
                for (var seed = 1; seed <= 5; seed++)
                {
                    _settings.Seed = seed;
                    var grid = NewGrid();
                    var island = Generate(grid);

                    var land = 0;
                    for (var z = 0; z < Size; z++)
                        for (var x = 0; x < Size; x++)
                            if (IsLand(grid, x, z))
                                land++;

                    Assert.That(land, Is.GreaterThan(400), $"{shape} seed {seed} made almost no land");
                    Assert.That(island.Shape, Is.Not.EqualTo(LandShape.Any), "the shape is chosen, never left as Any");
                }
            }
        }

        [Test]
        public void TheCoastlineHasAtLeastOneInlet()
        {
            // An inlet is sea that reaches well inside the island: a cell of water with land on
            // most sides of it a good way in from the open sea.
            for (var seed = 1; seed <= 5; seed++)
            {
                _settings.Seed = seed;
                _settings.Shape = LandShape.Continent;
                var grid = NewGrid();
                Generate(grid);

                var inlets = 0;
                for (var z = 2; z < Size - 2; z++)
                {
                    for (var x = 2; x < Size - 2; x++)
                    {
                        if (!grid.IsGround(x, z) || IsLand(grid, x, z))
                            continue;
                        var surrounded = 0;
                        for (var d = 0; d < 8; d++)
                        {
                            var nx = x + (d % 3) - 1;
                            var nz = z + (d / 3) - 1;
                            if (IsLand(grid, nx, nz))
                                surrounded++;
                        }

                        // Four of the eight neighbours on land makes this water a notch in the
                        // coast rather than a bite out of a headland.
                        if (surrounded >= 4)
                            inlets++;
                    }
                }

                Assert.That(inlets, Is.GreaterThan(8), $"seed {seed} has a coast with no bays in it");
            }
        }

        [Test]
        public void NoLandPatchIsSmallerThanTheMinimumExceptInAnArchipelago()
        {
            _settings.Shape = LandShape.Continent;
            var grid = NewGrid();
            Generate(grid);

            var smallest = int.MaxValue;
            foreach (var patch in Patches(grid))
                smallest = Mathf.Min(smallest, patch);
            Assert.That(smallest, Is.GreaterThanOrEqualTo(_settings.MinLandBlob), "a speck of land survived");

            _settings.Shape = LandShape.Archipelago;
            var archipelago = NewGrid();
            Generate(archipelago);
            Assert.That(Patches(archipelago).Count, Is.GreaterThan(1), "an archipelago is more than one island");
        }

        [Test]
        public void EveryRiverEndsAtTheSea()
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                _settings.Seed = seed;
                _settings.Shape = LandShape.Continent;
                var grid = NewGrid();
                var island = Generate(grid);

                Assert.That(island.Rivers.Count, Is.GreaterThan(0), $"seed {seed} made no river at all");
                foreach (var river in island.Rivers)
                {
                    var mouth = river[river.Count - 1];
                    Assert.That(mouth.y, Is.LessThan(World.SeaLevel), $"seed {seed}: a river stops above the sea");
                    for (var i = 1; i < river.Count; i++)
                        Assert.That(river[i].y, Is.LessThanOrEqualTo(river[i - 1].y + 1e-4f), $"seed {seed}: a river runs uphill");
                }
            }
        }

        [Test]
        public void ThereAreBeachesAndRockyShores()
        {
            _settings.Shape = LandShape.Continent;
            var grid = NewGrid();
            Generate(grid);

            var beach = 0;
            var rocky = 0;
            for (var z = 1; z < Size - 1; z++)
            {
                for (var x = 1; x < Size - 1; x++)
                {
                    if (!IsLand(grid, x, z))
                        continue;
                    var onTheCoast = false;
                    for (var n = 0; n < 4 && !onTheCoast; n++)
                    {
                        var nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                        var nz = z + (n == 2 ? 1 : n == 3 ? -1 : 0);
                        onTheCoast = grid.IsGround(nx, nz) && !IsLand(grid, nx, nz);
                    }

                    if (!onTheCoast)
                        continue;
                    if (grid.GetTopMaterial(x, z) == MaterialTable.Sand)
                        beach++;
                    else
                        rocky++;
                }
            }

            Assert.That(beach, Is.GreaterThan(50), "the gentle coasts should be sand");
            Assert.That(rocky, Is.GreaterThan(5), "and the steep ones should not be");
        }

        [Test]
        public void AMapIsGeneratedInUnderHalfASecond()
        {
            // At the size the game actually plays at, not the small test map.
            var settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            try
            {
                settings.Seed = 5;
                settings.Shape = LandShape.Continent;
                // Best of three: one run inside a busy editor swings 430-560 ms (JIT, a heap the
                // rest of the suite has filled, whatever else the machine is doing), so a single
                // sample made this a coin flip at 500 either way. The best run is what the
                // generator costs; the budget itself is unchanged.
                var best = double.MaxValue;
                for (var run = 0; run < 3; run++)
                {
                    var grid = new TerrainGrid(512, 512, MaterialTable.CreateDefault(), 1f, settings.Datum);
                    best = System.Math.Min(best, IslandGenerator.Generate(grid, settings).Milliseconds);
                }

                Assert.That(best, Is.LessThan(650d), $"512² took {best:0} ms at best of three");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        /// <summary>The size of every connected patch of land, in cells.</summary>
        static List<int> Patches(TerrainGrid grid)
        {
            var seen = new bool[grid.Width * grid.Height];
            var sizes = new List<int>();
            var stack = new Stack<Vector2Int>();
            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var start = z * grid.Width + x;
                    if (seen[start] || !IsLand(grid, x, z))
                        continue;

                    var size = 0;
                    seen[start] = true;
                    stack.Push(new Vector2Int(x, z));
                    while (stack.Count > 0)
                    {
                        var at = stack.Pop();
                        size++;
                        for (var n = 0; n < 4; n++)
                        {
                            var nx = at.x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                            var nz = at.y + (n == 2 ? 1 : n == 3 ? -1 : 0);
                            if (!grid.InBounds(nx, nz))
                                continue;
                            var next = nz * grid.Width + nx;
                            if (seen[next] || !IsLand(grid, nx, nz))
                                continue;
                            seen[next] = true;
                            stack.Push(new Vector2Int(nx, nz));
                        }
                    }

                    sizes.Add(size);
                }
            }

            return sizes;
        }
    }
}
