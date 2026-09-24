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

        /// <summary>
        /// Metres of rock face a unit may take off from its foot. Benching holds a soil neighbour
        /// within <see cref="BenchDepth"/>; rock only has to stay within this, because a face of it
        /// can be worked from below. See <see cref="CrewUnit.WithinDigReach"/>.
        /// </summary>
        public float CliffWorkDepth = 6f;

        /// <summary>
        /// Metres a soil cell may be taken below its highest dig-designated neighbour: the height
        /// of one bench.
        ///
        /// This was one climbable step, and a step is not a bench. On a half-metre grid at
        /// forty-five degrees a climb is a single step, so every cell in a marked block was held
        /// within half a metre of every other one, and a flat block came down in lock-step: its
        /// outer ring was cut once, the ring could then go no further because the ground inside it
        /// was higher, and the middle could not be reached from anywhere a unit was allowed to
        /// stand. Four robots managed twenty-four cuts on a seven by seven pit and stopped, with
        /// every one of them saying "is at its bench floor" (2026-09-22).
        ///
        /// A metre — two steps — is a bench a unit can work a face off, and it leaves the cut
        /// standing over the ground beside it by more than a climb, which is exactly the state the
        /// ramp planner is built to fix: it cuts a way down instead of nobody being able to move.
        /// </summary>
        public float BenchDepth = 1f;

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
        readonly List<List<int>> _haulersOfDigger = new List<List<int>>();
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
            Worksites.Removed += OnWorksiteRemoved;
            Worksites.Changed += OnWorksiteChanged;
        }

        /// <summary>The worksites vehicles are assigned to (2026-09-24).</summary>
        public Worksites Worksites { get; } = new Worksites();

        /// <summary>
        /// Whether a unit needs a worksite to work at all. On in the game — a vehicle with no worksite
        /// parks and waits to be assigned, as in Captain of Industry, instead of roaming the map for
        /// anything to do. Off, a unit with no worksite takes work anywhere, as the crew always did;
        /// the tests of the crew's own rules run that way.
        /// </summary>
        public bool RequireWorksite { get; set; }

        /// <summary>A worksite gone: its vehicles are unassigned, and so park.</summary>
        void OnWorksiteRemoved(int id)
        {
            foreach (var unit in _units)
            {
                if (unit == null || unit.Site != id)
                    continue;
                unit.Site = 0;
                unit.RequestRethink();
            }
        }

        /// <summary>A worksite moved or resized: its vehicles look at their work again.</summary>
        void OnWorksiteChanged(int id)
        {
            foreach (var unit in _units)
                if (unit != null && unit.Site == id)
                    unit.RequestRethink();
        }

        public DesignationMap Designations => _designations;

        /// <summary>Everything the crew has dug out of the ground, by material (slice 10).</summary>
        public MiningLedger Ledger { get; } = new MiningLedger();

        public RegionMap Regions { get; }

        /// <summary>
        /// The island's roads as built so far, for the bulldozer and the paver to find their work
        /// in; null until something hands them over. Roads are not designations — a graded or
        /// surfaced cell has no dig or fill on it — so they are not claimed through the map.
        /// </summary>
        public RoadBuilder Roads { get; set; }

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
            _haulersOfDigger.Add(new List<int>());
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

        /// <summary>
        /// The hauler this digger should load next: one of its own that can still take a load, the
        /// one already standing beside it first, then the nearest. -1 when it has none, or none of
        /// them has room.
        ///
        /// A digger used to have exactly one hauler, so a second dumper could never be given work
        /// (Ronan, 2026-09-24: dumpers *"queue on the digger"*). Standing idle, the spare parked
        /// where it stopped and the working one spent its life squeezing past it: one digger and
        /// two dumpers shifted 0.88 m3 a minute where the same site with one dumper shifted 5.79.
        /// </summary>
        public int HaulerFor(int diggerId)
        {
            var queue = QueueOf(diggerId);
            if (queue == null || queue.Count == 0)
                return -1;

            var digger = UnitOf(diggerId);
            var at = digger != null ? digger.Cell : new Vector2Int(-1, -1);
            var best = -1;
            var bestScore = float.MinValue;
            foreach (var id in queue)
            {
                var hauler = UnitOf(id);
                if (hauler == null || !hauler.CanTakeALoad)
                    continue;
                var here = hauler.Cell;
                var away = Mathf.Max(Mathf.Abs(here.x - at.x), Mathf.Abs(here.y - at.y));
                // One that has arrived and is standing waiting beats one that is still on its way,
                // however near that one has got. A digger that waited on whichever dumper was
                // closest waited on one wedged in traffic — 570 repaths, still "squeezing past a
                // unit" ten minutes later — while two more sat parked beside it with room in the
                // bed (2026-09-24). Then beside it beats near it, and near beats far.
                var score = (hauler.State == CrewUnitState.Parked ? 10000f : 0f)
                            + (away <= LoadingDistance(hauler, digger) ? 1000f : 0f) - away;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = id;
            }

            return best;
        }

        /// <summary>How many haulers are queued on this digger.</summary>
        public int HaulerCountFor(int diggerId)
        {
            var queue = QueueOf(diggerId);
            return queue == null ? 0 : queue.Count;
        }

        List<int> QueueOf(int diggerId) =>
            diggerId >= 0 && diggerId < _haulersOfDigger.Count ? _haulersOfDigger[diggerId] : null;

        /// <summary>The digger this hauler is serving, or -1.</summary>
        public int DiggerFor(int haulerId) => Lookup(_diggerOfHauler, haulerId);

        /// <summary>
        /// Whether the crew has a hauler with room that is not itself stuck for somewhere to tip.
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
        /// Whether the crew has any hauler that is not stuck for somewhere to tip, full or not.
        /// A digger with haulers in the crew waits for one rather than carrying spoil itself:
        /// hauling is what they are for, and a digger that drives off leaves the cut idle.
        /// </summary>
        public bool HasHaulers
        {
            get
            {
                foreach (var unit in _units)
                    if (unit != null && unit.Role == UnitRole.Hauler && unit.State != CrewUnitState.NeedsSomewhereToTip)
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
            return HasHaulersAt(digger.Site);
        }

        /// <summary>
        /// Whether a hauler that can take a load is assigned to worksite <paramref name="site"/> (0: to
        /// none). A digger waits for one of its own worksite's dumpers: waiting because there is a
        /// dumper at some other site would wait for ever.
        /// </summary>
        public bool HasHaulersAt(int site)
        {
            foreach (var unit in _units)
                if (unit != null && unit.Role == UnitRole.Hauler && unit.Site == site && unit.State != CrewUnitState.NeedsSomewhereToTip)
                    return true;
            return false;
        }

        /// <summary>
        /// Gives this hauler a digger to serve: one of its own worksite's that it can reach, the
        /// ones with no dumpers yet first, then the ones asking, then the fullest. -1 when there is
        /// nobody to serve.
        ///
        /// A hauler still serves one digger, but a digger takes as many haulers as are given to it
        /// and loads whichever of them is beside it. Dumpers spread across the diggers before they
        /// queue, so a second digger is always worth more than a second dumper on the same face.
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
                // A dumper serves the diggers of its own worksite and nobody else's.
                if (unit == null || unit.Role != UnitRole.Digger || unit.Site != hauler.Site)
                    continue;
                var there = unit.Cell;
                if (!Regions.CanReach(here.x, here.y, there.x, there.y))
                    continue;
                var score = unit.Inventory.Total + (_wantsHauler[unit.Id] ? 1000f : 0f)
                            - HaulerCountFor(unit.Id) * 10000f;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = unit.Id;
            }

            if (best < 0)
                return -1;
            QueueOf(best).Add(hauler.Id);
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
                QueueOf(digger)?.Remove(unitId);
            }

            // Called with a digger's id as well, which lets its whole queue go.
            var queue = QueueOf(unitId);
            if (queue != null && queue.Count > 0)
            {
                foreach (var id in queue)
                    if (Lookup(_diggerOfHauler, id) == unitId)
                        _diggerOfHauler[id] = -1;
                queue.Clear();
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

        /// <summary>
        /// Cells between a digger and a hauler when one loads the other: the two stand side by side,
        /// so their half-widths added and rounded up to whole cells. One cell for the crew robot,
        /// two for the forge machines (0.86 and 0.93 m wide on half-metre cells), which is the
        /// forge's own loading placement: the dumper 1.07 m off the digger's swing axis, the bed
        /// under the bucket. Loading used to mean the next cell over, and a truck a metre wide
        /// parked inside the digger it served (Ronan, 2026-09-24).
        /// </summary>
        public static int LoadingDistance(CrewUnit a, CrewUnit b) =>
            Math.Max(1, (int)Math.Ceiling(a.HalfWidth + b.HalfWidth - 0.05f));

        /// <summary>Whether these two are a digger and the hauler serving it.</summary>
        public bool Partnered(int unitId, int otherId) =>
            HaulerFor(unitId) == otherId || DiggerFor(unitId) == otherId;

        /// <summary>Whether another unit is standing on this cell.</summary>
        public bool IsOccupiedByOther(int x, int z, int unitId)
        {
            var owner = _occupant[z * _grid.Width + x];
            return owner >= 0 && owner != unitId;
        }

        /// <summary>
        /// Whether a unit of <paramref name="radius"/> may move from <paramref name="from"/> to
        /// <paramref name="to"/> without closing to within its own radius plus the other unit's
        /// of anybody. Cell occupancy only changes as a unit crosses an edge, so two of them can
        /// still overlap on a boundary or clip past each other on a diagonal; this is what
        /// actually keeps the bodies apart, and it is what gives the machines their room — a
        /// machine is over a metre long where a robot is half of one. Units already standing
        /// closer than that (a hauler parked at a digger) may still move, as long as they are not
        /// getting closer.
        /// </summary>
        public bool CanMoveTo(Vector2 from, Vector2 to, int unitId, float radius)
        {
            var me = UnitOf(unitId);
            foreach (var other in _units)
            {
                if (other == null || other.Id == unitId)
                    continue;
                // A digger and its own hauler come in side by side, so only their widths keep them
                // apart; anyone else keeps the full radius, which is the long way round.
                var apart = Partnered(unitId, other.Id)
                    ? (me != null ? me.HalfWidth : radius) + other.HalfWidth
                    : radius + other.Radius;
                var after = (to - other.Position).sqrMagnitude;
                if (after >= apart * apart)
                    continue;
                // Already inside the gap — a hauler nested at its digger — so holding it to "never
                // any closer" is the one thing that cannot help: getting out past a partner means
                // going round it, and going round it starts by closing the gap before it opens.
                // A full dumper sat 0.87 cells from its mech for ten game minutes, creeping a
                // hundredth of a cell and being sent back, while the mech waited for it to come
                // and be loaded (2026-09-22). Once they are that close the cells are what keep
                // them out of each other, and cells allow one unit each.
                var before = (from - other.Position).sqrMagnitude;
                if (before < apart * apart)
                    continue;
                if (after < before)
                    return false;
            }

            return true;
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
        /// on, one <see cref="BenchDepth"/> below its highest dig-designated neighbour if that is
        /// higher. Keeps a designated hill coming down in benches rather than as a cliff, so its
        /// upper cells always have somewhere within reach to be worked from.
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
                if (!_grid.InBounds(nx, nz) || _designations.GetKind(nx, nz) != DesignationKind.Dig)
                    continue;

                // Rock is worked from below: a unit at the foot of a face cuts the top off it a
                // step at a time, so a rock neighbour only has to stay within that reach rather
                // than within a climbable step. Holding it to a step is what deadlocks a cliffed
                // hill — the face cannot come down until its neighbour does, and the neighbour
                // cannot be reached until the face comes down.
                var depth = MaterialTable.IsStone(_grid.GetTopMaterial(nx, nz))
                    ? CliffWorkDepth
                    : Math.Max(climb, BenchDepth);
                floor = Math.Max(floor, _grid.GetSurfaceHeight(nx, nz) - depth);
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
            // Aim at somewhere the unit could stand to work it, not at the work. In a pit the work
            // is in the middle of a block that still stands level with the ground outside, so a
            // corridor to it crosses nothing steep and there is nothing to cut; the way in is
            // barred at the edge of what has already been cut, and that is where a unit would have
            // to stand. Where there is no such place yet, the work itself is the best guess.
            if (unit.TryFindStand(target.x, target.y, out var wants))
                target = wants;
            var found = _pathfinder.TryFindCorridor(start.x, start.y, target.x, target.y, _corridor, RampSteepPenalty,
                (x, z) => _designations.GetKind(x, z) == DesignationKind.None || Regions.CanReach(start.x, start.y, x, z));
            if (!found)
                return Blocked($"no ramp route to ({target.x}, {target.y})");
            // A corridor of one cell is the unit standing on the target: there is no step along it
            // to cut. This only showed up once a unit could ask for a ramp while it still had a
            // job, which is often enough to be standing on the thing it is asking about.
            if (_corridor.Count < 2)
                return Blocked($"already on ({target.x}, {target.y})");

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
                if (!unit.WithinReach(standHeight, highHeight) && !MaterialTable.IsStone(_grid.GetTopMaterial(high.x, high.y)))
                    return Blocked($"{highHeight - standHeight:0.#} m cliff at ({high.x}, {high.y}) is out of reach");

                _designations.Designate(high.x, high.y, DesignationKind.Dig, lowHeight + step, auto: true);
                _rampAuto = high.y * _grid.Width + high.x;
                RampNote = $"cutting ramp at ({high.x}, {high.y}) toward ({target.x}, {target.y})";
                return true;
            }

            var last = _corridor[_corridor.Count - 2];
            return Blocked(unit.WithinDigReach(_grid.GetSurfaceHeight(last.x, last.y), target.x, target.y)
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
            Worksites.Removed -= OnWorksiteRemoved;
            Worksites.Changed -= OnWorksiteChanged;
            Regions.Dispose();
        }

        Vector2Int ToCell(int cell) => cell < 0 ? new Vector2Int(-1, -1) : new Vector2Int(cell % _grid.Width, cell / _grid.Width);
    }
}
