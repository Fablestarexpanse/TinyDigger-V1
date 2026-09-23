using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Turning a drawn outline into cells: which cells are inside it, how deep in they lie, and what
    /// each one is asked to become. All of it plain arithmetic, so none of it needs a scene.
    /// </summary>
    public class LandformRasterTests
    {
        const int Size = 40;
        const float Ground = 10f;

        TerrainGrid _grid;

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
        }

        /// <summary>A closed outline through the given corners, in cells.</summary>
        static List<RoadNode> Loop(params Vector2[] corners)
        {
            var nodes = new List<RoadNode>();
            foreach (var corner in corners)
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            return nodes;
        }

        /// <summary>The outline walked and filled, as cells.</summary>
        List<int> FillOf(List<RoadNode> loop, bool curved = false)
        {
            var samples = new List<RoadSample>();
            var outline = new List<Vector2>();
            var cells = new List<int>();
            LandformSpline.Polygon(new Landform { Nodes = loop, Curved = curved },
                LandformPlan.SampleSpacing, samples, outline);
            LandformRaster.Fill(_grid, outline, cells);
            return cells;
        }

        static bool Has(List<int> cells, int x, int z) => cells.Contains(z * Size + x);

        [Test]
        public void ASquareFillsTheCellsInsideItAndNoOthers()
        {
            // Corners on cell boundaries, so there is no argument about which cells are half in:
            // a cell belongs to the shape when its *centre* does.
            var cells = FillOf(Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                new Vector2(20f, 20f), new Vector2(10f, 20f)));

            Assert.That(cells.Count, Is.EqualTo(100), "ten by ten cells, however the curve is walked");
            Assert.That(Has(cells, 10, 10), Is.True, "the first cell inside the corner");
            Assert.That(Has(cells, 19, 19), Is.True, "and the last");
            Assert.That(Has(cells, 9, 15), Is.False, "nothing outside the left edge");
            Assert.That(Has(cells, 20, 15), Is.False, "nor past the right");
        }

        [Test]
        public void AConcaveOutlineLeavesItsNotchOut()
        {
            // An L. The notch is the top right quarter, and a scanline fill has to leave it out —
            // which is the whole reason for filling by crossings rather than by a bounding box.
            var cells = FillOf(Loop(
                new Vector2(10f, 10f), new Vector2(20f, 10f), new Vector2(20f, 15f),
                new Vector2(15f, 15f), new Vector2(15f, 20f), new Vector2(10f, 20f)));

            Assert.That(Has(cells, 12, 12), Is.True, "the foot of the L");
            Assert.That(Has(cells, 18, 12), Is.True, "the toe");
            Assert.That(Has(cells, 12, 18), Is.True, "the upright");
            Assert.That(Has(cells, 18, 18), Is.False, "and not the notch");
        }

        [Test]
        public void ACurvedOutlineBowsOutPastItsCorners()
        {
            // Why corners are the default. The same four nodes, curved, do not make a square: a
            // Catmull-Rom through them bulges past every edge, and a ten-cell pad came out twelve by
            // eleven. Fine for a road, wrong for a pad, so a shape only curves when it is asked to.
            var corners = new[]
            {
                new Vector2(10f, 10f), new Vector2(20f, 10f), new Vector2(20f, 20f), new Vector2(10f, 20f),
            };

            var straight = FillOf(Loop(corners));
            var curved = FillOf(Loop(corners), curved: true);

            Assert.That(straight.Count, Is.EqualTo(100));
            Assert.That(curved.Count, Is.GreaterThan(straight.Count),
                "a curve through the corners covers more ground than the rectangle they describe");
        }

        [Test]
        public void TheLoopClosesWithoutAKinkAtTheSeam()
        {
            // RoadSpline treats the first and last node of a chain as ends and gives them a doubled
            // one-sided tangent. Sampling a loop as an open chain therefore bends it exactly where
            // the player joined it up. The seam is the join, and the curve has to run through it as
            // smoothly as through any other node.
            var loop = Loop(new Vector2(20f, 12f), new Vector2(28f, 20f),
                new Vector2(20f, 28f), new Vector2(12f, 20f));
            var samples = new List<RoadSample>();
            LandformSpline.SampleLoop(loop, 0.25f, samples);

            Assert.That(samples.Count, Is.GreaterThan(8));
            var first = samples[0].Direction;
            var last = samples[samples.Count - 1].Direction;
            var turn = Vector2.Angle(last, first);

            Assert.That(turn, Is.LessThan(12f),
                $"the curve should run through the seam, not corner at it — it turns {turn:0.#}°");
        }

        [Test]
        public void HowFarInsideACellLiesIsOneAtTheRimAndGrowsTowardsTheMiddle()
        {
            var cells = FillOf(Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                new Vector2(20f, 20f), new Vector2(10f, 20f)));
            var inside = new List<InsideCell>();
            LandformRaster.Inside(_grid, cells, inside);

            var byCell = new Dictionary<int, InsideCell>();
            foreach (var one in inside)
                byCell[one.Cell] = one;

            Assert.That(byCell[10 * Size + 10].Distance, Is.EqualTo(1f).Within(1e-3f),
                "a cell on the rim is one cell from the outside, so a batter starts at the ground");
            Assert.That(byCell[15 * Size + 14].Distance, Is.EqualTo(5f).Within(1e-3f),
                "and the middle of a ten-cell square is five in");
            Assert.That(byCell[15 * Size + 14].EdgeGround, Is.EqualTo(Ground).Within(1e-3f),
                "carrying the ground at the nearest point outside, so a profile follows the land");
        }

        [Test]
        public void ThePerimeterIsTheRimAndNothingElse()
        {
            var cells = FillOf(Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                new Vector2(20f, 20f), new Vector2(10f, 20f)));
            var rim = new List<int>();
            LandformRaster.Perimeter(_grid, cells, rim);

            // Ten by ten: the ring is a hundred less the eight by eight inside it.
            Assert.That(rim.Count, Is.EqualTo(100 - 64));
            Assert.That(Has(rim, 10, 10), Is.True, "a corner is on the rim");
            Assert.That(Has(rim, 15, 10), Is.True, "so is an edge");
            Assert.That(Has(rim, 15, 15), Is.False, "the middle is not");
        }

        [Test]
        public void AnAreaDigsWhereTheGroundIsHighAndFillsWhereItIsLow()
        {
            // A step through the middle of the square: half the ground two metres up, half two down.
            for (var z = 0; z < Size; z++)
                for (var x = 15; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Ground),
                    });

            var plan = new LandformPlan();
            plan.Add(new Landform
            {
                Kind = LandformKind.Area,
                Height = Ground,
                Nodes = Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                    new Vector2(20f, 20f), new Vector2(10f, 20f)),
            });

            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells);
            Blueprints.Volumes(cells, out var cut, out var fill, _grid.CellArea);

            // Fifty cells stand two metres proud and want cutting; the other fifty are already there.
            Assert.That(cut, Is.EqualTo(50 * 2f * _grid.CellArea).Within(1e-3f), "half the square cut two metres");
            Assert.That(fill, Is.Zero, "and nothing to fill");
            Assert.That(cells.Count, Is.EqualTo(50), "cells already at the target are left out");
        }

        [Test]
        public void TheGhostKeepsTheCellsThatNeedNoWorkSoTheShapeDrawsWhole()
        {
            var plan = new LandformPlan();
            plan.Add(new Landform
            {
                Kind = LandformKind.Area,
                Height = Ground,
                Nodes = Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                    new Vector2(20f, 20f), new Vector2(10f, 20f)),
            });

            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells, includeSettled: true);

            Assert.That(cells.Count, Is.EqualTo(100), "every cell of the shape, work or no work");
            foreach (var cell in cells)
                Assert.That(cell.Cut + cell.Fill, Is.Zero, "and none of them any work, on flat ground at its own height");
        }

        [Test]
        public void ARampRunsEvenlyAcrossTheShapeInStepsAMachineCanBuild()
        {
            var plan = new LandformPlan();
            plan.Add(new Landform
            {
                Kind = LandformKind.Area,
                Height = Ground,
                HeightB = Ground + 5f,
                Ramped = true,
                RampFrom = new Vector2(10f, 15f),
                RampTo = new Vector2(20f, 15f),
                Nodes = Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                    new Vector2(20f, 20f), new Vector2(10f, 20f)),
            });

            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells, includeSettled: true);

            var heights = new Dictionary<int, float>();
            foreach (var cell in cells)
                heights[cell.Z * Size + cell.X] = cell.Height;

            Assert.That(heights[15 * Size + 10], Is.EqualTo(Ground + 0.25f).Within(0.3f), "low at the near edge");
            Assert.That(heights[15 * Size + 19], Is.EqualTo(Ground + 4.75f).Within(0.3f), "high at the far one");
            Assert.That(heights[15 * Size + 10], Is.LessThan(heights[15 * Size + 15]), "and rising between");

            foreach (var cell in cells)
                Assert.That(cell.Height / _grid.HeightStep, Is.EqualTo(Mathf.Round(cell.Height / _grid.HeightStep)).Within(1e-3f),
                    "every height a whole number of steps, so the ramp comes out as even treads");
        }

        [Test]
        public void TheLaterShapeWinsWhereTwoOverlapUnlessItIsToldToDigOnly()
        {
            var square = Loop(new Vector2(10f, 10f), new Vector2(20f, 10f),
                new Vector2(20f, 20f), new Vector2(10f, 20f));
            var over = Loop(new Vector2(15f, 15f), new Vector2(25f, 15f),
                new Vector2(25f, 25f), new Vector2(15f, 25f));

            var plan = new LandformPlan();
            plan.Add(new Landform { Height = Ground - 3f, Nodes = square });
            var second = plan.Add(new Landform { Height = Ground - 1f, Nodes = over });

            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface);
            Assert.That(surface[17 * Size + 17], Is.EqualTo(Ground - 1f).Within(1e-3f),
                "the shape drawn later wins where they share cells");

            // Told to dig only, it cannot raise what the first one already cut.
            second.Blend = LandformBlend.Lower;
            plan.Surface(_grid, surface);
            Assert.That(surface[17 * Size + 17], Is.EqualTo(Ground - 3f).Within(1e-3f),
                "dig only takes the lower of the two");
            Assert.That(surface[22 * Size + 22], Is.EqualTo(Ground - 1f).Within(1e-3f),
                "and is unchanged where it is alone");
        }

        [Test]
        public void NothingIsPlannedOffTheMapOrInTheVoid()
        {
            for (var z = 10; z < 20; z++)
                _grid.SetVoid(12, z, true);

            var plan = new LandformPlan();
            plan.Add(new Landform
            {
                Height = Ground - 2f,
                // Hangs off the left edge of the map.
                Nodes = Loop(new Vector2(-5f, 10f), new Vector2(20f, 10f),
                    new Vector2(20f, 20f), new Vector2(-5f, 20f)),
            });

            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells, includeSettled: true);

            foreach (var cell in cells)
            {
                Assert.That(cell.X, Is.InRange(0, Size - 1));
                Assert.That(cell.Z, Is.InRange(0, Size - 1));
                Assert.That(_grid.IsVoid(cell.X, cell.Z), Is.False, "a void cell is not part of the world");
            }

            Assert.That(cells.Count, Is.EqualTo(20 * 10 - 10), "the map's own cells, less the void stripe");
        }
    }
}
