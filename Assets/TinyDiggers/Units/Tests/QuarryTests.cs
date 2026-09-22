using System;
using NUnit.Framework;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Slice 17: the Quarry zone. Fill material has to come from somewhere (Ronan, 2026-09-22), so a
    /// Fill with nothing to feed it says so, and a marked quarry is dug — down to its floor and no
    /// further, and only while something is waiting to be filled.
    /// </summary>
    public class QuarryTests
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

        /// <summary>A hollow one step deep at (4, 4), marked to be filled back to the plain.</summary>
        void DigAHoleToFill()
        {
            SetHeight(4, 4, 7f);
            Assert.That(_map.Designate(4, 4, DesignationKind.Fill, 8f), Is.True);
        }

        /// <summary>A 4 x 4 quarry at the far side, floor 3 m down.</summary>
        void MarkQuarry(float floor = 5f)
        {
            for (var z = 16; z < 20; z++)
                for (var x = 16; x < 20; x++)
                    Assert.That(_map.SetQuarry(x, z, true, floor), Is.True);
        }

        // --- the map layer --------------------------------------------------------------------

        [Test]
        public void QuarryCellsAreTheirOwnLayer()
        {
            MarkQuarry();
            Assert.That(_map.QuarryCount, Is.EqualTo(16));
            Assert.That(_map.IsQuarry(17, 17), Is.True);
            Assert.That(_map.IsQuarry(4, 4), Is.False);
            Assert.That(_map.QuarryFloor(17, 17), Is.EqualTo(5f).Within(Tolerance));

            // 3 m of ground over the floor, over a cell of 1 m x 1 m.
            Assert.That(_map.QuarryLeft(17, 17), Is.EqualTo(3f * _grid.CellArea).Within(Tolerance));

            // A quarry is not work of its own: it does not show up as a designation.
            Assert.That(_map.Count, Is.EqualTo(0));
            Assert.That(_map.GetKind(17, 17), Is.EqualTo(DesignationKind.None));

            Assert.That(_map.SetQuarry(17, 17, false), Is.True);
            Assert.That(_map.QuarryCount, Is.EqualTo(15));
            Assert.That(_map.QuarryLeft(17, 17), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void ClearTakesTheQuarryOffToo()
        {
            MarkQuarry();
            Assert.That(_map.Cancel(17, 17), Is.True);
            Assert.That(_map.IsQuarry(17, 17), Is.False);

            _map.ClearAll();
            Assert.That(_map.QuarryCount, Is.EqualTo(0));
        }

        [Test]
        public void FillCountTracksFillDesignations()
        {
            Assert.That(_map.FillCount, Is.EqualTo(0));
            DigAHoleToFill();
            Assert.That(_map.FillCount, Is.EqualTo(1));

            // Re-designating the same cell as a Dig moves it out of the count.
            _map.Designate(4, 4, DesignationKind.Dig, 6f);
            Assert.That(_map.FillCount, Is.EqualTo(0));

            _map.Designate(4, 4, DesignationKind.Fill, 8f);
            Assert.That(_map.FillCount, Is.EqualTo(1));
            _map.Cancel(4, 4);
            Assert.That(_map.FillCount, Is.EqualTo(0));
        }

        // --- the crew -------------------------------------------------------------------------

        [Test]
        public void AFillWithNothingToFillItSaysSo()
        {
            DigAHoleToFill();
            var unit = Spawn(2, 2);

            Run(20f, () => unit.State == CrewUnitState.NeedsMaterial);

            Assert.That(unit.State, Is.EqualTo(CrewUnitState.NeedsMaterial));
            Assert.That(unit.Status, Does.Contain("Quarry"));
            Assert.That(_grid.GetSurfaceHeight(4, 4), Is.EqualTo(7f).Within(Tolerance), "nothing appeared from nowhere");
        }

        [Test]
        public void AQuarryFeedsTheFill()
        {
            DigAHoleToFill();
            MarkQuarry();
            var unit = Spawn(2, 2);

            var seconds = Run(240f, () => _grid.GetSurfaceHeight(4, 4) >= 8f - Tolerance
                && _map.GetKind(4, 4) == DesignationKind.None);

            // The last barrow may tip a whole step, so the hole can end a little proud of the plain.
            Assert.That(_grid.GetSurfaceHeight(4, 4), Is.GreaterThanOrEqualTo(8f - Tolerance), $"still short after {seconds:0.#} s");
            Assert.That(_map.GetKind(4, 4), Is.EqualTo(DesignationKind.None), "the Fill cleared itself");

            // The material came out of the quarry, not out of thin air.
            var dug = 0f;
            for (var z = 16; z < 20; z++)
                for (var x = 16; x < 20; x++)
                    dug += 8f - _grid.GetSurfaceHeight(x, z);
            Assert.That(dug, Is.GreaterThan(0.5f), "the quarry went down");
        }

        [Test]
        public void MarkingAQuarryWakesAStalledUnit()
        {
            // The play run of 2026-09-22 caught this: a unit in NeedsMaterial never re-planned, so
            // marking a quarry beside it changed nothing.
            DigAHoleToFill();
            var unit = Spawn(2, 2);
            Run(20f, () => unit.State == CrewUnitState.NeedsMaterial);
            Assert.That(unit.State, Is.EqualTo(CrewUnitState.NeedsMaterial), "it never stalled");

            MarkQuarry();
            Run(30f, () => unit.State != CrewUnitState.NeedsMaterial);
            Assert.That(unit.State, Is.Not.EqualTo(CrewUnitState.NeedsMaterial), "it slept through the new quarry");
        }

        [Test]
        public void TheQuarryIsNeverDugBelowItsFloor()
        {
            // A big Fill: more than the quarry can ever give.
            for (var z = 4; z < 10; z++)
                for (var x = 4; x < 10; x++)
                {
                    SetHeight(x, z, 4f);
                    _map.Designate(x, z, DesignationKind.Fill, 8f);
                }

            MarkQuarry(floor: 7f); // only 1 m a cell: 16 m³ against a hole of 144 m³
            var unit = Spawn(2, 2);

            Run(400f, () => unit.State == CrewUnitState.NeedsMaterial && _map.QuarryCount > 0
                && _grid.GetSurfaceHeight(17, 17) <= 7f + Tolerance);

            for (var z = 16; z < 20; z++)
                for (var x = 16; x < 20; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.GreaterThanOrEqualTo(7f - Tolerance),
                        $"({x}, {z}) was cut past the floor");
        }

        [Test]
        public void NothingIsQuarriedWhileNothingNeedsFilling()
        {
            MarkQuarry();
            var unit = Spawn(2, 2);

            Run(30f, () => false);

            Assert.That(_grid.GetSurfaceHeight(17, 17), Is.EqualTo(8f).Within(Tolerance), "the quarry was dug for nothing");
            Assert.That(unit.State, Is.EqualTo(CrewUnitState.Idle));
        }
    }
}
