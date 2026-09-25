using NUnit.Framework;

namespace PromptWaffle.Terrain.Tests
{
    public class AngleOfReposeSimulatorTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 21;
        const int Centre = 10;

        TerrainGrid _grid;
        AngleOfReposeSimulator _slump;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateBasic(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f) });
            _slump = new AngleOfReposeSimulator(_grid);
            // Nothing to do on flat bedrock; start each test with an empty queue.
            _slump.RunUntilStable();
        }

        [TearDown]
        public void TearDown()
        {
            _slump.Dispose();
        }

        [Test]
        public void DroppingSettledCellsKeepsOnlyWhatCanSlideAndSettlesTheSame()
        {
            // Every cell queued, as generation leaves it, with one loose pile that must come down.
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, CopyColumn(x, z));
            Assert.That(_slump.PendingCount, Is.EqualTo(Size * Size));

            var dropped = _slump.DropSettled();

            Assert.That(dropped, Is.EqualTo(Size * Size - 1), "only the pile can slide");
            Assert.That(_slump.PendingCount, Is.EqualTo(1));
            _slump.RunUntilStable();
            var pruned = Heights();

            // The same pile settled the plain way.
            SetUp();
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);
            _slump.RunUntilStable();
            Assert.That(pruned, Is.EqualTo(Heights()), "the same slope either way");
        }

        Layer[] CopyColumn(int x, int z)
        {
            var layers = new Layer[_grid.GetLayerCount(x, z)];
            for (var i = 0; i < layers.Length; i++)
                layers[i] = _grid.GetLayer(x, z, i);
            return layers;
        }

        float[] Heights()
        {
            var heights = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    heights[z * Size + x] = _grid.GetSurfaceHeight(x, z);
            return heights;
        }

        float TotalHeight()
        {
            var total = 0f;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    total += _grid.GetSurfaceHeight(x, z);
            return total;
        }

        void Pile(int x, int z, MaterialId material, float height)
        {
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(material, height) });
        }

        [Test]
        public void ATallPileOfLooseDirtSpreadsOut()
        {
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);

            _slump.RunUntilStable();

            Assert.That(_grid.GetSurfaceHeight(Centre, Centre), Is.LessThan(10f - Tolerance));
            Assert.That(_grid.GetTopMaterial(Centre + 1, Centre), Is.EqualTo(MaterialTable.DirtLoose));
        }

        [Test]
        public void SlumpingConservesMaterial()
        {
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);
            Pile(Centre + 3, Centre, MaterialTable.RockLoose, 6f);
            var before = TotalHeight();

            _slump.RunUntilStable();

            Assert.That(TotalHeight(), Is.EqualTo(before).Within(Tolerance));
        }

        [Test]
        public void ItSettlesAndStops()
        {
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);

            var ticks = _slump.RunUntilStable(10000);

            Assert.That(_slump.PendingCount, Is.EqualTo(0));
            Assert.That(ticks, Is.LessThan(10000));
        }

        [Test]
        public void MaterialAlsoSlidesToDiagonalNeighbours()
        {
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);

            _slump.RunUntilStable();

            // 8m of loose dirt spreads to a 1m skin over 8 of the 9 cells: the four axis neighbours
            // and three of the diagonals. Which diagonal misses out follows the fixed neighbour
            // order; a 4-neighbour rule would have left all four diagonals bare.
            var diagonalsReached = 0;
            foreach (var (dx, dz) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                if (_grid.GetSurfaceHeight(Centre + dx, Centre + dz) > 2f + Tolerance)
                    diagonalsReached++;
            Assert.That(diagonalsReached, Is.EqualTo(3));
            Assert.That(_grid.GetSurfaceHeight(Centre + 1, Centre + 1), Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void TheRemainderCarriesOverWhenTheBudgetRunsOut()
        {
            _slump.MaxTilesPerTick = 3;
            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f); // queues the cell and its 8 neighbours

            var examined = _slump.Tick();

            Assert.That(examined, Is.EqualTo(3));
            Assert.That(_slump.PendingCount, Is.GreaterThan(0), "the rest waits for the next tick");

            _slump.MaxTilesPerTick = 1000;
            _slump.RunUntilStable();
            Assert.That(_slump.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void TheDefaultBudgetIsAThousandTiles()
        {
            Assert.That(new AngleOfReposeSimulator(_grid).MaxTilesPerTick, Is.EqualTo(1000));
        }

        [Test]
        public void AThinLooseSkinClingsWhereAThickLayerSlides()
        {
            // Same 3m drop to the neighbours. Thin: a 0.5m skin of loose dirt over granite.
            // Thick: 3m of loose dirt.
            _grid.SetColumn(5, 5, new[]
            {
                new Layer(MaterialTable.Bedrock, 1f),
                new Layer(MaterialTable.Granite, 3.5f),
                new Layer(MaterialTable.DirtLoose, 0.5f),
            });
            Pile(15, 15, MaterialTable.DirtLoose, 3f);

            _slump.RunUntilStable();

            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.EqualTo(5f).Within(Tolerance), "thin skin should cling");
            Assert.That(_grid.GetSurfaceHeight(15, 15), Is.LessThan(5f - Tolerance), "thick loose layer should slide");
        }

        [Test]
        public void AThinTopsoilCapDoesNotHoldUpACutWall()
        {
            // The thin-layer bias is for loose material only; a 0.3m undisturbed cap over dirt
            // standing 3m proud gives way on dirt's own angle.
            _grid.SetColumn(5, 5, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f),
                new Layer(MaterialTable.Dirt, 2.7f),
                new Layer(MaterialTable.Topsoil, 0.3f),
            });

            _slump.RunUntilStable();

            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.LessThan(5f - Tolerance));
            Assert.That(_slump.EffectiveAngle(MaterialTable.Topsoil, 0.3f),
                Is.EqualTo(_grid.Materials.Get(MaterialTable.Topsoil).AngleOfRepose));
        }

        [Test]
        public void ASoilCapSlidesOffButTheRockUnderItHolds()
        {
            // Rock to 6 m under 1 m of soil, standing 3 m above its neighbours. The soil (50°)
            // gives way; the rock (80°) does not follow it.
            _grid.SetColumn(5, 5, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f),
                new Layer(MaterialTable.Rock, 4f),
                new Layer(MaterialTable.Dirt, 0.7f),
                new Layer(MaterialTable.Topsoil, 0.3f),
            });
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if (dx != 0 || dz != 0)
                        _grid.SetColumn(5 + dx, 5 + dz, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Rock, 2f) });

            _slump.RunUntilStable();

            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.EqualTo(6f).Within(Tolerance), "the soil went, the rock stayed");
            Assert.That(_grid.GetTopMaterial(5, 5), Is.EqualTo(MaterialTable.Rock));
        }

        [Test]
        public void DiggingStraightDownMakesTheWallsSlumpIn()
        {
            // TERRAIN_REFERENCE.md section 3: an unstepped pit collapses onto the digger.
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f),
                        new Layer(MaterialTable.Dirt, 4.7f),
                        new Layer(MaterialTable.Topsoil, 0.3f),
                    });
            _slump.RunUntilStable();
            var removed = new float[_grid.Materials.MaxId + 1];

            for (var i = 0; i < 3; i++)
                TerrainBrush.Dig(_grid, Centre, Centre, 1, 1f, removed);
            _slump.RunUntilStable();

            // Which rim cells give way depends on the order slumps refill the pit; what matters is
            // that ground outside the dug footprint came down and landed in it.
            var rimCellsLowered = 0;
            for (var z = Centre - 3; z <= Centre + 3; z++)
                for (var x = Centre - 3; x <= Centre + 3; x++)
                {
                    var dx = x - Centre;
                    var dz = z - Centre;
                    if (dx * dx + dz * dz > 1 && _grid.GetSurfaceHeight(x, z) < 7f - Tolerance)
                        rimCellsLowered++;
                }

            Assert.That(rimCellsLowered, Is.GreaterThan(0), "the rim gave way");
            Assert.That(_grid.GetSurfaceHeight(Centre, Centre), Is.GreaterThan(4f + Tolerance), "spoil slid back into the pit");
            Assert.That(_grid.GetTopMaterial(Centre, Centre), Is.EqualTo(MaterialTable.DirtLoose));
        }

        [Test]
        public void ThinLayersGetASteeperEffectiveAngle()
        {
            var thin = _slump.EffectiveAngle(MaterialTable.DirtLoose, 0.25f);
            var thick = _slump.EffectiveAngle(MaterialTable.DirtLoose, 5f);

            Assert.That(thick, Is.EqualTo(_grid.Materials.Get(MaterialTable.DirtLoose).AngleOfRepose).Within(Tolerance));
            Assert.That(thin, Is.GreaterThan(thick));
            Assert.That(thin, Is.LessThanOrEqualTo(90f));
        }

        [Test]
        public void UndisturbedRockHoldsACliffThatLooseRockCannot()
        {
            Pile(5, 5, MaterialTable.Rock, 4f);
            Pile(15, 15, MaterialTable.RockLoose, 4f);

            _slump.RunUntilStable();

            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.EqualTo(6f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(15, 15), Is.LessThan(6f - Tolerance));
        }

        [Test]
        public void SlumpedGroundLandsDisturbed()
        {
            // Undisturbed dirt with a thick column: once it slumps, what lands is loose dirt.
            Pile(Centre, Centre, MaterialTable.Dirt, 10f);

            _slump.RunUntilStable();

            Assert.That(_grid.GetTopMaterial(Centre + 1, Centre), Is.EqualTo(MaterialTable.DirtLoose));
        }

        [Test]
        public void ANeighbourWithAFullStackIsSkippedAndNothingIsLost()
        {
            // Every neighbour of the pile is a full 8-layer stack topped with sand, so none of
            // them can take loose dirt.
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                        continue;
                    var layers = new Layer[TerrainGrid.MaxLayersPerCell];
                    layers[0] = new Layer(MaterialTable.Bedrock, 2f);
                    for (var i = 1; i < layers.Length; i++)
                        layers[i] = new Layer(i % 2 == 0 ? MaterialTable.Clay : MaterialTable.Sand, i == layers.Length - 1 ? 1f : 0f + 1e-3f);
                    _grid.SetColumn(Centre + dx, Centre + dz, layers);
                }
            }

            Pile(Centre, Centre, MaterialTable.DirtLoose, 8f);
            var before = TotalHeight();

            _slump.RunUntilStable();

            Assert.That(TotalHeight(), Is.EqualTo(before).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(Centre, Centre), Is.EqualTo(10f).Within(Tolerance),
                "boxed in by full stacks, the pile cannot shed at all");
        }

        [Test]
        public void TheSameEditsSettleTheSameWay()
        {
            var a = Settled();
            var b = Settled();

            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    Assert.That(b.GetSurfaceHeight(x, z), Is.EqualTo(a.GetSurfaceHeight(x, z)));
        }

        static TerrainGrid Settled()
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateBasic(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f) });
            using (var slump = new AngleOfReposeSimulator(grid))
            {
                grid.SetColumn(Centre, Centre, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.DirtLoose, 9f) });
                grid.SetColumn(Centre + 2, Centre - 1, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.RockLoose, 5f) });
                slump.RunUntilStable();
            }

            return grid;
        }
    }
}
