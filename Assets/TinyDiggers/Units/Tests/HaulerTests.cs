using System;
using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>Slice 5: diggers and haulers, and tipping only where the player has said.</summary>
    public class HaulerTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 24;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        AngleOfReposeSimulator _slump;
        readonly List<CrewUnit> _units = new List<CrewUnit>();

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    SetHeight(x, z, 8f);
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var unit in _units)
                unit.Dispose();
            _units.Clear();
            _slump?.Dispose();
            _dispatcher.Dispose();
            _map.Dispose();
        }

        void SetHeight(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f) });

        void WithSlump() => _slump = new AngleOfReposeSimulator(_grid);

        CrewUnit Spawn(int x, int z, UnitRole role = UnitRole.Digger, float capacity = 5f)
        {
            var unit = new CrewUnit(_dispatcher, x, z, role, capacity);
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
                _slump?.RunUntilStable();
                elapsed += TickSeconds;
                eachTick?.Invoke();
            }

            return elapsed;
        }

        float TerrainVolumeAbove(float datum)
        {
            var total = 0f;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    total += _grid.GetSurfaceHeight(x, z) - datum;
            return total;
        }

        // --- transfer -----------------------------------------------------------------------------

        [Test]
        public void TransferMovesMaterialAndConservesVolume()
        {
            var scoop = new MaterialInventory(5f);
            var bed = new MaterialInventory(20f);
            scoop.Add(MaterialTable.DirtLoose, 3f);
            scoop.Add(MaterialTable.RockLoose, 2f);

            var moved = Excavation.Transfer(scoop, bed, 4f);

            Assert.That(moved, Is.EqualTo(4f).Within(Tolerance));
            Assert.That(scoop.Total + bed.Total, Is.EqualTo(5f).Within(Tolerance));
            Assert.That(bed.GetVolume(MaterialTable.RockLoose), Is.EqualTo(2f).Within(Tolerance), "the newest went first");
            Assert.That(bed.GetVolume(MaterialTable.DirtLoose), Is.EqualTo(2f).Within(Tolerance));

            // A full bed takes no more, and nothing is lost trying.
            var small = new MaterialInventory(4.5f);
            var spare = new MaterialInventory(0.5f);
            small.Add(MaterialTable.DirtLoose, 4f);
            var squeezed = Excavation.Transfer(small, spare, 4f);
            Assert.That(squeezed, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(small.Total + spare.Total, Is.EqualTo(4f).Within(Tolerance));
        }

        [Test]
        public void ADiggerEmptiesItsScoopIntoAHaulerBesideIt()
        {
            var digger = Spawn(10, 10);
            var hauler = Spawn(11, 10, UnitRole.Hauler, 20f);
            for (var x = 12; x <= 15; x++)
                _map.Designate(x, 10, DesignationKind.Dig, 7f);

            var before = TerrainVolumeAbove(0f);
            Run(120f, () => hauler.Inventory.Total > 4f);

            Assert.That(hauler.Inventory.Total, Is.GreaterThan(4f), $"hauler took nothing: {hauler.Status} | {digger.Status}");
            Assert.That(digger.Transferred, Is.EqualTo(hauler.Transferred).Within(Tolerance));
            var moved = before - TerrainVolumeAbove(0f);
            Assert.That(moved, Is.GreaterThan(0f), "ground was dug");
            Assert.That(digger.Inventory.Total + hauler.Inventory.Total, Is.GreaterThan(0f));
        }

        // --- parking ------------------------------------------------------------------------------

        [Test]
        public void AHaulerLeavesWhenItIsFull()
        {
            WithSlump();
            var digger = Spawn(10, 10);
            var hauler = Spawn(11, 12, UnitRole.Hauler, 6f);
            for (var z = 9; z <= 12; z++)
                for (var x = 12; x <= 15; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 6f);
            for (var z = 2; z <= 4; z++)
                for (var x = 2; x <= 4; x++)
                    _map.SetDumpZone(x, z, true, 12f);

            Run(300f, () => hauler.Inventory.Remaining < 1f);
            Assert.That(hauler.Inventory.Remaining, Is.LessThan(1f), $"never filled: {hauler.Status}");

            Run(60f, () => hauler.State != CrewUnitState.Parked && hauler.Job != CrewJobKind.Serve);
            Assert.That(hauler.State, Is.Not.EqualTo(CrewUnitState.Parked), "it stayed parked while full");
            Assert.That(_dispatcher.DiggerFor(hauler.Id), Is.EqualTo(-1), "it let its digger go");
            Assert.That(digger.Id, Is.Not.EqualTo(hauler.Id));
        }

        [Test]
        public void AParkedHaulerWaitsForAFullBedWhileItsDiggerIsStillWorking()
        {
            // A digger with plenty to dig, and a hauler with a bed far too big to fill quickly.
            for (var z = 8; z <= 12; z++)
                for (var x = 8; x <= 12; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 5f);
            // Somewhere to tip, so the only reason it could leave is a full bed.
            for (var z = 2; z <= 4; z++)
                for (var x = 2; x <= 4; x++)
                    _map.SetDumpZone(x, z, true);
            var digger = Spawn(13, 10);
            var hauler = Spawn(14, 14, UnitRole.Hauler, 400f);
            hauler.ParkPatience = 1f;

            Run(120f, () => hauler.State == CrewUnitState.Parked);
            Assert.That(hauler.State, Is.EqualTo(CrewUnitState.Parked), $"never parked: {hauler.Status}");

            // Long past its patience, and nowhere near full: it stays, because more is coming
            // (Ronan: "he shouldn't go until full").
            Run(30f, () => hauler.State != CrewUnitState.Parked && !digger.StillWorking);
            Assert.That(hauler.Inventory.Remaining, Is.GreaterThan(0f), "the test wants a part load");
            Assert.That(hauler.State, Is.EqualTo(CrewUnitState.Parked),
                $"left part loaded while its digger was still working: {hauler.Status}");
        }

        [Test]
        public void AParkedHaulerLeavesAfterItsPatienceRunsOut()
        {
            // A digger with nothing to dig: the hauler parks, gets nothing, and gives up.
            var digger = Spawn(10, 10);
            var hauler = Spawn(14, 14, UnitRole.Hauler, 20f);
            hauler.ParkPatience = 3f;

            Run(60f, () => hauler.State == CrewUnitState.Parked);
            Assert.That(hauler.State, Is.EqualTo(CrewUnitState.Parked), $"never parked: {hauler.Status}");
            Assert.That(_dispatcher.DiggerFor(hauler.Id), Is.EqualTo(digger.Id));

            var waited = Run(30f, () => hauler.State != CrewUnitState.Parked);

            Assert.That(hauler.State, Is.Not.EqualTo(CrewUnitState.Parked), "it parked for ever");
            Assert.That(waited, Is.GreaterThanOrEqualTo(hauler.ParkPatience - 1f).And.LessThan(hauler.ParkPatience + 3f));
        }

        [Test]
        public void ADiggerWithAFullScoopAndNoHaulerInReachWaitsWithoutTouchingTheGround()
        {
            // A wall with no gap: the hauler exists but cannot get to the digger.
            for (var z = 0; z < Size; z++)
                SetHeight(12, z, 11f);
            var digger = Spawn(16, 10);
            Spawn(4, 10, UnitRole.Hauler, 20f);
            for (var x = 14; x <= 19; x++)
                _map.Designate(x, 10, DesignationKind.Dig, 6f);

            Run(120f, () => digger.State == CrewUnitState.WaitingForHauler);
            Assert.That(digger.State, Is.EqualTo(CrewUnitState.WaitingForHauler), digger.Status);
            var heights = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    heights[z * Size + x] = _grid.GetSurfaceHeight(x, z);

            Run(30f, () => false);

            Assert.That(digger.State, Is.EqualTo(CrewUnitState.WaitingForHauler), digger.Status);
            Assert.That(digger.Inventory.Total, Is.GreaterThan(4f), "still holding its load");
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.EqualTo(heights[z * Size + x]).Within(Tolerance),
                        $"({x}, {z}) changed while it waited");
        }

        /// <summary>
        /// Two haulers and one digger: they queue on it (Ronan, 2026-09-24), where until then the
        /// second could never be assigned at all — and, having nothing to do, stood where it
        /// stopped and jammed the one that had. Measured on the same site, the pair went from
        /// 0.88 m³ a game minute back up to 8.24. See <c>DiggerDutyTests</c>.
        /// </summary>
        [Test]
        public void TwoHaulersQueueOnOneDigger()
        {
            var digger = Spawn(10, 10);
            var a = Spawn(6, 6, UnitRole.Hauler, 20f);
            var b = Spawn(7, 7, UnitRole.Hauler, 20f);
            for (var z = 9; z <= 11; z++)
                for (var x = 12; x <= 14; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 6f);

            var doubled = 0;
            Run(300f, () => _map.Count == 0, () =>
            {
                if (_dispatcher.DiggerFor(a.Id) == digger.Id && _dispatcher.DiggerFor(b.Id) == digger.Id)
                    doubled++;
            });

            Assert.That(doubled, Is.GreaterThan(0), "both haulers should have been able to serve the one digger");
        }

        /// <summary>
        /// The other half of the same rule: a spare hauler goes to a digger of its own before it
        /// queues on one that is already served, so a second digger is always worth more than a
        /// second dumper on the same face.
        /// </summary>
        [Test]
        public void AHaulerTakesAnUnservedDiggerOverAQueue()
        {
            var first = Spawn(10, 10);
            var second = Spawn(10, 16);
            for (var z = 9; z <= 11; z++)
                for (var x = 12; x <= 14; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 6f);
            for (var z = 15; z <= 17; z++)
                for (var x = 12; x <= 14; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 6f);

            var a = Spawn(6, 6, UnitRole.Hauler, 20f);
            var b = Spawn(7, 7, UnitRole.Hauler, 20f);
            Run(120f, () => _dispatcher.DiggerFor(a.Id) >= 0 && _dispatcher.DiggerFor(b.Id) >= 0);

            Assert.That(_dispatcher.DiggerFor(a.Id), Is.Not.EqualTo(_dispatcher.DiggerFor(b.Id)),
                $"one each: {a.Id} serves {_dispatcher.DiggerFor(a.Id)}, {b.Id} serves {_dispatcher.DiggerFor(b.Id)}");
            Assert.That(_dispatcher.HaulerCountFor(first.Id), Is.EqualTo(1));
            Assert.That(_dispatcher.HaulerCountFor(second.Id), Is.EqualTo(1));
        }

        // --- tipping from the rim -------------------------------------------------------------------

        [Test]
        public void AHaulerFillsAPitFromItsRimWithoutDrivingIn()
        {
            // A 2 m deep 5x5 pit, designated to be filled back to ground level.
            WithSlump();
            for (var z = 8; z <= 12; z++)
                for (var x = 8; x <= 12; x++)
                {
                    SetHeight(x, z, 6f);
                    _map.Designate(x, z, DesignationKind.Fill, 8f);
                }

            var hauler = Spawn(3, 10, UnitRole.Hauler, 20f);
            hauler.Inventory.Add(MaterialTable.DirtLoose, 20f);

            var tippedFromInside = 0;
            var took = Run(600f, () => _map.Count == 0, () =>
            {
                // Driving across, or standing on part of it that is already filled, is fine;
                // standing on ground still to be filled is not.
                var stand = hauler.JobStand;
                if (hauler.State == CrewUnitState.Tipping && _map.GetKind(stand.x, stand.y) == DesignationKind.Fill)
                    tippedFromInside++;
                // The player keeps it supplied: a digger would do this in a real crew.
                if (hauler.Inventory.Total < 1f)
                    hauler.Inventory.Add(MaterialTable.DirtLoose, hauler.Inventory.Remaining);
            });

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {hauler.Status}");
            for (var z = 8; z <= 12; z++)
                for (var x = 8; x <= 12; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.GreaterThanOrEqualTo(8f - Tolerance), $"({x}, {z}) left low");
            Assert.That(tippedFromInside, Is.EqualTo(0), "it stood on ground it was supposed to be filling");
        }

        // --- a mixed crew ---------------------------------------------------------------------------

        [Test]
        public void TwoDiggersAndTwoHaulersTakeAMoundDown()
        {
            WithSlump();
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 15), Math.Abs(z - 15));
                    if (ring <= 2)
                        SetHeight(x, z, 11f - ring);
                }
            }

            Spawn(3, 10);
            Spawn(3, 12);
            Spawn(5, 10, UnitRole.Hauler, 20f);
            Spawn(5, 12, UnitRole.Hauler, 20f);
            for (var z = 2; z <= 7; z++)
                for (var x = 2; x <= 7; x++)
                    _map.SetDumpZone(x, z, true, 14f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (_grid.GetSurfaceHeight(x, z) > 8f)
                        _map.Designate(x, z, DesignationKind.Dig, 8f);

            var took = Run(1500f, () => _map.Count == 0);

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {_units[0].Status} | {_units[2].Status}");
            for (var z = 13; z <= 17; z++)
                for (var x = 13; x <= 17; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(8f + Tolerance), $"({x}, {z})");
            Assert.That(_units[2].Transferred + _units[3].Transferred, Is.GreaterThan(0f), "the haulers carried nothing");
        }

        [Test]
        public void AHaulerEmptiesItsBedBeforeGoingBackToItsDigger()
        {
            for (var z = 2; z <= 5; z++)
                for (var x = 2; x <= 5; x++)
                    _map.SetDumpZone(x, z, true, 12f);
            var hauler = Spawn(10, 10, UnitRole.Hauler, 20f);
            hauler.Inventory.Add(MaterialTable.DirtLoose, 20f);
            Spawn(18, 18);
            for (var x = 19; x <= 22; x++)
                _map.Designate(x, 18, DesignationKind.Dig, 7f);

            // Once it starts tipping it should not leave with a bedful still on board.
            var leftLoaded = 0;
            Run(600f, () => hauler.Inventory.Total < 1f, () =>
            {
                // Driving back to a digger with a load still on board is the fault; standing
                // parked while a digger fills it is not.
                if (hauler.Job == CrewJobKind.Serve && hauler.State == CrewUnitState.Moving && hauler.Inventory.Total > 1f)
                    leftLoaded++;
            });

            Assert.That(hauler.Inventory.Total, Is.LessThan(1f), $"still holding {hauler.Inventory.Total:0.0} m³: {hauler.Status}");
            Assert.That(leftLoaded, Is.EqualTo(0), "it went back to its digger with a load still on board");
        }

        [Test]
        public void ADiggerLeavesTheHaulingToTheHaulers()
        {
            // The Dump Zone is right across the map. With a hauler in the crew the digger should
            // never make that trip itself, however long it has to wait between loads.
            for (var z = 2; z <= 5; z++)
                for (var x = 2; x <= 5; x++)
                    _map.SetDumpZone(x, z, true, 12f);
            var digger = Spawn(18, 18);
            Spawn(17, 18, UnitRole.Hauler, 20f);
            for (var x = 19; x <= 22; x++)
                _map.Designate(x, 18, DesignationKind.Dig, 6f);

            var diggerTipped = 0;
            var took = Run(900f, () => _map.Count == 0, () =>
            {
                if (digger.State == CrewUnitState.Tipping)
                    diggerTipped++;
            });

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {digger.Status}");
            Assert.That(diggerTipped, Is.EqualTo(0), "the digger hauled its own spoil");
            Assert.That(_units[1].Transferred, Is.GreaterThan(0f), "the hauler did the carrying");
        }

        [Test]
        public void WithNowhereToTipEveryUnitStopsAndTheGroundOutsideIsUntouched()
        {
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 15), Math.Abs(z - 15));
                    if (ring <= 2)
                        SetHeight(x, z, 11f - ring);
                }
            }

            Spawn(3, 10);
            Spawn(5, 12, UnitRole.Hauler, 20f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (_grid.GetSurfaceHeight(x, z) > 8f)
                        _map.Designate(x, z, DesignationKind.Dig, 8f);

            var before = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    before[z * Size + x] = _grid.GetSurfaceHeight(x, z);

            var took = Run(600f, () =>
            {
                foreach (var unit in _units)
                    if (unit.State != CrewUnitState.NeedsSomewhereToTip)
                        return false;
                return true;
            });

            foreach (var unit in _units)
            {
                Assert.That(unit.State, Is.EqualTo(CrewUnitState.NeedsSomewhereToTip), $"after {took:0} s: {unit.Status}");
                Assert.That(unit.Status, Does.Contain("Dump Zone"));
            }

            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (_map.GetKind(x, z) == DesignationKind.None && before[z * Size + x] <= 8f + Tolerance)
                        Assert.That(_grid.GetSurfaceHeight(x, z), Is.EqualTo(before[z * Size + x]).Within(Tolerance),
                            $"spoil landed on ({x}, {z})");
        }
    }
}

