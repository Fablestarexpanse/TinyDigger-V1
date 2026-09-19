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

        /// <summary>Largest surface-height difference a single step can climb or drop, in metres.</summary>
        public float MaxStepHeight = 1f;

        /// <summary>Extra cost per metre of height change, as a fraction of the step's length.</summary>
        public float SlopeCostFactor = 1f;

        readonly TerrainGrid _grid;
        readonly float[] _cost;
        readonly int[] _parent;
        readonly int[] _seen;
        readonly int[] _closed;
        readonly Stack<int> _floodStack = new Stack<int>();
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
            var distance = ax != bx && az != bz ? Diagonal : 1f;
            var rise = Math.Abs(_grid.GetSurfaceHeight(bx, bz) - _grid.GetSurfaceHeight(ax, az));
            return distance * (1f + rise * SlopeCostFactor);
        }

        /// <summary>Shortest path from start to goal, both inclusive. False if unreachable.</summary>
        public bool TryFindPath(int startX, int startZ, int goalX, int goalZ, List<Vector2Int> path)
        {
            var goal = goalZ * _grid.Width + goalX;
            var found = Search(startX, startZ, cell => cell == goal, goalX, goalZ, 0f, true);
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
        /// Marks every cell reachable from the start in <paramref name="reachable"/> (indexed
        /// z * width + x). Returns how many there are.
        /// </summary>
        public int FloodReachable(int startX, int startZ, bool[] reachable)
        {
            if (reachable == null || reachable.Length != _grid.Width * _grid.Height)
                throw new ArgumentException("Needs one entry per cell.", nameof(reachable));
            Array.Clear(reachable, 0, reachable.Length);
            if (!_grid.InBounds(startX, startZ))
                return 0;

            var width = _grid.Width;
            var stack = _floodStack;
            stack.Clear();
            var start = startZ * width + startX;
            reachable[start] = true;
            stack.Push(start);
            var count = 1;
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                var x = cell % width;
                var z = cell / width;
                for (var n = 0; n < 8; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (!_grid.InBounds(nx, nz))
                        continue;
                    var next = nz * width + nx;
                    if (reachable[next] || !CanStep(x, z, nx, nz))
                        continue;
                    reachable[next] = true;
                    count++;
                    stack.Push(next);
                }
            }

            return count;
        }

        bool Climbable(int ax, int az, int bx, int bz) =>
            Math.Abs(_grid.GetSurfaceHeight(ax, az) - _grid.GetSurfaceHeight(bx, bz)) <= MaxStepHeight + Tolerance;

        /// <summary>A* (or Dijkstra without a heuristic). Returns the goal cell index, or -1.</summary>
        int Search(int startX, int startZ, Predicate<int> isGoal, int headingX, int headingZ, float heuristicSlack, bool useHeuristic)
        {
            LastExpandedCount = 0;
            if (!_grid.InBounds(startX, startZ))
                return -1;

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
                var cell = Pop();
                if (_closed[cell] == _generation)
                    continue;
                _closed[cell] = _generation;
                LastExpandedCount++;
                if (isGoal(cell))
                    return cell;

                var x = cell % width;
                var z = cell / width;
                for (var n = 0; n < 8; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (!_grid.InBounds(nx, nz))
                        continue;
                    var next = nz * width + nx;
                    if (_closed[next] == _generation || !CanStep(x, z, nx, nz))
                        continue;

                    var cost = _cost[cell] + StepCost(x, z, nx, nz);
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

        /// <summary>Octile distance to (tx, tz), less a slack for goals that only need to be near it.</summary>
        static float Heuristic(int x, int z, int tx, int tz, float slack)
        {
            var dx = Math.Abs(x - tx);
            var dz = Math.Abs(z - tz);
            var octile = Math.Max(dx, dz) + (Diagonal - 1f) * Math.Min(dx, dz);
            return Math.Max(0f, octile - slack);
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
