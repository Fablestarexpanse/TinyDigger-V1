using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    public class TerrainCellMapTests
    {
        TerrainGrid _grid;
        TerrainCellMap _map;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(8, 6, MaterialTable.CreateDefault());
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Rock, 3f),
                        new Layer(MaterialTable.Topsoil, 0.5f),
                    });
            _map = new TerrainCellMap(_grid);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        (int top, int exposed) At(int x, int z)
        {
            var pixel = _map.Texture.GetPixel(x, z);
            return (Mathf.RoundToInt(pixel.r * 255f), Mathf.RoundToInt(pixel.g * 255f));
        }

        [Test]
        public void TheMapIsOneTexelPerCell()
        {
            Assert.That(_map.Texture.width, Is.EqualTo(_grid.Width));
            Assert.That(_map.Texture.height, Is.EqualTo(_grid.Height));
            Assert.That(_map.Texture.filterMode, Is.EqualTo(FilterMode.Point));
        }

        [Test]
        public void RedIsTheMaterialOnTop()
        {
            Assert.That(At(3, 2).top, Is.EqualTo(MaterialTable.Topsoil.Value));
        }

        [Test]
        public void GreenIsWhatACutWouldExposeUnderAThinTopLayer()
        {
            // Half a metre of topsoil is less than one height step, so a cut goes through it into
            // the rock beneath.
            Assert.That(At(3, 2).exposed, Is.EqualTo(MaterialTable.Rock.Value));
        }

        [Test]
        public void AThickTopLayerIsWhatACutExposes()
        {
            _grid.SetColumn(4, 4, new[]
            {
                new Layer(MaterialTable.Rock, 3f),
                new Layer(MaterialTable.Dirt, 4f),
            });
            _map.Flush();
            Assert.That(At(4, 4), Is.EqualTo(((int)MaterialTable.Dirt.Value, (int)MaterialTable.Dirt.Value)));
        }

        [Test]
        public void AChangedCellIsUploadedOnTheNextFlush()
        {
            var before = _map.UploadCount;
            _grid.SetColumn(1, 1, new[] { new Layer(MaterialTable.Sand, 2f) });
            _map.Flush();

            Assert.That(_map.UploadCount, Is.EqualTo(before + 1));
            Assert.That(At(1, 1).top, Is.EqualTo(MaterialTable.Sand.Value));
        }

        [Test]
        public void AFlushWithNothingChangedUploadsNothing()
        {
            var before = _map.UploadCount;
            _map.Flush();
            _map.Flush();
            Assert.That(_map.UploadCount, Is.EqualTo(before));
        }

        [Test]
        public void VoidCellsHaveNoMaterial()
        {
            _grid.SetVoid(0, 0, true);
            _map.Flush();
            Assert.That(At(0, 0), Is.EqualTo((0, 0)));
        }
    }
}
