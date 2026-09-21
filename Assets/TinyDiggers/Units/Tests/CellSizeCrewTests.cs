using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Half-metre cells (slice 11) for the crew: loads are m³ whatever the cell, a step on a
    /// quarter-square-metre cell holds a quarter of the volume, and paths and grades are measured
    /// in metres.
    /// </summary>
    public class CellSizeCrewTests
    {
        const float Half = 0.5f;
        const float Tolerance = 1e-3f;

        static TerrainGrid DirtField(int size = 9)
        {
            var grid = new TerrainGrid(size, size, MaterialTable.CreateDefault(), Half, 0f, Half);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 4f) });
            return grid;
        }

        [Test]
        public void DiggingHalfAMetreFromAHalfMetreCellIsAnEighthOfACubicMetre()
        {
            var grid = DirtField();
            var scoop = new MaterialInventory(capacity: 5f);

            var report = Excavation.Dig(grid, scoop, 4, 4, 0, Half);

            Assert.That(grid.GetSurfaceHeight(4, 4), Is.EqualTo(5.5f).Within(Tolerance), "the ground drops by the depth dug");
            Assert.That(report.InPlace, Is.EqualTo(0.125f).Within(Tolerance), "0.5 m × 0.25 m² in place");
            Assert.That(scoop.Total, Is.EqualTo(0.125f * 1.25f).Within(Tolerance), "bulked by dirt's 1.25");
        }

        [Test]
        public void TippingAQuarterCubicMetreOnAHalfMetreCellRaisesItAMetre()
        {
            var grid = DirtField();
            var load = new MaterialInventory(capacity: 5f);
            load.Add(MaterialTable.DirtLoose, 1f);

            var report = Excavation.Tip(grid, load, 4, 4, 0.25f);

            Assert.That(report.Tipped, Is.EqualTo(0.25f).Within(Tolerance));
            Assert.That(grid.GetSurfaceHeight(4, 4), Is.EqualTo(7f).Within(Tolerance), "0.25 m³ over 0.25 m² is 1 m, two steps");
            Assert.That(load.Total, Is.EqualTo(0.75f).Within(Tolerance));
        }

        [Test]
        public void DigThenTipPutsTheSameVolumeBack()
        {
            var grid = DirtField();
            var scoop = new MaterialInventory(capacity: 5f);
            var before = grid.GetSurfaceHeight(2, 2) + grid.GetSurfaceHeight(6, 6);

            Excavation.Dig(grid, scoop, 2, 2, 0, 1f);
            // Loose dirt: tipped at its bulked volume, rounded down to whole steps.
            Excavation.Tip(grid, scoop, 6, 6);

            var raised = grid.GetSurfaceHeight(6, 6) - 6f;
            Assert.That(raised, Is.EqualTo(1f).Within(Tolerance), "1 m dug, 1.25 m loose, 1 m of it tips as whole half-metre steps");
            Assert.That(scoop.Total, Is.EqualTo(0.25f * 0.25f).Within(Tolerance), "the quarter metre that is not a whole step stays in the load");
            Assert.That(grid.GetSurfaceHeight(2, 2) + grid.GetSurfaceHeight(6, 6), Is.EqualTo(before).Within(Tolerance));
        }

        [Test]
        public void PathCostsAreMetresAndAStepIsOneCellHigh()
        {
            var grid = DirtField();
            var pathfinder = new GridPathfinder(grid);

            Assert.That(pathfinder.MaxStepHeight, Is.EqualTo(Half), "one half-metre step is a 45-degree climb");
            Assert.That(pathfinder.StepCost(1, 1, 2, 1), Is.EqualTo(Half).Within(Tolerance));
            Assert.That(pathfinder.StepCost(1, 1, 2, 2), Is.EqualTo(Half * Mathf.Sqrt(2f)).Within(Tolerance));

            grid.Remove(3, 3, Half, new System.Collections.Generic.List<MaterialVolume>(), bulk: false);
            Assert.That(pathfinder.CanStep(2, 3, 3, 3), Is.True, "half a metre down is drivable");
            grid.Remove(3, 3, Half, new System.Collections.Generic.List<MaterialVolume>(), bulk: false);
            Assert.That(pathfinder.CanStep(2, 3, 3, 3), Is.False, "a metre down over half a metre is not");
        }

        [Test]
        public void RoadGradeIsRiseOverMetres()
        {
            var from = new RoadPoint(new Vector2Int(0, 0), 0f);
            var to = new RoadPoint(new Vector2Int(8, 0), 1f);

            Assert.That(Blueprints.LegGrade(from, to), Is.EqualTo(0.125f).Within(Tolerance), "8 one-metre cells");
            Assert.That(Blueprints.LegGrade(from, to, Half), Is.EqualTo(0.25f).Within(Tolerance), "8 half-metre cells is 4 m");
        }

        [Test]
        public void PlanVolumesAreCubicMetres()
        {
            var grid = DirtField();
            var plan = new System.Collections.Generic.List<PlannedCell>();
            Blueprints.PlanLevel(grid, new RectInt(0, 0, 4, 4), 5f, plan);

            Blueprints.Volumes(plan, out var cut, out var fill, grid.CellArea);

            Assert.That(cut, Is.EqualTo(16 * 1f * 0.25f).Within(Tolerance), "16 cells, 1 m each, 0.25 m² each");
            Assert.That(fill, Is.Zero);
        }
    }
}
