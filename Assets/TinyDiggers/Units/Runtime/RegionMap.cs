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

        /// <summary>
        /// Member lists handed back when a region goes, to be handed out again when one is made.
        ///
        /// A region's members are every cell in it, and the crew's landmass is most of the island:
        /// each relabel allocated a fresh list of about 795,000 ints and abandoned the last one.
        /// That is hundreds of megabytes of rubbish per rebuild, and it showed — the managed heap
        /// sat near **three gigabytes** and the game stopped for about 1.3 s at a time while the
        /// collector walked it (2026-09-23). The lists are the same size and shape every time, so
        /// there is no reason to make new ones.
        /// </summary>
        readonly Stack<List<int>> _spare = new Stack<List<int>>();

        /// <summary>The cells a rebuild re-floods from; kept so it is not made afresh each time.</summary>
        readonly List<int> _seeds = new List<int>();

        List<int> TakeList()
        {
            if (_spare.Count == 0)
                return new List<int>();
            var list = _spare.Pop();
            list.Clear();
            return list;
        }

        void Recycle(List<int> list)
        {
            // Only worth keeping the big ones; a pool of thousands of tiny lists is its own leak.
            if (list == null || list.Capacity < 1024 || _spare.Count >= 8)
                return;
            _spare.Push(list);
        }
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
            _grid.CellHeightChanged += OnHeightChanged;
            _grid.CellChanged += OnCellChanged;
            _grid.WaterChanged += OnCellChanged;
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
            _grid.CellHeightChanged -= OnHeightChanged;
            _grid.CellChanged -= OnCellChanged;
            _grid.WaterChanged -= OnCellChanged;
        }

        void OnCellChanged(int x, int z)
        {
            var cell = z * _grid.Width + x;
            // The height change just before this one said the ground still joins up exactly as it
            // did, so no label can have moved and there is nothing to rebuild.
            if (cell == _unchanged)
            {
                _unchanged = -1;
                return;
            }

            _unchanged = -1;
            if (_dirty[cell])
                return;
            _dirty[cell] = true;
            _dirtyCells.Add(cell);
        }

        /// <summary>The cell whose last height change joined nothing up differently, or -1.</summary>
        int _unchanged = -1;

        /// <summary>
        /// Asks whether a change to one cell moved any *connection*, and remembers the answer for
        /// the <see cref="TerrainGrid.CellChanged"/> that follows it.
        ///
        /// This is the whole cost of digging. Relabelling is incremental in the number of dirty
        /// cells and not at all in the size of a region, and the crew dig on a landmass that is one
        /// region of most of the island: one scoop cost a 140.8 ms rebuild relabelling 794,803
        /// cells, and there had been 839 of them in one session (2026-09-23). Almost none of them
        /// were needed — taking half a metre off a cell leaves every neighbour as reachable as it
        /// was.
        ///
        /// It is asked exactly, not approximately, by having the pathfinder answer as though the
        /// cell were still what it was: every ordered pair of adjacent cells in the three by three
        /// block around it, which covers the steps out of the cell, the steps into it, and the
        /// diagonals between its neighbours that cut a corner past it. If all of them agree, the
        /// labels cannot have changed.
        /// </summary>
        void OnHeightChanged(int x, int z, float was, bool wasBlocked)
        {
            _unchanged = -1;
            if (_needsFullBuild)
                return;

            var cell = z * _grid.Width + x;
            if (_dirty[cell])
                return;   // already down for a rebuild; nothing to decide

            _pathfinder.PretendCellWas(x, z, was, wasBlocked);
            var before = Connections(x, z);
            _pathfinder.EndPretending();
            var after = Connections(x, z);
            if (before == after)
                _unchanged = cell;
        }

        /// <summary>
        /// The step graph of the three by three block around (x, z), as a bit per ordered pair of
        /// adjacent cells in it. Two blocks are the same exactly when the same things join up.
        /// </summary>
        ulong Connections(int x, int z)
        {
            var bits = 0UL;
            var bit = 0;
            for (var az = -1; az <= 1; az++)
            {
                for (var ax = -1; ax <= 1; ax++)
                {
                    for (var d = 0; d < 8; d++)
                    {
                        var bx = ax + Around[d * 2];
                        var bz = az + Around[d * 2 + 1];
                        // Only pairs that both lie in the block: a step from its edge to the world
                        // outside cannot have changed, because only the middle cell moved.
                        if (bx < -1 || bx > 1 || bz < -1 || bz > 1)
                            continue;
                        if (_pathfinder.CanStep(x + ax, z + az, x + bx, z + bz))
                            bits |= 1UL << bit;
                        bit++;
                    }
                }
            }

            return bits;
        }

        static readonly int[] Around = { 1, 0, -1, 0, 0, 1, 0, -1, 1, 1, 1, -1, -1, 1, -1, -1 };

        void FullBuild()
        {
            Array.Fill(_label, Unlabelled);
            foreach (var cells in _members.Values)
                Recycle(cells);
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
            //
            // Kept between rebuilds rather than made fresh: this fills with every cell of every
            // region being relabelled — about 795,000 of them on the island's landmass — so a new
            // one each time meant a few megabytes of rubbish per rebuild, and the list doubling its
            // way up there threw away every size on the way. Forty rebuilds in thirty-five seconds
            // grew the heap by 126 MB (2026-09-23).
            var seeds = _seeds;
            seeds.Clear();
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
                Recycle(cells);
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
            var members = TakeList();
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
