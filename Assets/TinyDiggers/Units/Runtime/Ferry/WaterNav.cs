using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Where a hull floats and how it gets across the water (FERRY_PROPOSAL.md, slice A). A cell
    /// floats a hull when the water on it is at least the draft plus a clearance deep, and so is
    /// every cell within half the beam of it, so the hull's sides never ground either. Routes are
    /// found over those cells only.
    ///
    /// Rivers need no rule of their own (Ronan, 2026-09-24: "if it can't go up river then it stays
    /// at beach"): the beam and the draft decide. A 2.87 m beam passes a 5 m river with its sides
    /// clear and not a 2.5 m one, so a craft goes up a wide deep river and waits at the mouth of a
    /// narrow one.
    ///
    /// The land pathfinder is not reused: it climbs the ground's steps, and a hull does not care
    /// what the ground under the water does, only how deep the water over it is.
    /// </summary>
    public sealed class WaterNav
    {
        readonly TerrainGrid _grid;
        bool[] _floats;
        float[] _cost;
        int[] _parent;
        int[] _stamp;
        int _search;

        /// <summary>Metres the hull sits below the water, loaded.</summary>
        public float Draft { get; }

        /// <summary>Metres of water kept under the keel on top of the draft.</summary>
        public float Clearance { get; }

        /// <summary>Half the hull's beam, in cells.</summary>
        public float HalfBeamCells { get; }

        /// <param name="halfBeam">Half the beam, in metres.</param>
        public WaterNav(TerrainGrid grid, float draft, float halfBeam, float clearance = 0.15f)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Draft = draft;
            Clearance = clearance;
            HalfBeamCells = halfBeam / Math.Max(0.01f, grid.CellSize);
        }

        /// <summary>Whether a hull centred on the cell floats clear, sides and all.</summary>
        public bool Floats(int x, int z)
        {
            if (_floats == null)
                Refresh();
            return _grid.InBounds(x, z) && _floats[z * _grid.Width + x];
        }

        /// <summary>Whether the water on the cell is deep enough for the keel, beam aside.</summary>
        public bool DeepEnough(int x, int z) =>
            _grid.IsGround(x, z) && _grid.WaterDepth(x, z) >= Draft + Clearance;

        /// <summary>
        /// Works out afresh where the hull floats. Live water moves and the crew digs, so this is
        /// for a slow clock or a change, not every frame: one pass over the map and a distance
        /// transform.
        /// </summary>
        public void Refresh()
        {
            var width = _grid.Width;
            var cells = width * _grid.Height;
            var shallow = new bool[cells];
            for (var z = 0; z < _grid.Height; z++)
                for (var x = 0; x < width; x++)
                    shallow[z * width + x] = !DeepEnough(x, z);
            // Distance from each cell to the nearest one too shallow: the hull fits where that is
            // more than half its beam.
            var reach = HalfBeamCells + 2f;
            var distance = TerrainErosion.EuclideanDistance(shallow, width, _grid.Height, reach);
            // Measured to the near edge of the shallow cell, not its middle: from middles, a 2.5 m
            // river let a 2.87 m beam through.
            _floats = new bool[cells];
            for (var i = 0; i < cells; i++)
                _floats[i] = !shallow[i] && distance[i] - 0.5f > HalfBeamCells;
        }

        /// <summary>
        /// The shortest way over floating cells from <paramref name="from"/> to
        /// <paramref name="to"/>, both cells a hull floats on, eight ways round. False if the
        /// water does not join them.
        /// </summary>
        public bool TryFindRoute(Vector2Int from, Vector2Int to, List<Vector2Int> route)
        {
            if (route == null)
                throw new ArgumentNullException(nameof(route));
            route.Clear();
            if (!Floats(from.x, from.y) || !Floats(to.x, to.y))
                return false;

            var width = _grid.Width;
            var cells = width * _grid.Height;
            if (_cost == null || _cost.Length != cells)
            {
                _cost = new float[cells];
                _parent = new int[cells];
                _stamp = new int[cells];
            }

            _search++;
            var start = from.y * width + from.x;
            var goal = to.y * width + to.x;
            var open = new MinHeap();
            _stamp[start] = _search;
            _cost[start] = 0f;
            _parent[start] = -1;
            open.Push(start, Octile(from, to));
            while (open.Count > 0)
            {
                var cell = open.Pop();
                if (cell == goal)
                    break;
                int cx = cell % width, cz = cell / width;
                for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (!Floats(nx, nz))
                            continue;
                        // A diagonal needs both sides it cuts past, or it slips through a gap.
                        if (dx != 0 && dz != 0 && (!Floats(cx + dx, cz) || !Floats(cx, cz + dz)))
                            continue;
                        var next = nz * width + nx;
                        var cost = _cost[cell] + (dx != 0 && dz != 0 ? 1.41421356f : 1f);
                        if (_stamp[next] == _search && cost >= _cost[next])
                            continue;
                        _stamp[next] = _search;
                        _cost[next] = cost;
                        _parent[next] = cell;
                        open.Push(next, cost + Octile(new Vector2Int(nx, nz), to));
                    }
            }

            if (_stamp[goal] != _search)
                return false;
            for (var cell = goal; cell >= 0; cell = _parent[cell])
                route.Add(new Vector2Int(cell % width, cell / width));
            route.Reverse();
            return true;
        }

        static float Octile(Vector2Int a, Vector2Int b)
        {
            var dx = Math.Abs(a.x - b.x);
            var dz = Math.Abs(a.y - b.y);
            return Math.Max(dx, dz) + 0.41421356f * Math.Min(dx, dz);
        }

        /// <summary>A plain binary heap of cells by priority.</summary>
        sealed class MinHeap
        {
            readonly List<(int cell, float priority)> _items = new List<(int, float)>();

            public int Count => _items.Count;

            public void Push(int cell, float priority)
            {
                _items.Add((cell, priority));
                var i = _items.Count - 1;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (_items[parent].priority <= _items[i].priority)
                        break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public int Pop()
            {
                var top = _items[0].cell;
                var last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                var i = 0;
                while (true)
                {
                    int left = 2 * i + 1, right = left + 1, smallest = i;
                    if (left < _items.Count && _items[left].priority < _items[smallest].priority)
                        smallest = left;
                    if (right < _items.Count && _items[right].priority < _items[smallest].priority)
                        smallest = right;
                    if (smallest == i)
                        break;
                    (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                    i = smallest;
                }

                return top;
            }
        }
    }
}
