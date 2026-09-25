using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The landing craft making a trip (FERRY_PROPOSAL.md, slice B): off one beach, over the water
    /// and onto another, bow first with the ramp down at the end.
    /// </summary>
    public class FerryTests
    {
        const int Width = 80;
        const int Depth = 40;
        const float Cell = 0.5f;

        TerrainGrid _grid;
        WaterNav _nav;

        [SetUp]
        public void SetUp()
        {
            // A strait running north-south: a beach on each side of two metres of water.
            _grid = new TerrainGrid(Width, Depth, MaterialTable.CreateDefault(), heightStep: 0.5f, datum: -10f,
                cellSize: Cell);
            Fill(x =>
            {
                if (x < 8) return 1f;
                if (x < 14) return 0.5f - (x - 8) * 0.4f;        // west beach: dry sand at 0.5 m, then under
                if (x < 66) return -2f;
                if (x < 72) return -2f + (x - 65) * 0.5f;        // east beach up out of it
                return 1f;
            });
            _nav = new WaterNav(_grid, Ferry.Draft, Ferry.HalfBeam);
        }

        void Fill(System.Func<int, float> surface)
        {
            for (var z = 0; z < Depth; z++)
                for (var x = 0; x < Width; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Mathf.Max(0.5f, surface(x) + 8f)),
                    });
        }

        Ferry BeachedWest()
        {
            var west = Ferry.FindLanding(_grid, _nav, new Vector2Int(8, 20));
            Assert.That(west.Found, Is.True, "the west beach takes the craft: " + west.Refusal);
            return Ferry.BeachedAt(_grid, _nav, west);
        }

        [Test]
        public void ItSailsFromOneBeachToAnotherAndLowersItsRamp()
        {
            var ferry = BeachedWest();
            Assert.That(ferry.SailTo(new Vector2Int(72, 20), out var why), Is.True, why);

            var seen = new HashSet<FerryState>();
            for (var t = 0f; t < 120f && ferry.State != FerryState.RampDown; t += 0.05f)
            {
                ferry.Tick(0.05f);
                seen.Add(ferry.State);
            }

            Assert.That(ferry.State, Is.EqualTo(FerryState.RampDown), ferry.Status);
            Assert.That(seen.Contains(FerryState.BackingOff), Is.True, "it backed off the west beach");
            Assert.That(seen.Contains(FerryState.Sailing), Is.True, "it sailed");
            Assert.That(seen.Contains(FerryState.RunningIn), Is.True, "it ran in onto the east beach");
            Assert.That(Vector2.Distance(ferry.Position, ferry.Landing.Hull), Is.LessThan(0.01f), "on the landing it found");
            Assert.That(Mathf.DeltaAngle(ferry.Heading, 90f), Is.InRange(-46f, 46f), "bow east, onto the east beach");
            Assert.That(ferry.Landing.RampFoot.x, Is.GreaterThanOrEqualTo(66), "the ramp comes down on the east side");
        }

        [Test]
        public void ItNeverSailsWhereItWouldGround()
        {
            var ferry = BeachedWest();
            Assert.That(ferry.SailTo(new Vector2Int(72, 20), out _), Is.True);
            for (var t = 0f; t < 120f && ferry.State != FerryState.RampDown; t += 0.05f)
            {
                ferry.Tick(0.05f);
                if (ferry.State == FerryState.Sailing)
                {
                    var c = new Vector2Int(Mathf.FloorToInt(ferry.Position.x), Mathf.FloorToInt(ferry.Position.y));
                    Assert.That(_nav.DeepEnough(c.x, c.y), Is.True, $"sailing over ({c.x}, {c.y}), which would ground it");
                }
            }
        }

        [Test]
        public void ASendToACliffIsRefusedWithTheReason()
        {
            // The east shore made a wall: nowhere for the ramp.
            Fill(x => x < 8 ? 1f : x < 14 ? 0.5f - (x - 8) * 0.4f : x < 66 ? -2f : 3f);
            _nav.Refresh();
            var ferry = BeachedWest();
            Assert.That(ferry.SailTo(new Vector2Int(68, 20), out var why), Is.False);
            Assert.That(why, Is.EqualTo(LandingFinder.TooSteep));
            Assert.That(ferry.State, Is.EqualTo(FerryState.Beached), "and it stays where it was");
        }

        [Test]
        public void ARefusedOrderUnderWayLeavesItsCourseAlone()
        {
            var ferry = BeachedWest();
            Assert.That(ferry.SailTo(new Vector2Int(72, 20), out var why), Is.True, why);
            for (var t = 0f; t < 60f && ferry.State != FerryState.Sailing; t += 0.05f)
                ferry.Tick(0.05f);
            Assert.That(ferry.State, Is.EqualTo(FerryState.Sailing), ferry.Status);

            // A dam across the strait ahead of it: the east beach still takes a craft, but there is
            // no water to it any more.
            for (var z = 0; z < Depth; z++)
                for (var x = 44; x < 47; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 9f) });
            _nav.Refresh();

            var route = new List<Vector2Int>(ferry.Route);
            var landing = ferry.Landing;
            Assert.That(ferry.SailTo(new Vector2Int(72, 20), out why), Is.False);
            Assert.That(why, Is.EqualTo("no way over the water from here"));
            Assert.That(ferry.State, Is.EqualTo(FerryState.Sailing), "still sailing");
            Assert.That(ferry.Route, Is.EqualTo(route), "on the course it had");
            Assert.That(ferry.Landing.Hull, Is.EqualTo(landing.Hull), "for the landing it had");
        }
    }
}
