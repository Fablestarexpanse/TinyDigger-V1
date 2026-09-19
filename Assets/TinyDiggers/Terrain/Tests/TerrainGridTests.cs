using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace TinyDiggers.Terrain.Tests
{
    public class TerrainGridTests
    {
        const float Tolerance = 1e-4f;

        MaterialTable _materials;
        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _materials = MaterialTable.CreateDefault();
            _grid = new TerrainGrid(4, 3, _materials);
        }

        static void SetColumn(TerrainGrid grid, int x, int z, params Layer[] layers)
        {
            grid.SetColumn(x, z, layers);
        }

        // --- construction and bounds -------------------------------------------------------

        [Test]
        public void NewGridIsEmpty()
        {
            Assert.That(_grid.Width, Is.EqualTo(4));
            Assert.That(_grid.Height, Is.EqualTo(3));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(0f));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(0));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialId.None));
        }

        [Test]
        public void InBoundsRejectsCellsOutsideTheGrid()
        {
            Assert.That(_grid.InBounds(0, 0), Is.True);
            Assert.That(_grid.InBounds(3, 2), Is.True);
            Assert.That(_grid.InBounds(4, 0), Is.False);
            Assert.That(_grid.InBounds(0, 3), Is.False);
            Assert.That(_grid.InBounds(-1, 0), Is.False);
            Assert.That(_grid.InBounds(0, -1), Is.False);
        }

        [Test]
        public void AccessingCellOutsideTheGridThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _grid.GetSurfaceHeight(4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => _grid.Add(-1, 0, MaterialTable.Dirt, 1f));
        }

        [Test]
        public void CellsAreAddressedIndependently()
        {
            _grid.Add(1, 2, MaterialTable.Dirt, 3f);

            Assert.That(_grid.GetSurfaceHeight(1, 2), Is.EqualTo(3f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(2, 1), Is.EqualTo(0f));
        }

        // --- Add ---------------------------------------------------------------------------

        [Test]
        public void AddRaisesTheSurfaceAndSetsTheTopMaterial()
        {
            var added = _grid.Add(0, 0, MaterialTable.Dirt, 1.5f);

            Assert.That(added, Is.EqualTo(1.5f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(1.5f).Within(Tolerance));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.DirtLoose), "tipped dirt lands loose");
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(1));
        }

        [Test]
        public void AddMergesIntoTheTopLayerWhenTheMaterialMatches()
        {
            _grid.Add(0, 0, MaterialTable.Dirt, 1f);
            _grid.Add(0, 0, MaterialTable.Dirt, 2f);

            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(1));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void AddStacksANewLayerWhenTheMaterialDiffers()
        {
            _grid.Add(0, 0, MaterialTable.Dirt, 1f);
            _grid.Add(0, 0, MaterialTable.Sand, 2f);

            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(2));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.Sand));
            Assert.That(_grid.GetLayer(0, 0, 0).Material, Is.EqualTo(MaterialTable.DirtLoose));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void AddOfZeroOrNegativeVolumeDoesNothing()
        {
            Assert.That(_grid.Add(0, 0, MaterialTable.Dirt, 0f), Is.EqualTo(0f));
            Assert.That(_grid.Add(0, 0, MaterialTable.Dirt, -5f), Is.EqualTo(0f));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(0));
        }

        [Test]
        public void AddRefusesADifferentMaterialOnAFullStack()
        {
            FillStack(0, 0);

            var added = _grid.Add(0, 0, MaterialTable.Clay, 1f);

            Assert.That(added, Is.EqualTo(0f));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(TerrainGrid.MaxLayersPerCell));
        }

        [Test]
        public void AddStillMergesOnAFullStackWhenTheMaterialMatches()
        {
            FillStack(0, 0);
            var heightBefore = _grid.GetSurfaceHeight(0, 0);
            var top = _grid.GetTopMaterial(0, 0);

            var added = _grid.Add(0, 0, top, 1f);

            Assert.That(added, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(TerrainGrid.MaxLayersPerCell));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(heightBefore + 1f).Within(Tolerance));
        }

        [Test]
        public void AddRejectsTheNoneMaterial()
        {
            Assert.Throws<ArgumentException>(() => _grid.Add(0, 0, MaterialId.None, 1f));
        }

        // --- Remove ------------------------------------------------------------------------

        [Test]
        public void RemoveTakesPartOfTheTopLayer()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            var count = _grid.Remove(0, 0, 0.25f, removed);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(removed[0].Material, Is.EqualTo(MaterialTable.Dirt), "dug topsoil comes out as its disturbed form, dirt");
            Assert.That(removed[0].Volume, Is.EqualTo(0.25f * 1.25f).Within(Tolerance), "topsoil bulks by 1.25");
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(14.25f).Within(Tolerance));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(4), "a partly dug layer stays on the stack");
        }

        [Test]
        public void RemoveSpanningTwoLayersReportsBothMaterials()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            var count = _grid.Remove(0, 0, 1.5f, removed);

            Assert.That(count, Is.EqualTo(2));
            Assert.That(removed[0].Material, Is.EqualTo(MaterialTable.Dirt));
            Assert.That(removed[0].Volume, Is.EqualTo(0.5f * 1.25f).Within(Tolerance));
            Assert.That(removed[1].Material, Is.EqualTo(MaterialTable.RockLoose));
            Assert.That(removed[1].Volume, Is.EqualTo(1f * 1.5f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(13f).Within(Tolerance));
            // The 1m of rock was exactly consumed, so the granite beneath it is now the surface.
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.Granite));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(2));
        }

        [Test]
        public void RemovingAWholeLayerPopsIt()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            _grid.Remove(0, 0, 0.5f, removed);

            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(3), "the emptied topsoil layer is gone");
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.Rock));
        }

        [Test]
        public void RemoveLeavesNoSliverOfALayerItAlmostConsumed()
        {
            _grid.Add(0, 0, MaterialTable.Dirt, 1f);
            var removed = new List<MaterialVolume>();

            var count = _grid.Remove(0, 0, 1f - 1e-6f, removed);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(removed[0].Volume, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(0));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void RemoveStopsAtBedrockAndReportsLessThanAsked()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            _grid.Remove(0, 0, 100f, removed);

            var total = 0f;
            foreach (var entry in removed)
                total += entry.Volume;

            // 4.5m in place: 0.5 topsoil at 1.25x, then 4m of rock and granite at 1.5x.
            Assert.That(total, Is.EqualTo(0.5f * 1.25f + 4f * 1.5f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(10f).Within(Tolerance));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.Bedrock));
        }

        [Test]
        public void RemoveOnBareBedrockChangesNothing()
        {
            SetColumn(_grid, 0, 0, new Layer(MaterialTable.Bedrock, 10f));
            var removed = new List<MaterialVolume>();

            var count = _grid.Remove(0, 0, 5f, removed);

            Assert.That(count, Is.EqualTo(0));
            Assert.That(removed, Is.Empty);
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(10f).Within(Tolerance));
        }

        [Test]
        public void RemoveOnAnEmptyColumnChangesNothing()
        {
            var removed = new List<MaterialVolume>();

            Assert.That(_grid.Remove(0, 0, 5f, removed), Is.EqualTo(0));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(0f));
        }

        [Test]
        public void RemoveOfZeroOrNegativeVolumeDoesNothing()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            Assert.That(_grid.Remove(0, 0, 0f, removed), Is.EqualTo(0));
            Assert.That(_grid.Remove(0, 0, -1f, removed), Is.EqualTo(0));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(14.5f).Within(Tolerance));
        }

        [Test]
        public void RemoveMergesConsecutiveLayersOfTheSameMaterialIntoOneEntry()
        {
            // Generation can leave two bands of the same material touching; digging through both
            // should still report one pile of that material.
            SetColumn(_grid, 0, 0,
                new Layer(MaterialTable.Bedrock, 5f),
                new Layer(MaterialTable.Dirt, 1f),
                new Layer(MaterialTable.Dirt, 1f));
            var removed = new List<MaterialVolume>();

            var count = _grid.Remove(0, 0, 2f, removed);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(removed[0].Material, Is.EqualTo(MaterialTable.DirtLoose));
            Assert.That(removed[0].Volume, Is.EqualTo(2f * 1.25f).Within(Tolerance));
        }

        [Test]
        public void RemoveStopsRatherThanLoseMaterialItCannotReport()
        {
            SetTestColumn(0, 0);
            Span<MaterialVolume> tooSmall = stackalloc MaterialVolume[1];

            var count = _grid.Remove(0, 0, 10f, tooSmall);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(tooSmall[0].Material, Is.EqualTo(MaterialTable.Dirt));
            // Only topsoil went; the rock below is untouched rather than dug and dropped.
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(14f).Within(Tolerance));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.Rock));
        }

        [Test]
        public void TippingDugMaterialBackLeavesAHeapBecauseItBulked()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            _grid.Remove(0, 0, 2f, removed);
            foreach (var entry in removed)
                _grid.Add(0, 0, entry.Material, entry.Volume);

            // 2m dug: 0.5 topsoil (x1.25), 1 rock and 0.5 granite (x1.5) come back as 2.875m loose.
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(12.5f + 2.875f).Within(Tolerance));
        }

        [Test]
        public void UnbulkedRemoveThenAddRestoresTheOriginalHeight()
        {
            SetTestColumn(0, 0);
            var removed = new List<MaterialVolume>();

            _grid.Remove(0, 0, 2f, removed, bulk: false);
            foreach (var entry in removed)
                _grid.Add(0, 0, entry.Material, entry.Volume);

            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(14.5f).Within(Tolerance));
        }

        // --- reading the stack ---------------------------------------------------------------

        [Test]
        public void CopyLayersReturnsTheColumnTopFirst()
        {
            SetTestColumn(0, 0);
            Span<Layer> buffer = stackalloc Layer[TerrainGrid.MaxLayersPerCell];

            var count = _grid.CopyLayers(0, 0, buffer);

            Assert.That(count, Is.EqualTo(4));
            Assert.That(buffer[0].Material, Is.EqualTo(MaterialTable.Topsoil));
            Assert.That(buffer[1].Material, Is.EqualTo(MaterialTable.Rock));
            Assert.That(buffer[2].Material, Is.EqualTo(MaterialTable.Granite));
            Assert.That(buffer[3].Material, Is.EqualTo(MaterialTable.Bedrock));
        }

        [Test]
        public void GetLayerIsIndexedFromTheBottom()
        {
            SetTestColumn(0, 0);

            Assert.That(_grid.GetLayer(0, 0, 0).Material, Is.EqualTo(MaterialTable.Bedrock));
            Assert.That(_grid.GetLayer(0, 0, 3).Material, Is.EqualTo(MaterialTable.Topsoil));
            Assert.Throws<ArgumentOutOfRangeException>(() => _grid.GetLayer(0, 0, 4));
        }

        // --- SetColumn ------------------------------------------------------------------------

        [Test]
        public void SetColumnReplacesTheWholeStack()
        {
            _grid.Add(0, 0, MaterialTable.Sand, 99f);

            SetTestColumn(0, 0);

            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(4));
            Assert.That(_grid.GetSurfaceHeight(0, 0), Is.EqualTo(14.5f).Within(Tolerance));
        }

        [Test]
        public void SetColumnRejectsAnOversizedOrEmptyLayer()
        {
            var tooMany = new Layer[TerrainGrid.MaxLayersPerCell + 1];
            for (var i = 0; i < tooMany.Length; i++)
                tooMany[i] = new Layer(MaterialTable.Dirt, 1f);

            Assert.Throws<ArgumentException>(() => _grid.SetColumn(0, 0, tooMany));
            Assert.Throws<ArgumentException>(
                () => SetColumn(_grid, 0, 0, new Layer(MaterialTable.Dirt, 0f)));
            Assert.Throws<ArgumentException>(
                () => SetColumn(_grid, 0, 0, new Layer(MaterialId.None, 1f)));
        }

        // --- change notification ---------------------------------------------------------------

        [Test]
        public void MutationsRaiseCellChangedForTheCellThatMoved()
        {
            var changes = new List<(int x, int z)>();
            _grid.CellChanged += (x, z) => changes.Add((x, z));

            _grid.Add(2, 1, MaterialTable.Dirt, 1f);
            var removed = new List<MaterialVolume>();
            _grid.Remove(2, 1, 0.5f, removed);

            Assert.That(changes, Is.EqualTo(new[] { (2, 1), (2, 1) }));
        }

        [Test]
        public void NoOpsDoNotRaiseCellChanged()
        {
            SetColumn(_grid, 0, 0, new Layer(MaterialTable.Bedrock, 4f));
            var raised = 0;
            _grid.CellChanged += (x, z) => raised++;

            _grid.Add(0, 0, MaterialTable.Dirt, 0f);
            var removed = new List<MaterialVolume>();
            _grid.Remove(0, 0, 3f, removed); // bedrock refuses to be dug

            Assert.That(raised, Is.EqualTo(0));
        }

        // --- disturbed materials -------------------------------------------------------------

        [TestCase(7, 5)] // Topsoil -> Dirt
        [TestCase(5, 9)] // Dirt -> DirtLoose
        [TestCase(3, 8)] // Rock -> RockLoose
        [TestCase(9, 9)] // DirtLoose stays loose
        [TestCase(6, 6)] // Sand has no disturbed form
        public void RemoveReportsTheDisturbedForm(int dug, int expected)
        {
            SetColumn(_grid, 0, 0, new Layer(MaterialTable.Bedrock, 1f), new Layer(new MaterialId((byte)dug), 2f));
            var removed = new List<MaterialVolume>();

            _grid.Remove(0, 0, 1f, removed);

            Assert.That(removed[0].Material, Is.EqualTo(new MaterialId((byte)expected)));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(new MaterialId((byte)dug)), "what stays in the ground is undisturbed");
        }

        [Test]
        public void AddPlacesTheDisturbedFormAndMergesWithIt()
        {
            _grid.Add(0, 0, MaterialTable.Rock, 1f);
            _grid.Add(0, 0, MaterialTable.RockLoose, 1f);

            Assert.That(_grid.GetLayerCount(0, 0), Is.EqualTo(1));
            Assert.That(_grid.GetTopMaterial(0, 0), Is.EqualTo(MaterialTable.RockLoose));
        }

        [Test]
        public void LooseVariantsSlumpAtShallowerAnglesThanTheGroundTheyCameFrom()
        {
            var table = MaterialTable.CreateDefault();

            Assert.That(table.Get(MaterialTable.RockLoose).AngleOfRepose, Is.LessThan(table.Get(MaterialTable.Rock).AngleOfRepose));
            Assert.That(table.Get(MaterialTable.DirtLoose).AngleOfRepose, Is.LessThan(table.Get(MaterialTable.Dirt).AngleOfRepose));
        }

        [TestCase(3, 1.5f)] // Rock
        [TestCase(2, 1.5f)] // Granite
        [TestCase(5, 1.25f)] // Dirt
        [TestCase(7, 1.25f)] // Topsoil
        [TestCase(6, 1.1f)] // Sand
        [TestCase(8, 1f)] // RockLoose is already loose
        [TestCase(9, 1f)] // DirtLoose is already loose
        public void BulkingFactorsFollowTheReference(int id, float expected)
        {
            Assert.That(MaterialTable.CreateDefault().Get(new MaterialId((byte)id)).BulkingFactor, Is.EqualTo(expected));
        }

        [Test]
        public void OnlyLooseMaterialsAreMarkedLoose()
        {
            var table = MaterialTable.CreateDefault();

            Assert.That(table.Get(MaterialTable.RockLoose).IsLoose, Is.True);
            Assert.That(table.Get(MaterialTable.DirtLoose).IsLoose, Is.True);
            Assert.That(table.Get(MaterialTable.Sand).IsLoose, Is.True);
            Assert.That(table.Get(MaterialTable.Topsoil).IsLoose, Is.False);
            Assert.That(table.Get(MaterialTable.Rock).IsLoose, Is.False);
        }

        [Test]
        public void ABulkingFactorBelowOneIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MaterialDefinition(new MaterialId(1), "A", default, 0f, 45f, bulkingFactor: 0.9f));
        }

        [Test]
        public void ATableThatDisturbsIntoAMissingMaterialIsRejected()
        {
            Assert.Throws<ArgumentException>(() => new MaterialTable(
                new MaterialDefinition(new MaterialId(1), "A", default, 0f, 45f, disturbed: new MaterialId(2))));
        }

        // --- height step ------------------------------------------------------------------------

        [Test]
        public void WithAHeightStepEditsMoveWholeSteps()
        {
            var grid = new TerrainGrid(2, 2, MaterialTable.CreateDefault(), heightStep: 1f);
            SetColumn(grid, 0, 0, new Layer(MaterialTable.Bedrock, 1f), new Layer(MaterialTable.Dirt, 5f));
            var removed = new List<MaterialVolume>();

            Assert.That(grid.Add(0, 0, MaterialTable.Dirt, 0.4f), Is.EqualTo(0f), "under half a step rounds to nothing");
            Assert.That(grid.Add(0, 0, MaterialTable.Dirt, 1.6f), Is.EqualTo(2f).Within(Tolerance));
            grid.Remove(0, 0, 1.4f, removed);

            Assert.That(removed[0].Volume, Is.EqualTo(1f).Within(Tolerance), "one whole step of the tipped loose dirt, which does not bulk again");
            Assert.That(grid.GetSurfaceHeight(0, 0), Is.EqualTo(7f).Within(Tolerance));
        }

        [Test]
        public void WithoutAHeightStepVolumesAreUsedAsGiven()
        {
            Assert.That(_grid.HeightStep, Is.EqualTo(0f));
            Assert.That(_grid.Quantize(0.37f), Is.EqualTo(0.37f));
        }

        // --- material at height ---------------------------------------------------------------

        [Test]
        public void GetMaterialAtFindsTheLayerAtThatHeight()
        {
            SetTestColumn(0, 0);

            Assert.That(_grid.GetMaterialAt(0, 0, -3f), Is.EqualTo(MaterialTable.Bedrock));
            Assert.That(_grid.GetMaterialAt(0, 0, 5f), Is.EqualTo(MaterialTable.Bedrock));
            Assert.That(_grid.GetMaterialAt(0, 0, 11f), Is.EqualTo(MaterialTable.Granite));
            Assert.That(_grid.GetMaterialAt(0, 0, 13.5f), Is.EqualTo(MaterialTable.Rock));
            Assert.That(_grid.GetMaterialAt(0, 0, 14.2f), Is.EqualTo(MaterialTable.Topsoil));
            Assert.That(_grid.GetMaterialAt(0, 0, 99f), Is.EqualTo(MaterialTable.Topsoil));
            Assert.That(_grid.GetMaterialAt(1, 1, 0f), Is.EqualTo(MaterialId.None));
        }

        // --- helpers ----------------------------------------------------------------------------

        /// <summary>Bedrock 10, granite 3, rock 1, topsoil 0.5. Surface at 14.5m, 4.5m of it diggable.</summary>
        void SetTestColumn(int x, int z)
        {
            SetColumn(_grid, x, z,
                new Layer(MaterialTable.Bedrock, 10f),
                new Layer(MaterialTable.Granite, 3f),
                new Layer(MaterialTable.Rock, 1f),
                new Layer(MaterialTable.Topsoil, 0.5f));
        }

        /// <summary>Fills the cell to <see cref="TerrainGrid.MaxLayersPerCell"/> with alternating materials.</summary>
        void FillStack(int x, int z)
        {
            var layers = new Layer[TerrainGrid.MaxLayersPerCell];
            for (var i = 0; i < layers.Length; i++)
                layers[i] = new Layer(i % 2 == 0 ? MaterialTable.Dirt : MaterialTable.Sand, 1f);
            _grid.SetColumn(x, z, layers);
        }
    }
}
