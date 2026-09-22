using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>
    /// Slice 17: Ctrl+Z and Ctrl+Y take back and redo the player's designation edits, 50 steps,
    /// and never what the crew does to the map.
    /// </summary>
    public class DesignationHistoryTests
    {
        const int Size = 12;
        const float Ground = 6f;

        TerrainGrid _grid;
        DesignationMap _map;
        DesignationHistory _history;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 0.5f, 0f, 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Dirt, Ground) });
            _map = new DesignationMap(_grid);
            _history = new DesignationHistory(_map);
        }

        [TearDown]
        public void TearDown()
        {
            _history.Dispose();
            _map.Dispose();
        }

        void Stroke(string label, int fromX, int toX, int z, DesignationKind kind, float height)
        {
            _history.Begin(label);
            for (var x = fromX; x <= toX; x++)
                _map.Designate(x, z, kind, height);
            _history.Commit();
        }

        [Test]
        public void UndoTakesBackAWholeStrokeAndRedoPutsItBack()
        {
            Stroke("dig", 2, 6, 3, DesignationKind.Dig, 5f);
            Assert.That(_map.Count, Is.EqualTo(5));

            Assert.That(_history.Undo(), Is.EqualTo("dig"));
            Assert.That(_map.Count, Is.Zero, "one undo, the whole stroke");

            Assert.That(_history.Redo(), Is.EqualTo("dig"));
            Assert.That(_map.Count, Is.EqualTo(5));
            Assert.That(_map.GetTarget(4, 3), Is.EqualTo(5f));
        }

        [Test]
        public void UndoRestoresWhatAnEditReplacedNotJustEmptiness()
        {
            Stroke("dig", 2, 4, 3, DesignationKind.Dig, 5f);
            _history.Begin("zone");
            _map.SetDumpZone(3, 3, true, 9f);
            _history.Commit();
            Stroke("fill over", 2, 4, 3, DesignationKind.Fill, 7f);

            _history.Undo();

            Assert.That(_map.GetKind(3, 3), Is.EqualTo(DesignationKind.Dig), "the dig it replaced is back");
            Assert.That(_map.GetTarget(3, 3), Is.EqualTo(5f));
            Assert.That(_map.IsDumpZone(3, 3), Is.True);
            Assert.That(_map.DumpZoneCap(3, 3), Is.EqualTo(9f));
        }

        [Test]
        public void TheCrewsWorkIsNeverUndone()
        {
            Stroke("dig", 2, 3, 3, DesignationKind.Dig, 5f);
            // The crew digs cell (2, 3) down to 5 m, which meets and clears its designation.
            _grid.SetColumn(2, 3, new[] { new Layer(MaterialTable.Dirt, 5f) });
            _map.Prune();
            Assert.That(_map.GetKind(2, 3), Is.EqualTo(DesignationKind.None));
            // A unit cuts itself a ramp step: an Auto designation, outside any recording.
            _map.Designate(8, 8, DesignationKind.Dig, 5.5f, auto: true);

            Assert.That(_history.UndoCount, Is.EqualTo(1), "only the player's stroke is a step");
            _history.Undo();

            Assert.That(_map.GetKind(3, 3), Is.EqualTo(DesignationKind.None), "the stroke is gone");
            Assert.That(_map.GetKind(8, 8), Is.EqualTo(DesignationKind.Dig), "the ramp step is the unit's, left alone");
            Assert.That(_grid.GetSurfaceHeight(2, 3), Is.EqualTo(5f), "and the ground stays dug");
        }

        [Test]
        public void ANewEditClearsRedoAndAnEmptyEditIsNoStep()
        {
            Stroke("a", 1, 2, 1, DesignationKind.Dig, 5f);
            Stroke("b", 1, 2, 2, DesignationKind.Dig, 5f);
            _history.Undo();
            Assert.That(_history.CanRedo, Is.True);

            Stroke("c", 1, 2, 4, DesignationKind.Dig, 5f);
            Assert.That(_history.CanRedo, Is.False);

            // Designating to a height the ground already meets changes nothing.
            Stroke("nothing", 1, 2, 5, DesignationKind.Dig, 8f);
            Assert.That(_history.UndoCount, Is.EqualTo(2), "a and c");
            Assert.That(_history.NextUndo, Is.EqualTo("c"));
        }

        [Test]
        public void OnlyTheLastFiftyStepsAreKept()
        {
            for (var i = 0; i < 60; i++)
            {
                _history.Begin($"step {i}");
                _map.Designate(i % Size, i / Size, DesignationKind.Dig, 5f);
                _history.Commit();
            }

            Assert.That(_history.UndoCount, Is.EqualTo(DesignationHistory.Capacity));
            var undone = 0;
            while (_history.Undo() != null)
                undone++;
            Assert.That(undone, Is.EqualTo(50));
            Assert.That(_map.Count, Is.EqualTo(10), "the first ten fell off the end and stay");
        }

        // --- the brush -----------------------------------------------------------------------------

        [Test]
        public void TheBrushIsADiscOfGroundCells()
        {
            var cells = new List<Vector2Int>();
            BrushPlan.Cells(_grid, 6, 6, 1, cells);
            Assert.That(cells.Count, Is.EqualTo(5), "radius 1 is a plus");
            BrushPlan.Cells(_grid, 6, 6, 2, cells);
            Assert.That(cells.Count, Is.EqualTo(13));
            BrushPlan.Cells(_grid, 0, 0, 2, cells);
            Assert.That(cells.TrueForAll(c => c.x >= 0 && c.y >= 0), Is.True, "nothing off the map");
            Assert.That(cells.Count, Is.EqualTo(6));
        }

        [Test]
        public void TheBrushCostsWhatDesignatingItWouldCost()
        {
            var cells = new List<Vector2Int>();
            BrushPlan.Cells(_grid, 6, 6, 1, cells);
            BrushPlan.Volumes(_grid, cells, 5f, cuts: true, fills: false, out var cut, out var fill);
            Assert.That(cut, Is.EqualTo(5 * 1f * _grid.CellArea).Within(1e-4f), "five cells a metre down");
            Assert.That(fill, Is.Zero);

            BrushPlan.Volumes(_grid, cells, 6.5f, cuts: true, fills: true, out cut, out fill);
            Assert.That(cut, Is.Zero);
            Assert.That(fill, Is.EqualTo(5 * 0.5f * _grid.CellArea).Within(1e-4f));
        }
    }
}
