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

        /// <summary>Tipping a load somewhere undesignated because no fill can take it.</summary>
        Dump,
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
    /// - With a full load and no reachable Fill: dump on the nearest cell that is not its own,
    ///   not lower than where it stands (so never back into a pit it dug), and not beside any
    ///   designation. It never stalls with a full load; a part load with nothing to do is kept.
    /// - Nothing reachable: idle, or UNREACHABLE if designations exist that it cannot work.
    ///
    /// Dig reach (TERRAIN_REFERENCE.md section 4): a cell can be dug or filled only from an
    /// adjacent cell whose surface is within <see cref="DigReachLevels"/> height steps of it.
    /// This is what makes ramps necessary: the top of a hill cannot be worked from its foot.
    /// The unit also never works from a cell that is itself designated for digging, so a dig
    /// area is worked from outside in and the unit cannot strand itself on a pillar; it may
    /// drive across one.
    ///
    /// "Nearest" is by path cost, found with one Dijkstra search outward from the unit rather
    /// than a path to every candidate. The path is re-planned whenever a cell on it changes.
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

        readonly TerrainGrid _grid;
        readonly DesignationMap _designations;
        readonly GridPathfinder _pathfinder;
        readonly List<Vector2Int> _path = new List<Vector2Int>();
        readonly bool[] _onPath;
        readonly List<int> _onPathCells = new List<int>();
        readonly bool[] _reachable;

        /// <summary>Cells this unit has dug or filled, so it never dumps spoil back onto finished work.</summary>
        readonly bool[] _worked;

        int _pathIndex;
        float _jobHeight;
        bool _repath;
        bool _rethink = true;
        float _rethinkTimer;
        float _workTimer;
        bool _loadFull;
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
            _reachable = new bool[grid.Width * grid.Height];
            _worked = new bool[grid.Width * grid.Height];

            _grid.CellChanged += OnCellChanged;
            _designations.Changed += OnDesignationChanged;
            Status = "Idle";
        }

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

        /// <summary>Changes whenever state, job or status changes, so displays redraw only then.</summary>
        public int Version { get; private set; }

        /// <summary>How many times the path was re-planned because a cell on it changed. For tests.</summary>
        public int RepathCount { get; private set; }

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

        public void Tick(float deltaTime)
        {
            if (_disposed)
                return;

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
        }

        // --- choosing work ------------------------------------------------------------------------

        void ChooseJob()
        {
            _rethink = false;
            _rethinkTimer = IdleRethinkInterval;
            ClearPath();
            var start = Cell;
            UpdateUnreachable(start);

            var step = Step;
            var full = _loadFull || Inventory.Remaining + Epsilon < step;
            if (!full && TryPlan(CrewJobKind.Dig, start))
                return;
            if (Inventory.Total + Epsilon >= step && TryPlan(CrewJobKind.Fill, start))
                return;
            // Dump only a full load: a part load with nothing to do stays in the bucket for the
            // next fill, rather than being tipped on whatever ground is nearest.
            if (full && TryPlan(CrewJobKind.Dump, start))
                return;

            Job = CrewJobKind.None;
            if (UnreachableCount > 0)
                SetState(CrewUnitState.Unreachable, $"UNREACHABLE: {NearestUnreachable}" + (UnreachableCount > 1 ? $" (+{UnreachableCount - 1} more)" : ""));
            else
                SetState(CrewUnitState.Idle, _designations.Count == 0 ? "Idle: nothing designated" : "Idle: nothing it can do yet");
        }

        bool TryPlan(CrewJobKind kind, Vector2Int start)
        {
            var target = new Vector2Int(-1, -1);
            var found = _pathfinder.TryFindNearest(start.x, start.y, (standX, standZ) =>
            {
                if (!CanStandToWork(standX, standZ))
                    return false;
                var standHeight = _grid.GetSurfaceHeight(standX, standZ);
                for (var n = 0; n < 8; n++)
                {
                    var x = standX + NeighbourX[n];
                    var z = standZ + NeighbourZ[n];
                    if (!_grid.InBounds(x, z) || !IsJobCell(kind, standHeight, x, z))
                        continue;
                    target = new Vector2Int(x, z);
                    return true;
                }

                return false;
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

        bool IsJobCell(CrewJobKind kind, float standHeight, int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            if (!WithinReach(standHeight, height))
                return false;

            switch (kind)
            {
                case CrewJobKind.Dig:
                    Span<Layer> diggable = stackalloc Layer[1];
                    return _designations.GetKind(x, z) == DesignationKind.Dig && _grid.PeekRemove(x, z, Step, diggable) > 0;
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
                default:
                    return false;
            }
        }

        /// <summary>
        /// The unit never works from a cell that is itself still to be dug: it would dig the
        /// ground round itself away and strand itself on a pillar. Dig areas are worked from
        /// outside, moving in as cells are finished.
        /// </summary>
        bool CanStandToWork(int x, int z) => _designations.GetKind(x, z) != DesignationKind.Dig;

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

        float DumpAmount(float standHeight, int x, int z) =>
            Math.Min(ReachHeadroom(standHeight, _grid.GetSurfaceHeight(x, z)), WholeStepsHeld());

        float ReachHeadroom(float standHeight, float cellHeight) => standHeight + DigReachLevels * Step - cellHeight;

        float WholeStepsHeld() => (float)Math.Floor((Inventory.Total + Epsilon) / Step) * Step;

        /// <summary>
        /// Floods the reachable area and counts designations that no reachable cell can work.
        /// Runs at each job choice, so the readout can say which designations are cut off even
        /// while the unit is busy with others.
        /// </summary>
        void UpdateUnreachable(Vector2Int start)
        {
            UnreachableCount = 0;
            NearestUnreachable = "";
            if (_designations.Count == 0)
                return;

            _pathfinder.FloodReachable(start.x, start.y, _reachable);
            var width = _grid.Width;
            var nearestDistance = float.MaxValue;
            foreach (var cell in _designations.ActiveCells)
            {
                var x = cell % width;
                var z = cell / width;
                if (CanBeWorkedFromReachableCell(x, z))
                    continue;

                UnreachableCount++;
                var distance = (new Vector2(x + 0.5f, z + 0.5f) - Position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    NearestUnreachable = DescribeDesignation(x, z);
                }
            }
        }

        bool CanBeWorkedFromReachableCell(int x, int z)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            for (var n = 0; n < 8; n++)
            {
                var standX = x + NeighbourX[n];
                var standZ = z + NeighbourZ[n];
                if (!_grid.InBounds(standX, standZ) || !_reachable[standZ * _grid.Width + standX] || !CanStandToWork(standX, standZ))
                    continue;
                if (WithinReach(_grid.GetSurfaceHeight(standX, standZ), height))
                    return true;
            }

            return false;
        }

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
            if (_designations.GetKind(target.x, target.y) != DesignationKind.Dig
                || !WithinReach(standHeight, _grid.GetSurfaceHeight(target.x, target.y)))
            {
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

            if (report.CellsDug == 0)
            {
                // Bedrock: the designation can never be met. Drop it rather than loop on it.
                _designations.Clear(target.x, target.y);
                _rethink = true;
                return;
            }

            Version++;
            if (_designations.GetKind(target.x, target.y) != DesignationKind.Dig)
                _rethink = true;
        }

        void TipStep()
        {
            var target = JobTarget;
            var standHeight = _grid.GetSurfaceHeight(JobStand.x, JobStand.y);
            float amount;
            if (Job == CrewJobKind.Fill)
            {
                if (_designations.GetKind(target.x, target.y) != DesignationKind.Fill)
                {
                    _rethink = true;
                    return;
                }

                amount = FillAmount(standHeight, target.x, target.y);
                _worked[target.y * _grid.Width + target.x] = true;
            }
            else
            {
                amount = DumpAmount(standHeight, target.x, target.y);
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
            else if (x == JobTarget.x && z == JobTarget.y && Job != CrewJobKind.Dump)
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

        string DescribeJob()
        {
            switch (Job)
            {
                case CrewJobKind.Dig:
                    return $"dig ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Fill:
                    return $"fill ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Dump:
                    return $"spoil at ({JobTarget.x}, {JobTarget.y})";
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
