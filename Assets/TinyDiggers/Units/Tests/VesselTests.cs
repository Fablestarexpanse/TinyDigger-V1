using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// The forge's working craft afloat (2026-09-25, slice A): each sails across open water and
    /// anchors; each goes only where its own hull floats; and a refused order leaves its course alone.
    /// </summary>
    public class VesselTests
    {
        const int Width = 80;
        const int Depth = 40;
        const float Cell = 0.5f;

        TerrainGrid _grid;

        [SetUp]
        public void SetUp()
        {
            // A strait running north-south, two metres deep, 26 m across. Fine height steps, so a
            // shelf can sit between the tug's draft and the scow's.
            _grid = new TerrainGrid(Width, Depth, MaterialTable.CreateDefault(), heightStep: 0.05f, datum: -10f, cellSize: Cell);
            Fill(x => x < 14 ? 1f : x < 66 ? -2f : 1f);
        }

        void Fill(System.Func<int, float> surface)
        {
            for (var z = 0; z < Depth; z++)
                for (var x = 0; x < Width; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Mathf.Max(0.5f, surface(x) + 8f)),
                    });
        }

        static void SailOut(Vessel vessel, System.Action<Vessel> each = null)
        {
            for (var t = 0f; t < 300f && vessel.State == VesselState.Sailing; t += 0.05f)
            {
                vessel.Tick(0.05f);
                each?.Invoke(vessel);
            }
        }

        [Test]
        public void EveryKindSailsAcrossTheStraitAndAnchors([Values] VesselKind kind)
        {
            var vessel = new Vessel(kind, _grid, new Vector2(24.5f, 20.5f), 90f);
            Assert.That(vessel.SailTo(new Vector2Int(56, 20), out var why), Is.True, why);
            SailOut(vessel, v =>
            {
                var c = new Vector2Int(Mathf.FloorToInt(v.Position.x), Mathf.FloorToInt(v.Position.y));
                Assert.That(v.Nav.DeepEnough(c.x, c.y), Is.True, $"the {v.Spec.Name} went over ({c.x}, {c.y}), which would ground it");
            });
            Assert.That(vessel.State, Is.EqualTo(VesselState.Moored), vessel.Status);
            Assert.That(Vector2.Distance(vessel.Position, new Vector2(56.5f, 20.5f)), Is.LessThan(SearchReach()), "anchored where it was sent");
        }

        static float SearchReach() => Vessel.SearchCells * 1.5f;

        [Test]
        public void TheScowRefusesWaterTheTugTakes()
        {
            // The east half of the strait is a 0.65 m shelf: enough for the tug's 0.42 m draft and
            // its clearance, not for the scow's 0.59 m loaded.
            Fill(x => x < 14 ? 1f : x < 40 ? -2f : x < 66 ? -0.65f : 1f);
            var scow = new Vessel(VesselKind.Scow, _grid, new Vector2(24.5f, 20.5f), 0f);
            var tug = new Vessel(VesselKind.Tug, _grid, new Vector2(24.5f, 20.5f), 0f);
            Assert.That(scow.SailTo(new Vector2Int(58, 20), out var why), Is.False);
            Assert.That(why, Does.Contain("scow"));
            Assert.That(scow.State, Is.EqualTo(VesselState.Moored), "and it stays at anchor");
            Assert.That(tug.SailTo(new Vector2Int(58, 20), out why), Is.True, why);
        }

        [Test]
        public void ARefusedOrderUnderWayLeavesItsCourseAlone()
        {
            var tug = new Vessel(VesselKind.Tug, _grid, new Vector2(20.5f, 20.5f), 90f);
            Assert.That(tug.SailTo(new Vector2Int(60, 20), out var why), Is.True, why);
            tug.Tick(1f);

            // A dam across the strait ahead of it: the east side still floats a tug, but there is no
            // water to it any more.
            for (var z = 0; z < Depth; z++)
                for (var x = 44; x < 47; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 9f) });
            tug.Nav.Refresh();

            var route = new List<Vector2Int>(tug.Route);
            Assert.That(tug.SailTo(new Vector2Int(60, 20), out why), Is.False);
            Assert.That(why, Is.EqualTo("no way over the water from here"));
            Assert.That(tug.State, Is.EqualTo(VesselState.Sailing), "still sailing");
            Assert.That(tug.Route, Is.EqualTo(route), "on the course it had");
        }

        [Test]
        public void ASendOntoLandFarFromWaterIsRefused()
        {
            var dredge = new Vessel(VesselKind.Dredge, _grid, new Vector2(30.5f, 20.5f), 0f);
            Assert.That(dredge.SailTo(new Vector2Int(2, 20), out var why), Is.False);
            Assert.That(why, Does.Contain("dredge"));
        }
    }
}
