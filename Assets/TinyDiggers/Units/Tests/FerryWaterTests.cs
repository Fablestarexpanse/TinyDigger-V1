using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Where the landing craft floats and where it can beach (FERRY_PROPOSAL.md, slice A). Water
    /// here is the sea-level rule: ground below sea level (0 m) is under that much water.
    /// </summary>
    public class FerryWaterTests
    {
        const int Size = 48;
        const float Cell = 0.5f;

        // The craft at the game's size (x1.41): 2.87 m beam, 4.9 m long, 0.17 m loaded draft,
        // a 0.65 m ramp.
        const float Draft = 0.17f;
        const float HalfBeam = 1.43f;
        const float HalfLength = 2.2f;
        const float RampReach = 0.65f;
        const float RampRise = 0.5f;
        const int LineUp = 5;
        const float MaxStep = 0.5f;

        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f, datum: -10f,
                cellSize: Cell);
            Fill((x, z) => -2f);
        }

        void Fill(System.Func<int, int, float> surface)
        {
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, surface(x, z) + 10f - 2f),
                    });
        }

        WaterNav Nav() => new WaterNav(_grid, Draft, HalfBeam);

        [Test]
        public void TheCraftFloatsInDeepWaterAndNotInShallows()
        {
            Fill((x, z) => x < 24 ? -2f : -0.2f);
            var nav = Nav();
            Assert.That(nav.Floats(10, 24), Is.True, "two metres of water");
            Assert.That(nav.Floats(36, 24), Is.False, "twenty centimetres of water grounds it");
        }

        [Test]
        public void ItsSidesMustFloatToo()
        {
            // Deep water right under the middle, shallows a metre to the side: the beam is 2.87 m,
            // so the sides would ground.
            Fill((x, z) => Mathf.Abs(x - 24) <= 1 ? -2f : -0.1f);
            Assert.That(Nav().Floats(24, 24), Is.False);
        }

        [TestCase(2, false)]   // a 2.5 m river: narrower than the 2.87 m beam
        [TestCase(5, true)]    // a 5.5 m river: room for the beam with its sides clear
        public void ItGoesUpARiverOnlyWhereItsBeamFits(int halfWidthCells, bool passes)
        {
            // Two lakes joined by a river. Ronan, 2026-09-24: "if it can't go up river then it
            // stays at beach" -- the beam and draft decide, not a rule about rivers.
            Fill((x, z) =>
            {
                var lake = x < 16 || x > 32;
                var river = Mathf.Abs(z - 24) <= halfWidthCells;
                return lake || river ? -2f : 1f;
            });
            var nav = Nav();
            var route = new List<Vector2Int>();
            Assert.That(nav.Floats(6, 24) && nav.Floats(42, 24), Is.True, "both lakes float it");
            Assert.That(nav.TryFindRoute(new Vector2Int(6, 24), new Vector2Int(42, 24), route), Is.EqualTo(passes));
        }

        [Test]
        public void ItSailsBetweenLakesJoinedByOpenWater()
        {
            Fill((x, z) =>
            {
                var lake = x < 16 || x > 32;
                var strait = Mathf.Abs(z - 24) < 10;
                return lake || strait ? -2f : 1f;
            });
            var route = new List<Vector2Int>();
            Assert.That(Nav().TryFindRoute(new Vector2Int(6, 24), new Vector2Int(42, 24), route), Is.True);
            Assert.That(route[0], Is.EqualTo(new Vector2Int(6, 24)));
            Assert.That(route[route.Count - 1], Is.EqualTo(new Vector2Int(42, 24)));
        }

        [Test]
        public void ItBeachesOnAGentleShoreBowToTheLand()
        {
            // Sea to the west, a beach rising a quarter metre a cell from x = 20: the water's edge
            // at x = 24, land from x = 26.
            Fill((x, z) => x < 20 ? -2f : Mathf.Min(1f, -1f + (x - 20) * 0.25f));
            var landing = LandingFinder.Find(_grid, Nav(), new Vector2Int(22, 24), 16,
                HalfLength, RampReach, RampRise, LineUp, MaxStep);

            Assert.That(landing.Found, Is.True, landing.Refusal);
            Assert.That(Mathf.DeltaAngle(landing.Heading, 90f), Is.InRange(-46f, 46f), "bow east, to the land");
            Assert.That(_grid.IsPassableGround(landing.RampFoot.x, landing.RampFoot.y), Is.True);
            Assert.That(_grid.WaterDepth(landing.RampFoot.x, landing.RampFoot.y), Is.LessThanOrEqualTo(0.05f),
                "the ramp comes down on dry ground");
        }

        [Test]
        public void ItWillNotMoorAcrossARiver()
        {
            // A river 5.5 m wide, wide enough to float the craft lengthwise but narrower than the
            // craft is long: lying across it, bow on one bank, its stern would be on the other.
            Fill((x, z) => Mathf.Abs(z - 24) <= 5 ? -2f : 1f);
            var landing = LandingFinder.Find(_grid, Nav(), new Vector2Int(24, 24), 16,
                HalfLength, RampReach, RampRise, LineUp, MaxStep);
            if (landing.Found)
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(landing.Heading, 0f)) > 50f && Mathf.Abs(Mathf.DeltaAngle(landing.Heading, 180f)) > 50f,
                    Is.True, $"it may only lie along the river, not across it (heading {landing.Heading:0})");
        }

        [Test]
        public void ItWillNotBeachUnderACliffAndSaysWhy()
        {
            // Deep water straight up to a wall three metres high.
            Fill((x, z) => x < 26 ? -2f : 3f);
            var landing = LandingFinder.Find(_grid, Nav(), new Vector2Int(22, 24), 16,
                HalfLength, RampReach, RampRise, LineUp, MaxStep);

            Assert.That(landing.Found, Is.False);
            Assert.That(landing.Refusal, Is.EqualTo(LandingFinder.TooSteep));
        }
    }
}
