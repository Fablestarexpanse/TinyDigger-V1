using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Terrain.Tests
{
    /// <summary>The scorecard the natural-terrain work is tuned against: each number means what it says.</summary>
    public class TerrainScorecardTests
    {
        const float Half = 0.5f;

        /// <summary>Half-metre cells and steps, like the game, with every column's surface from <paramref name="height"/>.</summary>
        static TerrainGrid Field(int size, System.Func<int, int, float> height)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault(), Half, -10f, Half);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, height(x, z) + 10f) });
            return grid;
        }

        [Test]
        public void FlatLandIsAllFlatWithNoPitsOrCliffs()
        {
            var card = TerrainScorecard.Measure(Field(20, (x, z) => 3f));

            Assert.That(card.LandCells, Is.EqualTo(400));
            Assert.That(card.LandSquareMetres, Is.EqualTo(100f).Within(1e-3f));
            Assert.That(card.Flat, Is.EqualTo(1f));
            Assert.That(card.Pits, Is.Zero);
            Assert.That(card.CliffSteps, Is.Zero);
            Assert.That(card.Highest, Is.EqualTo(3f));
        }

        [Test]
        public void AHollowCountsAsAPitAndADeepOneAsDeep()
        {
            var card = TerrainScorecard.Measure(Field(20, (x, z) =>
                x == 5 && z == 5 ? 2.5f : x == 12 && z == 12 ? 1f : 3f));

            Assert.That(card.Pits, Is.EqualTo(2));
            Assert.That(card.DeepPits, Is.EqualTo(1), "only the 2 m hollow is a metre deep or more");
        }

        [Test]
        public void SlopesFallInTheirBands()
        {
            // 0.5 m rise per 2 m is 14°: gentle. 1 m per metre is 45°: steep.
            var gentle = TerrainScorecard.Measure(Field(20, (x, z) => 2f + x * Half * 0.25f));
            var steep = TerrainScorecard.Measure(Field(20, (x, z) => 2f + x * Half));

            Assert.That(gentle.Gentle, Is.GreaterThan(0.8f));
            Assert.That(steep.Steep, Is.GreaterThan(0.8f));
        }

        [Test]
        public void ACoastRunningIntoTheSeaIsBeachAndAStepIsNot()
        {
            // Sea for x < 5, then land. One coast rises a step at a time; the other stands 3 m up.
            var beach = TerrainScorecard.Measure(Field(20, (x, z) => x < 5 ? -1f : (x - 5) * 0.25f));
            var cliff = TerrainScorecard.Measure(Field(20, (x, z) => x < 5 ? -1f : 3f));

            Assert.That(beach.CoastCells, Is.EqualTo(20));
            Assert.That(beach.BeachCoast, Is.EqualTo(1f));
            Assert.That(cliff.CoastCells, Is.EqualTo(20));
            Assert.That(cliff.BeachCoast, Is.Zero);
            Assert.That(cliff.CliffSteps, Is.GreaterThan(0f));
        }
    }
}
