using System;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// Metres from each patch of a zone's water to the nearest dry ground or wall, capped at
    /// <see cref="Reach"/>: what surf, and anything else that should know where the shore is, lays
    /// itself out by. Water depth only stands in for that on a gentle shelf; on a steep beach every
    /// wave laid out by depth crowds into a strip a metre or two wide (2026-09-24).
    ///
    /// Plain C#, like <see cref="WaterSimulation"/>: built on the GPU by a jump flood over a coarse
    /// grid (one texel to <see cref="CellsPerTexel"/>² cells) from the simulation's published state.
    /// Nothing runs until <see cref="Update"/> is called; the shore moves only as fast as the water
    /// does, so a renderer calls it every second or so, not every frame.
    /// </summary>
    public sealed class WaterShoreDistance : IDisposable
    {
        static readonly int StateId = Shader.PropertyToID("_State");
        static readonly int SeedsInId = Shader.PropertyToID("_SeedsIn");
        static readonly int SeedsOutId = Shader.PropertyToID("_SeedsOut");
        static readonly int DistanceId = Shader.PropertyToID("_Distance");

        const string KernelPath = "PromptWaffle/ShoreDistance";

        readonly WaterSimulation _simulation;
        readonly ComputeShader _shader;
        readonly int _seedKernel, _floodKernel, _resolveKernel;
        readonly int _groupsX, _groupsZ;
        readonly RenderTexture[] _seeds = new RenderTexture[2];
        bool _disposed;

        /// <param name="reach">Metres beyond which the distance reads as the reach itself.</param>
        /// <param name="cellsPerTexel">Simulation cells across one texel of the field.</param>
        /// <param name="dryBelow">Water thinner than this, in metres, counts as shore.</param>
        public WaterShoreDistance(WaterSimulation simulation, float reach = 40f, int cellsPerTexel = 2, float dryBelow = 0.05f)
        {
            _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            if (reach <= 0f)
                throw new ArgumentException("The reach must be more than nothing.", nameof(reach));
            if (cellsPerTexel < 1)
                throw new ArgumentException("At least one cell a texel.", nameof(cellsPerTexel));
            var source = Resources.Load<ComputeShader>(KernelPath);
            if (source == null)
                throw new InvalidOperationException($"Compute shader Resources/{KernelPath} is missing.");
            _shader = UnityEngine.Object.Instantiate(source);
            _shader.hideFlags = HideFlags.DontSave;
            _seedKernel = _shader.FindKernel("Seed");
            _floodKernel = _shader.FindKernel("Flood");
            _resolveKernel = _shader.FindKernel("Resolve");

            var desc = simulation.Desc;
            Reach = reach;
            CellsPerTexel = cellsPerTexel;
            Width = (desc.Width + cellsPerTexel - 1) / cellsPerTexel;
            Height = (desc.Height + cellsPerTexel - 1) / cellsPerTexel;
            MetresPerTexel = desc.CellSize * cellsPerTexel;
            _groupsX = (Width + 7) / 8;
            _groupsZ = (Height + 7) / 8;

            for (var i = 0; i < 2; i++)
            {
                _seeds[i] = new RenderTexture(Width, Height, 0, RenderTextureFormat.RInt, RenderTextureReadWrite.Linear)
                {
                    name = $"PromptWaffle Shore Seeds {i}",
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.DontSave,
                };
                _seeds[i].Create();
            }

            Distance = new RenderTexture(Width, Height, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                name = "PromptWaffle Shore Distance",
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            Distance.Create();

            _shader.SetInt("_CellsWide", desc.Width);
            _shader.SetInt("_CellsHigh", desc.Height);
            _shader.SetInt("_TexelsWide", Width);
            _shader.SetInt("_TexelsHigh", Height);
            _shader.SetInt("_CellsPerTexel", cellsPerTexel);
            _shader.SetFloat("_DryBelow", dryBelow);
            _shader.SetFloat("_MetresPerTexel", MetresPerTexel);
            _shader.SetFloat("_Reach", reach);
            Update();
        }

        /// <summary>Metres to the nearest dry ground, capped at <see cref="Reach"/>; bilinear, one texel to <see cref="CellsPerTexel"/>² cells.</summary>
        public RenderTexture Distance { get; }

        public float Reach { get; }
        public int CellsPerTexel { get; }
        public int Width { get; }
        public int Height { get; }
        public float MetresPerTexel { get; }

        /// <summary>World metres the field covers across and down: a whole number of texels, so a little past the zone when it does not divide.</summary>
        public Vector2 Covers => new Vector2(Width * MetresPerTexel, Height * MetresPerTexel);

        /// <summary>Rebuilds the field from the simulation's state as it is now.</summary>
        public void Update()
        {
            if (_disposed)
                return;
            _shader.SetTexture(_seedKernel, StateId, _simulation.State);
            _shader.SetTexture(_seedKernel, SeedsOutId, _seeds[0]);
            _shader.Dispatch(_seedKernel, _groupsX, _groupsZ, 1);

            var current = 0;
            var reachTexels = Mathf.CeilToInt(Reach / MetresPerTexel);
            var step = Mathf.NextPowerOfTwo(Mathf.Max(1, reachTexels)) / 2;
            // Down to 1, then 1 once more: the extra pass mends most of the flood's near misses.
            for (var pass = Mathf.Max(1, step); ; pass = Mathf.Max(1, pass / 2))
            {
                Flood(pass, ref current);
                if (pass == 1)
                    break;
            }

            Flood(1, ref current);

            _shader.SetTexture(_resolveKernel, SeedsInId, _seeds[current]);
            _shader.SetTexture(_resolveKernel, DistanceId, Distance);
            _shader.Dispatch(_resolveKernel, _groupsX, _groupsZ, 1);
        }

        void Flood(int step, ref int current)
        {
            _shader.SetInt("_Step", step);
            _shader.SetTexture(_floodKernel, SeedsInId, _seeds[current]);
            _shader.SetTexture(_floodKernel, SeedsOutId, _seeds[1 - current]);
            _shader.Dispatch(_floodKernel, _groupsX, _groupsZ, 1);
            current = 1 - current;
        }

        /// <summary>Every texel's distance, row by row. Stalls the GPU: tests and tools only.</summary>
        public float[] ReadImmediate()
        {
            var previous = RenderTexture.active;
            RenderTexture.active = Distance;
            var texture = new Texture2D(Width, Height, TextureFormat.RFloat, false, true);
            texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
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
            foreach (var seeds in _seeds)
            {
                seeds.Release();
                DestroyObject(seeds);
            }

            Distance.Release();
            DestroyObject(Distance);
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
