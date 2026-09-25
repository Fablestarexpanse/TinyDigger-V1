using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// What the ground round a shape does once the shape is cut, and the shortcut that makes it
    /// affordable: only the shape's rim can batter anything.
    /// </summary>
    public class LandformBatterTests
    {
        const int Size = 60;
        const float Ground = 20f;

        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f, cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Ground - 2f),
                    });
        }

        static List<RoadNode> Square(float x0, float z0, float side)
        {
            var nodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(x0, z0), new Vector2(x0 + side, z0),
                         new Vector2(x0 + side, z0 + side), new Vector2(x0, z0 + side),
                     })
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            return nodes;
        }

        List<PlannedCell> Footprint(Landform form, bool includeSettled = true)
        {
            var plan = new LandformPlan();
            plan.Add(form);
            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells, includeSettled);
            return cells;
        }

        [Test]
        public void ACutInDirtBattersBackAtTheGroundsOwnAngleAndNoFurther()
        {
            // Two metres down into dirt. The face cannot stand vertical: it falls back at dirt's
            // angle of repose, so the ground outside the pad comes down with it out to the distance
            // that angle allows, and not a cell further.
            const float depth = 2f;
            var footprint = Footprint(new Landform { Height = Ground - depth, Nodes = Square(20f, 20f, 20f) });
            var faces = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, footprint, MaterialTable.DirtLoose, faces);

            var slope = Mathf.Tan(_grid.Materials.Get(MaterialTable.Dirt).AngleOfRepose * Mathf.Deg2Rad) * _grid.CellSize;
            var expected = Mathf.CeilToInt(depth / slope);

            var furthest = 0;
            foreach (var face in faces)
            {
                // Cells left of the pad, on its middle row: how far out the face reaches.
                if (face.Z != 30 || face.X >= 20)
                    continue;
                furthest = Mathf.Max(furthest, 20 - face.X);
            }

            Assert.That(faces.Count, Is.GreaterThan(0), "a two-metre cut has a face");
            Assert.That(furthest, Is.EqualTo(expected).Within(1),
                $"the face should reach {expected} cells at dirt's angle of repose, and it reaches {furthest}");
        }

        [Test]
        public void BatteringTheRimGivesTheSameFacesAsBatteringEveryCell()
        {
            // The claim the whole shortcut rests on. Settling the rim alone has to be *identical* to
            // settling all 400 cells, not merely close — an outside cell's constraint is decided by
            // the nearest planned cell, and the nearest cell of a filled shape is always on its rim.
            var footprint = Footprint(new Landform { Height = Ground - 3f, Nodes = Square(20f, 20f, 20f) });

            var quick = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, footprint, MaterialTable.DirtLoose, quick);

            var slow = new List<PlannedCell>();
            RoadPlanner.Settle(_grid, footprint, MaterialTable.DirtLoose, LandformPlanner.MaxReach, slow);

            var slowByCell = new Dictionary<int, float>();
            foreach (var face in slow)
                slowByCell[face.Z * Size + face.X] = face.Height;

            Assert.That(quick.Count, Is.EqualTo(slow.Count),
                $"the rim gives {quick.Count} faces, the whole footprint gives {slow.Count}");
            foreach (var face in quick)
            {
                Assert.That(slowByCell.ContainsKey(face.Z * Size + face.X), Is.True,
                    $"({face.X}, {face.Z}) is battered by the rim but not by the whole footprint");
                Assert.That(face.Height, Is.EqualTo(slowByCell[face.Z * Size + face.X]).Within(1e-4f),
                    $"({face.X}, {face.Z}) settles to a different height");
            }
        }

        [Test]
        public void ARaggedShapeBattersTheSameWayTooNotJustASquare()
        {
            // A square's rim is easy. The interesting case is a shape with a notch in it, where a
            // cell outside the shape can be nearest to a cell part way along a concave edge.
            var nodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(18f, 18f), new Vector2(40f, 18f), new Vector2(40f, 28f),
                         new Vector2(28f, 28f), new Vector2(28f, 40f), new Vector2(18f, 40f),
                     })
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });

            var footprint = Footprint(new Landform { Height = Ground - 2.5f, Nodes = nodes });

            var quick = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, footprint, MaterialTable.DirtLoose, quick);
            var slow = new List<PlannedCell>();
            RoadPlanner.Settle(_grid, footprint, MaterialTable.DirtLoose, LandformPlanner.MaxReach, slow);

            var slowByCell = new Dictionary<int, float>();
            foreach (var face in slow)
                slowByCell[face.Z * Size + face.X] = face.Height;

            Assert.That(quick.Count, Is.EqualTo(slow.Count));
            foreach (var face in quick)
                Assert.That(face.Height, Is.EqualTo(slowByCell[face.Z * Size + face.X]).Within(1e-4f),
                    $"({face.X}, {face.Z}) settles differently in the notch");
        }

        [Test]
        public void AFillBuildsAnEmbankmentOutAtTheSpoilsAngle()
        {
            // The other direction: a pad raised above the land stands on a bank of tipped spoil,
            // which runs out at the spoil's angle rather than the ground's.
            var footprint = Footprint(new Landform { Height = Ground + 2f, Nodes = Square(20f, 20f, 20f) });
            var faces = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, footprint, MaterialTable.DirtLoose, faces);

            var raised = 0;
            foreach (var face in faces)
                if (face.IsFill)
                    raised++;

            Assert.That(raised, Is.GreaterThan(0), "a pad standing two metres proud needs a bank under its edge");
            foreach (var face in faces)
                Assert.That(face.IsDig, Is.False, "and nothing round it is cut");
        }

        [Test]
        public void AShapeThatSitsOnTheGroundMovesNothingAroundIt()
        {
            var footprint = Footprint(new Landform { Height = Ground, Nodes = Square(20f, 20f, 20f) });
            var faces = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, footprint, MaterialTable.DirtLoose, faces);

            Assert.That(faces, Is.Empty, "nothing to batter where nothing is cut or filled");
        }

        [Test]
        public void TheReachGrowsWithTheCutRatherThanBeingGuessed()
        {
            // A deep cut's face has to be allowed to reach further than a shallow one's, and a
            // shallow one must not pay to probe ground it could never touch.
            var shallow = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, Footprint(new Landform { Height = Ground - 1f, Nodes = Square(20f, 20f, 20f) }),
                MaterialTable.DirtLoose, shallow);
            var deep = new List<PlannedCell>();
            LandformPlanner.Batters(_grid, Footprint(new Landform { Height = Ground - 6f, Nodes = Square(20f, 20f, 20f) }),
                MaterialTable.DirtLoose, deep);

            Assert.That(deep.Count, Is.GreaterThan(shallow.Count),
                "six metres down disturbs more ground than one");
        }
    }
}
