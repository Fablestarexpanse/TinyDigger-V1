using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Terrain.Tests
{
    /// <summary>Natural terrain, phase 3: straight-line distance, erosion and filled hollows.</summary>
    public class TerrainErosionTests
    {
        [Test]
        public void DistanceIsTheStraightLineNotTheCityBlock()
        {
            const int n = 41;
            var seed = new bool[n * n];
            seed[20 * n + 20] = true;

            var distance = TerrainErosion.EuclideanDistance(seed, n, n);

            Assert.That(distance[20 * n + 20], Is.Zero);
            Assert.That(distance[20 * n + 30], Is.EqualTo(10f).Within(1e-3f));
            Assert.That(distance[30 * n + 30], Is.EqualTo(Mathf.Sqrt(200f)).Within(1e-3f), "a four-way flood would say 20");
            Assert.That(distance[23 * n + 24], Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void DistanceStopsAtItsReachAndIsFarWithNoSeed()
        {
            const int n = 30;
            var seed = new bool[n * n];
            seed[0] = true;

            var reached = TerrainErosion.EuclideanDistance(seed, n, n, reach: 5f);
            var none = TerrainErosion.EuclideanDistance(new bool[n * n], n, n);

            Assert.That(reached[4], Is.EqualTo(4f).Within(1e-3f));
            Assert.That(reached[10], Is.EqualTo(float.MaxValue));
            Assert.That(none.All(d => d == float.MaxValue));
        }

        [Test]
        public void SlopesSettleToTheirRestingAngleConservingSoil()
        {
            const int n = 21;
            var heights = new float[n * n];
            heights[10 * n + 10] = 10f;
            var fixedCells = new bool[n * n];
            for (var i = 0; i < n; i++)
                fixedCells[i] = fixedCells[(n - 1) * n + i] = fixedCells[i * n] = fixedCells[i * n + n - 1] = true;
            var talus = Enumerable.Repeat(1f, n * n).ToArray();
            var before = heights.Sum();

            TerrainErosion.Thermal(heights, n, n, fixedCells, talus, 400);

            Assert.That(heights.Sum(), Is.EqualTo(before).Within(1e-3f), "nothing made or lost");
            for (var z = 1; z < n - 1; z++)
                for (var x = 1; x < n - 2; x++)
                    Assert.That(Mathf.Abs(heights[z * n + x] - heights[z * n + x + 1]), Is.LessThanOrEqualTo(1.05f));
            Assert.That(heights[10 * n + 10], Is.LessThan(5f), "the spike came down");
            Assert.That(fixedCells.Select((f, i) => f ? heights[i] : 0f).Sum(), Is.Zero, "fixed cells took nothing");
        }

        [Test]
        public void AHollowIsFilledToWhereItSpillsAndADrainedOneIsNot()
        {
            // A 9-wide platform at 2 m with the sea round it. A pit at (3,3) sits at 0.5 m inside
            // a ring at 1.5 m; a groove at (6,1..8) runs out to the sea, so it drains.
            const int n = 11;
            const float step = 0.5f;
            var heights = Enumerable.Repeat(2f, n * n).ToArray();
            var land = new bool[n * n];
            var outlets = new bool[n * n];
            for (var z = 0; z < n; z++)
                for (var x = 0; x < n; x++)
                {
                    var edge = x == 0 || z == 0 || x == n - 1 || z == n - 1;
                    outlets[z * n + x] = edge;
                    land[z * n + x] = !edge;
                    if (edge)
                        heights[z * n + x] = -1f;
                }

            for (var z = 2; z <= 4; z++)
                for (var x = 2; x <= 4; x++)
                    heights[z * n + x] = 1.5f;
            heights[3 * n + 3] = 0.5f;
            for (var z = 1; z <= 9; z++)
                heights[z * n + 6] = 0.5f;

            var raised = TerrainErosion.FillDepressions(heights, n, n, land, outlets, step);

            Assert.That(heights[3 * n + 3], Is.EqualTo(2f), "the pit and its ring fill to the platform, where they spill");
            Assert.That(heights[3 * n + 2], Is.EqualTo(2f));
            Assert.That(heights[5 * n + 6], Is.EqualTo(0.5f), "the groove drains to the sea and is left alone");
            Assert.That(raised, Is.EqualTo(9));
            Assert.That(heights.All(h => Mathf.Abs(h / step - Mathf.Round(h / step)) < 1e-4f), "still on the levels");
        }

        [Test]
        public void RainWearsAHillDownTheSameWayEveryTime()
        {
            const int n = 48;
            float[] Hill()
            {
                var h = new float[n * n];
                for (var z = 0; z < n; z++)
                    for (var x = 0; x < n; x++)
                        h[z * n + x] = Mathf.Max(0f, 12f - Vector2.Distance(new Vector2(x, z), new Vector2(24, 24)) * 0.6f);
                return h;
            }

            var fixedCells = new bool[n * n];
            for (var i = 0; i < n; i++)
                fixedCells[i] = fixedCells[(n - 1) * n + i] = fixedCells[i * n] = fixedCells[i * n + n - 1] = true;
            var first = Hill();
            var second = Hill();
            var untouched = Hill();

            TerrainErosion.Droplets(first, n, n, fixedCells, 3000, 7, TerrainErosion.DropletSettings.Default);
            TerrainErosion.Droplets(second, n, n, fixedCells, 3000, 7, TerrainErosion.DropletSettings.Default);

            Assert.That(first, Is.EqualTo(second), "same seed, same land");
            Assert.That(first.Where((h, i) => Mathf.Abs(h - untouched[i]) > 1e-4f).Count(), Is.GreaterThan(100), "the rain changed the hill");
            Assert.That(first[24 * n + 24], Is.LessThanOrEqualTo(untouched[24 * n + 24] + 1e-3f), "the top was not built up");
            // An uncapped drop speed once let capacity run away and drops drilled the land to
            // -50 km. Nothing should move by more than a few metres.
            Assert.That(first.Select((h, i) => Mathf.Abs(h - untouched[i])).Max(), Is.LessThan(4f), "the rain wears, it does not drill");
            Assert.That(first.Min(), Is.GreaterThanOrEqualTo(-1e-3f), "and never below the lowest ground there was");
            for (var i = 0; i < n; i++)
                Assert.That(first[i], Is.EqualTo(untouched[i]), "the fixed edge never changes");
        }
    }
}
