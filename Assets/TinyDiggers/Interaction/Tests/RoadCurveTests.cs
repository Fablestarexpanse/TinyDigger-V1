using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>
    /// Slice 17 Part C: the tools that shape a road rather than place it — the turn a bend makes,
    /// rounding a bend out to a radius, holding one grade along a ramp, and carrying the road at a
    /// fixed height above the ground.
    /// </summary>
    public class RoadCurveTests
    {
        const float Cell = 0.5f;

        static float Flat(Vector2 at) => 5f;

        static RoadDraft Draft()
        {
            return new RoadDraft { CellSize = Cell, Snap45 = false };
        }

        static List<RoadNode> Chain(params Vector2[] points)
        {
            var chain = new List<RoadNode>();
            foreach (var point in points)
                chain.Add(new RoadNode { Position = point, Height = 0f });
            return chain;
        }

        [Test]
        public void AStraightRoadTurnsAroundNothing()
        {
            var chain = Chain(new Vector2(0f, 0f), new Vector2(10f, 0f), new Vector2(20f, 0f));

            Assert.That(RoadSpline.TightestTurn(chain, Cell), Is.EqualTo(float.PositiveInfinity));
        }

        [Test]
        public void TheTurnRadiusIsTheCircleTheRoadBendsAround()
        {
            // Points evenly round a circle of ten cells: the middle of the chain, where both ends
            // of the segment take their tangents from neighbours on the circle, should read about
            // that circle — ten cells, which is five metres on half-metre cells. (The first and
            // last nodes of a chain take a doubled tangent, so their segments bend tighter; that
            // is why the reading taken here is the middle one.)
            const float radius = 10f;
            var chain = new List<RoadNode>();
            for (var k = -2; k <= 2; k++)
            {
                var angle = k * 0.35f;
                chain.Add(new RoadNode
                {
                    Position = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius,
                    Height = 0f,
                });
            }

            var got = RoadSpline.TurnRadius(chain, 2, Cell);

            Assert.That(got, Is.EqualTo(radius * Cell).Within(radius * Cell * 0.15f),
                "the spline through three points of a circle bends around about that circle");
        }

        [Test]
        public void ASharpBendReadsTighterThanAGentleOne()
        {
            var sharp = Chain(new Vector2(0f, 0f), new Vector2(10f, 0f), new Vector2(11f, 8f));
            var gentle = Chain(new Vector2(0f, 0f), new Vector2(10f, 0f), new Vector2(20f, 2f));

            Assert.That(RoadSpline.TightestTurn(sharp, Cell),
                Is.LessThan(RoadSpline.TightestTurn(gentle, Cell)));
        }

        [Test]
        public void SmoothingABendWidensTheTurnItMakes()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(20f, 0f), Flat);
            draft.Place(new Vector2(28f, 14f), Flat);
            var before = RoadSpline.TightestTurn(draft.Nodes, Cell);

            var after = draft.SmoothTo(1, 6f);

            Assert.That(draft.Nodes[1].HasHandle, Is.True, "smoothing sets the node's handle");
            Assert.That(after, Is.GreaterThan(before), "the bend is wider than it was");
            Assert.That(RoadSpline.TightestTurn(draft.Nodes, Cell), Is.EqualTo(after).Within(1e-3f),
                "what it reports is what the road now does");
        }

        [Test]
        public void SmoothingSaysWhatItReachedWhenTheRadiusCannotBeHad()
        {
            // Two cells apart, a six-metre arc simply does not fit between them.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(2f, 0f), Flat);
            draft.Place(new Vector2(2f, 2f), Flat);

            var got = draft.SmoothTo(1, 6f);

            Assert.That(got, Is.LessThan(6f), "it does not claim a turn it did not make");
            Assert.That(got, Is.GreaterThan(0f));
        }

        [Test]
        public void SmoothingTheEndsOfARoadDoesNothing()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(10f, 0f), Flat);
            draft.Place(new Vector2(18f, 8f), Flat);

            Assert.That(draft.SmoothTo(0, 6f), Is.EqualTo(float.PositiveInfinity));
            Assert.That(draft.SmoothTo(2, 6f), Is.EqualTo(float.PositiveInfinity));
            Assert.That(draft.Nodes[0].HasHandle, Is.False);
            Assert.That(draft.Nodes[2].HasHandle, Is.False);
        }

        [Test]
        public void ACorneredNodeTurnsTighterThanASmoothedOne()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(20f, 0f), Flat);
            draft.Place(new Vector2(28f, 14f), Flat);

            draft.SmoothTo(1, 8f);
            var smoothed = RoadSpline.TightestTurn(draft.Nodes, Cell);
            draft.Corner(1);
            var cornered = RoadSpline.TightestTurn(draft.Nodes, Cell);

            Assert.That(cornered, Is.LessThan(smoothed));

            draft.AutoHandle(1);
            Assert.That(draft.Nodes[1].HasHandle, Is.False, "and it can go back to automatic");
        }

        [Test]
        public void ATightTurnIsJudgedTheWayASteepGradeIs()
        {
            Assert.That(RoadPlanner.JudgeTurn(10f, 4f), Is.EqualTo(RoadGradeState.Fine));
            Assert.That(RoadPlanner.JudgeTurn(3f, 4f), Is.EqualTo(RoadGradeState.Steep));
            Assert.That(RoadPlanner.JudgeTurn(1.5f, 4f), Is.EqualTo(RoadGradeState.Refused));
            Assert.That(RoadPlanner.JudgeTurn(float.PositiveInfinity, 4f), Is.EqualTo(RoadGradeState.Fine),
                "a straight road never turns too tight");
        }

        [Test]
        public void HoldingAGradeGivesEveryNodeTheSameSlope()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Nodes[0].Height = 0f;
            draft.Place(new Vector2(20f, 0f), Flat);
            draft.Nodes[1].Height = 9f;     // absurdly steep to start with
            draft.Place(new Vector2(60f, 0f), Flat);
            draft.Nodes[2].Height = 11f;

            var moved = draft.ApplyGrade(0.04f);

            Assert.That(moved, Is.EqualTo(2));
            // 20 cells at half a metre is ten metres of run; 4% of that is 0.4 m.
            Assert.That(draft.Nodes[1].Height, Is.EqualTo(0.4f).Within(1e-3f));
            Assert.That(draft.Nodes[2].Height, Is.EqualTo(0.4f + 0.8f).Within(1e-3f));
            Assert.That(draft.Nodes[1].LockToGround, Is.False, "a held grade leaves the ground");

            var grades = new List<float>();
            draft.Grades(Cell, grades);
            foreach (var grade in grades)
                Assert.That(grade, Is.LessThan(0.041f), "and no stretch is steeper than what was asked for");
        }

        [Test]
        public void AHeldGradeKeepsTheWayEachStretchAlreadyRan()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Nodes[0].Height = 10f;
            draft.Place(new Vector2(20f, 0f), Flat);
            draft.Nodes[1].Height = 2f;     // down
            draft.Place(new Vector2(40f, 0f), Flat);
            draft.Nodes[2].Height = 2f;     // and then flat

            draft.ApplyGrade(0.06f);

            Assert.That(draft.Nodes[1].Height, Is.LessThan(10f), "down stays down");
            Assert.That(draft.Nodes[2].Height, Is.EqualTo(draft.Nodes[1].Height).Within(1e-4f), "flat stays flat");
        }

        [Test]
        public void DrawingWithAGradeLockedClimbsInsteadOfFollowingTheGround()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.GradeLock = 0.05f;
            var up = draft.Place(new Vector2(20f, 0f), at => 20f);      // ground far above
            var down = draft.Place(new Vector2(40f, 0f), at => 0f);     // and then far below

            Assert.That(up.Height, Is.EqualTo(5f + 0.5f).Within(1e-3f), "climbs 5% of ten metres");
            Assert.That(up.LockToGround, Is.False);
            Assert.That(down.Height, Is.EqualTo(up.Height - 0.5f).Within(1e-3f), "and falls where the ground does");
        }

        [Test]
        public void ANodeHeldAboveTheGroundFollowsItAtThatHeight()
        {
            var draft = Draft();
            draft.GroundOffset = 2f;
            var node = draft.Place(new Vector2(10f, 0f), Flat);

            Assert.That(node.Height, Is.EqualTo(7f).Within(1e-4f));
            Assert.That(node.LockToGround, Is.True, "it still follows the ground, two metres up");

            draft.Move(0, new Vector2(30f, 0f), at => 9f);
            Assert.That(draft.Nodes[0].Height, Is.EqualTo(11f).Within(1e-4f), "carried over the new ground too");

            draft.SetGroundOffset(0, 0f, at => 9f);
            Assert.That(draft.Nodes[0].Height, Is.EqualTo(9f).Within(1e-4f), "and back down onto it");
        }

        [Test]
        public void ATypedHeightSetsTheNodeOutrightAndUnlocksIt()
        {
            var draft = Draft();
            draft.Place(new Vector2(10f, 0f), Flat);

            draft.SetHeight(0, 12.5f);

            Assert.That(draft.Nodes[0].Height, Is.EqualTo(12.5f).Within(1e-4f));
            Assert.That(draft.Nodes[0].LockToGround, Is.False);
        }

        [Test]
        public void TheDraftReportsEverySegmentsTurnAndJudgesTheWorst()
        {
            var draft = Draft();
            draft.MinTurnRadius = 4f;
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(20f, 0f), Flat);
            draft.Place(new Vector2(21f, 6f), Flat);
            var turns = new List<float>();

            var state = draft.Turns(turns);
            var before = Mathf.Min(turns[0], turns[1]);

            Assert.That(turns.Count, Is.EqualTo(2), "one reading per segment");
            Assert.That(state, Is.Not.EqualTo(RoadGradeState.Fine), "that hairpin is not a road");

            draft.SmoothAll(20f);
            draft.Turns(turns);

            Assert.That(Mathf.Min(turns[0], turns[1]), Is.GreaterThan(before),
                "smoothing the whole road widens its tightest bend");
        }
    }
}
