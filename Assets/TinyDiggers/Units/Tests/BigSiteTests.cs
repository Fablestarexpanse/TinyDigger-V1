using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
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
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f, cellSize: 0.5f);
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

        /// <summary>
        /// The same run, but reporting how much moved in each stretch of it. A crew that is merely
        /// slow moves the same amount every stretch; a crew that is stuck moves less and less and
        /// then none. Reading a status line cannot tell those apart, and has twice been wrong.
        /// </summary>
        string Rate(float cap, int stretches)
        {
            var text = "";
            var was = Outstanding();
            for (var s = 0; s < stretches; s++)
            {
                var until = 0f;
                while (until < cap / stretches && _map.Count > 0)
                {
                    _dispatcher.Tick(TickSeconds);
                    foreach (var unit in _units)
                        unit.Tick(TickSeconds);
                    until += TickSeconds;
                }

                var now = Outstanding();
                text += $"{was - now:0.##} ";
                was = now;
            }

            return text.Trim();
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
        public void ABigCutComesDownInBenchesRatherThanStalling()
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

            var rate = Rate(600f, 6);
            var (seconds, moved, deepest) = Work(900f);
            Debug.Log($"BIG CUT: haul road at {RoadSpline.Grade(chain, 0, _grid.CellSize) * 100f:0.#}%; "
                      + $"{moved:0.#} m³ moved in {seconds:0} game s, deepest a unit stood: {deepest:0.##} m "
                      + $"of {depth} m, {Outstanding():0.#} m³ left, ramp note: {_dispatcher.RampNote}; "
                      + $"m³ per 100 game s over the first 600: {rate}");

            // This looked like a stall and is not one, and reading the status line said "stall"
            // for the third time on this bug (2026-09-23). The rate settles it: 2.88, 2, 2.13,
            // 1.88, 1.75, 1.5 m³ per hundred game seconds — declining gently as the haul to the
            // tip lengthens, never stopping. That is about 1 m³ a minute, which is what four
            // starter robots with barrows move. The hole is 1200 m³.
            //
            // "Deepest a unit stood: 1 m" is benching doing its job, not a crew stuck on a rim:
            // DigFloor holds every cell within a metre of its highest designated neighbour, so a
            // forty-metre block comes down a metre at a time and nobody stands deeper than the
            // bench they have finished. They are about one per cent into the first bench.
            //
            // So the big cut is not the ramp fault. That one is specific to a block too small to
            // hold a bench slope at all — seven cells across wanting three metres — where nothing
            // can come down until a ramp is cut, and the ramp planner has its blind spot.
            Assert.That(rate, Is.Not.Empty);
            Assert.That(moved, Is.GreaterThan(5f),
                $"the crew should keep working a big cut rather than stalling on its rim — {moved:0.#} m³ moved");
            Assert.That(deepest, Is.GreaterThanOrEqualTo(0.5f),
                "and should get at least the first bench down");
        }
    }
}
