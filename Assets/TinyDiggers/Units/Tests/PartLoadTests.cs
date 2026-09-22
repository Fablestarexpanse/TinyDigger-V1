using NUnit.Framework;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// A unit holding less than one cut, after tipping most of a load, has to go back to work.
    /// The full latch used to stay set: too full to dig, too empty to tip, standing in front of a
    /// job it could do. Every worker on the site ended up like that.
    /// </summary>
    public class PartLoadTests
    {
        const int Size = 16;
        const float TickSeconds = 0.05f;

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
        }

        [TearDown]
        public void TearDown()
        {
            _unit?.Dispose();
            _map.Dispose();
        }

        [Test]
        public void APartLoadGoesBackToWork()
        {
            // A barrow that holds four cuts, so a part load is well short of full.
            _unit = new CrewUnit(_grid, _map, _pathfinder, 4, 4,
                capacity: _grid.HeightStep * _grid.CellArea * 4f);
            Assert.That(_map.Designate(5, 4, DesignationKind.Dig, 5f), Is.True);
            Assert.That(_map.SetDumpZone(9, 9, true), Is.True);

            var before = _grid.GetSurfaceHeight(5, 4);
            var elapsed = 0f;
            while (elapsed < 60f && _grid.GetSurfaceHeight(5, 4) >= before)
            {
                _unit.Tick(TickSeconds);
                elapsed += TickSeconds;
            }

            Assert.That(_grid.GetSurfaceHeight(5, 4), Is.LessThan(before), "it should have dug");

            // Now it is carrying part of a load. It must still take the next cut rather than
            // standing there because a latch says it is full.
            var carried = _unit.Inventory.Total;
            Assert.That(carried, Is.GreaterThan(0f));
            Assert.That(_unit.Inventory.Remaining, Is.GreaterThan(_grid.HeightStep * _grid.CellArea),
                "the test wants a part load, not a full one");

            var height = _grid.GetSurfaceHeight(5, 4);
            elapsed = 0f;
            while (elapsed < 60f && _grid.GetSurfaceHeight(5, 4) >= height
                   && _map.GetKind(5, 4) == DesignationKind.Dig)
            {
                _unit.Tick(TickSeconds);
                elapsed += TickSeconds;
            }

            Assert.That(_grid.GetSurfaceHeight(5, 4) < height || _map.GetKind(5, 4) != DesignationKind.Dig,
                Is.True, "a unit carrying part of a load still digs");
        }
    }
}
