using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Slice 17 Part B: a planned road becomes designations; its bed turns to Road once built,
    /// without changing height; editing or removing it undoes only what it has to.
    /// </summary>
    public class RoadBuilderTests
    {
        const int Size = 30;
        const float Ground = 5f;

        TerrainGrid _grid;
        DesignationMap _map;
        RoadBuilder _builder;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 0.5f, 0f, 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, 3f), new Layer(MaterialTable.Topsoil, Ground - 3f) });
            _map = new DesignationMap(_grid);
            _builder = new RoadBuilder(_grid, _map);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        void Plan(int road, float fromX, float toX, float z, float height, int width = 3)
        {
            var chain = new List<RoadNode>
            {
                new RoadNode { Position = new Vector2(fromX, z), Height = height },
                new RoadNode { Position = new Vector2(toX, z), Height = height },
            };
            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            var footprint = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            RoadPlanner.Footprint(_grid, samples, width, footprint, bed);
            _builder.PlanRoad(road, footprint, bed);
        }

        [Test]
        public void ARoadOnLevelGroundIsPavedStraightAwayWithoutChangingHeight()
        {
            Plan(1, 4f, 24f, 15.5f, Ground);
            Assert.That(_map.Count, Is.Zero, "nothing to dig or fill");

            var paved = _builder.Tick();

            Assert.That(paved, Is.GreaterThan(50));
            Assert.That(_grid.GetTopMaterial(10, 15), Is.EqualTo(MaterialTable.Road));
            Assert.That(_grid.GetSurfaceHeight(10, 15), Is.EqualTo(Ground).Within(1e-3f), "the road is laid in the ground, not on it");
            Assert.That(_grid.GetTopMaterial(10, 18), Is.EqualTo(MaterialTable.Topsoil), "beside the road is untouched");
        }

        [Test]
        public void ARaisedRoadWaitsForItsFillThenIsPaved()
        {
            Plan(1, 4f, 24f, 15.5f, Ground + 1f);
            Assert.That(_map.GetKind(10, 15), Is.EqualTo(DesignationKind.Fill));
            Assert.That(_builder.Tick(), Is.Zero, "nothing is paved while the fill is still to do");

            // The crew fills the bed to the road's height and the designations are met.
            for (var z = 14; z <= 16; z++)
                for (var x = 4; x <= 24; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, 3f), new Layer(MaterialTable.Topsoil, 2f), new Layer(MaterialTable.DirtLoose, 1f) });
            _map.Prune();

            Assert.That(_builder.Tick(), Is.GreaterThan(40));
            Assert.That(_grid.GetTopMaterial(10, 15), Is.EqualTo(MaterialTable.Road));
            Assert.That(_grid.GetSurfaceHeight(10, 15), Is.EqualTo(Ground + 1f).Within(1e-3f));
        }

        [Test]
        public void RemovingARoadTakesItsWorkAwayAndTurnsItsRoadToDirt()
        {
            Plan(1, 4f, 24f, 15.5f, Ground);
            _builder.Tick();
            Plan(2, 4f, 24f, 5.5f, Ground + 1f);
            Assert.That(_map.Count, Is.GreaterThan(0));

            _builder.RemoveRoad(1);
            _builder.RemoveRoad(2);

            Assert.That(_grid.GetTopMaterial(10, 15), Is.EqualTo(MaterialTable.Dirt));
            Assert.That(_grid.GetSurfaceHeight(10, 15), Is.EqualTo(Ground).Within(1e-3f), "the ground keeps its height");
            Assert.That(_map.Count, Is.Zero, "the unbuilt road's fill is gone too");
        }

        [Test]
        public void ReplanningAShorterRoadTakesTheRoadOffWhatItLeft()
        {
            Plan(1, 4f, 24f, 15.5f, Ground);
            _builder.Tick();
            Plan(1, 4f, 14f, 15.5f, Ground);
            _builder.Tick();

            Assert.That(_grid.GetTopMaterial(8, 15), Is.EqualTo(MaterialTable.Road), "kept where the road still runs");
            Assert.That(_grid.GetTopMaterial(20, 15), Is.EqualTo(MaterialTable.Dirt), "given back where it no longer does");
        }

        [Test]
        public void WhereTwoRoadsMeetTheCellsStayRoadWhileEitherDoes()
        {
            Plan(1, 4f, 24f, 15.5f, Ground);
            var chain = new List<RoadNode>
            {
                new RoadNode { Position = new Vector2(14.5f, 15.5f), Height = Ground },
                new RoadNode { Position = new Vector2(14.5f, 26f), Height = Ground },
            };
            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            var footprint = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            RoadPlanner.Footprint(_grid, samples, 3, footprint, bed);
            _builder.PlanRoad(3, footprint, bed);
            _builder.Tick();
            Assert.That(_grid.GetTopMaterial(14, 15), Is.EqualTo(MaterialTable.Road));

            _builder.RemoveRoad(3);

            Assert.That(_grid.GetTopMaterial(14, 15), Is.EqualTo(MaterialTable.Road), "the other road still runs through the junction");
            Assert.That(_grid.GetTopMaterial(14, 22), Is.EqualTo(MaterialTable.Dirt), "the removed road's own cells are dirt again");
        }

        [Test]
        public void PavingKeepsEveryLayerUnderTheRoad()
        {
            Assert.That(RoadBuilder.Pave(_grid, 3, 3), Is.True);
            var layers = new Layer[TerrainGrid.MaxLayersPerCell];
            // Top first.
            var count = _grid.CopyLayers(3, 3, layers);
            Assert.That(count, Is.EqualTo(3));
            Assert.That(layers[0].Material, Is.EqualTo(MaterialTable.Road));
            Assert.That(layers[0].Thickness, Is.EqualTo(RoadBuilder.Thickness).Within(1e-4f));
            Assert.That(layers[1].Material, Is.EqualTo(MaterialTable.Topsoil), "the soil under it is still there, thinner");
            Assert.That(layers[1].Thickness, Is.EqualTo(2f - RoadBuilder.Thickness).Within(1e-4f));
            Assert.That(layers[2].Material, Is.EqualTo(MaterialTable.Rock));
            Assert.That(_grid.GetSurfaceHeight(3, 3), Is.EqualTo(Ground).Within(1e-3f));
            Assert.That(RoadBuilder.Unpave(_grid, 3, 3), Is.True);
            Assert.That(_grid.GetTopMaterial(3, 3), Is.EqualTo(MaterialTable.Dirt));
        }
    }
}
