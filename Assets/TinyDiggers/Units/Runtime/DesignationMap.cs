using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units
{
    public enum DesignationKind : byte
    {
        None,

        /// <summary>Lower the cell until its surface is at or below the target height.</summary>
        Dig,

        /// <summary>Raise the cell until its surface is at or above the target height.</summary>
        Fill,
    }

    /// <summary>
    /// What the player has asked to be done to each cell: dig to height H or fill to height H,
    /// per cell (TERRAIN_REFERENCE.md section 4). A designation stays until it is met, when it
    /// clears itself: the map listens to the grid and drops any designation whose cell has
    /// reached its target. Designating a cell that already meets the target does nothing.
    /// </summary>
    public sealed class DesignationMap : IDisposable
    {
        /// <summary>Heights this close count as meeting the target.</summary>
        const float Tolerance = 1e-3f;

        readonly TerrainGrid _grid;
        readonly DesignationKind[] _kinds;
        readonly float[] _targets;
        readonly int[] _slot;
        readonly List<int> _active = new List<int>();
        bool _disposed;

        public DesignationMap(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            var cells = grid.Width * grid.Height;
            _kinds = new DesignationKind[cells];
            _targets = new float[cells];
            _slot = new int[cells];
            for (var i = 0; i < cells; i++)
                _slot[i] = -1;
            _grid.CellChanged += OnCellChanged;
        }

        /// <summary>Raised with (x, z) whenever a cell's designation is added, changed or cleared.</summary>
        public event Action<int, int> Changed;

        /// <summary>Changes on every add, change or clear.</summary>
        public int Version { get; private set; }

        public int Count => _active.Count;

        /// <summary>Designated cells as grid indices (z * width + x), in no particular order.</summary>
        public IReadOnlyList<int> ActiveCells => _active;

        public TerrainGrid Grid => _grid;

        public DesignationKind GetKind(int x, int z) => _kinds[Index(x, z)];

        public float GetTarget(int x, int z) => _targets[Index(x, z)];

        /// <summary>Whether the cell's surface already satisfies a designation of this kind and height.</summary>
        public bool Satisfies(int x, int z, DesignationKind kind, float height)
        {
            var surface = _grid.GetSurfaceHeight(x, z);
            return kind == DesignationKind.Dig ? surface <= height + Tolerance
                : kind == DesignationKind.Fill ? surface >= height - Tolerance
                : true;
        }

        /// <summary>
        /// Marks the cell. Returns false, and leaves the cell clear, if the surface already meets
        /// the target: there is nothing to do. Replaces any previous designation on the cell.
        /// </summary>
        public bool Designate(int x, int z, DesignationKind kind, float height)
        {
            if (kind == DesignationKind.None)
                throw new ArgumentException("Use Clear to remove a designation.", nameof(kind));

            var cell = Index(x, z);
            if (Satisfies(x, z, kind, height))
            {
                Clear(x, z);
                return false;
            }

            if (_kinds[cell] == kind && _targets[cell] == height)
                return true;

            _kinds[cell] = kind;
            _targets[cell] = height;
            if (_slot[cell] < 0)
            {
                _slot[cell] = _active.Count;
                _active.Add(cell);
            }

            Raise(x, z);
            return true;
        }

        public void Clear(int x, int z)
        {
            var cell = Index(x, z);
            if (_kinds[cell] == DesignationKind.None)
                return;

            _kinds[cell] = DesignationKind.None;
            _targets[cell] = 0f;
            // Swap-remove from the active list, keeping slots in step.
            var slot = _slot[cell];
            var last = _active[_active.Count - 1];
            _active[slot] = last;
            _slot[last] = slot;
            _active.RemoveAt(_active.Count - 1);
            _slot[cell] = -1;
            Raise(x, z);
        }

        public void ClearAll()
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var cell = _active[i];
                Clear(cell % _grid.Width, cell / _grid.Width);
            }
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
            if (_kinds[cell] != DesignationKind.None && Satisfies(x, z, _kinds[cell], _targets[cell]))
                Clear(x, z);
        }

        void Raise(int x, int z)
        {
            Version++;
            Changed?.Invoke(x, z);
        }

        int Index(int x, int z)
        {
            if (!_grid.InBounds(x, z))
                throw new ArgumentOutOfRangeException(nameof(x), $"Cell ({x}, {z}) is off the map.");
            return z * _grid.Width + x;
        }
    }
}
