using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.DynamicWater.Tests
{
    /// <summary>
    /// The shore field (2026-09-24): metres from each patch of water to the nearest dry ground,
    /// capped at the reach, and it follows the water when the water moves.
    /// </summary>
    public class ShoreDistanceTests
    {
        const float Cell = 0.5f;

        /// <summary>A 32 m zone, dry land on the west half and 1 m of water on the east.</summary>
        static WaterSimulation Beach(out ArrayGround ground)
        {
            ground = new ArrayGround(64, 64);
            for (var z = 0; z < 64; z++)
                for (var x = 0; x < 32; x++)
                    ground[x, z] = 5f;
            var simulation = new WaterSimulation(WaterSimulationDesc.Default(64, 64, Cell, Vector2.zero), ground);
            simulation.FillTo(1f);
            return simulation;
        }

        [Test]
        public void WaterReadsItsMetresFromTheShoreAndLandReadsNothing()
        {
            using (var simulation = Beach(out _))
            using (var shore = new WaterShoreDistance(simulation, reach: 40f, cellsPerTexel: 2))
            {
                Assert.That(shore.Width, Is.EqualTo(32));
                Assert.That(shore.MetresPerTexel, Is.EqualTo(1f));
                var field = shore.ReadImmediate();
                float At(int x) => field[16 * shore.Width + x];
                Assert.That(At(5), Is.EqualTo(0f), "dry land");
                Assert.That(At(16), Is.EqualTo(1f).Within(0.01f), "the first metre of water");
                Assert.That(At(26), Is.EqualTo(11f).Within(0.01f), "eleven metres out");
            }
        }

        [Test]
        public void FarWaterReadsTheReach()
        {
            using (var simulation = Beach(out _))
            using (var shore = new WaterShoreDistance(simulation, reach: 6f, cellsPerTexel: 2))
            {
                var field = shore.ReadImmediate();
                Assert.That(field[16 * shore.Width + 30], Is.EqualTo(6f).Within(0.01f));
            }
        }

        [Test]
        public void TheShoreFollowsTheWaterWhenRebuilt()
        {
            using (var simulation = Beach(out _))
            using (var shore = new WaterShoreDistance(simulation, reach: 40f, cellsPerTexel: 2))
            {
                // Drain the east edge dry: water there is now next to a shore it did not have.
                var depths = simulation.ReadDepthsImmediate();
                for (var z = 0; z < 64; z++)
                    for (var x = 60; x < 64; x++)
                        depths[z * 64 + x] = 0f;
                simulation.SetDepths(depths);
                shore.Update();
                var field = shore.ReadImmediate();
                Assert.That(field[16 * shore.Width + 28], Is.EqualTo(2f).Within(0.01f), "two metres from the new east shore");
            }
        }
    }
}
