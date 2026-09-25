using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// A dumper mech empties its bed in one tip (Ronan, 2026-09-22: "the dumper mech should dump
    /// entire load in one tip"): the bed goes up, the lot falls out, and the heap slumps. A robot
    /// with a barrow puts its load down a step at a time, which is all a barrow can do.
    /// </summary>
    public class WholeLoadTipTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 20;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f,
                cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 4f),
                    });
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        [Test]
        public void OnlyTheDumperMechEmptiesItsBedInOneGo()
        {
            var dumper = new CrewUnit(_dispatcher, 4, 4, UnitRole.Hauler, UnitLoads.Bed).AsMachine();
            var robot = new CrewUnit(_dispatcher, 8, 4, UnitRole.Worker, UnitLoads.Barrow);
            Assert.That(dumper.TipsWholeLoad, Is.True);
            Assert.That(robot.TipsWholeLoad, Is.False);
        }

        [Test]
        public void TheWholeBedGoesDownInOneTip()
        {
            for (var z = 8; z <= 12; z++)
                for (var x = 8; x <= 12; x++)
                    Assert.That(_map.SetDumpZone(x, z, true), Is.True);

            var dumper = new CrewUnit(_dispatcher, 4, 10, UnitRole.Hauler, UnitLoads.Bed).AsMachine();
            dumper.Inventory.Add(MaterialTable.Dirt, UnitLoads.Bed);
            Assert.That(dumper.Inventory.Remaining, Is.EqualTo(0f).Within(Tolerance), "it starts full");

            var elapsed = 0f;
            while (elapsed < 240f && dumper.Inventory.Total > Tolerance)
            {
                _dispatcher.Tick(TickSeconds);
                dumper.Tick(TickSeconds);
                elapsed += TickSeconds;
            }

            Assert.That(dumper.Inventory.Total, Is.EqualTo(0f).Within(Tolerance),
                "the bed should be empty: " + dumper.Status);
            Assert.That(dumper.Transferred, Is.GreaterThanOrEqualTo(0f));
        }
    }
}
