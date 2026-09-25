using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Where a digger's time actually goes (Ronan, 2026-09-24: *"the excavators don't seem to dig
    /// consistently, there is a lot of stopping, doing nothing, taking a scoop, waiting"*).
    ///
    /// <see cref="LoadSizingTests"/> already measures what a pair shifts, but it only counts the
    /// digging and the driving, so a digger that spends half its life idle and half of that waiting
    /// looks the same as one that spends all of it waiting. These count every state the digger and
    /// its dumper are in, and how often the digger fills up with nowhere to put it — so the stall
    /// is named before anything is changed to fix it.
    /// </summary>
    public class DiggerDutyTests
    {
        const int Size = 64;
        const float TickSeconds = 0.05f;
        const float Ground = 8f;
        const float Window = 600f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        readonly List<CrewUnit> _units = new List<CrewUnit>();

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f, cellSize: 0.5f);
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
            foreach (var unit in _units)
                unit.Dispose();
            _units.Clear();
            _map.Dispose();
        }

        CrewUnit Machine(UnitRole role, float capacity, int x, int z)
        {
            var unit = new CrewUnit(_dispatcher, x, z, role, capacity)
            {
                DigReachLevels = Mathf.RoundToInt(2f / _grid.HeightStep),
                CliffReachLevels = Mathf.RoundToInt(6f / _grid.HeightStep),
                Speed = 3f,
                WorkInterval = 0.4f,
            };
            unit.AsMachine();
            _units.Add(unit);
            return unit;
        }

        /// <summary>A face the digger can always reach, and a tip wide enough not to wall itself in.</summary>
        void Site(Vector2Int dig, Vector2Int tip)
        {
            for (var z = dig.y - 10; z <= dig.y + 10; z++)
                for (var x = dig.x - 1; x <= dig.x + 1; x++)
                    _map.Designate(x, z, DesignationKind.Dig, Ground - 1f);
            for (var z = tip.y - 4; z <= tip.y + 4; z++)
                for (var x = tip.x - 4; x <= tip.x + 4; x++)
                    _map.SetDumpZone(x, z, true);
        }

        static string Split(Dictionary<CrewUnitState, float> spent, float ran)
        {
            var states = new List<KeyValuePair<CrewUnitState, float>>(spent);
            states.Sort((a, b) => b.Value.CompareTo(a.Value));
            var text = "";
            foreach (var state in states)
                text += $"{state.Key} {100f * state.Value / ran:0}%  ";
            return text;
        }

        /// <summary>Runs the site and reports where every unit's time went.</summary>
        (float ran, Dictionary<CrewUnit, Dictionary<CrewUnitState, float>> spent) Run()
        {
            var spent = new Dictionary<CrewUnit, Dictionary<CrewUnitState, float>>();
            foreach (var unit in _units)
                spent[unit] = new Dictionary<CrewUnitState, float>();

            var ran = 0f;
            while (ran < Window && _map.Count > 0)
            {
                _dispatcher.Tick(TickSeconds);
                foreach (var unit in _units)
                    unit.Tick(TickSeconds);
                ran += TickSeconds;
                foreach (var unit in _units)
                {
                    spent[unit].TryGetValue(unit.State, out var so_far);
                    spent[unit][unit.State] = so_far + TickSeconds;
                }
            }

            return (ran, spent);
        }

        float Standing(Vector2Int at)
        {
            var total = 0f;
            for (var z = at.y - 10; z <= at.y + 10; z++)
                for (var x = at.x - 1; x <= at.x + 1; x++)
                    total += Mathf.Max(0f, _grid.GetSurfaceHeight(x, z) - (Ground - 1f));
            return total * _grid.CellArea;
        }

        [TestCase(1, 6, "one dumper, short haul")]
        [TestCase(1, 20, "one dumper, long haul")]
        [TestCase(2, 6, "two dumpers, short haul")]
        [TestCase(2, 20, "two dumpers, long haul")]
        [TestCase(3, 20, "three dumpers, long haul")]
        public void WhereADiggersTimeGoes(int dumpers, int haulCells, string what)
        {
            var dig = new Vector2Int(20, 30);
            var tip = new Vector2Int(25 + haulCells, 30);
            Site(dig, tip);

            var digger = Machine(UnitRole.Digger, UnitLoads.Scoop, 16, 30);
            for (var i = 0; i < dumpers; i++)
                Machine(UnitRole.Hauler, UnitLoads.Bed, 16, 32 + i * 2);

            var before = Standing(dig);
            var (ran, spent) = Run();
            var perMinute = (before - Standing(dig)) * 60f / ran;

            var text = $"digger duty — {what} ({haulCells} cells): {perMinute:0.###} m³ a game minute over {ran:0} s\n"
                       + $"  digger:  {Split(spent[digger], ran)}(filled up with no dumper {digger.HaulerWaits} times)\n";
            for (var i = 1; i < _units.Count; i++)
                text += $"  dumper {i}: {Split(spent[_units[i]], ran)}\n";
            text += $"  digger says: {digger.Status}\n";
            foreach (var unit in _units)
                text += $"  {unit.Role} {unit.Id} ended at ({unit.Position.x:0.0}, {unit.Position.y:0.0}) "
                        + $"{unit.State}, load {unit.Inventory.Total:0.##}, repaths {unit.RepathCount}, "
                        + $"serves digger {_dispatcher.DiggerFor(unit.Id)}, that digger's queue "
                        + $"{_dispatcher.HaulerCountFor(unit.Role == UnitRole.Digger ? unit.Id : _dispatcher.DiggerFor(unit.Id))}"
                        + $" :: {unit.Status}\n";
            Debug.Log(text);

            Assert.That(perMinute, Is.GreaterThan(0f), "the pair shifted something. " + digger.Status);
        }
    }
}

