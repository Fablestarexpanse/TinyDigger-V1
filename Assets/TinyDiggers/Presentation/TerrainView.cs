using System.Diagnostics;
using TinyDiggers.Terrain;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TinyDiggers.Presentation
{
    /// <summary>Which <see cref="ITerrainRenderer"/> a <see cref="TerrainView"/> builds.</summary>
    public enum TerrainRendererKind
    {
        /// <summary>Corner-averaged heightfield with steep-face layer colouring. The game look.</summary>
        Smoothed,

        /// <summary>Flat-topped columns with layer-banded vertical walls. For inspecting the raw grid.</summary>
        Walled,
    }

    /// <summary>
    /// Scene entry point for the terrain: builds the grid, fills it with test terrain, and wires
    /// it to a renderer and the slump simulator. Holds no terrain logic of its own.
    /// </summary>
    public sealed class TerrainView : MonoBehaviour
    {
        [SerializeField, Min(1)] int _width = 512;
        [SerializeField, Min(1)] int _height = 512;
        [SerializeField, Range(1, ChunkedTerrainRenderer.MaxChunkSize)] int _chunkSize = ChunkedTerrainRenderer.DefaultChunkSize;
        [SerializeField] int _seed = 1;
        [SerializeField] Material _material;
        [SerializeField] TerrainRendererKind _renderer = TerrainRendererKind.Smoothed;

        [Header("Material detail")]
        [SerializeField, Tooltip("Leave empty to draw with the flat vertex-colour material instead.")]
        TerrainTextureSet _textures;

        /// <summary>Metres one tile of the albedo covers.</summary>
        [Min(0.1f)] public float AlbedoRepeat = 0.5f;

        /// <summary>Metres one tile of the fine detail normal covers.</summary>
        [Min(0.05f)] public float DetailRepeat = 0.25f;

        /// <summary>How far the detail normal bends the mesh's smooth normal. 0 is off.</summary>
        [Range(0f, 2f)] public float DetailStrength = 1f;

        /// <summary>Metres one cycle of the slow brightness mottle covers.</summary>
        [Min(1f)] public float MottleRepeat = 12f;

        /// <summary>How far the mottle lifts and drops brightness, either way.</summary>
        [Range(0f, 0.4f)] public float MottleStrength = 0.1f;

        /// <summary>Cells a material boundary is blended across.</summary>
        [Range(0.05f, 1f)] public float BlendWidth = 0.5f;

        /// <summary>
        /// Metres. Generated surfaces snap to this, and digs, fills and slumps move whole
        /// multiples of it. Read once when the grid is built.
        /// </summary>
        [Min(0f)] public float HeightStep = 1f;

        /// <summary>Most cells the slump simulator examines per frame; the rest wait for the next frame.</summary>
        [Min(1)] public int SlumpTilesPerTick = 1000;

        ChunkedTerrainRenderer _terrainRenderer;
        AngleOfReposeSimulator _slump;
        TerrainDetail _detail;

        public TerrainGrid Grid { get; private set; }

        /// <summary>Cells from the middle of the grid to the edge of the disc of land.</summary>
        public float DiscRadius { get; private set; }

        /// <summary>The middle of the disc, in cells.</summary>
        public Vector2 DiscCentre => Grid == null ? Vector2.zero : new Vector2(Grid.Width * 0.5f, Grid.Height * 0.5f);

        /// <summary>The height the disc is cut to at its rim.</summary>
        public float RimHeight => TerrainGenerator.RimHeight;

        public ITerrainRenderer Renderer => _terrainRenderer;

        /// <summary>Triangles across all chunks as last built.</summary>
        public long TriangleCount => _terrainRenderer?.TriangleCount ?? 0;

        /// <summary>Cells waiting for the slump simulator.</summary>
        public int SlumpPending => _slump?.PendingCount ?? 0;

        /// <summary>Chunks rebuilt in the last frame. For perf reporting.</summary>
        public int ChunksRebuiltLastFrame => _terrainRenderer?.LastRebuiltChunkCount ?? 0;

        void Awake()
        {
            var stopwatch = Stopwatch.StartNew();
            Grid = new TerrainGrid(_width, _height, MaterialTable.CreateDefault(), HeightStep);
            TerrainGenerator.Generate(Grid, _seed);
            DiscRadius = TerrainGenerator.DiscRadius(Grid);
            var generated = stopwatch.Elapsed.TotalMilliseconds;

            var material = _material;
            if (_textures != null)
            {
                var origin = new Vector2(transform.position.x, transform.position.z);
                try
                {
                    _detail = new TerrainDetail(Grid, _textures, origin);
                    PushTuning();
                    material = _detail.Material;
                }
                catch (System.Exception error)
                {
                    // A missing shader or an unreadable texture should not cost the whole scene;
                    // the flat material still shows the terrain.
                    Debug.LogError($"TerrainView: material detail is off. {error.Message}");
                    _detail?.Dispose();
                    _detail = null;
                }
            }

            stopwatch.Restart();
            _terrainRenderer = _renderer == TerrainRendererKind.Walled
                ? new WalledTerrainRenderer(Grid, transform, material, _chunkSize)
                : new SmoothedTerrainRenderer(Grid, transform, material, _chunkSize);
            var built = stopwatch.Elapsed.TotalMilliseconds;

            // Created after generation, so the freshly generated map is not queued for slumping;
            // only edits start collapses.
            _slump = new AngleOfReposeSimulator(Grid);

            Debug.Log(
                $"TerrainView: {_width}x{_height} cells, {_renderer} renderer, " +
                $"{_terrainRenderer.ChunkCountX * _terrainRenderer.ChunkCountZ} chunks, " +
                $"{_terrainRenderer.TriangleCount:N0} triangles. Generated in {generated:0} ms, meshed in {built:0} ms.");
        }

        static readonly ProfilerMarker SlumpMarker = new ProfilerMarker("TinyDiggers.SlumpTick");
        static readonly ProfilerMarker RebuildMarker = new ProfilerMarker("TinyDiggers.TerrainRebuild");

        void Update()
        {
            _slump.MaxTilesPerTick = SlumpTilesPerTick;
            using (SlumpMarker.Auto())
                _slump.Tick();
        }

        void LateUpdate()
        {
            using (RebuildMarker.Auto())
                _terrainRenderer.Rebuild();

            if (_detail == null)
                return;
            PushTuning();
            _detail.Flush();
        }

        void PushTuning() =>
            _detail.SetTuning(AlbedoRepeat, DetailRepeat, DetailStrength, MottleRepeat, MottleStrength, BlendWidth);

        void OnDestroy()
        {
            _slump?.Dispose();
            _terrainRenderer?.Dispose();
            _detail?.Dispose();
        }
    }
}
