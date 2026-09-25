using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// The CPU side of the GPU grass (2026-09-24): the clump mesh is small and stands on its origin,
    /// and the height texture holds the drawn surface's corner heights and follows the ground as it
    /// is dug.
    /// </summary>
    public class GpuGrassTests
    {
        [Test]
        public void AClumpIsAFewThinBladesOneUnitTallAtMost()
        {
            var mesh = GrassClumpMesh.Build(5);
            try
            {
                Assert.That(mesh.triangles.Length / 3, Is.EqualTo(5 * GrassClumpMesh.TrianglesPerBlade));
                Assert.That(mesh.bounds.min.y, Is.EqualTo(0f).Within(1e-4f), "stands on its origin");
                Assert.That(mesh.bounds.max.y, Is.LessThanOrEqualTo(1.001f));
                var uvs = new System.Collections.Generic.List<Vector2>();
                mesh.GetUVs(0, uvs);
                Assert.That(uvs.TrueForAll(uv => uv.y >= 0f && uv.y <= 1f), "uv.y runs root to tip");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TheHeightTextureHoldsTheDrawnCornersAndFollowsADig()
        {
            var grid = new TerrainGrid(8, 8, TinyDiggersMaterials.CreateTable(), heightStep: 0.25f);
            for (var z = 0; z < 8; z++)
                for (var x = 0; x < 8; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 1f), new Layer(MaterialTable.Dirt, 2f + x * 0.25f) });
            using (var heights = new TerrainHeightTexture(grid))
            {
                Assert.That(heights.Corner(3, 3), Is.EqualTo(TerrainSurface.CornerHeight(grid, 3, 3)));
                Assert.That(heights.Texture.width, Is.EqualTo(9), "one texel per corner");

                grid.Remove(3, 3, 1f, new System.Collections.Generic.List<MaterialVolume>());
                Assert.That(heights.Corner(4, 4), Is.EqualTo(TerrainSurface.CornerHeight(grid, 4, 4)), "the dug cell's corners follow");
            }
        }
    }
}
