using PromptWaffle.Terrain;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>Natural terrain, phase 2: each seed's mix of plains, hills and mountains, and where they lie.</summary>
    public class LandTypesTests
    {
        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp() => _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        [Test]
        public void EachSeedDrawsItsOwnMixWithinTheRangesAddingUpToOne()
        {
            var mixes = Enumerable.Range(0, 30).Select(seed => LandTypes.Draw(new System.Random(seed), _settings)).ToList();

            foreach (var mix in mixes)
            {
                Assert.That(mix.Plains + mix.Hills + mix.Mountains, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(mix.Plains, Is.InRange(_settings.PlainsShareMin - 1e-4f, _settings.PlainsShareMax + 1e-4f));
                Assert.That(mix.Mountains, Is.InRange(_settings.MountainShareMin - 1e-4f, _settings.MountainShareMax + 1e-4f));
                Assert.That(mix.Hills, Is.GreaterThanOrEqualTo(0.15f - 1e-4f), "hills always get a share");
            }

            Assert.That(mixes.Select(m => Mathf.Round(m.Plains * 100f)).Distinct().Count(), Is.GreaterThan(10), "the mix varies by seed");
        }

        [Test]
        public void HillsKeepAShareEvenWhenTheRangesAskForTooMuch()
        {
            _settings.PlainsShareMin = _settings.PlainsShareMax = 0.7f;
            _settings.MountainShareMin = _settings.MountainShareMax = 0.4f;

            var mix = LandTypes.Draw(new System.Random(1), _settings);

            Assert.That(mix.Hills, Is.EqualTo(0.15f).Within(1e-4f));
            Assert.That(mix.Plains / mix.Mountains, Is.EqualTo(0.7f / 0.4f).Within(1e-3f), "the other two shrink in proportion");
        }

        [Test]
        public void TheCutsSplitTheFieldIntoTheMix()
        {
            var values = Enumerable.Range(0, 1000).Select(i => i / 999f).ToArray();
            var mix = new LandMix(0.4f, 0.4f, 0.2f);

            var (plainsBelow, mountainsAbove) = LandTypes.Thresholds(values, values.Length, mix);

            Assert.That(values.Count(v => v < plainsBelow) / 1000f, Is.EqualTo(0.4f).Within(0.01f));
            Assert.That(values.Count(v => v > mountainsAbove) / 1000f, Is.EqualTo(0.2f).Within(0.01f));
        }

        [Test]
        public void WeightsAddUpToOneAndBlendAcrossEachBorder()
        {
            for (var v = -0.2f; v <= 1.2f; v += 0.01f)
            {
                var (p, h, m) = LandTypes.Weights(v, 0.4f, 0.8f, 0.05f);
                Assert.That(p + h + m, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(p, Is.InRange(0f, 1f));
                Assert.That(m, Is.InRange(0f, 1f));
            }

            Assert.That(LandTypes.Weights(0.1f, 0.4f, 0.8f, 0.05f).plains, Is.EqualTo(1f), "deep in a plain");
            Assert.That(LandTypes.Weights(0.6f, 0.4f, 0.8f, 0.05f).hills, Is.EqualTo(1f), "between the cuts is hills");
            Assert.That(LandTypes.Weights(1f, 0.4f, 0.8f, 0.05f).mountains, Is.EqualTo(1f), "deep in the range");
            var border = LandTypes.Weights(0.4f, 0.4f, 0.8f, 0.05f);
            Assert.That(border.plains, Is.EqualTo(0.5f).Within(1e-3f), "half and half on the border");
        }
    }
}
