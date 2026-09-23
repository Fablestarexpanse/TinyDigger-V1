using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Sending a crew to work one shape and nothing else (Ronan, 2026-09-23: *"when I tell them to
    /// dig it, select units, assign, type deal"*). Until now a unit took work from the whole map,
    /// which is fine for one job and useless the moment there are two.
    /// </summary>
    public class PostedCrewTests
    {
        const int Size = 60;
        const float Ground = 10f;
        const float TickSeconds = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        GridPathfinder _pathfinder;
        JobDispatcher _dispatcher;
        readonly List<CrewUnit> _units = new List<CrewUnit>();

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
            _pathfinder = new GridPathfinder(_grid) { MaxStepHeight = 1f, MaxSlopeDegrees = 45f };
            _dispatcher = new JobDispatcher(_grid, _map, _pathfinder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var unit in _units)
                unit.Dispose();
            _units.Clear();
            _map.Dispose();
        }

        CrewUnit Worker(int x, int z)
        {
            var unit = new CrewUnit(_dispatcher, x, z, UnitRole.Worker, UnitLoads.Barrow)
            {
                DigReachLevels = 4, CliffReachLevels = 12, Speed = 3f, WorkInterval = 0.2f,
            };
            _units.Add(unit);
            return unit;
        }

        /// <summary>Two patches of digging, one on each side of the map, each its own site.</summary>
        void TwoSites()
        {
            for (var z = 20; z < 26; z++)
                for (var x = 10; x < 16; x++)
                {
                    _map.Designate(x, z, DesignationKind.Dig, Ground - 1f);
                    _map.SetSite(x, z, 1);
                }

            for (var z = 20; z < 26; z++)
                for (var x = 40; x < 46; x++)
                {
                    _map.Designate(x, z, DesignationKind.Dig, Ground - 1f);
                    _map.SetSite(x, z, 2);
                }
        }

        void Work(CrewUnit unit, float seconds)
        {
            var elapsed = 0f;
            while (elapsed < seconds)
            {
                _dispatcher.Tick(TickSeconds);
                unit.Tick(TickSeconds);
                elapsed += TickSeconds;
            }
        }

        static float Dug(TerrainGrid grid, int x0, int x1, int z0, int z1)
        {
            var total = 0f;
            for (var z = z0; z < z1; z++)
                for (var x = x0; x < x1; x++)
                    total += Ground - grid.GetSurfaceHeight(x, z);
            return total;
        }

        [Test]
        public void AUnitPostedToASiteWorksThatSiteAndLeavesTheOtherAlone()
        {
            TwoSites();
            // Stood between the two, nearer the second, and posted to the first: without the posting
            // it would take the near work every time.
            var unit = Worker(35, 23);
            unit.Site = 1;

            Work(unit, 240f);

            Assert.That(Dug(_grid, 10, 16, 20, 26), Is.GreaterThan(0f), "it worked the site it was posted to");
            Assert.That(Dug(_grid, 40, 46, 20, 26), Is.Zero, "and never touched the one it was not");
        }

        [Test]
        public void AUnitThatIsNotPostedTakesWorkAnywhere()
        {
            // Nothing changes for a crew you have not sent anywhere, which is the whole crew until
            // you do.
            TwoSites();
            var unit = Worker(35, 23);

            Work(unit, 120f);

            Assert.That(Dug(_grid, 40, 46, 20, 26), Is.GreaterThan(0f),
                "an unposted unit takes the nearest work, as it always has");
        }

        [Test]
        public void PostingIsOnePerUnitSoACrewCanBeSplitBetweenSites()
        {
            TwoSites();
            var first = Worker(30, 23);
            var second = Worker(30, 30);
            first.Site = 1;
            second.Site = 2;

            var elapsed = 0f;
            while (elapsed < 240f)
            {
                _dispatcher.Tick(TickSeconds);
                first.Tick(TickSeconds);
                second.Tick(TickSeconds);
                elapsed += TickSeconds;
            }

            Assert.That(Dug(_grid, 10, 16, 20, 26), Is.GreaterThan(0f), "one crew on the first site");
            Assert.That(Dug(_grid, 40, 46, 20, 26), Is.GreaterThan(0f), "the other on the second");
        }

        [Test]
        public void APostedUnitStillHasSomewhereToPutItsSpoil()
        {
            // The exception that keeps posting usable: a crew sent to dig a hole must still be able
            // to tip, and the heap is almost never inside the hole. Filtering tipping by site would
            // post a crew and then stall it the moment the first machine filled up.
            for (var z = 20; z < 24; z++)
                for (var x = 10; x < 14; x++)
                {
                    _map.Designate(x, z, DesignationKind.Dig, Ground - 2f);
                    _map.SetSite(x, z, 1);
                }

            // The tip is well outside the site, and belongs to no site at all.
            for (var z = 20; z < 24; z++)
                for (var x = 20; x < 24; x++)
                    _map.SetDumpZone(x, z, true, Ground + 2f);

            var unit = Worker(15, 22);
            unit.Site = 1;

            Work(unit, 300f);

            Assert.That(Dug(_grid, 10, 14, 20, 24), Is.GreaterThan(0f), "it dug its site");
            var heaped = 0f;
            for (var z = 20; z < 24; z++)
                for (var x = 20; x < 24; x++)
                    heaped += _grid.GetSurfaceHeight(x, z) - Ground;
            Assert.That(heaped, Is.GreaterThan(0f), "and got the spoil to a tip outside it");
        }

        [Test]
        public void TakingAUnitOffItsSiteLetsItWorkAnywhereAgain()
        {
            TwoSites();
            // Somewhere to put the spoil, or the unit fills up and stalls whatever site it is on,
            // and the test would be measuring a full barrow rather than a posting.
            for (var z = 30; z < 36; z++)
                for (var x = 28; x < 34; x++)
                    _map.SetDumpZone(x, z, true, Ground + 2f);

            var unit = Worker(35, 23);
            unit.Site = 1;
            Work(unit, 120f);
            Assert.That(Dug(_grid, 40, 46, 20, 26), Is.Zero, "posted, it leaves the other site alone");

            // Take the first site's work away as well as the posting. Freeing the unit alone proves
            // nothing while it is standing in site one with work still in front of it — *that* is
            // then the nearest work, posted or not.
            for (var z = 20; z < 26; z++)
                for (var x = 10; x < 16; x++)
                    _map.Cancel(x, z);
            unit.Site = 0;
            Work(unit, 300f);

            Assert.That(Dug(_grid, 40, 46, 20, 26), Is.GreaterThan(0f),
                "and once it is taken off, nothing stops it crossing to the other site");
        }

        [Test]
        public void ACellLeavingThePlanStopsBelongingToItsSite()
        {
            var plan = new LandformPlan();
            var nodes = new List<RoadNode>();
            foreach (var corner in new[]
                     {
                         new Vector2(10f, 20f), new Vector2(20f, 20f),
                         new Vector2(20f, 30f), new Vector2(10f, 30f),
                     })
                nodes.Add(new RoadNode { Position = corner, Height = Ground, LockToGround = false });
            var form = plan.Add(new Landform { Height = Ground - 2f, Nodes = nodes });

            var builder = new LandformBuilder(_grid, _map);
            var owners = new Dictionary<int, int>();
            var surface = new Dictionary<int, float>();
            plan.Surface(_grid, surface, owners);
            builder.CommitSites(owners);
            Assert.That(_map.SiteAt(15, 25), Is.EqualTo(form.Id), "the shape's ground belongs to it");

            plan.Remove(form.Id);
            plan.Surface(_grid, surface, owners);
            builder.CommitSites(owners);

            Assert.That(_map.SiteAt(15, 25), Is.Zero, "and belongs to nobody once the shape is gone");
        }
    }
}
