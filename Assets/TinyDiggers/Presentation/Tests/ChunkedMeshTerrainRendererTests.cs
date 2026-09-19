using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    public class ChunkedMeshTerrainRendererTests
    {
        const float Tolerance = 1e-4f;
        const int ChunkSize = 32;

        GameObject _root;
        TerrainGrid _grid;
        ChunkedMeshTerrainRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Renderer Test Root");
            // 70x40 is deliberately not a multiple of the chunk size: 3x2 chunks, the last row
            // and column partial.
            _grid = new TerrainGrid(70, 40, MaterialTable.CreateDefault());
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.Add(x, z, MaterialTable.Dirt, 2f);
            _renderer = new ChunkedMeshTerrainRenderer(_grid, _root.transform, null, ChunkSize);
        }

        [TearDown]
        public void TearDown()
        {
            _renderer.Dispose();
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void ChunksCoverTheGridIncludingPartialEdgeChunks()
        {
            Assert.That(_renderer.ChunkCountX, Is.EqualTo(3));
            Assert.That(_renderer.ChunkCountZ, Is.EqualTo(2));
            Assert.That(_root.transform.childCount, Is.EqualTo(6));

            // Four vertices per cell: a full chunk and the 6x8 corner chunk.
            Assert.That(_renderer.GetChunkMesh(0, 0).vertexCount, Is.EqualTo(ChunkSize * ChunkSize * 4));
            Assert.That(_renderer.GetChunkMesh(2, 1).vertexCount, Is.EqualTo(6 * 8 * 4));
        }

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
        public void EditingACellOnAChunkEdgeDirtiesTheNeighbourToo()
        {
            // x = 31 is the last column of chunk 0; its right-hand corners belong to chunk 1's cells too.
            _grid.Add(31, 10, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(2));
        }

        [Test]
        public void EditingACellOnAChunkCornerDirtiesAllFourChunks()
        {
            _grid.Add(32, 32, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(4));
        }

        [Test]
        public void RepeatedEditsToOneChunkQueueItOnce()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 1f);
            _grid.Add(11, 10, MaterialTable.Dirt, 1f);
            _grid.Add(10, 11, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void RebuildMovesTheSurfaceToTheNewHeight()
        {
            // Raise a 3x3 patch so the centre cell's corners are each surrounded by raised cells.
            for (var z = 9; z <= 11; z++)
                for (var x = 9; x <= 11; x++)
                    _grid.Add(x, z, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(0));
            var mesh = _renderer.GetChunkMesh(0, 0);
            var centreQuad = (10 * ChunkSize + 10) * 4;
            var vertices = mesh.vertices;
            for (var k = 0; k < 4; k++)
                Assert.That(vertices[centreQuad + k].y, Is.EqualTo(6f).Within(Tolerance));
            Assert.That(mesh.bounds.max.y, Is.EqualTo(6f).Within(Tolerance));
        }

        [Test]
        public void CornerHeightsAverageTheCellsAroundThem()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            // The raised cell's own quad has four corners, each shared with three flat neighbours:
            // (6 + 2 + 2 + 2) / 4 = 3.
            var vertices = _renderer.GetChunkMesh(0, 0).vertices;
            var quad = (10 * ChunkSize + 10) * 4;
            for (var k = 0; k < 4; k++)
                Assert.That(vertices[quad + k].y, Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void VertexColourFollowsTheTopMaterial()
        {
            _grid.Add(5, 5, MaterialTable.Sand, 1f);

            _renderer.Rebuild();

            var colors = _renderer.GetChunkMesh(0, 0).colors32;
            var expected = _grid.Materials.Get(MaterialTable.Sand).Color;
            var quad = (5 * ChunkSize + 5) * 4;
            Assert.That(colors[quad], Is.EqualTo(expected));
            Assert.That(colors[quad + 4], Is.EqualTo(_grid.Materials.Get(MaterialTable.Dirt).Color),
                "the neighbouring cell keeps its own colour");
        }

        [Test]
        public void FlatGroundFacesStraightUp()
        {
            var normals = _renderer.GetChunkMesh(1, 0).normals;

            Assert.That(normals[0].y, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void DisposeRemovesTheChunksAndStopsListening()
        {
            _renderer.Dispose();

            Assert.That(_root.transform.childCount, Is.EqualTo(0));
            _grid.Add(10, 10, MaterialTable.Dirt, 1f);
            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(0));
        }
    }
}
