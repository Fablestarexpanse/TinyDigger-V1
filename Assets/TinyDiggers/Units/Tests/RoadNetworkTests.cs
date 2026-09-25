using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Roads as splines (Slice 17 Part B): the network, the curve and its grades, the footprint the
    /// crew works to, and road cells being cheaper to drive.
    /// </summary>
    public class RoadNetworkTests
    {
        static TerrainGrid Flat(int size, float height, float cellSize = 1f)
        {
            var grid = new TerrainGrid(size, size, TinyDiggersMaterials.CreateTable(), 0.5f, 0f, cellSize);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, height) });
            return grid;
        }

        static List<RoadNode> Chain(params Vector3[] points)
        {
            var chain = new List<RoadNode>();
            foreach (var p in points)
                chain.Add(new RoadNode { Position = new Vector2(p.x, p.z), Height = p.y });
            return chain;
        }

        [Test]
        public void SamplesRunEvenlyAlongTheCurveAndOnlyForwards()
        {
            var chain = Chain(new Vector3(2f, 5f, 2f), new Vector3(12f, 6f, 4f), new Vector3(18f, 6f, 14f), new Vector3(10f, 7f, 22f));
            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, 0.25f, samples);

            Assert.That(samples.Count, Is.GreaterThan(80));
            for (var i = 1; i < samples.Count; i++)
            {
                Assert.That(samples[i].Distance, Is.GreaterThan(samples[i - 1].Distance), $"sample {i} went backwards");
                var gap = Vector2.Distance(new Vector2(samples[i].Position.x, samples[i].Position.z),
                    new Vector2(samples[i - 1].Position.x, samples[i - 1].Position.z));
                Assert.That(gap, Is.LessThanOrEqualTo(0.25f + 1e-3f), $"samples {i - 1} and {i} are {gap} apart");
                if (i < samples.Count - 1)
                    Assert.That(gap, Is.GreaterThan(0.2f), "and evenly: only the last step may be short");
            }

            Assert.That(Vector3.Distance(samples[0].Position, new Vector3(2f, 5f, 2f)), Is.LessThan(1e-3f), "starts at the first node");
            Assert.That(Vector3.Distance(samples[samples.Count - 1].Position, new Vector3(10f, 7f, 22f)), Is.LessThan(1e-3f), "ends at the last");
        }

        [Test]
        public void GradeIsRiseOverRunAndIsJudgedAgainstTheLimit()
        {
            // 20 cells of 0.5 m is 10 m on the flat; rising 1.2 m is 12%.
            var chain = Chain(new Vector3(0f, 5f, 0f), new Vector3(20f, 6.2f, 0f));
            Assert.That(RoadSpline.Grade(chain, 0, 0.5f), Is.EqualTo(0.12f).Within(1e-3f));
            Assert.That(RoadSpline.AverageGrade(chain, 0, 0.5f), Is.EqualTo(0.12f).Within(1e-3f));

            Assert.That(RoadPlanner.Judge(0.12f, RoadPlanner.DefaultMaxGrade), Is.EqualTo(RoadGradeState.Fine), "at the limit is fine");
            Assert.That(RoadPlanner.Judge(0.2f, RoadPlanner.DefaultMaxGrade), Is.EqualTo(RoadGradeState.Steep));
            Assert.That(RoadPlanner.Judge(0.25f, RoadPlanner.DefaultMaxGrade), Is.EqualTo(RoadGradeState.Refused), "over twice the limit");
        }

        [Test]
        public void AGradeIsTheSteepestStretchNotTheAverage()
        {
            // A middle node held flat: the height eases in and out of it, so each segment is
            // steeper in its middle than its average.
            var chain = Chain(new Vector3(0f, 5f, 0f), new Vector3(10f, 6f, 0f), new Vector3(20f, 7f, 0f));
            var steepest = RoadSpline.Grade(chain, 0, 1f);
            var average = RoadSpline.AverageGrade(chain, 0, 1f);
            Assert.That(steepest, Is.GreaterThanOrEqualTo(average - 1e-4f));
        }

        [Test]
        public void TheFootprintCoversTheFullWidthWithNoHoles()
        {
            var grid = Flat(48, 5f);
            // A curving road, 5 cells wide, a metre above the ground all the way.
            var chain = Chain(new Vector3(6f, 6f, 6f), new Vector3(20f, 6f, 10f), new Vector3(30f, 6f, 24f), new Vector3(28f, 6f, 40f));
            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            var plan = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            RoadPlanner.Footprint(grid, samples, 5, plan, bed);

            var inBed = new HashSet<Vector2Int>(bed);
            var checkedCells = 0;
            for (var z = 0; z < 48; z++)
            {
                for (var x = 0; x < 48; x++)
                {
                    var centre = new Vector2(x + 0.5f, z + 0.5f);
                    var nearest = float.MaxValue;
                    foreach (var sample in samples)
                        nearest = Mathf.Min(nearest, Vector2.Distance(centre, new Vector2(sample.Position.x, sample.Position.z)));
                    // Everything well inside the half width is road bed.
                    if (nearest > 2.5f - 0.2f)
                        continue;
                    checkedCells++;
                    Assert.That(inBed.Contains(new Vector2Int(x, z)), Is.True, $"a hole in the road at ({x}, {z})");
                }
            }

            Assert.That(checkedCells, Is.GreaterThan(150));
            foreach (var cell in plan)
                if (inBed.Contains(new Vector2Int(cell.X, cell.Z)))
                    Assert.That(cell.Height, Is.EqualTo(6f).Within(1e-3f), "the bed is at the road's height");
            Assert.That(plan.TrueForAll(c => c.IsFill), Is.True, "a road above the ground is all fill");
        }

        [Test]
        public void AStraightRoadIsExactlyItsWidthAcross()
        {
            var grid = Flat(40, 5f);
            var chain = Chain(new Vector3(4f, 5f, 20.5f), new Vector3(36f, 5f, 20.5f));
            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            var plan = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            RoadPlanner.Footprint(grid, samples, 5, plan, bed);

            for (var x = 8; x < 32; x++)
                Assert.That(bed.FindAll(c => c.x == x).Count, Is.EqualTo(5), $"column {x}");
        }

        [Test]
        public void ARoadDrawnFromAnotherRoadsEndJoinsIt()
        {
            var network = new RoadNetwork();
            var first = network.AddRoad(new[] { new Vector3(0f, 5f, 0f), new Vector3(10f, 5f, 0f) }, 3);
            // Clicked half a cell off the first road's end: it snaps on.
            var second = network.AddRoad(new[] { new Vector3(10.5f, 5f, 0.4f), new Vector3(10f, 5f, 10f) }, 3);

            Assert.That(network.Nodes.Count, Is.EqualTo(3), "the shared end is one node");
            var shared = network.NodeNear(new Vector2(10f, 0f), 0.1f);
            Assert.That(shared, Is.Not.Null, "the joined node kept its place");
            Assert.That(network.Degree(shared.Id), Is.EqualTo(2));

            network.AddRoad(new[] { new Vector3(10f, 5f, 0f), new Vector3(20f, 5f, 0f) }, 5);
            Assert.That(network.Degree(shared.Id), Is.EqualTo(3), "a third road there makes a junction");
            Assert.That(network.Roads().Count, Is.EqualTo(3));

            Assert.That(network.RemoveRoad(second), Is.True);
            Assert.That(network.Degree(shared.Id), Is.EqualTo(2));
            Assert.That(network.Nodes.Count, Is.EqualTo(3), "the removed road's far end went with it");
            Assert.That(network.Chain(first).Count, Is.EqualTo(2));
        }

        [Test]
        public void TheNetworkRoundTripsThroughJson()
        {
            var network = new RoadNetwork();
            network.AddRoad(new[] { new Vector3(1f, 5f, 1f), new Vector3(9f, 6f, 3f), new Vector3(15f, 6f, 9f) }, 7);
            var node = network.Nodes[1];
            network.SetHeight(node.Id, 6.5f);
            network.SetHandle(node.Id, new Vector2(2f, 1f));

            var copy = RoadNetwork.FromJson(network.ToJson());

            Assert.That(copy.Nodes.Count, Is.EqualTo(3));
            Assert.That(copy.Segments.Count, Is.EqualTo(2));
            Assert.That(copy.Node(node.Id).Height, Is.EqualTo(6.5f));
            Assert.That(copy.Node(node.Id).LockToGround, Is.False, "setting a height unlocks it from the ground");
            Assert.That(copy.Node(node.Id).Handle, Is.EqualTo(new Vector2(2f, 1f)));
            Assert.That(copy.WidthOf(copy.Roads()[0]), Is.EqualTo(7));
        }

        [Test]
        public void RoadCellsCostSevenTenthsAndUnitsGoOutOfTheirWayForThem()
        {
            var grid = Flat(24, 5f);
            var pathfinder = new GridPathfinder(grid);
            // A road along row 14 from (2, 14) to (21, 14), with ramps to it at the ends.
            for (var x = 2; x <= 21; x++)
                grid.SetColumn(x, 14, new[] { new Layer(MaterialTable.Dirt, 4.9f), new Layer(TinyDiggersMaterials.Road, 0.1f) });
            Assert.That(pathfinder.StepCost(5, 14, 6, 14), Is.EqualTo(0.7f * grid.CellSize).Within(1e-5f));
            Assert.That(pathfinder.StepCost(5, 10, 6, 10), Is.EqualTo(1f * grid.CellSize).Within(1e-5f));

            // From (2, 12) to (21, 12): straight over the dirt costs 19 cells. Two diagonal steps up
            // to the road, 15 along it and two back down cost 2.8 + 15 x 0.7 + 2.8 = 16.2, so the
            // road wins although it is the longer way.
            var path = new List<Vector2Int>();
            Assert.That(pathfinder.TryFindPath(2, 12, 21, 12, path), Is.True);
            var onRoad = path.FindAll(c => c.y == 14).Count;
            Assert.That(onRoad, Is.GreaterThan(12), $"the unit took the road: {onRoad} of {path.Count} cells on it");
        }
    }
}
