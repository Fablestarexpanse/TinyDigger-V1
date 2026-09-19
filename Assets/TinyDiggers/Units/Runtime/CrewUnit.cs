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
    }

    /// <summary>
    /// One digger that has to physically reach its work. Plain C#: the scene side calls
    /// <see cref="Tick"/> and draws the body at <see cref="Position"/>, <see cref="Height"/> and
    /// <see cref="Heading"/>.
    ///
    /// Job loop:
    /// - With room in the load: go to the nearest cell from which a Dig designation is within
    ///   dig reach, and take one height step at a time until it is met or the load is full.
    /// - With at least a step of load and no reachable dig (or a full load): go to the nearest
    ///   reachable Fill and tip, never past its target or out of reach.
    /// - With a full load and no reachable Fill: tip on the nearest reachable Dump Zone cell; with
    ///   no Dump Zone, on the nearest cell that is not its own, not lower than where it stands
    ///   (so never back into a pit it dug), and not beside any designation or finished work.
    /// - Nothing reachable to dig: with <see cref="AutoRamp"/> on, cut a ramp toward the nearest
    ///   unreachable dig (below). Failing that, a part load goes to a Dump Zone if there is one;
    ///   then idle, or UNREACHABLE if designations exist that it cannot work.
    ///
    /// Dig reach (TERRAIN_REFERENCE.md section 4): a cell can be dug or filled only from an
    /// adjacent cell whose surface is within <see cref="DigReachLevels"/> height steps of it.
    /// The unit never works from a cell that is itself still to be dug, so it cannot dig the
    /// ground out from under itself; it may drive across one.
    ///
    /// With <see cref="AutoRamp"/> on, two things keep work reachable:
    /// - Benching. A dig cell is never taken lower than one climbable step below any
    ///   neighbouring dig cell (<see cref="DigFloor"/>), so a designated hill comes down in layers
    ///   and stays a staircase the unit can drive. A dig cell sitting at that floor is "benched":
    ///   nothing more can be dug there for now, so the unit may stand on it, unless it is lower
    ///   than undesignated ground beside it (in a pit that would let it dig itself in).
    /// - Auto ramps. When nothing is reachable, it plans a corridor to the nearest unreachable
    ///   dig with the step limit lifted but steep steps penalised, finds the first step on it
    ///   that is too steep, and designates an Auto dig of the higher cell to one step above the
    ///   lower. When that is met it looks again, until the target can be worked; any Auto left
    ///   then is removed. Auto designations only go on undesignated cells. If the player cancels
    ///   one, the unit leaves that target alone for <see cref="RampCancelSeconds"/>.
    ///
    /// "Nearest" is by path cost, found with one Dijkstra search outward from the unit rather
    /// than a path to every candidate; a set lookup first checks that there is a candidate at
    /// all, so a search never floods the whole map for nothing. The path is re-planned whenever
    /// a cell on it changes.
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

        /// <summary>Bench dig areas and cut its own ramps to work it cannot otherwise reach.</summary>
        public bool AutoRamp = true;

        /// <summary>Seconds a target is left alone after the player cancels a ramp step toward it.</summary>
        public float RampCancelSeconds = 10f;

        /// <summary>Seconds a target is left alone after no ramp to it could be planned.</summary>
        public float RampBlockedSeconds = 10f;

        /// <summary>Extra corridor cost per metre a step is over the climb limit.</summary>
        public float RampSteepPenalty = 20f;

        /// <summary>Most unreachable targets tried for a ramp per rethink.</summary>
        public int RampAttempts = 4;

        readonly TerrainGrid _grid;
        readonly DesignationMap _designations;
        readonly GridPathfinder _pathfinder;
        readonly ReachabilityCache _reach;
        readonly List<Vector2Int> _path = new List<Vector2Int>();
        readonly List<Vector2Int> _corridor = new List<Vector2Int>();
        readonly bool[] _onPath;
        readonly List<int> _onPathCells = new List<int>();
        readonly List<int> _unreachableDigs = new List<int>();
        readonly Dictionary<int, float> _rampSuppressedUntil = new Dictionary<int, float>();

        /// <summary>Cells this unit has dug or filled, so it never dumps spoil back onto finished work.</summary>
        readonly bool[] _worked;

        int _pathIndex;
        float _jobHeight;
        bool _repath;
        bool _rethink = true;
        float _rethinkTimer;
        float _workTimer;
        bool _loadFull;
        bool _zoneBelowOnly;
        float _clock;
        int _rampTarget = -1;
        int _rampAuto = -1;
        bool _disposed;

        public CrewUnit(TerrainGrid grid, DesignationMap designations, GridPathfinder pathfinder, int startX, int startZ, float capacity = MaterialInventory.DefaultCapacity)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _designations = designations ?? throw new ArgumentNullException(nameof(designations));
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            if (!grid.InBounds(startX, startZ))
                throw new ArgumentOutOfRangeException(nameof(startX), "The unit must start on the map.");

            Inventory = new MaterialInventory(capacity);
            Position = new Vector2(startX + 0.5f, startZ + 0.5f);
            Height = TerrainSurface.SampleHeight(grid, Position.x, Position.y);
            _onPath = new bool[grid.Width * grid.Height];
            _worked = new bool[grid.Width * grid.Height];
            _reach = new ReachabilityCache(grid, pathfinder);

            _grid.CellChanged += OnCellChanged;
            _designations.Changed += OnDesignationChanged;
            _designations.AutoCancelled += OnAutoCancelled;
            Status = "Idle";
        }

        public MaterialInventory Inventory { get; }

        /// <summary>The cells the unit can drive to. Exposed for tests and the readout.</summary>
        public ReachabilityCache Reachability => _reach;

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

        /// <summary>Whether a ramp is being cut toward an unreachable dig.</summary>
        public bool HasRamp => _rampTarget >= 0;

        /// <summary>The unreachable dig the current ramp leads to; only meaningful with <see cref="HasRamp"/>.</summary>
        public Vector2Int RampTarget => ToCell(_rampTarget);

        /// <summary>Whether the current job is cutting an Auto ramp step.</summary>
        public bool OnAutoRamp => Job == CrewJobKind.Dig && _designations.IsAuto(JobTarget.x, JobTarget.y);

        /// <summary>What the ramp planner last decided, for the readout; empty if it has nothing to say.</summary>
        public string RampNote { get; private set; } = "";

        /// <summary>The last corridor planned for a ramp, unit side first. For tests and debug drawing.</summary>
        public IReadOnlyList<Vector2Int> RampCorridor => _corridor;

        /// <summary>Changes whenever state, job or status changes, so displays redraw only then.</summary>
        public int Version { get; private set; }

        /// <summary>How many times the path was re-planned because a cell on it changed. For tests.</summary>
        public int RepathCount { get; private set; }

        float Step => _grid.HeightStep > 0f ? _grid.HeightStep : 1f;

        /// <summary>The biggest whole number of height steps the unit can drive up or down, at least one.</summary>
        float Climb => Math.Max(1f, (float)Math.Floor((_pathfinder.MaxStepHeight + Epsilon) / Step)) * Step;

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

        /// <summary>
        /// The lowest a dig cell may be taken right now: its target, or with
        /// <see cref="AutoRamp"/> on, one climbable step below its highest dig-designated
        /// neighbour if that is higher. Keeps a designated hill a drivable staircase while it is
        /// taken down, so its upper cells always have somewhere within reach to be worked from.
        /// </summary>
        public float DigFloor(int x, int z)
        {
            var floor = _designations.GetTarget(x, z);
            if (!AutoRamp)
                return floor;

            var climb = Climb;
            for (var n = 0; n < 8; n++)
            {
                var nx = x + NeighbourX[n];
                var nz = z + NeighbourZ[n];
                if (_grid.InBounds(nx, nz) && _designations.GetKind(nx, nz) == DesignationKind.Dig)
                    floor = Math.Max(floor, _grid.GetSurfaceHeight(nx, nz) - climb);
            }

            return floor;
        }

        /// <summary>Makes the unit choose its job afresh on the next tick, as if the world had changed.</summary>
        public void RequestRethink() => _rethink = true;

        public void Tick(float deltaTime)
        {
            if (_disposed)
                return;

            _clock += deltaTime;
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
                    Move(deltaTime);
                    break;
                case CrewUnitState.Digging:
                case CrewUnitState.Tipping:
                    Work(deltaTime);
                    break;
            }

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
            _designations.AutoCancelled -= OnAutoCancelled;
            _reach.Dispose();
        }

        // --- choosing work ------------------------------------------------------------------------

        void ChooseJob()
        {
            _rethink = false;
            _rethinkTimer = IdleRethinkInterval;
            ClearPath();
            var start = Cell;
            _reach.Update(start.x, start.y);
            UpdateUnreachable();
            UpdateRamp();

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
            if (!full && AutoRamp && TryStartRamp() && TryPlan(CrewJobKind.Dig, start))
                return;
            // Nothing left it can dig: a part load goes to the Dump Zone, so it idles empty.
            if (!full && Inventory.Total + Epsilon >= step && TryPlanDumpZone(start))
                return;

            Job = CrewJobKind.None;
            if (UnreachableCount > 0)
                SetState(CrewUnitState.Unreachable, $"UNREACHABLE: {NearestUnreachable}"
                    + (UnreachableCount > 1 ? $" (+{UnreachableCount - 1} more)" : "")
                    + (AutoRamp && RampNote.Length > 0 ? $"; {RampNote}" : ""));
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
                if (!CanStandToWork(standX, standZ))
                    return false;
                var standHeight = _grid.GetSurfaceHeight(standX, standZ);
                var bestHeight = float.MaxValue;
                for (var n = 0; n < 8; n++)
                {
                    var x = standX + NeighbourX[n];
                    var z = standZ + NeighbourZ[n];
                    if (!_grid.InBounds(x, z) || !IsJobCell(kind, standX, standZ, standHeight, x, z))
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
            MarkPath();
            SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            return true;
        }

        /// <summary>A Dump Zone cell below the stand first (fill the hole), then any with room in reach.</summary>
        bool TryPlanDumpZone(Vector2Int start)
        {
            if (_designations.DumpZoneCount == 0)
                return false;
            _zoneBelowOnly = true;
            if (TryPlan(CrewJobKind.DumpZone, start))
                return true;
            _zoneBelowOnly = false;
            return TryPlan(CrewJobKind.DumpZone, start);
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
            for (var i = 0; i < cells.Count; i++)
            {
                var x = cells[i] % width;
                var z = cells[i] / width;
                for (var n = 0; n < 8; n++)
                {
                    var standX = x + NeighbourX[n];
                    var standZ = z + NeighbourZ[n];
                    if (!_grid.InBounds(standX, standZ) || !_reach.Contains(standX, standZ) || !CanStandToWork(standX, standZ))
                        continue;
                    if (IsJobCell(kind, standX, standZ, _grid.GetSurfaceHeight(standX, standZ), x, z))
                        return true;
                }
            }

            return false;
        }

        bool IsJobCell(CrewJobKind kind, int standX, int standZ, float standHeight, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            if (!WithinReach(standHeight, height))
                return false;

            switch (kind)
            {
                case CrewJobKind.Dig:
                    return CanDigStep(standHeight, x, z);
                case CrewJobKind.Fill:
                    return _designations.GetKind(x, z) == DesignationKind.Fill && FillAmount(standHeight, x, z) + Epsilon >= Step;
                case CrewJobKind.Dump:
                    // Not its own cell, not into a hole (a pit it just dug is no longer designated,
                    // so without this it would refill it), and not beside open work.
                    var own = Cell;
                    return (x != own.x || z != own.y)
                        && height + Epsilon >= standHeight
                        && !NearWork(x, z)
                        && DumpAmount(standHeight, x, z) + Epsilon >= Step;
                case CrewJobKind.DumpZone:
                    var self = Cell;
                    return _designations.IsDumpZone(x, z)
                        && _designations.GetKind(x, z) == DesignationKind.None
                        && (x != self.x || z != self.y)
                        && (!_zoneBelowOnly || height < standHeight - Epsilon)
                        && ZoneAmount(standHeight, x, z) + Epsilon >= Step;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether one more step can be dug from (x, z) by a unit standing at
        /// <paramref name="standHeight"/>: it is designated, has diggable ground on top, the cut
        /// stays above its <see cref="DigFloor"/>, and the cut surface is still within reach.
        /// </summary>
        bool CanDigStep(float standHeight, int x, int z)
        {
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return false;
            var after = _grid.GetSurfaceHeight(x, z) - Step;
            if (after < DigFloor(x, z) - Epsilon || after < standHeight - DigReachLevels * Step - Epsilon)
                return false;
            Span<Layer> diggable = stackalloc Layer[1];
            return _grid.PeekRemove(x, z, Step, diggable) > 0;
        }

        /// <summary>
        /// Where the unit may stand to work. Not on a cell that is still to be dug: it would dig
        /// the ground round itself away and strand itself on a pillar. With
        /// <see cref="AutoRamp"/> on, a dig cell that is benched (at its <see cref="DigFloor"/>,
        /// so nothing can be dug there yet) is fine, as long as no undesignated neighbour is
        /// higher: benching is for taking hills down, and in a pit it would let the unit dig
        /// itself in.
        /// </summary>
        bool CanStandToWork(int x, int z)
        {
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return true;
            if (!AutoRamp)
                return false;

            var height = _grid.GetSurfaceHeight(x, z);
            if (height - Step >= DigFloor(x, z) - Epsilon)
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

        /// <summary>
        /// Whether (x, z) or any of its eight neighbours carries a designation or has been worked
        /// by this unit. Met designations clear themselves, so without the worked-cell memory a
        /// finished pit at ground level looks like any other flat ground to dump on.
        /// </summary>
        bool NearWork(int x, int z)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!_grid.InBounds(x + dx, z + dz))
                        continue;
                    if (_designations.GetKind(x + dx, z + dz) != DesignationKind.None || _worked[(z + dz) * _grid.Width + x + dx])
                        return true;
                }
            }

            return false;
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
            Math.Min(standHeight + Climb - _grid.GetSurfaceHeight(x, z), WholeStepsHeld());

        /// <summary>On a Dump Zone: a cell below the stand is filled up to it; one level with it takes a heap one climbable step high.</summary>
        float ZoneAmount(float standHeight, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            var room = height < standHeight - Epsilon ? standHeight - height : standHeight + Climb - height;
            return Math.Min(room, WholeStepsHeld());
        }

        float ReachHeadroom(float standHeight, float cellHeight) => standHeight + DigReachLevels * Step - cellHeight;

        float WholeStepsHeld() => (float)Math.Floor((Inventory.Total + Epsilon) / Step) * Step;

        /// <summary>
        /// Counts designations that no reachable cell can work, and lists the unreachable digs for
        /// the ramp planner. Runs at each job choice, so the readout can say which designations
        /// are cut off even while the unit is busy with others. A dig cell held up by its
        /// <see cref="DigFloor"/> is waiting, not unreachable.
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
                if (kind == DesignationKind.Dig && _grid.GetSurfaceHeight(x, z) - Step < DigFloor(x, z) - Epsilon)
                    continue;
                if (CanBeWorkedFromReachableCell(kind, x, z))
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

        bool CanBeWorkedFromReachableCell(DesignationKind kind, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            for (var n = 0; n < 8; n++)
            {
                var standX = x + NeighbourX[n];
                var standZ = z + NeighbourZ[n];
                if (!_grid.InBounds(standX, standZ) || !_reach.Contains(standX, standZ) || !CanStandToWork(standX, standZ))
                    continue;
                var standHeight = _grid.GetSurfaceHeight(standX, standZ);
                if (kind == DesignationKind.Dig ? CanDigStep(standHeight, x, z) : WithinReach(standHeight, height))
                    return true;
            }

            return false;
        }

        // --- auto ramps ---------------------------------------------------------------------------

        /// <summary>
        /// Keeps the current ramp honest at each job choice: dropped when auto-ramping is off or
        /// its target is met, cleared or no longer unreachable (any Auto step left is removed);
        /// otherwise, once its Auto step is met, the next one is placed.
        /// </summary>
        void UpdateRamp()
        {
            if (_rampTarget < 0)
                return;

            var target = ToCell(_rampTarget);
            if (!AutoRamp || _designations.GetKind(target.x, target.y) != DesignationKind.Dig || !_unreachableDigs.Contains(_rampTarget))
            {
                EndRamp(0f, "");
                return;
            }

            if (_rampAuto >= 0 && _designations.IsAuto(_rampAuto % _grid.Width, _rampAuto / _grid.Width))
                return;
            _rampAuto = -1;
            if (!PlaceRampStep(_rampTarget))
                EndRamp(RampBlockedSeconds, RampNote);
        }

        /// <summary>
        /// Called when nothing is reachable to dig. If a ramp is already running, its step could
        /// not be worked either, so it is abandoned. Then tries the nearest unreachable digs, by
        /// straight-line distance, until one gets a ramp step.
        /// </summary>
        bool TryStartRamp()
        {
            if (_rampTarget >= 0)
                EndRamp(RampBlockedSeconds, "ramp step could not be worked");

            var attempts = 0;
            while (attempts < RampAttempts)
            {
                var best = -1;
                var bestDistance = float.MaxValue;
                foreach (var cell in _unreachableDigs)
                {
                    if (_rampSuppressedUntil.TryGetValue(cell, out var until) && _clock < until)
                        continue;
                    var distance = (new Vector2(cell % _grid.Width + 0.5f, cell / _grid.Width + 0.5f) - Position).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = cell;
                    }
                }

                if (best < 0)
                    return false;
                attempts++;
                if (PlaceRampStep(best))
                {
                    _rampTarget = best;
                    return true;
                }

                _rampSuppressedUntil[best] = _clock + RampBlockedSeconds;
            }

            return false;
        }

        /// <summary>
        /// Plans a corridor from the unit to the target and designates an Auto dig on the higher
        /// side of the first step along it that is too steep to drive: down to one step above the
        /// lower side, worked from the cell before it. False, with <see cref="RampNote"/> saying
        /// why, if the corridor has no such step it can fix.
        /// </summary>
        bool PlaceRampStep(int targetCell)
        {
            var target = ToCell(targetCell);
            var start = Cell;
            var found = _pathfinder.TryFindCorridor(start.x, start.y, target.x, target.y, _corridor, RampSteepPenalty,
                (x, z) => _designations.GetKind(x, z) == DesignationKind.None || _reach.Contains(x, z));
            if (!found)
                return Blocked($"no ramp route to ({target.x}, {target.y})");

            var climb = Climb;
            for (var i = 1; i < _corridor.Count - 1; i++)
            {
                var a = _corridor[i - 1];
                var b = _corridor[i];
                var heightA = _grid.GetSurfaceHeight(a.x, a.y);
                var heightB = _grid.GetSurfaceHeight(b.x, b.y);
                if (Math.Abs(heightB - heightA) <= climb + Epsilon)
                    continue;

                // The step has to come down on its high side, worked from the cell before that.
                var climbing = heightB > heightA;
                if (!climbing && i < 2)
                    return Blocked($"drop at ({a.x}, {a.y}) right where it stands");
                var high = climbing ? b : a;
                var stand = climbing ? a : _corridor[i - 2];
                var lowHeight = Math.Min(heightA, heightB);
                if (_designations.GetKind(high.x, high.y) != DesignationKind.None)
                    return Blocked($"ramp would cut into designated ({high.x}, {high.y})");
                var standHeight = _grid.GetSurfaceHeight(stand.x, stand.y);
                var highHeight = _grid.GetSurfaceHeight(high.x, high.y);
                if (!WithinReach(standHeight, highHeight))
                    return Blocked($"{highHeight - standHeight:0.#} m cliff at ({high.x}, {high.y}) is out of reach");

                _designations.Designate(high.x, high.y, DesignationKind.Dig, lowHeight + Step, auto: true);
                _rampAuto = high.y * _grid.Width + high.x;
                RampNote = $"cutting ramp at ({high.x}, {high.y}) toward ({target.x}, {target.y})";
                return true;
            }

            // Every step on the way is drivable, so the target itself is the problem.
            var last = _corridor[_corridor.Count - 2];
            return Blocked(WithinReach(_grid.GetSurfaceHeight(last.x, last.y), _grid.GetSurfaceHeight(target.x, target.y))
                ? $"nowhere to stand beside ({target.x}, {target.y})"
                : $"({target.x}, {target.y}) is out of reach of every way up");
        }

        bool Blocked(string why)
        {
            RampNote = "ramp blocked: " + why;
            return false;
        }

        /// <summary>Drops the ramp, removing its Auto step if still there, and optionally leaves its target alone for a while.</summary>
        void EndRamp(float suppressSeconds, string note)
        {
            if (_rampAuto >= 0)
            {
                var x = _rampAuto % _grid.Width;
                var z = _rampAuto / _grid.Width;
                if (_designations.IsAuto(x, z))
                    _designations.Clear(x, z);
            }

            if (_rampTarget >= 0 && suppressSeconds > 0f)
                _rampSuppressedUntil[_rampTarget] = _clock + suppressSeconds;
            _rampTarget = -1;
            _rampAuto = -1;
            RampNote = note;
        }

        void OnAutoCancelled(int x, int z)
        {
            if (z * _grid.Width + x != _rampAuto)
                return;
            _rampAuto = -1;
            var target = RampTarget;
            EndRamp(RampCancelSeconds, $"ramp to ({target.x}, {target.y}) cancelled; leaving it {RampCancelSeconds:0} s");
            _rethink = true;
        }

        /// <summary>Whether a ramp toward (x, z) is on hold because the player cancelled one or none could be planned.</summary>
        public bool IsRampSuppressed(int x, int z) =>
            _rampSuppressedUntil.TryGetValue(z * _grid.Width + x, out var until) && _clock < until;

        // --- moving -----------------------------------------------------------------------------

        void Move(float deltaTime)
        {
            if (_repath)
            {
                _repath = false;
                RepathCount++;
                var cell = Cell;
                if (!_pathfinder.TryFindPath(cell.x, cell.y, JobStand.x, JobStand.y, _path))
                {
                    _rethink = true;
                    return;
                }

                _pathIndex = 0;
                MarkPath();
                Version++;
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
            if (Job == CrewJobKind.Dig)
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

            _worked[target.y * _grid.Width + target.x] = true;
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
                    _worked[target.y * _grid.Width + target.x] = true;
                    break;
                case CrewJobKind.DumpZone:
                    amount = _designations.IsDumpZone(target.x, target.y) ? ZoneAmount(standHeight, target.x, target.y) : 0f;
                    break;
                default:
                    amount = DumpAmount(standHeight, target.x, target.y);
                    break;
            }

            if (amount + Epsilon >= Step)
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
            if (State == CrewUnitState.Moving && _onPath[z * _grid.Width + x])
                _repath = true;
        }

        void OnDesignationChanged(int x, int z)
        {
            if (State == CrewUnitState.Idle || State == CrewUnitState.Unreachable)
                _rethink = true;
            else if (x == JobTarget.x && z == JobTarget.y && (Job == CrewJobKind.Dig || Job == CrewJobKind.Fill || Job == CrewJobKind.DumpZone))
                _rethink = true;
        }

        /// <summary>
        /// Flags the cells whose height the path depends on: the path's cells, and for each
        /// diagonal step the two cells it cuts past.
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
        }

        void SetState(CrewUnitState state, string status)
        {
            State = state;
            Status = status;
            Version++;
        }

        Vector2Int ToCell(int cell) => cell < 0 ? new Vector2Int(-1, -1) : new Vector2Int(cell % _grid.Width, cell / _grid.Width);

        string DescribeJob()
        {
            switch (Job)
            {
                case CrewJobKind.Dig:
                    if (_designations.IsAuto(JobTarget.x, JobTarget.y))
                    {
                        var toward = RampTarget;
                        return $"ramp ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m, toward ({toward.x}, {toward.y})";
                    }

                    return $"dig ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Fill:
                    return $"fill ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Dump:
                    return $"spoil at ({JobTarget.x}, {JobTarget.y})";
                case CrewJobKind.DumpZone:
                    return $"spoil at ({JobTarget.x}, {JobTarget.y}) (dump zone)";
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
