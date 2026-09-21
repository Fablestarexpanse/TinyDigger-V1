using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Terrain.Tests
{
    public class TerrainGeneratorTests
    {
        const float Tolerance = 1e-4f;

        static TerrainGrid Generate(int seed, int size = 96)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault());
            TerrainGenerator.Generate(grid, seed);
            return grid;
        }

        [Test]
        public void SameSeedProducesIdenticalTerrain()
        {
            var a = Generate(7);
            var b = Generate(7);

            for (var z = 0; z < a.Height; z++)
                for (var x = 0; x < a.Width; x++)
                    Assert.That(b.GetSurfaceHeight(x, z), Is.EqualTo(a.GetSurfaceHeight(x, z)), $"cell ({x}, {z})");
        }

        [Test]
        public void DifferentSeedsProduceDifferentTerrain()
        {
            var a = Generate(1);
            var b = Generate(2);

            var differing = 0;
            for (var z = 0; z < a.Height; z++)
                for (var x = 0; x < a.Width; x++)
                    if (!Mathf.Approximately(a.GetSurfaceHeight(x, z), b.GetSurfaceHeight(x, z)))
                        differing++;

            Assert.That(differing, Is.GreaterThan(a.Width * a.Height / 2));
        }

        [Test]
        public void EveryColumnSitsOnBedrockUnderATopsoilCap()
        {
            var grid = Generate(3);

            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    var bottom = grid.GetLayer(x, z, 0);
                    Assert.That(bottom.Material, Is.EqualTo(MaterialTable.Bedrock));
                    Assert.That(bottom.Thickness, Is.EqualTo(TerrainGenerator.BedrockThickness).Within(Tolerance));
                    Assert.That(grid.GetTopMaterial(x, z), Is.EqualTo(MaterialTable.Topsoil));
                    Assert.That(grid.GetLayerCount(x, z), Is.LessThan(TerrainGrid.MaxLayersPerCell),
                        "generation must leave room for the player to tip material on top");
                }
            }
        }

        [Test]
        public void TheHighestPointHasRockWithinAMetreOfTheSurface()
        {
            var grid = Generate(5);

            int peakX = 0, peakZ = 0;
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    if (grid.IsGround(x, z) && grid.GetSurfaceHeight(x, z) > grid.GetSurfaceHeight(peakX, peakZ))
                    {
                        peakX = x;
                        peakZ = z;
                    }

            var soil = 0f;
            for (var i = grid.GetLayerCount(peakX, peakZ) - 1; i >= 0; i--)
            {
                var layer = grid.GetLayer(peakX, peakZ, i);
                if (layer.Material != MaterialTable.Topsoil && layer.Material != MaterialTable.Dirt)
                    break;
                soil += layer.Thickness;
            }

            var mean = 0f;
            var land = 0;
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    if (grid.IsGround(x, z))
                    {
                        mean += grid.GetSurfaceHeight(x, z);
                        land++;
                    }

            mean /= Mathf.Max(1, land);

            Assert.That(grid.GetSurfaceHeight(peakX, peakZ), Is.GreaterThan(mean + 3f), "expected at least one real hill");
            Assert.That(soil, Is.LessThanOrEqualTo(1f));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void AtGameScaleNeighboursNeverDifferByMoreThanOneStep(int seed)
        {
            // Terraces one step high are what make the land read as plateaus, and a single step
            // can never slump, so freshly generated ground is stable.
            var grid = new TerrainGrid(256, 256, MaterialTable.CreateDefault(), heightStep: 1f);
            TerrainGenerator.Generate(grid, seed);

            var flat = 0;
            for (var z = 0; z < grid.Height - 1; z++)
            {
                for (var x = 0; x < grid.Width - 1; x++)
                {
                    // Only where there is land on both sides: the map is a disc, and the drop
                    // from the rim into the void is the edge of the world, not a slope.
                    if (!grid.IsGround(x, z) || !grid.IsGround(x + 1, z) || !grid.IsGround(x, z + 1))
                        continue;
                    var h = grid.GetSurfaceHeight(x, z);
                    var east = Mathf.Abs(h - grid.GetSurfaceHeight(x + 1, z));
                    var north = Mathf.Abs(h - grid.GetSurfaceHeight(x, z + 1));
                    Assert.That(Mathf.Max(east, north), Is.LessThanOrEqualTo(1f + 1e-3f), $"cell ({x}, {z})");
                    if (east < 1e-3f && north < 1e-3f)
                        flat++;
                }
            }

            // The disc covers about pi/4 of the square grid, and most of that should be terrace tops.
            Assert.That(flat, Is.GreaterThan(255 * 255 * 0.55f), "most of the land should be terrace tops, not risers");
        }

        [Test]
        public void WithAHeightStepEverySurfaceLandsOnTheStepGrid()
        {
            var grid = new TerrainGrid(64, 64, MaterialTable.CreateDefault(), heightStep: 1f);
            TerrainGenerator.Generate(grid, 4);

            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var height = grid.GetSurfaceHeight(x, z);
                    Assert.That(height, Is.EqualTo(Mathf.Round(height)).Within(1e-3f), $"cell ({x}, {z})");
                }
            }
        }

        [Test]
        public void GeneratedHeightsEqualTheSumOfTheirLayers()
        {
            var grid = Generate(9, 32);

            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var sum = 0f;
                    for (var i = 0; i < grid.GetLayerCount(x, z); i++)
                        sum += grid.GetLayer(x, z, i).Thickness;
                    // Half a millimetre: the cached height is snapped to the millimetre, which is
                    // what keeps float drift from deciding whether a cell is land or sea.
                    Assert.That(grid.GetSurfaceHeight(x, z), Is.EqualTo(sum).Within(5e-4f));
                }
            }
        }
    }
}
