using System;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    public class CrewUnitTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 21;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        CrewUnit _unit;

        [SetUp]
        public void SetUp()
        {
            // Flat ground at 8 m: 2 m bedrock under 6 m of dirt.
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

        /// <summary>Ticks until <paramref name="done"/> or the time runs out. Returns the seconds taken.</summary>
        float Run(float maxSeconds, Func<bool> done)
        {
            var elapsed = 0f;
            while (elapsed < maxSeconds && !done())
            {
                _unit.Tick(TickSeconds);
                elapsed += TickSeconds;
            }

            return elapsed;
        }

        // --- dig reach ----------------------------------------------------------------------------

        [Test]
        public void DigReachRejectsACellThreeLevelsUp()
        {
            Spawn(2, 2);
            SetHeight(10, 10, 11f);

            Assert.That(_unit.CanWork(9, 10, 10, 10), Is.False, "3 levels up");

            SetHeight(10, 10, 10f);
            Assert.That(_unit.CanWork(9, 10, 10, 10), Is.True, "2 levels up");

            SetHeight(10, 10, 5f);
            Assert.That(_unit.CanWork(9, 10, 10, 10), Is.False, "3 levels down");
        }

        [Test]
        public void OnlyAdjacentCellsCanBeWorked()
        {
            Spawn(2, 2);

            Assert.That(_unit.CanWork(9, 10, 11, 10), Is.False);
            Assert.That(_unit.CanWork(9, 10, 9, 10), Is.False);
            Assert.That(_unit.CanWork(9, 10, 10, 11), Is.True, "diagonal neighbours count");
        }

        // --- the job loop -------------------------------------------------------------------------

        [Test]
        public void FinishesATwoCellDigToHeightAndAMatchingFill()
        {
            Spawn(3, 10);
            _map.Designate(10, 10, DesignationKind.Dig, 7f);
            _map.Designate(11, 10, DesignationKind.Dig, 7f);
            _map.Designate(10, 4, DesignationKind.Fill, 9f);
            _map.Designate(11, 4, DesignationKind.Fill, 9f);

            var took = Run(120f, () => _map.Count == 0 && _unit.State == CrewUnitState.Idle);

            Assert.That(_map.Count, Is.EqualTo(0), $"designations left after {took:0.0} s; unit: {_unit.Status}");
            Assert.That(_grid.GetSurfaceHeight(10, 10), Is.EqualTo(7f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(11, 10), Is.EqualTo(7f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(10, 4), Is.EqualTo(9f).Within(Tolerance));
            Assert.That(_grid.GetSurfaceHeight(11, 4), Is.EqualTo(9f).Within(Tolerance));
            // Two 1 m cuts of dirt bulk to 2.5 m³; two 1 m fills use 2; half a step stays in the load.
            Assert.That(_unit.Inventory.Total, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Idle));
        }

        [Test]
        public void TheUnitWalksThereRatherThanWorkingFromAfar()
        {
            Spawn(3, 10);
            _map.Designate(15, 10, DesignationKind.Dig, 7f);

            _unit.Tick(TickSeconds);

            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Moving));
            Assert.That(_unit.JobTarget, Is.EqualTo(new Vector2Int(15, 10)));
            Assert.That(_grid.GetSurfaceHeight(15, 10), Is.EqualTo(8f), "nothing dug yet");

            Run(30f, () => _map.Count == 0);
            var stand = _unit.JobStand;
            Assert.That(Mathf.Abs(stand.x - 15) <= 1 && Mathf.Abs(stand.y - 10) <= 1, Is.True, "it dug from next door");
            Assert.That(_unit.Cell, Is.EqualTo(stand));
        }

        [Test]
        public void NeverStallsWithAFullLoad()
        {
            // Six 1 m cuts of dirt is 7.5 m³ loose: more than one 5 m³ load, and nowhere designated
            // to put it. The unit has to dump somewhere and come back.
            Spawn(3, 10);
            for (var x = 10; x < 16; x++)
                _map.Designate(x, 10, DesignationKind.Dig, 7f);

            var took = Run(240f, () => _map.Count == 0);

            Assert.That(_map.Count, Is.EqualTo(0), $"stalled after {took:0.0} s; unit: {_unit.Status}");
            for (var x = 10; x < 16; x++)
                Assert.That(_grid.GetSurfaceHeight(x, 10), Is.EqualTo(7f).Within(Tolerance));
        }

        [Test]
        public void TheLoadNeverOverflows()
        {
            Spawn(3, 10);
            for (var x = 10; x < 16; x++)
                _map.Designate(x, 10, DesignationKind.Dig, 6f);

            var worst = 0f;
            Run(240f, () =>
            {
                worst = Mathf.Max(worst, _unit.Inventory.Total);
                return _map.Count == 0;
            });

            Assert.That(worst, Is.LessThanOrEqualTo(_unit.Inventory.Capacity + Tolerance));
        }

        // --- reach and reachability ------------------------------------------------------------

        [Test]
        public void SaysUnreachableForADesignationItCannotWorkFromAnywhere()
        {
            // A 3x3 plateau 4 m above the plain: its middle can only be worked from the plateau,
            // which a 1 m step limit cannot climb onto, and its edge cells are 4 levels up from
            // the plain, beyond dig reach.
            for (var z = 10; z <= 12; z++)
                for (var x = 10; x <= 12; x++)
                    SetHeight(x, z, 12f);
            Spawn(3, 3);
            _map.Designate(11, 11, DesignationKind.Dig, 8f);

            _unit.Tick(TickSeconds);

            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Unreachable));
            Assert.That(_unit.Status, Does.StartWith("UNREACHABLE"));
            Assert.That(_unit.Status, Does.Contain("(11, 11)"));
            Assert.That(_unit.UnreachableCount, Is.EqualTo(1));
        }

        [Test]
        public void ReportsUnreachableDesignationsWhileBusyWithReachableOnes()
        {
            for (var z = 10; z <= 12; z++)
                for (var x = 10; x <= 12; x++)
                    SetHeight(x, z, 12f);
            Spawn(3, 3);
            _map.Designate(11, 11, DesignationKind.Dig, 8f);
            _map.Designate(5, 5, DesignationKind.Dig, 7f);

            _unit.Tick(TickSeconds);

            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Moving));
            Assert.That(_unit.JobTarget, Is.EqualTo(new Vector2Int(5, 5)));
            Assert.That(_unit.UnreachableCount, Is.EqualTo(1));
            Assert.That(_unit.NearestUnreachable, Does.Contain("(11, 11)"));
        }

        [Test]
        public void WorksADigAreaFromOutsideNeverFromInside()
        {
            // A 3x3 block to dig: the unit starts in the middle of it, but must stand outside.
            for (var z = 9; z <= 11; z++)
                for (var x = 9; x <= 11; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 7f);
            Spawn(10, 10);

            var digSteps = 0;
            Run(120f, () =>
            {
                if (_unit.State == CrewUnitState.Digging)
                {
                    digSteps++;
                    var stand = _unit.JobStand;
                    // Standing on a finished (no longer designated) cell is fine; on one still to dig is not.
                    Assert.That(_map.GetKind(stand.x, stand.y), Is.Not.EqualTo(DesignationKind.Dig),
                        $"worked from {stand} while it was still designated");
                }

                return _map.Count == 0;
            });

            Assert.That(_map.Count, Is.EqualTo(0), _unit.Status);
            Assert.That(digSteps, Is.GreaterThan(0));
        }

        [Test]
        public void WithoutAutoRampUpperTerracesAreUnreachableUntilAStepIsLeft()
        {
            // A 4-terrace mound, one cell per terrace: centre 12, then rings at 11, 10, 9 on 8 m ground.
            BuildMound(10, 10, 8f);
            Spawn(2, 10);
            _unit.AutoRamp = false;
            _unit.Benching = false;
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (_grid.GetSurfaceHeight(x, z) > 8f)
                        _map.Designate(x, z, DesignationKind.Dig, 8f);

            Run(600f, () => _unit.State == CrewUnitState.Unreachable || _map.Count == 0);

            // The two lower terraces are dug; the top two stand 3-4 m above ground now and cannot
            // be worked from it.
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Unreachable), _unit.Status);
            Assert.That(_grid.GetSurfaceHeight(10, 7), Is.EqualTo(8f).Within(Tolerance), "first terrace dug");
            Assert.That(_grid.GetSurfaceHeight(10, 8), Is.EqualTo(8f).Within(Tolerance), "second terrace dug");
            Assert.That(_grid.GetSurfaceHeight(10, 9), Is.EqualTo(11f).Within(Tolerance), "third terrace out of reach");
            Assert.That(_grid.GetSurfaceHeight(10, 10), Is.EqualTo(12f).Within(Tolerance), "top untouched");
            Assert.That(_unit.UnreachableCount, Is.GreaterThan(0));
        }

        /// <summary>Concentric one-cell terraces up to 4 m above <paramref name="ground"/>.</summary>
        internal void BuildMound(int cx, int cz, float ground)
        {
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var ring = Math.Max(Math.Abs(x - cx), Math.Abs(z - cz));
                    if (ring <= 3)
                        SetHeight(x, z, ground + 4 - ring);
                }
            }
        }

        [Test]
        public void ReplansWhenACellOnItsPathChanges()
        {
            Spawn(1, 10);
            _map.Designate(19, 10, DesignationKind.Dig, 7f);
            Run(0.5f, () => false);
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Moving));
            Assert.That(_unit.Path, Has.Member(new Vector2Int(10, 10)), "straight along the row");

            // A 3 m block lands on the route.
            SetHeight(10, 10, 11f);
            _unit.Tick(TickSeconds);

            Assert.That(_unit.RepathCount, Is.EqualTo(1));
            Assert.That(_unit.Path, Has.No.Member(new Vector2Int(10, 10)));
            Run(60f, () => _map.Count == 0);
            Assert.That(_map.Count, Is.EqualTo(0), _unit.Status);
        }

        [Test]
        public void DoesNotReplanForChangesOffItsPath()
        {
            Spawn(1, 10);
            _map.Designate(19, 10, DesignationKind.Dig, 7f);
            Run(0.5f, () => false);

            SetHeight(10, 3, 11f);
            _unit.Tick(TickSeconds);

            Assert.That(_unit.RepathCount, Is.EqualTo(0));
        }

        [Test]
        public void IdleWithNothingDesignated()
        {
            Spawn(3, 3);

            _unit.Tick(TickSeconds);

            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Idle));
            Assert.That(_unit.Status, Does.Contain("nothing designated"));
        }

        [Test]
        public void TheBodyRidesTheSurface()
        {
            Spawn(3, 10);
            _map.Designate(15, 10, DesignationKind.Dig, 7f);
            for (var x = 6; x <= 9; x++)
                SetHeight(x, 10, 9f);

            Run(1.5f, () => false);

            var ground = TerrainSurface.SampleHeight(_grid, _unit.Position.x, _unit.Position.y);
            Assert.That(Mathf.Abs(_unit.Height - ground), Is.LessThan(0.6f), "smoothed, but close");
        }
    }
}
