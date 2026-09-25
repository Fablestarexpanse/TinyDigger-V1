using PromptWaffle.Terrain;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>
    /// What the surface pass has to guarantee: one material across a uniform hillside rather than
    /// stripes along the contours, no single-cell speckle, and sand only where a beach could be.
    /// </summary>
    public class SurfaceMaterialTests
    {
        const int Size = 96;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Seed = 4;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        /// <summary>A uniform slope of <paramref name="degrees"/>, quantised to whole metres as the land is.</summary>
        static float[] Ramp(float degrees, int size)
        {
            var rise = Mathf.Tan(degrees * Mathf.Deg2Rad);
            var heights = new float[size * size];
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    heights[z * size + x] = Mathf.Round(10f + x * rise);
            return heights;
        }

        static bool[] AllOnMap(int size)
        {
            var inDisc = new bool[size * size];
            for (var i = 0; i < inDisc.Length; i++)
                inDisc[i] = true;
            return inDisc;
        }

        static float[] FarFromWater(int size)
        {
            var toWater = new float[size * size];
            for (var i = 0; i < toWater.Length; i++)
                toWater[i] = 999f;
            return toWater;
        }

        [TestCase(8f)]
        [TestCase(55f)]
        public void AUniformSlopeWellInsideABandIsOneMaterial(float degrees)
        {
            var materials = SurfaceMaterials.Assign(Ramp(degrees, Size), AllOnMap(Size), FarFromWater(Size),
                Size, Size, _settings, Vector2.zero);

            var counts = new Dictionary<MaterialId, int>();
            var total = 0;
            for (var z = 8; z < Size - 8; z++)
            {
                for (var x = 8; x < Size - 8; x++)
                {
                    var material = materials[z * Size + x];
                    counts.TryGetValue(material, out var count);
                    counts[material] = count + 1;
                    total++;
                }
            }

            var biggest = 0;
            foreach (var pair in counts)
                biggest = Mathf.Max(biggest, pair.Value);
            Assert.That(biggest / (float)total, Is.GreaterThan(0.9f),
                $"a uniform {degrees}° hillside should be one material, not a set of contour bands");
        }

        [Test]
        public void AUniformSlopeNeverAlternatesAlongTheContours()
        {
            // The case this pass exists for: at 30° the quantised ramp is a staircase, one metre up
            // every two cells, so a per-cell slope alternates from row to row and used to paint the
            // hillside in bands that followed the contours. Patches are fine; stripes are not.
            var materials = SurfaceMaterials.Assign(Ramp(30f, Size), AllOnMap(Size), FarFromWater(Size),
                Size, Size, _settings, Vector2.zero);

            // A contour on this ramp runs along z, so walking down one is walking along a line of
            // constant height: it must not change material every cell or two.
            for (var x = 20; x < Size - 20; x += 7)
            {
                var flips = 0;
                for (var z = 9; z < Size - 8; z++)
                    if (materials[z * Size + x] != materials[(z - 1) * Size + x])
                        flips++;
                // A handful, because patches of noise cross the line; a striped hillside would
                // change every cell or two, which over this stretch is dozens.
                Assert.That(flips, Is.LessThan(10), $"the contour at x={x} changes material {flips} times");
            }

        }

        [Test]
        public void SteeperGroundGetsABarerMaterial()
        {
            var inDisc = AllOnMap(Size);
            var gentle = SurfaceMaterials.Assign(Ramp(5f, Size), inDisc, FarFromWater(Size), Size, Size, _settings, Vector2.zero);
            var steep = SurfaceMaterials.Assign(Ramp(60f, Size), inDisc, FarFromWater(Size), Size, Size, _settings, Vector2.zero);

            Assert.That(gentle[Size / 2 * Size + Size / 2], Is.EqualTo(MaterialTable.Topsoil), "gentle ground is grass");
            Assert.That(steep[Size / 2 * Size + Size / 2], Is.EqualTo(MaterialTable.Rock), "a cliff is rock");
        }

        [Test]
        public void ThereAreNoSingleCellMaterialIslands()
        {
            var heights = Ramp(25f, Size);
            // Roughen it, so the thresholds are crossed back and forth the way real land does.
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    heights[z * Size + x] += Mathf.Round(Mathf.PerlinNoise(x * 0.25f, z * 0.25f) * 3f);

            var inDisc = AllOnMap(Size);
            var materials = SurfaceMaterials.Assign(heights, inDisc, FarFromWater(Size), Size, Size, _settings, new Vector2(11f, 23f));

            var smallest = int.MaxValue;
            foreach (var patch in Patches(materials, Size))
                smallest = Mathf.Min(smallest, patch);
            Assert.That(smallest, Is.GreaterThanOrEqualTo(_settings.MinMaterialPatch),
                "a patch smaller than the minimum survived the cleanup");
        }

        [Test]
        public void SandNeverClimbsAboveTheBeach()
        {
            var settings = _settings;
            var inDisc = AllOnMap(Size);
            var heights = Ramp(20f, Size);
            // Water off the left edge, so the low ground is coastal and the high ground is not.
            var toWater = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    toWater[z * Size + x] = x;

            var materials = SurfaceMaterials.Assign(heights, inDisc, toWater, Size, Size, settings, Vector2.zero);
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (materials[z * Size + x] != MaterialTable.Sand)
                        continue;
                    Assert.That(heights[z * Size + x], Is.LessThanOrEqualTo(settings.SandMaxHeight),
                        $"sand at ({x}, {z}) is above the beach");
                    Assert.That(toWater[z * Size + x], Is.LessThanOrEqualTo(settings.SandMaxDistance),
                        $"sand at ({x}, {z}) is inland");
                }
            }
        }

        [Test]
        public void TheIslandItselfHasNoSpeckleAndNoHighSand()
        {
            var settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            try
            {
                settings.Seed = 11;
                settings.Shape = LandShape.Continent;
                var grid = new TerrainGrid(256, 256, TestOres.CreateTable(), 1f, settings.Datum);
                settings.RimWaterCells = 14;
                settings.LandFeatureSize = 80f;
                settings.RidgeWidth = 34f;
                settings.PlateauRadius = 26f;
                settings.ShallowCells = 6;
                settings.ChannelCells = 5;
                IslandGenerator.Generate(grid, settings, TestOres.Ores);

                for (var z = 0; z < grid.Height; z++)
                {
                    for (var x = 0; x < grid.Width; x++)
                    {
                        if (!grid.IsGround(x, z) || grid.IsWater(x, z))
                            continue;
                        if (grid.GetTopMaterial(x, z) != MaterialTable.Sand)
                            continue;
                        Assert.That(grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(settings.SandMaxHeight + 0.01f),
                            $"sand at ({x}, {z}) is {grid.GetSurfaceHeight(x, z)} m up");
                    }
                }

                var lonely = 0;
                for (var z = 1; z < grid.Height - 1; z++)
                {
                    for (var x = 1; x < grid.Width - 1; x++)
                    {
                        if (!grid.IsGround(x, z) || grid.IsWater(x, z))
                            continue;
                        var material = grid.GetTopMaterial(x, z);
                        var same = 0;
                        for (var n = 0; n < 4; n++)
                        {
                            var nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                            var nz = z + (n == 2 ? 1 : n == 3 ? -1 : 0);
                            // The seabed counts: the material map covers it, and a beach cell
                            // whose neighbour is the sand it runs into is not alone.
                            if (grid.IsGround(nx, nz) && grid.GetTopMaterial(nx, nz) == material)
                                same++;
                        }

                        if (same == 0)
                            lonely++;
                    }
                }

                // Not quite zero: a one-cell knoll that stands a metre above the beach around it is
                // legitimately not sand, and nothing should pretend otherwise. What matters is
                // that speckle is not a texture across the island.
                var land = 0;
                for (var z = 0; z < grid.Height; z++)
                    for (var x = 0; x < grid.Width; x++)
                        if (grid.IsGround(x, z) && !grid.IsWater(x, z))
                            land++;
                Assert.That(lonely, Is.LessThan(land / 1000),
                    $"{lonely} of {land} land cells are the only one of their material in their neighbourhood");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        /// <summary>The size of every connected patch of one material.</summary>
        static List<int> Patches(MaterialId[] materials, int size)
        {
            var seen = new bool[materials.Length];
            var sizes = new List<int>();
            var stack = new Stack<Vector2Int>();
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    var start = z * size + x;
                    if (seen[start])
                        continue;

                    var material = materials[start];
                    var count = 0;
                    seen[start] = true;
                    stack.Push(new Vector2Int(x, z));
                    while (stack.Count > 0)
                    {
                        var at = stack.Pop();
                        count++;
                        for (var n = 0; n < 4; n++)
                        {
                            var nx = at.x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                            var nz = at.y + (n == 2 ? 1 : n == 3 ? -1 : 0);
                            if (nx < 0 || nz < 0 || nx >= size || nz >= size)
                                continue;
                            var next = nz * size + nx;
                            if (seen[next] || materials[next] != material)
                                continue;
                            seen[next] = true;
                            stack.Push(new Vector2Int(nx, nz));
                        }
                    }

                    sizes.Add(count);
                }
            }

            return sizes;
        }
    }
}
