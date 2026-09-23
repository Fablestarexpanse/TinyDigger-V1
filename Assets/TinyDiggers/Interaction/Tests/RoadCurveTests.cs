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
            Assert.That(RoadSpline.TightestTurn(draft.Nodes, Cell), Is.GreaterThan(before),
                "and the road itself turns wider than it did, not just the reading");
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
        public void TheEndsOfARoadAreShapedToo()
        {
            // An end node's automatic tangent is a whole chord, twice an inside node's, and the
            // swing that puts into the first and last stretches is a real bend the crew drive.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(20f, 2f), Flat);
            draft.Place(new Vector2(22f, 22f), Flat);
            var before = RoadSpline.TurnRadius(draft.Nodes, 0, Cell);

            var got = draft.SmoothTo(0, 6f);

            Assert.That(draft.Nodes[0].HasHandle, Is.True);
            Assert.That(got, Is.GreaterThanOrEqualTo(before), "the first stretch is no tighter than it was");
        }

        [Test]
        public void SmoothingAskedOfNothingIsRefusedQuietly()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);

            Assert.That(draft.SmoothTo(0, 6f), Is.EqualTo(float.PositiveInfinity), "one node is not a bend");
            Assert.That(draft.SmoothTo(7, 6f), Is.EqualTo(float.PositiveInfinity), "and neither is a node that is not there");
        }

        [Test]
        public void ACutCornerTurnsAtTheRadiusItWasAskedFor()
        {
            // A right angle with long legs: pulling the handle could not make four metres of it,
            // but an arc cut into the corner is exactly four metres by construction.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(40f, 0f), Flat);
            draft.Place(new Vector2(40f, 40f), Flat);

            var got = draft.Fillet(1, 4f);

            Assert.That(draft.Nodes.Count, Is.EqualTo(4), "the corner became the two ends of an arc");
            Assert.That(got, Is.EqualTo(4f).Within(0.2f));
            Assert.That(RoadSpline.TightestTurn(draft.Nodes, Cell), Is.GreaterThan(3.8f),
                "and nothing else on the road turns tighter");
        }

        [Test]
        public void ABendReadsTheSameWhereverOnTheIslandItIs()
        {
            // Roads are drawn fifteen hundred cells from the origin, and the points the radius is
            // taken from are a fraction of a cell apart. Worked in float on the raw coordinates,
            // a four-metre arc read 3.7 m out in the world and the tool warned about bends it had
            // just built properly (2026-09-22).
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(40f, 0f), Flat);
            draft.Place(new Vector2(40f, 40f), Flat);
            draft.Fillet(1, 4f);
            var atOrigin = RoadSpline.TightestTurn(draft.Nodes, Cell);

            var far = new Vector2(1565.5f, 1547.5f);
            foreach (var node in draft.Nodes)
                node.Position += far;

            Assert.That(RoadSpline.TightestTurn(draft.Nodes, Cell), Is.EqualTo(atOrigin).Within(0.02f));
        }

        [Test]
        public void ACutCornerKeepsTheRoadWhereItWas()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(40f, 0f), Flat);
            draft.Place(new Vector2(40f, 40f), Flat);

            draft.Fillet(1, 4f);

            // Both new nodes sit on the legs the corner was on, a tangent distance back from it.
            Assert.That(draft.Nodes[1].Position.y, Is.EqualTo(0f).Within(1e-3f), "on the first leg");
            Assert.That(draft.Nodes[2].Position.x, Is.EqualTo(40f).Within(1e-3f), "and on the second");
            Assert.That(draft.Nodes[1].Position.x, Is.LessThan(40f).And.GreaterThan(30f), "cutting the corner, not the road");
            Assert.That(draft.Nodes[0].Position, Is.EqualTo(new Vector2(0f, 0f)), "the ends do not move");
            Assert.That(draft.Nodes[3].Position, Is.EqualTo(new Vector2(40f, 40f)));
        }

        [Test]
        public void AnArcTooBigForTheLegsIsCutDownToFit()
        {
            // Legs four cells (two metres) long cannot hold a ten-metre arc, and a tool that tried
            // would put the two new nodes past each other and tie the road in a knot.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(4f, 0f), Flat);
            draft.Place(new Vector2(4f, 4f), Flat);

            var got = draft.Fillet(1, 10f);

            Assert.That(got, Is.LessThan(10f), "it does not claim the arc it was asked for");
            Assert.That(draft.Nodes[1].Position.x, Is.LessThan(draft.Nodes[2].Position.x + 1e-3f));
            Assert.That(draft.Nodes[1].Position.x, Is.GreaterThan(0f), "and stays clear of the node before");
        }

        [Test]
        public void AStraightRunHasNoCornerToCut()
        {
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(20f, 0f), Flat);
            draft.Place(new Vector2(40f, 0f), Flat);

            Assert.That(draft.Fillet(1, 4f), Is.EqualTo(float.PositiveInfinity));
            Assert.That(draft.Nodes.Count, Is.EqualTo(3), "and nothing is inserted");
        }

        [Test]
        public void RoundingFallsBackToCuttingTheCornerWhenTheHandleCannotReach()
        {
            // The dog-leg that smoothing could only open to about 3.7 m.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(26f, 4f), Flat);
            draft.Place(new Vector2(28f, 28f), Flat);
            draft.Place(new Vector2(52f, 34f), Flat);

            draft.RoundAll(4f);

            Assert.That(draft.Nodes.Count, Is.EqualTo(6), "both corners were cut, not just pulled");
            Assert.That(RoadSpline.TightestTurn(draft.Nodes, Cell), Is.GreaterThanOrEqualTo(3.95f),
                "and now the road really does turn at the radius asked for");
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
            // 20 cells at half a metre is ten metres of run, then twice that: the second node has
            // to climb twice what the first did, whatever the grade settles at.
            var first = draft.Nodes[1].Height - draft.Nodes[0].Height;
            var second = draft.Nodes[2].Height - draft.Nodes[1].Height;
            Assert.That(second, Is.EqualTo(first * 2f).Within(1e-3f));
            Assert.That(first, Is.GreaterThan(0.3f).And.LessThanOrEqualTo(0.4f + 1e-3f), "about 4% of ten metres");
            Assert.That(draft.Nodes[1].LockToGround, Is.False, "a held grade leaves the ground");

            var grades = new List<float>();
            draft.Grades(Cell, grades);
            foreach (var grade in grades)
                Assert.That(grade, Is.LessThanOrEqualTo(0.04f + 1e-4f),
                    "and nowhere along it reads steeper than what was asked for");
        }

        [Test]
        public void SmoothingABendWidensItFarMoreThanTheNeighbouringBendsAllow()
        {
            // A right-angle dog-leg with long legs: there is room for a wide arc, and the tool
            // should find it. Reading the whole of both segments instead of the ground either side
            // of the node had it stop at a bend barely wider than the sharp one (2.2 m to 2.5 m,
            // 2026-09-22), because a long handle here straightens the far end and tightens the
            // neighbour's own bend, which this is not the tool for.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(26f, 4f), Flat);
            draft.Place(new Vector2(28f, 28f), Flat);
            draft.Place(new Vector2(52f, 34f), Flat);

            var got = draft.SmoothTo(1, 4f);

            Assert.That(got, Is.GreaterThanOrEqualTo(4f),
                "legs this long have room for a four-metre arc");
        }

        [Test]
        public void SmoothingTheWholeRoadOpensItOutFarWithoutPromisingTheRadius()
        {
            // Two near-right-angle bends twelve metres apart. Pulling handles can only do so much
            // with the node positions fixed: asked for four metres this opens 2.2 m out to about
            // 3.7 m and stops (2026-09-22). That is the honest answer — a road wanting a wider
            // arc than its nodes allow needs another node, not a longer handle — so what is
            // tested is that it opens out a long way and that the tally then says what it is.
            var draft = Draft();
            draft.Place(new Vector2(0f, 0f), Flat);
            draft.Place(new Vector2(26f, 4f), Flat);
            draft.Place(new Vector2(28f, 28f), Flat);
            draft.Place(new Vector2(52f, 34f), Flat);
            var before = RoadSpline.TightestTurn(draft.Nodes, Cell);

            draft.SmoothAll(4f);
            var after = RoadSpline.TightestTurn(draft.Nodes, Cell);

            Assert.That(after, Is.GreaterThan(before * 1.5f), "much wider than it was drawn");
            var turns = new List<float>();
            draft.MinTurnRadius = 4f;
            Assert.That(draft.Turns(turns), Is.Not.EqualTo(RoadGradeState.Fine),
                "and it still owns up to being tighter than the limit");
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
