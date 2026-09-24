using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Builds the roads in a <see cref="RoadNetwork"/> (Slice 17 Part B). A road goes through the
    /// stages the crew will work it through (Ronan, 2026-09-24: "a bulldozer bot that smoothes the
    /// road, then another unit that lays the road"):
    /// 1. **Earthworks.** Planning a road turns its footprint into Dig and Fill designations. The
    ///    ground moves in whole height steps, so a finished stretch is a staircase.
    /// 2. **Graded.** Once a footprint cell's earthwork is met it can be graded: smoothed to the
    ///    road's true height, which is drawn (<see cref="Drawn"/>) instead of the stepped one. That
    ///    is the bulldozer's job (<see cref="Grade"/>); until it exists, <see cref="AutoGrade"/>
    ///    grades each cell as soon as its earthwork is done. The ground is still whatever the road
    ///    crosses — the rock a cut laid bare, the spoil a fill was built from.
    /// 3. **Surfaced.** A graded road-bed cell has its top <see cref="Thickness"/> turned into Road
    ///    in place (<see cref="LaySurface"/>), so the height does not change. That is the paver's
    ///    job, and only a surfaced cell is quicker to drive on. Nothing does it on its own unless
    ///    <see cref="AutoSurface"/> is set.
    ///
    /// Re-planning a road after an edit designates only what changed, un-grades what moved, and
    /// takes the road off cells it no longer covers. Removing a road turns its Road back into Dirt.
    /// A cell can be under two roads where they meet; it stays Road while either does.
    /// Plain C#: <see cref="Tick"/> is called once a frame.
    /// </summary>
    public sealed class RoadBuilder
    {
        /// <summary>Metres of the top of a road-bed cell that become Road.</summary>
        public const float Thickness = 0.25f;

        /// <summary>Metres a paved cell may sit from its planned height and still count as built.</summary>
        const float Tolerance = 0.05f;

        /// <summary>
        /// A fill is met at or above its height, and the crew heaps a little over so slump can
        /// carry it on: a cell up to one height step over its plan counts as built.
        /// </summary>
        float Above => _grid.HeightStep > 0f ? _grid.HeightStep : 0.5f;

        /// <summary>
        /// Grades every footprint cell as soon as its earthwork is done: the stand-in for the
        /// bulldozer. Turn it off once a unit calls <see cref="Grade"/> itself.
        /// </summary>
        public bool AutoGrade = true;

        /// <summary>
        /// Surfaces every graded road-bed cell straight away: the stand-in for a paver. Off by
        /// default — until a unit lays the final layer, a road stays the ground it crosses.
        /// </summary>
        public bool AutoSurface;

        /// <summary>
        /// Raised with a cell's index (z × width + x) whenever the height it is drawn at changes
        /// without the ground itself changing — it was graded, or stopped being — so the terrain
        /// renderer knows to redraw it.
        /// </summary>
        public event Action<int> DrawnChanged;

        sealed class Plan
        {
            public readonly Dictionary<int, float> Footprint = new Dictionary<int, float>();
            public readonly Dictionary<int, float> Exact = new Dictionary<int, float>();
            public readonly HashSet<int> Bed = new HashSet<int>();

            public float ExactAt(int cell, float planned) => Exact.TryGetValue(cell, out var exact) ? exact : planned;
        }

        readonly TerrainGrid _grid;
        readonly DesignationMap _map;
        readonly Dictionary<int, Plan> _plans = new Dictionary<int, Plan>();
        readonly Dictionary<int, int> _bedOwners = new Dictionary<int, int>();

        /// <summary>Road-bed cells not yet surfaced.</summary>
        readonly HashSet<int> _waiting = new HashSet<int>();

        /// <summary>Footprint and bed cells not yet graded.</summary>
        readonly HashSet<int> _ungraded = new HashSet<int>();

        /// <summary>Graded cells: the stepped height the ground was built to, and the height to draw it at.</summary>
        readonly Dictionary<int, (float Planned, float Exact)> _graded = new Dictionary<int, (float, float)>();

        readonly List<int> _done = new List<int>();

        public RoadBuilder(TerrainGrid grid, DesignationMap map)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _map = map ?? throw new ArgumentNullException(nameof(map));
        }

        /// <summary>Road-bed cells planned but not yet surfaced.</summary>
        public int Waiting => _waiting.Count;

        /// <summary>Footprint cells planned but not yet graded.</summary>
        public int Ungraded => _ungraded.Count;

        public bool IsPlanned(int road) => _plans.ContainsKey(road);

        public bool IsGraded(int x, int z) => _graded.ContainsKey(z * _grid.Width + x);

        /// <summary>Whether a cell is waiting for the bulldozer: its earthwork is done and it is not yet graded.</summary>
        public bool NeedsGrading(int x, int z)
        {
            var cell = z * _grid.Width + x;
            return _ungraded.Contains(cell) && _map.GetKind(x, z) == DesignationKind.None && PlanAt(cell, out _, out _);
        }

        /// <summary>Whether a road-bed cell is waiting for the paver: graded and not yet surfaced.</summary>
        public bool NeedsSurface(int x, int z)
        {
            var cell = z * _grid.Width + x;
            return _waiting.Contains(cell) && _graded.ContainsKey(cell);
        }

        /// <summary>
        /// Plans (or re-plans) a road: designates its footprint, and remembers its bed to grade and
        /// surface. <paramref name="exact"/> is each cell's height before it was rounded to the height
        /// step (see <see cref="RoadPlanner.Footprint"/>): what a graded cell is drawn at. On a
        /// re-plan, cells the road no longer covers lose their designation and, unless another road
        /// still has them, their Road; cells whose height changed have to be graded again. Returns
        /// how many designations were made.
        /// </summary>
        public int PlanRoad(int road, IReadOnlyList<PlannedCell> footprint, IReadOnlyList<Vector2Int> bed,
            IReadOnlyDictionary<int, float> exact = null)
        {
            var width = _grid.Width;
            var next = new Plan();
            foreach (var cell in footprint)
            {
                var index = cell.Z * width + cell.X;
                next.Footprint[index] = cell.Height;
                if (exact != null && exact.TryGetValue(index, out var height))
                    next.Exact[index] = height;
            }
            foreach (var cell in bed)
                next.Bed.Add(cell.y * width + cell.x);

            _plans.TryGetValue(road, out var previous);
            if (previous != null)
            {
                foreach (var pair in previous.Footprint)
                {
                    if (next.Footprint.ContainsKey(pair.Key))
                        continue;
                    var (x, z) = (pair.Key % width, pair.Key / width);
                    if (_map.GetKind(x, z) != DesignationKind.None && Math.Abs(_map.GetTarget(x, z) - pair.Value) < 1e-4f)
                        _map.CancelDesignation(x, z);
                }

                foreach (var cell in previous.Bed)
                    if (!next.Bed.Contains(cell))
                        Release(cell);
            }

            _plans[road] = next;
            var made = 0;
            foreach (var cell in footprint)
            {
                var kind = cell.IsDig ? DesignationKind.Dig : cell.IsFill ? DesignationKind.Fill : DesignationKind.None;
                if (kind != DesignationKind.None && _map.Designate(cell.X, cell.Z, kind, cell.Height))
                    made++;
            }

            foreach (var cell in next.Bed)
            {
                if (previous != null && previous.Bed.Contains(cell))
                    continue;
                _bedOwners.TryGetValue(cell, out var owners);
                _bedOwners[cell] = owners + 1;
                if (_grid.GetTopMaterial(cell % width, cell / width) != MaterialTable.Road)
                    _waiting.Add(cell);
            }

            // Grading: what the road left, and what it now wants at another height, are graded
            // afresh; what it kept exactly as it was stays graded.
            if (previous != null)
            {
                foreach (var cell in Cells(previous))
                    if (!next.Footprint.ContainsKey(cell) && !next.Bed.Contains(cell))
                        Regrade(cell);
            }

            foreach (var cell in Cells(next))
            {
                if (previous != null && _graded.TryGetValue(cell, out var graded)
                    && next.Footprint.TryGetValue(cell, out var planned) && Math.Abs(planned - graded.Planned) < 1e-4f
                    && Math.Abs(next.ExactAt(cell, planned) - graded.Exact) < 1e-4f)
                    continue;
                Regrade(cell);
            }

            return made;
        }

        /// <summary>Takes a road away: its designations are cancelled and its Road turned back to Dirt.</summary>
        public void RemoveRoad(int road)
        {
            if (!_plans.TryGetValue(road, out var plan))
                return;
            var width = _grid.Width;
            foreach (var pair in plan.Footprint)
            {
                var (x, z) = (pair.Key % width, pair.Key / width);
                if (_map.GetKind(x, z) != DesignationKind.None && Math.Abs(_map.GetTarget(x, z) - pair.Value) < 1e-4f)
                    _map.CancelDesignation(x, z);
            }

            foreach (var cell in plan.Bed)
                Release(cell);
            _plans.Remove(road);
            foreach (var cell in Cells(plan))
                Regrade(cell);
        }

        /// <summary>Forgets every road without touching the ground: for a new island.</summary>
        public void Clear()
        {
            _plans.Clear();
            _bedOwners.Clear();
            _waiting.Clear();
            _ungraded.Clear();
            var drawn = new List<int>(_graded.Keys);
            _graded.Clear();
            foreach (var cell in drawn)
                DrawnChanged?.Invoke(cell);
        }

        /// <summary>
        /// Grades every cell whose earthwork is done (with <see cref="AutoGrade"/>), then surfaces
        /// every graded road-bed cell (with <see cref="AutoSurface"/>). Returns how many cells were
        /// surfaced.
        /// </summary>
        public int Tick()
        {
            var width = _grid.Width;
            if (AutoGrade && _ungraded.Count > 0)
            {
                _done.Clear();
                foreach (var cell in _ungraded)
                    if (TryGrade(cell))
                        _done.Add(cell);
                foreach (var cell in _done)
                    _ungraded.Remove(cell);
            }

            if (!AutoSurface || _waiting.Count == 0)
                return 0;
            _done.Clear();
            foreach (var cell in _waiting)
            {
                var (x, z) = (cell % width, cell / width);
                if (CanSurface(cell) && Pave(_grid, x, z))
                    _done.Add(cell);
            }

            foreach (var cell in _done)
                _waiting.Remove(cell);
            return _done.Count;
        }

        /// <summary>
        /// Grades a cell whose earthwork is done: from now on it is drawn at the road's true height
        /// rather than the stepped one. What a bulldozer does. False if the cell is not waiting to
        /// be graded or its earthwork is not finished.
        /// </summary>
        public bool Grade(int x, int z)
        {
            var cell = z * _grid.Width + x;
            if (!_ungraded.Contains(cell) || !TryGrade(cell))
                return false;
            _ungraded.Remove(cell);
            return true;
        }

        /// <summary>
        /// Lays the final layer on a graded road-bed cell: its top becomes Road, quicker to drive.
        /// What a paver does. False if the cell is not a graded bed cell waiting for it.
        /// </summary>
        public bool LaySurface(int x, int z)
        {
            var cell = z * _grid.Width + x;
            if (!_waiting.Contains(cell) || !CanSurface(cell) || !Pave(_grid, x, z))
                return false;
            _waiting.Remove(cell);
            return true;
        }

        /// <summary>
        /// The height to draw cell <paramref name="cell"/> at, given the height it is: a graded
        /// road cell still standing where it was built is drawn at the road's true height, so a
        /// graded road reads as one smooth grade instead of the height steps the ground moves in.
        /// Anything else — and a graded cell that has since been dug into or tipped on — is drawn
        /// where it is.
        /// </summary>
        public float Drawn(int cell, float height) =>
            _graded.TryGetValue(cell, out var graded) && Math.Abs(height - graded.Planned) <= Tolerance
                ? graded.Exact
                : height;

        bool CanSurface(int cell) =>
            _graded.ContainsKey(cell) && _map.GetKind(cell % _grid.Width, cell / _grid.Width) == DesignationKind.None
            && IsAtPlan(cell);

        /// <summary>Grades a cell if its earthwork is done, remembering the heights it is drawn from.</summary>
        bool TryGrade(int cell)
        {
            var (x, z) = (cell % _grid.Width, cell / _grid.Width);
            if (_map.GetKind(x, z) != DesignationKind.None || !PlanAt(cell, out var planned, out var exact))
                return false;
            _graded[cell] = (planned, exact);
            DrawnChanged?.Invoke(cell);
            return true;
        }

        /// <summary>
        /// The plan a cell stands at, if any: the height the ground was built to and the height to
        /// draw it at. A bed cell that needed no work, and was planned with no footprint entry, is at
        /// whatever height it is.
        /// </summary>
        bool PlanAt(int cell, out float planned, out float exact)
        {
            var surface = _grid.GetSurfaceHeight(cell % _grid.Width, cell / _grid.Width);
            foreach (var plan in _plans.Values)
            {
                if (!plan.Footprint.TryGetValue(cell, out var height)
                    || surface < height - Tolerance || surface > height + Above + Tolerance)
                    continue;
                planned = height;
                exact = plan.ExactAt(cell, height);
                return true;
            }

            foreach (var plan in _plans.Values)
            {
                if (!plan.Bed.Contains(cell) || plan.Footprint.ContainsKey(cell))
                    continue;
                planned = exact = surface;
                return true;
            }

            planned = exact = 0f;
            return false;
        }

        /// <summary>Whether a road-bed cell stands where some road planned it.</summary>
        bool IsAtPlan(int cell) => PlanAt(cell, out _, out _);

        /// <summary>
        /// Drops a cell's grading and, if a road still covers it, queues it to be graded again.
        /// </summary>
        void Regrade(int cell)
        {
            if (_graded.Remove(cell))
                DrawnChanged?.Invoke(cell);
            _ungraded.Remove(cell);
            foreach (var plan in _plans.Values)
            {
                if (!plan.Footprint.ContainsKey(cell) && !plan.Bed.Contains(cell))
                    continue;
                _ungraded.Add(cell);
                return;
            }
        }

        static IEnumerable<int> Cells(Plan plan)
        {
            foreach (var cell in plan.Footprint.Keys)
                yield return cell;
            foreach (var cell in plan.Bed)
                if (!plan.Footprint.ContainsKey(cell))
                    yield return cell;
        }

        void Release(int cell)
        {
            if (!_bedOwners.TryGetValue(cell, out var owners))
                return;
            owners--;
            if (owners > 0)
            {
                _bedOwners[cell] = owners;
                return;
            }

            _bedOwners.Remove(cell);
            _waiting.Remove(cell);
            Unpave(_grid, cell % _grid.Width, cell / _grid.Width);
        }

        /// <summary>
        /// Turns the top <see cref="Thickness"/> of a column into Road, keeping its height: the
        /// layers it covers are shortened or dropped. Returns false if the cell is off the map, in
        /// the void or has too little ground.
        /// </summary>
        public static bool Pave(TerrainGrid grid, int x, int z)
        {
            if (!grid.IsGround(x, z) || grid.GetTopMaterial(x, z) == MaterialTable.Road)
                return false;
            Span<Layer> layers = stackalloc Layer[TerrainGrid.MaxLayersPerCell];
            // CopyLayers gives the top first; SetColumn takes the bottom first.
            var count = grid.CopyLayers(x, z, layers);
            layers.Slice(0, count).Reverse();
            var total = 0f;
            for (var i = 0; i < count; i++)
                total += layers[i].Thickness;
            if (total <= Thickness)
                return false;

            var remove = Thickness;
            while (count > 0 && remove > 1e-5f)
            {
                var top = layers[count - 1];
                if (top.Thickness > remove + 1e-5f)
                {
                    layers[count - 1] = new Layer(top.Material, top.Thickness - remove);
                    remove = 0f;
                }
                else
                {
                    remove -= top.Thickness;
                    count--;
                }
            }

            if (count >= TerrainGrid.MaxLayersPerCell)
                count = TerrainGrid.MaxLayersPerCell - 1;
            layers[count++] = new Layer(MaterialTable.Road, Thickness);
            grid.SetColumn(x, z, layers.Slice(0, count));
            return true;
        }

        /// <summary>Turns a cell's Road back into Dirt, keeping its height. Returns whether there was any.</summary>
        public static bool Unpave(TerrainGrid grid, int x, int z)
        {
            if (!grid.IsGround(x, z))
                return false;
            Span<Layer> layers = stackalloc Layer[TerrainGrid.MaxLayersPerCell];
            var count = grid.CopyLayers(x, z, layers);
            layers.Slice(0, count).Reverse();
            var changed = false;
            for (var i = 0; i < count; i++)
            {
                if (layers[i].Material != MaterialTable.Road)
                    continue;
                layers[i] = new Layer(MaterialTable.Dirt, layers[i].Thickness);
                changed = true;
            }

            if (changed)
                grid.SetColumn(x, z, layers.Slice(0, count));
            return changed;
        }
    }
}
