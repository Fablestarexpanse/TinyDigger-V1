using System;
using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Rivers always block the crew (Ronan, 2026-09-22), "they will have to fill them in or dig
    /// them out". A river is crossed by tipping spoil into it until the crossing stands above the
    /// water, from the bank, and then walking over.
    /// </summary>
    public class RiverCrossingTests
    {
        const float Half = 0.5f;
        const int Size = 24;
        const float Ground = 6f;
        const float Bed = 5f;
        const float RiverSurface = 5.3f; // 0.3 m: shallow, but a river, so it blocks.
        const int RiverFrom = 11;
        const int RiverTo = 12;
        const float TickSeconds = 0.05f;
        const float Tolerance = 1e-3f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        readonly List<CrewUnit> _units = new List<CrewUnit>();

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), Half, 0f, Half);
            var riverBeds = new bool[Size * Size];
            var surfaces = new float[Size * Size];
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var river = x >= RiverFrom && x <= RiverTo;
                    SetHeight(x, z, river ? Bed : Ground);
                    riverBeds[z * Size + x] = river;
                    surfaces[z * Size + x] = river ? RiverSurface : float.NegativeInfinity;
                }
            }

            _grid.SetRiverBeds(riverBeds);
            _grid.SetWaterSurfaces(surfaces);
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

        [Test]
        public void TheCrewCannotCrossAShallowRiverUntilItIsFilledIn()
        {
            var path = new List<Vector2Int>();
            Assert.That(_grid.IsWater(RiverFrom, 10), Is.True, "0.3 m of river is still river");
            Assert.That(_pathfinder.TryFindPath(4, 10, 20, 10, path), Is.False, "nothing crosses it before it is filled");
        }

        [Test]
        public void AWorkerFillsACrossingFromTheBankAndThenWalksOverIt()
        {
            // A crossing three rows wide, to be built up level with the banks.
            for (var z = 9; z <= 11; z++)
                for (var x = RiverFrom; x <= RiverTo; x++)
                    Assert.That(_map.Designate(x, z, DesignationKind.Fill, Ground), Is.True, "a river takes a fill");

            var worker = new CrewUnit(_dispatcher, 4, 10, UnitRole.Worker, 0.19f) { Speed = 1.5f };
            _units.Add(worker);
            worker.Inventory.Add(MaterialTable.DirtLoose, worker.Inventory.Remaining);

            var took = Run(600f, () => _map.Count == 0, () =>
            {
                // The player keeps the barrow full: a digger would bring the spoil in a real crew.
                // Topped up whenever less than a whole step (0.125 m³) is left, which is what a
                // tip needs; a part-step rides on in the barrow.
                if (worker.Inventory.Total < Half * _grid.CellArea)
                    worker.Inventory.Add(MaterialTable.DirtLoose, worker.Inventory.Remaining);
            });

            Assert.That(_map.Count, Is.Zero, $"after {took:0} s: {worker.Status}");
            for (var z = 9; z <= 11; z++)
                for (var x = RiverFrom; x <= RiverTo; x++)
                {
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.GreaterThanOrEqualTo(Ground - Tolerance), $"({x}, {z}) left low");
                    Assert.That(_grid.IsWater(x, z), Is.False, $"({x}, {z}) still river");
                }

            var path = new List<Vector2Int>();
            Assert.That(_pathfinder.TryFindPath(4, 10, 20, 10, path), Is.True, "the crossing can be walked");
            Assert.That(path.TrueForAll(c => c.y >= 9 && c.y <= 11 || c.x < RiverFrom || c.x > RiverTo), Is.True,
                "over the filled crossing, nowhere else");
            Assert.That(_grid.IsWater(RiverFrom, 2), Is.True, "the rest of the river still blocks");
        }
    }
}
