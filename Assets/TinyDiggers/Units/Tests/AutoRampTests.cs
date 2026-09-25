using System;
using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
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
        AngleOfReposeSimulator _slump;

        [SetUp]
        public void SetUp()
        {
            // Flat ground at 8 m: 2 m bedrock under dirt.
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 1f);
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
            _slump?.Dispose();
            _map.Dispose();
        }

        void SetHeight(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f) });

        CrewUnit Spawn(int x, int z) => _unit = new CrewUnit(_grid, _map, _pathfinder, x, z);

        /// <summary>Lets tipped material settle, as it does in play.</summary>
        void WithSlump() => _slump = new AngleOfReposeSimulator(_grid);

        float Run(float maxSeconds, Func<bool> done, Action eachTick = null)
        {
            var elapsed = 0f;
            while (elapsed < maxSeconds && !done())
            {
                _unit.Tick(TickSeconds);
                _slump?.RunUntilStable();
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

            // Dig a cell deep in the west half, deep enough that nothing can step into it any more
            // so the labels really do have to move: only the west half is re-labelled.
            SetHeight(4, 4, 4f);
            regions.Update();

            Assert.That(regions.CellsRelabelledLast, Is.LessThanOrEqualTo(west + 2), "the east half was left alone");
            Assert.That(regions.CellsRelabelledLast, Is.GreaterThan(0));
            Assert.That(regions.CanReach(2, 2, 20, 20), Is.False);
            Assert.That(regions.RegionSize(20, 20), Is.EqualTo(east));
            regions.Dispose();
        }

        [Test]
        public void ADigThatJoinsNothingUpDifferentlyRelabelsNothing()
        {
            // The whole cost of digging was here: relabelling is incremental in dirty cells and not
            // at all in the size of a region, and the crew work on a landmass that is one region of
            // most of the island — so one scoop cost 140.8 ms and relabelled 794,803 cells
            // (2026-09-23). Taking a step off a cell leaves every neighbour as reachable as it was,
            // and the region map now asks that before it does any work.
            var regions = new RegionMap(_grid, _pathfinder);
            regions.Update();
            var rebuilds = regions.RebuildCount;

            // A step down, well within what a unit can climb: nothing joins up differently.
            SetHeight(4, 4, 7f);
            regions.Update();

            Assert.That(regions.RebuildCount, Is.EqualTo(rebuilds), "nothing needed rebuilding");
            Assert.That(regions.CanReach(2, 2, 20, 20), Is.True, "and the answers are still right");
            Assert.That(regions.RegionOf(4, 4), Is.EqualTo(regions.RegionOf(2, 2)));

            // A cut too deep to step into has to be noticed.
            SetHeight(4, 4, 3f);
            regions.Update();
            Assert.That(regions.RebuildCount, Is.GreaterThan(rebuilds), "a real change still rebuilds");
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
            for (var z = 2; z <= 4; z++)
                for (var x = 2; x <= 4; x++)
                    _map.SetDumpZone(x, z, true, 12f);
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
            // The corridor ends where the unit would stand to work the cell, which is the cell
            // itself only when there is nowhere beside it to stand from (2026-09-22: a ramp aimed
            // at the work has nothing to cut when the work stands level with the ground outside,
            // as it does in the middle of a pit).
            var end = corridor[corridor.Count - 1];
            Assert.That(Mathf.Max(Mathf.Abs(end.x - 16), Mathf.Abs(end.y - 10)), Is.LessThanOrEqualTo(1),
                $"the corridor should end at (16, 10) or beside it, not at ({end.x}, {end.y})");
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
        public void ARampDroppedBecauseItIsDoneIsNotTreatedAsCancelled()
        {
            // Ending a ramp clears its Auto step, and clearing an Auto step is how the player
            // cancels one — so ending a ramp normally looked to the dispatcher exactly like the
            // player cancelling it: the target got held off for the cancel timeout and every unit
            // was told to think again. That is the "path 1/8 RETHINK" the deep pit sat in
            // (2026-09-22).
            BuildTwoTiers();
            Spawn(3, 10);
            _map.Designate(16, 10, DesignationKind.Dig, 11f);
            Run(5f, () => _map.AutoCount > 0);
            Assert.That(_unit.HasRamp, Is.True, "a ramp was wanted in the first place");

            // A way up appears elsewhere, so the ramp is no longer needed and is dropped.
            SetHeight(9, 3, 9f);
            SetHeight(10, 3, 10f);
            SetHeight(11, 3, 11f);
            _unit.RequestRethink();
            _unit.Tick(TickSeconds);

            Assert.That(_unit.HasRamp, Is.False, "the ramp went, as it should");
            Assert.That(_unit.IsRampSuppressed(16, 10), Is.False,
                "and the target is not held off: nobody cancelled anything");
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
            WithSlump();
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
            // Somewhere for the spoil: nothing may be tipped on open ground.
            for (var z = 1; z <= 8; z++)
                for (var x = 1; x <= 8; x++)
                    _map.SetDumpZone(x, z, true, 16f);
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
                for (var x = 12; x < Size; x++)
                    if (Math.Max(Math.Abs(x - 12), Math.Abs(z - 12)) <= 3)
                        Assert.That(_grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(8f + Tolerance),
                            $"({x}, {z}) — the half of the mound away from the Dump Zone and its overspill");
            Assert.That(longestUnreachable, Is.LessThan(3f), "never stuck UNREACHABLE for more than a few seconds");
        }

        // --- dump zones -------------------------------------------------------------------------

        [Test]
        public void SpoilGoesIntoTheDumpZoneFirst()
        {
            WithSlump();
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
                    if (after[z * Size + x] <= before[z * Size + x] + Tolerance)
                        continue;
                    // Tipped into the zone, or slumped from it onto the cells next door.
                    var nearZone = false;
                    for (var dz = -1; dz <= 1 && !nearZone; dz++)
                        for (var dx = -1; dx <= 1 && !nearZone; dx++)
                            nearZone = _grid.InBounds(x + dx, z + dz) && _map.IsDumpZone(x + dx, z + dz);
                    Assert.That(nearZone, Is.True, $"spoil landed away from the zone at ({x}, {z})");
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
