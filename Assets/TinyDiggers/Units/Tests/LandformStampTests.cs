using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// A library stamp as a landform (stamps slice B, 2026-09-24): placed in the plan it becomes
    /// fill where it rises and dig where it is turned upside down, it sits on the plan surface an
    /// earlier shape left, and moving it and committing again moves its orders with it.
    /// </summary>
    public class LandformStampTests
    {
        const int Size = 120;
        const float Ground = 20f;

        TerrainGrid _grid;
        DesignationMap _map;
        HeightStamp _mound;

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

            // A flat-topped mound, full height across its middle.
            _mound = ScriptableObject.CreateInstance<HeightStamp>();
            _mound.name = "mound";
            _mound.EdgeFalloff = 0.3f;
            _mound.SetHeights(2, new[] { 1f, 1f, 1f, 1f });
        }

        [TearDown]
        public void TearDown()
        {
            _map.Dispose();
            Object.DestroyImmediate(_mound);
        }

        Landform Stamp(float x, float z, float size = 20f, float height = 3f, bool invert = false) => new Landform
        {
            Kind = LandformKind.Stamp,
            StampName = _mound.name,
            StampAsset = _mound,
            Placement = new StampPlacement
            {
                Centre = new Vector2(x, z), Size = size, Height = height, Invert = invert, Base = Ground,
            },
        };

        static Dictionary<Vector2Int, PlannedCell> ByCell(List<PlannedCell> cells)
        {
            var by = new Dictionary<Vector2Int, PlannedCell>();
            foreach (var cell in cells)
                by[new Vector2Int(cell.X, cell.Z)] = cell;
            return by;
        }

        [Test]
        public void AStampIsFillWhereItRisesAndDigUpsideDown()
        {
            var plan = new LandformPlan();
            plan.Add(Stamp(40.5f, 40.5f));
            plan.Add(Stamp(80.5f, 80.5f, invert: true));
            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells);
            var by = ByCell(cells);

            Assert.That(by[new Vector2Int(40, 40)].IsFill, Is.True);
            Assert.That(by[new Vector2Int(40, 40)].Height, Is.EqualTo(Ground + 3f), "a mound three metres up");
            Assert.That(by[new Vector2Int(80, 80)].IsDig, Is.True);
            Assert.That(by[new Vector2Int(80, 80)].Height, Is.EqualTo(Ground - 3f), "and a hollow three down");
            Assert.That(by.ContainsKey(new Vector2Int(40, 80)), Is.False, "nothing between them");
        }

        [Test]
        public void AStampSitsOnThePadDrawnBeforeIt()
        {
            var plan = new LandformPlan();
            var pad = new Landform { Kind = LandformKind.Area, Height = Ground + 2f };
            foreach (var corner in new[] { new Vector2(20f, 20f), new Vector2(60f, 20f), new Vector2(60f, 60f), new Vector2(20f, 60f) })
                pad.Nodes.Add(new RoadNode { Position = corner, Height = Ground + 2f });
            plan.Add(pad);
            plan.Add(Stamp(40.5f, 40.5f, size: 10f, height: 1f));

            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface);
            Assert.That(surface[40 * Size + 40], Is.EqualTo(Ground + 3f), "one metre on top of the two-metre pad");
            Assert.That(surface[25 * Size + 25], Is.EqualTo(Ground + 2f), "the pad round it untouched");
        }

        [Test]
        public void MovingAStampAndCommittingAgainMovesItsOrders()
        {
            var plan = new LandformPlan();
            var form = plan.Add(Stamp(40.5f, 40.5f));
            var builder = new LandformBuilder(_grid, _map);
            var cells = new List<PlannedCell>();
            plan.Rasterise(_grid, cells);
            Assert.That(builder.Commit(cells), Is.GreaterThan(0));
            Assert.That(_map.GetKind(40, 40), Is.EqualTo(DesignationKind.Fill));

            form.Placement.Centre = new Vector2(80.5f, 80.5f);
            form.Touch();
            plan.Rasterise(_grid, cells);
            builder.Commit(cells);

            Assert.That(_map.GetKind(40, 40), Is.EqualTo(DesignationKind.None), "the old place is let go");
            Assert.That(_map.GetKind(80, 80), Is.EqualTo(DesignationKind.Fill), "and the new one designated");
            Assert.That(_map.GetTarget(80, 80), Is.EqualTo(Ground + 3f));
        }

        [Test]
        public void AStampWithNoStampFoundIsNotDrawn()
        {
            var lost = new Landform { Kind = LandformKind.Stamp, StampName = "no such stamp", Placement = new StampPlacement { Size = 20f } };
            Assert.That(lost.IsDrawn, Is.False);
            Assert.That(lost.Closed, Is.False, "a stamp has no outline to close");
        }
    }
}
