using System;
using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// What each machine carries, and who can cut rock (Ronan, 2026-09-22): three of the digger's
    /// scoops fill the dumper, the starter robot takes six barrows to do the same, and the robot
    /// can take on stone but only slowly — the digger is the machine built for it.
    /// </summary>
    public class LoadsAndStoneTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 16;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        CrewUnit _unit;

        [SetUp]
        public void SetUp()
        {
            // The sandbox's own half-metre cells and steps: one cut is 0.125 m³ there, which fits
            // in a barrow. At a metre a single cut is 1 m³ and no unit in the game could lift it.
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f,
                cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    SetColumn(x, z, MaterialTable.Dirt);
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
        }

        [TearDown]
        public void TearDown()
        {
            _unit?.Dispose();
            _map.Dispose();
        }

        void SetColumn(int x, int z, MaterialId top) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(top, 6f) });


        CrewUnit Spawn(int x, int z, UnitRole role)
        {
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder);
            var unit = new CrewUnit(_dispatcher, x, z, role,
                role == UnitRole.Worker ? UnitLoads.Barrow
                : role == UnitRole.Digger ? UnitLoads.Scoop : UnitLoads.Bed);
            // The digger and the dumper are machines in the game; the crew logic makes no such
            // assumption, so whatever spawns them says so.
            if (role != UnitRole.Worker)
                unit.AsMachine();
            return _unit = unit;
        }

        // --- the loads ------------------------------------------------------------------------

        [Test]
        public void ThreeScoopsFillTheDumper()
        {
            Assert.That(UnitLoads.Bed / UnitLoads.Scoop, Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void SixBarrowsFillTheDumper()
        {
            Assert.That(UnitLoads.Bed / UnitLoads.Barrow, Is.EqualTo(6f).Within(Tolerance));
        }

        [Test]
        public void AScoopIsTwoBarrows()
        {
            Assert.That(UnitLoads.Scoop / UnitLoads.Barrow, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void ADumperTakesThreeScoopsAndNoMore()
        {
            var bed = new MaterialInventory(UnitLoads.Bed);
            for (var scoop = 0; scoop < 3; scoop++)
                Assert.That(bed.CanFit(UnitLoads.Scoop), Is.True, $"scoop {scoop + 1} should fit");
            for (var scoop = 0; scoop < 3; scoop++)
                bed.Add(MaterialTable.Dirt, UnitLoads.Scoop);
            Assert.That(bed.Remaining, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(bed.CanFit(UnitLoads.Scoop), Is.False);
        }

        // --- stone ------------------------------------------------------------------------------

        [Test]
        public void TheDiggerIsBuiltForStoneAndTheRobotIsNot()
        {
            Assert.That(Spawn(2, 2, UnitRole.Digger).CutsStone, Is.True);
            _unit.Dispose();
            Assert.That(Spawn(2, 2, UnitRole.Worker).CutsStone, Is.False);
        }

        [Test]
        public void SoilCostsTheRobotNoMoreThanItCostsTheMachine()
        {
            var robot = Spawn(2, 2, UnitRole.Worker);
            Assert.That(robot.StepSeconds(5, 5), Is.EqualTo(robot.WorkInterval).Within(Tolerance));
        }

        [Test]
        public void RockCostsTheRobotMoreAndTheMachineNothingExtra()
        {
            SetColumn(5, 5, MaterialTable.Rock);

            var machine = Spawn(2, 2, UnitRole.Digger);
            var machineSeconds = machine.StepSeconds(5, 5);
            Assert.That(machineSeconds, Is.EqualTo(machine.WorkInterval).Within(Tolerance));
            machine.Dispose();

            var robot = Spawn(2, 2, UnitRole.Worker);
            var robotSeconds = robot.StepSeconds(5, 5);
            Assert.That(robotSeconds, Is.GreaterThan(machineSeconds * 2f),
                "rock should cost the robot at least twice a soil step");
        }

        [Test]
        public void HarderRockCostsTheRobotMore()
        {
            var robot = Spawn(2, 2, UnitRole.Worker);
            SetColumn(5, 5, MaterialTable.Rock);
            var rock = robot.StepSeconds(5, 5);
            SetColumn(5, 5, MaterialTable.Granite);
            var granite = robot.StepSeconds(5, 5);
            Assert.That(granite, Is.GreaterThan(rock));
        }

        [Test]
        public void TheRobotStillGetsThroughRockInTheEnd()
        {
            for (var z = 4; z <= 6; z++)
                for (var x = 4; x <= 6; x++)
                    SetColumn(x, z, MaterialTable.Rock);
            Assert.That(_map.Designate(5, 5, DesignationKind.Dig, 7.5f), Is.True);

            var robot = Spawn(4, 4, UnitRole.Worker);
            var before = _grid.GetSurfaceHeight(5, 5);
            var elapsed = 0f;
            while (elapsed < 120f && _grid.GetSurfaceHeight(5, 5) >= before - Tolerance)
            {
                // The unit does not own this dispatcher, so the test drives it.
                _dispatcher.Tick(TickSeconds);
                robot.Tick(TickSeconds);
                elapsed += TickSeconds;
            }

            Assert.That(_grid.GetSurfaceHeight(5, 5), Is.LessThan(before),
                "the robot should cut rock eventually, only slowly");
        }
    }
}
