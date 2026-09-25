using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// How much one unit shifts, and where its time goes.
    ///
    /// This is the bench for deciding what a unit should carry (Ronan, 2026-09-22: "the best
    /// amount of material to dig and carry for each unit that will make gameplay fun but not too
    /// fast, and we can use as a metric to scale up when we add bigger units"). One unit, one
    /// column to dig, a tip a measured distance away, and a fixed stretch of game time — so the
    /// answer is **cubic metres a game minute at a given haul**, which is a number a new unit can
    /// be placed against without replaying the whole game.
    ///
    /// The split matters as much as the total. A unit that spends a twentieth of its life digging
    /// is a commuter, whatever it delivers, and the fix for that is the time a cut takes, not the
    /// size of the load. Both are printed.
    /// </summary>
    public class LoadSizingTests
    {
        const int Size = 48;
        const float TickSeconds = 0.05f;
        const float Ground = 8f;

        /// <summary>Game seconds each measurement runs for.</summary>
        const float Window = 600f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        CrewUnit _unit;
        CrewUnit _mate;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f,
                cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 6f),
                    });
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid) { MaxStepHeight = 1f, MaxSlopeDegrees = 45f };
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder) { Benching = true, AutoRamp = true };
        }

        [TearDown]
        public void TearDown()
        {
            _unit?.Dispose();
            _mate?.Dispose();
            _unit = null;
            _mate = null;
            _map.Dispose();
        }

        /// <summary>
        /// One unit, a face to work that never runs out within its reach, and a tip
        /// <paramref name="haulCells"/> away. Returns m³ a game minute and where the time went.
        /// </summary>
        (float perMinute, string split) Measure(UnitRole role, float capacity, bool machine, int haulCells)
        {
            var dig = new Vector2Int(14, 24);
            var tip = new Vector2Int(19 + haulCells, 24);

            _unit = new CrewUnit(_dispatcher, 10, 24, role, capacity);
            _unit.DigReachLevels = Mathf.RoundToInt(2f / _grid.HeightStep);
            _unit.CliffReachLevels = Mathf.RoundToInt(6f / _grid.HeightStep);
            _unit.Speed = machine ? 3f : 1.5f;
            _unit.WorkInterval = 0.4f;
            if (machine)
                _unit.AsMachine();

            // A strip three cells wide rather than a block: every cell of it is next to open
            // ground, so the unit always has a face it can reach and the measurement is of the
            // unit rather than of the site. A five by five block two metres deep read
            // "Unreachable 82%" — true of the block, and nothing to do with what a barrow holds.
            // The tip is wide for the same reason: a heap that walls itself in measures the heap.
            for (var z = dig.y - 10; z <= dig.y + 10; z++)
                for (var x = dig.x - 1; x <= dig.x + 1; x++)
                    _map.Designate(x, z, DesignationKind.Dig, Ground - 1f);
            for (var z = tip.y - 4; z <= tip.y + 4; z++)
                for (var x = tip.x - 4; x <= tip.x + 4; x++)
                    _map.SetDumpZone(x, z, true);

            var before = Standing(dig);
            var spent = new Dictionary<CrewUnitState, float>();
            var ran = 0f;
            while (ran < Window && _map.Count > 0)
            {
                _dispatcher.Tick(TickSeconds);
                _unit.Tick(TickSeconds);
                ran += TickSeconds;
                spent.TryGetValue(_unit.State, out var so_far);
                spent[_unit.State] = so_far + TickSeconds;
            }

            var moved = before - Standing(dig);
            var split = $"over {ran:0} s: ";
            var states = new List<KeyValuePair<CrewUnitState, float>>(spent);
            states.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var state in states)
                split += $"{state.Key} {100f * state.Value / ran:0}% ";
            return (moved * 60f / ran, split);
        }

        /// <summary>m³ still standing above the block's floor.</summary>
        float Standing(Vector2Int at)
        {
            var total = 0f;
            for (var z = at.y - 10; z <= at.y + 10; z++)
                for (var x = at.x - 1; x <= at.x + 1; x++)
                    total += Mathf.Max(0f, _grid.GetSurfaceHeight(x, z) - (Ground - 1f));
            return total * _grid.CellArea;
        }

        [TestCase(UnitRole.Worker, 6, "starter robot, barrow, short haul")]
        [TestCase(UnitRole.Worker, 20, "starter robot, barrow, long haul")]
        public void AStarterRobotShifts(UnitRole role, int haulCells, string what)
        {
            var (perMinute, split) = Measure(role, UnitLoads.Barrow, machine: false, haulCells);
            Debug.Log($"load sizing — {what} ({haulCells} cells, {haulCells * _grid.CellSize:0.#} m): "
                      + $"{perMinute:0.###} m³ a game minute, load {UnitLoads.Barrow:0.###} m³ "
                      + $"({UnitLoads.Barrow / (_grid.HeightStep * _grid.CellArea):0.#} cuts). {split}");
            Assert.That(perMinute, Is.GreaterThan(0f), "it should have shifted something. " + split);
        }

        /// <summary>
        /// The pair the digger mech is actually for: it stands at the face and loads a dumper
        /// parked beside it, and the dumper does all the walking. Measuring the mech on its own
        /// measures the one thing it is not meant to do, so this is the number that should set
        /// the ladder when bigger units arrive.
        /// </summary>
        [TestCase(6, "digger mech and dumper, short haul")]
        [TestCase(20, "digger mech and dumper, long haul")]
        public void ADiggerAndDumperShift(int haulCells, string what)
        {
            var dig = new Vector2Int(14, 24);
            var tip = new Vector2Int(19 + haulCells, 24);

            _unit = Machine(UnitRole.Digger, UnitLoads.Scoop, 10, 24);
            _mate = Machine(UnitRole.Hauler, UnitLoads.Bed, 10, 26);

            for (var z = dig.y - 10; z <= dig.y + 10; z++)
                for (var x = dig.x - 1; x <= dig.x + 1; x++)
                    _map.Designate(x, z, DesignationKind.Dig, Ground - 1f);
            for (var z = tip.y - 4; z <= tip.y + 4; z++)
                for (var x = tip.x - 4; x <= tip.x + 4; x++)
                    _map.SetDumpZone(x, z, true);

            var before = Standing(dig);
            var digging = 0f;
            var hauling = 0f;
            var ran = 0f;
            // Stop when the face runs out, not only when the clock does. A pair cleared the whole
            // strip inside the window, so dividing by the window quoted a rate that was really a
            // floor — the site had run out, not the unit.
            while (ran < Window && _map.Count > 0)
            {
                _dispatcher.Tick(TickSeconds);
                _unit.Tick(TickSeconds);
                _mate.Tick(TickSeconds);
                ran += TickSeconds;
                if (_unit.State == CrewUnitState.Digging)
                    digging += TickSeconds;
                if (_mate.State == CrewUnitState.Moving)
                    hauling += TickSeconds;
            }

            var perMinute = (before - Standing(dig)) * 60f / ran;
            Debug.Log($"load sizing — {what} ({haulCells} cells, {haulCells * _grid.CellSize:0.#} m): "
                      + $"{perMinute:0.###} m³ a game minute over {ran:0} s; the mech digs "
                      + $"{100f * digging / ran:0}% of the time, the dumper drives "
                      + $"{100f * hauling / ran:0}%. Mech: {_unit.Status}. Dumper: {_mate.Status}.");
            // A pair that ran the clock out never got going, and two machines jammed is a far
            // smaller case to read than four robots. Six ticks of both of them says whether they
            // are waiting on each other, on the ground, or on nothing at all.
            if (_map.Count > 0)
                for (var t = 0; t < 6; t++)
                {
                    _dispatcher.Tick(TickSeconds);
                    _unit.Tick(TickSeconds);
                    _mate.Tick(TickSeconds);
                    Debug.Log($"pair tick {t}: [mech {_unit.State} at ({_unit.Position.x:0.00}, "
                              + $"{_unit.Position.y:0.00}) cell ({_unit.Cell.x}, {_unit.Cell.y}) "
                              + $"load {_unit.Inventory.Total:0.###}/{UnitLoads.Scoop:0.###} "
                              + $":: {_unit.Status}] [dumper {_mate.State} at "
                              + $"({_mate.Position.x:0.00}, {_mate.Position.y:0.00}) cell "
                              + $"({_mate.Cell.x}, {_mate.Cell.y}) load "
                              + $"{_mate.Inventory.Total:0.###}/{UnitLoads.Bed:0.###} "
                              + $":: {_mate.Status}] apart "
                              + $"{(_unit.Position - _mate.Position).magnitude:0.00} cells, radii "
                              + $"{_unit.Radius:0.##} and {_mate.Radius:0.##}");
                }

            Assert.That(perMinute, Is.GreaterThan(0f),
                $"the pair should have shifted something. Mech: {_unit.Status}. Dumper: {_mate.Status}.");
        }

        CrewUnit Machine(UnitRole role, float capacity, int x, int z)
        {
            var unit = new CrewUnit(_dispatcher, x, z, role, capacity);
            unit.DigReachLevels = Mathf.RoundToInt(2f / _grid.HeightStep);
            unit.CliffReachLevels = Mathf.RoundToInt(6f / _grid.HeightStep);
            unit.Speed = 3f;
            unit.WorkInterval = 0.4f;
            unit.AsMachine();
            return unit;
        }

        [TestCase(UnitRole.Digger, 6, "digger mech, scoop, short haul")]
        [TestCase(UnitRole.Digger, 20, "digger mech, scoop, long haul")]
        public void ADiggerMechShifts(UnitRole role, int haulCells, string what)
        {
            var (perMinute, split) = Measure(role, UnitLoads.Scoop, machine: true, haulCells);
            Debug.Log($"load sizing — {what} ({haulCells} cells, {haulCells * _grid.CellSize:0.#} m): "
                      + $"{perMinute:0.###} m³ a game minute, load {UnitLoads.Scoop:0.###} m³ "
                      + $"({UnitLoads.Scoop / (_grid.HeightStep * _grid.CellArea):0.#} cuts). {split}");
            Assert.That(perMinute, Is.GreaterThan(0f), "it should have shifted something. " + split);
        }
    }
}
