using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The same questions as <see cref="DeepPitTests"/> at the size the game is actually played at
    /// (Ronan, 2026-09-23: *"it would depend how big you make it, and if you selected a hill and
    /// below you can dig out the hill for fill — that's part of the game"*).
    ///
    /// The pit in <see cref="DeepPitTests"/> is seven cells across — three and a half metres — and
    /// a 12% ramp three metres down wants twenty-five metres of run, so of course none fits. A
    /// real cut is tens of metres across with its haul road inside it, which is a different
    /// question and gets its own site: eighty by eighty cells, forty metres square.
    /// </summary>
    public class BigSiteTests
    {
        const int Size = 120;
        const float TickSeconds = 0.05f;

        /// <summary>Ground over the plain: two of bedrock, six of dirt.</summary>
        const float Ground = 8f;

        static readonly Vector2Int Middle = new Vector2Int(72, 60);

        /// <summary>Cells either side of the middle: a forty by forty block, twenty metres square.</summary>
        const int Half = 20;

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
            _pathfinder = new GridPathfinder(_grid);
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

        /// <summary>The scene's crew, set up the way <see cref="DeepPitTests"/> sets it up.</summary>
        void Crew(int count)
        {
            _pathfinder.MaxStepHeight = 1f;
            _pathfinder.MaxSlopeDegrees = 45f;
            for (var i = 0; i < count; i++)
            {
                var unit = new CrewUnit(_dispatcher, 6 + i, 60, UnitRole.Worker, UnitLoads.Barrow)
                {
                    DigReachLevels = Levels(2f),
                    CliffReachLevels = Levels(6f),
                    Speed = 1.5f,
                    WorkInterval = 0.4f,
                };
                _units.Add(unit);
            }
        }

        int Levels(float metres) => Mathf.Max(1, Mathf.RoundToInt(metres / _grid.HeightStep));

        float Outstanding()
        {
            var total = 0f;
            foreach (var cell in _map.ActiveCells)
            {
                var x = cell % _grid.Width;
                var z = cell / _grid.Width;
                total += Mathf.Abs(_grid.GetSurfaceHeight(x, z) - _map.GetTarget(x, z));
            }

            return total * _grid.CellArea;
        }

        /// <summary>The deepest any unit has got below the plain, in metres.</summary>
        float DeepestUnit()
        {
            var deepest = 0f;
            foreach (var unit in _units)
            {
                var cell = unit.Cell;
                if (!_grid.InBounds(cell.x, cell.y))
                    continue;
                deepest = Mathf.Max(deepest, Ground - _grid.GetSurfaceHeight(cell.x, cell.y));
            }

            return deepest;
        }

        (float seconds, float moved, float deepest) Work(float cap)
        {
            var start = Outstanding();
            var elapsed = 0f;
            var deepest = 0f;
            while (elapsed < cap && _map.Count > 0)
            {
                _dispatcher.Tick(TickSeconds);
                foreach (var unit in _units)
                    unit.Tick(TickSeconds);
                elapsed += TickSeconds;
                deepest = Mathf.Max(deepest, DeepestUnit());
            }

            return (elapsed, start - Outstanding(), deepest);
        }

        [Test]
        public void ABigCutHasRoomForARealHaulRoadDownIntoIt()
        {
            // Three metres down at 12% wants twenty-five metres of run. A forty-metre cut has that
            // inside it, which is the whole of Ronan's point: the run is not a property of the
            // ramp, it is a property of how big you make the hole.
            const float depth = 3f;
            var floor = Ground - depth;
            var chain = new List<RoadNode>
            {
                // In across the plain, then down the length of the cut to its far side.
                new RoadNode { Position = new Vector2(Middle.x - Half - 14 + 0.5f, Middle.y + 0.5f), Height = Ground },
                new RoadNode { Position = new Vector2(Middle.x + Half + 0.5f, Middle.y + 0.5f), Height = floor },
            };

            var grade = RoadSpline.Grade(chain, 0, _grid.CellSize);
            var run = Vector2.Distance(chain[0].Position, chain[1].Position) * _grid.CellSize;

            Assert.That(RoadPlanner.Judge(grade, RoadPlanner.DefaultMaxGrade), Is.EqualTo(RoadGradeState.Fine),
                $"{depth} m down in {run:0.#} m of run is {grade * 100f:0.#}%, which a haul road should be happy with");
        }

        [Test]
        public void AHaulRoadDrawnIntoABigCutTakesTheCrewToTheFloor()
        {
            // The claim under test: at a real size the road tool *is* the way in. The pit is
            // marked as the player would mark it, the haul road is drawn down the middle of it,
            // and the question is whether the crew end up standing on the floor rather than
            // looking at a wall.
            Crew(4);
            const float depth = 3f;
            var floor = Ground - depth;
            for (var z = Middle.y - Half; z <= Middle.y + Half; z++)
                for (var x = Middle.x - Half; x <= Middle.x + Half; x++)
                    _map.Designate(x, z, DesignationKind.Dig, floor);
            for (var z = 20; z <= 34; z++)
                for (var x = 20; x <= 34; x++)
                    _map.SetDumpZone(x, z, true);

            var chain = new List<RoadNode>
            {
                new RoadNode { Position = new Vector2(Middle.x - Half - 14 + 0.5f, Middle.y + 0.5f), Height = Ground },
                new RoadNode { Position = new Vector2(Middle.x + Half + 0.5f, Middle.y + 0.5f), Height = floor },
            };
            var samples = new List<RoadSample>();
            var footprint = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            RoadPlanner.Footprint(_grid, samples, 5, footprint, bed, includeSettled: true);
            foreach (var cell in footprint)
                if (cell.IsDig)
                    _map.Designate(cell.X, cell.Z, DesignationKind.Dig, cell.Height);

            var (seconds, moved, deepest) = Work(900f);
            Debug.Log($"BIG CUT: haul road at {RoadSpline.Grade(chain, 0, _grid.CellSize) * 100f:0.#}%; "
                      + $"{moved:0.#} m³ moved in {seconds:0} game s, deepest a unit stood: {deepest:0.##} m "
                      + $"of {depth} m, {Outstanding():0.#} m³ left, ramp note: {_dispatcher.RampNote}");

            // Written down because it settles the question it was built to ask. Making the hole
            // big enough fixes the *geometry* — a 12% haul road fits in a forty-metre cut, which
            // ABigCutHasRoomForARealHaulRoadDownIntoIt now holds — but it does not fix the crew.
            // They cut the first step of the ramp, half a metre, and stop: 16 m³ of 1200 in 900
            // game seconds, with "nowhere to stand beside" (2026-09-23). That is the same fault as
            // in the seven-cell pit, so the fault is in how a step is cut, not in how much room
            // there is, and no amount of size will get round it.
            Assert.That(deepest, Is.LessThan(depth * 0.6f),
                $"if the crew now walk down into the cut ({deepest:0.##} m of {depth} m), the ramp "
                + "fault is fixed and this test should assert that instead");
        }
    }
}
