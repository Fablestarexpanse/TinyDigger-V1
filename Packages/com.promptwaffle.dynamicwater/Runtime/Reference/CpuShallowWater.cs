using System;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// The simulation on the CPU: the same "virtual pipes" shallow-water model as
    /// ShallowWater.compute, step for step, in plain C#. It is the reference the GPU version is
    /// tested against, and small zones or tools can run it directly.
    ///
    /// The model (after O'Brien and Hodgins, and Mei et al.): each cell holds a water depth over
    /// its ground, and a pipe to each of its four neighbours carries an outflow, in m³/s. Each step:
    /// 1. Flux: every pipe's outflow is kept (times <see cref="WaterSimulationDesc.FlowRetention"/>)
    ///    and pushed by the difference in water surface height across it (gravity × area × Δh / l),
    ///    never below zero; force effectors add along their direction. If the outflows would take
    ///    more water than the cell holds in one step, they are all scaled down to exactly what it
    ///    holds, which is what keeps depth from going negative.
    /// 2. Depth: each cell gains what its neighbours' pipes send in and loses what its own send out,
    ///    so water is only ever moved, never made or lost. Then sources, drains and levels apply.
    /// 3. Velocity: from the average flow through the cell each way, over the depth.
    /// Walls (<see cref="WaterGround.Wall"/>) and the zone's edge are closed pipes.
    /// </summary>
    public sealed class CpuShallowWater
    {
        const int Left = 0, Right = 1, Down = 2, Up = 3;

        readonly WaterSimulationDesc _desc;
        readonly float[] _ground;
        readonly float[] _depth;
        readonly Vector4[] _flux;
        readonly Vector2[] _velocity;
        WaterEffector[] _effectors = Array.Empty<WaterEffector>();

        public CpuShallowWater(WaterSimulationDesc desc, float[] ground)
        {
            desc.Validate();
            if (ground == null || ground.Length != desc.CellCount)
                throw new ArgumentException("Ground must hold one height per cell.", nameof(ground));
            _desc = desc;
            _ground = (float[])ground.Clone();
            _depth = new float[desc.CellCount];
            _flux = new Vector4[desc.CellCount];
            _velocity = new Vector2[desc.CellCount];
        }

        public WaterSimulationDesc Desc => _desc;

        public float DepthAt(int x, int z) => _depth[z * _desc.Width + x];

        public float SurfaceAt(int x, int z) => _ground[z * _desc.Width + x] + _depth[z * _desc.Width + x];

        public Vector2 VelocityAt(int x, int z) => _velocity[z * _desc.Width + x];

        public void SetDepth(int x, int z, float depth) => _depth[z * _desc.Width + x] = Mathf.Max(0f, depth);

        public void SetGround(int x, int z, float height)
        {
            var i = z * _desc.Width + x;
            _ground[i] = height;
            if (WaterGround.IsWall(height))
                _depth[i] = 0f;
        }

        /// <summary>Fills every open cell below <paramref name="level"/> up to it.</summary>
        public void FillTo(float level)
        {
            for (var i = 0; i < _depth.Length; i++)
                _depth[i] = WaterGround.IsWall(_ground[i]) ? 0f : Mathf.Max(0f, level - _ground[i]);
        }

        public void SetEffectors(ReadOnlySpan<WaterEffector> effectors)
        {
            _effectors = effectors.ToArray();
            for (var i = 0; i < _effectors.Length; i++)
                _effectors[i].Prepare(_desc);
        }

        /// <summary>Cubic metres of water in the zone.</summary>
        public double TotalVolume()
        {
            double sum = 0;
            foreach (var d in _depth)
                sum += d;
            return sum * _desc.CellArea;
        }

        public void Step()
        {
            var dt = _desc.FixedStep;
            var w = _desc.Width;
            var h = _desc.Height;
            var area = _desc.CellArea;
            var pipe = area * _desc.Gravity / _desc.CellSize;

            // 1. Flux.
            for (var z = 0; z < h; z++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = z * w + x;
                    if (WaterGround.IsWall(_ground[i]))
                    {
                        _flux[i] = Vector4.zero;
                        continue;
                    }

                    var surface = _ground[i] + _depth[i];
                    var f = _flux[i] * _desc.FlowRetention;
                    f.x = Outflow(f.x, surface, x - 1, z, pipe, dt);
                    f.y = Outflow(f.y, surface, x + 1, z, pipe, dt);
                    f.z = Outflow(f.z, surface, x, z - 1, pipe, dt);
                    f.w = Outflow(f.w, surface, x, z + 1, pipe, dt);
                    f += ForceFlux(_desc.CellCentre(x, z), _depth[i], dt);
                    f = ClosePipes(f, x, z);

                    var total = f.x + f.y + f.z + f.w;
                    var available = _depth[i] * area;
                    if (total * dt > available && total > 0f)
                        f *= available / (total * dt);
                    _flux[i] = f;
                }
            }

            // 2. Depth.
            for (var z = 0; z < h; z++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = z * w + x;
                    if (WaterGround.IsWall(_ground[i]))
                        continue;
                    var inflow = (x > 0 ? _flux[i - 1].y : 0f) + (x < w - 1 ? _flux[i + 1].x : 0f)
                               + (z > 0 ? _flux[i - w].w : 0f) + (z < h - 1 ? _flux[i + w].z : 0f);
                    var f = _flux[i];
                    var outflow = f.x + f.y + f.z + f.w;
                    var depth = _depth[i] + dt * (inflow - outflow) / area;
                    _depth[i] = Mathf.Max(0f, ApplyEffectors(depth, _ground[i], _desc.CellCentre(x, z), dt));
                }
            }

            // 3. Velocity.
            for (var z = 0; z < h; z++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = z * w + x;
                    var d = _depth[i];
                    if (d <= _desc.DryDepth)
                    {
                        _velocity[i] = Vector2.zero;
                        continue;
                    }

                    var f = _flux[i];
                    var fromLeft = x > 0 ? _flux[i - 1].y : 0f;
                    var fromRight = x < w - 1 ? _flux[i + 1].x : 0f;
                    var fromDown = z > 0 ? _flux[i - w].w : 0f;
                    var fromUp = z < h - 1 ? _flux[i + w].z : 0f;
                    var flowX = 0.5f * (fromLeft - f.x + f.y - fromRight);
                    var flowZ = 0.5f * (fromDown - f.z + f.w - fromUp);
                    var v = new Vector2(flowX, flowZ) / (_desc.CellSize * d);
                    _velocity[i] = Vector2.ClampMagnitude(v, _desc.MaxSpeed);
                }
            }
        }

        float Outflow(float current, float surface, int nx, int nz, float pipe, float dt)
        {
            if (nx < 0 || nz < 0 || nx >= _desc.Width || nz >= _desc.Height)
                return 0f;
            var n = nz * _desc.Width + nx;
            if (WaterGround.IsWall(_ground[n]))
                return 0f;
            var difference = surface - (_ground[n] + _depth[n]);
            return Mathf.Max(0f, current + dt * pipe * difference);
        }

        Vector4 ClosePipes(Vector4 f, int x, int z)
        {
            var w = _desc.Width;
            var i = z * w + x;
            if (x == 0 || WaterGround.IsWall(_ground[i - 1])) f.x = 0f;
            if (x == w - 1 || WaterGround.IsWall(_ground[i + 1])) f.y = 0f;
            if (z == 0 || WaterGround.IsWall(_ground[i - w])) f.z = 0f;
            if (z == _desc.Height - 1 || WaterGround.IsWall(_ground[i + w])) f.w = 0f;
            return new Vector4(Mathf.Max(0f, f.x), Mathf.Max(0f, f.y), Mathf.Max(0f, f.z), Mathf.Max(0f, f.w));
        }

        Vector4 ForceFlux(Vector2 centre, float depth, float dt)
        {
            var result = Vector4.zero;
            if (depth <= _desc.DryDepth)
                return result;
            foreach (var e in _effectors)
            {
                if (e.Kind != (int)WaterEffectorKind.Force)
                    continue;
                var weight = WaterEffector.Weight(Vector2.Distance(centre, e.Position), e.Radius);
                if (weight <= 0f)
                    continue;
                // Acceleration × dt is a speed; × the pipe's cross-section (depth × cell) a flow.
                var push = e.Direction * (e.Rate * weight * dt * depth * _desc.CellSize);
                result += new Vector4(Mathf.Max(0f, -push.x), Mathf.Max(0f, push.x), Mathf.Max(0f, -push.y), Mathf.Max(0f, push.y));
            }

            return result;
        }

        float ApplyEffectors(float depth, float ground, Vector2 centre, float dt)
        {
            foreach (var e in _effectors)
            {
                var weight = WaterEffector.Weight(Vector2.Distance(centre, e.Position), e.Radius);
                if (weight <= 0f)
                    continue;
                switch ((WaterEffectorKind)e.Kind)
                {
                    case WaterEffectorKind.Source:
                        depth += e.Rate * weight * e.Scale * dt;
                        break;
                    case WaterEffectorKind.Drain:
                        depth = Mathf.Max(0f, depth - e.Rate * weight * e.Scale * dt);
                        break;
                    case WaterEffectorKind.Level:
                        var gap = e.Level - (ground + depth);
                        depth += gap * Mathf.Clamp01(e.Rate * dt) * weight;
                        break;
                    case WaterEffectorKind.Overflow:
                        var head = Mathf.Min(ground + depth - e.Level, depth);
                        if (head > 0f)
                            depth -= Mathf.Min(head, WaterEffector.WeirRate(e.Rate, head) * weight * e.Scale * dt);
                        break;
                }
            }

            return depth;
        }
    }
}
