using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    public class TerrainPickerTests
    {
        const float Tolerance = 1e-3f;

        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            // 20x20 of flat 2m dirt.
            _grid = new TerrainGrid(20, 20, MaterialTable.CreateBasic());
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < _grid.Width; x++)
                    _grid.Add(x, z, MaterialTable.Dirt, 2f);
        }

        [Test]
        public void StraightDownHitsTheCellBelowAtItsSurface()
        {
            var hit = TerrainPicker.TryPick(_grid, new Vector3(5.5f, 50f, 7.5f), Vector3.down, out var x, out var z, out var point);

            Assert.That(hit, Is.True);
            Assert.That((x, z), Is.EqualTo((5, 7)));
            Assert.That(point.y, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void AnAngledRayHitsWhereItMeetsTheSurface()
        {
            // Down 45 degrees along +x from (0.5, 10): meets y = 2 after 8 units, at x = 8.5.
            var hit = TerrainPicker.TryPick(_grid, new Vector3(0.5f, 10f, 3.5f), new Vector3(1f, -1f, 0f), out var x, out var z, out var point);

            Assert.That(hit, Is.True);
            Assert.That((x, z), Is.EqualTo((8, 3)));
            Assert.That(point.x, Is.EqualTo(8.5f).Within(Tolerance));
        }

        [Test]
        public void ATallColumnInTheWayIsHitOnItsSide()
        {
            _grid.Add(5, 3, MaterialTable.Rock, 20f);

            var hit = TerrainPicker.TryPick(_grid, new Vector3(0.5f, 10f, 3.5f), new Vector3(1f, -1f, 0f), out var x, out var z, out var point);

            Assert.That(hit, Is.True);
            Assert.That((x, z), Is.EqualTo((5, 3)), "the ray should stop at the rock column before reaching the ground beyond");
            Assert.That(point.x, Is.EqualTo(5f).Within(Tolerance));
        }

        [Test]
        public void ARayFromOutsideTheGridEntersAndHits()
        {
            var hit = TerrainPicker.TryPick(_grid, new Vector3(-10f, 12f, 4.5f), new Vector3(1f, -1f, 0f), out var x, out var z, out _);

            Assert.That(hit, Is.True);
            Assert.That((x, z), Is.EqualTo((0, 4)));
        }

        [Test]
        public void ADiagonalRayStepsThroughBothAxes()
        {
            var hit = TerrainPicker.TryPick(_grid, new Vector3(0.2f, 6f, 0.7f), new Vector3(1f, -1f, 1f), out var x, out var z, out var point);

            Assert.That(hit, Is.True);
            // It falls 4 to reach the surface, moving 4 along x and z too: lands at (4.2, 2, 4.7).
            Assert.That(point.y, Is.EqualTo(2f).Within(Tolerance));
            Assert.That((x, z), Is.EqualTo((4, 4)));
            Assert.That(x, Is.EqualTo(Mathf.FloorToInt(point.x)));
            Assert.That(z, Is.EqualTo(Mathf.FloorToInt(point.z)));
        }

        [Test]
        public void ARayPointingUpMisses()
        {
            Assert.That(TerrainPicker.TryPick(_grid, new Vector3(5f, 10f, 5f), Vector3.up, out _, out _, out _), Is.False);
        }

        [Test]
        public void ARayThatNeverCrossesTheGridMisses()
        {
            Assert.That(TerrainPicker.TryPick(_grid, new Vector3(-5f, 10f, -5f), new Vector3(-1f, -1f, 0f), out _, out _, out _), Is.False);
        }

        [Test]
        public void ARayThatSkimsOverTheGridMisses()
        {
            Assert.That(TerrainPicker.TryPick(_grid, new Vector3(-5f, 10f, 5.5f), new Vector3(1f, -0.01f, 0f), out _, out _, out _), Is.False);
        }
    }
}
