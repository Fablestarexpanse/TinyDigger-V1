using NUnit.Framework;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>
    /// Machines going aboard the landing craft, crossing and landing (FERRY_PROPOSAL.md, slice C;
    /// Ronan, 2026-09-24: "the excavator and dozer go to it and can be loaded then the boat can
    /// drive around in water and unload them someplace else").
    /// </summary>
    public class FerryBoardingTests
    {
        const int Width = 80;
        const int Depth = 40;
        const float Tick = 0.05f;

        TerrainGrid _grid;
        DesignationMap _map;
        JobDispatcher _dispatcher;
        WaterNav _nav;
        Ferry _ferry;

        [SetUp]
        public void SetUp()
        {
            // The strait from FerryTests: a beach each side of two metres of water.
            _grid = new TerrainGrid(Width, Depth, TinyDiggersMaterials.CreateTable(), heightStep: 0.5f, datum: -10f,
                cellSize: 0.5f);
            for (var z = 0; z < Depth; z++)
                for (var x = 0; x < Width; x++)
                {
                    var surface = x < 8 ? 1f : x < 14 ? 0.5f - (x - 8) * 0.4f : x < 66 ? -2f : x < 72 ? -2f + (x - 65) * 0.5f : 1f;
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, Mathf.Max(0.5f, surface + 8f)),
                    });
                }

            _map = new DesignationMap(_grid);
            _dispatcher = new JobDispatcher(_grid, _map, new GridPathfinder(_grid));
            _nav = new WaterNav(_grid, Ferry.Draft, Ferry.HalfBeam);
            var west = Ferry.FindLanding(_grid, _nav, new Vector2Int(8, 20));
            Assert.That(west.Found, Is.True, west.Refusal);
            _ferry = Ferry.BeachedAt(_grid, _nav, west);
            _ferry.LowerRamp();
            Run(() => _ferry.State == FerryState.RampDown, 10f);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        CrewUnit Machine(int x, int z, UnitRole role) =>
            new CrewUnit(_dispatcher, x, z, role, UnitLoads.Bed).AsMachine();

        CrewUnit[] _units = new CrewUnit[0];

        void Run(System.Func<bool> done, float seconds)
        {
            for (var t = 0f; t < seconds && !done(); t += Tick)
            {
                _dispatcher.Tick(Tick);
                foreach (var unit in _units)
                    unit.Tick(Tick);
                _ferry.Tick(Tick);
            }
        }

        [Test]
        public void TwoMachinesBoardAThirdIsTurnedAway()
        {
            var digger = Machine(2, 14, UnitRole.Digger);
            var dozer = Machine(2, 26, UnitRole.Bulldozer);
            var truck = Machine(2, 20, UnitRole.Hauler);
            _units = new[] { digger, dozer, truck };

            Assert.That(digger.OrderBoard(_ferry, out var why), Is.True, why);
            Assert.That(dozer.OrderBoard(_ferry, out why), Is.True, why);
            Assert.That(truck.OrderBoard(_ferry, out why), Is.False);
            Assert.That(why, Is.EqualTo("the landing craft is full"));

            var reversed = false;
            Run(() =>
            {
                reversed |= digger.Reversing || dozer.Reversing;
                return digger.State == CrewUnitState.Aboard && dozer.State == CrewUnitState.Aboard;
            }, 90f);

            Assert.That(digger.State, Is.EqualTo(CrewUnitState.Aboard), digger.Status);
            Assert.That(dozer.State, Is.EqualTo(CrewUnitState.Aboard), dozer.Status);
            Assert.That(digger.FerryLane, Is.Not.EqualTo(dozer.FerryLane), "a lane each");
            Assert.That(reversed, Is.True, "they back aboard, so they can drive off forwards");
            Assert.That(Mathf.DeltaAngle(digger.Heading, _ferry.Heading), Is.InRange(-1f, 1f), "facing the ramp");
            Assert.That(_ferry.Busy, Is.False, "both parked, so it may sail");
        }

        [Test]
        public void TheyCrossAndDriveOffOntoTheFarBeach()
        {
            var digger = Machine(2, 14, UnitRole.Digger);
            var dozer = Machine(2, 26, UnitRole.Bulldozer);
            _units = new[] { digger, dozer };
            Assert.That(digger.OrderBoard(_ferry, out _) && dozer.OrderBoard(_ferry, out _), Is.True);
            Assert.That(_ferry.SailTo(new Vector2Int(72, 20), out var refused), Is.False, "not while they are still boarding");
            Assert.That(refused, Is.EqualTo("a machine is still boarding or landing"));

            Run(() => digger.State == CrewUnitState.Aboard && dozer.State == CrewUnitState.Aboard, 90f);
            Assert.That(_ferry.SailTo(new Vector2Int(72, 20), out var why), Is.True, why);

            Run(() => !digger.OnFerry && !dozer.OnFerry && !_ferry.Loaded
                      && digger.State != CrewUnitState.Moving && dozer.State != CrewUnitState.Moving, 240f);

            foreach (var unit in _units)
            {
                Assert.That(unit.OnFerry, Is.False, unit.Status);
                Assert.That(unit.Position.x, Is.GreaterThan(66f), $"{unit.Role} landed on the east beach: {unit.Status}");
                Assert.That(_grid.IsPassableGround(unit.Cell.x, unit.Cell.y), Is.True, $"{unit.Role} is on dry ground");
            }

            Assert.That(_ferry.Loaded, Is.False, "and the craft is empty");
        }

        [Test]
        public void HoldStopsAUnitWhereItIsAndReleaseLetsItGo()
        {
            var dozer = Machine(2, 26, UnitRole.Bulldozer);
            _units = new[] { dozer };
            Assert.That(dozer.OrderHold(), Is.True);
            Assert.That(dozer.Holding, Is.True);
            Run(() => false, 2f);
            Assert.That(dozer.Cell, Is.EqualTo(new Vector2Int(2, 26)), "it stays put");
            Assert.That(dozer.Holding, Is.True, dozer.Status);

            dozer.ReleaseHold();
            Assert.That(dozer.Holding, Is.False);
        }

        [Test]
        public void AMachineOnTheCraftCannotBeHeld()
        {
            var digger = Machine(2, 14, UnitRole.Digger);
            _units = new[] { digger };
            Assert.That(digger.OrderBoard(_ferry, out var why), Is.True, why);
            Run(() => digger.State == CrewUnitState.Aboard, 90f);
            Assert.That(digger.OrderHold(), Is.False, "the craft is driving, not the machine");
            Assert.That(digger.State, Is.EqualTo(CrewUnitState.Aboard));
        }
    }
}
