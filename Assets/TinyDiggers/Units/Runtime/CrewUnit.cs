using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    public enum CrewUnitState
    {
        Idle,
        Moving,
        Digging,
        Tipping,

        /// <summary>There is work designated, but none of it can be reached and worked from.</summary>
        Unreachable,

        /// <summary>Held up by another unit on the next cell of its path.</summary>
        Waiting,
    }

    public enum CrewJobKind
    {
        None,
        Dig,
        Fill,

        /// <summary>Tipping a full load somewhere undesignated because no fill can take it.</summary>
        Dump,

        /// <summary>Tipping a load on a player-marked Dump Zone.</summary>
        DumpZone,

        /// <summary>Driving out of another unit's way, having given its job up.</summary>
        Yield,
    }

    /// <summary>
    /// One digger that has to physically reach its work. Plain C#: the scene side calls
    /// <see cref="Tick"/> and draws the body at <see cref="Position"/>, <see cref="Height"/> and
    /// <see cref="Heading"/>.
    ///
    /// Everything shared by a crew belongs to the <see cref="JobDispatcher"/>: the designations,
    /// which of them each unit has claimed, reachability, benching, Auto ramps and who is
    /// standing where. A unit asks it what it may work, and takes the nearest of those by path
    /// cost.
    ///
    /// Job loop:
    /// - With room in the load: go to the nearest cell from which an unclaimed Dig designation is
    ///   within dig reach, and take one height step at a time until it is met or the load is full.
    /// - With at least a step of load and no reachable dig (or a full load): go to the nearest
    ///   reachable Fill and tip, never past its target or out of reach.
    /// - With a full load and no reachable Fill: tip on the nearest Dump Zone cell under its cap;
    ///   with none, on the nearest cell that is not its own, not lower than where it stands (so
    ///   never back into a pit it dug), not beside any designation or finished work, and not
    ///   where another unit stands or is heading.
    /// - Nothing reachable to dig: ask the dispatcher for a ramp. Failing that, a part load goes
    ///   to a Dump Zone if there is one; then idle, or UNREACHABLE.
    ///
    /// Dig reach (TERRAIN_REFERENCE.md section 4): a cell can be dug or filled only from an
    /// adjacent cell whose surface is within <see cref="DigReachLevels"/> height steps of it.
    /// A unit never works from a cell that is still to be dug (it would dig the ground out from
    /// under itself), unless benching has left that cell at its floor; it may drive across one.
    ///
    /// Traffic: units never share a cell. One whose next path cell is taken waits up to
    /// <see cref="TrafficWaitSeconds"/>, then re-paths around the units in the way, and gives the
    /// job up if there is no way round.
    /// </summary>
    public sealed class CrewUnit : IDisposable
    {
        const float Epsilon = 1e-3f;
        static readonly int[] NeighbourX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] NeighbourZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>Cells per second.</summary>
        public float Speed = 3f;

        /// <summary>How many height steps above or below its own cell the unit can dig or fill.</summary>
        public int DigReachLevels = 2;

        /// <summary>Seconds per height step dug, or per tip.</summary>
        public float WorkInterval = 0.4f;

        /// <summary>Degrees per second the body turns toward its direction of travel.</summary>
        public float TurnRate = 360f;

        /// <summary>How quickly the body's height catches up with the surface under it; higher is snappier.</summary>
        public float HeightFollowRate = 10f;

        /// <summary>Seconds between re-checks for work while idle or unreachable, in case the world changed.</summary>
        public float IdleRethinkInterval = 1f;

        /// <summary>Seconds a unit waits for another one to move off its next cell before going round.</summary>
        public float TrafficWaitSeconds = 1f;

        readonly TerrainGrid _grid;
        readonly DesignationMap _designations;
        readonly GridPathfinder _pathfinder;
        readonly JobDispatcher _dispatcher;
        readonly bool _ownsDispatcher;
        readonly List<Vector2Int> _path = new List<Vector2Int>();
        readonly bool[] _onPath;
        readonly List<int> _onPathCells = new List<int>();
        readonly List<int> _unreachableDigs = new List<int>();

        int _pathIndex;
        float _jobHeight;
        bool _repath;
        bool _rethink = true;
        float _rethinkTimer;
        float _workTimer;
        float _waitTimer;
        bool _loadFull;
        bool _zoneBelowOnly;
        bool _disposed;

        public CrewUnit(TerrainGrid grid, DesignationMap designations, GridPathfinder pathfinder, int startX, int startZ, float capacity = MaterialInventory.DefaultCapacity)
            : this(new JobDispatcher(grid, designations, pathfinder), startX, startZ, capacity, ownsDispatcher: true)
        {
        }

        public CrewUnit(JobDispatcher dispatcher, int startX, int startZ, float capacity = MaterialInventory.DefaultCapacity)
            : this(dispatcher, startX, startZ, capacity, ownsDispatcher: false)
        {
        }

        CrewUnit(JobDispatcher dispatcher, int startX, int startZ, float capacity, bool ownsDispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _ownsDispatcher = ownsDispatcher;
            _grid = dispatcher.Grid;
            _designations = dispatcher.Designations;
            _pathfinder = dispatcher.Pathfinder;
            if (!_grid.InBounds(startX, startZ))
                throw new ArgumentOutOfRangeException(nameof(startX), "The unit must start on the map.");

            Inventory = new MaterialInventory(capacity);
            Position = new Vector2(startX + 0.5f, startZ + 0.5f);
            Height = TerrainSurface.SampleHeight(_grid, Position.x, Position.y);
            _onPath = new bool[_grid.Width * _grid.Height];

            Id = _dispatcher.Register(this);
            _dispatcher.SetCell(Id, startX, startZ);
            _grid.CellChanged += OnCellChanged;
            _designations.Changed += OnDesignationChanged;
            Status = "Idle";
        }

        /// <summary>This unit's place in the crew; the dispatcher's key for it.</summary>
        public int Id { get; }

        public JobDispatcher Dispatcher => _dispatcher;

        public MaterialInventory Inventory { get; }

        /// <summary>Position on the map in cell units; cell (i, j) spans i..i+1, j..j+1.</summary>
        public Vector2 Position { get; private set; }

        /// <summary>Height of the ground under the body, smoothed.</summary>
        public float Height { get; private set; }

        /// <summary>Degrees clockwise from +z, the direction the body faces.</summary>
        public float Heading { get; private set; }

        public Vector2Int Cell => new Vector2Int(
            Math.Min(Math.Max((int)Math.Floor(Position.x), 0), _grid.Width - 1),
            Math.Min(Math.Max((int)Math.Floor(Position.y), 0), _grid.Height - 1));

        public CrewUnitState State { get; private set; }

        public CrewJobKind Job { get; private set; }

        /// <summary>The cell being dug or filled.</summary>
        public Vector2Int JobTarget { get; private set; }

        /// <summary>The cell the unit works from.</summary>
        public Vector2Int JobStand { get; private set; }

        /// <summary>The path being followed, including cells already passed; <see cref="PathIndex"/> is the next waypoint.</summary>
        public IReadOnlyList<Vector2Int> Path => _path;

        public int PathIndex => _pathIndex;

        /// <summary>One line describing what the unit is doing, for the readout.</summary>
        public string Status { get; private set; }

        /// <summary>Designations that no reachable cell can work, as of the last job choice.</summary>
        public int UnreachableCount { get; private set; }

        /// <summary>The unreachable designation closest to the unit, described; empty if none.</summary>
        public string NearestUnreachable { get; private set; } = "";

        /// <summary>Whether the last look for somewhere to tip found Dump Zone cells but no room in them.</summary>
        public bool DumpZoneFull { get; private set; }

        /// <summary>Changes whenever state, job or status changes, so displays redraw only then.</summary>
        public int Version { get; private set; }

        /// <summary>How many times the path was re-planned, because a cell on it changed or a unit was in the way.</summary>
        public int RepathCount { get; private set; }

        /// <summary>How long it has been held up by another unit, in seconds.</summary>
        public float WaitingFor => _waitTimer;

        // Crew-wide settings and state live on the dispatcher; these reach them for convenience.

        public bool AutoRamp
        {
            get => _dispatcher.AutoRamp;
            set => _dispatcher.AutoRamp = value;
        }

        public bool Benching
        {
            get => _dispatcher.Benching;
            set => _dispatcher.Benching = value;
        }

        public float RampCancelSeconds
        {
            get => _dispatcher.RampCancelSeconds;
            set => _dispatcher.RampCancelSeconds = value;
        }

        public float RampBlockedSeconds
        {
            get => _dispatcher.RampBlockedSeconds;
            set => _dispatcher.RampBlockedSeconds = value;
        }

        public bool HasRamp => _dispatcher.HasRamp;

        public Vector2Int RampTarget => _dispatcher.RampTarget;

        public string RampNote => _dispatcher.RampNote;

        public IReadOnlyList<Vector2Int> RampCorridor => _dispatcher.RampCorridor;

        public bool OnAutoRamp => Job == CrewJobKind.Dig && _designations.IsAuto(JobTarget.x, JobTarget.y);

        public bool IsRampSuppressed(int x, int z) => _dispatcher.IsRampSuppressed(x, z);

        public float DigFloor(int x, int z) => _dispatcher.DigFloor(x, z);

        float Step => _grid.HeightStep > 0f ? _grid.HeightStep : 1f;

        /// <summary>Whether a unit standing at height <paramref name="standHeight"/> can dig or fill a cell at <paramref name="cellHeight"/>.</summary>
        public bool WithinReach(float standHeight, float cellHeight) =>
            Math.Abs(cellHeight - standHeight) <= DigReachLevels * Step + Epsilon;

        /// <summary>Whether a unit on (standX, standZ) can work the adjacent cell (targetX, targetZ).</summary>
        public bool CanWork(int standX, int standZ, int targetX, int targetZ)
        {
            if (!_grid.InBounds(standX, standZ) || !_grid.InBounds(targetX, targetZ))
                return false;
            var adjacent = Math.Abs(standX - targetX) <= 1 && Math.Abs(standZ - targetZ) <= 1 && (standX != targetX || standZ != targetZ);
            return adjacent && WithinReach(_grid.GetSurfaceHeight(standX, standZ), _grid.GetSurfaceHeight(targetX, targetZ));
        }

        /// <summary>Makes the unit choose its job afresh on the next tick, as if the world had changed.</summary>
        public void RequestRethink() => _rethink = true;

        public void Tick(float deltaTime)
        {
            if (_disposed)
                return;

            if (_ownsDispatcher)
                _dispatcher.Tick(deltaTime);

            if (State == CrewUnitState.Idle || State == CrewUnitState.Unreachable)
            {
                _rethinkTimer -= deltaTime;
                if (_rethinkTimer <= 0f)
                    _rethink = true;
            }

            if (_rethink)
                ChooseJob();

            switch (State)
            {
                case CrewUnitState.Moving:
                case CrewUnitState.Waiting:
                    Move(deltaTime);
                    break;
                case CrewUnitState.Digging:
                case CrewUnitState.Tipping:
                    Work(deltaTime);
                    break;
            }

            var cell = Cell;
            _dispatcher.SetCell(Id, cell.x, cell.y);
            var ground = TerrainSurface.SampleHeight(_grid, Position.x, Position.y);
            Height += (ground - Height) * (1f - (float)Math.Exp(-HeightFollowRate * deltaTime));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
            _designations.Changed -= OnDesignationChanged;
            _dispatcher.Unregister(this);
            if (_ownsDispatcher)
                _dispatcher.Dispose();
        }

        // --- choosing work ------------------------------------------------------------------------

        void ChooseJob()
        {
            _rethink = false;
            _rethinkTimer = IdleRethinkInterval;
            ClearPath();
            var start = Cell;
            UpdateUnreachable();

            var step = Step;
            var full = _loadFull || Inventory.Remaining + Epsilon < step;
            if (!full && TryPlan(CrewJobKind.Dig, start))
                return;
            if (Inventory.Total + Epsilon >= step && TryPlan(CrewJobKind.Fill, start))
                return;
            // Dump only a full load, on a Dump Zone if the player has marked one: a part load with
            // nothing to do stays in the bucket for the next fill rather than going on whatever
            // ground is nearest.
            if (full && (TryPlanDumpZone(start) || TryPlan(CrewJobKind.Dump, start)))
                return;
            if (!full && _dispatcher.RequestRamp(this, _unreachableDigs) && TryPlan(CrewJobKind.Dig, start))
                return;
            // Nothing left it can dig: a part load goes to the Dump Zone, so it idles empty.
            if (!full && Inventory.Total + Epsilon >= step && TryPlanDumpZone(start))
                return;

            Job = CrewJobKind.None;
            _dispatcher.Release(Id);
            var note = _dispatcher.AutoRamp && _dispatcher.RampNote.Length > 0 ? $"; {_dispatcher.RampNote}" : "";
            if (UnreachableCount > 0)
                SetState(CrewUnitState.Unreachable, $"UNREACHABLE: {NearestUnreachable}"
                    + (UnreachableCount > 1 ? $" (+{UnreachableCount - 1} more)" : "") + note);
            else if (DumpZoneFull && Inventory.Total + Epsilon >= step)
                SetState(CrewUnitState.Idle, "Idle: Dump Zone full, nowhere to tip");
            else
                SetState(CrewUnitState.Idle, _designations.Count == 0 ? "Idle: nothing designated" : "Idle: nothing it can do yet");
        }

        bool TryPlan(CrewJobKind kind, Vector2Int start)
        {
            if (!AnyCandidate(kind))
                return false;

            var target = new Vector2Int(-1, -1);
            var found = _pathfinder.TryFindNearest(start.x, start.y, (standX, standZ) =>
            {
                if (!CanStandHere(standX, standZ))
                    return false;
                var standHeight = _grid.GetSurfaceHeight(standX, standZ);
                var bestHeight = float.MaxValue;
                for (var n = 0; n < 8; n++)
                {
                    var x = standX + NeighbourX[n];
                    var z = standZ + NeighbourZ[n];
                    if (!_grid.InBounds(x, z) || !IsJobCell(kind, standHeight, x, z))
                        continue;
                    // On a Dump Zone, the lowest cell in reach fills first.
                    var height = kind == CrewJobKind.DumpZone ? _grid.GetSurfaceHeight(x, z) : 0f;
                    if (height >= bestHeight)
                        continue;
                    bestHeight = height;
                    target = new Vector2Int(x, z);
                }

                return bestHeight < float.MaxValue;
            }, _path);
            if (!found)
                return false;

            Job = kind;
            JobTarget = target;
            JobStand = _path[_path.Count - 1];
            _jobHeight = _designations.GetTarget(target.x, target.y);
            _pathIndex = 0;
            _waitTimer = 0f;
            _dispatcher.Claim(target.x, target.y, Id);
            MarkPath();
            SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            return true;
        }

        /// <summary>A Dump Zone cell below the stand first (fill the hole), then any with room under its cap.</summary>
        bool TryPlanDumpZone(Vector2Int start)
        {
            DumpZoneFull = false;
            if (_designations.DumpZoneCount == 0)
                return false;
            _zoneBelowOnly = true;
            if (TryPlan(CrewJobKind.DumpZone, start))
                return true;
            _zoneBelowOnly = false;
            if (TryPlan(CrewJobKind.DumpZone, start))
                return true;
            DumpZoneFull = true;
            return false;
        }

        /// <summary>
        /// Whether any cell could be worked for this job from a reachable place to stand: set
        /// lookups only. A nearest-first search with no goal would expand every reachable cell on
        /// the map; this is what keeps an UNREACHABLE unit cheap.
        /// </summary>
        bool AnyCandidate(CrewJobKind kind)
        {
            IReadOnlyList<int> cells;
            switch (kind)
            {
                case CrewJobKind.Dig:
                case CrewJobKind.Fill:
                    cells = _designations.ActiveCells;
                    break;
                case CrewJobKind.DumpZone:
                    cells = _designations.DumpZoneCells;
                    break;
                default:
                    // Undesignated ground to dump on is almost everywhere; the search ends quickly.
                    return true;
            }

            var width = _grid.Width;
            var me = Cell;
            for (var i = 0; i < cells.Count; i++)
            {
                var x = cells[i] % width;
                var z = cells[i] / width;
                for (var n = 0; n < 8; n++)
                {
                    var standX = x + NeighbourX[n];
                    var standZ = z + NeighbourZ[n];
                    if (!_grid.InBounds(standX, standZ) || !CanStandHere(standX, standZ)
                        || !_dispatcher.Regions.CanReach(me.x, me.y, standX, standZ))
                        continue;
                    if (IsJobCell(kind, _grid.GetSurfaceHeight(standX, standZ), x, z))
                        return true;
                }
            }

            return false;
        }

        bool IsJobCell(CrewJobKind kind, float standHeight, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            if (!WithinReach(standHeight, height))
                return false;

            switch (kind)
            {
                case CrewJobKind.Dig:
                    return !_dispatcher.IsClaimedByOther(x, z, Id) && CanDigStep(standHeight, x, z);
                case CrewJobKind.Fill:
                    return !_dispatcher.IsClaimedByOther(x, z, Id)
                        && _designations.GetKind(x, z) == DesignationKind.Fill
                        && FillAmount(standHeight, x, z) + Epsilon >= Step;
                case CrewJobKind.Dump:
                    // Not its own cell, not into a hole (a pit it just dug is no longer designated,
                    // so without this it would refill it), not beside open work, and not where
                    // another unit is standing or heading.
                    var own = Cell;
                    return (x != own.x || z != own.y)
                        && height + Epsilon >= standHeight
                        && !_dispatcher.NearWork(x, z)
                        && CanTipHere(x, z)
                        && DumpAmount(standHeight, x, z) + Epsilon >= Step;
                case CrewJobKind.DumpZone:
                    var self = Cell;
                    return _designations.IsDumpZone(x, z)
                        && _designations.GetKind(x, z) == DesignationKind.None
                        && (x != self.x || z != self.y)
                        && (!_zoneBelowOnly || height < standHeight - Epsilon)
                        && CanTipHere(x, z)
                        && ZoneAmount(standHeight, x, z) + Epsilon >= Step;
                default:
                    return false;
            }
        }

        /// <summary>No tipping where another unit stands, or on a cell it is about to drive over.</summary>
        bool CanTipHere(int x, int z) =>
            !_dispatcher.IsOccupiedByOther(x, z, Id) && !_dispatcher.IsOnAnotherPath(x, z, Id);

        /// <summary>
        /// Whether one more step can be dug from (x, z) by a unit standing at
        /// <paramref name="standHeight"/>: it is designated, has diggable ground on top, the cut
        /// stays above its <see cref="JobDispatcher.DigFloor"/>, and the cut surface is still
        /// within reach.
        /// </summary>
        bool CanDigStep(float standHeight, int x, int z)
        {
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return false;
            var after = _grid.GetSurfaceHeight(x, z) - Step;
            if (after < _dispatcher.DigFloor(x, z) - Epsilon || after < standHeight - DigReachLevels * Step - Epsilon)
                return false;
            Span<Layer> diggable = stackalloc Layer[1];
            return _grid.PeekRemove(x, z, Step, diggable) > 0;
        }

        /// <summary>
        /// Where the unit may stand to work: not on a cell another unit holds, and not on one that
        /// is still to be dug, which would dig the ground round itself away and strand it on a
        /// pillar. With benching on, a dig cell that is at its floor (so nothing can be dug there
        /// yet) is fine, as long as no undesignated neighbour is higher: benching is for taking
        /// hills down, and in a pit it would let the unit dig itself in.
        /// </summary>
        bool CanStandHere(int x, int z)
        {
            if (_dispatcher.IsOccupiedByOther(x, z, Id))
                return false;
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return true;
            if (!_dispatcher.Benching)
                return false;

            var height = _grid.GetSurfaceHeight(x, z);
            if (height - Step >= _dispatcher.DigFloor(x, z) - Epsilon)
                return false;
            for (var n = 0; n < 8; n++)
            {
                var nx = x + NeighbourX[n];
                var nz = z + NeighbourZ[n];
                if (_grid.InBounds(nx, nz) && _designations.GetKind(nx, nz) != DesignationKind.Dig && _grid.GetSurfaceHeight(nx, nz) > height + Epsilon)
                    return false;
            }

            return true;
        }

        /// <summary>How much to tip on a fill: up to its target, never out of reach, whole steps of load.</summary>
        float FillAmount(float standHeight, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            var toTarget = _designations.GetTarget(x, z) - height;
            return Math.Min(toTarget, Math.Min(ReachHeadroom(standHeight, height), WholeStepsHeld()));
        }

        /// <summary>
        /// Spoil heaps only go one climbable step above where the unit stands. Heaping up to dig
        /// reach (2 m) was tried first: on the mound test the unit ringed its work with 2 m heaps
        /// it could not drive over, and then had nowhere left to dump.
        /// </summary>
        float DumpAmount(float standHeight, int x, int z) =>
            Math.Min(standHeight + _dispatcher.Climb - _grid.GetSurfaceHeight(x, z), WholeStepsHeld());

        /// <summary>
        /// On a Dump Zone: a cell below the stand is filled up to it, one level with it takes a
        /// heap one climbable step high, and nothing goes above the zone's cap.
        /// </summary>
        float ZoneAmount(float standHeight, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            var room = height < standHeight - Epsilon ? standHeight - height : standHeight + _dispatcher.Climb - height;
            return Math.Min(Math.Min(room, _designations.DumpZoneCap(x, z) - height), WholeStepsHeld());
        }

        float ReachHeadroom(float standHeight, float cellHeight) => standHeight + DigReachLevels * Step - cellHeight;

        float WholeStepsHeld() => (float)Math.Floor((Inventory.Total + Epsilon) / Step) * Step;

        /// <summary>
        /// Counts designations that no reachable cell can work, and lists the unreachable digs for
        /// the ramp planner. Runs at each job choice, so the readout can say which designations
        /// are cut off even while the unit is busy with others. A dig cell held up by its bench
        /// floor is waiting, not unreachable.
        /// </summary>
        void UpdateUnreachable()
        {
            UnreachableCount = 0;
            NearestUnreachable = "";
            _unreachableDigs.Clear();
            if (_designations.Count == 0)
                return;

            var width = _grid.Width;
            var nearestDistance = float.MaxValue;
            foreach (var cell in _designations.ActiveCells)
            {
                var x = cell % width;
                var z = cell / width;
                var kind = _designations.GetKind(x, z);
                if (kind == DesignationKind.Dig && _grid.GetSurfaceHeight(x, z) - Step < _dispatcher.DigFloor(x, z) - Epsilon)
                    continue;
                if (CanWorkFromSomewhereReachable(x, z))
                    continue;

                UnreachableCount++;
                if (kind == DesignationKind.Dig && !_designations.IsAuto(x, z))
                    _unreachableDigs.Add(cell);
                var distance = (new Vector2(x + 0.5f, z + 0.5f) - Position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    NearestUnreachable = DescribeDesignation(x, z);
                }
            }
        }

        /// <summary>
        /// Whether this unit could work the designation on (x, z) from somewhere it can drive to.
        /// The dispatcher asks this to know when a ramp has done its job.
        /// </summary>
        public bool CanWorkFromSomewhereReachable(int x, int z)
        {
            var kind = _designations.GetKind(x, z);
            if (kind == DesignationKind.None)
                return false;
            var me = Cell;
            var height = _grid.GetSurfaceHeight(x, z);
            for (var n = 0; n < 8; n++)
            {
                var standX = x + NeighbourX[n];
                var standZ = z + NeighbourZ[n];
                if (!_grid.InBounds(standX, standZ) || !_dispatcher.Regions.CanReach(me.x, me.y, standX, standZ) || !CanStandHere(standX, standZ))
                    continue;
                var standHeight = _grid.GetSurfaceHeight(standX, standZ);
                if (kind == DesignationKind.Dig ? CanDigStep(standHeight, x, z) : WithinReach(standHeight, height))
                    return true;
            }

            return false;
        }

        // --- moving -----------------------------------------------------------------------------

        void Move(float deltaTime)
        {
            if (_repath && !Replan(null))
                return;

            // Units never share a cell: wait for the one in the way, then go round it, and give
            // the job up if there is no way round.
            var next = NextCell();
            if (next.x >= 0 && _dispatcher.IsOccupiedByOther(next.x, next.y, Id))
            {
                _waitTimer += deltaTime;
                if (_waitTimer < TrafficWaitSeconds)
                {
                    if (State != CrewUnitState.Waiting)
                        SetState(CrewUnitState.Waiting, $"Waiting for a unit at ({next.x}, {next.y})");
                    return;
                }

                if (!Replan((x, z) => _dispatcher.IsOccupiedByOther(x, z, Id)))
                {
                    // No way round: get out of the way instead, so neither unit is stuck waiting
                    // on the other. In a one-cell corridor this is what breaks the deadlock.
                    TryYield();
                    return;
                }
            }
            else if (State == CrewUnitState.Waiting)
            {
                _waitTimer = 0f;
                SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            }

            var travel = Speed * deltaTime;
            while (travel > 0f && _pathIndex < _path.Count)
            {
                var waypoint = _path[_pathIndex];
                var target = new Vector2(waypoint.x + 0.5f, waypoint.y + 0.5f);
                var delta = target - Position;
                var distance = delta.magnitude;
                if (distance > 1e-5f)
                    TurnToward(delta, deltaTime);
                if (distance <= travel)
                {
                    Position = target;
                    travel -= distance;
                    _pathIndex++;
                    _dispatcher.SetCell(Id, waypoint.x, waypoint.y);
                    _dispatcher.SetPath(Id, _path, _pathIndex);
                }
                else
                {
                    Position += delta / distance * travel;
                    travel = 0f;
                }
            }

            if (_pathIndex >= _path.Count)
                Arrive();
        }

        /// <summary>
        /// Drives to the nearest cell that is not another unit's and not on its way, giving this
        /// unit's job up. Called when it is blocked and there is no way round.
        /// </summary>
        bool TryYield()
        {
            var me = Cell;
            var found = _pathfinder.TryFindNearest(me.x, me.y,
                (x, z) => !_dispatcher.IsOccupiedByOther(x, z, Id) && !_dispatcher.IsOnAnotherPath(x, z, Id) && CanStandHere(x, z),
                _path);
            if (!found || _path.Count <= 1)
                return false;

            Job = CrewJobKind.Yield;
            JobTarget = _path[_path.Count - 1];
            JobStand = JobTarget;
            _pathIndex = 0;
            _rethink = false;
            MarkPath();
            SetState(CrewUnitState.Moving, "Standing aside at " + DescribeJob());
            return true;
        }

        /// <summary>The next cell the unit will drive into, or (-1, -1) if it is on its last one.</summary>
        Vector2Int NextCell()
        {
            var me = Cell;
            for (var i = _pathIndex; i < _path.Count; i++)
                if (_path[i] != me)
                    return _path[i];
            return new Vector2Int(-1, -1);
        }

        /// <summary>
        /// Re-plans the way to the job. False when there is none, in which case the job is given
        /// up (its claim released) and a fresh one chosen next tick.
        /// </summary>
        bool Replan(Func<int, int, bool> avoid)
        {
            _repath = false;
            RepathCount++;
            _waitTimer = 0f;
            var cell = Cell;
            if (!_pathfinder.TryFindPath(cell.x, cell.y, JobStand.x, JobStand.y, _path, avoid))
            {
                _rethink = true;
                _dispatcher.Release(Id);
                return false;
            }

            _pathIndex = 0;
            MarkPath();
            if (State == CrewUnitState.Waiting)
                SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            else
                Version++;
            return true;
        }

        void TurnToward(Vector2 direction, float deltaTime)
        {
            var desired = (float)(Math.Atan2(direction.x, direction.y) * 180.0 / Math.PI);
            Heading = Mathf.MoveTowardsAngle(Heading, desired, TurnRate * deltaTime);
        }

        void Arrive()
        {
            ClearPath();
            _workTimer = 0f;
            var toTarget = new Vector2(JobTarget.x + 0.5f, JobTarget.y + 0.5f) - Position;
            if (toTarget.sqrMagnitude > 1e-6f)
                Heading = (float)(Math.Atan2(toTarget.x, toTarget.y) * 180.0 / Math.PI);
            if (Job == CrewJobKind.Yield)
            {
                _rethink = true;
                SetState(CrewUnitState.Idle, "Stood aside; looking for work");
            }
            else if (Job == CrewJobKind.Dig)
                SetState(CrewUnitState.Digging, "Digging " + DescribeJob());
            else
                SetState(CrewUnitState.Tipping, (Job == CrewJobKind.Fill ? "Filling " : "Dumping ") + DescribeJob());
        }

        // --- working ----------------------------------------------------------------------------

        void Work(float deltaTime)
        {
            _workTimer += deltaTime;
            if (_workTimer < WorkInterval)
                return;
            _workTimer -= WorkInterval;

            if (Cell != JobStand)
            {
                _rethink = true;
                return;
            }

            if (State == CrewUnitState.Digging)
                DigStep();
            else
                TipStep();
        }

        void DigStep()
        {
            var target = JobTarget;
            var standHeight = _grid.GetSurfaceHeight(JobStand.x, JobStand.y);
            if (!WithinReach(standHeight, _grid.GetSurfaceHeight(target.x, target.y)) || !CanDigStep(standHeight, target.x, target.y))
            {
                if (_designations.GetKind(target.x, target.y) == DesignationKind.Dig && !HasDiggableTop(target.x, target.y))
                {
                    // Bedrock: the designation can never be met. Drop it rather than loop on it.
                    _designations.Clear(target.x, target.y);
                }

                _rethink = true;
                return;
            }

            _dispatcher.MarkWorked(target.x, target.y);
            var report = Excavation.Dig(_grid, Inventory, target.x, target.y, 0, Step);
            if (report.WasFull)
            {
                _loadFull = true;
                _rethink = true;
                return;
            }

            Version++;
            if (_designations.GetKind(target.x, target.y) != DesignationKind.Dig)
                _rethink = true;
        }

        bool HasDiggableTop(int x, int z)
        {
            Span<Layer> diggable = stackalloc Layer[1];
            return _grid.PeekRemove(x, z, Step, diggable) > 0;
        }

        void TipStep()
        {
            var target = JobTarget;
            var standHeight = _grid.GetSurfaceHeight(JobStand.x, JobStand.y);
            float amount;
            switch (Job)
            {
                case CrewJobKind.Fill:
                    if (_designations.GetKind(target.x, target.y) != DesignationKind.Fill)
                    {
                        _rethink = true;
                        return;
                    }

                    amount = FillAmount(standHeight, target.x, target.y);
                    _dispatcher.MarkWorked(target.x, target.y);
                    break;
                case CrewJobKind.DumpZone:
                    amount = _designations.IsDumpZone(target.x, target.y) ? ZoneAmount(standHeight, target.x, target.y) : 0f;
                    break;
                default:
                    amount = DumpAmount(standHeight, target.x, target.y);
                    break;
            }

            if (amount + Epsilon >= Step && CanTipHere(target.x, target.y))
            {
                var report = Excavation.Tip(_grid, Inventory, target.x, target.y, amount);
                if (report.Tipped > 0f)
                    _loadFull = false;
            }

            Version++;
            _rethink = true;
        }

        // --- bookkeeping --------------------------------------------------------------------------

        void OnCellChanged(int x, int z)
        {
            if ((State == CrewUnitState.Moving || State == CrewUnitState.Waiting) && _onPath[z * _grid.Width + x])
                _repath = true;
        }

        void OnDesignationChanged(int x, int z)
        {
            if (State == CrewUnitState.Idle || State == CrewUnitState.Unreachable)
                _rethink = true;
            else if (x == JobTarget.x && z == JobTarget.y && Job != CrewJobKind.Dump)
                _rethink = true;
        }

        /// <summary>
        /// Flags the cells whose height the path depends on: the path's cells, and for each
        /// diagonal step the two cells it cuts past. Also tells the dispatcher where this unit is
        /// heading, so nobody tips spoil in front of it.
        /// </summary>
        void MarkPath()
        {
            UnmarkPath();
            for (var i = 0; i < _path.Count; i++)
            {
                Mark(_path[i].x, _path[i].y);
                if (i == 0)
                    continue;
                var a = _path[i - 1];
                var b = _path[i];
                if (a.x != b.x && a.y != b.y)
                {
                    Mark(b.x, a.y);
                    Mark(a.x, b.y);
                }
            }

            _dispatcher.SetPath(Id, _path, _pathIndex);
        }

        void Mark(int x, int z)
        {
            var cell = z * _grid.Width + x;
            if (_onPath[cell])
                return;
            _onPath[cell] = true;
            _onPathCells.Add(cell);
        }

        void UnmarkPath()
        {
            foreach (var cell in _onPathCells)
                _onPath[cell] = false;
            _onPathCells.Clear();
        }

        void ClearPath()
        {
            UnmarkPath();
            _path.Clear();
            _pathIndex = 0;
            _repath = false;
            _waitTimer = 0f;
            _dispatcher.SetPath(Id, null, 0);
        }

        void SetState(CrewUnitState state, string status)
        {
            State = state;
            Status = status;
            Version++;
        }

        string DescribeJob()
        {
            switch (Job)
            {
                case CrewJobKind.Dig:
                    if (_designations.IsAuto(JobTarget.x, JobTarget.y))
                    {
                        var toward = _dispatcher.RampTarget;
                        return $"ramp ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m, toward ({toward.x}, {toward.y})";
                    }

                    return $"dig ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Fill:
                    return $"fill ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Dump:
                    return $"spoil at ({JobTarget.x}, {JobTarget.y})";
                case CrewJobKind.DumpZone:
                    return $"spoil at ({JobTarget.x}, {JobTarget.y}) (dump zone)";
                case CrewJobKind.Yield:
                    return $"({JobTarget.x}, {JobTarget.y})";

                default:
                    return "nothing";
            }
        }

        string DescribeDesignation(int x, int z)
        {
            var kind = _designations.GetKind(x, z);
            var verb = kind == DesignationKind.Fill ? "fill" : "dig";
            return $"{verb} ({x}, {z}) to {_designations.GetTarget(x, z):0.#} m";
        }
    }
}
