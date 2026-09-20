using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// What the sim does about the sea: water is impassable and cannot be dug, but it is a valid
    /// place to fill or to tip, because reclaiming the shallows is the point of having them.
    /// </summary>
    public class WaterCellTests
    {
        const int Size = 16;

        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            // The datum is below the sea, so a column's height decides whether it is land: five
            // metres of rock puts the left half at +1 m, one metre puts the right half at -3 m.
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, datum: -4f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, x < Size / 2 ? 5f : 1f) });
        }

        static (int x, int z) Land => (2, 8);

        static (int x, int z) Water => (12, 8);

        [Test]
        public void ACellBelowSeaLevelIsWaterAndKnowsItsDepth()
        {
            Assert.That(_grid.IsWater(Water.x, Water.z), Is.True);
            Assert.That(_grid.WaterDepth(Water.x, Water.z), Is.EqualTo(3f).Within(1e-4f));
            Assert.That(_grid.IsWater(Land.x, Land.z), Is.False);
            Assert.That(_grid.WaterDepth(Land.x, Land.z), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void WaterIsStillGroundButNotPassable()
        {
            Assert.That(_grid.IsGround(Water.x, Water.z), Is.True, "the seabed is part of the world");
            Assert.That(_grid.IsPassableGround(Water.x, Water.z), Is.False, "nothing walks on it");
            Assert.That(_grid.IsPassableGround(Land.x, Land.z), Is.True);
        }

        [Test]
        public void ThePathfinderWillNotCrossWater()
        {
            var pathfinder = new GridPathfinder(_grid);
            var path = new List<Vector2Int>();

            Assert.That(pathfinder.TryFindPath(Land.x, Land.z, Water.x, Water.z, path), Is.False,
                "there is no way onto the seabed");

            var reachable = new bool[Size * Size];
            pathfinder.FloodReachable(Land.x, Land.z, reachable);
            Assert.That(reachable[Water.z * Size + Water.x], Is.False, "water floods like the void");
            Assert.That(reachable[Land.z * Size + Land.x], Is.True);
        }

        [Test]
        public void RegionsStopAtTheWaterline()
        {
            var regions = new RegionMap(_grid, new GridPathfinder(_grid));
            regions.Update();

            Assert.That(regions.CanReach(Land.x, Land.z, Water.x, Water.z), Is.False);
            Assert.That(regions.CanReach(Land.x, Land.z, Land.x + 1, Land.z), Is.True);
        }

        [Test]
        public void WaterRefusesADigButTakesAFillOrADumpZone()
        {
            var designations = new DesignationMap(_grid);

            Assert.That(designations.Designate(Water.x, Water.z, DesignationKind.Dig, -4f), Is.False,
                "there is nothing to dig out of the seabed");
            Assert.That(designations.Designate(Water.x, Water.z, DesignationKind.Fill, 2f), Is.True,
                "filling the shallows is reclamation");
            Assert.That(designations.SetDumpZone(Water.x, Water.z, true), Is.True,
                "and a dump zone is how the crew is told to do it");
        }

        [Test]
        public void FillingAWaterCellAboveSeaLevelTurnsItIntoLand()
        {
            var x = Water.x;
            var z = Water.z;
            Assert.That(_grid.IsWater(x, z), Is.True);

            // Three metres of rock takes the surface from -3 m to sea level: still water, because
            // land starts one whole step above the sea.
            _grid.Add(x, z, MaterialTable.Rock, 3f);
            Assert.That(_grid.GetSurfaceHeight(x, z), Is.EqualTo(World.SeaLevel).Within(1e-4f));
            Assert.That(_grid.IsWater(x, z), Is.True, "level with the sea is not yet dry land");
            Assert.That(_grid.IsPassableGround(x, z), Is.False);

            _grid.Add(x, z, MaterialTable.Rock, 1f);
            Assert.That(_grid.IsWater(x, z), Is.False, "one step above the sea is land");
            Assert.That(_grid.IsPassableGround(x, z), Is.True);

            var regions = new RegionMap(_grid, new GridPathfinder(_grid));
            regions.Update();
            Assert.That(regions.CanReach(x, z, x, z), Is.True, "and the crew can be there");
        }

        [Test]
        public void DiggingLandDownToTheSeaMakesItWaterAgain()
        {
            var x = Land.x;
            var z = Land.z;
            Assert.That(_grid.GetSurfaceHeight(x, z), Is.EqualTo(World.SeaLevel + 1f).Within(1e-4f));

            var removed = new MaterialVolume[TerrainGrid.MaxLayersPerCell];
            _grid.Remove(x, z, 1f, removed);

            Assert.That(_grid.GetSurfaceHeight(x, z), Is.EqualTo(World.SeaLevel).Within(1e-4f));
            Assert.That(_grid.IsWater(x, z), Is.True, "cut back to sea level, the sea takes it back");
            Assert.That(_grid.IsPassableGround(x, z), Is.False);
        }

    }
}
