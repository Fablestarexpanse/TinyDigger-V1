using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// How much room a unit takes (Ronan, 2026-09-22: "footprint will be important"). Every unit
    /// is one cell to the crew logic, but a machine is over a metre long where a robot is half of
    /// one, so each carries a radius and two units keep the sum of theirs between their centres.
    /// </summary>
    public class FootprintTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 24;

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
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 4f)
                    });
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        /// <summary>A machine of that role: in the game these are the digger and the dumper.</summary>
        CrewUnit Spawn(int x, int z, UnitRole role) =>
            new CrewUnit(_dispatcher, x, z, role, UnitLoads.Barrow).AsMachine();

        [Test]
        public void AUnitIsCrewSizedUntilItIsFittedOutAsAMachine()
        {
            var plain = new CrewUnit(_dispatcher, 2, 2, UnitRole.Digger, UnitLoads.Barrow);
            Assert.That(plain.Radius, Is.EqualTo(CrewUnit.CrewRadius).Within(Tolerance));
            Assert.That(plain.CutsStone, Is.False);
            Assert.That(plain.AsMachine().Radius, Is.EqualTo(CrewUnit.DiggerRadius).Within(Tolerance));
        }

        [Test]
        public void EachKindOfUnitKnowsItsOwnSize()
        {
            Assert.That(Spawn(2, 2, UnitRole.Worker).Radius, Is.EqualTo(CrewUnit.CrewRadius).Within(Tolerance));
            Assert.That(Spawn(6, 2, UnitRole.Digger).Radius, Is.EqualTo(CrewUnit.DiggerRadius).Within(Tolerance));
            Assert.That(Spawn(10, 2, UnitRole.Hauler).Radius, Is.EqualTo(CrewUnit.HaulerRadius).Within(Tolerance));
        }

        [Test]
        public void AMachineTakesMoreRoomThanARobot()
        {
            Assert.That(CrewUnit.DiggerRadius, Is.GreaterThan(CrewUnit.CrewRadius * 2f),
                "a machine is more than twice a robot across");
        }

        [Test]
        public void TwoRobotsKeepTheGapTheyAlwaysDid()
        {
            var standing = Spawn(8, 8, UnitRole.Worker);
            var moving = Spawn(10, 8, UnitRole.Worker);
            var gap = CrewUnit.CrewRadius * 2f;

            var justInside = new Vector2(standing.Position.x + gap * 0.9f, standing.Position.y);
            var justOutside = new Vector2(standing.Position.x + gap * 1.1f, standing.Position.y);
            Assert.That(_dispatcher.CanMoveTo(moving.Position, justOutside, moving.Id, moving.Radius), Is.True);
            Assert.That(_dispatcher.CanMoveTo(moving.Position, justInside, moving.Id, moving.Radius), Is.False);
        }

        [Test]
        public void AMachineStopsFurtherFromARobotThanARobotWould()
        {
            var robot = Spawn(8, 8, UnitRole.Worker);
            var machine = Spawn(12, 8, UnitRole.Digger);
            var other = Spawn(14, 8, UnitRole.Worker);

            // A point a robot may stand at, but a machine may not: between the two radii sums.
            var reach = CrewUnit.CrewRadius * 2f + (CrewUnit.DiggerRadius - CrewUnit.CrewRadius) * 0.5f;
            var spot = new Vector2(robot.Position.x + reach, robot.Position.y);
            Assert.That(_dispatcher.CanMoveTo(other.Position, spot, other.Id, other.Radius), Is.True);
            Assert.That(_dispatcher.CanMoveTo(machine.Position, spot, machine.Id, machine.Radius), Is.False);
        }

        [Test]
        public void TwoMachinesCannotStandInsideOneAnother()
        {
            var parked = Spawn(8, 8, UnitRole.Digger);
            var coming = Spawn(14, 8, UnitRole.Hauler);
            var overlapping = new Vector2(parked.Position.x + 1f, parked.Position.y);
            Assert.That(_dispatcher.CanMoveTo(coming.Position, overlapping, coming.Id, coming.Radius), Is.False);

            var clear = new Vector2(
                parked.Position.x + CrewUnit.DiggerRadius + CrewUnit.HaulerRadius + 0.1f, parked.Position.y);
            Assert.That(_dispatcher.CanMoveTo(coming.Position, clear, coming.Id, coming.Radius), Is.True);
        }

        [Test]
        public void AMachineHaulerCanStillGetBesideItsDiggerToBeLoaded()
        {
            var digger = Spawn(8, 8, UnitRole.Digger);
            var hauler = Spawn(12, 8, UnitRole.Hauler);
            Assert.That(_dispatcher.AssignDigger(hauler), Is.EqualTo(digger.Id));

            // The cell right beside the digger: one cell between the centres. Two machines kept
            // the sum of their radii apart could never reach it, and nothing would ever be loaded.
            var beside = new Vector2(digger.Position.x + 1f, digger.Position.y);
            Assert.That(_dispatcher.Partnered(hauler.Id, digger.Id), Is.True);
            Assert.That(_dispatcher.CanMoveTo(hauler.Position, beside, hauler.Id, hauler.Radius), Is.True);
        }

        [Test]
        public void AMachineThatIsNotTheDiggersHaulerStaysOut()
        {
            var digger = Spawn(8, 8, UnitRole.Digger);
            var stranger = Spawn(12, 8, UnitRole.Hauler);
            var beside = new Vector2(digger.Position.x + 1f, digger.Position.y);
            Assert.That(_dispatcher.CanMoveTo(stranger.Position, beside, stranger.Id, stranger.Radius),
                Is.False);
        }

        [Test]
        public void AUnitAlreadyTooCloseMayStillMoveAway()
        {
            var parked = Spawn(8, 8, UnitRole.Digger);
            var hauler = Spawn(9, 8, UnitRole.Hauler);   // parked at the digger, inside its radius
            var away = new Vector2(hauler.Position.x + 0.2f, hauler.Position.y);
            Assert.That(_dispatcher.CanMoveTo(hauler.Position, away, hauler.Id, hauler.Radius), Is.True,
                "a hauler parked at its digger has to be able to leave");
        }
    }
}
