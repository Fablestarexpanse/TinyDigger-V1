using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>Slice 6: the void at the edge of the disc, and the Level and Road tools.</summary>
    public class BlueprintTests
    {
        const float Tolerance = 1e-3f;
        const int Size = 32;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        readonly List<PlannedCell> _plan = new List<PlannedCell>();

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    SetHeight(x, z, 8f);
            _map = new DesignationMap(_grid);
            _pathfinder = new GridPathfinder(_grid);
        }

        [TearDown]
        public void TearDown()
        {
            _map.Dispose();
        }

        void SetHeight(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f) });

        // --- the void ---------------------------------------------------------------------------

        [Test]
        public void VoidCellsHoldNothingAndCannotBeDesignated()
        {
            _grid.SetVoid(4, 4, true);

            Assert.That(_grid.IsVoid(4, 4), Is.True);
            Assert.That(_grid.IsGround(4, 4), Is.False);
            Assert.That(_grid.GetLayerCount(4, 4), Is.EqualTo(0), "voiding a cell empties its column");
            Assert.That(_grid.GetSurfaceHeight(4, 4), Is.EqualTo(0f).Within(Tolerance));

            Assert.That(_map.Designate(4, 4, DesignationKind.Dig, 7f), Is.False);
            Assert.That(_map.Designate(4, 4, DesignationKind.Fill, 9f), Is.False);
            Assert.That(_map.SetDumpZone(4, 4, true), Is.False);
            Assert.That(_map.Count, Is.EqualTo(0));
            Assert.That(_map.DumpZoneCount, Is.EqualTo(0));
        }

        [Test]
        public void UnitsCannotDriveIntoTheVoid()
        {
            // A wall of void across the map, with no gap.
            for (var z = 0; z < Size; z++)
                _grid.SetVoid(12, z, true);

            Assert.That(_pathfinder.CanStep(11, 5, 12, 5), Is.False, "into the void");
            Assert.That(_pathfinder.CanStep(12, 5, 13, 5), Is.False, "out of the void");

            var path = new List<Vector2Int>();
            Assert.That(_pathfinder.TryFindPath(4, 5, 20, 5, path), Is.False, "no way across");

            var reachable = new bool[Size * Size];
            _pathfinder.FloodReachable(4, 5, reachable);
            Assert.That(reachable[5 * Size + 20], Is.False);
            Assert.That(reachable[5 * Size + 12], Is.False, "the void is not part of anywhere");

            var regions = new RegionMap(_grid, _pathfinder);
            Assert.That(regions.CanReach(4, 5, 20, 5), Is.False);
            Assert.That(regions.CanReach(4, 5, 12, 5), Is.False);
            Assert.That(regions.RegionOf(12, 5), Is.EqualTo(-1), "void cells belong to no region");
            regions.Dispose();
        }

        [Test]
        public void TheRimTakesItsHeightFromTheGroundBesideIt()
        {
            // Ground on one side, void on the other: the corner between them reads as the ground.
            for (var z = 0; z < Size; z++)
                for (var x = 16; x < Size; x++)
                    _grid.SetVoid(x, z, true);

            Assert.That(TerrainSurface.CornerHeight(_grid, 16, 8), Is.EqualTo(8f).Within(Tolerance));
            Assert.That(TerrainSurface.CornerHeight(_grid, 20, 8), Is.EqualTo(0f).Within(Tolerance), "out over the void");
        }

        // --- the Level tool ---------------------------------------------------------------------

        [Test]
        public void LevellingASlopeCutsTheHighSideAndFillsTheLow()
        {
            // A staircase: 6 m at x = 4 up to 10 m at x = 8.
            for (var z = 0; z < Size; z++)
                for (var x = 4; x <= 8; x++)
                    SetHeight(x, z, 6f + (x - 4));

            Blueprints.PlanLevel(_grid, new RectInt(4, 10, 5, 1), 8f, _plan);

            Assert.That(_plan.Count, Is.EqualTo(4), "the cell already at 8 m needs nothing");
            var digs = 0;
            var fills = 0;
            foreach (var cell in _plan)
            {
                Assert.That(cell.Height, Is.EqualTo(8f).Within(Tolerance));
                if (cell.IsDig)
                    digs++;
                if (cell.IsFill)
                    fills++;
            }

            Assert.That(digs, Is.EqualTo(2), "9 m and 10 m are cut");
            Assert.That(fills, Is.EqualTo(2), "6 m and 7 m are filled");
            Blueprints.Volumes(_plan, out var cut, out var fill);
            Assert.That(cut, Is.EqualTo(3f).Within(Tolerance));
            Assert.That(fill, Is.EqualTo(3f).Within(Tolerance));

            Assert.That(Blueprints.Apply(_map, _plan), Is.EqualTo(4));
            Assert.That(_map.GetKind(8, 10), Is.EqualTo(DesignationKind.Dig));
            Assert.That(_map.GetKind(4, 10), Is.EqualTo(DesignationKind.Fill));
            Assert.That(_map.GetTarget(4, 10), Is.EqualTo(8f).Within(Tolerance));
        }

        [Test]
        public void LevellingLeavesTheVoidAlone()
        {
            for (var z = 0; z < Size; z++)
                for (var x = 20; x < Size; x++)
                    _grid.SetVoid(x, z, true);

            Blueprints.PlanLevel(_grid, new RectInt(18, 10, 6, 1), 6f, _plan);

            Assert.That(_plan.Count, Is.EqualTo(2), "only the two cells that are still land");
            foreach (var cell in _plan)
                Assert.That(cell.X, Is.LessThan(20));
        }

        // --- the Road tool ----------------------------------------------------------------------

        [Test]
        public void ARoadInterpolatesItsHeightBetweenControlPoints()
        {
            // Ground well below the road, so every cell of it needs work and appears in the plan.
            for (var z = 12; z <= 20; z++)
                for (var x = 0; x < Size; x++)
                    SetHeight(x, z, 5f);

            var points = new[]
            {
                new RoadPoint(new Vector2Int(4, 16), 8f),
                new RoadPoint(new Vector2Int(12, 16), 12f),
                new RoadPoint(new Vector2Int(20, 16), 8f),
            };

            Blueprints.PlanRoad(_grid, points, 3, _plan);

            Assert.That(HeightAt(8, 16), Is.EqualTo(10f).Within(Tolerance), "halfway up the first leg");
            Assert.That(HeightAt(12, 16), Is.EqualTo(12f).Within(Tolerance), "the middle point");
            Assert.That(HeightAt(16, 16), Is.EqualTo(10f).Within(Tolerance), "halfway down the second");
            Assert.That(HeightAt(4, 16), Is.EqualTo(8f).Within(Tolerance));
            Assert.That(HeightAt(20, 16), Is.EqualTo(8f).Within(Tolerance));

            // Three cells wide: the cells either side of the centre line are in, the next are not.
            Assert.That(Planned(8, 15), Is.True);
            Assert.That(Planned(8, 17), Is.True);
            Assert.That(Planned(8, 13), Is.False);
        }

        [Test]
        public void ARoadIsRefusedAboveTheMaxGradeAndAllowedAtIt()
        {
            // One metre in four cells is the limit.
            var atTheLimit = new[]
            {
                new RoadPoint(new Vector2Int(4, 16), 8f),
                new RoadPoint(new Vector2Int(8, 16), 9f),
            };
            var tooSteep = new[]
            {
                new RoadPoint(new Vector2Int(4, 16), 8f),
                new RoadPoint(new Vector2Int(8, 16), 10f),
            };

            Assert.That(Blueprints.SteepestGrade(atTheLimit, out var flatLeg), Is.EqualTo(0.25f).Within(Tolerance));
            Assert.That(flatLeg, Is.EqualTo(0));
            Assert.That(Blueprints.SteepestGrade(tooSteep, out var steepLeg), Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(steepLeg, Is.EqualTo(0));

            const float maxGrade = 0.25f;
            Assert.That(Blueprints.SteepestGrade(atTheLimit, out _), Is.LessThanOrEqualTo(maxGrade + Tolerance), "allowed at the limit");
            Assert.That(Blueprints.SteepestGrade(tooSteep, out _), Is.GreaterThan(maxGrade), "refused above it");
        }

        [Test]
        public void ARoadOverAHillIsMostlyCutAndCostsWhatItSays()
        {
            // A ridge across the middle of the route, 3 m proud.
            for (var z = 14; z <= 18; z++)
                for (var x = 10; x <= 14; x++)
                    SetHeight(x, z, 11f);

            var points = new[]
            {
                new RoadPoint(new Vector2Int(4, 16), 8f),
                new RoadPoint(new Vector2Int(20, 16), 8f),
            };

            Blueprints.PlanRoad(_grid, points, 3, _plan);
            Blueprints.Volumes(_plan, out var cut, out var fill);

            Assert.That(cut, Is.GreaterThan(0f), "the ridge is cut through");
            Assert.That(fill, Is.EqualTo(0f).Within(Tolerance), "the rest is already at 8 m");
            // Five cells of ridge, three wide, 3 m proud.
            Assert.That(cut, Is.EqualTo(5 * 3 * 3f).Within(Tolerance));
            Assert.That(Blueprints.Apply(_map, _plan), Is.EqualTo(_plan.Count));
        }

        float HeightAt(int x, int z)
        {
            foreach (var cell in _plan)
                if (cell.X == x && cell.Z == z)
                    return cell.Height;
            return float.NaN;
        }

        bool Planned(int x, int z)
        {
            foreach (var cell in _plan)
                if (cell.X == x && cell.Z == z)
                    return true;
            return false;
        }
    }
}
