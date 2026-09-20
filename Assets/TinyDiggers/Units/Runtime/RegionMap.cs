using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// The map cut into connected regions: two cells are in the same region when a unit can drive
    /// between them (<see cref="GridPathfinder.MaxStepHeight"/>). "Can this unit reach that cell"
    /// is then a comparison of two labels, shared by every unit rather than flooded per unit.
    ///
    /// The first query labels the whole map. After that, only the regions touched by changed
    /// cells are rebuilt: their labels are cleared and re-flooded, which splits a region that was
    /// cut in two. A re-flood that runs into a region that was not dirty absorbs it, which joins
    /// regions that a filled step has connected. Changes are collected and applied on the next
    /// query, so a burst of digging costs one rebuild.
    /// </summary>
    public sealed class RegionMap : IDisposable
    {
        const int Unlabelled = -1;

        readonly TerrainGrid _grid;
        readonly GridPathfinder _pathfinder;
        readonly int[] _label;
        readonly Dictionary<int, List<int>> _members = new Dictionary<int, List<int>>();
        readonly List<int> _dirtyCells = new List<int>();
        readonly bool[] _dirty;
        readonly Stack<int> _stack = new Stack<int>();
        readonly HashSet<int> _affected = new HashSet<int>();
        int _nextRegion;
        bool _needsFullBuild = true;
        float _builtStepHeight = float.NaN;
        bool _disposed;

        public RegionMap(TerrainGrid grid, GridPathfinder pathfinder)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            var cells = grid.Width * grid.Height;
            _label = new int[cells];
            _dirty = new bool[cells];
            _grid.CellChanged += OnCellChanged;
        }

        /// <summary>How many times regions have been (re)built. For tests and perf reporting.</summary>
        public int RebuildCount { get; private set; }

        /// <summary>Cells re-labelled by the last rebuild: the work a change actually cost.</summary>
        public int CellsRelabelledLast { get; private set; }

        /// <summary>Regions currently on the map.</summary>
        public int RegionCount
        {
            get
            {
                Update();
                return _members.Count;
            }
        }

        /// <summary>Whether a rebuild is pending.</summary>
        public bool IsStale => _needsFullBuild || _dirtyCells.Count > 0 || _pathfinder.MaxStepHeight != _builtStepHeight;

        /// <summary>The region (x, z) belongs to; regions are only comparable with each other.</summary>
        public int RegionOf(int x, int z)
        {
            Update();
            return _label[z * _grid.Width + x];
        }

        /// <summary>How many cells are in the region holding (x, z).</summary>
        public int RegionSize(int x, int z)
        {
            var region = RegionOf(x, z);
            return region == Unlabelled || !_members.TryGetValue(region, out var cells) ? 0 : cells.Count;
        }

        /// <summary>Whether a unit standing on (fromX, fromZ) can drive to (x, z).</summary>
        public bool CanReach(int fromX, int fromZ, int x, int z)
        {
            if (!_grid.IsPassableGround(fromX, fromZ) || !_grid.IsPassableGround(x, z))
                return false;
            Update();
            var width = _grid.Width;
            var from = _label[fromZ * width + fromX];
            return from != Unlabelled && from == _label[z * width + x];
        }

        public void Invalidate()
        {
            _needsFullBuild = true;
        }

        /// <summary>Applies any pending changes. Called by every query; exposed for timing it.</summary>
        public void Update()
        {
            if (_pathfinder.MaxStepHeight != _builtStepHeight)
            {
                _builtStepHeight = _pathfinder.MaxStepHeight;
                _needsFullBuild = true;
            }

            if (_needsFullBuild)
            {
                FullBuild();
                return;
            }

            if (_dirtyCells.Count == 0)
                return;

            RebuildDirty();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
        }

        void OnCellChanged(int x, int z)
        {
            var cell = z * _grid.Width + x;
            if (_dirty[cell])
                return;
            _dirty[cell] = true;
            _dirtyCells.Add(cell);
        }

        void FullBuild()
        {
            Array.Fill(_label, Unlabelled);
            _members.Clear();
            _nextRegion = 0;
            CellsRelabelledLast = 0;
            var voids = _grid.BlockedCells;
            for (var cell = 0; cell < _label.Length; cell++)
                if (_label[cell] == Unlabelled && !voids[cell])
                    Flood(cell);

            ClearDirty();
            _needsFullBuild = false;
            _builtStepHeight = _pathfinder.MaxStepHeight;
            RebuildCount++;
        }

        /// <summary>
        /// Re-labels every region that a changed cell touches. Their cells are unlabelled first,
        /// so a region that has been cut comes back as several; a flood that meets a region left
        /// alone swallows it, so regions that have been joined come back as one.
        /// </summary>
        void RebuildDirty()
        {
            CellsRelabelledLast = 0;
            _affected.Clear();
            var width = _grid.Width;
            foreach (var cell in _dirtyCells)
            {
                var x = cell % width;
                var z = cell / width;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (!_grid.InBounds(x + dx, z + dz))
                            continue;
                        var label = _label[(z + dz) * width + x + dx];
                        if (label != Unlabelled)
                            _affected.Add(label);
                    }
                }
            }

            // Unlabel them all first: what was one region may now be several.
            var seeds = new List<int>();
            foreach (var region in _affected)
            {
                if (!_members.TryGetValue(region, out var cells))
                    continue;
                foreach (var cell in cells)
                {
                    _label[cell] = Unlabelled;
                    seeds.Add(cell);
                }

                _members.Remove(region);
            }

            var voids = _grid.BlockedCells;
            foreach (var cell in _dirtyCells)
                if (_label[cell] == Unlabelled)
                    seeds.Add(cell);

            foreach (var cell in seeds)
                if (_label[cell] == Unlabelled && !voids[cell])
                    Flood(cell);

            ClearDirty();
            RebuildCount++;
        }

        /// <summary>Labels everything connected to <paramref name="start"/> as one new region. Void cells are skipped.</summary>
        void Flood(int start)
        {
            var region = _nextRegion++;
            var members = new List<int>();
            _members[region] = members;
            var heights = _grid.SurfaceHeights;
            var voids = _grid.BlockedCells;
            var width = _grid.Width;
            var depth = _grid.Height;
            var limit = _pathfinder.MaxStepHeight + 1e-3f;
            _stack.Clear();
            _label[start] = region;
            members.Add(start);
            _stack.Push(start);
            CellsRelabelledLast++;
            while (_stack.Count > 0)
            {
                var cell = _stack.Pop();
                var x = cell % width;
                var z = cell / width;
                var height = heights[cell];
                for (var n = 0; n < 8; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                        continue;
                    var next = nz * width + nx;
                    if (_label[next] == region || voids[next] || Math.Abs(heights[next] - height) > limit)
                        continue;
                    if (n >= 4)
                    {
                        var sideA = z * width + nx;
                        var sideB = nz * width + x;
                        var nextHeight = heights[next];
                        if (Math.Abs(heights[sideA] - height) > limit || Math.Abs(heights[sideA] - nextHeight) > limit
                            || Math.Abs(heights[sideB] - height) > limit || Math.Abs(heights[sideB] - nextHeight) > limit)
                            continue;
                    }

                    // A neighbour still carrying an older label means the two are joined now:
                    // swallow that region whole rather than leaving two labels for one area.
                    if (_label[next] != Unlabelled)
                    {
                        Absorb(_label[next], region, members);
                        continue;
                    }

                    _label[next] = region;
                    members.Add(next);
                    CellsRelabelledLast++;
                    _stack.Push(next);
                }
            }
        }

        void Absorb(int other, int region, List<int> members)
        {
            if (!_members.TryGetValue(other, out var cells))
                return;
            foreach (var cell in cells)
            {
                _label[cell] = region;
                members.Add(cell);
                CellsRelabelledLast++;
                _stack.Push(cell);
            }

            _members.Remove(other);
        }

        void ClearDirty()
        {
            foreach (var cell in _dirtyCells)
                _dirty[cell] = false;
            _dirtyCells.Clear();
        }

        static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] StepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
    }
}
