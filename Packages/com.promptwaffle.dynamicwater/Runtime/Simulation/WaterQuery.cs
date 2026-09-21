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
    /// is a few frames old and refreshed every <see cref="Interval"/> seconds, which is fine for
    /// buoyancy, AI and "is this cell flooded" but not for pixel-exact effects (those sample the
    /// state texture in a shader).
    /// </summary>
    public sealed class WaterQuery : IDisposable
    {
        readonly WaterSimulation _simulation;
        NativeArray<Vector4> _snapshot;
        bool _pending;
        float _lastRequest = float.NegativeInfinity;

        internal WaterQuery(WaterSimulation simulation)
        {
            _simulation = simulation;
        }

        /// <summary>Seconds between readbacks.</summary>
        public float Interval = 0.2f;

        /// <summary>Whether a snapshot has arrived yet.</summary>
        public bool HasData => _snapshot.IsCreated;

        /// <summary>
        /// The whole snapshot: one (surface, depth, velocity x, velocity z) per cell, indexed
        /// <c>z * Width + x</c> of the simulation's <see cref="WaterSimulation.Desc"/>. Empty before
        /// the first. For game logic that wants every cell at once, such as a grid's flood map.
        /// </summary>
        public NativeArray<Vector4>.ReadOnly Snapshot => _snapshot.IsCreated ? _snapshot.AsReadOnly() : default;

        /// <summary>Time.realtimeSinceStartup of the snapshot now held.</summary>
        public float SnapshotTime { get; private set; }

        /// <summary>Asks for a fresh snapshot if <see cref="Interval"/> has passed and none is in flight. Call once a frame.</summary>
        public void Update()
        {
            var now = Time.realtimeSinceStartup;
            if (_pending || now - _lastRequest < Interval || !SystemInfo.supportsAsyncGPUReadback)
                return;
            _pending = true;
            _lastRequest = now;
            AsyncGPUReadback.Request(_simulation.State, 0, OnReadback);
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            _pending = false;
            if (request.hasError || _simulation.State == null)
                return;
            var data = request.GetData<Vector4>();
            if (!_snapshot.IsCreated || _snapshot.Length != data.Length)
            {
                if (_snapshot.IsCreated)
                    _snapshot.Dispose();
                _snapshot = new NativeArray<Vector4>(data.Length, Allocator.Persistent);
            }

            _snapshot.CopyFrom(data);
            SnapshotTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// The water at world (x, z), bilinear between cell centres. False outside the zone or
        /// before the first snapshot.
        /// </summary>
        public bool TrySample(Vector3 world, out WaterSample sample)
        {
            sample = default;
            if (!_snapshot.IsCreated)
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
            if (_snapshot.IsCreated)
                _snapshot.Dispose();
        }
    }
}
