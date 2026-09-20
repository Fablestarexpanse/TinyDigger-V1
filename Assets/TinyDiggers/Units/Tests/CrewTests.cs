using System;
using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>Slice 4: several units sharing a dispatcher, the ground and the roads.</summary>
    public class CrewTests
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

        /// <summary>Lets tipped material settle, as it does in play.</summary>
        void WithSlump() => _slump = new AngleOfReposeSimulator(_grid);

        CrewUnit Spawn(int x, int z)
        {
            var unit = new CrewUnit(_dispatcher, x, z);
            _units.Add(unit);
            return unit;
        }

        /// <summary>Ticks the whole crew until <paramref name="done"/> or the time runs out.</summary>
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

        // --- assignment ---------------------------------------------------------------------------

        [Test]
        public void ADesignationIsNeverGivenToTwoUnits()
        {
            var a = Spawn(3, 10);
            var b = Spawn(3, 12);
            _map.Designate(15, 11, DesignationKind.Dig, 7f);

            var bothOnIt = 0;
            Run(60f, () => _map.Count == 0, () =>
            {
                var workers = 0;
                foreach (var unit in _units)
                    if (unit.Job == CrewJobKind.Dig && unit.JobTarget == new Vector2Int(15, 11))
                        workers++;
                if (workers > 1)
                    bothOnIt++;
            });

            Assert.That(bothOnIt, Is.EqualTo(0), "two units took the same designation");
            Assert.That(_map.Count, Is.EqualTo(0));
            Assert.That(_grid.GetSurfaceHeight(15, 11), Is.EqualTo(7f).Within(Tolerance));
            Assert.That(a.Id, Is.Not.EqualTo(b.Id));
        }

        [Test]
        public void ADesignationIsReleasedWhenItsUnitGoes()
        {
            var a = Spawn(3, 10);
            var b = Spawn(20, 20);
            _map.Designate(6, 10, DesignationKind.Dig, 7f);
            Run(2f, () => a.Job == CrewJobKind.Dig);
            Assert.That(_dispatcher.ClaimOf(a.Id), Is.EqualTo(new Vector2Int(6, 10)));
            Assert.That(_dispatcher.IsClaimedByOther(6, 10, b.Id), Is.True);

            _units.Remove(a);
            a.Dispose();

            Assert.That(_dispatcher.IsClaimedByOther(6, 10, b.Id), Is.False, "the claim went with it");
            var took = Run(120f, () => _map.Count == 0);
            Assert.That(_map.Count, Is.EqualTo(0), $"the other unit picked it up; after {took:0} s: {b.Status}");
        }

        // --- traffic ------------------------------------------------------------------------------

        [Test]
        public void UnitsNeverShareACell()
        {
            WithSlump();
            for (var i = 0; i < 4; i++)
                Spawn(3 + i % 2 * 2, 10 + i / 2 * 2);
            for (var z = 2; z <= 5; z++)
                for (var x = 2; x <= 5; x++)
                    _map.SetDumpZone(x, z, true, 12f);
            for (var z = 10; z <= 13; z++)
                for (var x = 14; x <= 17; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 7f);

            var shared = 0;
            var tippedUnderAnother = 0;
            var took = Run(600f, () => _map.Count == 0, () =>
            {
                for (var i = 0; i < _units.Count; i++)
                {
                    for (var j = i + 1; j < _units.Count; j++)
                        if (_units[i].Cell == _units[j].Cell)
                            shared++;
                    if (_units[i].State != CrewUnitState.Tipping)
                        continue;
                    for (var j = 0; j < _units.Count; j++)
                        if (j != i && _units[i].JobTarget == _units[j].Cell)
                            tippedUnderAnother++;
                }
            });

            Assert.That(shared, Is.EqualTo(0), "two units stood on one cell");
            Assert.That(tippedUnderAnother, Is.EqualTo(0), "a unit tipped where another was standing");
            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s");
        }

        [Test]
        public void TwoUnitsSharingOneGapDoNotDeadlock()
        {
            // A wall with a single one-cell gap. The digs are east of it and the Dump Zone west,
            // so both units have to drive through the gap, in both directions, again and again.
            for (var z = 0; z < Size; z++)
                SetHeight(12, z, 11f);
            SetHeight(12, 10, 8f);
            for (var z = 9; z <= 11; z++)
                for (var x = 5; x <= 7; x++)
                {
                    SetHeight(x, z, 6f);
                    _map.SetDumpZone(x, z, true, 9f);
                }

            var west = Spawn(4, 10);
            var east = Spawn(18, 10);
            for (var x = 14; x <= 19; x++)
                _map.Designate(x, 10, DesignationKind.Dig, 6f);

            var longestWait = 0f;
            var took = Run(900f, () => _map.Count == 0, () =>
            {
                foreach (var unit in _units)
                    longestWait = Mathf.Max(longestWait, unit.WaitingFor);
            });

            Assert.That(_map.Count, Is.EqualTo(0), $"deadlocked after {took:0} s: {west.Status} | {east.Status}");
            Assert.That(longestWait, Is.LessThan(5f), "a unit waited far longer than its traffic timeout");
            Assert.That(west.RepathCount + east.RepathCount, Is.GreaterThan(0), "they had to go round each other at least once");
        }

        // --- a crew on a mound ---------------------------------------------------------------------

        [Test]
        public void FourUnitsTakeTheMoundDownTogether()
        {
            WithSlump();
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 12), Math.Abs(z - 12));
                    if (ring <= 3)
                        SetHeight(x, z, 12f - ring);
                }
            }

            for (var i = 0; i < 4; i++)
                Spawn(3 + i % 2 * 2, 12 + i / 2 * 2);
            for (var z = 1; z <= 8; z++)
                for (var x = 1; x <= 8; x++)
                    _map.SetDumpZone(x, z, true, 16f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (_grid.GetSurfaceHeight(x, z) > 8f)
                        _map.Designate(x, z, DesignationKind.Dig, 8f);

            var idleWithWork = 0;
            var took = Run(1200f, () => _map.Count == 0, () =>
            {
                foreach (var unit in _units)
                    if (unit.State == CrewUnitState.Idle && unit.UnreachableCount == 0 && _map.Count > 0)
                        idleWithWork++;
            });

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {_units[0].Status}");
            for (var z = 0; z < Size; z++)
                for (var x = 12; x < Size; x++)
                    if (Math.Max(Math.Abs(x - 12), Math.Abs(z - 12)) <= 3)
                        Assert.That(_grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(8f + Tolerance),
                            $"({x}, {z}) — the half of the mound away from the Dump Zone and its overspill");
            // A unit may idle for a tick between jobs; sitting out whole seconds is the bug.
            Assert.That(idleWithWork * TickSeconds, Is.LessThan(took * 0.5f), "units sat idle with work left");
        }

        // --- dump zone caps -------------------------------------------------------------------------

        [Test]
        public void SpoilStopsAtTheDumpZoneCap()
        {
            // Four zone cells with 1 m of room each: enough for one 2 m cut of dirt (2.5 m³).
            for (var z = 9; z <= 10; z++)
                for (var x = 5; x <= 6; x++)
                    _map.SetDumpZone(x, z, true, 9f);
            Spawn(10, 10);
            _map.Designate(14, 10, DesignationKind.Dig, 6f);

            var took = Run(900f, () => _map.Count == 0);

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {_units[0].Status}");
            for (var z = 9; z <= 10; z++)
                for (var x = 5; x <= 6; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(9f + Tolerance), $"zone ({x}, {z}) went over its cap");
        }

        [Test]
        public void AFullDumpZoneIsReportedAndTheUnitStops()
        {
            // One zone cell, capped where it already is: it can never take anything.
            _map.SetDumpZone(5, 10, true, 8f);
            var unit = Spawn(10, 10);
            for (var x = 14; x <= 19; x++)
                _map.Designate(x, 10, DesignationKind.Dig, 6f);

            var took = Run(900f, () => unit.State == CrewUnitState.NeedsSomewhereToTip);

            Assert.That(unit.State, Is.EqualTo(CrewUnitState.NeedsSomewhereToTip), $"after {took:0} s: {unit.Status}");
            Assert.That(unit.DumpZoneFull, Is.True, "the full Dump Zone was never reported");
            Assert.That(unit.Status, Does.Contain("no room"));
            Assert.That(_grid.GetSurfaceHeight(5, 10), Is.EqualTo(8f).Within(Tolerance), "nothing went on the capped cell");
        }

    }
}
