using NUnit.Framework;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>
    /// The stamp in hand (Ronan, 2026-09-24: "make sure we have tools to rotate scale etc"): every
    /// way of sizing, turning, raising and flipping it stays in range, and Reset puts it back as the
    /// stamp comes. The keys, the wheel and the panel all land on these.
    /// </summary>
    public class StampDraftTests
    {
        HeightStamp _stamp;
        LandformDraft _draft;

        [SetUp]
        public void SetUp()
        {
            _stamp = ScriptableObject.CreateInstance<HeightStamp>();
            _stamp.name = "mound";
            _stamp.NativeSize = 40f;
            _stamp.NativeHeight = 6f;
            _stamp.SetHeights(2, new[] { 1f, 1f, 1f, 1f });
            _draft = new LandformDraft();
            _draft.SetKind(LandformKind.Stamp);
            _draft.SetStamp(_stamp);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_stamp);

        [Test]
        public void ItComesAtItsOwnSizeAndHeight()
        {
            Assert.That(_draft.Form.Placement.Size, Is.EqualTo(40f));
            Assert.That(_draft.Form.Placement.Height, Is.EqualTo(6f));
        }

        [Test]
        public void SizeStaysBetweenFourAndFourHundredMetres()
        {
            _draft.SetStampSize(1f);
            Assert.That(_draft.Form.Placement.Size, Is.EqualTo(4f));
            for (var i = 0; i < 100; i++)
                _draft.ScaleStamp(1.1f);
            Assert.That(_draft.Form.Placement.Size, Is.EqualTo(400f));
            _draft.SetStampSize(72.5f);
            Assert.That(_draft.Form.Placement.Size, Is.EqualTo(72.5f));
        }

        [Test]
        public void TurningComesRoundAndTypedDegreesAreBroughtIntoRange()
        {
            _draft.TurnStamp(-15f);
            Assert.That(_draft.Form.Placement.Rotation, Is.EqualTo(345f));
            _draft.SetStampTurn(400f);
            Assert.That(_draft.Form.Placement.Rotation, Is.EqualTo(40f).Within(1e-3f));
        }

        [Test]
        public void HeightNeverGoesUnderHalfAMetre()
        {
            _draft.RaiseStamp(-100f);
            Assert.That(_draft.Form.Placement.Height, Is.EqualTo(0.5f));
            _draft.SetStampHeight(12f);
            Assert.That(_draft.Form.Placement.Height, Is.EqualTo(12f));
        }

        [Test]
        public void ResetPutsItBackAsItComes()
        {
            _draft.SetStampSize(90f);
            _draft.SetStampTurn(120f);
            _draft.SetStampHeight(2f);
            _draft.FlipStamp();
            var version = _draft.Version;

            _draft.ResetStamp();

            var placement = _draft.Form.Placement;
            Assert.That(placement.Size, Is.EqualTo(40f));
            Assert.That(placement.Height, Is.EqualTo(6f));
            Assert.That(placement.Rotation, Is.EqualTo(0f));
            Assert.That(placement.Invert, Is.False);
            Assert.That(_draft.Version, Is.GreaterThan(version), "the ghost is worked out again");
        }

        [Test]
        public void AHollowComesUpsideDownAndResetKeepsItSo()
        {
            var canyon = ScriptableObject.CreateInstance<HeightStamp>();
            canyon.name = "canyon";
            canyon.Negative = true;
            canyon.SetHeights(2, new[] { 1f, 1f, 1f, 1f });
            try
            {
                _draft.SetStamp(canyon);
                Assert.That(_draft.Form.Placement.Invert, Is.True, "a canyon digs as it comes");
                _draft.FlipStamp();
                _draft.ResetStamp();
                Assert.That(_draft.Form.Placement.Invert, Is.True, "and Reset puts it back to digging");

                _draft.SetStamp(_stamp);
                Assert.That(_draft.Form.Placement.Invert, Is.False, "a mound picked after it builds");
            }
            finally
            {
                Object.DestroyImmediate(canyon);
            }
        }

        [Test]
        public void SizeTurnAndHeightCarryOverToTheNextStamp()
        {
            _draft.SetStampSize(90f);
            _draft.SetStampTurn(30f);
            _draft.Clear();
            Assert.That(_draft.Form.Placement.Size, Is.EqualTo(90f));
            Assert.That(_draft.Form.Placement.Rotation, Is.EqualTo(30f));
            Assert.That(_draft.Form.StampName, Is.EqualTo("mound"));
        }
    }
}
