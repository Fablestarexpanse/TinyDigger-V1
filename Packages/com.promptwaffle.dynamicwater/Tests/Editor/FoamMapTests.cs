using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.DynamicWater.Tests
{
    /// <summary>
    /// Foam round hulls (2026-09-24): a hull at rest laps foam round its waterline and nowhere else;
    /// a moving one leaves a wake behind it and nothing far ahead; and foam fades away once nothing
    /// is making it.
    /// </summary>
    public class FoamMapTests
    {
        const float Cell = 0.5f;
        const int Size = 64;

        static WaterSimulation Pond()
        {
            var simulation = new WaterSimulation(WaterSimulationDesc.Default(Size, Size, Cell, Vector2.zero), new ArrayGround(Size, Size));
            simulation.FillTo(1f);
            return simulation;
        }

        static WaterFoamEmitter Hull(Vector2 at, Vector2 velocity) => new WaterFoamEmitter
        {
            Position = at,
            Forward = Vector2.right,
            HalfSize = new Vector2(2f, 1f),
            Velocity = velocity,
            Ring = 0.45f,
            Bow = 1f,
            Wake = 0.9f,
        };

        static float At(float[] foam, float x, float z) => foam[Mathf.FloorToInt(z / Cell) * Size + Mathf.FloorToInt(x / Cell)];

        [Test]
        public void AHullAtRestLapsFoamRoundItsWaterlineOnly()
        {
            using (var simulation = Pond())
            using (var map = new WaterFoamMap(simulation))
            {
                map.Step(0.1f, new[] { Hull(new Vector2(16f, 16f), Vector2.zero) });
                var foam = map.ReadImmediate();
                Assert.That(At(foam, 18.25f, 16.25f), Is.GreaterThan(0.3f), "at the waterline");
                Assert.That(At(foam, 16.25f, 16.25f), Is.EqualTo(0f), "under the hull");
                Assert.That(At(foam, 26.25f, 16.25f), Is.EqualTo(0f), "far off");
            }
        }

        [Test]
        public void AMovingHullLeavesAWakeBehindAndNothingFarAhead()
        {
            using (var simulation = Pond())
            using (var map = new WaterFoamMap(simulation))
            {
                var x = 8f;
                for (var i = 0; i < 30; i++)
                {
                    map.Step(0.1f, new[] { Hull(new Vector2(x, 16f), new Vector2(3f, 0f)) });
                    x += 0.3f;
                }

                var foam = map.ReadImmediate();
                var stern = x - 0.3f - 2f;
                Assert.That(At(foam, stern - 4f, 16.25f), Is.GreaterThan(0.2f), "the wake four metres astern");
                Assert.That(At(foam, x + 2f + 3.5f, 16.25f), Is.EqualTo(0f), "three and a half metres ahead of the bow");
            }
        }

        [Test]
        public void FoamFadesOnceNothingMakesIt()
        {
            using (var simulation = Pond())
            using (var map = new WaterFoamMap(simulation, lifetime: 1f))
            {
                map.Step(0.1f, new[] { Hull(new Vector2(16f, 16f), new Vector2(3f, 0f)) });
                for (var i = 0; i < 60; i++)
                    map.Step(0.1f, System.Array.Empty<WaterFoamEmitter>());
                var foam = map.ReadImmediate();
                Assert.That(Mathf.Max(foam), Is.EqualTo(0f));
                Assert.That(map.LastTexels, Is.EqualTo(0), "nothing worked on once the foam is gone");
            }
        }
    }
}
