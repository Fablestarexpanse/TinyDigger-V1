using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace TinyDiggers.Presentation.Tests
{
    public class SmoothedTerrainRendererTests
    {
        const float Tolerance = 1e-4f;
        const int ChunkSize = 32;

        GameObject _root;
        TerrainGrid _grid;
        SmoothedTerrainRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Renderer Test Root");
            // 70x40 is deliberately not a multiple of the chunk size: 3x2 chunks, the last row
            // and column partial. Flat 2m of undisturbed dirt everywhere.
            _grid = new TerrainGrid(70, 40, MaterialTable.CreateDefault());
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, 2f) });
            _renderer = new SmoothedTerrainRenderer(_grid, _root.transform, null, ChunkSize);
        }

        [TearDown]
        public void TearDown()
        {
            _renderer.Dispose();
            Object.DestroyImmediate(_root);
        }

        static int Quad(int i, int j) => (j * ChunkSize + i) * 4;

        Color32 ColorOf(MaterialId id) => _grid.Materials.Get(id).Color;

        static Color32 ExposedAt(Mesh mesh, int vertex)
        {
            var exposed = new List<Vector4>();
            mesh.GetUVs(1, exposed);
            var v = exposed[vertex];
            return new Color32((byte)Mathf.RoundToInt(v.x * 255f), (byte)Mathf.RoundToInt(v.y * 255f), (byte)Mathf.RoundToInt(v.z * 255f), 255);
        }

        // --- chunking ------------------------------------------------------------------------

        [Test]
        public void ChunksCoverTheGridWithOneQuadPerCell()
        {
            Assert.That(_renderer.ChunkCountX, Is.EqualTo(3));
            Assert.That(_renderer.ChunkCountZ, Is.EqualTo(2));
            Assert.That(_root.transform.childCount, Is.EqualTo(6));
            Assert.That(_renderer.GetChunkMesh(0, 0).vertexCount, Is.EqualTo(ChunkSize * ChunkSize * 4));
            Assert.That(_renderer.GetChunkMesh(2, 1).vertexCount, Is.EqualTo(6 * 8 * 4));
            Assert.That(_renderer.TriangleCount, Is.EqualTo(70L * 40L * 2L));
        }

        [Test]
        public void ACornerBesideTheVoidTakesTheHeightOfTheGroundNotTheVoid()
        {
            // The disc's rim: a void cell has no layers, so its surface sits at the datum. The
            // mesh used to average it into the rim corners, which hung a comb of blades under the
            // edge of the world once the plinth that hid them was gone.
            _grid.SetVoid(5, 5, true);
            _renderer.Rebuild();

            var corner = _renderer.GetChunkMesh(0, 0).vertices[Quad(4, 5) + 3];
            Assert.That(corner.y, Is.EqualTo(SmoothedTerrainRenderer.CornerHeight(_grid, 5, 5)).Within(Tolerance));
            Assert.That(corner.y, Is.EqualTo(2f).Within(Tolerance), "the ground beside the void is 2 m of dirt");
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
        public void EditingACellOnAChunkEdgeDirtiesTheNeighbourToo()
        {
            // Cell 31's right-hand corners are shared with chunk 1's first column.
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
        public void RepeatedEditsToOneCellAreRecordedOnce()
        {
            // A slump tick can touch the same cell many times; the renderer should expand it into
            // chunks once.
            for (var i = 0; i < 10; i++)
                _grid.Add(10, 10, MaterialTable.Dirt, 0.1f);

            Assert.That(_renderer.PendingCellCount, Is.EqualTo(1));
            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(1));
            Assert.That(_renderer.PendingCellCount, Is.EqualTo(0), "expanded into chunks");
        }

        [Test]
        public void RebuildingKeepsTheSameQuadLayout()
        {
            var mesh = _renderer.GetChunkMesh(0, 0);
            var indicesBefore = mesh.GetIndexCount(0);

            _grid.Add(10, 10, MaterialTable.Dirt, 3f);
            _renderer.Rebuild();

            Assert.That(mesh.GetIndexCount(0), Is.EqualTo(indicesBefore));
            Assert.That(mesh.vertices[Quad(10, 10)].y, Is.GreaterThan(2f), "the new height was uploaded");
        }

        [Test]
        public void EditingACellTwoFromAChunkEdgeStillDirtiesTheNeighbour()
        {
            // Cell 30 moves corner 31, whose height feeds the normal at corner 32, the first
            // column of chunk 1.
            _grid.Add(30, 10, MaterialTable.Dirt, 1f);

            Assert.That(_renderer.PendingChunkCount, Is.EqualTo(2));
        }

        // --- normals ---------------------------------------------------------------------------

        [Test]
        public void NormalsAtChunkEdgesMatchAnUnchunkedMesh()
        {
            // Rough ground across the chunk borders: a ramp plus a lump that straddles x = 32.
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, 2f + x * 0.1f + (x - 32) * (x - 32) % 3) });
            _renderer.Rebuild();

            var root = new GameObject("Unchunked");
            // Chunk size 127 covers the whole 70x40 grid in one chunk: no borders at all.
            var unchunked = new SmoothedTerrainRenderer(_grid, root.transform, null, 127);
            try
            {
                Assert.That(unchunked.ChunkCountX * unchunked.ChunkCountZ, Is.EqualTo(1));
                var whole = unchunked.GetChunkMesh(0, 0);
                var wholeNormals = whole.normals;

                // Every cell along the x = 32 border, on both sides, and along z = 32.
                var checkedVertices = 0;
                for (var z = 0; z < _grid.Height; z++)
                {
                    foreach (var x in new[] { 31, 32 })
                    {
                        var chunkX = x / ChunkSize;
                        var chunkZ = z / ChunkSize;
                        var chunked = _renderer.GetChunkMesh(chunkX, chunkZ).normals;
                        var local = ((z - chunkZ * ChunkSize) * ChunkWidth(chunkX) + (x - chunkX * ChunkSize)) * 4;
                        var global = (z * _grid.Width + x) * 4;
                        for (var k = 0; k < 4; k++, checkedVertices++)
                            Assert.That(chunked[local + k], Is.EqualTo(wholeNormals[global + k]).Using(Vector3EqualityComparer.Instance),
                                $"cell ({x}, {z}) corner {k}");
                    }
                }

                for (var x = 0; x < _grid.Width; x++)
                {
                    foreach (var z in new[] { 31, 32 })
                    {
                        if (z >= _grid.Height)
                            continue;
                        var chunkX = x / ChunkSize;
                        var chunkZ = z / ChunkSize;
                        var chunked = _renderer.GetChunkMesh(chunkX, chunkZ).normals;
                        var local = ((z - chunkZ * ChunkSize) * ChunkWidth(chunkX) + (x - chunkX * ChunkSize)) * 4;
                        var global = (z * _grid.Width + x) * 4;
                        for (var k = 0; k < 4; k++, checkedVertices++)
                            Assert.That(chunked[local + k], Is.EqualTo(wholeNormals[global + k]).Using(Vector3EqualityComparer.Instance),
                                $"cell ({x}, {z}) corner {k}");
                    }
                }

                Assert.That(checkedVertices, Is.GreaterThan(500));
            }
            finally
            {
                unchunked.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void NormalsFollowTheSlopeOfTheGround()
        {
            // A uniform ramp rising 0.5m per cell along +x: interior normals lean back towards -x.
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, 1f + 0.5f * x) });
            _renderer.Rebuild();

            var normal = _renderer.GetChunkMesh(0, 0).normals[Quad(10, 10)];
            var expected = new Vector3(-0.5f, 1f, 0f).normalized;
            Assert.That(normal, Is.EqualTo(expected).Using(Vector3EqualityComparer.Instance));
        }

        [Test]
        public void NeighbouringQuadsShareNormalsAtTheirCommonCorner()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 4f);
            _renderer.Rebuild();

            // Corner (11, 11) is c for cell (10, 10) and a for cell (11, 11): smooth, not faceted.
            var normals = _renderer.GetChunkMesh(0, 0).normals;
            Assert.That(normals[Quad(10, 10) + 2], Is.EqualTo(normals[Quad(11, 11)]).Using(Vector3EqualityComparer.Instance));
        }

        int ChunkWidth(int chunkX) => Mathf.Min(ChunkSize, _grid.Width - chunkX * ChunkSize);

        // --- geometry --------------------------------------------------------------------------

        [Test]
        public void CornerHeightsAverageTheCellsAroundThem()
        {
            _grid.Add(10, 10, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            // Each corner of the raised cell is shared with three flat neighbours: (6 + 2 + 2 + 2) / 4.
            var vertices = _renderer.GetChunkMesh(0, 0).vertices;
            for (var k = 0; k < 4; k++)
                Assert.That(vertices[Quad(10, 10) + k].y, Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void ARaisedPatchReachesItsFullHeightInside()
        {
            for (var z = 9; z <= 11; z++)
                for (var x = 9; x <= 11; x++)
                    _grid.Add(x, z, MaterialTable.Dirt, 4f);

            _renderer.Rebuild();

            var mesh = _renderer.GetChunkMesh(0, 0);
            var vertices = mesh.vertices;
            for (var k = 0; k < 4; k++)
                Assert.That(vertices[Quad(10, 10) + k].y, Is.EqualTo(6f).Within(Tolerance));
            Assert.That(mesh.bounds.max.y, Is.EqualTo(6f).Within(Tolerance));
        }

        [Test]
        public void FlatGroundFacesStraightUp()
        {
            var normals = _renderer.GetChunkMesh(1, 0).normals;

            Assert.That(normals[0].y, Is.EqualTo(1f).Within(Tolerance));
        }

        // --- colour -----------------------------------------------------------------------------

        [Test]
        public void VertexColourIsTheTopMaterial()
        {
            _grid.Add(5, 5, MaterialTable.Sand, 1f);

            _renderer.Rebuild();

            var colors = _renderer.GetChunkMesh(0, 0).colors32;
            Assert.That(colors[Quad(5, 5)], Is.EqualTo(ColorOf(MaterialTable.Sand)));
            Assert.That(colors[Quad(6, 5)], Is.EqualTo(ColorOf(MaterialTable.Dirt)), "the neighbour keeps its own colour");
        }

        [Test]
        public void ASteepFaceCarriesTheColourOfTheLayerItCutsThrough()
        {
            // A tall column of rock under topsoil beside flat dirt. The slope down from it runs
            // across the neighbour's quad, between heights 3.5 and 2, which is rock in the tall
            // column.
            _grid.SetColumn(10, 10, new[]
            {
                new Layer(MaterialTable.Bedrock, 1f),
                new Layer(MaterialTable.Rock, 6f),
                new Layer(MaterialTable.Topsoil, 1f),
            });

            _renderer.Rebuild();

            var mesh = _renderer.GetChunkMesh(0, 0);
            var slope = Quad(11, 10);
            Assert.That(mesh.normals[slope].y, Is.LessThan(Mathf.Cos(45f * Mathf.Deg2Rad)), "the face should be steep");
            Assert.That(mesh.colors32[slope], Is.EqualTo(ColorOf(MaterialTable.Dirt)), "gentle-face colour is still the top");
            Assert.That(ExposedAt(mesh, slope), Is.EqualTo(ColorOf(MaterialTable.Rock)));
            Assert.That(mesh.colors32[Quad(10, 10)], Is.EqualTo(ColorOf(MaterialTable.Topsoil)));
        }

        [Test]
        public void EdgeFlagsMarkSidesWhereTheTopMaterialChanges()
        {
            _grid.Add(5, 5, MaterialTable.Sand, 1f);
            _renderer.Rebuild();

            var edges = new List<Vector4>();
            var mesh = _renderer.GetChunkMesh(0, 0);
            mesh.GetUVs(2, edges);

            Assert.That(edges[Quad(5, 5)], Is.EqualTo(new Vector4(1f, 1f, 1f, 1f)), "sand surrounded by dirt on all four sides");
            Assert.That(edges[Quad(6, 5)], Is.EqualTo(new Vector4(1f, 0f, 0f, 0f)), "the dirt to the east only borders sand on its west");
            Assert.That(edges[Quad(20, 20)], Is.EqualTo(Vector4.zero));
        }

        [Test]
        public void FlatGroundExposesItsOwnTop()
        {
            var mesh = _renderer.GetChunkMesh(0, 0);

            Assert.That(ExposedAt(mesh, Quad(20, 20)), Is.EqualTo(ColorOf(MaterialTable.Dirt)));
        }

        [Test]
        public void ChangingAColumnsLayersRecoloursTheFacesBesideIt()
        {
            _grid.SetColumn(10, 10, new[] { new Layer(MaterialTable.Bedrock, 1f), new Layer(MaterialTable.Rock, 7f) });
            _renderer.Rebuild();

            _grid.SetColumn(10, 10, new[] { new Layer(MaterialTable.Bedrock, 1f), new Layer(MaterialTable.Clay, 7f) });
            _renderer.Rebuild();

            Assert.That(ExposedAt(_renderer.GetChunkMesh(0, 0), Quad(11, 10)), Is.EqualTo(ColorOf(MaterialTable.Clay)));
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
