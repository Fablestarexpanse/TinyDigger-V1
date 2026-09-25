using System;
using System.Collections.Generic;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>What the landing craft is doing.</summary>
    public enum FerryState
    {
        /// <summary>Floating with nothing to do.</summary>
        Afloat,
        /// <summary>Bow on a beach, ramp up.</summary>
        Beached,
        /// <summary>Bow on a beach, ramp down: machines can drive on and off.</summary>
        RampDown,
        RaisingRamp,
        BackingOff,
        Sailing,
        /// <summary>The last straight run in, bow first, onto the beach.</summary>
        RunningIn,
        LoweringRamp,
    }

    /// <summary>
    /// The landing craft as a unit (FERRY_PROPOSAL.md, slice B): where it is, which way it faces,
    /// and a trip from one beach to another. Plain C#, like <see cref="CrewUnit"/>; whatever draws
    /// it reads <see cref="Position"/>, <see cref="Heading"/> and <see cref="State"/>.
    ///
    /// A trip: raise the ramp, back off the beach, sail the water route (<see cref="WaterNav"/>) to
    /// a point out from the new landing, turn onto its line, run in bow first, lower the ramp. It
    /// only goes where the water floats it: a landing it cannot reach is refused with the reason.
    /// </summary>
    public sealed class Ferry
    {
        // The craft at the game's size (x1.41 over machine-forge): 4.9 m long, 2.87 m beam,
        // 0.17 m loaded draft, a 0.65 m ramp.
        public const float Draft = 0.17f;
        public const float HalfBeam = 1.43f;
        public const float HalfLength = 2.2f;
        public const float RampReach = 0.65f;
        public const float RampRise = 0.5f;
        public const int LineUpCells = 5;
        public const float MaxStep = 0.5f;

        /// <summary>Cells round a clicked shore searched for somewhere to beach.</summary>
        public const int SearchCells = 24;

        /// <summary>Machines it carries: one a lane, two lanes (the forge's well, 1.2 m a lane).</summary>
        public const int LaneCount = 2;

        /// <summary>Metres from the middle line to a lane's middle, at our size (forge 0.425 m).</summary>
        public const float LaneOffset = 0.6f;

        /// <summary>Metres forward of the hull's middle a machine parks: the middle of the well.</summary>
        public const float LaneForward = 0.2f;

        readonly int[] _lanes = { -1, -1 };
        readonly bool[] _aboard = new bool[LaneCount];

        readonly TerrainGrid _grid;
        readonly WaterNav _nav;
        readonly List<Vector2Int> _route = new List<Vector2Int>();
        readonly List<Vector2Int> _routeScratch = new List<Vector2Int>();
        int _routeIndex;
        float _timer;
        Vector2 _backTo;
        Vector2 _lineUp;
        Landing _next;

        /// <summary>Metres a second under way.</summary>
        public float Speed = 2f;

        /// <summary>Degrees a second it turns.</summary>
        public float TurnRate = 40f;

        /// <summary>Seconds the ramp takes to go down or up (the forge's RampDown clip).</summary>
        public float RampSeconds = 2f;

        /// <summary>Its middle, in cells.</summary>
        public Vector2 Position { get; private set; }

        /// <summary>Degrees, bow direction (0 = +z, 90 = +x), as <see cref="CrewUnit.Heading"/>.</summary>
        public float Heading { get; private set; }

        public FerryState State { get; private set; }

        /// <summary>The beach it is on, or making for.</summary>
        public Landing Landing { get; private set; }

        /// <summary>What it is doing, in words, for the status bar.</summary>
        public string Status { get; private set; } = "";

        /// <summary>Whether it is moving under its own power, for the props.</summary>
        public bool UnderWay => State == FerryState.BackingOff || State == FerryState.Sailing || State == FerryState.RunningIn;

        /// <summary>The water route it is sailing, cells; empty when not sailing.</summary>
        public IReadOnlyList<Vector2Int> Route => _route;

        public WaterNav Nav => _nav;

        /// <summary>The unit in each lane, or -1 where it is free.</summary>
        public IReadOnlyList<int> Lanes => _lanes;

        /// <summary>Whether a machine is on its way aboard or ashore: it does not sail while one is.</summary>
        public bool Busy
        {
            get
            {
                for (var i = 0; i < LaneCount; i++)
                    if (_lanes[i] >= 0 && !_aboard[i])
                        return true;
                return false;
            }
        }

        /// <summary>Whether anyone is aboard.</summary>
        public bool Loaded
        {
            get
            {
                for (var i = 0; i < LaneCount; i++)
                    if (_lanes[i] >= 0)
                        return true;
                return false;
            }
        }

        /// <summary>Takes a free lane for <paramref name="unit"/>; false when both are taken.</summary>
        public bool TryReserveLane(int unit, out int lane)
        {
            for (lane = 0; lane < LaneCount; lane++)
                if (_lanes[lane] == unit)
                    return true;
            for (lane = 0; lane < LaneCount; lane++)
                if (_lanes[lane] < 0)
                {
                    _lanes[lane] = unit;
                    _aboard[lane] = false;
                    return true;
                }

            lane = -1;
            return false;
        }

        /// <summary>The unit is in its lane, parked.</summary>
        public void MarkAboard(int unit, bool aboard)
        {
            for (var i = 0; i < LaneCount; i++)
                if (_lanes[i] == unit)
                    _aboard[i] = aboard;
        }

        /// <summary>Frees the unit's lane.</summary>
        public void Leave(int unit)
        {
            for (var i = 0; i < LaneCount; i++)
                if (_lanes[i] == unit)
                {
                    _lanes[i] = -1;
                    _aboard[i] = false;
                }
        }

        /// <summary>Where a machine parks in <paramref name="lane"/>, cells: port lane 0, starboard 1.</summary>
        public Vector2 LanePosition(int lane)
        {
            var dir = Direction(Heading);
            var right = new Vector2(dir.y, -dir.x);
            var across = (lane == 0 ? -LaneOffset : LaneOffset) / CellSize;
            return Position + dir * (LaneForward / CellSize) + right * across;
        }

        /// <summary>
        /// The cell a machine lines up on to reverse aboard: inland of the ramp foot, down the straight
        /// run the landing was chosen for (<see cref="LandingFinder"/>).
        /// </summary>
        public Vector2Int LineUpCell(int cells)
        {
            var dir = Direction(Landing.Heading);
            return Landing.RampFoot + new Vector2Int(Mathf.RoundToInt(dir.x), Mathf.RoundToInt(dir.y)) * cells;
        }

        Ferry(TerrainGrid grid, WaterNav nav)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _nav = nav ?? throw new ArgumentNullException(nameof(nav));
        }

        /// <summary>A craft beached on <paramref name="landing"/>, ramp up: moored off a beach.</summary>
        public static Ferry BeachedAt(TerrainGrid grid, WaterNav nav, Landing landing)
        {
            if (!landing.Found)
                throw new ArgumentException("Needs a landing that was found: " + landing.Refusal, nameof(landing));
            var ferry = new Ferry(grid, nav)
            {
                Position = landing.Hull,
                Heading = landing.Heading,
                Landing = landing,
                State = FerryState.Beached,
                Status = "Beached, ramp up",
            };
            return ferry;
        }

        /// <summary>The nearest good landing to <paramref name="near"/>, or a refusal saying why.</summary>
        public static Landing FindLanding(TerrainGrid grid, WaterNav nav, Vector2Int near, int searchCells = SearchCells) =>
            LandingFinder.Find(grid, nav, near, searchCells, HalfLength, RampReach, RampRise, LineUpCells, MaxStep);

        /// <summary>
        /// Sends it to beach near <paramref name="shore"/>. False, and nothing changes, when there
        /// is no landing there or no way over the water to it; <paramref name="why"/> says which.
        /// </summary>
        public bool SailTo(Vector2Int shore, out string why)
        {
            if (Busy)
            {
                why = "a machine is still boarding or landing";
                return false;
            }

            var landing = FindLanding(_grid, _nav, shore);
            if (!landing.Found)
            {
                why = landing.Refusal;
                return false;
            }

            var cell = CellSize;
            var dir = Direction(Heading);
            var backTo = State == FerryState.Beached || State == FerryState.RampDown || State == FerryState.LoweringRamp
                ? Position - dir * (HalfLength / cell * 1.6f)
                : Position;
            var from = NearestFloating(Cell(backTo), 6);
            var landingDir = Direction(landing.Heading);
            var lineUp = landing.Hull - landingDir * (HalfLength / cell * 1.6f);
            var to = NearestFloating(Cell(lineUp), 6);
            // Found into a scratch list: clearing the live route on a refusal left a craft under way
            // with nothing ahead of it, and it ran in on its old landing from wherever it was
            // (2026-09-24).
            _routeScratch.Clear();
            if (from.x < 0 || to.x < 0 || !_nav.TryFindRoute(from, to, _routeScratch))
            {
                why = "no way over the water from here";
                return false;
            }

            _route.Clear();
            _route.AddRange(_routeScratch);

            _next = landing;
            Landing = landing;
            _backTo = new Vector2(from.x + 0.5f, from.y + 0.5f);
            _lineUp = new Vector2(to.x + 0.5f, to.y + 0.5f);
            _routeIndex = 0;
            _timer = 0f;
            State = State == FerryState.RampDown || State == FerryState.LoweringRamp ? FerryState.RaisingRamp
                : State == FerryState.Beached ? FerryState.BackingOff
                : FerryState.Sailing;
            Status = "Setting out";
            why = null;
            return true;
        }

        /// <summary>Lowers the ramp if it is beached with the ramp up.</summary>
        public bool LowerRamp()
        {
            if (State != FerryState.Beached)
                return false;
            State = FerryState.LoweringRamp;
            _timer = 0f;
            return true;
        }

        public void Tick(float deltaTime)
        {
            var cells = Speed / CellSize * deltaTime;
            switch (State)
            {
                case FerryState.RaisingRamp:
                    Status = "Raising the ramp";
                    if ((_timer += deltaTime) >= RampSeconds)
                        State = FerryState.BackingOff;
                    break;

                case FerryState.BackingOff:
                    // Straight astern, bow still to the beach, till there is water under all of it.
                    Status = "Backing off the beach";
                    if (MoveTowards(_backTo, cells, deltaTime, astern: true))
                        State = FerryState.Sailing;
                    break;

                case FerryState.Sailing:
                    Status = "Sailing";
                    while (_routeIndex < _route.Count && cells > 0f)
                    {
                        var at = _route[_routeIndex];
                        var target = new Vector2(at.x + 0.5f, at.y + 0.5f);
                        var before = Position;
                        if (!MoveTowards(target, cells, deltaTime, astern: false))
                            break;
                        cells -= Vector2.Distance(before, Position);
                        _routeIndex++;
                    }

                    if (_routeIndex >= _route.Count)
                    {
                        _route.Clear();
                        State = FerryState.RunningIn;
                    }

                    break;

                case FerryState.RunningIn:
                    // Onto the landing's line, turned to its heading, then in bow first.
                    Status = "Running in to the beach";
                    if (MoveTowards(_next.Hull, cells, deltaTime, astern: false, finalHeading: _next.Heading))
                    {
                        Heading = _next.Heading;
                        State = FerryState.LoweringRamp;
                        _timer = 0f;
                    }

                    break;

                case FerryState.LoweringRamp:
                    Status = "Lowering the ramp";
                    if ((_timer += deltaTime) >= RampSeconds)
                    {
                        State = FerryState.RampDown;
                        Status = "Beached, ramp down";
                    }

                    break;
            }
        }

        /// <summary>
        /// Steers for <paramref name="target"/> and moves up to <paramref name="cells"/>: it turns
        /// before it goes, slowing while it is well off its course, so it swings like a boat rather
        /// than sliding sideways. Astern it keeps its heading and goes backwards. True on arrival.
        /// </summary>
        bool MoveTowards(Vector2 target, float cells, float deltaTime, bool astern, float? finalHeading = null)
        {
            var offset = target - Position;
            var distance = offset.magnitude;
            if (distance <= 1e-3f)
            {
                Position = target;
                return true;
            }

            var along = offset / distance;
            if (!astern)
            {
                // Off the last cell of the run in, it lines up on the beach's heading.
                var wanted = finalHeading.HasValue && distance < 2f
                    ? finalHeading.Value
                    : (float)(Math.Atan2(along.x, along.y) * 180.0 / Math.PI);
                Heading = Mathf.MoveTowardsAngle(Heading, wanted, TurnRate * deltaTime);
                var off = Mathf.Abs(Mathf.DeltaAngle(Heading, (float)(Math.Atan2(along.x, along.y) * 180.0 / Math.PI)));
                cells *= Mathf.Clamp01(Mathf.Cos(off * Mathf.Deg2Rad));
            }

            if (distance <= cells)
            {
                Position = target;
                return true;
            }

            Position += along * cells;
            return false;
        }

        Vector2Int NearestFloating(Vector2Int around, int radius)
        {
            for (var ring = 0; ring <= radius; ring++)
                for (var dz = -ring; dz <= ring; dz++)
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                            continue;
                        if (_nav.Floats(around.x + dx, around.y + dz))
                            return new Vector2Int(around.x + dx, around.y + dz);
                    }

            return new Vector2Int(-1, -1);
        }

        float CellSize => Math.Max(0.01f, _grid.CellSize);

        static Vector2 Direction(float heading)
        {
            var radians = heading * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        }

        static Vector2Int Cell(Vector2 at) => new Vector2Int(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));
    }
}
