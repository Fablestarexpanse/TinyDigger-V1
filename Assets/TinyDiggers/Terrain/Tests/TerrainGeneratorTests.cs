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
                    if (grid.GetSurfaceHeight(x, z) > grid.GetSurfaceHeight(peakX, peakZ))
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

            Assert.That(grid.GetSurfaceHeight(peakX, peakZ), Is.GreaterThan(TerrainGenerator.BedrockThickness + 10f),
                "expected at least one real hill");
            Assert.That(soil, Is.LessThanOrEqualTo(1f));
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
                    Assert.That(grid.GetSurfaceHeight(x, z), Is.EqualTo(sum).Within(Tolerance));
                }
            }
        }
    }
}
