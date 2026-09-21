using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.DynamicWater.Tests
{
    /// <summary>
    /// The swell: a mix of heights whatever the seed, never taller than water can hold, never
    /// folding over itself, the same for the same seed, and flat in the shallows.
    /// </summary>
    public class SwellTests
    {
        WaterWaveSettings _settings;

        [SetUp]
        public void SetUp() => _settings = ScriptableObject.CreateInstance<WaterWaveSettings>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        [Test]
        public void EverySeedMixesBigAndSmallWaves()
        {
            for (var seed = 1; seed <= 20; seed++)
            {
                _settings.Seed = seed;
                var waves = WaterWaves.Generate(_settings);
                var max = 0f;
                var min = float.MaxValue;
                foreach (var w in waves)
                {
                    max = Mathf.Max(max, w.Amplitude);
                    min = Mathf.Min(min, w.Amplitude);
                }

                Assert.That(max, Is.GreaterThan(min * 2f), $"seed {seed}: {min:0.00}..{max:0.00} m is one height, not a mix");
            }
        }

        [Test]
        public void NoCrestIsTallerThanWaterCanHoldOrFoldsOver()
        {
            _settings.MaxAmplitude = 5f;
            _settings.MinWavelength = 2f;
            _settings.MaxWavelength = 10f;
            _settings.Steepness = 1f;
            for (var seed = 1; seed <= 20; seed++)
            {
                _settings.Seed = seed;
                var waves = WaterWaves.Generate(_settings);
                foreach (var w in waves)
                    Assert.That(w.Amplitude, Is.LessThanOrEqualTo(w.Wavelength * WaterWaves.BreakingRatio + 1e-5f));
                Assert.That(WaterWaves.Sharpness(waves), Is.LessThanOrEqualTo(1f + 1e-4f));
            }
        }

        [Test]
        public void TheSameSeedMakesTheSameSea()
        {
            var a = WaterWaves.Generate(_settings);
            var b = WaterWaves.Generate(_settings);
            for (var i = 0; i < a.Length; i++)
            {
                Assert.That(b[i].Amplitude, Is.EqualTo(a[i].Amplitude));
                Assert.That(b[i].Direction, Is.EqualTo(a[i].Direction));
            }
        }

        [Test]
        public void TheShallowsLieFlatAndDeepWaterMoves()
        {
            var waves = WaterWaves.Generate(_settings);
            Assert.That(WaterWaves.Damping(_settings, 0f, 10f, 10f), Is.Zero);
            var damping = WaterWaves.Damping(_settings, 20f, 10f, 10f);
            Assert.That(damping, Is.InRange(_settings.GustCalm, 1f));

            var moved = 0f;
            for (var t = 0f; t < 5f; t += 0.25f)
                moved = Mathf.Max(moved, Mathf.Abs(WaterWaves.Displacement(waves, 10f, 10f, t, damping).y));
            Assert.That(moved, Is.GreaterThan(0.05f), "deep water rises and falls");
            Assert.That(WaterWaves.Displacement(waves, 10f, 10f, 3f, 0f).magnitude, Is.Zero, "damped water is still");
        }
    }
}

