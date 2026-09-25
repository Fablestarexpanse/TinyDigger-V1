using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Putting a plan to the crew, and taking it back when the plan changes. The question under all
    /// of it: can a shape be moved after the crew have started on it without either losing orders
    /// that still stand or leaving behind orders that do not?
    /// </summary>
    public class LandformCommitTests
    {
        const int Size = 40;
        const float Ground = 10f;

        TerrainGrid _grid;
        DesignationMap _map;
        LandformBuilder _builder;
        LandformPlan _plan;

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
            _map = new DesignationMap(_grid);
            _builder = new LandformBuilder(_grid, _map);
            _plan = new LandformPlan();
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        static List<RoadNode> Square(float x0, float z0, float side)
        {
            var corners = new[]
            {
                new Vector2(x0, z0), new Vector2(x0 + side, z0),
                new Vector2(x0 + side, z0 + side), new Vector2(x0, z0 + side),
            };
            var nodes = new List<RoadNode>();
            foreach (var corner in corners)
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            return nodes;
        }

        /// <summary>Rasterises the plan as it now stands and puts it to the crew.</summary>
        int Commit()
        {
            var cells = new List<PlannedCell>();
            _plan.Rasterise(_grid, cells);
            return _builder.Commit(cells);
        }

        [Test]
        public void CommittingAShapeGivesTheCrewOneOrderPerCellThatNeedsWork()
        {
            _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });

            var made = Commit();

            Assert.That(made, Is.EqualTo(100), "ten by ten cells, all of them two metres proud");
            Assert.That(_map.Count, Is.EqualTo(100));
            Assert.That(_map.GetKind(15, 15), Is.EqualTo(DesignationKind.Dig));
            Assert.That(_map.GetTarget(15, 15), Is.EqualTo(Ground - 2f).Within(1e-3f));
        }

        [Test]
        public void MovingAShapeTakesBackTheOrdersOnTheGroundItHasLeft()
        {
            var form = _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });
            Commit();
            Assert.That(_map.GetKind(11, 11), Is.EqualTo(DesignationKind.Dig), "before the move");

            // Slide it five cells right: the left half is let go, the right half is new ground.
            form.Nodes = Square(15f, 10f, 10f);
            form.Touch();
            Commit();

            Assert.That(_map.GetKind(11, 11), Is.EqualTo(DesignationKind.None), "the ground it left is free again");
            Assert.That(_map.GetKind(17, 15), Is.EqualTo(DesignationKind.Dig), "the ground it still covers is untouched");
            Assert.That(_map.GetKind(23, 15), Is.EqualTo(DesignationKind.Dig), "and the ground it has taken is work now");
            Assert.That(_map.Count, Is.EqualTo(100), "still a ten by ten shape, in a new place");
        }

        [Test]
        public void AnOrderThePlayerHasChangedByHandIsNotTakenBack()
        {
            // The guard that matters. Someone re-marks a cell inside the shape to a different depth;
            // moving the shape away must leave their mark alone rather than quietly undoing it.
            var form = _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });
            Commit();
            _map.Designate(11, 11, DesignationKind.Dig, Ground - 5f);

            form.Nodes = Square(25f, 25f, 10f);
            form.Touch();
            Commit();

            Assert.That(_map.GetKind(11, 11), Is.EqualTo(DesignationKind.Dig), "their mark still stands");
            Assert.That(_map.GetTarget(11, 11), Is.EqualTo(Ground - 5f).Within(1e-3f), "at the depth they asked for");
            Assert.That(_map.GetKind(12, 12), Is.EqualTo(DesignationKind.None), "while the plan's own orders are gone");
        }

        [Test]
        public void ACellTwoShapesShareKeepsItsOrderWhenOneOfThemLeaves()
        {
            // Composition decides the height before the builder sees it, so a shared cell arrives
            // once. Removing the shape on top must hand the cell back to the one underneath rather
            // than clearing it.
            _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });
            var top = _plan.Add(new Landform { Height = Ground - 4f, Nodes = Square(15f, 15f, 10f) });
            Commit();
            Assert.That(_map.GetTarget(17, 17), Is.EqualTo(Ground - 4f).Within(1e-3f), "the later shape wins");

            _plan.Remove(top.Id);
            Commit();

            Assert.That(_map.GetKind(17, 17), Is.EqualTo(DesignationKind.Dig), "the cell still belongs to the first shape");
            Assert.That(_map.GetTarget(17, 17), Is.EqualTo(Ground - 2f).Within(1e-3f), "at its height");
            Assert.That(_map.GetKind(22, 22), Is.EqualTo(DesignationKind.None), "and the ground only the second one had is free");
        }

        [Test]
        public void WorkTheCrewHaveAlreadyDoneIsNotUndoneByAnEdit()
        {
            // Ronan's ruling: re-editing leaves the ground alone. The plan is worked out afresh
            // against the ground as it now is, so what was dug stays dug and only what is now wrong
            // becomes new work.
            var form = _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });
            Commit();

            // The crew get one cell down to its target.
            _grid.SetColumn(15, 15, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Ground - 4f),
            });
            _map.Prune();
            Assert.That(_map.GetKind(15, 15), Is.EqualTo(DesignationKind.None), "that cell is done");

            // Now the shape is made shallower than the hole they dug.
            form.Height = Ground - 1f;
            form.Touch();
            Commit();

            Assert.That(_grid.GetSurfaceHeight(15, 15), Is.EqualTo(Ground - 2f).Within(1e-3f),
                "the hole is still a hole — nothing is put back by an edit");
            Assert.That(_map.GetKind(15, 15), Is.EqualTo(DesignationKind.Fill),
                "and the cell that is now too low becomes a fill, which is new work");
        }

        [Test]
        public void ClearingThePlanTakesEveryOrderBack()
        {
            _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });
            Commit();
            Assert.That(_map.Count, Is.EqualTo(100));

            _builder.Clear();

            Assert.That(_map.Count, Is.Zero);
            Assert.That(_builder.Count, Is.Zero);
        }

        [Test]
        public void EveryCellKnowsWhichShapeAskedForIt()
        {
            // What a unit posted to a site reads, and what the tool needs to know which shape the
            // pointer is over.
            var first = _plan.Add(new Landform { Height = Ground - 2f, Nodes = Square(10f, 10f, 10f) });
            var second = _plan.Add(new Landform { Height = Ground - 4f, Nodes = Square(15f, 15f, 10f) });

            var surface = new Dictionary<int, float>();
            var owners = new Dictionary<int, int>();
            _plan.Surface(_grid, surface, owners);

            Assert.That(owners[12 * Size + 12], Is.EqualTo(first.Id));
            Assert.That(owners[17 * Size + 17], Is.EqualTo(second.Id), "where they overlap, the shape that won");
            Assert.That(owners.ContainsKey(30 * Size + 30), Is.False, "and nothing is claimed that nobody drew");
        }
    }
}
