using System;
using NUnit.Framework;

namespace PromptWaffle.Terrain.Tests
{
    public class TerrainBrushTests
    {
        const float Tolerance = 1e-4f;

        TerrainGrid _grid;
        float[] _removed;

        [SetUp]
        public void SetUp()
        {
            // Bedrock 5, dirt 1, topsoil 0.5 everywhere.
            _grid = new TerrainGrid(12, 12, MaterialTable.CreateDefault());
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 5f),
                        new Layer(MaterialTable.Dirt, 1f),
                        new Layer(MaterialTable.Topsoil, 0.5f),
                    });
            _removed = new float[_grid.Materials.MaxId + 1];
        }

        int CountCellsBelow(float height)
        {
            var count = 0;
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    if (_grid.GetSurfaceHeight(x, z) < height - Tolerance)
                        count++;
            return count;
        }

        [Test]
        public void RadiusZeroDigsOnlyTheCentreCell()
        {
            TerrainBrush.Dig(_grid, 5, 5, 0, 0.25f, _removed);

            Assert.That(CountCellsBelow(6.5f), Is.EqualTo(1));
            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.EqualTo(6.25f).Within(Tolerance));
        }

        [TestCase(1, 5)]
        [TestCase(2, 13)]
        [TestCase(3, 29)]
        public void RadiusCoversADiscOfCells(int radius, int expectedCells)
        {
            TerrainBrush.Dig(_grid, 5, 5, radius, 0.25f, _removed);

            Assert.That(CountCellsBelow(6.5f), Is.EqualTo(expectedCells));
        }

        [Test]
        public void DigTotalsWhatCameOutByMaterial()
        {
            // 5 cells x 1m in place: 0.5 topsoil and 0.5 dirt from each, reported loose (x1.25)
            // in their disturbed forms.
            var total = TerrainBrush.Dig(_grid, 5, 5, 1, 1f, _removed, out var inPlace);

            Assert.That(inPlace, Is.EqualTo(5f).Within(Tolerance));
            Assert.That(total, Is.EqualTo(5f * 1.25f).Within(Tolerance));
            Assert.That(_removed[MaterialTable.Dirt.Value], Is.EqualTo(2.5f * 1.25f).Within(Tolerance), "from topsoil");
            Assert.That(_removed[MaterialTable.DirtLoose.Value], Is.EqualTo(2.5f * 1.25f).Within(Tolerance), "from dirt");
        }

        [Test]
        public void DigAddsToTheCallersTotalsRatherThanReplacingThem()
        {
            TerrainBrush.Dig(_grid, 5, 5, 0, 0.25f, _removed);
            TerrainBrush.Dig(_grid, 5, 5, 0, 0.25f, _removed);

            Assert.That(_removed[MaterialTable.Dirt.Value], Is.EqualTo(0.5f * 1.25f).Within(Tolerance));
        }

        [Test]
        public void DigStopsAtBedrock()
        {
            var total = TerrainBrush.Dig(_grid, 5, 5, 0, 100f, _removed, out var inPlace);

            Assert.That(inPlace, Is.EqualTo(1.5f).Within(Tolerance));
            Assert.That(total, Is.EqualTo(1.5f * 1.25f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.EqualTo(5f).Within(Tolerance));
            Assert.That(TerrainBrush.Dig(_grid, 5, 5, 0, 1f, _removed), Is.EqualTo(0f));
        }

        [Test]
        public void ABrushOverhangingTheCornerDigsOnlyCellsInTheGrid()
        {
            // Radius 2 at the corner: the quarter disc inside the grid is 6 cells.
            Assert.DoesNotThrow(() => TerrainBrush.Dig(_grid, 0, 0, 2, 0.25f, _removed));
            Assert.That(CountCellsBelow(6.5f), Is.EqualTo(6));
        }

        [Test]
        public void FillTipsMaterialOnEveryCellInTheDisc()
        {
            var added = TerrainBrush.Fill(_grid, 5, 5, 1, MaterialTable.Dirt, 1f);

            Assert.That(added, Is.EqualTo(5f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.EqualTo(7.5f).Within(Tolerance));
            Assert.That(_grid.GetTopMaterial(5, 5), Is.EqualTo(MaterialTable.DirtLoose));
            Assert.That(_grid.GetSurfaceHeight(7, 5), Is.EqualTo(6.5f).Within(Tolerance));
        }

        [Test]
        public void TooSmallATotalsBufferIsRejected()
        {
            var tooSmall = new float[2];
            Assert.Throws<ArgumentException>(() => TerrainBrush.Dig(_grid, 5, 5, 0, 1f, tooSmall));
        }

        [Test]
        public void NegativeRadiusIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TerrainBrush.Fill(_grid, 5, 5, -1, MaterialTable.Dirt, 1f));
        }
    }
}
