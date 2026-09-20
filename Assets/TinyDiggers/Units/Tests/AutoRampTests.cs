using System;
using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>Slice 3: the reachability cache, benching, Auto ramps and Dump Zones.</summary>
    public class AutoRampTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 24;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        CrewUnit _unit;

        [SetUp]
        public void SetUp()
        {
            // Flat ground at 8 m: 2 m bedrock under dirt.
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    SetHeight(x, z, 8f);
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
        }

        [TearDown]
        public void TearDown()
        {
            _unit?.Dispose();
            _map.Dispose();
        }

        void SetHeight(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f) });

        CrewUnit Spawn(int x, int z) => _unit = new CrewUnit(_grid, _map, _pathfinder, x, z);

        float Run(float maxSeconds, Func<bool> done, Action eachTick = null)
        {
            var elapsed = 0f;
            while (elapsed < maxSeconds && !done())
            {
                _unit.Tick(TickSeconds);
                elapsed += TickSeconds;
                eachTick?.Invoke();
            }

            return elapsed;
        }

        /// <summary>A plain at 8 m, a 2 m band at 10 m from x = 10, and a plateau at 12 m from x = 12.</summary>
        void BuildTwoTiers()
        {
            for (var z = 0; z < Size; z++)
            {
                for (var x = 10; x < Size; x++)
                    SetHeight(x, z, x < 12 ? 10f : 12f);
            }
        }

        // --- regions --------------------------------------------------------------------------

        [Test]
        public void RegionsRebuildOnlyAfterAHeightChange()
        {
            var regions = new RegionMap(_grid, _pathfinder);
            Assert.That(regions.CanReach(2, 2, 20, 20), Is.True);
            Assert.That(regions.RebuildCount, Is.EqualTo(1));

            // More queries, from anywhere: no new build.
            Assert.That(regions.CanReach(15, 3, 1, 1), Is.True);
            Assert.That(regions.CanReach(2, 2, 3, 3), Is.True);
            Assert.That(regions.RebuildCount, Is.EqualTo(1));

            // A wall 3 m high across the map cuts it in two; the next query sees it.
            for (var z = 0; z < Size; z++)
                SetHeight(12, z, 11f);
            Assert.That(regions.IsStale, Is.True);
            Assert.That(regions.CanReach(2, 2, 20, 20), Is.False);
            Assert.That(regions.RegionCount, Is.EqualTo(3), "either side, and the wall itself");
            Assert.That(regions.RebuildCount, Is.EqualTo(2));

            // A gap in the wall joins them again.
            SetHeight(12, 5, 8f);
            Assert.That(regions.CanReach(2, 2, 20, 20), Is.True);
            Assert.That(regions.RebuildCount, Is.EqualTo(3));
            regions.Dispose();
        }

        [Test]
        public void ARebuildOnlyTouchesTheRegionThatChanged()
        {
            // Two halves split by a wall, plus the wall itself as its own region.
            for (var z = 0; z < Size; z++)
                SetHeight(12, z, 11f);
            var regions = new RegionMap(_grid, _pathfinder);
            var west = regions.RegionSize(2, 2);
            var east = regions.RegionSize(20, 20);
            Assert.That(west, Is.GreaterThan(0));
            Assert.That(east, Is.GreaterThan(0));

            // Dig a cell deep in the west half: only the west half is re-labelled.
            SetHeight(4, 4, 7f);
            regions.Update();

            Assert.That(regions.CellsRelabelledLast, Is.LessThanOrEqualTo(west + 2), "the east half was left alone");
            Assert.That(regions.CellsRelabelledLast, Is.GreaterThan(0));
            Assert.That(regions.CanReach(2, 2, 20, 20), Is.False);
            Assert.That(regions.RegionSize(20, 20), Is.EqualTo(east));
            regions.Dispose();
        }

        [Test]
        public void AnUnreachableUnitDoesNotSearchTheMapEveryRethink()
        {
            // The Slice 2 340 ms frame: every rethink while UNREACHABLE flooded the map and ran
            // nearest-searches that expanded every reachable cell and found nothing.
            for (var z = 10; z <= 12; z++)
                for (var x = 10; x <= 12; x++)
                    SetHeight(x, z, 12f);
            Spawn(3, 3);
            _unit.AutoRamp = false;
            _map.Designate(11, 11, DesignationKind.Dig, 8f);

            Run(5f, () => false);

            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Unreachable));
            Assert.That(_unit.Dispatcher.Regions.RebuildCount, Is.EqualTo(1), "nothing changed, so one build");

            Assert.That(_pathfinder.LastExpandedCount, Is.EqualTo(0), "no search ran: no candidate passed the set lookup");
        }

        // --- auto ramps -------------------------------------------------------------------------

        [Test]
        public void CutsAClimbableRampUpTwoTiersToReachItsTarget()
        {
            BuildTwoTiers();
            Spawn(3, 10);
            _map.Designate(16, 10, DesignationKind.Dig, 11f);

            var autosMade = new HashSet<Vector2Int>();
            var took = Run(300f, () => _map.Count == 0, () =>
            {
                foreach (var cell in _map.ActiveCells)
                    if (_map.IsAuto(cell % Size, cell / Size))
                        autosMade.Add(new Vector2Int(cell % Size, cell / Size));
            });

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0.0} s: {_unit.Status}");
            Assert.That(_grid.GetSurfaceHeight(16, 10), Is.EqualTo(11f).Within(Tolerance));
            Assert.That(autosMade.Count, Is.GreaterThanOrEqualTo(2), "one Auto step per tier");

            // The way up it made: from the plain to the target, every step drivable and none down.
            var route = new List<Vector2Int>();
            Assert.That(_pathfinder.TryFindPath(3, 10, 15, 10, route), Is.True, "the plateau is drivable now");
            for (var i = 1; i < route.Count; i++)
            {
                var rise = _grid.GetSurfaceHeight(route[i].x, route[i].y) - _grid.GetSurfaceHeight(route[i - 1].x, route[i - 1].y);
                Assert.That(Mathf.Abs(rise), Is.LessThanOrEqualTo(_pathfinder.MaxStepHeight + Tolerance), $"step {i}");
            }

            foreach (var cell in autosMade)
                Assert.That(_map.GetKind(cell.x, cell.y), Is.EqualTo(DesignationKind.None), "no Auto step left behind");
        }

        [Test]
        public void TheRampCorridorClimbsMonotonicallyOnceCut()
        {
            BuildTwoTiers();
            Spawn(3, 10);
            _map.Designate(16, 10, DesignationKind.Dig, 11f);

            // Run until the ramp is no longer needed; keep the last corridor it planned.
            List<Vector2Int> corridor = null;
            Run(300f, () => _map.Count == 0, () =>
            {
                if (_unit.HasRamp && _unit.RampCorridor.Count > 0)
                    corridor = new List<Vector2Int>(_unit.RampCorridor);
            });

            Assert.That(corridor, Is.Not.Null, "a ramp was planned");
            Assert.That(corridor[corridor.Count - 1], Is.EqualTo(new Vector2Int(16, 10)));
            // Up to the cell before the target: never down, never more than one step up.
            for (var i = 1; i < corridor.Count - 1; i++)
            {
                var rise = _grid.GetSurfaceHeight(corridor[i].x, corridor[i].y) - _grid.GetSurfaceHeight(corridor[i - 1].x, corridor[i - 1].y);
                Assert.That(rise, Is.GreaterThanOrEqualTo(-Tolerance), $"corridor step {i} goes down");
                Assert.That(rise, Is.LessThanOrEqualTo(1f + Tolerance), $"corridor step {i} too high");
            }
        }

        [Test]
        public void AutoStepsAreRemovedOnceTheTargetIsReachable()
        {
            BuildTwoTiers();
            Spawn(3, 10);
            _map.Designate(16, 10, DesignationKind.Dig, 11f);
            Run(5f, () => _map.AutoCount > 0);
            Assert.That(_map.AutoCount, Is.EqualTo(1));
            Assert.That(_unit.HasRamp, Is.True);

            // The player opens a way up elsewhere before the unit gets there: a staircase at z = 3.
            SetHeight(9, 3, 9f);
            SetHeight(10, 3, 10f);
            SetHeight(11, 3, 11f);
            _unit.RequestRethink();
            _unit.Tick(TickSeconds);

            Assert.That(_unit.HasRamp, Is.False);
            Assert.That(_map.AutoCount, Is.EqualTo(0), "the leftover Auto step went");
        }

        [Test]
        public void CancellingAnAutoStepHoldsOffThatTargetForTheTimeout()
        {
            BuildTwoTiers();
            Spawn(3, 10);
            _map.Designate(16, 10, DesignationKind.Dig, 11f);
            Run(5f, () => _map.AutoCount > 0);
            var auto = FirstAuto();

            _map.Cancel(auto.x, auto.y);
            var held = Run(_unit.RampCancelSeconds - 1f, () => _map.AutoCount > 0);

            Assert.That(_map.AutoCount, Is.EqualTo(0), $"re-created after {held:0.0} s");
            Assert.That(_unit.IsRampSuppressed(16, 10), Is.True);
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Unreachable), _unit.Status);

            Run(3f, () => _map.AutoCount > 0);
            Assert.That(_map.AutoCount, Is.EqualTo(1), "back after the timeout");
        }

        [Test]
        public void DesignatingOverAnAutoStepCountsAsCancelling()
        {
            BuildTwoTiers();
            Spawn(3, 10);
            _map.Designate(16, 10, DesignationKind.Dig, 11f);
            Run(5f, () => _map.AutoCount > 0);
            var auto = FirstAuto();

            _map.Designate(auto.x, auto.y, DesignationKind.Dig, 8f);
            _unit.Tick(TickSeconds);

            Assert.That(_map.IsAuto(auto.x, auto.y), Is.False, "the player's now");
            Assert.That(_unit.IsRampSuppressed(16, 10), Is.True);
        }

        [Test]
        public void ACliffTooTallToCutIsReportedNotRamped()
        {
            // 4 m sheer: the high side is out of dig reach from below, so no ramp step can be cut.
            for (var z = 10; z <= 12; z++)
                for (var x = 10; x <= 12; x++)
                    SetHeight(x, z, 12f);
            Spawn(3, 3);
            _map.Designate(11, 11, DesignationKind.Dig, 8f);

            _unit.Tick(TickSeconds);

            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Unreachable));
            Assert.That(_map.AutoCount, Is.EqualTo(0));
            Assert.That(_unit.Status, Does.Contain("ramp blocked"));
        }

        [Test]
        public void AutoRampOffMakesNoAutoSteps()
        {
            BuildTwoTiers();
            Spawn(3, 10);
            _unit.AutoRamp = false;
            _map.Designate(16, 10, DesignationKind.Dig, 11f);

            Run(10f, () => false);

            Assert.That(_map.AutoCount, Is.EqualTo(0));
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Unreachable));
        }

        Vector2Int FirstAuto()
        {
            foreach (var cell in _map.ActiveCells)
                if (_map.IsAuto(cell % Size, cell / Size))
                    return new Vector2Int(cell % Size, cell / Size);
            Assert.Fail("no Auto designation");
            return default;
        }

        // --- benching ---------------------------------------------------------------------------

        [Test]
        public void TakesAFourTerraceMoundDownWithoutGettingStuck()
        {
            // Centre 12, rings at 11, 10, 9 on 8 m ground; everything above ground to 8.
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 12), Math.Abs(z - 12));
                    if (ring <= 3)
                        SetHeight(x, z, 12f - ring);
                }
            }

            Spawn(3, 12);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (_grid.GetSurfaceHeight(x, z) > 8f)
                        _map.Designate(x, z, DesignationKind.Dig, 8f);

            var unreachableFor = 0f;
            var longestUnreachable = 0f;
            var took = Run(1200f, () => _map.Count == 0, () =>
            {
                unreachableFor = _unit.State == CrewUnitState.Unreachable ? unreachableFor + TickSeconds : 0f;
                longestUnreachable = Mathf.Max(longestUnreachable, unreachableFor);
            });

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {_unit.Status} at {_unit.Cell} load {_unit.Inventory.Total} free {_unit.Inventory.Remaining}\n{Picture(12, 12, 11)}");
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (Math.Max(Math.Abs(x - 12), Math.Abs(z - 12)) <= 3)
                        Assert.That(_grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(8f + Tolerance), $"({x}, {z})");
            Assert.That(longestUnreachable, Is.LessThan(3f), "never stuck UNREACHABLE for more than a few seconds");
        }

        // --- dump zones -------------------------------------------------------------------------

        [Test]
        public void SpoilGoesIntoTheDumpZoneFirst()
        {
            // A 2 m dip marked as Dump Zone, and three cells to dig 2 m down: 7.5 m³ of loose dirt.
            for (var z = 3; z <= 5; z++)
            {
                for (var x = 3; x <= 5; x++)
                {
                    SetHeight(x, z, 6f);
                    _map.SetDumpZone(x, z, true);
                }
            }

            Spawn(10, 10);
            for (var x = 14; x <= 16; x++)
                _map.Designate(x, 14, DesignationKind.Dig, 6f);
            var before = Heights();

            var took = Run(600f, () => _map.Count == 0 && _unit.State == CrewUnitState.Idle);

            Assert.That(_map.Count, Is.EqualTo(0), $"after {took:0} s: {_unit.Status}");
            var after = Heights();
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var raised = after[z * Size + x] > before[z * Size + x] + Tolerance;
                    if (raised)
                        Assert.That(_map.IsDumpZone(x, z), Is.True, $"spoil landed outside the zone at ({x}, {z})");
                }
            }

            Assert.That(_unit.Inventory.Total, Is.LessThan(_grid.HeightStep), "idle with less than a step left: " + _unit.Status);
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Idle));
        }

        [Test]
        public void WithoutADumpZoneAPartLoadIsKept()
        {
            Spawn(10, 10);
            _map.Designate(14, 14, DesignationKind.Dig, 7f);

            Run(120f, () => _map.Count == 0 && _unit.State == CrewUnitState.Idle);

            Assert.That(_unit.Inventory.Total, Is.EqualTo(1.25f).Within(Tolerance), "one 1 m cut of dirt, bulked, kept");
        }

        /// <summary>Heights around (cx, cz), north row first; * marks a dig still designated, U the unit.</summary>
        string Picture(int cx, int cz, int radius)
        {
            var text = new System.Text.StringBuilder();
            for (var z = cz + radius; z >= cz - radius; z--)
            {
                for (var x = cx - radius; x <= cx + radius; x++)
                {
                    text.Append(_grid.GetSurfaceHeight(x, z).ToString("00"));
                    text.Append(_unit.Cell == new Vector2Int(x, z) ? 'U' : _map.GetKind(x, z) == DesignationKind.Dig ? '*' : ' ');
                    text.Append(' ');
                }

                text.AppendLine();
            }

            return text.ToString();
        }

        float[] Heights()
        {
            var heights = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    heights[z * Size + x] = _grid.GetSurfaceHeight(x, z);
            return heights;
        }
    }
}
