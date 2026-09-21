using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// The dam ring: it closes with no gap or overlap, the terminals sit on the compass points,
    /// every gap holds the features asked for, the same seed lays it out the same way, and bent
    /// neighbours meet exactly.
    /// </summary>
    public class DamLayoutTests
    {
        const float Radius = 259f;

        DamSettings _settings;

        [SetUp]
        public void SetUp() => _settings = ScriptableObject.CreateInstance<DamSettings>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        static float Circumference => 2f * Mathf.PI * Radius;

        [Test]
        public void TheRingClosesWithNoGapOrOverlap()
        {
            var slots = DamLayout.Build(_settings, Radius, 7).OrderBy(s => s.Start).ToList();

            Assert.That(slots.Sum(s => s.Width), Is.EqualTo(Circumference).Within(0.01f), "widths add up to the circle");
            for (var i = 0; i < slots.Count; i++)
            {
                var next = slots[(i + 1) % slots.Count];
                var end = (slots[i].Start + slots[i].Width) % Circumference;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(end / Radius * Mathf.Rad2Deg, next.Start / Radius * Mathf.Rad2Deg)),
                    Is.LessThan(0.01f), $"piece {i} ends where the next begins");
            }
        }

        [Test]
        public void PiecesAreStretchedOnlyALittle()
        {
            foreach (var slot in DamLayout.Build(_settings, Radius, 7))
                Assert.That(slot.Stretch, Is.InRange(0.98f, 1.02f), "within half a bay spread over a gap");
        }

        [Test]
        public void FourTerminalsSitOnTheCompassPoints()
        {
            var terminals = DamLayout.Build(_settings, Radius, 7).Where(s => s.Kind == DamPieceKind.Terminal).ToList();

            Assert.That(terminals.Count, Is.EqualTo(4));
            var angles = terminals.Select(t => Mathf.Repeat(t.Centre / Radius * Mathf.Rad2Deg, 360f)).OrderBy(a => a).ToList();
            for (var i = 0; i < 4; i++)
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(angles[i], i * 90f)), Is.LessThan(0.01f));
        }

        [Test]
        public void EveryGapHoldsItsFeatures()
        {
            var slots = DamLayout.Build(_settings, Radius, 7);

            Assert.That(slots.Count(s => s.Kind == DamPieceKind.Spillway), Is.EqualTo(4 * _settings.SpillwaysPerGap));
            Assert.That(slots.Count(s => s.Kind == DamPieceKind.Pad), Is.EqualTo(4 * _settings.PadsPerGap));
            Assert.That(slots.Count(s => s.Kind == DamPieceKind.Tower), Is.EqualTo(4 * _settings.TowersPerGap));
        }

        [Test]
        public void NoTwoFeaturesTouch()
        {
            var slots = DamLayout.Build(_settings, Radius, 3).OrderBy(s => s.Start).ToList();
            for (var i = 0; i < slots.Count; i++)
            {
                var a = slots[i].Kind;
                var b = slots[(i + 1) % slots.Count].Kind;
                Assert.That(a == DamPieceKind.Bay || b == DamPieceKind.Bay, Is.True, $"{a} next to {b} at piece {i}");
            }
        }

        [Test]
        public void TheSameSeedLaysItOutTheSameWay()
        {
            var a = DamLayout.Build(_settings, Radius, 11);
            var b = DamLayout.Build(_settings, Radius, 11);
            Assert.That(b.Select(s => (s.Kind, s.Start)), Is.EqualTo(a.Select(s => (s.Kind, s.Start))));
        }

        [Test]
        public void BentNeighboursMeetExactlyAtEveryHeightAndDepth()
        {
            // A bay's +x end (kit -x in Unity) against the next bay's -x end, at the crest and
            // out at the footing: the same point once bent.
            var width = _settings.BayWidth;
            var stretch = 1.004f;
            var first = 0.3f;
            var second = first + width * stretch / Radius;
            foreach (var (up, outward) in new[] { (2f, 0f), (-20f, 20f), (6f, 9f) })
            {
                var endOfFirst = DamBend.Point(new Vector3(-width / 2f, up, -outward), first, Radius, stretch);
                var startOfSecond = DamBend.Point(new Vector3(width / 2f, up, -outward), second, Radius, stretch);
                Assert.That(Vector3.Distance(endOfFirst, startOfSecond), Is.LessThan(1e-3f), $"up {up}, out {outward}");
            }
        }

        [Test]
        public void TheInnerFaceLandsOnTheRadiusAndOutIsOutward()
        {
            var inner = DamBend.Point(Vector3.zero, 0f, Radius, 1f);
            var outer = DamBend.Point(new Vector3(0f, 0f, -20f), 0f, Radius, 1f);

            Assert.That(inner, Is.EqualTo(new Vector3(0f, 0f, Radius)).Using(Vector3EqualityComparer.Instance));
            Assert.That(outer.z, Is.EqualTo(Radius + 20f).Within(1e-3f), "kit -z is outward, north at angle 0");
        }

        sealed class Vector3EqualityComparer : System.Collections.Generic.IEqualityComparer<Vector3>
        {
            public static readonly Vector3EqualityComparer Instance = new Vector3EqualityComparer();
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
            public int GetHashCode(Vector3 v) => 0;
        }
    }
}
