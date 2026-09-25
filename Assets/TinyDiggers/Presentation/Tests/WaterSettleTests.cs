using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// Rivers and creeks, step 4: the island settles its water while it loads, then keeps only
    /// what belongs — the sea and the channel beds. Everything else is taken off, and off the beds
    /// a film soaks away instead of standing (Ronan, 2026-09-22).
    /// </summary>
    public class WaterSettleTests
    {
        const int Size = 8;

        static (TerrainGrid grid, IslandMap island) Island(float landHeight)
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, landHeight) });

            var island = new IslandMap { ChannelBeds = new byte[Size * Size] };
            return (grid, island);
        }

        [Test]
        public void TheSeaAndTheBedsAreKeptAndNothingElseIs()
        {
            // The island's own sea level is 0 m, which a column cannot sit under in a test grid,
            // so the shore is put at 2 m here: the mask takes the level it is given.
            const float sea = 2f;
            var (grid, island) = Island(6f);
            // A seabed cell at (1, 1) and a channel bed cell at (5, 5).
            grid.SetColumn(1, 1, new[] { new Layer(MaterialTable.Bedrock, 1f) });
            island.ChannelBeds[5 * Size + 5] = 1;

            var keep = new bool[Size * Size];
            var kept = WaterSettle.BuildKeepMask(keep, grid, island, sea, bedMargin: 0);

            Assert.That(kept, Is.EqualTo(2));
            Assert.That(keep[1 * Size + 1], Is.True, "the sea's basin");
            Assert.That(keep[5 * Size + 5], Is.True, "a channel bed");
            Assert.That(keep[3 * Size + 3], Is.False, "open ground");
        }

        [Test]
        public void ABedKeepsTheCellsEitherSideOfItToo()
        {
            var (grid, island) = Island(6f);
            island.ChannelBeds[5 * Size + 5] = 2;

            var keep = new bool[Size * Size];
            var kept = WaterSettle.BuildKeepMask(keep, grid, island, 2f, WaterSettle.DefaultBedMargin);

            Assert.That(kept, Is.EqualTo(9), "the bed cell and the ring around it");
            Assert.That(keep[4 * Size + 4], Is.True, "a stream wanders outside the band it was cut in");
            Assert.That(keep[3 * Size + 3], Is.False, "two cells out is open ground again");
        }

        [Test]
        public void ZapTakesTheStandingWaterOffAndLeavesTheRest()
        {
            var keep = new[] { true, false, true, false, false };
            var depths = new[] { 2f, 0.3f, 0.1f, 0f, 1.5f };

            var cleared = WaterSettle.Zap(depths, keep, out var metres);

            Assert.That(cleared, Is.EqualTo(2), "the two wet cells that are not kept");
            Assert.That(metres, Is.EqualTo(1.8f).Within(1e-4f));
            Assert.That(depths, Is.EqualTo(new[] { 2f, 0f, 0.1f, 0f, 0f }).AsCollection.Within(1e-4f));
        }

        [Test]
        public void AFilmSoaksEverywhereTheWaterIsNotKept()
        {
            var keep = new[] { true, false, true, false };
            var soaks = new bool[keep.Length];

            WaterSettle.SoakMask(keep, soaks);

            Assert.That(soaks, Is.EqualTo(new[] { false, true, false, true }).AsCollection);
        }

        [Test]
        public void StrayWaterIsWhatStandsOffTheBeds()
        {
            var keep = new[] { true, false, false, false };
            var depths = new[] { 3f, 0.4f, 0.05f, 0f };

            Assert.That(WaterSettle.CountStray(depths, keep, WaterSettle.DefaultSoakDepth), Is.EqualTo(1),
                "the sea is kept, a film does not count, and dry ground does not");
        }

        [Test]
        public void SteadyIsStrayWaterThatStoppedSpreading()
        {
            Assert.That(WaterSettle.Steady(100, -1, 32), Is.False, "the first batch has nothing to compare with");
            Assert.That(WaterSettle.Steady(100, 110, 32), Is.True, "a few cells either way is settled");
            Assert.That(WaterSettle.Steady(100, 400, 32), Is.False, "still spreading");
        }

        [Test]
        public void TheSoakedFilmIsShallowerThanTheCrewWades()
        {
            var grid = new TerrainGrid(4, 4, MaterialTable.CreateDefault());
            Assert.That(WaterSettle.DefaultSoakDepth, Is.LessThan(grid.DeepWater),
                "only a film soaks; anything the player floods stays");
            Assert.That(WaterSettle.DefaultSoakRate, Is.GreaterThan(0f));
        }
    }
}
