using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The freehand brush. It sculpts the *plan surface*, never the ground, because the crew do the
    /// work: a raise stroke on open country is a mound to be filled and a lower stroke is a hollow
    /// to be dug.
    /// </summary>
    public class LandformBrushTests
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

        static Landform Stroke(BrushMode mode, float amount, params Vector2[] at)
        {
            var form = new Landform { Kind = LandformKind.Brush, Height = Ground };
            foreach (var point in at)
                form.Dabs.Add(new BrushDab { At = point, Radius = 4, Mode = mode, Amount = amount });
            return form;
        }

        Dictionary<int, float> Surface(params Landform[] forms)
        {
            var plan = new LandformPlan();
            foreach (var form in forms)
                plan.Add(form);
            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface);
            return surface;
        }

        float At(Dictionary<int, float> surface, int x, int z) =>
            surface.TryGetValue(z * Size + x, out var height) ? height : _grid.GetSurfaceHeight(x, z);

        [Test]
        public void ARaisePressLiftsThePlanMostAtItsCentreAndNotAtAllAtItsRim()
        {
            var surface = Surface(Stroke(BrushMode.Raise, 2f, new Vector2(30.5f, 30.5f)));
            var ground = _grid.GetSurfaceHeight(30, 30);

            Assert.That(At(surface, 30, 30), Is.GreaterThan(ground), "the middle comes up");
            Assert.That(At(surface, 32, 30), Is.LessThan(At(surface, 30, 30)), "less so further out");
            Assert.That(At(surface, 34, 30), Is.EqualTo(_grid.GetSurfaceHeight(34, 30)).Within(0.3f),
                "and the rim of the press is left where it was, so a stroke has no edge to it");
        }

        [Test]
        public void ALowerPressDigsRatherThanFills()
        {
            var surface = Surface(Stroke(BrushMode.Lower, 2f, new Vector2(30.5f, 30.5f)));

            Assert.That(At(surface, 30, 30), Is.LessThan(_grid.GetSurfaceHeight(30, 30)));
        }

        [Test]
        public void SmoothingTakesTheStepOutOfAPlanRatherThanOutOfTheGround()
        {
            // A pad with a hard edge, then a stroke along that edge: the step should soften. The
            // *ground* is untouched throughout — nothing here digs anything, it changes what is
            // being asked for.
            var nodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(20f, 20f), new Vector2(30f, 20f), new Vector2(30f, 40f), new Vector2(20f, 40f),
                     })
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            var pad = new Landform { Height = Ground - 4f, Nodes = nodes };

            var sharp = Surface(pad);
            var stepBefore = Mathf.Abs(At(sharp, 29, 30) - At(sharp, 31, 30));

            var softened = Surface(pad, Stroke(BrushMode.Smooth, 1f,
                new Vector2(30.5f, 28.5f), new Vector2(30.5f, 30.5f), new Vector2(30.5f, 32.5f)));
            var stepAfter = Mathf.Abs(At(softened, 29, 30) - At(softened, 31, 30));

            Assert.That(stepBefore, Is.GreaterThan(3f), "the pad's edge really is a step");
            Assert.That(stepAfter, Is.LessThan(stepBefore), $"and smoothing softens it: {stepBefore:0.##} to {stepAfter:0.##}");
            Assert.That(_grid.GetSurfaceHeight(30, 30), Is.EqualTo(Ground).Within(0.6f),
                "while the ground has not moved — the brush works the plan, not the land");
        }

        [Test]
        public void FlattenPullsThePlanTowardsTheHeightTheStrokeWasStartedAt()
        {
            var nodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(20f, 20f), new Vector2(40f, 20f), new Vector2(40f, 40f), new Vector2(20f, 40f),
                     })
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            var pad = new Landform { Height = Ground - 6f, Nodes = nodes };

            var stroke = Stroke(BrushMode.Flatten, 1f, new Vector2(30.5f, 30.5f));
            stroke.Height = Ground - 1f;   // what the stroke was started at
            var surface = Surface(pad, stroke);

            Assert.That(At(surface, 30, 30), Is.GreaterThan(Ground - 6f),
                "the flattened middle comes up towards the height asked for");
            Assert.That(At(surface, 30, 30), Is.LessThanOrEqualTo(Ground - 1f + 1e-3f), "and not past it");
        }

        [Test]
        public void AStrokeIsKeptAsPressesSoItFollowsWhatIsUnderIt()
        {
            // The reason a stroke stores presses rather than the heights they produced: change the
            // shape beneath it and the stroke works against the new one, instead of being stranded
            // at the height it was first worked out at.
            var nodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(20f, 20f), new Vector2(40f, 20f), new Vector2(40f, 40f), new Vector2(20f, 40f),
                     })
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            var pad = new Landform { Height = Ground - 4f, Nodes = nodes };
            var stroke = Stroke(BrushMode.Raise, 2f, new Vector2(30.5f, 30.5f));

            var low = At(Surface(pad, stroke), 30, 30);
            pad.Height = Ground + 4f;
            var high = At(Surface(pad, stroke), 30, 30);

            Assert.That(high - low, Is.EqualTo(8f).Within(0.6f),
                $"the stroke rides the pad it sits on: {low:0.##} against {high:0.##}");
        }

        [Test]
        public void AStrokeBecomesWorkLikeAnythingElseInThePlan()
        {
            var plan = new LandformPlan();
            plan.Add(Stroke(BrushMode.Lower, 3f, new Vector2(30.5f, 30.5f)));
            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells);

            Assert.That(cells.Count, Is.GreaterThan(0), "a hollow painted on open country is digging to be done");
            foreach (var cell in cells)
                Assert.That(cell.IsDig, Is.True, "all of it digging, since the stroke only went down");
        }

        [Test]
        public void EveryPressOfAStrokeIsWorkedOutAgainstTheSurfaceBeforeIt()
        {
            // A press that read its own output as it went would drag the result towards whichever
            // corner the loop happened to start in. Two presses side by side must give the same
            // answer whichever order the cells inside them are visited.
            var surface = Surface(Stroke(BrushMode.Raise, 1f,
                new Vector2(30.5f, 30.5f), new Vector2(31.5f, 30.5f)));

            // The two presses sit either side of x = 31, so the cell to compare with 29 is 32 — its
            // mirror image about that line, one cell from the near press and two from the far one.
            var left = At(surface, 29, 30) - _grid.GetSurfaceHeight(29, 30);
            var right = At(surface, 32, 30) - _grid.GetSurfaceHeight(32, 30);

            Assert.That(left, Is.EqualTo(right).Within(_grid.HeightStep + 1e-3f),
                $"the stroke should be even about its middle: {left:0.##} against {right:0.##}");
        }
    }
}
