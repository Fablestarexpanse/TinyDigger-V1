using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace PromptWaffle.DynamicWater
{
    /// <summary>What the water is doing at one point.</summary>
    public struct WaterSample
    {
        /// <summary>World height of the water surface, metres. Equals the ground where dry.</summary>
        public float Surface;

        /// <summary>Metres of water.</summary>
        public float Depth;

        /// <summary>Flow velocity in world x, z, m/s.</summary>
        public Vector2 Velocity;

        public bool IsWet => Depth > 0.001f;
    }

    /// <summary>
    /// Gameplay's view of a zone's water: height, depth and flow at any world point, from a copy
    /// of <see cref="WaterSimulation.State"/> read back from the GPU without stalling it. The copy
    /// is a few frames old and refreshed about every <see cref="Interval"/> seconds, which is fine
    /// for buoyancy, AI and "is this cell flooded" but not for pixel-exact effects (those sample
    /// the state texture in a shader).
    ///
    /// The state is read a band of rows at a time, at most <see cref="BandBytes"/> a request and
    /// <see cref="MaxInFlight"/> requests at once, sweeping the zone from the first row to the
    /// last. A small zone is one band, read whole. A big one is never copied in one go: a 3104²
    /// zone is 154 MB, and reading it whole five times a second stalled the GPU for up to 42 ms.
    /// </summary>
    public sealed class WaterQuery : IDisposable
    {
        readonly WaterSimulation _simulation;
        NativeArray<Vector4> _snapshot;
        bool _complete;
        bool _sweeping;
        bool _sweepFailed;
        bool _disposed;
        int _nextRow;
        int _rowsLanded;
        int _inFlight;
        float _sweepStarted = float.NegativeInfinity;

        internal WaterQuery(WaterSimulation simulation)
        {
            _simulation = simulation;
        }

        /// <summary>Least seconds from the start of one sweep of the zone to the start of the next.</summary>
        public float Interval = 0.2f;

        /// <summary>Most bytes one readback request carries (16 a cell).</summary>
        public int BandBytes = 4 << 20;

        /// <summary>Most band requests waiting on the GPU at once.</summary>
        public int MaxInFlight = 2;

        /// <summary>Rows of the zone one band holds.</summary>
        public int RowsPerBand => Mathf.Clamp(BandBytes / Mathf.Max(1, _simulation.Desc.Width * 16), 1, _simulation.Desc.Height);

        /// <summary>Whether a whole sweep has arrived yet.</summary>
        public bool HasData => _complete;

        /// <summary>
        /// The whole snapshot: one (surface, depth, velocity x, velocity z) per cell, indexed
        /// <c>z * Width + x</c> of the simulation's <see cref="WaterSimulation.Desc"/>. Empty before
        /// the first. For game logic that wants every cell at once, such as a grid's flood map.
        /// Rows may be up to one sweep apart in age.
        /// </summary>
        public NativeArray<Vector4>.ReadOnly Snapshot => _complete ? _snapshot.AsReadOnly() : default;

        /// <summary>Time.realtimeSinceStartup at which the last whole sweep finished landing.</summary>
        public float SnapshotTime { get; private set; }

        /// <summary>Whole sweeps landed so far.</summary>
        public int Sweeps { get; private set; }

        /// <summary>Starts a sweep when <see cref="Interval"/> has passed, and keeps the current one's bands flowing. Call once a frame.</summary>
        public void Update()
        {
            if (_disposed || !SystemInfo.supportsAsyncGPUReadback || _simulation.State == null)
                return;
            var desc = _simulation.Desc;
            var now = Time.realtimeSinceStartup;
            if (!_sweeping)
            {
                if (now - _sweepStarted < Interval)
                    return;
                _sweeping = true;
                _sweepStarted = now;
                _nextRow = 0;
                _rowsLanded = 0;
                _sweepFailed = false;
            }

            if (!_snapshot.IsCreated)
                _snapshot = new NativeArray<Vector4>(desc.Width * desc.Height, Allocator.Persistent);

            var rows = RowsPerBand;
            while (_inFlight < Mathf.Max(1, MaxInFlight) && _nextRow < desc.Height)
            {
                var first = _nextRow;
                var count = Mathf.Min(rows, desc.Height - first);
                _nextRow += count;
                _inFlight++;
                AsyncGPUReadback.Request(_simulation.State, 0, 0, desc.Width, first, count, 0, 1,
                    request => OnBand(request, first, count));
            }
        }

        void OnBand(AsyncGPUReadbackRequest request, int firstRow, int rows)
        {
            _inFlight--;
            if (_disposed || !_snapshot.IsCreated)
                return;
            var width = _simulation.Desc.Width;
            // A failed band keeps its old rows; the sweep still ends, and the next one retries.
            if (request.hasError)
                _sweepFailed = true;
            else
                NativeArray<Vector4>.Copy(request.GetData<Vector4>(), 0, _snapshot, firstRow * width, rows * width);
            _rowsLanded += rows;
            if (_rowsLanded < _simulation.Desc.Height)
                return;
            _sweeping = false;
            // The first snapshot has to be whole: until then a failed band would leave zeros.
            _complete |= !_sweepFailed;
            SnapshotTime = Time.realtimeSinceStartup;
            Sweeps++;
        }

        /// <summary>
        /// The water at world (x, z), bilinear between cell centres. False outside the zone or
        /// before the first snapshot.
        /// </summary>
        public bool TrySample(Vector3 world, out WaterSample sample)
        {
            sample = default;
            if (!_complete)
                return false;
            var desc = _simulation.Desc;
            var fx = (world.x - desc.Origin.x) / desc.CellSize - 0.5f;
            var fz = (world.z - desc.Origin.y) / desc.CellSize - 0.5f;
            if (fx < -0.5f || fz < -0.5f || fx > desc.Width - 0.5f || fz > desc.Height - 0.5f)
                return false;
            fx = Mathf.Clamp(fx, 0f, desc.Width - 1.001f);
            fz = Mathf.Clamp(fz, 0f, desc.Height - 1.001f);
            var x = (int)fx;
            var z = (int)fz;
            var tx = fx - x;
            var tz = fz - z;
            var a = Cell(x, z, desc.Width);
            var b = Cell(x + 1, z, desc.Width);
            var c = Cell(x, z + 1, desc.Width);
            var d = Cell(x + 1, z + 1, desc.Width);
            var v = Vector4.Lerp(Vector4.Lerp(a, b, tx), Vector4.Lerp(c, d, tx), tz);
            sample = new WaterSample { Surface = v.x, Depth = v.y, Velocity = new Vector2(v.z, v.w) };
            return true;
        }

        Vector4 Cell(int x, int z, int width) => _snapshot[z * width + x];

        public void Dispose()
        {
            _disposed = true;
            if (_snapshot.IsCreated)
                _snapshot.Dispose();
        }
    }
}
