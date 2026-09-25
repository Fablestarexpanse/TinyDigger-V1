using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// How steep is too steep (Ronan, 2026-09-22: "we need to figure out the topography, they
    /// should be able to climb and not climb"). The limit belongs to the ground, not the machine:
    /// one slope for everything, said as an angle so it means the same whatever size the cells
    /// are. Steeper than that wants a ramp or a road.
    /// </summary>
    public class SlopeTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 16;

        TerrainGrid _grid;
        GridPathfinder _pathfinder;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f,
                cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 4f),
                    });
            _pathfinder = new GridPathfinder(_grid);
        }

        [Test]
        public void ASlopeIsAStepMeasuredAgainstTheCell()
        {
            _pathfinder.MaxSlope = 1f;
            Assert.That(_pathfinder.MaxStepHeight, Is.EqualTo(_grid.CellSize).Within(Tolerance),
                "rise equal to run is forty-five degrees");
            Assert.That(_pathfinder.MaxSlopeDegrees, Is.EqualTo(45f).Within(0.01f));

            _pathfinder.MaxSlopeDegrees = 35f;
            Assert.That(_pathfinder.MaxSlope, Is.EqualTo(0.7002f).Within(0.001f));
            Assert.That(_pathfinder.MaxStepHeight, Is.EqualTo(0.35f).Within(0.01f));
        }

        [Test]
        public void TheSameAngleMeansTheSameGroundWhateverTheCellSize()
        {
            var coarse = new GridPathfinder(new TerrainGrid(Size, Size,
                TinyDiggersMaterials.CreateTable(), heightStep: 1f, cellSize: 1f));
            _pathfinder.MaxSlopeDegrees = 30f;
            coarse.MaxSlopeDegrees = 30f;

            Assert.That(_pathfinder.MaxStepHeight / _grid.CellSize,
                Is.EqualTo(coarse.MaxStepHeight / 1f).Within(Tolerance),
                "an angle is an angle however finely the map is cut");
            Assert.That(coarse.MaxStepHeight, Is.GreaterThan(_pathfinder.MaxStepHeight));
        }

        [Test]
        public void GroundSteeperThanTheLimitCannotBeDriven()
        {
            _pathfinder.MaxSlopeDegrees = 35f;   // 0.35 m of rise per half-metre cell

            // A bank one step (0.5 m) proud: steeper than 35 degrees, so nothing drives up it.
            for (var z = 0; z < Size; z++)
                _grid.SetColumn(8, z, new[]
                {
                    new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 4.5f),
                });

            var path = new System.Collections.Generic.List<UnityEngine.Vector2Int>();
            Assert.That(_pathfinder.TryFindPath(4, 4, 12, 4, path), Is.False,
                "a bank steeper than the limit is a wall until something cuts it");

            // Let the limit up past that bank and the way opens.
            _pathfinder.MaxSlopeDegrees = 50f;
            Assert.That(_pathfinder.TryFindPath(4, 4, 12, 4, path), Is.True);
        }
    }
}
