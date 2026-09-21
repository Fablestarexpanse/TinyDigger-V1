using NUnit.Framework;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// How full a spillway's falling water is drawn: dry below the noise floor, full at the full
    /// head, never past full, and always more for more water over the crest.
    /// </summary>
    public class SpillwaySheetTests
    {
        [Test]
        public void ADrySpillwayDrawsNoSheet()
        {
            Assert.That(DamSpillways.SheetFullness(0f, 0.4f), Is.EqualTo(0f));
            Assert.That(DamSpillways.SheetFullness(-0.3f, 0.4f), Is.EqualTo(0f), "below the crest");
            Assert.That(DamSpillways.SheetFullness(DamSpillways.MinHead * 0.5f, 0.4f), Is.EqualTo(0f), "simulation noise");
        }

        [Test]
        public void TheSheetIsFullAtTheFullHeadAndNoFuller()
        {
            Assert.That(DamSpillways.SheetFullness(0.4f, 0.4f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(DamSpillways.SheetFullness(2f, 0.4f), Is.EqualTo(1f));
        }

        [Test]
        public void MoreWaterOverTheCrestDrawsAFullerSheet()
        {
            var last = 0f;
            for (var head = 0.01f; head <= 0.4f; head += 0.01f)
            {
                var fullness = DamSpillways.SheetFullness(head, 0.4f);
                Assert.That(fullness, Is.GreaterThan(last), $"at {head} m");
                last = fullness;
            }

            // A trickle still shows: a tenth of the full head draws well over a tenth of a sheet.
            Assert.That(DamSpillways.SheetFullness(0.04f, 0.4f), Is.GreaterThan(0.15f));
        }
    }
}
