using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The height the ground is *drawn* at, easing toward the height the simulation says it is
    /// (Ronan, 2026-09-23: "the renderer keeps a per-vertex displayed height that eases toward the
    /// sim height"). Dropped cells sink into place and raised cells swell, instead of a cut
    /// appearing between one frame and the next.
    ///
    /// It has to be the drawing and not the simulation, because the simulation has almost nothing
    /// to pace: a 0.75 m³ scoop tipped on flat ground moves **two cells**, and settles in a single
    /// frame at any budget worth having (measured 2026-09-23). Slowing the slump cannot make a
    /// load run down a heap; only the drawn surface can.
    ///
    /// Only cells that have moved recently are in here — a handful at a time while the crew work —
    /// so the cost is a dictionary probe per cell of a chunk that was being rebuilt anyway.
    /// </summary>
    public sealed class TerrainHeightLag : IDisposable
    {
        /// <summary>Roughly how long a cell takes to catch up, in seconds.</summary>
        public float Seconds = 0.25f;

        /// <summary>
        /// How close to the real height a cell has to get before it stops being drawn separately.
        /// A millimetre: the grid snaps its own heights to one, so anything finer never arrives.
        /// </summary>
        public float Settled = 0.001f;

        /// <summary>
        /// Longest a cell may lag, however far it still has to go. A cell that is being dug every
        /// second would otherwise never settle and never leave the set.
        /// </summary>
        public float GiveUpAfterSeconds = 1f;

        readonly TerrainGrid _grid;
        readonly Dictionary<int, Lagging> _lagging = new Dictionary<int, Lagging>();
        readonly List<int> _done = new List<int>();
        readonly List<int> _cells = new List<int>();
        bool _disposed;

        struct Lagging
        {
            public float Drawn;
            public float Velocity;
            public float Age;
        }

        public TerrainHeightLag(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _grid.CellHeightChanged += Note;
        }

        /// <summary>How many cells are being drawn away from their real height.</summary>
        public int Count => _lagging.Count;

        /// <summary>Whether anything is lagging at all, for callers that can skip their work entirely.</summary>
        public bool Any => _lagging.Count > 0;

        /// <summary>
        /// The height to draw cell <paramref name="cell"/> at, given the height it really is.
        /// Cells that are not lagging are drawn where they are, which is all of them most of the
        /// time.
        /// </summary>
        public float Drawn(int cell, float actual) =>
            _lagging.TryGetValue(cell, out var lagging) ? lagging.Drawn : actual;

        /// <summary>
        /// Eases every lagging cell toward its real height and drops the ones that have arrived.
        /// The cells still lagging afterwards are added to <paramref name="into"/>, so whatever
        /// draws them can rebuild exactly those and nothing else.
        /// </summary>
        public void Tick(float deltaTime, List<int> into)
        {
            if (_lagging.Count == 0 || deltaTime <= 0f)
                return;

            // Over a copy of the keys, not the dictionary: writing a value back while enumerating
            // is allowed on modern .NET and throws on the runtime Unity ships (2026-09-23).
            _done.Clear();
            _cells.Clear();
            foreach (var cell in _lagging.Keys)
                _cells.Add(cell);

            foreach (var cell in _cells)
            {
                var lagging = _lagging[cell];
                var actual = _grid.SurfaceHeights[cell];
                lagging.Age += deltaTime;
                lagging.Drawn = Mathf.SmoothDamp(lagging.Drawn, actual, ref lagging.Velocity,
                    Mathf.Max(0.0001f, Seconds), float.PositiveInfinity, deltaTime);

                if (Mathf.Abs(lagging.Drawn - actual) <= Settled || lagging.Age >= GiveUpAfterSeconds)
                {
                    _done.Add(cell);
                    continue;
                }

                _lagging[cell] = lagging;
                into?.Add(cell);
            }

            // A cell that has arrived still needs drawing once more, at the height it arrived at.
            foreach (var cell in _done)
            {
                _lagging.Remove(cell);
                into?.Add(cell);
            }
        }

        /// <summary>Forgets every lagging cell, so the ground is drawn where it is.</summary>
        public void Clear() => _lagging.Clear();

        void Note(int x, int z, float was, bool wasBlocked)
        {
            var cell = z * _grid.Width + x;
            if (_lagging.ContainsKey(cell))
                return;   // already easing: it keeps going from where it is, toward the new height

            _lagging[cell] = new Lagging { Drawn = was, Velocity = 0f, Age = 0f };
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellHeightChanged -= Note;
            _lagging.Clear();
        }
    }
}
