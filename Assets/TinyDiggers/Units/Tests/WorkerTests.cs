using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The worker robot (Ronan, 2026-09-21): the first unit, a human with a barrow. It digs and
    /// hauls its own tiny loads, and the player moves it about: it goes where it is sent and holds
    /// there until told to work.
    /// </summary>
    public class WorkerTests
    {
        const float Half = 0.5f;
        const int Size = 20;
        const float Surface = 6f;
        const float Barrow = 0.19f;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        readonly List<CrewUnit> _units = new List<CrewUnit>();

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), Half, 0f, Half);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    SetHeight(x, z, Surface);
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid) { MaxStepHeight = 1f };
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var unit in _units)
                unit.Dispose();
            _units.Clear();
            _dispatcher.Dispose();
            _map.Dispose();
        }

        void SetHeight(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f) });

        CrewUnit Spawn(int x, int z, UnitRole role = UnitRole.Worker, float capacity = Barrow)
        {
            var unit = new CrewUnit(_dispatcher, x, z, role, capacity) { Speed = 1.5f };
            _units.Add(unit);
            return unit;
        }

        float Run(float maxSeconds, Func<bool> done, Action eachTick = null)
        {
            var elapsed = 0f;
            while (elapsed < maxSeconds && !done())
            {
                _dispatcher.Tick(TickSeconds);
                foreach (var unit in _units)
                    unit.Tick(TickSeconds);
                elapsed += TickSeconds;
                eachTick?.Invoke();
            }

            return elapsed;
        }

        void DesignateDig(int x, int z, float to) => _map.Designate(x, z, DesignationKind.Dig, to);

        [Test]
        public void AWorkerCarriesItsOwnBarrowToTheDumpZoneEvenWithAHaulerAbout()
        {
            DesignateDig(5, 10, Surface - Half);
            DesignateDig(6, 10, Surface - Half);
            for (var z = 10; z <= 11; z++)
                for (var x = 14; x <= 15; x++)
                    _map.SetDumpZone(x, z, true, Surface + 2f);
            var worker = Spawn(10, 10);
            var hauler = Spawn(10, 4, UnitRole.Hauler, 20f);
            var waited = false;

            // Tipping goes a whole step of one cell at a time (0.125 m³), so what is left under
            // that rides on in the barrow, as it does for any digger.
            var tipStep = Half * _grid.CellArea;
            Run(120f, () => _grid.GetSurfaceHeight(5, 10) <= Surface - Half + 1e-3f
                            && _grid.GetSurfaceHeight(6, 10) <= Surface - Half + 1e-3f
                            && worker.Inventory.Total < tipStep,
                () => waited |= worker.State == CrewUnitState.WaitingForHauler);

            Assert.That(_grid.GetSurfaceHeight(5, 10), Is.EqualTo(Surface - Half).Within(1e-3f), "dug");
            Assert.That(_grid.GetSurfaceHeight(6, 10), Is.EqualTo(Surface - Half).Within(1e-3f), "dug");
            Assert.That(worker.Inventory.Total, Is.LessThan(tipStep), "it tipped what it dug, all but a part-step");
            Assert.That(waited, Is.False, "a worker never waits for a hauler");
            Assert.That(hauler.Transferred, Is.Zero, "the hauler never took a load from it");
            var dumped = 0f;
            for (var z = 10; z <= 11; z++)
                for (var x = 14; x <= 15; x++)
                    dumped += (_grid.GetSurfaceHeight(x, z) - Surface) * _grid.CellArea;
            Assert.That(dumped, Is.GreaterThan(0.2f), "two scoops' worth landed in the dump zone");
        }

        [Test]
        public void AnOrderedWorkerGoesThereAndHoldsInsteadOfWorking()
        {
            DesignateDig(4, 4, Surface - Half);
            var worker = Spawn(6, 6);

            Assert.That(worker.OrderMoveTo(15, 15), Is.True);
            Run(30f, () => worker.Cell == new Vector2Int(15, 15) && worker.State == CrewUnitState.Idle);
            Assert.That(worker.Cell, Is.EqualTo(new Vector2Int(15, 15)));
            Assert.That(worker.Holding, Is.True);

            Run(5f, () => false);
            Assert.That(worker.Cell, Is.EqualTo(new Vector2Int(15, 15)), "it held there");
            Assert.That(_grid.GetSurfaceHeight(4, 4), Is.EqualTo(Surface).Within(1e-3f), "and left the designated work alone");

            worker.ReleaseHold();
            Run(60f, () => _grid.GetSurfaceHeight(4, 4) < Surface - 1e-3f);
            Assert.That(_grid.GetSurfaceHeight(4, 4), Is.LessThan(Surface - 1e-3f), "released, it went back to work");
        }

        [Test]
        public void AnOrderOntoDesignatedGroundGoesThereThenWorks()
        {
            DesignateDig(12, 12, Surface - Half);
            var worker = Spawn(3, 3);

            Assert.That(worker.OrderMoveTo(12, 11, workOnArrival: true), Is.True);
            Run(60f, () => _grid.GetSurfaceHeight(12, 12) < Surface - 1e-3f);

            Assert.That(_grid.GetSurfaceHeight(12, 12), Is.LessThan(Surface - 1e-3f));
            Assert.That(worker.Holding, Is.False);
        }

        [Test]
        public void AnOrderItCannotReachIsRefusedAndChangesNothing()
        {
            // A pit cell ringed by a wall 6 m high: no way in.
            for (var z = 16; z <= 18; z++)
                for (var x = 16; x <= 18; x++)
                    if (x != 17 || z != 17)
                        SetHeight(x, z, Surface + 6f);
            var worker = Spawn(4, 4);

            Assert.That(worker.OrderMoveTo(17, 17), Is.False);
            Assert.That(worker.OrderMoveTo(-1, 3), Is.False, "off the map");
            Assert.That(worker.Holding, Is.False);
            Assert.That(worker.OrderTarget, Is.Null);
        }

        [Test]
        public void ANewOrderReplacesTheOldOneMidway()
        {
            var worker = Spawn(2, 2);
            worker.OrderMoveTo(17, 2);
            Run(3f, () => false);
            Assert.That(worker.State, Is.EqualTo(CrewUnitState.Moving));

            worker.OrderMoveTo(2, 17);
            Run(40f, () => worker.Cell == new Vector2Int(2, 17) && worker.State == CrewUnitState.Idle);
            Assert.That(worker.Cell, Is.EqualTo(new Vector2Int(2, 17)));
        }

        // --- formation ----------------------------------------------------------------------------

        [Test]
        public void AGroupGetsDistinctCellsNearestTheClickSkippingOnesItCannotStandOn()
        {
            var centre = new Vector2Int(10, 10);
            var targets = CrewFormation.Targets(_grid, centre, 5, (x, z) => !(x == 10 && z == 10) && !(x == 11 && z == 10));

            Assert.That(targets.Count, Is.EqualTo(5));
            Assert.That(targets.Distinct().Count(), Is.EqualTo(5), "one cell each");
            Assert.That(targets, Has.No.Member(new Vector2Int(10, 10)));
            Assert.That(targets, Has.No.Member(new Vector2Int(11, 10)));
            foreach (var t in targets)
                Assert.That((t - centre).sqrMagnitude, Is.LessThanOrEqualTo(2), $"{t} is among the nearest cells");
        }

        [Test]
        public void EachUnitTakesTheTargetNearestItSoPathsDoNotCross()
        {
            var targets = new List<Vector2Int> { new Vector2Int(10, 10), new Vector2Int(12, 10) };
            var units = new List<Vector2> { new Vector2(20f, 10f), new Vector2(0f, 10f) };

            var picks = CrewFormation.Assign(units, targets, new Vector2(11f, 10f));

            Assert.That(picks[0], Is.EqualTo(1), "the one on the right takes the right-hand cell");
            Assert.That(picks[1], Is.EqualTo(0));
        }

        [Test]
        public void MoreUnitsThanTargetsLeavesTheFurthestWithout()
        {
            var targets = new List<Vector2Int> { new Vector2Int(5, 5) };
            var units = new List<Vector2> { new Vector2(9f, 9f), new Vector2(5f, 6f) };

            var picks = CrewFormation.Assign(units, targets, new Vector2(5f, 5f));

            Assert.That(picks[1], Is.EqualTo(0));
            Assert.That(picks[0], Is.EqualTo(-1));
        }
    }
}
