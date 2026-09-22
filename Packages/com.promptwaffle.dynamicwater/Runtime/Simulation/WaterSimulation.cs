using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// One shallow-water simulation zone on the GPU. Plain C#: it owns its compute buffers and
    /// runs ShallowWater.compute, and knows nothing about scenes, pipelines or renderers. A
    /// <see cref="WaterZone"/> component drives it in a scene; tools and tests can drive it directly.
    ///
    /// Each frame <see cref="Step"/> runs whole fixed steps for the time passed, up to
    /// <see cref="WaterSimulationDesc.MaxStepsPerFrame"/>, then publishes <see cref="State"/>: a
    /// float texture of (surface height, depth, velocity x, velocity z) per cell, which renderers
    /// sample and <see cref="WaterQuery"/> reads back for gameplay.
    /// </summary>
    public sealed class WaterSimulation : IDisposable
    {
        static readonly int GroundId = Shader.PropertyToID("_Ground");
        static readonly int DepthId = Shader.PropertyToID("_Depth");
        static readonly int FluxId = Shader.PropertyToID("_Flux");
        static readonly int EffectorsId = Shader.PropertyToID("_Effectors");
        static readonly int StateId = Shader.PropertyToID("_State");
        static readonly int WidthId = Shader.PropertyToID("_Width");
        static readonly int HeightId = Shader.PropertyToID("_Height");
        static readonly int EffectorCountId = Shader.PropertyToID("_EffectorCount");
        static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        static readonly int OriginId = Shader.PropertyToID("_Origin");
        static readonly int DtId = Shader.PropertyToID("_Dt");
        static readonly int PipeId = Shader.PropertyToID("_Pipe");
        static readonly int RetentionId = Shader.PropertyToID("_Retention");
        static readonly int DryDepthId = Shader.PropertyToID("_DryDepth");
        static readonly int MaxSpeedId = Shader.PropertyToID("_MaxSpeed");
        static readonly int LevelId = Shader.PropertyToID("_Level");
        static readonly int SoakId = Shader.PropertyToID("_Soak");
        static readonly int SoakDepthId = Shader.PropertyToID("_SoakDepth");
        static readonly int SoakRateId = Shader.PropertyToID("_SoakRate");
        static readonly int HasSoakId = Shader.PropertyToID("_HasSoak");

        const string KernelPath = "PromptWaffle/ShallowWater";
        const int MaxEffectors = 256;

        readonly WaterSimulationDesc _desc;
        readonly IWaterGround _ground;
        readonly ComputeShader _shader;
        readonly int _fluxKernel, _depthKernel, _velocityKernel, _fillKernel;
        readonly int _groupsX, _groupsZ;

        readonly ComputeBuffer _groundBuffer;
        readonly ComputeBuffer _depthBuffer;
        readonly ComputeBuffer _fluxBuffer;
        readonly ComputeBuffer _effectorBuffer;
        ComputeBuffer _soakBuffer;
        readonly float[] _groundRow;
        readonly WaterEffector[] _effectors = new WaterEffector[MaxEffectors];
        readonly List<Rect> _pendingGround = new List<Rect>();
        int _effectorCount;
        float _accumulated;
        bool _disposed;

        public WaterSimulation(WaterSimulationDesc desc, IWaterGround ground)
        {
            desc.Validate();
            _desc = desc;
            _ground = ground ?? throw new ArgumentNullException(nameof(ground));
            if (!SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("PromptWaffle Dynamic Water needs compute shaders.");
            var source = Resources.Load<ComputeShader>(KernelPath);
            if (source == null)
                throw new InvalidOperationException($"Compute shader Resources/{KernelPath} is missing.");
            // Its own copy: a compute shader's parameters and bound buffers belong to the asset,
            // so two zones sharing one would run on each other's data.
            _shader = UnityEngine.Object.Instantiate(source);
            _shader.hideFlags = HideFlags.DontSave;

            _fluxKernel = _shader.FindKernel("Flux");
            _depthKernel = _shader.FindKernel("Depth");
            _velocityKernel = _shader.FindKernel("Velocity");
            _fillKernel = _shader.FindKernel("Fill");
            _groupsX = (desc.Width + 7) / 8;
            _groupsZ = (desc.Height + 7) / 8;

            var cells = desc.CellCount;
            _groundBuffer = new ComputeBuffer(cells, sizeof(float));
            _depthBuffer = new ComputeBuffer(cells, sizeof(float));
            _fluxBuffer = new ComputeBuffer(cells, sizeof(float) * 4);
            _effectorBuffer = new ComputeBuffer(MaxEffectors, WaterEffector.Stride);
            // Bound but unread until a mask is set: a compute shader needs every buffer it names.
            _soakBuffer = new ComputeBuffer(1, sizeof(float));
            _depthBuffer.SetData(new float[cells]);
            _fluxBuffer.SetData(new Vector4[cells]);
            _groundRow = new float[desc.Width];

            State = new RenderTexture(desc.Width, desc.Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "PromptWaffle Water State",
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            State.Create();

            UploadGround(new RectInt(0, 0, desc.Width, desc.Height));
            _ground.Changed += OnGroundChanged;
            Bind();
            Query = new WaterQuery(this);
            Publish();
        }

        public WaterSimulationDesc Desc => _desc;

        /// <summary>(surface height, depth, velocity x, velocity z) per cell, bilinear. Walls read a surface of -5e5.</summary>
        public RenderTexture State { get; }

        public WaterQuery Query { get; }

        /// <summary>Steps run by the last <see cref="Step"/>.</summary>
        public int LastSteps { get; private set; }

        /// <summary>Seconds of simulation dropped because a frame needed more than the step cap.</summary>
        public float DroppedTime { get; private set; }

        internal ComputeBuffer DepthBuffer => _depthBuffer;

        /// <summary>Fills every open cell below <paramref name="level"/> up to it, and stills the water.</summary>
        public void FillTo(float level)
        {
            _shader.SetFloat(LevelId, level);
            _shader.Dispatch(_fillKernel, _groupsX, _groupsZ, 1);
            Publish();
        }

        /// <summary>Sets depth directly (tools and tests). Row by row, like the ground.</summary>
        public void SetDepths(float[] depths)
        {
            if (depths == null || depths.Length != _desc.CellCount)
                throw new ArgumentException("One depth per cell.", nameof(depths));
            _depthBuffer.SetData(depths);
            _fluxBuffer.SetData(new Vector4[_desc.CellCount]);
            Publish();
        }

        /// <summary>
        /// Where a film may soak away, one flag per cell, row by row: true lets water shallower
        /// than <see cref="WaterSimulationDesc.SoakDepth"/> drain at
        /// <see cref="WaterSimulationDesc.SoakRate"/>, false keeps it whatever its depth. An empty
        /// span lets every cell soak, which is the default.
        /// </summary>
        public void SetSoakMask(ReadOnlySpan<bool> mask)
        {
            if (mask.Length == 0)
            {
                _shader.SetInt(HasSoakId, 0);
                return;
            }

            if (mask.Length != _desc.CellCount)
                throw new ArgumentException("One flag per cell.", nameof(mask));
            if (_soakBuffer == null || _soakBuffer.count != _desc.CellCount)
            {
                _soakBuffer?.Release();
                _soakBuffer = new ComputeBuffer(_desc.CellCount, sizeof(float));
            }

            var values = new float[mask.Length];
            for (var i = 0; i < mask.Length; i++)
                values[i] = mask[i] ? 1f : 0f;
            _soakBuffer.SetData(values);
            BindSoak();
            _shader.SetInt(HasSoakId, 1);
        }

        public void SetEffectors(ReadOnlySpan<WaterEffector> effectors)
        {
            _effectorCount = Mathf.Min(effectors.Length, MaxEffectors);
            for (var i = 0; i < _effectorCount; i++)
            {
                _effectors[i] = effectors[i];
                _effectors[i].Prepare(_desc);
            }

            _effectorBuffer.SetData(_effectors, 0, 0, Mathf.Max(1, _effectorCount));
            _shader.SetInt(EffectorCountId, _effectorCount);
        }

        /// <summary>Advances by <paramref name="deltaTime"/> seconds of whole fixed steps.</summary>
        public void Step(float deltaTime)
        {
            if (_disposed)
                return;
            FlushGround();
            _accumulated += Mathf.Max(0f, deltaTime);
            var steps = Mathf.FloorToInt(_accumulated / _desc.FixedStep);
            if (steps > _desc.MaxStepsPerFrame)
            {
                DroppedTime += (steps - _desc.MaxStepsPerFrame) * _desc.FixedStep;
                steps = _desc.MaxStepsPerFrame;
                _accumulated = 0f;
            }
            else
            {
                _accumulated -= steps * _desc.FixedStep;
            }

            StepExactly(steps);
        }

        /// <summary>Runs exactly <paramref name="steps"/> fixed steps, then publishes.</summary>
        public void StepExactly(int steps)
        {
            FlushGround();
            LastSteps = steps;
            for (var s = 0; s < steps; s++)
            {
                _shader.Dispatch(_fluxKernel, _groupsX, _groupsZ, 1);
                _shader.Dispatch(_depthKernel, _groupsX, _groupsZ, 1);
            }

            if (steps > 0)
                Publish();
        }

        /// <summary>Every cell's depth, read back now. Stalls the GPU: tests and tools only.</summary>
        public float[] ReadDepthsImmediate()
        {
            var result = new float[_desc.CellCount];
            _depthBuffer.GetData(result);
            return result;
        }

        /// <summary>Cubic metres of water in the zone. Stalls the GPU: tests and tools only.</summary>
        public double TotalVolumeImmediate()
        {
            double sum = 0;
            foreach (var d in ReadDepthsImmediate())
                sum += d;
            return sum * _desc.CellArea;
        }

        void Publish() => _shader.Dispatch(_velocityKernel, _groupsX, _groupsZ, 1);

        void Bind()
        {
            foreach (var kernel in new[] { _fluxKernel, _depthKernel, _velocityKernel, _fillKernel })
            {
                _shader.SetBuffer(kernel, GroundId, _groundBuffer);
                _shader.SetBuffer(kernel, DepthId, _depthBuffer);
                _shader.SetBuffer(kernel, FluxId, _fluxBuffer);
                _shader.SetBuffer(kernel, EffectorsId, _effectorBuffer);
                _shader.SetTexture(kernel, StateId, State);
            }

            BindSoak();

            _shader.SetInt(WidthId, _desc.Width);
            _shader.SetInt(HeightId, _desc.Height);
            _shader.SetInt(EffectorCountId, 0);
            _shader.SetFloat(CellSizeId, _desc.CellSize);
            _shader.SetVector(OriginId, _desc.Origin);
            _shader.SetFloat(DtId, _desc.FixedStep);
            _shader.SetFloat(PipeId, _desc.CellArea * _desc.Gravity / _desc.CellSize);
            _shader.SetFloat(RetentionId, _desc.FlowRetention);
            _shader.SetFloat(DryDepthId, _desc.DryDepth);
            _shader.SetFloat(MaxSpeedId, _desc.MaxSpeed);
            _shader.SetFloat(SoakDepthId, _desc.SoakDepth);
            _shader.SetFloat(SoakRateId, _desc.SoakRate);
            _shader.SetInt(HasSoakId, 0);
        }

        void BindSoak()
        {
            foreach (var kernel in new[] { _fluxKernel, _depthKernel, _velocityKernel, _fillKernel })
                _shader.SetBuffer(kernel, SoakId, _soakBuffer);
        }

        void OnGroundChanged(Rect world) => _pendingGround.Add(world);

        void FlushGround()
        {
            if (_pendingGround.Count == 0)
                return;
            foreach (var world in _pendingGround)
            {
                var x0 = Mathf.FloorToInt((world.xMin - _desc.Origin.x) / _desc.CellSize) - 1;
                var z0 = Mathf.FloorToInt((world.yMin - _desc.Origin.y) / _desc.CellSize) - 1;
                var x1 = Mathf.CeilToInt((world.xMax - _desc.Origin.x) / _desc.CellSize) + 1;
                var z1 = Mathf.CeilToInt((world.yMax - _desc.Origin.y) / _desc.CellSize) + 1;
                x0 = Mathf.Clamp(x0, 0, _desc.Width);
                z0 = Mathf.Clamp(z0, 0, _desc.Height);
                x1 = Mathf.Clamp(x1, 0, _desc.Width);
                z1 = Mathf.Clamp(z1, 0, _desc.Height);
                if (x1 > x0 && z1 > z0)
                    UploadGround(new RectInt(x0, z0, x1 - x0, z1 - z0));
            }

            _pendingGround.Clear();
        }

        void UploadGround(RectInt region)
        {
            for (var z = region.yMin; z < region.yMax; z++)
            {
                _ground.WriteHeights(_desc, new RectInt(region.x, z, region.width, 1), _groundRow);
                _groundBuffer.SetData(_groundRow, 0, z * _desc.Width + region.x, region.width);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _ground.Changed -= OnGroundChanged;
            Query.Dispose();
            _groundBuffer.Release();
            _depthBuffer.Release();
            _fluxBuffer.Release();
            _effectorBuffer.Release();
            _soakBuffer?.Release();
            State.Release();
            DestroyObject(State);
            DestroyObject(_shader);
        }

        static void DestroyObject(UnityEngine.Object target)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(target);
            else
                UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
