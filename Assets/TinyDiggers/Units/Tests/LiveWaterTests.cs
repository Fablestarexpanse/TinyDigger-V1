using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The game reading live water (a simulation's surfaces) instead of the sea-level rule:
    /// - a dry pit below sea level is land;
    /// - water 0.5 m deep blocks digging and driving, and shallower water is waded through;
    /// - only cells that change raise events, and filling above the water dries a cell at once;
    /// - regions follow the water;
    /// - a unit the water comes up round climbs out.
    /// </summary>
    public class LiveWaterTests
    {
        const int Size = 16;
        const float Dry = float.NegativeInfinity;

        TerrainGrid _grid;
        float[] _surfaces;

        [SetUp]
        public void SetUp()
        {
            // Datum 4 m under the sea: the left half stands at +1 m, the right half is a pit at -3 m.
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, datum: -4f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, x < Size / 2 ? 5f : 1f) });
            _surfaces = new float[Size * Size];
            System.Array.Fill(_surfaces, Dry);
        }

        void Water(int x, int z, float surface) => _surfaces[z * Size + x] = surface;

        void Flood(int x0, int z0, int x1, int z1, float surface)
        {
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    Water(x, z, surface);
        }

        [Test]
        public void ADryPitBelowSeaLevelIsLandOnceTheWaterIsLive()
        {
            Assert.That(_grid.IsWater(12, 8), Is.True, "the sea-level rule floods it");

            _grid.SetWaterSurfaces(_surfaces);

            Assert.That(_grid.HasLiveWater, Is.True);
            Assert.That(_grid.IsWater(12, 8), Is.False, "no water has reached it");
            Assert.That(_grid.WaterDepth(12, 8), Is.EqualTo(0f));
            Assert.That(_grid.IsPassableGround(12, 8), Is.True);
            Assert.That(new DesignationMap(_grid).Designate(12, 8, DesignationKind.Dig, -4f), Is.True, "and it can be dug");
        }

        [Test]
        public void DeepWaterBlocksAndShallowWaterIsWaded()
        {
            Water(2, 2, 1.6f);  // 0.6 m over +1 m ground
            Water(3, 2, 1.3f);  // 0.3 m
            _grid.SetWaterSurfaces(_surfaces);
            var map = new DesignationMap(_grid);

            Assert.That(_grid.IsWater(2, 2), Is.True);
            Assert.That(_grid.IsPassableGround(2, 2), Is.False, "nothing drives through 0.6 m");
            Assert.That(map.Designate(2, 2, DesignationKind.Dig, 0f), Is.False, "nor digs under it");

            Assert.That(_grid.IsWater(3, 2), Is.False);
            Assert.That(_grid.WaterDepth(3, 2), Is.EqualTo(0.3f).Within(1e-4f));
            Assert.That(_grid.IsPassableGround(3, 2), Is.True, "0.3 m is waded");
            Assert.That(map.Designate(3, 2, DesignationKind.Dig, 0f), Is.True, "and dug in");
        }

        [Test]
        public void OnlyCellsThatTurnWaterOrStopBeingItRaiseAnEvent()
        {
            _grid.SetWaterSurfaces(_surfaces);
            var changed = new List<Vector2Int>();
            _grid.WaterChanged += (x, z) => changed.Add(new Vector2Int(x, z));

            Water(1, 1, 2f);
            Water(1, 2, 1.2f);  // shallow: still land
            Assert.That(_grid.SetWaterSurfaces(_surfaces), Is.EqualTo(1));
            Assert.That(changed, Is.EqualTo(new[] { new Vector2Int(1, 1) }));

            changed.Clear();
            Assert.That(_grid.SetWaterSurfaces(_surfaces), Is.EqualTo(0), "the same water again changes nothing");
            Assert.That(changed, Is.Empty);
        }

        [Test]
        public void ABandOfRowsChangesOnlyThoseRows()
        {
            _grid.SetWaterSurfaces(_surfaces);
            var changed = new List<Vector2Int>();
            _grid.WaterChanged += (x, z) => changed.Add(new Vector2Int(x, z));

            // Rows 4 and 5, flooded deep across the land half.
            var band = new float[2 * Size];
            System.Array.Fill(band, Dry);
            for (var x = 0; x < Size / 2; x++)
            {
                band[x] = 2f;
                band[Size + x] = 2f;
            }

            Assert.That(_grid.SetWaterRows(4, band), Is.EqualTo(Size));
            Assert.That(changed.TrueForAll(c => c.y == 4 || c.y == 5), Is.True, "only the band's rows");
            Assert.That(_grid.IsWater(0, 4) && _grid.IsWater(7, 5), Is.True);
            Assert.That(_grid.IsWater(0, 3) || _grid.IsWater(0, 6), Is.False, "rows either side untouched");
        }

        [Test]
        public void RowsCannotBeFedBeforeTheWholeMap()
        {
            Assert.Throws<System.InvalidOperationException>(() => _grid.SetWaterRows(0, new float[Size]),
                "a half-fed map would read its unfed half as dry");
            _grid.SetWaterSurfaces(_surfaces);
            Assert.Throws<System.ArgumentException>(() => _grid.SetWaterRows(0, new float[Size + 1]), "whole rows only");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _grid.SetWaterRows(Size - 1, new float[2 * Size]), "past the last row");
        }

        [Test]
        public void FillingAboveTheWaterDriesTheCellAtOnce()
        {
            Water(4, 4, 2f);
            _grid.SetWaterSurfaces(_surfaces);
            Assert.That(_grid.IsWater(4, 4), Is.True);

            // Built up to +2.5 m, over the water, before the simulation has caught up.
            _grid.SetColumn(4, 4, new[] { new Layer(MaterialTable.Rock, 6.5f) });

            Assert.That(_grid.IsWater(4, 4), Is.False);
            Assert.That(_grid.WaterDepth(4, 4), Is.EqualTo(0f));
            Assert.That(_grid.IsPassableGround(4, 4), Is.True);
        }

        [Test]
        public void ClearingLiveWaterGoesBackToTheSeaLevelRule()
        {
            _grid.SetWaterSurfaces(_surfaces);
            Assert.That(_grid.IsWater(12, 8), Is.False);

            _grid.ClearWaterSurfaces();

            Assert.That(_grid.HasLiveWater, Is.False);
            Assert.That(_grid.IsWater(12, 8), Is.True);
            Assert.That(_grid.WaterDepth(12, 8), Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void TheWayOutOfWaterLeadsToTheNearestDryCell()
        {
            Flood(2, 2, 6, 6, 2f);
            _grid.SetWaterSurfaces(_surfaces);
            var pathfinder = new GridPathfinder(_grid);
            var path = new List<Vector2Int>();

            Assert.That(pathfinder.TryFindWayOutOfWater(4, 4, path), Is.True);
            var exit = path[path.Count - 1];
            Assert.That(_grid.IsPassableGround(exit.x, exit.y), Is.True);
            Assert.That(path.Count, Is.EqualTo(4), "three cells out from the middle of a 5-wide flood");
            for (var i = 0; i < path.Count - 1; i++)
                Assert.That(_grid.IsPassableGround(path[i].x, path[i].y), Is.False, "only the last cell is out of the water");

            Assert.That(pathfinder.TryFindWayOutOfWater(10, 10, path), Is.False, "not in water");
        }

        [Test]
        public void TheWayOutGoesPastWaterJustUnderTheLimit()
        {
            // Deep in the middle, then a band at 0.45 m: under the 0.5 m limit, but where a rising
            // pool would catch a unit again straight away. Beyond that, dry.
            Flood(1, 1, 7, 7, 1.45f);
            Flood(3, 3, 5, 5, 2f);
            _grid.SetWaterSurfaces(_surfaces);
            var path = new List<Vector2Int>();

            Assert.That(new GridPathfinder(_grid).TryFindWayOutOfWater(4, 4, path), Is.True);
            var exit = path[path.Count - 1];
            Assert.That(_grid.WaterDepth(exit.x, exit.y), Is.LessThan(_grid.DeepWater * GridPathfinder.ClearOfWater),
                $"ends clear of the water, not at the 0.45 m band ({exit})");
            Assert.That(path.Count, Is.EqualTo(5), "out through the band");
        }

        [Test]
        public void RegionsFollowTheWater()
        {
            _grid.SetWaterSurfaces(_surfaces);
            var pathfinder = new GridPathfinder(_grid);
            using var regions = new RegionMap(_grid, pathfinder);
            regions.Update();
            Assert.That(regions.CanReach(1, 1, 1, 14), Is.True);

            // A channel across the land half cuts it in two.
            Flood(0, 7, 7, 8, 2f);
            _grid.SetWaterSurfaces(_surfaces);
            regions.Update();
            Assert.That(regions.CanReach(1, 1, 1, 14), Is.False);

            Flood(0, 7, 7, 8, Dry);
            _grid.SetWaterSurfaces(_surfaces);
            regions.Update();
            Assert.That(regions.CanReach(1, 1, 1, 14), Is.True, "drained, it joins up again");
        }

        [Test]
        public void AUnitTheWaterComesUpRoundClimbsOut()
        {
            _grid.SetWaterSurfaces(_surfaces);
            var map = new DesignationMap(_grid);
            var unit = new CrewUnit(_grid, map, new GridPathfinder(_grid), 4, 4);
            unit.Tick(0.1f);

            Flood(3, 3, 5, 5, 2f);
            _grid.SetWaterSurfaces(_surfaces);
            var escaped = false;
            for (var i = 0; i < 200; i++)
            {
                unit.Tick(0.1f);
                escaped |= unit.Job == CrewJobKind.Escape;
            }

            Assert.That(escaped, Is.True, "it set out to climb out");
            Assert.That(_grid.IsPassableGround(unit.Cell.x, unit.Cell.y), Is.True, $"it is out, at {unit.Cell}");
            Assert.That(unit.State, Is.EqualTo(CrewUnitState.Idle));
            unit.Dispose();
        }
    }
}
