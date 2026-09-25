using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// Height stamps (Ronan, 2026-09-24: one library for the generator and the terraform tool):
    /// a placed stamp becomes target heights per cell, rounded to the height step, eased into the
    /// ground at its rim, turned, flipped and blended four ways.
    /// </summary>
    public class StampRasterTests
    {
        const float CellSize = 0.5f;
        const float Step = 0.25f;
        const int Map = 200;

        readonly List<HeightStamp> _made = new List<HeightStamp>();

        [TearDown]
        public void TearDown()
        {
            foreach (var stamp in _made)
                Object.DestroyImmediate(stamp);
            _made.Clear();
        }

        HeightStamp Stamp(System.Func<float, float, float> shape, StampBlend blend = StampBlend.Add, float falloff = 0.2f, int resolution = 65)
        {
            var stamp = ScriptableObject.CreateInstance<HeightStamp>();
            stamp.Blend = blend;
            stamp.EdgeFalloff = falloff;
            var heights = new float[resolution * resolution];
            for (var z = 0; z < resolution; z++)
                for (var x = 0; x < resolution; x++)
                    heights[z * resolution + x] = shape(x / (resolution - 1f), z / (resolution - 1f));
            stamp.SetHeights(resolution, heights);
            _made.Add(stamp);
            return stamp;
        }

        static Dictionary<Vector2Int, float> Rasterise(HeightStamp stamp, StampPlacement placement, System.Func<int, int, float> ground = null)
        {
            ground ??= (x, z) => 0f;
            var cells = new Dictionary<Vector2Int, float>();
            StampRaster.Apply(stamp, placement, CellSize, Step,
                (x, z) => x >= 0 && z >= 0 && x < Map && z < Map, ground,
                (x, z, target) => cells[new Vector2Int(x, z)] = target);
            return cells;
        }

        static StampPlacement At(float x, float z, float size = 20f, float height = 4f) =>
            new StampPlacement { Centre = new Vector2(x, z), Size = size, Height = height };

        [Test]
        public void AFlatTopStampRaisesItsMiddleByItsHeightAndEasesToNothingAtTheRim()
        {
            var cells = Rasterise(Stamp((u, v) => 1f), At(100f, 100f));

            Assert.That(cells[new Vector2Int(100, 100)], Is.EqualTo(4f), "full height in the middle");
            // 20 m across on 0.5 m cells is a 20-cell radius; nothing reaches past it.
            foreach (var cell in cells.Keys)
                Assert.That(Vector2.Distance(cell + new Vector2(0.5f, 0.5f), new Vector2(100f, 100f)), Is.LessThan(20f), $"{cell} is outside the stamp");
            // Easing in: a cell near the rim is lower than one inside it.
            Assert.That(cells.TryGetValue(new Vector2Int(118, 100), out var rim) ? rim : 0f, Is.LessThan(cells[new Vector2Int(110, 100)]));
        }

        [Test]
        public void TargetsAreWholeHeightStepsAndUnchangedCellsAreLeftOut()
        {
            var cells = Rasterise(Stamp((u, v) => 0.37f), At(100f, 100f, height: 3.3f));
            Assert.That(cells.Count, Is.GreaterThan(0));
            foreach (var target in cells.Values)
            {
                Assert.That(target / Step, Is.EqualTo(Mathf.Round(target / Step)).Within(1e-4f), $"{target} is not a whole step");
                Assert.That(Mathf.Abs(target), Is.GreaterThanOrEqualTo(Step * 0.5f), "a cell that does not change is not given");
            }
        }

        [Test]
        public void TurningItAQuarterMovesTheHighSideClockwise()
        {
            // High on the stamp's east side (u = 1), nothing on its west.
            var ramp = Stamp((u, v) => u, falloff: 0.05f);
            var east = new Vector2Int(112, 100);
            var south = new Vector2Int(100, 87);

            var plain = Rasterise(ramp, At(100f, 100f));
            Assert.That(plain.ContainsKey(east) ? plain[east] : 0f, Is.GreaterThan(2f), "high to the east unturned");

            var turned = At(100f, 100f);
            turned.Rotation = 90f;
            var quarter = Rasterise(ramp, turned);
            Assert.That(quarter.ContainsKey(south) ? quarter[south] : 0f, Is.GreaterThan(2f), "high to the south turned 90° clockwise");
            Assert.That(quarter.ContainsKey(east) ? quarter[east] : 0f, Is.LessThan(2.5f).And.GreaterThan(1.5f), "the old high side is half way now");

            turned.Rotation = 360f;
            var full = Rasterise(ramp, turned);
            foreach (var pair in plain)
                Assert.That(full[pair.Key], Is.EqualTo(pair.Value), "a whole turn is no turn");
        }

        [Test]
        public void InvertedAMoundIsAHollowAsDeep()
        {
            var mound = Stamp((u, v) => 1f);
            var hollow = At(100f, 100f);
            hollow.Invert = true;

            var up = Rasterise(mound, At(100f, 100f));
            var down = Rasterise(mound, hollow);
            Assert.That(down.Count, Is.EqualTo(up.Count));
            foreach (var pair in up)
                Assert.That(down[pair.Key], Is.EqualTo(-pair.Value), $"{pair.Key}");
        }

        [Test]
        public void AddFollowsASlopeMaxOnlyRaisesMinOnlyCutsReplaceSetsTheSurface()
        {
            // Ground rising 0.1 m a cell eastward, 0 at the middle.
            float Slope(int x, int z) => Mathf.Round((x - 100) * 0.1f / Step) * Step;
            var middle = new Vector2Int(100, 100);
            var east = new Vector2Int(108, 100);
            var west = new Vector2Int(92, 100);

            var add = Rasterise(Stamp((u, v) => 1f), At(100f, 100f, height: 2f), Slope);
            Assert.That(add[east] - add[west], Is.EqualTo(Slope(108, 100) - Slope(92, 100)), "Add keeps the lie of the land");

            // A 2 m plateau standing on level 0: raises the west, leaves the east (already 0.75 m and
            // rising, but under 2 m) raised too, and never lowers anything.
            var max = Stamp((u, v) => 1f, StampBlend.Max);
            var mesa = Rasterise(max, At(100f, 100f, height: 2f), Slope);
            foreach (var pair in mesa)
                Assert.That(pair.Value, Is.GreaterThan(Slope(pair.Key.x, pair.Key.y)), "Max never lowers");
            Assert.That(mesa[middle], Is.EqualTo(2f));

            var min = Stamp((u, v) => 0f, StampBlend.Min);
            var cut = Rasterise(min, At(100f, 100f, height: 2f), Slope);
            foreach (var pair in cut)
                Assert.That(pair.Value, Is.LessThan(Slope(pair.Key.x, pair.Key.y)), "Min never raises");
            Assert.That(cut.ContainsKey(west), Is.False, "west of the middle is below the base already");
            Assert.That(cut[east], Is.EqualTo(0f), "cut back to the base level");

            var replace = Rasterise(Stamp((u, v) => 0.5f, StampBlend.Replace), new StampPlacement
            {
                Centre = new Vector2(100f, 100f), Size = 20f, Height = 2f, Base = 1f,
            }, Slope);
            Assert.That(replace[east], Is.EqualTo(2f), "base 1 m plus half of 2 m, whatever was there");
            Assert.That(replace[west], Is.EqualTo(2f));
        }

        [Test]
        public void OffTheMapIsSkipped()
        {
            var cells = Rasterise(Stamp((u, v) => 1f), At(2f, 2f));
            Assert.That(cells.Count, Is.GreaterThan(0));
            foreach (var cell in cells.Keys)
                Assert.That(cell.x >= 0 && cell.y >= 0, Is.True, $"{cell}");
        }
    }
}
