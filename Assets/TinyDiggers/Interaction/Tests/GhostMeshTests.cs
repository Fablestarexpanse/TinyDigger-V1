using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>The finished-ground surface the road ghost draws (2026-09-24).</summary>
    public class GhostMeshTests
    {
        static TerrainGrid Flat(int size, float height, float cellSize)
        {
            var grid = new TerrainGrid(size, size, TinyDiggersMaterials.CreateTable(), 0.5f, 0f, cellSize);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, height) });
            return grid;
        }

        [Test]
        public void AFinishedCellMeetsTheUntouchedGroundRoundIt()
        {
            var grid = Flat(8, 5f, 0.5f);
            var heights = new Dictionary<int, float> { [3 * grid.Width + 3] = 4f };
            var vertices = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();

            GhostMesh.Surface(grid, heights, _ => new Color32(200, 100, 50, 235), 0f, vertices, colors, triangles);

            Assert.That(vertices.Count, Is.EqualTo(4));
            Assert.That(triangles.Count, Is.EqualTo(6));
            // Each corner is shared with three cells still at 5 m: the dip is eased into them.
            foreach (var vertex in vertices)
                Assert.That(vertex.y, Is.EqualTo(4.75f).Within(1e-4f));
            Assert.That(vertices[0].x, Is.EqualTo(1.5f).Within(1e-4f), "in metres, not cells");
            Assert.That(colors[0].a, Is.EqualTo(235), "the colour's alpha is kept");
        }

        [Test]
        public void TheEdgeOfTheMapIsNotAveragedIn()
        {
            var grid = Flat(8, 5f, 1f);
            var heights = new Dictionary<int, float> { [0] = 3f };
            var vertices = new List<Vector3>();

            GhostMesh.Surface(grid, heights, _ => new Color32(200, 100, 50, 255), 0f, vertices, new List<Color32>(), new List<int>());

            // The corner at the map's own corner touches only this cell.
            Assert.That(vertices[0].y, Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void ASlopeFacingAwayFromTheLightIsDarkerThanFlatGround()
        {
            var grid = Flat(12, 5f, 1f);
            var flat = new Dictionary<int, float> { [5 * grid.Width + 5] = 5f };
            var slope = new Dictionary<int, float>();
            // A face falling towards the light's far side: higher to the south-west, lower to the north-east.
            for (var z = 3; z <= 7; z++)
                for (var x = 3; x <= 7; x++)
                    slope[z * grid.Width + x] = 5f - (x + z - 10) * 0.8f;
            var flatColors = new List<Color32>();
            var slopeColors = new List<Color32>();

            GhostMesh.Surface(grid, flat, _ => new Color32(200, 200, 200, 255), 0f, new List<Vector3>(), flatColors, new List<int>());
            GhostMesh.Surface(grid, slope, _ => new Color32(200, 200, 200, 255), 0f, new List<Vector3>(), slopeColors, new List<int>());

            var middle = 0;
            var i = 0;
            foreach (var key in slope.Keys)
            {
                if (key == 5 * grid.Width + 5)
                    middle = i;
                i++;
            }

            Assert.That(slopeColors[middle * 4].r, Is.LessThan(flatColors[0].r));
        }
    }
}
