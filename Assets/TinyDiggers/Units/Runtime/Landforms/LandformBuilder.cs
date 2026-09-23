using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Turns the plan into orders for the crew, and keeps them honest when the plan changes.
    ///
    /// Committing is a *diff*, not a rewrite: the builder remembers the height it asked of every
    /// cell, so a re-commit designates what the plan now wants and cancels only what the plan has
    /// let go of. That is what makes a shape re-editable — move a pad and the ground it has left
    /// stops being work, without touching a cell the pad never covered.
    ///
    /// Two details are lifted from <see cref="RoadBuilder"/>, where they were learned the hard way:
    ///
    /// - **Only cancel what is still ours.** A cell whose designation the player has since changed
    ///   by hand no longer holds the height the plan wrote, and is left alone. Anything else quietly
    ///   undoes the player's own work.
    /// - **A cell can only be let go once.** The plan is composed before it reaches here, so a cell
    ///   two shapes share arrives once, with the winning shape's height — the reason the diff is
    ///   over the whole plan rather than one shape at a time.
    ///
    /// Re-editing never un-digs (Ronan, 2026-09-23): what the crew have done stays done, and the
    /// plan is worked out afresh against the ground as it now stands.
    /// </summary>
    public sealed class LandformBuilder
    {
        /// <summary>How close a designation has to be to what we wrote to count as still ours.</summary>
        const float Ours = 1e-4f;

        readonly TerrainGrid _grid;
        readonly DesignationMap _map;
        readonly Dictionary<int, float> _committed = new Dictionary<int, float>();

        public LandformBuilder(TerrainGrid grid, DesignationMap map)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _map = map ?? throw new ArgumentNullException(nameof(map));
        }

        /// <summary>Cells the plan is currently asking for.</summary>
        public int Count => _committed.Count;

        /// <summary>Whether the plan has asked anything of this cell.</summary>
        public bool Holds(int x, int z) => _committed.ContainsKey(z * _grid.Width + x);

        /// <summary>
        /// Puts <paramref name="cells"/> — a rasterised plan — to the crew, and takes back whatever
        /// the last commit asked for and this one does not. Returns how many designations were made;
        /// a cell the ground already satisfies makes none, which is
        /// <see cref="DesignationMap.Designate"/> refusing to invent work.
        /// </summary>
        public int Commit(IReadOnlyList<PlannedCell> cells)
        {
            if (cells == null)
                throw new ArgumentNullException(nameof(cells));

            var width = _grid.Width;
            var next = new Dictionary<int, float>(cells.Count);
            foreach (var cell in cells)
                if (cell.IsDig || cell.IsFill)
                    next[cell.Z * width + cell.X] = cell.Height;

            Release(next);

            _committed.Clear();
            var made = 0;
            foreach (var cell in cells)
            {
                if (!cell.IsDig && !cell.IsFill)
                    continue;
                var kind = cell.IsDig ? DesignationKind.Dig : DesignationKind.Fill;
                if (_map.Designate(cell.X, cell.Z, kind, cell.Height))
                    made++;
                _committed[cell.Z * width + cell.X] = cell.Height;
            }

            return made;
        }

        /// <summary>Takes the whole plan back off the crew.</summary>
        public void Clear()
        {
            Release(null);
            _committed.Clear();
        }

        /// <summary>
        /// Cancels every cell the last commit asked for that <paramref name="next"/> does not, and
        /// only where the designation is still the one we wrote.
        /// </summary>
        void Release(Dictionary<int, float> next)
        {
            var width = _grid.Width;
            foreach (var pair in _committed)
            {
                if (next != null && next.ContainsKey(pair.Key))
                    continue;
                var x = pair.Key % width;
                var z = pair.Key / width;
                if (_map.GetKind(x, z) == DesignationKind.None)
                    continue;   // already done, or already cleared
                if (Math.Abs(_map.GetTarget(x, z) - pair.Value) >= Ours)
                    continue;   // the player has changed it since: not ours to take back
                _map.CancelDesignation(x, z);
            }
        }
    }
}
