using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// A ribbon is a road that is never paved: the same curve, the same footprint, the same shoulder
    /// easing back to the ground. These tests are mostly about proving that it really is the same
    /// code and not a second one that will drift.
    /// </summary>
    public class LandformRibbonTests
    {
        const int Size = 60;
        const float Ground = 20f;

        TerrainGrid _grid;
        DesignationMap _map;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f, cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Ground - 2f),
                    });
            _map = new DesignationMap(_grid);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        static List<RoadNode> Chain(params (float x, float z, float height)[] points)
        {
            var nodes = new List<RoadNode>();
            foreach (var point in points)
                nodes.Add(new RoadNode
                {
                    Position = new Vector2(point.x, point.z), Height = point.height, LockToGround = false,
                });
            return nodes;
        }

        [Test]
        public void ARibbonsFootprintIsTheRoadToolsOwn()
        {
            // The cheapest possible proof of reuse: for the same chain and the same width, what the
            // plan asks for and what RoadPlanner would stamp are the same cells at the same heights.
            var floor = Ground - 3f;
            var nodes = Chain((10f, 30f, floor), (30f, 30f, floor), (45f, 40f, floor));
            const int width = 5;

            var samples = new List<RoadSample>();
            var expected = new List<PlannedCell>();
            RoadSpline.Sample(nodes, RoadPlanner.SampleSpacing, samples);
            RoadPlanner.Footprint(_grid, samples, width, expected, null, includeSettled: true);

            var plan = new LandformPlan();
            plan.Add(new Landform { Kind = LandformKind.Ribbon, Width = width, Height = floor, Nodes = nodes });
            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface);

            Assert.That(surface.Count, Is.EqualTo(expected.Count),
                $"the ribbon covers {surface.Count} cells, the road tool {expected.Count}");
            foreach (var cell in expected)
            {
                var key = cell.Z * Size + cell.X;
                Assert.That(surface.ContainsKey(key), Is.True, $"({cell.X}, {cell.Z}) is missing from the ribbon");
                Assert.That(surface[key], Is.EqualTo(cell.Height).Within(1e-4f),
                    $"({cell.X}, {cell.Z}) is asked for at a different height");
            }
        }

        [Test]
        public void ARibbonGradesBetweenTheHeightsItsNodesWerePlacedAt()
        {
            // How a haul route is drawn: change H between clicks and the spline runs the height
            // along the chain. A ribbon that followed the ground would ask for nothing at all.
            var nodes = Chain((10f, 30f, Ground), (40f, 30f, Ground - 6f));
            var plan = new LandformPlan();
            plan.Add(new Landform { Kind = LandformKind.Ribbon, Width = 3, Height = Ground, Nodes = nodes });
            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface);

            Assert.That(surface[30 * Size + 11], Is.GreaterThan(surface[30 * Size + 38]),
                "it runs downhill from the end it started at");
            Assert.That(surface[30 * Size + 38], Is.LessThan(Ground - 4f), "and gets most of the way down");
        }

        [Test]
        public void ARibbonIsWorkAndNotPaving()
        {
            // The difference from a road: it leaves designations and no Road material anywhere, so
            // nothing about it says "this is a surface machines drive on".
            var floor = Ground - 2f;
            var nodes = Chain((10f, 30f, floor), (40f, 30f, floor));
            var plan = new LandformPlan();
            plan.Add(new Landform { Kind = LandformKind.Ribbon, Width = 3, Height = floor, Nodes = nodes });

            var builder = new LandformBuilder(_grid, _map);
            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells);
            var made = builder.Commit(cells);

            Assert.That(made, Is.GreaterThan(0), "a ribbon cut two metres down is work");
            Assert.That(_map.GetKind(25, 30), Is.EqualTo(DesignationKind.Dig));
            for (var z = 28; z < 33; z++)
                for (var x = 10; x < 41; x++)
                    Assert.That(_grid.GetTopMaterial(x, z), Is.Not.EqualTo(MaterialTable.Road),
                        $"({x}, {z}) should be ground, not road");
        }

        [Test]
        public void ARibbonAndAnAreaShareTheSameOrderingRule()
        {
            // Whatever is drawn later wins, whichever kinds they are — a ribbon cut across a pad is
            // you changing your mind about that ground.
            var padNodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(20f, 20f), new Vector2(40f, 20f), new Vector2(40f, 40f), new Vector2(20f, 40f),
                     })
                padNodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });

            var plan = new LandformPlan();
            plan.Add(new Landform { Height = Ground - 1f, Nodes = padNodes });
            plan.Add(new Landform
            {
                Kind = LandformKind.Ribbon, Width = 3, Height = Ground - 5f,
                Nodes = Chain((10f, 30f, Ground - 5f), (50f, 30f, Ground - 5f)),
            });

            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface);

            Assert.That(surface[30 * Size + 30], Is.EqualTo(Ground - 5f).Within(1e-3f),
                "the ribbon cuts through the pad where it crosses it");
            Assert.That(surface[25 * Size + 30], Is.EqualTo(Ground - 1f).Within(1e-3f),
                "and the pad is untouched where it does not");
        }
    }
}
