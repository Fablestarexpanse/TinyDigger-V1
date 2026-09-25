using System;
using System.Collections.Generic;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// Foam left on a zone's water by things moving through it (<see cref="WaterFoamEmitter"/>): a
    /// lapping ring round a hull at rest, a bow wave ahead of it and a wake behind it that drifts with
    /// the flow, spreads and fades (Ronan, 2026-09-24: "water effects surrounding" the boats).
    ///
    /// Plain C#, like <see cref="WaterSimulation"/>: one texel per cell, stepped on the GPU. Only the
    /// part of the zone emitters have been near in the last few <see cref="Lifetime"/>s is worked on,
    /// so a boat on a big sea costs a patch round its path, not the whole zone.
    /// </summary>
    public sealed class WaterFoamMap : IDisposable
    {
        static readonly int SourceId = Shader.PropertyToID("_Source");
        static readonly int TargetId = Shader.PropertyToID("_Target");
        static readonly int StateId = Shader.PropertyToID("_State");
        static readonly int EmittersId = Shader.PropertyToID("_Emitters");
        static readonly int RectId = Shader.PropertyToID("_Rect");
        static readonly int EmitterCountId = Shader.PropertyToID("_EmitterCount");
        static readonly int DtId = Shader.PropertyToID("_Dt");
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly int SpreadId = Shader.PropertyToID("_Spread");
        static readonly int ReferenceSpeedId = Shader.PropertyToID("_ReferenceSpeed");

        const string KernelPath = "PromptWaffle/FoamMap";

        /// <summary>Most emitters one step takes; more are ignored.</summary>
        public const int MaxEmitters = 32;

        readonly WaterSimulation _simulation;
        readonly ComputeShader _shader;
        readonly int _stepKernel, _clearKernel;
        readonly RenderTexture[] _foam = new RenderTexture[2];
        readonly ComputeBuffer _emitterBuffer;
        readonly WaterFoamEmitter[] _emitters = new WaterFoamEmitter[MaxEmitters];
        readonly List<(float time, RectInt rect)> _recent = new List<(float, RectInt)>();
        int _current;
        float _clock;
        bool _disposed;

        /// <param name="lifetime">Seconds for foam left behind to fade to about a third.</param>
        /// <param name="referenceSpeed">Metres a second at which bow wave and wake are full.</param>
        public WaterFoamMap(WaterSimulation simulation, float lifetime = 5f, float referenceSpeed = 3f)
        {
            _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            var source = Resources.Load<ComputeShader>(KernelPath);
            if (source == null)
                throw new InvalidOperationException($"Compute shader Resources/{KernelPath} is missing.");
            _shader = UnityEngine.Object.Instantiate(source);
            _shader.hideFlags = HideFlags.DontSave;
            _stepKernel = _shader.FindKernel("Step");
            _clearKernel = _shader.FindKernel("Clear");
            Lifetime = Mathf.Max(0.1f, lifetime);
            ReferenceSpeed = Mathf.Max(0.1f, referenceSpeed);

            var desc = simulation.Desc;
            for (var i = 0; i < 2; i++)
            {
                _foam[i] = new RenderTexture(desc.Width, desc.Height, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
                {
                    name = $"PromptWaffle Foam {i}",
                    enableRandomWrite = true,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
                _foam[i].Create();
                _shader.SetTexture(_clearKernel, TargetId, _foam[i]);
                _shader.Dispatch(_clearKernel, (desc.Width + 7) / 8, (desc.Height + 7) / 8, 1);
            }

            _emitterBuffer = new ComputeBuffer(MaxEmitters, WaterFoamEmitter.Stride);
            _shader.SetInt("_Width", desc.Width);
            _shader.SetInt("_Height", desc.Height);
            _shader.SetFloat("_CellSize", desc.CellSize);
            _shader.SetVector("_Origin", desc.Origin);
        }

        /// <summary>Foam 0..1 per cell, bilinear, the same size as the simulation's state.</summary>
        public RenderTexture Foam => _foam[_current];

        public float Lifetime { get; }
        public float ReferenceSpeed { get; }

        /// <summary>Share of foam eased toward its neighbours each second, so a wake widens as it ages.</summary>
        public float Spread { get; set; } = 4f;

        /// <summary>Texels worked on by the last <see cref="Step"/>: 0 when no foam can be anywhere.</summary>
        public int LastTexels { get; private set; }

        /// <summary>Carries, spreads and fades the foam by <paramref name="deltaTime"/>, then lays down what the emitters make now.</summary>
        public void Step(float deltaTime, ReadOnlySpan<WaterFoamEmitter> emitters)
        {
            if (_disposed)
                return;
            var desc = _simulation.Desc;
            _clock += Mathf.Max(0f, deltaTime);
            var count = Mathf.Min(emitters.Length, MaxEmitters);
            for (var i = 0; i < count; i++)
            {
                _emitters[i] = emitters[i];
                _recent.Add((_clock, Reach(emitters[i], desc)));
            }

            // Foam anywhere an emitter has been lately; four and a half lifetimes on, even full foam is
            // below the 0.02 cut and has been written as nothing.
            _recent.RemoveAll(entry => _clock - entry.time > Lifetime * 4.5f);
            LastTexels = 0;
            if (_recent.Count == 0)
                return;
            var rect = _recent[0].rect;
            foreach (var (_, r) in _recent)
                rect = Union(rect, r);
            rect = Clip(rect, desc);
            if (rect.width <= 0 || rect.height <= 0)
                return;

            _emitterBuffer.SetData(_emitters, 0, 0, Mathf.Max(1, count));
            var next = 1 - _current;
            _shader.SetTexture(_stepKernel, SourceId, _foam[_current]);
            _shader.SetTexture(_stepKernel, TargetId, _foam[next]);
            _shader.SetTexture(_stepKernel, StateId, _simulation.State);
            _shader.SetBuffer(_stepKernel, EmittersId, _emitterBuffer);
            _shader.SetInt(EmitterCountId, count);
            _shader.SetInts(RectId, rect.xMin, rect.yMin, rect.xMax, rect.yMax);
            _shader.SetFloat(DtId, deltaTime);
            _shader.SetFloat(FadeId, Mathf.Exp(-deltaTime / Lifetime));
            _shader.SetFloat(SpreadId, Mathf.Clamp01(Spread * deltaTime));
            _shader.SetFloat(ReferenceSpeedId, ReferenceSpeed);
            _shader.Dispatch(_stepKernel, (rect.width + 7) / 8, (rect.height + 7) / 8, 1);
            _current = next;
            LastTexels = rect.width * rect.height;
        }

        /// <summary>The texels an emitter can put foam on, with room for the wake to drift and spread.</summary>
        static RectInt Reach(in WaterFoamEmitter emitter, in WaterSimulationDesc desc)
        {
            var radius = Mathf.Max(emitter.HalfSize.x, emitter.HalfSize.y) + 4f;
            var min = (emitter.Position - Vector2.one * radius - desc.Origin) / desc.CellSize;
            var max = (emitter.Position + Vector2.one * radius - desc.Origin) / desc.CellSize;
            var x0 = Mathf.FloorToInt(min.x);
            var z0 = Mathf.FloorToInt(min.y);
            return new RectInt(x0, z0, Mathf.CeilToInt(max.x) - x0, Mathf.CeilToInt(max.y) - z0);
        }

        static RectInt Union(RectInt a, RectInt b)
        {
            var x0 = Mathf.Min(a.xMin, b.xMin);
            var z0 = Mathf.Min(a.yMin, b.yMin);
            return new RectInt(x0, z0, Mathf.Max(a.xMax, b.xMax) - x0, Mathf.Max(a.yMax, b.yMax) - z0);
        }

        static RectInt Clip(RectInt r, in WaterSimulationDesc desc)
        {
            var x0 = Mathf.Clamp(r.xMin, 0, desc.Width);
            var z0 = Mathf.Clamp(r.yMin, 0, desc.Height);
            var x1 = Mathf.Clamp(r.xMax, 0, desc.Width);
            var z1 = Mathf.Clamp(r.yMax, 0, desc.Height);
            return new RectInt(x0, z0, x1 - x0, z1 - z0);
        }

        /// <summary>Every cell's foam, row by row. Stalls the GPU: tests and tools only.</summary>
        public float[] ReadImmediate()
        {
            var previous = RenderTexture.active;
            RenderTexture.active = Foam;
            var texture = new Texture2D(Foam.width, Foam.height, TextureFormat.RFloat, false, true);
            texture.ReadPixels(new Rect(0, 0, Foam.width, Foam.height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            var result = texture.GetPixelData<float>(0).ToArray();
            DestroyObject(texture);
            return result;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var foam in _foam)
            {
                foam.Release();
                DestroyObject(foam);
            }

            _emitterBuffer.Release();
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
