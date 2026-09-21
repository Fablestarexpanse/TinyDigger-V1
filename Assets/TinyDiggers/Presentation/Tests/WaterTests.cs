using System.Linq;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// The water's rules: what is baked about the land, and the swell. Ronan's ruling is that wave
    /// heights are a random mix, never one height, and no wave may flood a beach.
    /// </summary>
    public class WaterTests
    {
        WaterSettings _settings;

        [SetUp]
        public void SetUp() => _settings = ScriptableObject.CreateInstance<WaterSettings>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        /// <summary>Land for x &lt; <paramref name="shoreX"/>, then sea sloping down one metre per cell.</summary>
        static TerrainGrid HalfIsland(int size, int shoreX)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault(), 1f, -20f);
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    var surface = x < shoreX ? 3f : -Mathf.Min(15f, x - shoreX + 1);
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Sand, surface + 20f) });
                }
            }

            return grid;
        }

        [Test]
        public void ShoreDistanceCountsMetresThroughTheWaterToTheNearestDryCell()
        {
            var field = WaterField.Bake(HalfIsland(40, 10));

            Assert.That(field.ShoreDistanceAt(5, 20), Is.Zero, "dry land is at the shore");
            Assert.That(field.ShoreDistanceAt(10, 20), Is.EqualTo(1f).Within(1e-3f));
            Assert.That(field.ShoreDistanceAt(25, 20), Is.EqualTo(16f).Within(1e-3f));
        }

        [Test]
        public void ShoreDirectionPointsAtTheLand()
        {
            var field = WaterField.Bake(HalfIsland(40, 10));
            var direction = field.ShoreDirectionAt(25, 20);

            Assert.That(direction.x, Is.EqualTo(-1f).Within(1e-3f), $"got {direction}");
            Assert.That(direction.y, Is.EqualTo(0f).Within(1e-3f), $"got {direction}");
        }

        [Test]
        public void DepthIsSmoothedButFollowsTheSeabed()
        {
            var field = WaterField.Bake(HalfIsland(40, 10));

            Assert.That(field.DepthAt(5, 20), Is.LessThan(0f), "dry land has no water over it");
            Assert.That(field.DepthAt(20, 20), Is.EqualTo(11f).Within(0.01f), "on an even slope the blur changes nothing");
            Assert.That(field.DepthAt(30, 20), Is.GreaterThan(field.DepthAt(15, 20)), "deeper out to sea");
        }

        [Test]
        public void TheSwellDiesAwayInTheShallowsAndAtTheShore()
        {
            Assert.That(WaterField.SwellDamping(0f, 20f, _settings.DampDepth, _settings.DampDistance), Is.Zero);
            Assert.That(WaterField.SwellDamping(20f, 0f, _settings.DampDepth, _settings.DampDistance), Is.Zero);
            Assert.That(WaterField.SwellDamping(20f, 50f, _settings.DampDepth, _settings.DampDistance), Is.EqualTo(1f));

            var waves = WaveSet.Generate(_settings);
            for (var t = 0f; t < 10f; t += 0.37f)
            {
                var atShore = WaveSet.Displacement(waves, 3f, 7f, t,
                    WaterField.SwellDamping(0.2f, 0.5f, _settings.DampDepth, _settings.DampDistance));
                Assert.That(Mathf.Abs(atShore.y), Is.LessThan(0.01f), $"at {t} s the water at the shore moved {atShore.y} m");
            }
        }

        [Test]
        public void WaveHeightsAreAMixNotOneHeight()
        {
            for (var seed = 0; seed < 20; seed++)
            {
                _settings.Seed = seed;
                var waves = WaveSet.Generate(_settings);
                var heights = waves.Select(w => w.Amplitude).ToArray();

                Assert.That(heights.Length, Is.EqualTo(_settings.WaveCount));
                Assert.That(heights.Max(), Is.GreaterThanOrEqualTo(3f * heights.Min()),
                    $"seed {seed}: heights {string.Join(", ", heights)} are too alike");
                foreach (var wave in waves)
                {
                    Assert.That(wave.Amplitude, Is.InRange(_settings.MinAmplitude - 1e-4f, _settings.MaxAmplitude + 1e-4f));
                    Assert.That(wave.Amplitude, Is.LessThanOrEqualTo(wave.Wavelength * WaveSet.BreakingRatio + 1e-4f),
                        "no wave taller than water can hold at its length");
                    var off = Vector2.Angle(wave.Direction, new Vector2(Mathf.Cos(_settings.WindDegrees * Mathf.Deg2Rad), Mathf.Sin(_settings.WindDegrees * Mathf.Deg2Rad)));
                    Assert.That(off, Is.LessThanOrEqualTo(_settings.Spread + 0.01f));
                }
            }
        }

        [Test]
        public void TheSameSeedGivesTheSameSeaAndAnotherSeedDoesNot()
        {
            _settings.Seed = 3;
            var first = WaveSet.Generate(_settings);
            var again = WaveSet.Generate(_settings);
            _settings.Seed = 4;
            var other = WaveSet.Generate(_settings);

            Assert.That(again.Select(w => w.Amplitude), Is.EqualTo(first.Select(w => w.Amplitude)));
            Assert.That(other.Select(w => w.Amplitude), Is.Not.EqualTo(first.Select(w => w.Amplitude)));
        }

        [Test]
        public void CrestsNeverFoldOverThemselves()
        {
            _settings.Steepness = 1f;
            for (var seed = 0; seed < 20; seed++)
            {
                _settings.Seed = seed;
                Assert.That(WaveSet.Sharpness(WaveSet.Generate(_settings)), Is.LessThanOrEqualTo(1f + 1e-4f));
            }
        }

        [Test]
        public void BakingAFullMapIsCheap()
        {
            var grid = HalfIsland(512, 200);
            WaterField.Bake(grid); // Warm up.
            var field = WaterField.Bake(grid);

            Assert.That(field.Milliseconds, Is.LessThan(60f), $"baking took {field.Milliseconds:0.0} ms");
        }
    }
}
