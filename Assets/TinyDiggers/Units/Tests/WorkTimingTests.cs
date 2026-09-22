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
