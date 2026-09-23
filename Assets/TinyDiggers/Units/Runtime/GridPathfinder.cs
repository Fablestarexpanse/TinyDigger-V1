using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// A* over the terrain's cells, 8-connected. Plain C#.
    ///
    /// Passability: a step between adjacent cells is blocked if their surface heights differ by
    /// more than <see cref="MaxStepHeight"/>. A diagonal step also needs both cells it cuts past
    /// to be steppable from the start and onto the end, so a unit cannot squeeze diagonally
    /// between two terrace edges.
    ///
    /// Cost of a step: distance x (1 + |height change| x <see cref="SlopeCostFactor"/>), so flat
    /// detours win over climbs of similar length. The octile distance is the heuristic, which
    /// never overestimates because no step costs less than its distance.
    ///
    /// Search buffers are sized to the grid once and reset lazily with a generation stamp, so a
    /// search allocates nothing and costs only the cells it touches.
    /// </summary>
    public sealed class GridPathfinder
    {
        const float Tolerance = 1e-3f;
        static readonly float Diagonal = (float)Math.Sqrt(2.0);
        static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] StepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>
        /// Largest surface-height difference a single step can climb or drop, in metres. Defaults to
        /// one cell's width: a 45-degree step.
        /// </summary>
        public float MaxStepHeight;

        /// <summary>
        /// The steepest ground anything drives, as rise over run — the same for every unit,
        /// because it is the ground that decides and not the machine (Ronan, 2026-09-22). It is
        /// <see cref="MaxStepHeight"/> said in a way that does not depend on how big a cell is:
        /// half a metre of rise across a half-metre cell is forty-five degrees whatever the map's
        /// resolution. Steeper than this wants a ramp or a road cut into it.
        /// </summary>
        public float MaxSlope
        {
            get => _grid.CellSize > 0f ? MaxStepHeight / _grid.CellSize : MaxStepHeight;
            set => MaxStepHeight = value * (_grid.CellSize > 0f ? _grid.CellSize : 1f);
        }

        /// <summary>The same limit as an angle from the horizontal, in degrees.</summary>
        public float MaxSlopeDegrees
        {
            get => (float)(Math.Atan(MaxSlope) * 180.0 / Math.PI);
            set => MaxSlope = (float)Math.Tan(value * Math.PI / 180.0);
        }

        /// <summary>Extra cost per metre of height change, as a fraction of the step's length.</summary>
        public float SlopeCostFactor = 1f;

        /// <summary>
        /// Share of the normal cost of a step from one Road cell to another (Slice 17 Part B), with
        /// no charge for the slope: that is what a road is for, and why units prefer one.
        /// </summary>
        public float RoadCost = 0.7f;

        /// <summary>
        /// How hard the heuristic pulls toward the goal, as a multiple of the admissible estimate.
        ///
        /// The estimate is scaled down by <see cref="RoadCost"/> so it can never overestimate even
        /// if the whole way were road, which keeps the search exact — and makes it search as though
        /// every cell might be road, when roads are a rope across a continent. Led by seven tenths
        /// of the real distance, a corner-to-corner path on open ground expanded **602,387 cells
        /// and took half a second** at 1024 square, and 2.46 million and 2.3 s at 2048
        /// (2026-09-23). One of those in a frame is the lag.
        ///
        /// Left at 1 the search stays exact. It is here for tuning, not for routine use: anything
        /// over about 1.15 stops a unit going out of its way onto a road, which is measurably what
        /// roads are for.
        /// </summary>
        public float HeuristicWeight = 1f;

        readonly TerrainGrid _grid;
        readonly float[] _cost;
        readonly int[] _parent;
        readonly int[] _seen;
        readonly int[] _closed;
        readonly int[] _floodCells;
        int _generation;

        // Binary min-heap of (key, cell); stale entries are skipped when popped.
        int[] _heapCell = new int[1024];
        float[] _heapKey = new float[1024];
        int _heapCount;

        public GridPathfinder(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            var cells = grid.Width * grid.Height;
            _cost = new float[cells];
            _parent = new int[cells];
            _seen = new int[cells];
            _closed = new int[cells];
            _floodCells = new int[cells];
            MaxStepHeight = grid.CellSize;

        }

        /// <summary>Cells expanded by the last search. For tests and perf reporting.</summary>
        public int LastExpandedCount { get; private set; }

        /// <summary>Whether a unit can move directly from cell a to adjacent cell b.</summary>
        public bool CanStep(int ax, int az, int bx, int bz)
        {
            if (!_grid.InBounds(ax, az) || !_grid.InBounds(bx, bz))
                return false;
            var dx = bx - ax;
            var dz = bz - az;
            if (Math.Abs(dx) > 1 || Math.Abs(dz) > 1 || (dx == 0 && dz == 0))
                return false;
            if (!Climbable(ax, az, bx, bz))
                return false;
            if (dx == 0 || dz == 0)
                return true;

            // Diagonal: both cells it cuts past must be passable on the way, or it is a corner-cut
            // over a step.
            return Climbable(ax, az, bx, az) && Climbable(bx, az, bx, bz)
                && Climbable(ax, az, ax, bz) && Climbable(ax, bz, bx, bz);
        }

        /// <summary>Cost of stepping from a to adjacent b; only meaningful when <see cref="CanStep"/> is true.</summary>
        public float StepCost(int ax, int az, int bx, int bz)
        {
            var distance = (ax != bx && az != bz ? Diagonal : 1f) * _grid.CellSize;
            if (_grid.GetTopMaterial(ax, az) == MaterialTable.Road && _grid.GetTopMaterial(bx, bz) == MaterialTable.Road)
                return distance * RoadCost;
            var rise = Math.Abs(_grid.GetSurfaceHeight(bx, bz) - _grid.GetSurfaceHeight(ax, az));
            return distance * (1f + rise * SlopeCostFactor);
        }

        /// <summary>
        /// Shortest path from start to goal, both inclusive. False if unreachable.
        /// <paramref name="avoid"/> blocks cells the unit must not drive through, such as those
        /// another unit is standing on; the start is never blocked.
        /// </summary>
        /// <summary>
        /// Most cells one search may expand before it gives up, or 0 for no limit. A path that
        /// exists is found long before this; what it bounds is the cost of one that does not.
        /// A cross-island path expands a few thousand cells, so this leaves a wide margin.
        /// </summary>
        public int MaxExpanded = 120_000;

        /// <summary>Whether the last search stopped on its budget rather than finishing.</summary>
        public bool GaveUp { get; private set; }

        public bool TryFindPath(int startX, int startZ, int goalX, int goalZ, List<Vector2Int> path, Func<int, int, bool> avoid = null)
        {
            var goal = goalZ * _grid.Width + goalX;
            var found = Search(startX, startZ, cell => cell == goal, goalX, goalZ, 0f, true, avoid: avoid);
            return Reconstruct(found, path);
        }

        /// <summary>
        /// Shortest path from start to any cell adjacent (8-way) to the target, optionally only
        /// cells for which <paramref name="canStandAt"/> is true. The target itself is never the
        /// end of the path, even if it is passable.
        /// </summary>
        public bool TryFindPathToAdjacent(int startX, int startZ, int targetX, int targetZ, List<Vector2Int> path, Func<int, int, bool> canStandAt = null)
        {
            var width = _grid.Width;
            var found = Search(startX, startZ, cell =>
            {
                var x = cell % width;
                var z = cell / width;
                if (x == targetX && z == targetZ)
                    return false;
                if (Math.Abs(x - targetX) > 1 || Math.Abs(z - targetZ) > 1)
                    return false;
                return canStandAt == null || canStandAt(x, z);
            }, targetX, targetZ, Diagonal, true);
            return Reconstruct(found, path);
        }

        /// <summary>
        /// Searches outward from the start in order of path cost and returns the path to the
        /// cheapest-to-reach cell for which <paramref name="isGoal"/> is true (Dijkstra). Used to
        /// find the nearest place to do a job without pathing to every candidate.
        /// </summary>
        public bool TryFindNearest(int startX, int startZ, Func<int, int, bool> isGoal, List<Vector2Int> path)
        {
            var width = _grid.Width;
            var found = Search(startX, startZ, cell => isGoal(cell % width, cell / width), 0, 0, 0f, false);
            return Reconstruct(found, path);
        }

        /// <summary>
        /// Share of <see cref="TerrainGrid.DeepWater"/> a way out of water ends in. Clear of the
        /// water rather than just under the limit: aimed at the nearest cell under the limit, a
        /// unit in a rising pool was caught again a cell later, and hopped four times in 0.75 s.
        /// </summary>
        public const float ClearOfWater = 0.5f;

        /// <summary>
        /// The way out for a unit the water has come up round: the cheapest path, driving through
        /// water (but never the void, and never up a step it could not climb), to the nearest cell
        /// that is not water and has under <see cref="ClearOfWater"/> of the deep-water depth on it.
        /// False if there is none, or the unit is not in water.
        /// </summary>
        public bool TryFindWayOutOfWater(int startX, int startZ, List<Vector2Int> path)
        {
            if (!_grid.IsGround(startX, startZ) || _grid.IsPassableGround(startX, startZ))
                return false;
            var width = _grid.Width;
            _wading = true;
            try
            {
                var clear = _grid.DeepWater * ClearOfWater;
                var found = Search(startX, startZ, cell =>
                {
                    int x = cell % width, z = cell / width;
                    return _grid.IsPassableGround(x, z) && _grid.WaterDepth(x, z) < clear;
                }, 0, 0, 0f, false);
                return Reconstruct(found, path);
            }
            finally
            {
                _wading = false;
            }
        }

        /// <summary>Whether a search may enter the cell: passable ground, or any ground while wading out of water.</summary>
        bool Open(int x, int z) => _wading ? _grid.IsGround(x, z) : _grid.IsPassableGround(x, z);

        bool _wading;

        /// <summary>
        /// Marks every cell reachable from the start in <paramref name="reachable"/> (indexed
        /// z * width + x). Returns how many there are.
        ///
        /// Reads the grid's height array directly rather than through <see cref="CanStep"/>: on
        /// open ground this touches every cell of the map, so it is the hot loop.
        /// </summary>
        public int FloodReachable(int startX, int startZ, bool[] reachable)
        {
            if (reachable == null || reachable.Length != _grid.Width * _grid.Height)
                throw new ArgumentException("Needs one entry per cell.", nameof(reachable));
            Array.Clear(reachable, 0, reachable.Length);
            if (!_grid.InBounds(startX, startZ))
                return 0;

            var heights = _grid.SurfaceHeights;
            var voids = _grid.BlockedCells;
            var width = _grid.Width;
            var depth = _grid.Height;
            var limit = MaxStepHeight + Tolerance;
            var stack = _floodCells;
            var top = 0;
            var start = startZ * width + startX;
            if (voids[start])
                return 0;
            reachable[start] = true;
            stack[top++] = start;
            var count = 1;
            while (top > 0)
            {
                var cell = stack[--top];
                var x = cell % width;
                var z = cell / width;
                var h = heights[cell];
                for (var n = 0; n < 8; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                        continue;
                    var next = nz * width + nx;
                    if (reachable[next] || voids[next] || Math.Abs(heights[next] - h) > limit)
                        continue;
                    if (n >= 4)
                    {
                        // Diagonal: no cutting past a corner that is a step away from either end.
                        var sideA = z * width + nx;
                        var sideB = nz * width + x;
                        var hn = heights[next];
                        if (Math.Abs(heights[sideA] - h) > limit || Math.Abs(heights[sideA] - hn) > limit
                            || Math.Abs(heights[sideB] - h) > limit || Math.Abs(heights[sideB] - hn) > limit)
                            continue;
                    }

                    reachable[next] = true;
                    count++;
                    stack[top++] = next;
                }
            }

            return count;
        }

        /// <summary>
        /// A route from the start to the goal that ignores the step limit, for planning a ramp.
        /// Steps higher than <see cref="MaxStepHeight"/> are allowed but cost an extra
        /// <paramref name="steepPenalty"/> per metre over the limit, so the route crosses as few
        /// and as small cliffs as it can. It is 4-connected, so a unit can drive the finished ramp
        /// without cutting corners. <paramref name="canCross"/> limits which cells it may use
        /// (the goal is always allowed). Start and goal inclusive.
        /// </summary>
        public bool TryFindCorridor(int startX, int startZ, int goalX, int goalZ, List<Vector2Int> path, float steepPenalty, Func<int, int, bool> canCross = null)
        {
            var goal = goalZ * _grid.Width + goalX;
            var found = Search(startX, startZ, cell => cell == goal, goalX, goalZ, 0f, true,
                canCross ?? ((x, z) => true), steepPenalty, goal);
            return Reconstruct(found, path);
        }

        bool Climbable(int ax, int az, int bx, int bz) =>
            Open(ax, az) && Open(bx, bz)
            && Math.Abs(_grid.GetSurfaceHeight(ax, az) - _grid.GetSurfaceHeight(bx, bz)) <= MaxStepHeight + Tolerance;

        /// <summary>
        /// A* (or Dijkstra without a heuristic). Returns the goal cell index, or -1. With
        /// <paramref name="corridor"/> set it is the ramp-planning search instead: 4-connected,
        /// no step limit, steep steps penalised, only cells <paramref name="corridor"/> allows.
        /// </summary>
        int Search(int startX, int startZ, Predicate<int> isGoal, int headingX, int headingZ, float heuristicSlack, bool useHeuristic,
            Func<int, int, bool> corridor = null, float steepPenalty = 0f, int corridorGoal = -1, Func<int, int, bool> avoid = null)
        {
            LastExpandedCount = 0;
            GaveUp = false;
            if (!_grid.InBounds(startX, startZ))
                return -1;

            // A search that cannot succeed expands every cell it can reach before it gives up, and
            // on this island that is nine and a half million of them: measured, a hopeless search
            // costs 188 ms at 512 cells square, 804 ms at 1024 and 3.4 s at 2048 — quadratic in
            // the map, about eight seconds at 3104 (2026-09-23). One of those a frame is the whole
            // of the lag. A budget turns "no" from the most expensive answer into a cheap one.
            var budget = MaxExpanded > 0 ? MaxExpanded : int.MaxValue;

            unchecked
            {
                _generation++;
            }

            var width = _grid.Width;
            var start = startZ * width + startX;
            _heapCount = 0;
            _cost[start] = 0f;
            _parent[start] = -1;
            _seen[start] = _generation;
            Push(start, useHeuristic ? Heuristic(startX, startZ, headingX, headingZ, heuristicSlack) : 0f);

            while (_heapCount > 0)
            {
                if (LastExpandedCount >= budget)
                {
                    GaveUp = true;
                    return -1;
                }

                var cell = Pop();
                if (_closed[cell] == _generation)
                    continue;
                _closed[cell] = _generation;
                LastExpandedCount++;
                if (isGoal(cell))
                    return cell;

                var x = cell % width;
                var z = cell / width;
                var neighbours = corridor == null ? 8 : 4;
                for (var n = 0; n < neighbours; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (!_grid.InBounds(nx, nz))
                        continue;
                    var next = nz * width + nx;
                    if (_closed[next] == _generation)
                        continue;
                    if (!Open(nx, nz))
                        continue;
                    if (corridor == null ? !CanStep(x, z, nx, nz) : next != corridorGoal && !corridor(nx, nz))
                        continue;
                    if (avoid != null && avoid(nx, nz))
                        continue;


                    var cost = _cost[cell] + (corridor == null ? StepCost(x, z, nx, nz) : CorridorCost(x, z, nx, nz, steepPenalty));
                    if (_seen[next] == _generation && _cost[next] <= cost)
                        continue;
                    _seen[next] = _generation;
                    _cost[next] = cost;
                    _parent[next] = cell;
                    Push(next, cost + (useHeuristic ? Heuristic(nx, nz, headingX, headingZ, heuristicSlack) : 0f));
                }
            }

            return -1;
        }

        float CorridorCost(int ax, int az, int bx, int bz, float steepPenalty)
        {
            var rise = Math.Abs(_grid.GetSurfaceHeight(bx, bz) - _grid.GetSurfaceHeight(ax, az));
            var cost = _grid.CellSize * (1f + rise * SlopeCostFactor);
            if (rise > MaxStepHeight + Tolerance)
                cost += (rise - MaxStepHeight) * steepPenalty;
            return cost;
        }

        bool Reconstruct(int goal, List<Vector2Int> path)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            path.Clear();
            if (goal < 0)
                return false;

            var width = _grid.Width;
            for (var cell = goal; cell >= 0; cell = _parent[cell])
                path.Add(new Vector2Int(cell % width, cell / width));
            path.Reverse();
            return true;
        }

        /// <summary>
        /// Octile distance in metres to (tx, tz), less a slack (in cells) for goals that only need to
        /// be near it.
        /// </summary>
        float Heuristic(int x, int z, int tx, int tz, float slack)
        {
            var dx = Math.Abs(x - tx);
            var dz = Math.Abs(z - tz);
            var octile = Math.Max(dx, dz) + (Diagonal - 1f) * Math.Min(dx, dz);
            // Scaled by the road cost, the cheapest a step can be, so it never overestimates and the
            // search still finds the cheapest way, road or not — then scaled back up by
            // HeuristicWeight, because being led by seven tenths of the real distance is what made
            // the search crawl outward in a huge diamond.
            // Scaled by the cheapest a step can be, so it never overestimates and the search finds
            // the cheapest way — but only while there is anything cheap to find. On a map with no
            // road on it the cheapest step is a plain one, and assuming otherwise means being led
            // by seven tenths of the real distance, which is what made the search crawl outward in
            // a huge diamond: a five-hundred-cell path expanded 161,385 cells and took 116 ms, and
            // the same path with the true distance expanded 501 cells and took 0.6 ms
            // (2026-09-23). Once a road exists the scaling comes back and the search goes back to
            // being exact, because a crew that will not walk onto its own roads is worse than a
            // slow one.
            var cheapest = _grid.RoadCellCount > 0 ? Math.Min(1f, RoadCost) : 1f;
            return Math.Max(0f, octile - slack) * _grid.CellSize * cheapest * HeuristicWeight;
        }

        void Push(int cell, float key)
        {
            if (_heapCount == _heapCell.Length)
            {
                Array.Resize(ref _heapCell, _heapCount * 2);
                Array.Resize(ref _heapKey, _heapCount * 2);
            }

            var i = _heapCount++;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (!Before(key, cell, _heapKey[parent], _heapCell[parent]))
                    break;
                _heapCell[i] = _heapCell[parent];
                _heapKey[i] = _heapKey[parent];
                i = parent;
            }

            _heapCell[i] = cell;
            _heapKey[i] = key;
        }

        int Pop()
        {
            var top = _heapCell[0];
            var lastCell = _heapCell[--_heapCount];
            var lastKey = _heapKey[_heapCount];
            var i = 0;
            while (true)
            {
                var child = 2 * i + 1;
                if (child >= _heapCount)
                    break;
                if (child + 1 < _heapCount && Before(_heapKey[child + 1], _heapCell[child + 1], _heapKey[child], _heapCell[child]))
                    child++;
                if (!Before(_heapKey[child], _heapCell[child], lastKey, lastCell))
                    break;
                _heapCell[i] = _heapCell[child];
                _heapKey[i] = _heapKey[child];
                i = child;
            }

            _heapCell[i] = lastCell;
            _heapKey[i] = lastKey;
            return top;
        }

        /// <summary>Heap order: by key, ties by cell index, so searches are deterministic.</summary>
        static bool Before(float keyA, int cellA, float keyB, int cellB) =>
            keyA < keyB || (keyA == keyB && cellA < cellB);
    }
}
