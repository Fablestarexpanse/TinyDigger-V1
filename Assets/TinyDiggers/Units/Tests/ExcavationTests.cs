using System;
using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    public class ExcavationTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 25;

        static TerrainGrid RockField(float heightStep)
        {
            // Bedrock 2m under 6m of rock everywhere: surface at 8m.
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Rock, 6f) });
            return grid;
        }

        static float TotalHeight(TerrainGrid grid)
        {
            var total = 0f;
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    total += grid.GetSurfaceHeight(x, z);
            return total;
        }

        // --- the slice's acceptance test ------------------------------------------------------

        [Test]
        public void DugRockBulksIntoTheLoadAndTippedRockSlumpsToItsAngle()
        {
            // A 0.5m step, so the 4.5m³ load is a whole number of steps and all of it can be tipped.
            var grid = RockField(heightStep: 0.5f);
            using var slump = new AngleOfReposeSimulator(grid);
            var crew = new MaterialInventory();

            // Dig 3m³ of rock (in place) from one cell into an empty crew.
            var dig = Excavation.Dig(grid, crew, 4, 4, 0, 3f);

            Assert.That(dig.InPlace, Is.EqualTo(3f).Within(Tolerance));
            Assert.That(dig.InPlaceBySource[MaterialTable.Rock.Value], Is.EqualTo(3f).Within(Tolerance));
            Assert.That(crew.GetVolume(MaterialTable.RockLoose), Is.EqualTo(4.5f).Within(Tolerance), "rock bulks 1.5x");
            Assert.That(crew.Total, Is.EqualTo(4.5f).Within(Tolerance));
            slump.RunUntilStable();

            // Tip it all on flat ground.
            var before = TotalHeight(grid);
            var tip = Excavation.Tip(grid, crew, 16, 16);

            Assert.That(tip.Tipped, Is.EqualTo(4.5f).Within(Tolerance));
            Assert.That(crew.IsEmpty, Is.True);
            Assert.That(TotalHeight(grid) - before, Is.EqualTo(4.5f).Within(Tolerance), "the terrain gained the loose volume");
            Assert.That(grid.GetSurfaceHeight(16, 16), Is.EqualTo(12.5f).Within(Tolerance), "as one column, before slumping");

            slump.RunUntilStable();

            Assert.That(TotalHeight(grid) - before, Is.EqualTo(4.5f).Within(Tolerance), "slumping moves material, it does not lose any");
            Assert.That(grid.GetSurfaceHeight(16, 16), Is.LessThan(12.5f - Tolerance), "the column slumped");
            Assert.That(grid.GetTopMaterial(17, 16), Is.EqualTo(MaterialTable.RockLoose), "loose rock spread to the neighbours");
            AssertNothingSteeperThanItsAngle(grid, slump);
        }

        /// <summary>
        /// The settled pile obeys the simulator's rule everywhere: wherever a drop is big enough to
        /// move a step, the slope is within the top material's effective angle (RockLoose's 38°,
        /// steepened only for loose skins thinner than the thin-layer depth).
        /// </summary>
        static void AssertNothingSteeperThanItsAngle(TerrainGrid grid, AngleOfReposeSimulator slump)
        {
            var unit = grid.HeightStep;
            var checkedDrops = 0;
            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var count = grid.GetLayerCount(x, z);
                    var top = grid.GetLayer(x, z, count - 1);
                    if (top.Material != MaterialTable.RockLoose)
                        continue;

                    var angle = slump.EffectiveAngle(top.Material, top.Thickness);
                    var limit = (float)Math.Tan(angle * Math.PI / 180.0);
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if ((dx == 0 && dz == 0) || !grid.InBounds(x + dx, z + dz))
                                continue;
                            var distance = dx != 0 && dz != 0 ? (float)Math.Sqrt(2.0) : 1f;
                            var drop = grid.GetSurfaceHeight(x, z) - grid.GetSurfaceHeight(x + dx, z + dz);
                            if (drop < 2f * unit - Tolerance)
                                continue;
                            checkedDrops++;
                            Assert.That(drop / distance, Is.LessThanOrEqualTo(limit + Tolerance),
                                $"({x}, {z}) to ({x + dx}, {z + dz}): drop {drop}, angle limit {angle:0.0}°");
                        }
                    }
                }
            }

            Assert.That(checkedDrops, Is.GreaterThanOrEqualTo(0));
        }

        // --- at the game's 1m step ---------------------------------------------------------------

        [Test]
        public void AtAOneMetreStepTheLeftoverUnderAStepStaysInTheLoad()
        {
            var grid = RockField(heightStep: 1f);
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 4, 4, 0, 3f);
            var before = TotalHeight(grid);

            var tip = Excavation.Tip(grid, crew, 16, 16);

            Assert.That(tip.Tipped, Is.EqualTo(4f).Within(Tolerance), "four whole steps");
            Assert.That(tip.HeldBack, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(crew.GetVolume(MaterialTable.RockLoose), Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(crew.Total, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(TotalHeight(grid) - before + crew.Total, Is.EqualTo(4.5f).Within(Tolerance), "nothing lost");
        }

        [Test]
        public void TheLeftoverGoesOutWithTheNextLoad()
        {
            var grid = RockField(heightStep: 1f);
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 4, 4, 0, 3f);
            Excavation.Tip(grid, crew, 16, 16); // 0.5 held back
            Excavation.Dig(grid, crew, 5, 4, 0, 1f); // +1.5, merges into the 0.5 on top

            var tip = Excavation.Tip(grid, crew, 16, 16);

            Assert.That(tip.Tipped, Is.EqualTo(2f).Within(Tolerance));
            Assert.That(crew.IsEmpty, Is.True);
        }

        // --- capacity -------------------------------------------------------------------------

        [Test]
        public void ABrushDigsOnlyTheCellsWhoseLooseOutputFits()
        {
            var grid = RockField(heightStep: 1f);
            var crew = new MaterialInventory();
            var before = TotalHeight(grid);

            // Radius 2 is 13 cells of 1m rock, 1.5m³ loose each; a 5m³ load takes three.
            var dig = Excavation.Dig(grid, crew, 10, 10, 2, 1f);

            Assert.That(dig.CellsDug, Is.EqualTo(3));
            Assert.That(dig.CellsThatDidNotFit, Is.EqualTo(10));
            Assert.That(dig.Loose, Is.EqualTo(4.5f).Within(Tolerance));
            Assert.That(before - TotalHeight(grid), Is.EqualTo(3f).Within(Tolerance), "no cell was part-dug");
            Assert.That(grid.GetSurfaceHeight(10, 10), Is.EqualTo(7f).Within(Tolerance), "the clicked cell is dug first");
        }

        [Test]
        public void AFullCrewDigsNothingAndSaysSo()
        {
            var grid = RockField(heightStep: 1f);
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 10, 10, 2, 1f); // 4.5 of 5 used
            var before = TotalHeight(grid);

            var dig = Excavation.Dig(grid, crew, 3, 3, 0, 1f);

            Assert.That(dig.WasFull, Is.True);
            Assert.That(dig.SmallestMisfit, Is.EqualTo(1.5f).Within(Tolerance));
            Assert.That(TotalHeight(grid), Is.EqualTo(before).Within(Tolerance));
        }

        [Test]
        public void ACellThatBulksLessCanStillFitWhenRockWouldNot()
        {
            var grid = RockField(heightStep: 1f);
            grid.SetColumn(3, 3, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Sand, 6f) });
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 10, 10, 2, 1f); // 0.5 free

            Assert.That(Excavation.Dig(grid, crew, 4, 4, 0, 1f).WasFull, Is.True, "rock needs 1.5");
            crew.RemoveFromTop(1f); // 1.5 free
            var dig = Excavation.Dig(grid, crew, 3, 3, 0, 1f);

            Assert.That(dig.CellsDug, Is.EqualTo(1), "sand needs 1.1");
            Assert.That(crew.GetVolume(MaterialTable.Sand), Is.EqualTo(1.1f).Within(Tolerance));
        }

        // --- tipping order ------------------------------------------------------------------------

        [Test]
        public void TippingPutsTheMostRecentlyDugMaterialDownFirst()
        {
            // A 0.5m step so both loads are whole steps and both get tipped.
            var grid = RockField(heightStep: 0.5f);
            grid.SetColumn(3, 3, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 6f) });
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 3, 3, 0, 2f); // 2.5 loose dirt
            Excavation.Dig(grid, crew, 4, 4, 0, 1f); // then 1.5 loose rock on top of the load

            Excavation.Tip(grid, crew, 16, 16);

            // Rock came off the top of the load first, so it is lowest on the ground.
            var count = grid.GetLayerCount(16, 16);
            Assert.That(grid.GetLayer(16, 16, count - 1).Material, Is.EqualTo(MaterialTable.DirtLoose));
            Assert.That(grid.GetLayer(16, 16, count - 2).Material, Is.EqualTo(MaterialTable.RockLoose));
        }

        [Test]
        public void ALoadOfSmallInterleavedPiecesStillTipsInWholeSteps()
        {
            // Plains soil: each 1m dig is 0.3 topsoil (-> 0.375 dirt) over 0.7 dirt (-> 0.875 loose
            // dirt), so the load alternates pieces that are each under one step.
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f),
                        new Layer(MaterialTable.Dirt, 4.7f),
                        new Layer(MaterialTable.Topsoil, 0.3f),
                    });
            var crew = new MaterialInventory();
            var dig = Excavation.Dig(grid, crew, 10, 10, 2, 1f);
            Assert.That(dig.CellsDug, Is.EqualTo(4), "1.25 m³ loose per cell, four fit in five");
            Assert.That(crew.Stack.Count, Is.EqualTo(8), "alternating dirt / loose dirt pieces");
            var before = TotalHeight(grid);

            var tip = Excavation.Tip(grid, crew, 16, 16);

            Assert.That(tip.Tipped, Is.EqualTo(5f).Within(Tolerance));
            Assert.That(TotalHeight(grid) - before, Is.EqualTo(5f).Within(Tolerance));
            Assert.That(crew.IsEmpty, Is.True);
            Assert.That(grid.GetTopMaterial(16, 16), Is.EqualTo(MaterialTable.DirtLoose), "it all lands as loose dirt");
            Assert.That(grid.GetSurfaceHeight(16, 16), Is.EqualTo(7f + 5f).Within(Tolerance), "still on the step grid");
        }

        [Test]
        public void AMixedHilltopLoadTipsAsOneLayerPerMaterial()
        {
            // Hilltop soil: each 1m dig is topsoil, a little dirt and some rock, three pieces.
            // Three cells interleave loose dirt and loose rock six times; one tip must still fit
            // on a generated five-layer column.
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 10f),
                        new Layer(MaterialTable.Granite, 3f),
                        new Layer(MaterialTable.Rock, 6.6f),
                        new Layer(MaterialTable.Dirt, 0.1f),
                        new Layer(MaterialTable.Topsoil, 0.3f),
                    });
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 10, 10, 2, 1f);
            Assert.That(crew.Stack.Count, Is.GreaterThanOrEqualTo(6));
            var layersBefore = grid.GetLayerCount(16, 16);

            var tip = Excavation.Tip(grid, crew, 16, 16);

            // Three cells at 1.4 m³ loose each is 4.2; four whole steps tip, 0.2 stays.
            Assert.That(tip.StackFull, Is.False);
            Assert.That(tip.Tipped, Is.EqualTo(4f).Within(Tolerance));
            Assert.That(tip.HeldBack, Is.EqualTo(0.2f).Within(Tolerance));
            Assert.That(grid.GetLayerCount(16, 16), Is.EqualTo(layersBefore + 2), "one loose rock layer, one loose dirt layer");
            Assert.That(grid.GetLayer(16, 16, layersBefore).Material, Is.EqualTo(MaterialTable.RockLoose), "newest material lowest");
        }

        [Test]
        public void TippingOntoAFullLayerStackTipsNothingAndKeepsTheLoad()
        {
            var grid = RockField(heightStep: 1f);
            var layers = new Layer[TerrainGrid.MaxLayersPerCell];
            layers[0] = new Layer(MaterialTable.Bedrock, 2f);
            for (var i = 1; i < layers.Length; i++)
                layers[i] = new Layer(i % 2 == 0 ? MaterialTable.Clay : MaterialTable.Sand, 1f);
            grid.SetColumn(16, 16, layers);
            var crew = new MaterialInventory();
            Excavation.Dig(grid, crew, 4, 4, 0, 2f); // 3 m³ loose rock
            var height = grid.GetSurfaceHeight(16, 16);

            var tip = Excavation.Tip(grid, crew, 16, 16);

            Assert.That(tip.StackFull, Is.True);
            Assert.That(tip.Tipped, Is.EqualTo(0f));
            Assert.That(crew.Total, Is.EqualTo(3f).Within(Tolerance));
            Assert.That(grid.GetSurfaceHeight(16, 16), Is.EqualTo(height));
        }

        [Test]
        public void AddStackRefusesASumOffTheStepGrid()
        {
            var grid = RockField(heightStep: 1f);
            var pieces = new[] { new MaterialVolume(MaterialTable.RockLoose, 0.6f), new MaterialVolume(MaterialTable.DirtLoose, 0.6f) };

            Assert.That(grid.AddStack(3, 3, pieces), Is.False);
            Assert.That(grid.GetSurfaceHeight(3, 3), Is.EqualTo(8f));

            pieces[1] = new MaterialVolume(MaterialTable.DirtLoose, 0.4f);
            Assert.That(grid.AddStack(3, 3, pieces), Is.True);
            Assert.That(grid.GetSurfaceHeight(3, 3), Is.EqualTo(9f).Within(Tolerance));
            Assert.That(grid.GetTopMaterial(3, 3), Is.EqualTo(MaterialTable.DirtLoose), "first piece lowest");
        }

        [Test]
        public void TippingAnEmptyLoadDoesNothing()
        {
            var grid = RockField(heightStep: 1f);

            var tip = Excavation.Tip(grid, new MaterialInventory(), 16, 16);

            Assert.That(tip.Tipped, Is.EqualTo(0f));
            Assert.That(grid.GetSurfaceHeight(16, 16), Is.EqualTo(8f));
        }

        [Test]
        public void PeekRemoveMatchesWhatRemoveTakes()
        {
            var grid = RockField(heightStep: 1f);
            grid.SetColumn(3, 3, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f),
                new Layer(MaterialTable.Rock, 3.3f),
                new Layer(MaterialTable.Dirt, 0.4f),
                new Layer(MaterialTable.Topsoil, 0.3f),
            });
            var preview = new Layer[TerrainGrid.MaxLayersPerCell];
            var removed = new MaterialVolume[TerrainGrid.MaxLayersPerCell];

            var previewed = grid.PeekRemove(3, 3, 2f, preview);
            var expectedLoose = 0f;
            for (var i = 0; i < previewed; i++)
                expectedLoose += preview[i].Thickness * grid.Materials.Get(preview[i].Material).BulkingFactor;
            var pieces = grid.Remove(3, 3, 2f, removed);
            var loose = 0f;
            for (var i = 0; i < pieces; i++)
                loose += removed[i].Volume;

            Assert.That(previewed, Is.EqualTo(3), "topsoil, dirt, then some rock");
            Assert.That(loose, Is.EqualTo(expectedLoose).Within(Tolerance));
        }
    }
}
