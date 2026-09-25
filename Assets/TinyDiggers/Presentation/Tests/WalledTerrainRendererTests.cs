using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    public class WalledTerrainRendererTests
    {
        const float Tolerance = 1e-4f;
        const int ChunkSize = 32;

        GameObject _root;
        TerrainGrid _grid;
        WalledTerrainRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Renderer Test Root");
            // 70x40 is deliberately not a multiple of the chunk size: 3x2 chunks, the last row
            // and column partial. Flat 2m of dirt everywhere.
            _grid = new TerrainGrid(70, 40, MaterialTable.CreateDefault());
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, 2f) });
            _renderer = new WalledTerrainRenderer(_grid, _root.transform, null, ChunkSize);
        }

        [TearDown]
        public void TearDown()
        {
            _renderer.Dispose();
            Object.DestroyImmediate(_root);
        }

        // --- chunking ------------------------------------------------------------------------

        [Test]
        public void ChunksCoverTheGridIncludingPartialEdgeChunks()
        {
            Assert.That(_renderer.ChunkCountX, Is.EqualTo(3));
            Assert.That(_renderer.ChunkCountZ, Is.EqualTo(2));
            Assert.That(_root.transform.childCount, Is.EqualTo(6));
        }

        [Test]
        public void FlatGroundDrawsTopsPlusWallsOnlyAlongTheWorldBorder()
        {
            // Chunk 0,0: 32x32 tops, plus the -x and -z world-border walls (32 cells each).
            Assert.That(_renderer.GetChunkMesh(0, 0).vertexCount, Is.EqualTo((32 * 32 + 32 + 32) * 4));
            // Chunk 1,0 is interior along x: tops plus the -z border only.
            Assert.That(_renderer.GetChunkMesh(1, 0).vertexCount, Is.EqualTo((32 * 32 + 32) * 4));
            // Chunk 2,1 is the 6x8 corner: tops plus the +x border (8) and +z border (6).
            Assert.That(_renderer.GetChunkMesh(2, 1).vertexCount, Is.EqualTo((6 * 8 + 8 + 6) * 4));
        }

        [Test]
        public void TriangleCountIsTwoPerQuad()
        {
            long quads = 0;
            for (var cz = 0; cz < _renderer.ChunkCountZ; cz++)
                for (var cx = 0; cx < _renderer.ChunkCountX; cx++)
                    quads += _renderer.GetChunkMesh(cx, cz).vertexCount / 4;

            Assert.That(_renderer.TriangleCount, Is.EqualTo(quads * 2));
        }

        // --- dirty tracking --------------------------------------------------------------------

        [Test]
        public void ConstructionBuildsEverythingAndLeavesNothingPending()
        {
            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(0));
        }

        [Test]
        public void EditingAnInteriorCellDirtiesOnlyItsChunk()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void EditingTheLastColumnOfAChunkDirtiesOnlyThatChunk()
        {
            // Cell 31 owns the wall on its +x edge, so nothing in chunk 1 depends on it.
            _grid.Add(31, 10, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void EditingTheFirstColumnOfAChunkDirtiesTheChunkThatOwnsItsWestWall()
        {
            // The wall between cells 31 and 32 is built by chunk 0 from cell 32's layers.
            _grid.Add(32, 10, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(2));
        }

        [Test]
        public void EditingTheFirstCellOfAChunkDirtiesTheChunksBehindAndBeside()
        {
            _grid.Add(32, 32, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(3));
        }

        [Test]
        public void RepeatedEditsToOneChunkQueueItOnce()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 1f);
            _grid.Add(11, 10, MaterialTable.Dirt, 1f);
            _grid.Add(10, 11, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(1));
        }

        // --- geometry --------------------------------------------------------------------------

        [Test]
        public void ASingleRaisedCellRendersAtItsTrueHeight()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            var mesh = _renderer.GetChunkMesh(0, 0);
            var top = FindTopQuad(mesh, 10, 10);
            var vertices = mesh.vertices;
            for (var k = 0; k < 4; k++)
                Assert.That(vertices[top + k].y, Is.EqualTo(6f).Within(Tolerance));
            Assert.That(vertices[FindTopQuad(mesh, 11, 10)].y, Is.EqualTo(2f).Within(Tolerance),
                "the neighbour is not dragged up with it");
        }

        [Test]
        public void ARaisedCellGetsAWallOnEverySide()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            var walls = WallsOf(_renderer.GetChunkMesh(0, 0), 10, 10);
            Assert.That(walls.Count, Is.EqualTo(4));
            foreach (var wall in walls)
            {
                Assert.That(wall.Bottom, Is.EqualTo(2f).Within(Tolerance));
                Assert.That(wall.Top, Is.EqualTo(6f).Within(Tolerance));
            }
        }

        [Test]
        public void WallsAreBandedByTheLayersTheyCut()
        {
            // Dirt 0-2, sand 2-3, topsoil 3-4, standing 2m proud of its dirt neighbours.
            _grid.SetColumn(5, 5, new[]
            {
                new Layer(MaterialTable.Dirt, 2f),
                new Layer(MaterialTable.Sand, 1f),
                new Layer(MaterialTable.Topsoil, 1f),
            });

            _renderer.Rebuild();

            var mesh = _renderer.GetChunkMesh(0, 0);
            var east = WallsOf(mesh, 5, 5).FindAll(w => w.Normal == Vector3.right);
            Assert.That(east.Count, Is.EqualTo(2));

            var sand = _grid.Materials.Get(MaterialTable.Sand).Color;
            var topsoil = _grid.Materials.Get(MaterialTable.Topsoil).Color;
            var lower = east[0].Bottom < east[1].Bottom ? east[0] : east[1];
            var upper = east[0].Bottom < east[1].Bottom ? east[1] : east[0];
            Assert.That(lower.Color, Is.EqualTo(sand));
            Assert.That(lower.Bottom, Is.EqualTo(2f).Within(Tolerance));
            Assert.That(lower.Top, Is.EqualTo(3f).Within(Tolerance));
            Assert.That(upper.Color, Is.EqualTo(topsoil));
            Assert.That(upper.Top, Is.EqualTo(4f).Within(Tolerance));
        }

        [Test]
        public void APitShowsTheLayersOfTheCellsAroundIt()
        {
            // Cap the east neighbour with topsoil, then dig the pit cell down 1.5m into the dirt.
            _grid.SetColumn(11, 10, new[] { new Layer(MaterialTable.Dirt, 2f), new Layer(MaterialTable.Topsoil, 0.5f) });
            var removed = new List<MaterialVolume>();
            _grid.Remove(10, 10, 1.5f, removed);

            _renderer.Rebuild();

            // The pit's east wall belongs to the pit cell's edge but shows the neighbour's column:
            // dirt from 0.5 to 2, topsoil from 2 to 2.5, facing back into the pit.
            var mesh = _renderer.GetChunkMesh(0, 0);
            var faces = WallsOf(mesh, 10, 10).FindAll(w => w.Normal == Vector3.left && Mathf.Approximately(w.X, 11f));
            Assert.That(faces.Count, Is.EqualTo(2));
            var colors = new HashSet<Color32> { faces[0].Color, faces[1].Color };
            Assert.That(colors, Does.Contain(_grid.Materials.Get(MaterialTable.Dirt).Color));
            Assert.That(colors, Does.Contain(_grid.Materials.Get(MaterialTable.Topsoil).Color));
        }

        [Test]
        public void TopsFaceUpAndTakeTheTopMaterialColour()
        {
            _grid.Add(5, 5, MaterialTable.Sand, 1f);

            _renderer.Rebuild();

            var mesh = _renderer.GetChunkMesh(0, 0);
            var top = FindTopQuad(mesh, 5, 5);
            Assert.That(mesh.normals[top], Is.EqualTo(Vector3.up));
            Assert.That(mesh.colors32[top], Is.EqualTo(_grid.Materials.Get(MaterialTable.Sand).Color));
            Assert.That(mesh.colors32[FindTopQuad(mesh, 6, 5)], Is.EqualTo(_grid.Materials.Get(MaterialTable.Dirt).Color));
        }

        [Test]
        public void BoundsCoverTheTallestColumn()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            Assert.That(_renderer.GetChunkMesh(0, 0).bounds.max.y, Is.EqualTo(6f).Within(Tolerance));
        }

        [Test]
        public void DisposeRemovesTheChunksAndStopsListening()
        {
            _renderer.Dispose();

            Assert.That(_root.transform.childCount, Is.EqualTo(0));
            _grid.Add(10, 10, MaterialTable.Dirt, 1f);
            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(0));
        }

        // --- helpers ---------------------------------------------------------------------------

        struct Wall
        {
            public Vector3 Normal;
            public Color32 Color;
            public float Bottom;
            public float Top;
            public float X;
        }

        /// <summary>First vertex of the upward-facing quad over local cell (i, j).</summary>
        static int FindTopQuad(Mesh mesh, int i, int j)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            for (var q = 0; q < vertices.Length; q += 4)
                if (normals[q] == Vector3.up && Mathf.Approximately(vertices[q].x, i) && Mathf.Approximately(vertices[q].z, j))
                    return q;
            Assert.Fail($"No top quad for cell ({i}, {j}).");
            return -1;
        }

        /// <summary>Every vertical quad lying on one of the four edges of local cell (i, j).</summary>
        static List<Wall> WallsOf(Mesh mesh, int i, int j)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var colors = mesh.colors32;
            var walls = new List<Wall>();
            for (var q = 0; q < vertices.Length; q += 4)
            {
                if (Mathf.Abs(normals[q].y) > 0.5f)
                    continue;

                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                float bottom = float.MaxValue, top = float.MinValue;
                for (var k = 0; k < 4; k++)
                {
                    var v = vertices[q + k];
                    minX = Mathf.Min(minX, v.x);
                    maxX = Mathf.Max(maxX, v.x);
                    minZ = Mathf.Min(minZ, v.z);
                    maxZ = Mathf.Max(maxZ, v.z);
                    bottom = Mathf.Min(bottom, v.y);
                    top = Mathf.Max(top, v.y);
                }

                var onXEdge = Mathf.Approximately(minX, maxX) && (Mathf.Approximately(minX, i) || Mathf.Approximately(minX, i + 1))
                    && Mathf.Approximately(minZ, j) && Mathf.Approximately(maxZ, j + 1);
                var onZEdge = Mathf.Approximately(minZ, maxZ) && (Mathf.Approximately(minZ, j) || Mathf.Approximately(minZ, j + 1))
                    && Mathf.Approximately(minX, i) && Mathf.Approximately(maxX, i + 1);
                if (onXEdge || onZEdge)
                    walls.Add(new Wall { Normal = normals[q], Color = colors[q], Bottom = bottom, Top = top, X = minX });
            }

            return walls;
        }
    }
}
