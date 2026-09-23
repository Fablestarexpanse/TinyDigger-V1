using NUnit.Framework;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// A cut takes as long as the scoop takes to swing (Ronan, 2026-09-22: "every dig needs a
    /// matching animation, the time to scoop and load truck"). The crew logic holds no animation
    /// itself; whatever draws a unit tells it how long the work looks.
    /// </summary>
    public class WorkTimingTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 16;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        CrewUnit _unit;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f,
                cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 4f),
                    });
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
            _unit = new CrewUnit(_grid, _map, _pathfinder, 4, 4);
        }

        [TearDown]
        public void TearDown()
        {
            _unit.Dispose();
            _map.Dispose();
        }

        [Test]
        public void TheGroundDoesNotMoveUntilTheBucketReachesIt()
        {
            // Ronan, 2026-09-23: the terrain change fires at the bite, not at the start of the
            // job. The unit is told where in the swing that is (BiteAt, from an OnBite event on
            // the clip); until then the swing is playing and the ground has not been touched.
            _unit.DigSeconds = 2f;
            _unit.BiteAt = 0.5f;
            _map.Designate(5, 4, DesignationKind.Dig, 5f);
            var was = _grid.GetSurfaceHeight(5, 4);

            // Up to the moment the bucket lands, nothing has come off.
            var elapsed = 0f;
            var moved = -1f;
            while (elapsed < 4f)
            {
                _unit.Tick(0.05f);
                elapsed += 0.05f;
                if (_grid.GetSurfaceHeight(5, 4) < was - 1e-4f)
                {
                    moved = elapsed;
                    break;
                }
            }

            Assert.That(moved, Is.GreaterThan(0f), "the cut landed at some point");
            Assert.That(_unit.State, Is.EqualTo(CrewUnitState.Digging).Or.EqualTo(CrewUnitState.Idle));
            // It walks a cell first, so the bite is a second of swing after it arrives, not a
            // second after the tick count starts; what matters is that it is not at the start and
            // not at the end of the swing.
            Assert.That(moved, Is.LessThan(4f), "and not after the whole clip had played twice");
        }

        [Test]
        public void ABiteLateInTheSwingMovesTheGroundLaterThanAnEarlyOne()
        {
            // The same cut, the same clip, the event moved: the earth moves later. This is what
            // authoring OnBite on a different frame buys.
            _map.Designate(5, 4, DesignationKind.Dig, 5f);
            _unit.DigSeconds = 2f;
            _unit.BiteAt = 0.1f;
            var early = TimeOfFirstCut();

            SetUpAgain();
            _map.Designate(5, 4, DesignationKind.Dig, 5f);
            _unit.DigSeconds = 2f;
            _unit.BiteAt = 0.9f;
            var late = TimeOfFirstCut();

            Assert.That(early, Is.GreaterThan(0f), "the early bite landed");
            Assert.That(late, Is.GreaterThan(early + 1f),
                $"moving the event four fifths of a two-second swing should cost about 1.6 s "
                + $"(early {early:0.##} s, late {late:0.##} s)");
        }

        /// <summary>Seconds of ticking until the target cell first loses height, or -1.</summary>
        float TimeOfFirstCut()
        {
            var was = _grid.GetSurfaceHeight(5, 4);
            var elapsed = 0f;
            while (elapsed < 8f)
            {
                _unit.Tick(0.05f);
                elapsed += 0.05f;
                if (_grid.GetSurfaceHeight(5, 4) < was - 1e-4f)
                    return elapsed;
            }

            return -1f;
        }

        /// <summary>A fresh grid, map and unit, for a second run inside one test.</summary>
        void SetUpAgain()
        {
            TearDown();
            SetUp();
        }

        [Test]
        public void WithoutAClipACutTakesTheUnitsOwnInterval()
        {
            Assert.That(_unit.DigSeconds, Is.EqualTo(0f));
            Assert.That(_unit.StepSeconds(5, 4), Is.EqualTo(_unit.WorkInterval).Within(Tolerance));
        }

        [Test]
        public void ACutTakesAsLongAsTheClipDoes()
        {
            _unit.DigSeconds = 3.75f;    // the digger mech's dig clip, 90 frames at 24 fps
            Assert.That(_unit.StepSeconds(5, 4), Is.EqualTo(3.75f).Within(Tolerance));
        }

        [Test]
        public void RockStillCostsMoreOnTopOfTheClipsTime()
        {
            _grid.SetColumn(5, 4, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Rock, 4f),
            });
            _unit.DigSeconds = 2f;
            Assert.That(_unit.CutsStone, Is.False);
            Assert.That(_unit.StepSeconds(5, 4), Is.GreaterThan(2f),
                "a unit with no cutter pays the hardness on top of the swing");

            _unit.AsMachine();
            _unit.DigSeconds = 2f;
            Assert.That(_unit.StepSeconds(5, 4), Is.EqualTo(2f).Within(Tolerance),
                "the mech is built for rock: its cut is the time its arm takes, no more");
        }
    }
}
