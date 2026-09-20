using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// The crew's shared brain. It owns the designations and everything that has to be decided
    /// once for the whole crew rather than per unit:
    /// - **Claims.** A designation has at most one worker. A unit claims the cell it is going to
    ///   work and releases it when it changes job, finishes or is removed.
    /// - **Reachability.** One <see cref="RegionMap"/> for everyone, so "can this unit get there"
    ///   is a label comparison and a terrain change costs one region rebuild, not one per unit.
    /// - **Benching.** <see cref="DigFloor"/>, the lowest a dig cell may be taken now, so that
    ///   units never bench a hill into a shape another unit cannot work.
    /// - **Auto ramps.** One ramp at a time for the whole crew, so two units cannot plan
    ///   competing ramps to the same place. Its Auto step is an ordinary designation, so whichever
    ///   unit is nearest cuts it.
    /// - **Traffic.** Which cell each unit stands on and which cells its path uses, so units do
    ///   not walk into each other or tip spoil where another one is going.
    /// </summary>
    public sealed class JobDispatcher : IDisposable
    {
        const float Epsilon = 1e-3f;
        static readonly int[] NeighbourX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] NeighbourZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>Take designated ground down in drivable layers (<see cref="DigFloor"/>).</summary>
        public bool Benching = true;

        /// <summary>Cut ramps to designations the crew cannot otherwise reach.</summary>
        public bool AutoRamp = true;

        /// <summary>Seconds a target is left alone after the player cancels a ramp step toward it.</summary>
        public float RampCancelSeconds = 10f;

        /// <summary>Seconds a target is left alone after no ramp to it could be planned.</summary>
        public float RampBlockedSeconds = 10f;

        /// <summary>Extra corridor cost per metre a step is over the climb limit.</summary>
        public float RampSteepPenalty = 20f;

        /// <summary>Most unreachable targets tried for a ramp per request.</summary>
        public int RampAttempts = 4;

        readonly TerrainGrid _grid;
        readonly DesignationMap _designations;
        readonly GridPathfinder _pathfinder;
        readonly List<CrewUnit> _units = new List<CrewUnit>();
        readonly List<Vector2Int> _corridor = new List<Vector2Int>();
        readonly Dictionary<int, float> _rampSuppressedUntil = new Dictionary<int, float>();
        readonly int[] _claimedBy;
        readonly int[] _occupant;
        readonly int[] _pathUsers;
        readonly List<int> _claimOf = new List<int>();
        readonly List<int> _haulerOfDigger = new List<int>();
        readonly List<int> _diggerOfHauler = new List<int>();
        readonly List<bool> _wantsHauler = new List<bool>();
        readonly List<int> _cellOf = new List<int>();
        readonly List<List<int>> _pathOf = new List<List<int>>();
        float _clock;
        int _rampTarget = -1;
        int _rampAuto = -1;
        bool _disposed;

        public JobDispatcher(TerrainGrid grid, DesignationMap designations, GridPathfinder pathfinder)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _designations = designations ?? throw new ArgumentNullException(nameof(designations));
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            var cells = grid.Width * grid.Height;
            _claimedBy = new int[cells];
            _occupant = new int[cells];
            _pathUsers = new int[cells];
            for (var i = 0; i < cells; i++)
            {
                _claimedBy[i] = -1;
                _occupant[i] = -1;
            }

            Regions = new RegionMap(grid, pathfinder);
            _designations.AutoCancelled += OnAutoCancelled;
        }

        public DesignationMap Designations => _designations;

        public RegionMap Regions { get; }

        public TerrainGrid Grid => _grid;

        public GridPathfinder Pathfinder => _pathfinder;

        public IReadOnlyList<CrewUnit> Units => _units;

        /// <summary>Seconds of world time the dispatcher has seen, for ramp timeouts.</summary>
        public float Clock => _clock;

        /// <summary>Whether a ramp is being cut toward a dig the crew cannot otherwise reach.</summary>
        public bool HasRamp => _rampTarget >= 0;

        public Vector2Int RampTarget => ToCell(_rampTarget);

        /// <summary>The Auto step being cut, or (-1, -1).</summary>
        public Vector2Int RampStep => ToCell(_rampAuto);

        /// <summary>What the ramp planner last decided, for the readout.</summary>
        public string RampNote { get; private set; } = "";

        /// <summary>The last corridor it planned, unit side first. For tests and debug drawing.</summary>
        public IReadOnlyList<Vector2Int> RampCorridor => _corridor;

        /// <summary>The biggest whole number of height steps a unit can drive up or down, at least one.</summary>
        public float Climb
        {
            get
            {
                var step = Step;
                return Math.Max(1f, (float)Math.Floor((_pathfinder.MaxStepHeight + Epsilon) / step)) * step;
            }
        }

        float Step => _grid.HeightStep > 0f ? _grid.HeightStep : 1f;

        /// <summary>Advances the clock and retires a ramp that is no longer needed. Call once per frame.</summary>
        public void Tick(float deltaTime)
        {
            _clock += deltaTime;
            _designations.Prune();
            UpdateRamp();
        }

        public int Register(CrewUnit unit)
        {
            var id = _units.Count;
            _units.Add(unit);
            _claimOf.Add(-1);
            _haulerOfDigger.Add(-1);
            _diggerOfHauler.Add(-1);
            _wantsHauler.Add(false);
            _cellOf.Add(-1);
            _pathOf.Add(new List<int>());
            return id;
        }

        /// <summary>Takes a unit off the crew: its claim, its cell and its path are freed.</summary>
        public void Unregister(CrewUnit unit)
        {
            var id = unit.Id;
            if (id < 0 || id >= _units.Count || _units[id] != unit)
                return;
            Release(id);
            ReleaseHauler(id);

            SetPath(id, null, 0);
            if (_cellOf[id] >= 0)
            {
                if (_occupant[_cellOf[id]] == id)
                    _occupant[_cellOf[id]] = -1;
                _cellOf[id] = -1;
            }

            _units[id] = null;
        }

        // --- haulers ------------------------------------------------------------------------------

        /// <summary>The hauler serving this digger, or -1.</summary>
        public int HaulerFor(int diggerId) => Lookup(_haulerOfDigger, diggerId);

        /// <summary>The digger this hauler is serving, or -1.</summary>
        public int DiggerFor(int haulerId) => Lookup(_diggerOfHauler, haulerId);

        /// <summary>
        /// Whether the crew has a hauler with room that is not itself stuck for somewhere to tip,
        /// so a full digger knows whether waiting is worth it.
        /// </summary>
        public bool HasUsableHauler
        {
            get
            {
                foreach (var unit in _units)
                    if (unit != null && unit.CanTakeALoad)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// A digger with a full scoop asks for a hauler. It is only worth waiting if the crew has
        /// one; the hauler picks its digger itself, fullest load first.
        /// </summary>
        public bool RequestHauler(CrewUnit digger)
        {
            if (digger.Id < _wantsHauler.Count)
                _wantsHauler[digger.Id] = true;
            return HasUsableHauler;
        }

        /// <summary>
        /// Gives this hauler a digger to serve: the one it can reach with the fullest load that
        /// has no hauler yet, diggers asking for one first. -1 when there is nobody to serve.
        /// One hauler per digger, one digger per hauler.
        /// </summary>
        public int AssignDigger(CrewUnit hauler)
        {
            var already = DiggerFor(hauler.Id);
            if (already >= 0)
                return already;

            var here = hauler.Cell;
            var best = -1;
            var bestScore = float.MinValue;
            foreach (var unit in _units)
            {
                if (unit == null || unit.Role != UnitRole.Digger || HaulerFor(unit.Id) >= 0)
                    continue;
                var there = unit.Cell;
                if (!Regions.CanReach(here.x, here.y, there.x, there.y))
                    continue;
                var score = unit.Inventory.Total + (_wantsHauler[unit.Id] ? 1000f : 0f);
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = unit.Id;
            }

            if (best < 0)
                return -1;
            _haulerOfDigger[best] = hauler.Id;
            _diggerOfHauler[hauler.Id] = best;
            _wantsHauler[best] = false;
            return best;
        }

        /// <summary>Breaks the link between a hauler and its digger, from either side.</summary>
        public void ReleaseHauler(int unitId)
        {
            var digger = Lookup(_diggerOfHauler, unitId);
            if (digger >= 0)
            {
                _diggerOfHauler[unitId] = -1;
                if (Lookup(_haulerOfDigger, digger) == unitId)
                    _haulerOfDigger[digger] = -1;
            }

            var hauler = Lookup(_haulerOfDigger, unitId);
            if (hauler >= 0)
            {
                _haulerOfDigger[unitId] = -1;
                if (Lookup(_diggerOfHauler, hauler) == unitId)
                    _diggerOfHauler[hauler] = -1;
            }
        }

        /// <summary>The crew member with this id, or null if there is none (or it has gone).</summary>
        public CrewUnit UnitOf(int id) => id >= 0 && id < _units.Count ? _units[id] : null;

        /// <summary>The unit standing on this cell, or null.</summary>
        public CrewUnit UnitOn(int x, int z)
        {
            var id = _occupant[z * _grid.Width + x];
            return id >= 0 && id < _units.Count ? _units[id] : null;
        }

        static int Lookup(List<int> links, int id) => id >= 0 && id < links.Count ? links[id] : -1;

        // --- claims -------------------------------------------------------------------------------


        /// <summary>Whether another unit has already taken this cell as its job.</summary>
        public bool IsClaimedByOther(int x, int z, int unitId)
        {
            var owner = _claimedBy[z * _grid.Width + x];
            return owner >= 0 && owner != unitId;
        }

        /// <summary>Gives the cell to this unit, releasing whatever it held before. One worker per cell.</summary>
        public bool Claim(int x, int z, int unitId)
        {
            var cell = z * _grid.Width + x;
            if (IsClaimedByOther(x, z, unitId))
                return false;
            Release(unitId);
            _claimedBy[cell] = unitId;
            _claimOf[unitId] = cell;
            return true;
        }

        /// <summary>Frees whatever this unit had claimed.</summary>
        public void Release(int unitId)
        {
            if (unitId < 0 || unitId >= _claimOf.Count)
                return;
            var cell = _claimOf[unitId];
            if (cell < 0)
                return;
            if (_claimedBy[cell] == unitId)
                _claimedBy[cell] = -1;
            _claimOf[unitId] = -1;
        }

        /// <summary>The cell this unit has claimed, or (-1, -1).</summary>
        public Vector2Int ClaimOf(int unitId) => ToCell(unitId >= 0 && unitId < _claimOf.Count ? _claimOf[unitId] : -1);

        // --- traffic ------------------------------------------------------------------------------

        /// <summary>Records where a unit is standing. Units never share a cell.</summary>
        public void SetCell(int unitId, int x, int z)
        {
            var cell = z * _grid.Width + x;
            var previous = _cellOf[unitId];
            if (previous == cell)
                return;
            if (previous >= 0 && _occupant[previous] == unitId)
                _occupant[previous] = -1;
            _cellOf[unitId] = cell;
            _occupant[cell] = unitId;
        }

        public bool IsOccupiedByOther(int x, int z, int unitId)
        {
            var owner = _occupant[z * _grid.Width + x];
            return owner >= 0 && owner != unitId;
        }

        /// <summary>The unit standing on the cell, or -1.</summary>
        public int OccupantOf(int x, int z) => _occupant[z * _grid.Width + x];

        /// <summary>Records the cells of a unit's path still ahead of it, for the tip rule.</summary>
        public void SetPath(int unitId, IReadOnlyList<Vector2Int> path, int fromIndex)
        {
            var mine = _pathOf[unitId];
            foreach (var cell in mine)
                _pathUsers[cell]--;
            mine.Clear();
            if (path == null)
                return;
            for (var i = fromIndex; i < path.Count; i++)
            {
                var cell = path[i].y * _grid.Width + path[i].x;
                mine.Add(cell);
                _pathUsers[cell]++;
            }
        }

        /// <summary>Whether a cell is on some other unit's path.</summary>
        public bool IsOnAnotherPath(int x, int z, int unitId)
        {
            var cell = z * _grid.Width + x;
            if (_pathUsers[cell] == 0)
                return false;
            var mine = _pathOf[unitId];
            var usedByMe = 0;
            foreach (var used in mine)
                if (used == cell)
                    usedByMe++;
            return _pathUsers[cell] > usedByMe;
        }

        // --- benching -----------------------------------------------------------------------------

        /// <summary>
        /// The lowest a dig cell may be taken right now: its target, or with <see cref="Benching"/>
        /// on, one climbable step below its highest dig-designated neighbour if that is higher.
        /// Keeps a designated hill a drivable staircase while it comes down, so its upper cells
        /// always have somewhere within reach to be worked from.
        /// </summary>
        public float DigFloor(int x, int z)
        {
            var floor = _designations.GetTarget(x, z);
            if (!Benching)
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

        // --- auto ramps ---------------------------------------------------------------------------

        /// <summary>Whether a ramp toward (x, z) is on hold: the player cancelled one, or none could be planned.</summary>
        public bool IsRampSuppressed(int x, int z) =>
            _rampSuppressedUntil.TryGetValue(z * _grid.Width + x, out var until) && _clock < until;

        /// <summary>
        /// Called by a unit that has nothing it can reach. Starts or advances the crew's one ramp
        /// toward the nearest unreachable dig it names, and returns whether there is an Auto step
        /// to work. The step is an ordinary designation, so any unit may take it.
        /// </summary>
        public bool RequestRamp(CrewUnit unit, IReadOnlyList<int> unreachableDigs)
        {
            if (!AutoRamp)
                return false;
            if (_rampAuto >= 0 && _designations.IsAuto(_rampAuto % _grid.Width, _rampAuto / _grid.Width))
                return true;

            var attempts = 0;
            while (attempts < RampAttempts)
            {
                var best = -1;
                var bestDistance = float.MaxValue;
                foreach (var cell in unreachableDigs)
                {
                    if (IsRampSuppressed(cell % _grid.Width, cell / _grid.Width))
                        continue;
                    var distance = (new Vector2(cell % _grid.Width + 0.5f, cell / _grid.Width + 0.5f) - unit.Position).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = cell;
                    }
                }

                if (best < 0)
                    return false;
                attempts++;
                if (PlaceRampStep(best, unit))
                {
                    _rampTarget = best;
                    return true;
                }

                _rampSuppressedUntil[best] = _clock + RampBlockedSeconds;
                if (best == _rampTarget)
                    EndRamp(0f, RampNote);
            }

            return false;
        }

        /// <summary>Drops a ramp whose target is met, gone or reachable again, removing any Auto step left.</summary>
        void UpdateRamp()
        {
            if (_rampTarget < 0)
                return;

            var target = ToCell(_rampTarget);
            if (!AutoRamp || _designations.GetKind(target.x, target.y) != DesignationKind.Dig)
            {
                EndRamp(0f, "");
                return;
            }

            foreach (var unit in _units)
            {
                if (unit == null || !unit.CanWorkFromSomewhereReachable(target.x, target.y))
                    continue;
                EndRamp(0f, "");
                return;
            }
        }

        /// <summary>
        /// Plans a corridor from the unit to the target and designates an Auto dig on the higher
        /// side of the first step along it that is too steep to drive: down to one step above the
        /// lower side, worked from the cell before it. False, with <see cref="RampNote"/> saying
        /// why, if there is no such step it can fix.
        /// </summary>
        bool PlaceRampStep(int targetCell, CrewUnit unit)
        {
            var target = ToCell(targetCell);
            var start = unit.Cell;
            var found = _pathfinder.TryFindCorridor(start.x, start.y, target.x, target.y, _corridor, RampSteepPenalty,
                (x, z) => _designations.GetKind(x, z) == DesignationKind.None || Regions.CanReach(start.x, start.y, x, z));
            if (!found)
                return Blocked($"no ramp route to ({target.x}, {target.y})");

            var climb = Climb;
            var step = Step;
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
                if (!unit.WithinReach(standHeight, highHeight))
                    return Blocked($"{highHeight - standHeight:0.#} m cliff at ({high.x}, {high.y}) is out of reach");

                _designations.Designate(high.x, high.y, DesignationKind.Dig, lowHeight + step, auto: true);
                _rampAuto = high.y * _grid.Width + high.x;
                RampNote = $"cutting ramp at ({high.x}, {high.y}) toward ({target.x}, {target.y})";
                return true;
            }

            var last = _corridor[_corridor.Count - 2];
            return Blocked(unit.WithinReach(_grid.GetSurfaceHeight(last.x, last.y), _grid.GetSurfaceHeight(target.x, target.y))
                ? $"nowhere to stand beside ({target.x}, {target.y})"
                : $"({target.x}, {target.y}) is out of reach of every way up");
        }

        bool Blocked(string why)
        {
            RampNote = "ramp blocked: " + why;
            return false;
        }

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
            foreach (var unit in _units)
                unit?.RequestRethink();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _designations.AutoCancelled -= OnAutoCancelled;
            Regions.Dispose();
        }

        Vector2Int ToCell(int cell) => cell < 0 ? new Vector2Int(-1, -1) : new Vector2Int(cell % _grid.Width, cell / _grid.Width);
    }
}
