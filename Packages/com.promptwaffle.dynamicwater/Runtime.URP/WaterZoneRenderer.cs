using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PromptWaffle.DynamicWater.URP
{
    /// <summary>
    /// Draws a <see cref="WaterZone"/>'s water in URP. Refraction reads URP's opaque texture:
    /// turn on "Opaque Texture" in the URP asset, or the water shows what is under it as grey.
    /// The zone is covered by flat grid chunks whose vertices are world positions
    /// (one vertex every <see cref="_cellsPerVertex"/> cells, 64 x 64 quads a chunk so each can be
    /// culled); the DynamicWaterSurface shader lifts every vertex to the simulated surface and
    /// hides dry ground. Nothing is rebuilt as the water moves: the state texture is all that
    /// changes.
    /// </summary>
    [AddComponentMenu("PromptWaffle/Dynamic Water/Water Zone Renderer (URP)")]
    [RequireComponent(typeof(WaterZone))]
    public sealed class WaterZoneRenderer : MonoBehaviour
    {
        static readonly int StateId = Shader.PropertyToID("_WaterState");
        static readonly int ZoneId = Shader.PropertyToID("_WaterZone");
        static readonly int TexelId = Shader.PropertyToID("_WaterTexel");
        static readonly int WaveAId = Shader.PropertyToID("_PWWaveA");
        static readonly int WaveBId = Shader.PropertyToID("_PWWaveB");
        static readonly int WaveCountId = Shader.PropertyToID("_PWWaveCount");
        static readonly int WaveParamsId = Shader.PropertyToID("_PWWaveParams");

        readonly Vector4[] _waveA = new Vector4[WaterWaves.MaxWaves];
        readonly Vector4[] _waveB = new Vector4[WaterWaves.MaxWaves];

        const int ChunkQuads = 64;

        [SerializeField] Material _material;

        [Tooltip("Simulation cells per mesh vertex. 1 follows every cell; 2 is a quarter of the vertices.")]
        [SerializeField, Range(1, 8)] int _cellsPerVertex = 2;

        [Tooltip("Metres the bounds are grown by, up and down, for waves.")]
        [SerializeField] float _boundsSlack = 20f;

        [SerializeField] ShadowCastingMode _shadows = ShadowCastingMode.Off;

        readonly List<MeshRenderer> _chunks = new List<MeshRenderer>();
        WaterZone _zone;
        MaterialPropertyBlock _block;
        WaterSimulation _builtFor;

        void OnEnable()
        {
            _zone = GetComponent<WaterZone>();
            _block = new MaterialPropertyBlock();
        }

        void OnDisable() => Clear();

        void LateUpdate()
        {
            var simulation = _zone != null ? _zone.Simulation : null;
            if (simulation == null || _material == null)
            {
                Clear();
                return;
            }

            if (_builtFor != simulation)
                Build(simulation);

            var desc = simulation.Desc;
            _block.SetTexture(StateId, simulation.State);
            _block.SetVector(ZoneId, new Vector4(desc.Origin.x, desc.Origin.y, desc.Width * desc.CellSize, desc.Height * desc.CellSize));
            _block.SetVector(TexelId, new Vector4(1f / desc.Width, 1f / desc.Height, desc.CellSize, 0f));
            var waves = _zone.Waves;
            var settings = _zone.WaveSettings;
            WaterWaves.Pack(waves, _waveA, _waveB);
            _block.SetVectorArray(WaveAId, _waveA);
            _block.SetVectorArray(WaveBId, _waveB);
            _block.SetInt(WaveCountId, settings != null ? waves.Count : 0);
            _block.SetVector(WaveParamsId, settings != null
                ? new Vector4(Mathf.Max(1f, settings.GustSize), settings.GustCalm, Mathf.Max(0.01f, settings.DampDepth), settings.Whitecaps)
                : new Vector4(1f, 1f, 1f, 2f));
            foreach (var chunk in _chunks)
                chunk.SetPropertyBlock(_block);
        }

        void Build(WaterSimulation simulation)
        {
            Clear();
            _builtFor = simulation;
            var desc = simulation.Desc;
            var step = desc.CellSize * _cellsPerVertex;
            var quadsX = Mathf.CeilToInt(desc.Width / (float)_cellsPerVertex);
            var quadsZ = Mathf.CeilToInt(desc.Height / (float)_cellsPerVertex);
            var sizeX = desc.Width * desc.CellSize;
            var sizeZ = desc.Height * desc.CellSize;

            for (var cz = 0; cz < quadsZ; cz += ChunkQuads)
            {
                for (var cx = 0; cx < quadsX; cx += ChunkQuads)
                {
                    var nx = Mathf.Min(ChunkQuads, quadsX - cx);
                    var nz = Mathf.Min(ChunkQuads, quadsZ - cz);
                    var mesh = GridMesh(nx, nz, step, desc.Origin + new Vector2(cx * step, cz * step), sizeX, sizeZ, desc.Origin);
                    // Persistent renderers, not Graphics.RenderMesh: a draw queued from LateUpdate
                    // reaches only the cameras that render that frame, so manual renders, captures
                    // and reflection cameras missed the water. The vertices are world positions
                    // (the shader ignores the object matrix), and the world bounds are set here, so
                    // the zone's own transform can never skew or mis-cull the water.
                    var go = new GameObject($"Water Chunk {cx / ChunkQuads},{cz / ChunkQuads}")
                    {
                        hideFlags = HideFlags.DontSave | HideFlags.NotEditable,
                        layer = gameObject.layer,
                    };
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = _material;
                    renderer.shadowCastingMode = _shadows;
                    renderer.receiveShadows = true;
                    renderer.bounds = mesh.bounds;
                    _chunks.Add(renderer);
                }
            }
        }

        /// <summary>A flat grid in world x, z. Bounds cover the wave slack.</summary>
        Mesh GridMesh(int quadsX, int quadsZ, float step, Vector2 corner, float zoneX, float zoneZ, Vector2 zoneOrigin)
        {
            var vertices = new Vector3[(quadsX + 1) * (quadsZ + 1)];
            var indices = new int[quadsX * quadsZ * 6];
            for (var z = 0; z <= quadsZ; z++)
                for (var x = 0; x <= quadsX; x++)
                {
                    var wx = Mathf.Min(corner.x + x * step, zoneOrigin.x + zoneX);
                    var wz = Mathf.Min(corner.y + z * step, zoneOrigin.y + zoneZ);
                    vertices[z * (quadsX + 1) + x] = new Vector3(wx, 0f, wz);
                }

            var t = 0;
            for (var z = 0; z < quadsZ; z++)
                for (var x = 0; x < quadsX; x++)
                {
                    var a = z * (quadsX + 1) + x;
                    var b = a + 1;
                    var c = a + quadsX + 1;
                    var d = c + 1;
                    indices[t++] = a; indices[t++] = c; indices[t++] = b;
                    indices[t++] = b; indices[t++] = c; indices[t++] = d;
                }

            var mesh = new Mesh { name = "Water Chunk", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.normals = System.Array.ConvertAll(vertices, _ => Vector3.up);
            mesh.RecalculateBounds();
            // The flat grid sits at y 0; the shader lifts it to the water, which is somewhere
            // around the zone's own height.
            var bounds = mesh.bounds;
            bounds.size = new Vector3(bounds.size.x, 0f, bounds.size.z);
            bounds.center = new Vector3(bounds.center.x, transform.position.y, bounds.center.z);
            bounds.Expand(new Vector3(0f, _boundsSlack * 2f, 0f));
            mesh.bounds = bounds;
            return mesh;
        }

        void Clear()
        {
            foreach (var chunk in _chunks)
            {
                if (chunk == null)
                    continue;
                DestroyObject(chunk.GetComponent<MeshFilter>().sharedMesh);
                DestroyObject(chunk.gameObject);
            }

            _chunks.Clear();
            _builtFor = null;
        }

        static void DestroyObject(Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }
    }
}
