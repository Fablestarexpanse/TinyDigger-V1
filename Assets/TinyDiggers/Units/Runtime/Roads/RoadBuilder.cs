using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Builds the roads in a <see cref="RoadNetwork"/> (Slice 17 Part B). Planning a road turns
    /// its footprint into Dig and Fill designations for the crew; once a road-bed cell's
    /// designation is met (or it needed none), the top of its ground becomes Road: the top
    /// <see cref="Thickness"/> of the column is turned into Road in place, so the height does not
    /// change. Re-planning a road after an edit designates only what changed, and takes the road
    /// off cells it no longer covers. Removing a road turns its Road back into Dirt.
    ///
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

        sealed class Plan
        {
            public readonly Dictionary<int, float> Footprint = new Dictionary<int, float>();
            public readonly HashSet<int> Bed = new HashSet<int>();
        }

        readonly TerrainGrid _grid;
        readonly DesignationMap _map;
        readonly Dictionary<int, Plan> _plans = new Dictionary<int, Plan>();
        readonly Dictionary<int, int> _bedOwners = new Dictionary<int, int>();
        readonly HashSet<int> _waiting = new HashSet<int>();
        readonly List<int> _done = new List<int>();

        public RoadBuilder(TerrainGrid grid, DesignationMap map)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _map = map ?? throw new ArgumentNullException(nameof(map));
        }

        /// <summary>Road-bed cells planned but not yet paved.</summary>
        public int Waiting => _waiting.Count;

        public bool IsPlanned(int road) => _plans.ContainsKey(road);

        /// <summary>
        /// Plans (or re-plans) a road: designates its footprint, and remembers its bed to pave.
        /// On a re-plan, cells the road no longer covers lose their designation and, unless
        /// another road still has them, their Road. Returns how many designations were made.
        /// </summary>
        public int PlanRoad(int road, IReadOnlyList<PlannedCell> footprint, IReadOnlyList<Vector2Int> bed)
        {
            var width = _grid.Width;
            var next = new Plan();
            foreach (var cell in footprint)
                next.Footprint[cell.Z * width + cell.X] = cell.Height;
            foreach (var cell in bed)
                next.Bed.Add(cell.y * width + cell.x);

            if (_plans.TryGetValue(road, out var previous))
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
        }

        /// <summary>Forgets every road without touching the ground: for a new island.</summary>
        public void Clear()
        {
            _plans.Clear();
            _bedOwners.Clear();
            _waiting.Clear();
        }

        /// <summary>Paves every waiting road-bed cell whose work is done. Returns how many it paved.</summary>
        public int Tick()
        {
            if (_waiting.Count == 0)
                return 0;
            var width = _grid.Width;
            _done.Clear();
            foreach (var cell in _waiting)
            {
                var (x, z) = (cell % width, cell / width);
                if (_map.GetKind(x, z) != DesignationKind.None)
                    continue;
                if (!IsAtPlan(cell))
                    continue;
                if (Pave(_grid, x, z))
                    _done.Add(cell);
            }

            foreach (var cell in _done)
                _waiting.Remove(cell);
            return _done.Count;
        }

        /// <summary>Whether a road-bed cell stands where some road planned it.</summary>
        bool IsAtPlan(int cell)
        {
            var surface = _grid.GetSurfaceHeight(cell % _grid.Width, cell / _grid.Width);
            foreach (var plan in _plans.Values)
                if (plan.Bed.Contains(cell) && plan.Footprint.TryGetValue(cell, out var height)
                    && surface >= height - Tolerance && surface <= height + Above + Tolerance)
                    return true;
            // A bed cell that needed no work has no footprint entry when planning skipped it.
            foreach (var plan in _plans.Values)
                if (plan.Bed.Contains(cell) && !plan.Footprint.ContainsKey(cell))
                    return true;
            return false;
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
