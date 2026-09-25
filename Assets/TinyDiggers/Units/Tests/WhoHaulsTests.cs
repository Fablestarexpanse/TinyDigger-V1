using System.Linq;
using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Who carries whose spoil (Ronan, 2026-09-22): "the smallest ones have to dig and haul their
    /// own material, they can't dump into the dump truck bot". Only the digger machine hands a
    /// load to a hauler; a starter robot fills its barrow and walks it to the heap itself.
    /// </summary>
    public class WhoHaulsTests
    {
        const int Size = 20;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f,
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

        CrewUnit Spawn(int x, int z, UnitRole role, float capacity) =>
            role == UnitRole.Worker
                ? new CrewUnit(_dispatcher, x, z, role, capacity)
                : new CrewUnit(_dispatcher, x, z, role, capacity).AsMachine();

        [Test]
        public void AHaulerServesTheDiggerMachineAndNotTheRobots()
        {
            var robot = Spawn(4, 4, UnitRole.Worker, UnitLoads.Barrow);
            var hauler = Spawn(6, 4, UnitRole.Hauler, UnitLoads.Bed);
            Assert.That(_dispatcher.AssignDigger(hauler), Is.EqualTo(-1),
                "a hauler has nobody to serve when the only digging unit is a robot");

            var machine = Spawn(8, 4, UnitRole.Digger, UnitLoads.Scoop);
            Assert.That(_dispatcher.AssignDigger(hauler), Is.EqualTo(machine.Id),
                "the digger machine is what a hauler serves");
            Assert.That(robot.Id, Is.Not.EqualTo(machine.Id));
        }

        [Test]
        public void ARobotTipsItsOwnBarrowRatherThanWaitingForAHauler()
        {
            var robot = Spawn(4, 4, UnitRole.Worker, UnitLoads.Barrow);
            Spawn(12, 12, UnitRole.Hauler, UnitLoads.Bed);

            for (var z = 3; z <= 5; z++)
                for (var x = 5; x <= 6; x++)
                    Assert.That(_map.Designate(x, z, DesignationKind.Dig, 5.5f), Is.True);
            for (var z = 8; z <= 10; z++)
                for (var x = 8; x <= 10; x++)
                    Assert.That(_map.SetDumpZone(x, z, true), Is.True);

            var tipped = false;
            var elapsed = 0f;
            while (elapsed < 240f && !tipped)
            {
                _dispatcher.Tick(TickSeconds);
                robot.Tick(TickSeconds);
                elapsed += TickSeconds;
                // Anywhere on the heap: it tips onto whichever cell of the zone is lowest.
                for (var z = 8; z <= 10 && !tipped; z++)
                    for (var x = 8; x <= 10 && !tipped; x++)
                        tipped = _grid.GetSurfaceHeight(x, z) > 6f + 1e-3f;
                Assert.That(robot.State, Is.Not.EqualTo(CrewUnitState.WaitingForHauler),
                    "a robot never waits for a hauler; it carries its own barrow");
            }

            Assert.That(tipped, Is.True, "the robot should have put its own load on the heap: " + robot.Status);
        }
    }
}
