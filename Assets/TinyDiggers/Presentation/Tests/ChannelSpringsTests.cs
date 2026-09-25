using NUnit.Framework;
using PromptWaffle.Terrain;
using PromptWaffle.Terrain.Generation;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// Rivers and creeks, step 2: a spring at every channel's head, and the beds filled to their
    /// running depth when the water starts, rivers too deep to wade and creeks shallow enough.
    /// </summary>
    public class ChannelSpringsTests
    {
        static Channel Straight(ChannelKind kind, float width, float depth, float floor, int fromX, int toX, int z)
        {
            var channel = new Channel { Kind = kind, Width = width, Depth = depth, Catchment = 20000f };
            for (var x = fromX; x <= toX; x += 2)
                channel.Path.Add(new Vector3(x + 0.5f, floor, z + 0.5f));
            return channel;
        }

        [Test]
        public void ARiverRunsTooDeepToWadeAndACreekDoesNot()
        {
            var wade = new TerrainGrid(4, 4, MaterialTable.CreateDefault()).DeepWater;
            var river = new Channel { Kind = ChannelKind.River, Depth = 1f };
            var creek = new Channel { Kind = ChannelKind.Creek, Depth = 0.5f };

            Assert.That(ChannelSprings.FillDepth(river, ChannelSprings.DefaultRiverFill, ChannelSprings.DefaultCreekFill),
                Is.GreaterThan(wade), "the shallowest river still blocks the crew");
            Assert.That(ChannelSprings.FillDepth(creek, ChannelSprings.DefaultRiverFill, ChannelSprings.DefaultCreekFill),
                Is.LessThan(wade), "a creek is waded");
        }

        [Test]
        public void ASpringGivesMoreForAWiderChannelAndABiggerCatchment()
        {
            var narrow = new Channel { Kind = ChannelKind.River, Width = 3f, Catchment = 20000f };
            var wide = new Channel { Kind = ChannelKind.River, Width = 6f, Catchment = 20000f };
            var big = new Channel { Kind = ChannelKind.River, Width = 3f, Catchment = 80000f };
            var huge = new Channel { Kind = ChannelKind.River, Width = 3f, Catchment = 8_000_000f };

            var rate = ChannelSprings.SpringRate(narrow, 1f, 0.4f, 20000f);
            Assert.That(rate, Is.EqualTo(3f * 1f * 0.4f).Within(1e-4f), "width times depth times speed, at the reference catchment");
            Assert.That(ChannelSprings.SpringRate(wide, 1f, 0.4f, 20000f), Is.EqualTo(rate * 2f).Within(1e-4f));
            var small = new Channel { Kind = ChannelKind.River, Width = 3f, Catchment = 20000f * 0.36f };
            Assert.That(ChannelSprings.SpringRate(small, 1f, 0.4f, 20000f), Is.EqualTo(rate * 0.6f).Within(1e-4f), "by the square root of the catchment");
            Assert.That(ChannelSprings.SpringRate(big, 1f, 0.4f, 20000f), Is.GreaterThan(rate), "a bigger catchment gives more");
            Assert.That(ChannelSprings.SpringRate(huge, 1f, 0.4f, 20000f), Is.EqualTo(rate * ChannelSprings.MaxCatchmentScale).Within(1e-4f),
                "but never more than the cap, or the beds overflow");
        }

        [Test]
        public void PrefillFillsTheBedToItsRunningDepthAndNoWider()
        {
            const int size = 20;
            var depths = new float[size * size];
            // A bed three cells across along row 10.
            var river = Straight(ChannelKind.River, 3f, 1f, 4f, 2, 16, 10);

            var raised = ChannelSprings.Prefill(depths, size, size, 1f, new[] { river }, 0.75f, 0.25f);

            Assert.That(raised, Is.GreaterThan(0));
            Assert.That(depths[10 * size + 8], Is.EqualTo(0.75f).Within(1e-4f), "the middle of the bed");
            Assert.That(depths[9 * size + 8], Is.EqualTo(0.75f).Within(1e-4f), "its edge");
            Assert.That(depths[7 * size + 8], Is.Zero, "the bank beyond stays dry");
            Assert.That(depths[10 * size + 19], Is.Zero, "past the mouth stays dry");
        }

        [Test]
        public void PrefillOnASteepReachNeverStandsDeeperThanTheFill()
        {
            // A creek dropping 4 m every two cells, its bed stepping down with it.
            const int size = 20;
            var depths = new float[size * size];
            var creek = new Channel { Kind = ChannelKind.Creek, Width = 1f, Depth = 0.5f };
            for (var x = 2; x <= 14; x += 2)
                creek.Path.Add(new Vector3(x + 0.5f, 40f - x * 2f, 10.5f));

            ChannelSprings.Prefill(depths, size, size, 1f, new[] { creek }, 0.75f, 0.25f);

            for (var x = 0; x < size; x++)
                Assert.That(depths[10 * size + x], Is.LessThanOrEqualTo(0.25f + 1e-4f), $"cell {x} was filled deeper than a creek runs");
        }

        [Test]
        public void PrefillNeverLowersWaterAlreadyThere()
        {
            const int size = 20;
            var depths = new float[size * size];
            depths[10 * size + 8] = 3f; // The sea, say.
            var creek = Straight(ChannelKind.Creek, 1f, 0.5f, 4f, 2, 16, 10);

            ChannelSprings.Prefill(depths, size, size, 1f, new[] { creek }, 0.75f, 0.25f);

            Assert.That(depths[10 * size + 8], Is.EqualTo(3f));
            Assert.That(depths[10 * size + 6], Is.EqualTo(0.25f).Within(1e-4f));
        }
    }
}
