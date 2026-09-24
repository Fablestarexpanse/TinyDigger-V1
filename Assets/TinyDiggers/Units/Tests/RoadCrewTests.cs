using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The road machines (2026-09-24): a bulldozer grades a road once its earthworks are done, and
    /// a paver lays the final layer on what has been graded. Stand-ins for the units Ronan has
    /// planned, running on the ordinary crew code.
    /// </summary>
    public class RoadCrewTests
    {
        const int Size = 40;
        const float Ground = 5f;
        const float TickSeconds = 0.1f;

        TerrainGrid _grid;
        DesignationMap _map;
        JobDispatcher _dispatcher;
        RoadBuilder _roads;
        readonly List<CrewUnit> _units = new List<CrewUnit>();

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 0.5f, 0f, 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, 3f), new Layer(MaterialTable.Topsoil, Ground - 3f) });
            _map = new DesignationMap(_grid);
            _dispatcher = new JobDispatcher(_grid, _map, new GridPathfinder(_grid));
            _roads = new RoadBuilder(_grid, _map) { AutoGrade = false, AutoSurface = false };
            _dispatcher.Roads = _roads;

            // A level road across the middle, on ground already at its height: no earthworks.
            var chain = new List<RoadNode>
            {
                new RoadNode { Position = new Vector2(6f, 20.5f), Height = Ground },
                new RoadNode { Position = new Vector2(30f, 20.5f), Height = Ground },
            };
            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            var footprint = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            var exact = new Dictionary<int, float>();
            RoadPlanner.Footprint(_grid, samples, 3, footprint, bed, includeSettled: true, exact: exact);
            _roads.PlanRoad(1, footprint, bed, exact);
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

        CrewUnit Hire(UnitRole role, int x, int z)
        {
            var unit = new CrewUnit(_dispatcher, x, z, role, UnitLoads.Bed).AsMachine();
            _units.Add(unit);
            return unit;
        }

        void Run(float seconds)
        {
            for (var t = 0f; t < seconds; t += TickSeconds)
            {
                _dispatcher.Tick(TickSeconds);
                foreach (var unit in _units)
                    unit.Tick(TickSeconds);
                _roads.Tick();
            }
        }

        bool AllBed(System.Func<int, int, bool> test)
        {
            for (var x = 8; x <= 28; x++)
                if (!test(x, 20))
                    return false;
            return true;
        }

        [Test]
        public void ABulldozerGradesTheRoadAndAPaverSurfacesIt()
        {
            var dozer = Hire(UnitRole.Bulldozer, 18, 32);
            var paver = Hire(UnitRole.Paver, 22, 32);

            Run(120f);

            Assert.That(AllBed((x, z) => _roads.IsGraded(x, z)), Is.True, "the bulldozer went over the whole bed");
            Assert.That(AllBed((x, z) => _grid.GetTopMaterial(x, z) == MaterialTable.Road), Is.True, "and the paver after it");
            Assert.That(dozer.RoadCellsDone, Is.GreaterThan(20));
            Assert.That(paver.RoadCellsDone, Is.GreaterThan(20));
            Assert.That(_grid.GetTopMaterial(20, 26), Is.EqualTo(MaterialTable.Topsoil), "off the road nothing changed");
            Assert.That(_map.Count, Is.Zero, "the road machines make no designations");
        }

        [Test]
        public void APaverWithNoBulldozerHasNothingToDo()
        {
            var paver = Hire(UnitRole.Paver, 20, 32);

            Run(20f);

            Assert.That(paver.RoadCellsDone, Is.Zero);
            Assert.That(_grid.GetTopMaterial(20, 20), Is.EqualTo(MaterialTable.Topsoil), "nothing is surfaced before it is graded");
            Assert.That(paver.State, Is.EqualTo(CrewUnitState.Idle));
            Assert.That(paver.Status, Does.Contain("graded"));
        }

        [Test]
        public void TheRoadMachinesTakeTheirOwnRoomAndNeverDig()
        {
            var dozer = Hire(UnitRole.Bulldozer, 18, 32);
            var paver = Hire(UnitRole.Paver, 22, 32);

            // The bulldozer is the forge model now, 1.85 m blade to ripper; the paver is still a
            // stand-in the dumper's size (2026-09-24).
            Assert.That(dozer.Radius, Is.EqualTo(CrewUnit.DozerRadius));
            Assert.That(paver.Radius, Is.EqualTo(CrewUnit.HaulerRadius));
            Assert.That(dozer.Digs, Is.False);
            Assert.That(paver.Digs, Is.False);
            Assert.That(dozer.WorksRoads && paver.WorksRoads, Is.True);
        }
    }
}
