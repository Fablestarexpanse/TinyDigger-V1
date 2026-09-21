using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// Levels of detail for the smoothed terrain:
    /// - the first build makes each chunk once, at the level its distance asks for;
    /// - a moving viewer refines chunks nearest first, within the per-frame budget, and swaps
    ///   back to a level already built without building it again;
    /// - an edit rebuilds the shown level and makes the others stale;
    /// - a coarse chunk's corners sit on the grid's own corner heights, with nothing undefined.
    /// </summary>
    public class TerrainLodTests
    {
        const int Size = 256;
        const int ChunkSize = 32;

        GameObject _root;
        TerrainGrid _grid;
        SmoothedTerrainRenderer _renderer;
        TerrainLod _lod;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("LOD Test Root");
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, 2f + (x + z) / 64) });
            _lod = new TerrainLod
            {
                Distances = new[] { 40f, 90f, 150f },
                BuildsPerFrame = 4,
                Viewer = new Vector3(16f, 20f, 16f),
                HasViewer = true,
            };
            _renderer = new SmoothedTerrainRenderer(_grid, _root.transform, null, ChunkSize, _lod);
        }

        [TearDown]
        public void TearDown()
        {
            _renderer.Dispose();
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void TheFirstBuildMakesEachChunkAtTheLevelItIsSeenAt()
        {
            Assert.That(_renderer.GetChunkLevel(0, 0), Is.EqualTo(0), "the chunk under the viewer");
            Assert.That(_renderer.GetChunkLevel(7, 7), Is.EqualTo(3), "the far corner");
            Assert.That(_renderer.LodPending, Is.EqualTo(0), "nothing left to build");

            var near = _renderer.GetChunkMesh(0, 0).vertexCount;
            var far = _renderer.GetChunkMesh(7, 7).vertexCount;
            Assert.That(far, Is.LessThan(near / 16), $"level 3 has {far} vertices to level 0's {near}");
        }

        [Test]
        public void AMovingViewerRefinesNearestFirstWithinTheBudget()
        {
            _renderer.UpdateLod(new Vector3(240f, 20f, 240f));
            var frames = 0;
            while (_renderer.LodPending > 0 && frames < 100)
            {
                _renderer.Rebuild();
                Assert.That(_renderer.LastRebuiltChunkCount, Is.LessThanOrEqualTo(_lod.BuildsPerFrame));
                frames++;
            }

            Assert.That(_renderer.LodPending, Is.EqualTo(0));
            Assert.That(_renderer.GetChunkLevel(7, 7), Is.EqualTo(0), "the chunk now under the viewer");
            Assert.That(_renderer.GetChunkLevel(0, 0), Is.EqualTo(3), "the one left behind");

            // Back again: chunk (0, 0) was built at level 0 before and nothing has changed, so it
            // swaps straight back without a build.
            _renderer.UpdateLod(new Vector3(16f, 20f, 16f));
            _renderer.Rebuild();
            Assert.That(_renderer.GetChunkLevel(0, 0), Is.EqualTo(0));
        }

        [Test]
        public void AnEditRebuildsTheShownLevelAndStalesTheRest()
        {
            // The far corner, shown at level 3, gets a 6 m tower in the middle.
            var before = MaxHeight(_renderer.GetChunkMesh(7, 7));
            _grid.SetColumn(240, 240, new[] { new Layer(MaterialTable.Rock, 20f) });
            _renderer.Rebuild();
            Assert.That(_renderer.GetChunkLevel(7, 7), Is.EqualTo(3));
            Assert.That(MaxHeight(_renderer.GetChunkMesh(7, 7)), Is.GreaterThan(before), "the shown level has the tower");

            // Brought close, level 0 has to be built fresh, with the tower in it.
            _renderer.UpdateLod(new Vector3(240f, 20f, 240f));
            for (var i = 0; i < 100 && _renderer.GetChunkLevel(7, 7) != 0; i++)
                _renderer.Rebuild();
            // Corners average the four cells round them, so the tower shows at its corners' height.
            var expected = Mathf.Max(
                Mathf.Max(TerrainSurface.CornerHeight(_grid, 240, 240), TerrainSurface.CornerHeight(_grid, 241, 240)),
                Mathf.Max(TerrainSurface.CornerHeight(_grid, 240, 241), TerrainSurface.CornerHeight(_grid, 241, 241)));
            Assert.That(MaxHeight(_renderer.GetChunkMesh(7, 7)), Is.EqualTo(expected).Within(1e-3f));
        }

        [Test]
        public void CoarseCornersSitOnTheGridsCornerHeights()
        {
            var mesh = _renderer.GetChunkMesh(7, 7);
            var vertices = new List<Vector3>();
            mesh.GetVertices(vertices);
            foreach (var v in vertices)
                Assert.That(float.IsNaN(v.y) || float.IsInfinity(v.y), Is.False);

            // The chunk's own corner (224, 224) and far corner (256, 256).
            Assert.That(vertices.Exists(v => v.x == 0f && v.z == 0f
                && Mathf.Abs(v.y - TerrainSurface.CornerHeight(_grid, 224, 224)) < 1e-4f), Is.True);
            Assert.That(vertices.Exists(v => v.x == 32f && v.z == 32f
                && Mathf.Abs(v.y - TerrainSurface.CornerHeight(_grid, 256, 256)) < 1e-4f), Is.True);
        }

        static float MaxHeight(Mesh mesh)
        {
            var vertices = new List<Vector3>();
            mesh.GetVertices(vertices);
            var max = float.MinValue;
            foreach (var v in vertices)
                max = Mathf.Max(max, v.y);
            return max;
        }
    }
}
