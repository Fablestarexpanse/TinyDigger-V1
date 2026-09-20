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
    ///
    /// A designation can be tagged Auto: made by a unit (a ramp step it cuts for itself), not by
    /// the player. The player removing one, or designating over it, raises
    /// <see cref="AutoCancelled"/> so the unit can back off.
    ///
    /// Dump Zones are a separate layer: cells where the player wants spoil tipped, each with a
    /// cap height nothing is heaped above. They never clear themselves and do not count as work.
    /// </summary>
    public sealed class DesignationMap : IDisposable
    {
        /// <summary>Heights this close count as meeting the target.</summary>
        const float Tolerance = 1e-3f;

        readonly TerrainGrid _grid;
        readonly DesignationKind[] _kinds;
        readonly float[] _targets;
        readonly bool[] _auto;
        readonly int[] _slot;
        readonly List<int> _active = new List<int>();
        readonly List<int> _pendingMet = new List<int>();
        readonly bool[] _pending;
        readonly int[] _dumpSlot;
        readonly float[] _dumpCap;
        readonly List<int> _dumpCells = new List<int>();
        bool _disposed;

        public DesignationMap(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            var cells = grid.Width * grid.Height;
            _kinds = new DesignationKind[cells];
            _targets = new float[cells];
            _auto = new bool[cells];
            _slot = new int[cells];
            _pending = new bool[cells];
            _dumpSlot = new int[cells];
            _dumpCap = new float[cells];
            for (var i = 0; i < cells; i++)
            {
                _slot[i] = -1;
                _dumpSlot[i] = -1;
                _dumpCap[i] = float.PositiveInfinity;
            }

            _grid.CellChanged += OnCellChanged;
        }

        /// <summary>Raised with (x, z) whenever a cell's designation or Dump Zone is added, changed or cleared.</summary>
        public event Action<int, int> Changed;

        /// <summary>
        /// Raised with (x, z) when the player removes an Auto designation, or replaces it with one
        /// of their own. Not raised when an Auto designation is met or a unit removes it.
        /// </summary>
        public event Action<int, int> AutoCancelled;

        /// <summary>Changes on every add, change or clear.</summary>
        public int Version { get; private set; }

        /// <summary>Dig and Fill designations, Auto ones included; Dump Zones are not counted.</summary>
        public int Count => _active.Count;

        /// <summary>How many of <see cref="Count"/> are Auto.</summary>
        public int AutoCount { get; private set; }

        /// <summary>Designated cells as grid indices (z * width + x), in no particular order.</summary>
        public IReadOnlyList<int> ActiveCells => _active;

        public int DumpZoneCount => _dumpCells.Count;

        /// <summary>Dump Zone cells as grid indices, in no particular order.</summary>
        public IReadOnlyList<int> DumpZoneCells => _dumpCells;

        public TerrainGrid Grid => _grid;

        public DesignationKind GetKind(int x, int z) => _kinds[Index(x, z)];

        public float GetTarget(int x, int z) => _targets[Index(x, z)];

        /// <summary>Whether the cell's designation was made by a unit rather than the player.</summary>
        public bool IsAuto(int x, int z) => _auto[Index(x, z)];

        public bool IsDumpZone(int x, int z) => _dumpSlot[Index(x, z)] >= 0;

        /// <summary>The height nothing may be heaped above on this Dump Zone cell; infinite if uncapped.</summary>
        public float DumpZoneCap(int x, int z) => _dumpCap[Index(x, z)];

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
        /// the target: there is nothing to do. Replaces any previous designation on the cell; a
        /// player designation (<paramref name="auto"/> false) replacing an Auto one counts as
        /// cancelling it.
        /// </summary>
        public bool Designate(int x, int z, DesignationKind kind, float height, bool auto = false)
        {
            if (kind == DesignationKind.None)
                throw new ArgumentException("Use Clear to remove a designation.", nameof(kind));

            var cell = Index(x, z);
            var cancelsAuto = _auto[cell] && !auto;
            if (Satisfies(x, z, kind, height))
            {
                Clear(x, z);
                if (cancelsAuto)
                    AutoCancelled?.Invoke(x, z);
                return false;
            }

            if (_kinds[cell] == kind && _targets[cell] == height && _auto[cell] == auto)
                return true;

            _kinds[cell] = kind;
            _targets[cell] = height;
            SetAuto(cell, auto);
            if (_slot[cell] < 0)
            {
                _slot[cell] = _active.Count;
                _active.Add(cell);
            }

            Raise(x, z);
            if (cancelsAuto)
                AutoCancelled?.Invoke(x, z);
            return true;
        }

        /// <summary>
        /// The player's clear: removes the cell's designation, Auto or not, and its Dump Zone.
        /// Removing an Auto designation raises <see cref="AutoCancelled"/>. Returns whether there
        /// was anything to remove.
        /// </summary>
        public bool Cancel(int x, int z)
        {
            var cell = Index(x, z);
            var hadDesignation = _kinds[cell] != DesignationKind.None;
            var wasAuto = _auto[cell];
            Clear(x, z);
            var hadZone = SetDumpZone(x, z, false);
            if (hadDesignation && wasAuto)
                AutoCancelled?.Invoke(x, z);
            return hadDesignation || hadZone;
        }

        /// <summary>Removes the cell's designation, met or not, without counting as a cancel.</summary>
        public void Clear(int x, int z)
        {
            var cell = Index(x, z);
            if (_kinds[cell] == DesignationKind.None)
                return;

            _kinds[cell] = DesignationKind.None;
            _targets[cell] = 0f;
            SetAuto(cell, false);
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

            for (var i = _dumpCells.Count - 1; i >= 0; i--)
            {
                var cell = _dumpCells[i];
                SetDumpZone(cell % _grid.Width, cell / _grid.Width, false);
            }
        }

        /// <summary>
        /// Marks or unmarks a Dump Zone cell, with the height spoil may be heaped to. Returns
        /// whether anything changed; re-marking a cell with a new cap counts as a change.
        /// </summary>
        public bool SetDumpZone(int x, int z, bool on, float cap = float.PositiveInfinity)
        {
            var cell = Index(x, z);
            if ((_dumpSlot[cell] >= 0) == on)
            {
                if (!on || _dumpCap[cell] == cap)
                    return false;
                _dumpCap[cell] = cap;
                Raise(x, z);
                return true;
            }

            if (on)
            {
                _dumpCap[cell] = cap;
                _dumpSlot[cell] = _dumpCells.Count;
                _dumpCells.Add(cell);
            }
            else
            {
                var slot = _dumpSlot[cell];
                var last = _dumpCells[_dumpCells.Count - 1];
                _dumpCells[slot] = last;
                _dumpSlot[last] = slot;
                _dumpCells.RemoveAt(_dumpCells.Count - 1);
                _dumpSlot[cell] = -1;
                _dumpCap[cell] = float.PositiveInfinity;

            }

            Raise(x, z);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
        }

        /// <summary>
        /// Drops designations whose cells have met their targets and still meet them. Tipping and
        /// digging are followed by slump, which can undo what a single change looked like it had
        /// achieved, so a designation is only met once the ground has settled: the dispatcher
        /// calls this once a frame, after the last frame's slumping.
        /// </summary>
        public void Prune()
        {
            for (var i = 0; i < _pendingMet.Count; i++)
            {
                var cell = _pendingMet[i];
                _pending[cell] = false;
                var x = cell % _grid.Width;
                var z = cell / _grid.Width;
                if (_kinds[cell] != DesignationKind.None && Satisfies(x, z, _kinds[cell], _targets[cell]))
                    Clear(x, z);
            }

            _pendingMet.Clear();
        }

        void OnCellChanged(int x, int z)
        {
            var cell = z * _grid.Width + x;
            if (_kinds[cell] == DesignationKind.None || _pending[cell] || !Satisfies(x, z, _kinds[cell], _targets[cell]))
                return;
            _pending[cell] = true;
            _pendingMet.Add(cell);
        }

        void SetAuto(int cell, bool auto)
        {
            if (_auto[cell] == auto)
                return;
            _auto[cell] = auto;
            AutoCount += auto ? 1 : -1;
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
