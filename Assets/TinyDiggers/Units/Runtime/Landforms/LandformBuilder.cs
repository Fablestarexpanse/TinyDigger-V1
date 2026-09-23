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
        readonly Dictionary<int, float> _caps = new Dictionary<int, float>();
        readonly Dictionary<int, float> _floors = new Dictionary<int, float>();

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

        /// <summary>
        /// Puts the heaps' caps and the pits' floors where the crew will find them. Both go in per
        /// cell, which `DesignationMap` has always allowed and no tool has ever used: that is the
        /// whole of what makes a heap a heap instead of a slab, and a pit benched instead of a box.
        ///
        /// A heap's cap goes in as **no cap at all**. The crown is what the player drew, not a wall
        /// (Ronan's ruling): the crew tip into the lowest cell of the zone as they already do, so
        /// the heap fills evenly and takes the drawn shape by itself, and when it passes the crown it
        /// keeps piling and spreads at the repose angle, which the slump does for free.
        /// </summary>
        public void CommitZones(IReadOnlyDictionary<int, float> caps, IReadOnlyDictionary<int, float> floors)
        {
            var width = _grid.Width;

            foreach (var pair in _caps)
                if (caps == null || !caps.ContainsKey(pair.Key))
                    _map.SetDumpZone(pair.Key % width, pair.Key / width, false);
            foreach (var pair in _floors)
                if (floors == null || !floors.ContainsKey(pair.Key))
                    _map.SetQuarry(pair.Key % width, pair.Key / width, false);

            _caps.Clear();
            _floors.Clear();
            if (caps != null)
                foreach (var pair in caps)
                {
                    _map.SetDumpZone(pair.Key % width, pair.Key / width, true);
                    _caps[pair.Key] = pair.Value;
                }

            if (floors != null)
                foreach (var pair in floors)
                {
                    _map.SetQuarry(pair.Key % width, pair.Key / width, true, pair.Value);
                    _floors[pair.Key] = pair.Value;
                }
        }

        /// <summary>Takes the whole plan back off the crew.</summary>
        public void Clear()
        {
            Release(null);
            _committed.Clear();
            CommitZones(null, null);
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
