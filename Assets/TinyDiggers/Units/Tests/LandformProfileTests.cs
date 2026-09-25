using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Heaps and pits: the slice that answers Ronan's "dump sites and quarries are primitive"
    /// (2026-09-23). The data was never primitive — <see cref="DesignationMap"/> has always held a
    /// cap and a floor per cell and the crew have always honoured both per cell. The tools were: a
    /// dragged rectangle with one number on it, so a heap was a slab and a pit was a box.
    /// </summary>
    public class LandformProfileTests
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

        /// <summary>The distance field the profile is built from, so a test can check the formula.</summary>
        List<InsideCell> Inside(Landform form)
        {
            var samples = new List<RoadSample>();
            var outline = new List<Vector2>();
            var cells = new List<int>();
            var inside = new List<InsideCell>();
            LandformSpline.Polygon(form, LandformPlan.SampleSpacing, samples, outline);
            LandformRaster.Fill(_grid, outline, cells);
            LandformRaster.Inside(_grid, cells, inside);
            return inside;
        }

        Dictionary<int, float> Profile(Landform form)
        {
            var plan = new LandformPlan();
            plan.Add(form);
            var caps = new Dictionary<int, float>();
            var floors = new Dictionary<int, float>();
            plan.Zones(_grid, caps, floors);
            return form.Kind == LandformKind.Heap ? caps : floors;
        }

        [Test]
        public void AHeapRisesFromItsEdgeAtTheSpoilsAngleAndFlattensAtTheCrown()
        {
            // Not a slab. The rim of the heap sits on the ground and it climbs inwards at the angle
            // loose dirt actually stands at, until it reaches the crown you drew.
            //
            // The expectation is worked out from the same distance field the profile is built from
            // rather than written in by hand: a heap that reaches its crown depends on how wide the
            // outline is against how steep the spoil stands, and hard-coding either makes a test
            // that fails when the material table is tuned rather than when the code is wrong.
            var form = new Landform
            {
                Kind = LandformKind.Heap, Height = Ground + 6f, Nodes = Square(20f, 20f, 20f),
            };
            var caps = Profile(form);
            var inside = Inside(form);
            var slope = Mathf.Tan(_grid.Materials.Get(form.Spoil).AngleOfRepose * Mathf.Deg2Rad) * _grid.CellSize;

            foreach (var cell in inside)
            {
                var wanted = Mathf.Min(form.Height, cell.EdgeGround + cell.Distance * slope);
                wanted = Mathf.Round(wanted / _grid.HeightStep) * _grid.HeightStep;
                Assert.That(caps[cell.Cell], Is.EqualTo(wanted).Within(1e-3f),
                    $"cell ({cell.Cell % Size}, {cell.Cell / Size}) is {cell.Distance:0.##} cells in from ground "
                    + $"{cell.EdgeGround:0.##} at {slope:0.###} m a cell, so it should cap at {wanted:0.##}");
            }

            var rim = caps[30 * Size + 20];
            var middle = caps[30 * Size + 29];
            Assert.That(middle, Is.GreaterThan(rim), $"the middle {middle:0.##} stands above the rim {rim:0.##}");
            // One cell's worth of slope, and then whatever rounding to the height step adds: the
            // profile is snapped like every other height in the game, so the bound has to allow it.
            Assert.That(rim, Is.LessThanOrEqualTo(_grid.GetSurfaceHeight(20, 30) + slope + _grid.HeightStep),
                $"the rim is barely off the ground — {rim:0.##} against ground {_grid.GetSurfaceHeight(20, 30):0.##}");
        }

        [Test]
        public void ACrownTooHighForTheOutlineIsHeldBackByTheAngleNotReached()
        {
            // A tall crown on a small outline: the heap cannot be a spike, so its middle is decided
            // by the repose angle and the crown is never reached.
            var caps = Profile(new Landform
            {
                Kind = LandformKind.Heap, Height = Ground + 40f, Nodes = Square(28f, 28f, 6f),
            });

            var tallest = 0f;
            foreach (var pair in caps)
                tallest = Mathf.Max(tallest, pair.Value);

            Assert.That(tallest, Is.LessThan(Ground + 10f),
                $"a six-cell heap cannot be forty metres tall — it got to {tallest - Ground:0.#} m");
        }

        [Test]
        public void AHeapsCapacityIsWhatItHoldsAndFallsAsSpoilArrives()
        {
            var form = new Landform { Kind = LandformKind.Heap, Height = Ground + 4f, Nodes = Square(20f, 20f, 20f) };
            var caps = Profile(form);
            var before = LandformProfile.Capacity(_grid, caps);

            Assert.That(before, Is.GreaterThan(0f));

            // Tip into it: a column comes up two metres.
            _grid.SetColumn(30, 30, new[]
            {
                new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Ground),
            });
            var after = LandformProfile.Capacity(_grid, caps);

            Assert.That(after, Is.LessThan(before), "what it still holds falls as spoil arrives");
            Assert.That(before - after, Is.EqualTo(2f * _grid.CellArea).Within(1e-3f),
                "by exactly what was tipped");
        }

        [Test]
        public void APitStepsDownInBenchesAndNeverCutsBelowItsFloor()
        {
            var floor = Ground - 8f;
            var floors = Profile(new Landform
            {
                Kind = LandformKind.Pit, Height = floor, BenchHeight = 2f, BenchWidth = 4f,
                Nodes = Square(20f, 20f, 20f),
            });

            // Walking in from the rim: the first bench is at the ground less one step, and each four
            // cells drops another.
            Assert.That(floors[30 * Size + 20], Is.EqualTo(Ground - 2f).Within(1e-3f), "the first bench");
            Assert.That(floors[30 * Size + 24], Is.EqualTo(Ground - 4f).Within(1e-3f), "the second, four cells in");
            Assert.That(floors[30 * Size + 28], Is.EqualTo(Ground - 6f).Within(1e-3f), "the third");

            foreach (var pair in floors)
                Assert.That(pair.Value, Is.GreaterThanOrEqualTo(floor - 1e-3f),
                    "and nothing is ever cut below the floor that was asked for");
        }

        [Test]
        public void APitsReservesAreWhatIsStillInIt()
        {
            var form = new Landform
            {
                Kind = LandformKind.Pit, Height = Ground - 6f, BenchHeight = 2f, BenchWidth = 4f,
                Nodes = Square(20f, 20f, 20f),
            };
            var floors = Profile(form);
            var reserves = LandformProfile.Reserves(_grid, floors);

            var byHand = 0f;
            foreach (var pair in floors)
                byHand += Ground - pair.Value;

            Assert.That(reserves, Is.EqualTo(byHand * _grid.CellArea).Within(1e-2f));
            Assert.That(LandformProfile.Benches(_grid, form, floors), Is.EqualTo(3),
                "six metres down in two-metre benches is three of them");
        }

        [Test]
        public void ProfilesFollowTheLieOfTheLandRatherThanOneHeight()
        {
            // The reason the inside-distance walk carries the ground at the nearest outside cell
            // with it: a heap built along a slope has its foot on the slope, not on one number.
            for (var z = 0; z < Size; z++)
                for (var x = 30; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Ground + 2f),
                    });

            var form = new Landform
            {
                Kind = LandformKind.Heap, Height = Ground + 20f, Nodes = Square(20f, 20f, 20f),
            };
            var caps = Profile(form);

            // The two rim cells either side, which are the same distance in and differ only in the
            // ground they stand on.
            var low = caps[30 * Size + 20];
            var high = caps[30 * Size + 39];
            // The step is measured rather than assumed. A column's surface is not simply the sum of
            // the layers put into it, so a test that writes down the number it *meant* to make tests
            // its own arithmetic instead of the profile's.
            var step = _grid.GetSurfaceHeight(39, 30) - _grid.GetSurfaceHeight(20, 30);

            Assert.That(step, Is.GreaterThan(_grid.HeightStep), "the ground really does step up");
            Assert.That(high - low, Is.EqualTo(step).Within(_grid.HeightStep + 1e-3f),
                $"the heap's foot follows that step: {low:0.##} on the low side against {high:0.##} on the high");
        }

        [Test]
        public void CommittingAHeapMarksTheZoneWithoutCappingIt()
        {
            // Ronan's ruling: the crown is what you drew, not a wall. The zone goes in so the crew
            // know where to tip, and nothing refuses them once it is full — the spoil piles on and
            // the heap spreads at its own angle, which the slump does by itself.
            var plan = new LandformPlan();
            plan.Add(new Landform { Kind = LandformKind.Heap, Height = Ground + 4f, Nodes = Square(20f, 20f, 20f) });
            var builder = new LandformBuilder(_grid, _map);
            var caps = new Dictionary<int, float>();
            var floors = new Dictionary<int, float>();
            plan.Zones(_grid, caps, floors);
            builder.CommitZones(caps, floors);

            Assert.That(_map.DumpZoneCount, Is.EqualTo(caps.Count), "every cell of the heap is somewhere to tip");
            Assert.That(_map.DumpZoneCap(30, 30), Is.EqualTo(float.PositiveInfinity),
                "and nothing is capped: the crown is a shape, not a limit");
        }

        [Test]
        public void CommittingAPitPutsItsFloorInPerCell()
        {
            var plan = new LandformPlan();
            plan.Add(new Landform
            {
                Kind = LandformKind.Pit, Height = Ground - 6f, BenchHeight = 2f, BenchWidth = 4f,
                Nodes = Square(20f, 20f, 20f),
            });
            var builder = new LandformBuilder(_grid, _map);
            var caps = new Dictionary<int, float>();
            var floors = new Dictionary<int, float>();
            plan.Zones(_grid, caps, floors);
            builder.CommitZones(caps, floors);

            Assert.That(_map.QuarryCount, Is.EqualTo(floors.Count));
            // The bench, not one flat floor everywhere — which is the whole difference from the
            // rectangle tool.
            Assert.That(_map.QuarryFloor(20, 30), Is.EqualTo(Ground - 2f).Within(1e-3f));
            Assert.That(_map.QuarryFloor(28, 30), Is.EqualTo(Ground - 6f).Within(1e-3f));
            Assert.That(_map.QuarryFloor(20, 30), Is.Not.EqualTo(_map.QuarryFloor(28, 30)));
        }

        [Test]
        public void ShrinkingAHeapClearsTheZoneOnTheGroundItHasLeft()
        {
            var plan = new LandformPlan();
            var form = plan.Add(new Landform
            {
                Kind = LandformKind.Heap, Height = Ground + 4f, Nodes = Square(20f, 20f, 20f),
            });
            var builder = new LandformBuilder(_grid, _map);
            var caps = new Dictionary<int, float>();
            var floors = new Dictionary<int, float>();
            plan.Zones(_grid, caps, floors);
            builder.CommitZones(caps, floors);
            Assert.That(_map.IsDumpZone(38, 38), Is.True, "the far corner is in the heap");

            form.Nodes = Square(20f, 20f, 10f);
            form.Touch();
            plan.Zones(_grid, caps, floors);
            builder.CommitZones(caps, floors);

            Assert.That(_map.IsDumpZone(38, 38), Is.False, "and is not, once the heap is drawn smaller");
            Assert.That(_map.IsDumpZone(25, 25), Is.True, "while the ground it still covers keeps it");
        }
    }
}
