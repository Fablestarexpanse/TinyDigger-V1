using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    public class GridPathfinderTests
    {
        const int Size = 21;

        TerrainGrid _grid;
        GridPathfinder _pathfinder;
        List<Vector2Int> _path;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    SetHeight(x, z, 5f);
            _pathfinder = new GridPathfinder(_grid);
            _path = new List<Vector2Int>();
        }

        void SetHeight(int x, int z, float height) =>
            _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 1f), new Layer(MaterialTable.Dirt, height - 1f) });

        [Test]
        public void FindsAStraightPathOnFlatGround()
        {
            Assert.That(_pathfinder.TryFindPath(0, 5, 10, 5, _path), Is.True);

            Assert.That(_path[0], Is.EqualTo(new Vector2Int(0, 5)));
            Assert.That(_path[_path.Count - 1], Is.EqualTo(new Vector2Int(10, 5)));
            Assert.That(_path.Count, Is.EqualTo(11));
        }

        [Test]
        public void AOneStepRiseIsPassableATwoStepRiseIsNot()
        {
            SetHeight(6, 5, 6f);
            SetHeight(7, 5, 7f);

            Assert.That(_pathfinder.CanStep(5, 5, 6, 5), Is.True, "1 m up");
            Assert.That(_pathfinder.CanStep(6, 5, 5, 5), Is.True, "1 m down");
            Assert.That(_pathfinder.CanStep(5, 5, 7, 5), Is.False, "not adjacent");
            SetHeight(8, 5, 9f);
            Assert.That(_pathfinder.CanStep(7, 5, 8, 5), Is.False, "2 m up");
        }

        [Test]
        public void AWallOfBlockedStepsIsRoutedThroughItsGap()
        {
            // A 2 m wall across the whole map at x = 10, with a gap at z = 17.
            for (var z = 0; z < Size; z++)
                if (z != 17)
                    SetHeight(10, z, 7f);

            Assert.That(_pathfinder.TryFindPath(5, 5, 15, 5, _path), Is.True);
            Assert.That(_path, Has.Member(new Vector2Int(10, 17)));
            foreach (var cell in _path)
                Assert.That(cell.x != 10 || cell.y == 17, $"crossed the wall at {cell}");
        }

        [Test]
        public void AClosedWallMakesTheOtherSideUnreachable()
        {
            for (var z = 0; z < Size; z++)
                SetHeight(10, z, 7f);

            Assert.That(_pathfinder.TryFindPath(5, 5, 15, 5, _path), Is.False);
            Assert.That(_path, Is.Empty);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void ADiagonalCannotCutACornerOverAStep(bool blockEast, bool blockNorth)
        {
            // Moving from (5,5) to (6,6): the two cells it cuts past are (6,5) and (5,6).
            if (blockEast)
                SetHeight(6, 5, 7f);
            if (blockNorth)
                SetHeight(5, 6, 7f);

            Assert.That(_pathfinder.CanStep(5, 5, 6, 6), Is.False);
        }

        [Test]
        public void ADiagonalIsFineWhenBothCornersAreSteppable()
        {
            SetHeight(6, 5, 6f);

            Assert.That(_pathfinder.CanStep(5, 5, 6, 6), Is.True);
        }

        [Test]
        public void CanPathToACellAdjacentToAnUnreachableTarget()
        {
            // A 5 m pillar: nothing can stand on it, but it can be reached from beside.
            SetHeight(10, 10, 10f);

            Assert.That(_pathfinder.TryFindPath(2, 10, 10, 10, _path), Is.False, "the pillar itself is unreachable");
            Assert.That(_pathfinder.TryFindPathToAdjacent(2, 10, 10, 10, _path), Is.True);

            var end = _path[_path.Count - 1];
            Assert.That(end, Is.EqualTo(new Vector2Int(9, 10)), "the nearest side");
            Assert.That(Mathf.Abs(end.x - 10) <= 1 && Mathf.Abs(end.y - 10) <= 1, Is.True);
        }

        [Test]
        public void AdjacentPathingCanBeLimitedToAcceptableStandingCells()
        {
            Assert.That(_pathfinder.TryFindPathToAdjacent(2, 10, 10, 10, _path, (x, z) => x == 11), Is.True);

            Assert.That(_path[_path.Count - 1].x, Is.EqualTo(11), "had to go round to the far side");
        }

        [Test]
        public void TheFlatDetourBeatsAClimbOfTheSameLength()
        {
            // A 1 m bump along row 5 from x 7 to 13, right on the straight line. Climbing over it
            // costs 12; stepping round it along row 4 or 6 costs about 10.8.
            for (var x = 7; x <= 13; x++)
                SetHeight(x, 5, 6f);

            Assert.That(_pathfinder.TryFindPath(5, 5, 15, 5, _path), Is.True);

            foreach (var cell in _path)
                Assert.That(_grid.GetSurfaceHeight(cell.x, cell.y), Is.EqualTo(5f), $"climbed at {cell}");
        }

        [Test]
        public void StepCostGrowsWithTheHeightChange()
        {
            SetHeight(6, 5, 6f);

            Assert.That(_pathfinder.StepCost(4, 5, 5, 5), Is.EqualTo(1f));
            Assert.That(_pathfinder.StepCost(5, 5, 6, 5), Is.EqualTo(2f), "1 x (1 + 1 x slope factor 1)");
        }

        [Test]
        public void TryFindNearestReturnsTheCheapestGoal()
        {
            var found = _pathfinder.TryFindNearest(10, 10, (x, z) => x == 3 || x == 15, _path);

            Assert.That(found, Is.True);
            Assert.That(_path[_path.Count - 1].x, Is.EqualTo(15), "5 away beats 7 away");
        }

        [Test]
        public void FloodReachableStopsAtWalls()
        {
            for (var z = 0; z < Size; z++)
                SetHeight(10, z, 7f);
            var reachable = new bool[Size * Size];

            var count = _pathfinder.FloodReachable(5, 5, reachable);

            Assert.That(count, Is.EqualTo(10 * Size));
            Assert.That(reachable[5 * Size + 15], Is.False);
        }
    }
}
