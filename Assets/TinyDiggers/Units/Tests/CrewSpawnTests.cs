using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Where the crew starts: on the wished-for cell when it is level, dry ground; otherwise the
    /// nearest cell whose whole 3 x 3 block is, never in water or on a slope.
    /// </summary>
    public class CrewSpawnTests
    {
        const int Size = 24;

        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            // Level land at +1 m everywhere (datum 4 m under the sea).
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, datum: -4f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    Column(x, z, 5f);
        }

        void Column(int x, int z, float rock) => _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, rock) });

        [Test]
        public void LevelDryGroundIsUsedAsItIs()
        {
            Assert.That(CrewSpawn.TryFind(_grid, 12, 12, 10, out var cell), Is.True);
            Assert.That(cell, Is.EqualTo(new Vector2Int(12, 12)));
        }

        [Test]
        public void WaterPushesTheSpawnToTheNearestDryBlock()
        {
            // A lake 7 cells across round the wished-for cell.
            for (var z = 9; z <= 15; z++)
                for (var x = 9; x <= 15; x++)
                    Column(x, z, 1f);

            Assert.That(CrewSpawn.TryFind(_grid, 12, 12, 10, out var cell), Is.True);
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    Assert.That(_grid.IsWater(cell.x + dx, cell.y + dz), Is.False, $"a crew member would start in the lake at {cell}");
            Assert.That(Mathf.Max(Mathf.Abs(cell.x - 12), Mathf.Abs(cell.y - 12)), Is.EqualTo(5), "the first ring clear of the lake");
        }

        [Test]
        public void ASteepBlockIsPassedOver()
        {
            // A 2 m step right beside the wished-for cell.
            for (var z = 0; z < Size; z++)
                Column(13, z, 7f);

            Assert.That(CrewSpawn.TryFind(_grid, 12, 12, 10, out var cell), Is.True);
            Assert.That(CrewSpawn.IsGood(_grid, cell.x, cell.y), Is.True);
            Assert.That(Mathf.Abs(cell.x - 13), Is.GreaterThan(1), "no block that straddles the step");
        }

        [Test]
        public void NoGoodGroundAtAllIsReported()
        {
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    Column(x, z, 1f);

            Assert.That(CrewSpawn.TryFind(_grid, 12, 12, 10, out _), Is.False);
        }
    }
}
