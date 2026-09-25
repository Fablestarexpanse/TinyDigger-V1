using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// The hollow map (Ronan, 2026-09-24: darken the hollows like the references): level ground reads
    /// level, a bowl's floor reads as a hollow and a mound's top as a ridge, and recomputing one patch
    /// gives what the whole map gave there.
    /// </summary>
    public class HollowsTests
    {
        const int Size = 64;

        static float[] Ground(System.Func<int, int, float> height)
        {
            var heights = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    heights[z * Size + x] = height(x, z);
            return heights;
        }

        static byte[] Whole(float[] heights)
        {
            var into = new byte[Size * Size];
            Hollows.Compute(heights, Size, Size, new RectInt(0, 0, Size, Size), 2, 6, 15, 1f, into);
            return into;
        }

        [Test]
        public void LevelGroundAndAnEvenSlopeReadLevel()
        {
            var flat = Whole(Ground((x, z) => 5f));
            Assert.That(flat[32 * Size + 32], Is.EqualTo(128));

            // A plane: every average of it equals the middle, so nothing is a hollow (away from the edge).
            var slope = Whole(Ground((x, z) => x * 0.25f));
            Assert.That(slope[32 * Size + 32], Is.InRange(126, 130));
        }

        [Test]
        public void HalfMetreTerraceStepsDoNotReadAsHollows()
        {
            // An even slope stored in half-metre steps, a step every four cells: the steps are the
            // grid's, not the land's, and must not come out as bands.
            var steps = Whole(Ground((x, z) => Mathf.Floor(x / 4f) * 0.5f));
            for (var x = 24; x < 40; x++)
                Assert.That(steps[32 * Size + x], Is.InRange(118, 138), $"x {x}");
        }

        [Test]
        public void ABowlFloorIsAHollowAndAMoundTopIsARidge()
        {
            float Bowl(int x, int z) => Mathf.Min(4f, Vector2.Distance(new Vector2(x, z), new Vector2(32, 32)) * 0.3f);
            var bowl = Whole(Ground(Bowl));
            var mound = Whole(Ground((x, z) => -Bowl(x, z)));

            Assert.That(bowl[32 * Size + 32], Is.GreaterThan(200), "deep in the bowl");
            Assert.That(mound[32 * Size + 32], Is.LessThan(56), "on top of the mound");
        }

        [Test]
        public void APatchRecomputedMatchesTheWholeMap()
        {
            var heights = Ground((x, z) => Mathf.Sin(x * 0.3f) * 2f + Mathf.Cos(z * 0.21f) * 1.5f);
            var whole = Whole(heights);
            var patch = new byte[Size * Size];
            Hollows.Compute(heights, Size, Size, new RectInt(20, 10, 15, 30), 2, 6, 15, 1f, patch);
            for (var z = 10; z < 40; z++)
                for (var x = 20; x < 35; x++)
                    Assert.That(patch[z * Size + x], Is.EqualTo(whole[z * Size + x]).Within(1), $"({x}, {z})");
        }
    }
}
