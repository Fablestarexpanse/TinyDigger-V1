using NUnit.Framework;
using PromptWaffle.Terrain;
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
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f,
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
            var hauler = Spawn(14, 8, UnitRole.Hauler);
            Assert.That(_dispatcher.AssignDigger(hauler), Is.EqualTo(digger.Id));
            Assert.That(_dispatcher.Partnered(hauler.Id, digger.Id), Is.True);

            // Side by side at the loading distance: two cells for the machines, as the forge
            // placed the truck under the bucket. Two machines kept the sum of their radii apart
            // could never get there, and nothing would ever be loaded.
            var reach = JobDispatcher.LoadingDistance(hauler, digger);
            Assert.That(reach, Is.EqualTo(2));
            var loading = new Vector2(digger.Position.x + reach, digger.Position.y);
            Assert.That(_dispatcher.CanMoveTo(hauler.Position, loading, hauler.Id, hauler.Radius), Is.True);
        }

        [Test]
        public void AMachineHaulerDoesNotParkInsideItsDigger()
        {
            var digger = Spawn(8, 8, UnitRole.Digger);
            var hauler = Spawn(14, 8, UnitRole.Hauler);
            Assert.That(_dispatcher.AssignDigger(hauler), Is.EqualTo(digger.Id));

            // The next cell over is half a metre from the digger's centre, and the two machines are
            // 0.86 and 0.93 m wide: parked there, the truck stood inside the digger (Ronan,
            // 2026-09-24: "fix the truck parking inside the digger").
            var inside = new Vector2(digger.Position.x + 1f, digger.Position.y);
            Assert.That(_dispatcher.CanMoveTo(hauler.Position, inside, hauler.Id, hauler.Radius), Is.False);
        }

        [Test]
        public void TheCrewRobotsStillLoadFromTheNextCell()
        {
            var robot = new CrewUnit(_dispatcher, 4, 4, UnitRole.Worker, UnitLoads.Barrow);
            var other = new CrewUnit(_dispatcher, 6, 4, UnitRole.Hauler, UnitLoads.Barrow);
            Assert.That(JobDispatcher.LoadingDistance(robot, other), Is.EqualTo(1));
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
        public void AMachineCutsDeeperFromWhereItStandsThanARobotCan()
        {
            var robot = new CrewUnit(_dispatcher, 4, 4, UnitRole.Worker, UnitLoads.Barrow);
            var machine = Spawn(8, 4, UnitRole.Digger);
            Assert.That(machine.DigDepthLevels, Is.GreaterThan(robot.DigDepthLevels));

            // A face three steps below the rim: the machine works it from where it stands and the
            // robot has to be given a way down (Ronan: "dig into the side instead of stepping up").
            var rim = _grid.GetSurfaceHeight(4, 4);
            var face = rim - 3f * _grid.HeightStep;
            Assert.That(machine.WithinReach(rim, face), Is.True);
            Assert.That(robot.WithinReach(rim, face), Is.False);
        }

        [Test]
        public void ReachingUpIsUnchangedByHowDeepAUnitCanCut()
        {
            var machine = Spawn(8, 4, UnitRole.Digger);
            var rim = _grid.GetSurfaceHeight(8, 4);
            var overhead = rim + (machine.DigReachLevels + 1) * _grid.HeightStep;
            Assert.That(machine.WithinReach(rim, overhead), Is.False,
                "a deeper arm does not let it work higher over its head");
        }

        [Test]
        public void AMachineDigsIntoAMoundFromTheFootOfIt()
        {
            // A heap of dirt three steps proud of the ground beside it.
            var foot = _grid.GetSurfaceHeight(6, 6);
            _grid.SetColumn(7, 6, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f),
                new Layer(MaterialTable.Dirt, 4f + 3f * _grid.HeightStep),
            });

            var machine = Spawn(6, 6, UnitRole.Digger);
            var robot = new CrewUnit(_dispatcher, 5, 6, UnitRole.Worker, UnitLoads.Barrow);
            Assert.That(machine.WithinDigReach(foot, 7, 6), Is.True,
                "a machine digs into the pile rather than climbing it");
            Assert.That(robot.WithinDigReach(foot, 7, 6), Is.False,
                "a robot still works what it can reach, and a soil face that tall slumps anyway");
        }

        [Test]
        public void ARockFaceIsStillWorkableByAnybody()
        {
            var foot = _grid.GetSurfaceHeight(10, 6);
            _grid.SetColumn(11, 6, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f),
                new Layer(MaterialTable.Rock, 4f + 5f * _grid.HeightStep),
            });

            var robot = new CrewUnit(_dispatcher, 10, 6, UnitRole.Worker, UnitLoads.Barrow);
            Assert.That(robot.WithinDigReach(foot, 11, 6), Is.True,
                "the cliff rule is for everyone, or a hill with a cliff can never come down");
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

        [Test]
        public void ADumpTruckBacksUpAndTipsOutOfItsTail()
        {
            // Ronan, 2026-09-24: "when it dumps it comes out behind it ... they back up to edge,
            // tilt, dump behind". It used to drive in nose first and face the heap, so the load
            // poured out of its tail onto ground it was not filling.
            for (var z = 8; z <= 12; z++)
                for (var x = 2; x <= 4; x++)
                    _map.SetDumpZone(x, z, true, 8f);
            var truck = Spawn(18, 10, UnitRole.Hauler);
            truck.Inventory.Add(MaterialTable.DirtLoose, truck.Inventory.Remaining);

            // Cells entered while reversing: a real back-in lines up and reverses the whole run,
            // not a shuffle at the end (the first version reversed for 0.7 s).
            var reversedCells = 0;
            var last = truck.Cell;
            var wasReversing = false;
            for (var t = 0f; t < 60f && truck.State != CrewUnitState.Tipping; t += 0.05f)
            {
                _dispatcher.Tick(0.05f);
                truck.Tick(0.05f);
                // Arriving clears the flag in the same tick it enters the last cell.
                if (truck.Cell != last && (truck.Reversing || wasReversing))
                    reversedCells++;
                last = truck.Cell;
                wasReversing = truck.Reversing;
            }

            Assert.That(truck.State, Is.EqualTo(CrewUnitState.Tipping), truck.Status);
            Assert.That(reversedCells, Is.GreaterThanOrEqualTo(CrewUnit.ReverseCells), "it backed the whole run in to the tip");
            var cell = truck.Cell;
            var target = truck.JobTarget;
            Assert.That(Mathf.Max(Mathf.Abs(target.x - cell.x), Mathf.Abs(target.y - cell.y)), Is.EqualTo(2),
                "it tips past its tailgate, not under its own bed");
            var facing = new Vector2(Mathf.Sin(truck.Heading * Mathf.Deg2Rad), Mathf.Cos(truck.Heading * Mathf.Deg2Rad));
            var toTip = new Vector2(target.x + 0.5f, target.y + 0.5f) - truck.Position;
            Assert.That(Vector2.Dot(facing, toTip.normalized), Is.LessThan(-0.7f), "its tail is to the tip");
        }
    }
}
