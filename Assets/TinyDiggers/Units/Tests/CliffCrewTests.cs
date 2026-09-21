using System;
using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// What a cliff means to the crew: a wall they cannot climb, and a hill they can still take
    /// down from the bottom, benching their way up it without being told how.
    /// </summary>
    public class CliffCrewTests
    {
        const int Size = 28;
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
                    SetRock(x, z, 8f);
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

        /// <summary>Rock all the way to the surface: a cliff is only a cliff if it is stone.</summary>
        void SetRock(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f),
                new Layer(MaterialTable.Rock, Mathf.Max(0.1f, height - 2f)),
            });

        CrewUnit Spawn(int x, int z, UnitRole role = UnitRole.Digger, float capacity = 5f)
        {
            var unit = new CrewUnit(_dispatcher, x, z, role, capacity);
            _units.Add(unit);
            return unit;
        }

        float Run(float maxSeconds, Func<bool> done)
        {
            var elapsed = 0f;
            while (elapsed < maxSeconds && !done())
            {
                _dispatcher.Tick(TickSeconds);
                foreach (var unit in _units)
                    unit.Tick(TickSeconds);
                _slump?.RunUntilStable();
                elapsed += TickSeconds;
            }

            return elapsed;
        }

        /// <summary>A block of rock standing <paramref name="height"/> m above the plain.</summary>
        void RaiseCliff(int fromX, int toX, float height)
        {
            for (var z = 0; z < Size; z++)
                for (var x = fromX; x <= toX; x++)
                    SetRock(x, z, height);
        }

        [Test]
        public void ACliffIsAWallToTheCrew()
        {
            RaiseCliff(14, Size - 1, 14f); // A six-metre face.

            Assert.That(_grid.GetSurfaceHeight(13, 10), Is.EqualTo(8f).Within(1e-3f));
            Assert.That(_grid.GetSurfaceHeight(14, 10), Is.EqualTo(14f).Within(1e-3f));

            var path = new List<Vector2Int>();
            Assert.That(_pathfinder.TryFindPath(4, 10, 20, 10, path), Is.False,
                "nothing should be able to walk up a six-metre rock face");

            var reachable = new bool[Size * Size];
            _pathfinder.FloodReachable(4, 10, reachable);
            Assert.That(reachable[10 * Size + 20], Is.False, "the top of the cliff is not reachable from the plain");
            Assert.That(reachable[10 * Size + 13], Is.True, "the foot of it is");
        }

        [Test]
        public void ACrewTakesACliffedHillDownFromTheBottom()
        {
            RaiseCliff(14, Size - 1, 14f);
            _slump = new AngleOfReposeSimulator(_grid);

            // Take a bite out of the cliff, back to the level of the plain. Nobody is told how to
            // get up it: benching and the auto ramp have to work that out. Kept to a few hundred
            // cubic metres so the test is about whether the crew can do it at all, not about how
            // long two diggers and a hauler take to shift a mountain.
            for (var z = 9; z < 14; z++)
                for (var x = 14; x < 18; x++)
                    _map.Designate(x, z, DesignationKind.Dig, 8f);

            for (var z = 8; z < 16; z++)
                for (var x = 4; x < 9; x++)
                    _map.SetDumpZone(x, z, true, 14f);

            Spawn(11, 10);
            Spawn(11, 12);
            Spawn(9, 11, UnitRole.Hauler, capacity: 20f);
            // Generous on purpose: the crew has to bench its own way up a six-metre rock face
            // before it can start, and the point of the test is whether it can at all.
            var seconds = Run(6000f, () => _map.Count == 0);

            Assert.That(_map.Count, Is.Zero,
                $"after {seconds:0} s there are still {_map.Count} designations: {Describe()}");
            for (var z = 9; z < 14; z++)
                for (var x = 14; x < 18; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.LessThanOrEqualTo(8f + 1e-3f),
                        $"({x}, {z}) is still standing at {_grid.GetSurfaceHeight(x, z)} m");
        }

        string Describe()
        {
            var text = "";
            foreach (var unit in _units)
                text += $"{unit.Id} {unit.State} {unit.Status} | ";
            return text;
        }
    }
}
