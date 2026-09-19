using System;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// The cells a unit can drive to, kept as a set so that "can it get there" is one array
    /// lookup instead of a path search.
    ///
    /// The set is one flood fill over passable steps (<see cref="GridPathfinder.MaxStepHeight"/>)
    /// from the unit's cell. Any height change on the map marks it stale, and the flood is only
    /// redone on the next query, so a burst of digging costs one flood, not one per cell. Moving
    /// does not make it stale: every cell in a connected area reaches the same area, so the set
    /// is only re-flooded when the unit turns up outside it.
    /// </summary>
    public sealed class ReachabilityCache : IDisposable
    {
        readonly TerrainGrid _grid;
        readonly GridPathfinder _pathfinder;
        readonly bool[] _reachable;
        float _floodedStepHeight = float.NaN;
        bool _stale = true;
        bool _disposed;

        public ReachabilityCache(TerrainGrid grid, GridPathfinder pathfinder)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            _reachable = new bool[grid.Width * grid.Height];
            _grid.CellChanged += OnCellChanged;
        }

        /// <summary>How many times the set has been flooded. For tests and perf reporting.</summary>
        public int FloodCount { get; private set; }

        /// <summary>Cells in the set as of the last flood.</summary>
        public int Count { get; private set; }

        /// <summary>Whether the next query will re-flood.</summary>
        public bool IsStale => _stale;

        /// <summary>Brings the set up to date for a unit standing on (fromX, fromZ).</summary>
        public void Update(int fromX, int fromZ)
        {
            var from = fromZ * _grid.Width + fromX;
            if (!_stale && _pathfinder.MaxStepHeight == _floodedStepHeight && _reachable[from])
                return;

            Count = _pathfinder.FloodReachable(fromX, fromZ, _reachable);
            _floodedStepHeight = _pathfinder.MaxStepHeight;
            _stale = false;
            FloodCount++;
        }

        /// <summary>Whether a unit on (fromX, fromZ) can drive to (x, z).</summary>
        public bool IsReachable(int fromX, int fromZ, int x, int z)
        {
            Update(fromX, fromZ);
            return _reachable[z * _grid.Width + x];
        }

        /// <summary>The set as last brought up to date by <see cref="Update"/>; no check that it is current.</summary>
        public bool Contains(int x, int z) => _reachable[z * _grid.Width + x];

        public void Invalidate() => _stale = true;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
        }

        void OnCellChanged(int x, int z) => _stale = true;
    }
}
