using System.Diagnostics;
using TinyDiggers.Terrain;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TinyDiggers.Presentation
{
    /// <summary>Which generator a <see cref="TerrainView"/> fills its grid with.</summary>
    public enum TerrainGeneratorKind
    {
        /// <summary>The island: sea, shelf, mountain, river. What the game plays on.</summary>
        Island,

        /// <summary>The old plateaus-and-hills test terrain, kept for the tests that use it.</summary>
        Plateaus,
    }

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

        [Tooltip("Metres across one cell. The island's settings are in metres, so a smaller cell gives the same island at a finer grain; set Height Step to match to keep one step per cell a 45-degree slope.")]
        [SerializeField, Min(0.05f)] float _cellSize = 1f;
        [SerializeField, Range(1, ChunkedTerrainRenderer.MaxChunkSize)] int _chunkSize = ChunkedTerrainRenderer.DefaultChunkSize;
        [SerializeField] int _seed = 1;
        [SerializeField] Material _material;
        [SerializeField] TerrainRendererKind _renderer = TerrainRendererKind.Smoothed;

        [Tooltip("Draw chunks far from the camera with fewer quads (smoothed renderer only).")]
        [SerializeField] bool _levelsOfDetail = true;

        [Tooltip("Metres from the camera beyond which a chunk uses one quad per 2x2, 4x4 and 8x8 cells.")]
        [SerializeField] float[] _lodDistances = { 120f, 300f, 650f };

        [Tooltip("Most chunk levels of detail built in one frame.")]
        [SerializeField, Min(1)] int _lodBuildsPerFrame = 48;

        [Header("Land")]
        [SerializeField] TerrainGeneratorKind _generator = TerrainGeneratorKind.Island;

        [Tooltip("The island's numbers. Without one the old plateau generator is used.")]
        [SerializeField] TerrainGenSettings _settings;

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
        [Min(1f)] public float MottleRepeat = 11.3f;

        /// <summary>How far the mottle lifts and drops brightness, either way.</summary>
        [Range(0f, 0.4f)] public float MottleStrength = 0.1f;

        /// <summary>Metres a material boundary is blended across.</summary>
        [Range(0.05f, 4f)] public float BlendWidth = 2f;

        /// <summary>
        /// Metres. Generated surfaces snap to this, and digs, fills and slumps move whole
        /// multiples of it. Read once when the grid is built.
        /// </summary>
        [Min(0f)] public float HeightStep = 1f;

        /// <summary>Most cells the slump simulator examines per frame; the rest wait for the next frame.</summary>
        [Min(1)] public int SlumpTilesPerTick = 1000;

        /// <summary>
        /// Cells the slump simulator may examine per **second**, so spoil takes a moment to find
        /// its angle instead of arriving already settled.
        ///
        /// A thousand a frame meant a tipped load finished spreading in the same frame it landed —
        /// the angle of repose was all there and none of it was ever visible, and a tip read as
        /// ground changing rather than as dirt being dumped (Ronan, 2026-09-22). At ninety a
        /// second a barrow's worth of cascade takes about half a second, which is long enough to
        /// see it run and short enough not to hold the site up. Nought means no limit, which is
        /// what generation wants.
        /// </summary>
        [Min(0f)] public float SlumpTilesPerSecond = 90f;

        /// <summary>Carried-over fraction of a cell, so the rate does not depend on the frame rate.</summary>
        float _slumpBudget;

        ChunkedTerrainRenderer _terrainRenderer;
        AngleOfReposeSimulator _slump;
        TerrainDetail _detail;

        public TerrainGrid Grid { get; private set; }

        /// <summary>Metres from the middle of the grid to the edge of the disc of land.</summary>
        public float DiscRadius { get; private set; }

        /// <summary>The middle of the disc, in metres in the terrain's local space.</summary>
        public Vector2 DiscCentre => Grid == null ? Vector2.zero
            : new Vector2(Grid.Width * 0.5f, Grid.Height * 0.5f) * Grid.CellSize;

        /// <summary>The height the disc reads as at its rim: the top of the wall the land sits in.</summary>
        public float RimHeight => PlinthTop;

        /// <summary>
        /// Where the top of the plinth wall sits. On the island it stands a metre above the sea, so
        /// the water is held inside the wall rather than running off the edge of the table.
        /// </summary>
        public float PlinthTop => UsingIsland ? World.SeaLevel + 1f : TerrainGenerator.RimHeight - 0.75f;

        /// <summary>The island as last generated: its peak and its river. Null for the old generator.</summary>
        public IslandMap Island { get; private set; }

        /// <summary>The seed the land was last generated from.</summary>
        public int Seed => _settings != null && UsingIsland ? _settings.Seed : _seed;

        /// <summary>The island's settings asset, or null when the old generator is in use.</summary>
        public TerrainGenSettings Settings => _settings;

        /// <summary>Raised after the land is regenerated, so anything holding cells can start again.</summary>
        public event System.Action Regenerated;

        bool UsingIsland => _generator == TerrainGeneratorKind.Island && _settings != null;

        /// <summary>
        /// The river the generator cut, in world space, for a river mesh to be laid along. Empty
        /// when there is no island or no river.
        /// </summary>
        public System.Collections.Generic.List<Vector3> RiverWorldPoints()
        {
            var points = new System.Collections.Generic.List<Vector3>();
            if (Island == null)
                return points;
            foreach (var point in Island.River)
                points.Add(transform.TransformPoint(point));
            return points;
        }

        public ITerrainRenderer Renderer => _terrainRenderer;

        /// <summary>The terrain's material, for anything that tunes how it is shaded. May be null.</summary>
        public Material DetailMaterial => _detail?.Material;

        /// <summary>Triangles across all chunks as last built.</summary>
        public long TriangleCount => _terrainRenderer?.TriangleCount ?? 0;

        /// <summary>Cells waiting for the slump simulator.</summary>
        public int SlumpPending => _slump?.PendingCount ?? 0;

        /// <summary>Chunks rebuilt in the last frame. For perf reporting.</summary>
        public int ChunksRebuiltLastFrame => _terrainRenderer?.LastRebuiltChunkCount ?? 0;

        void Awake()
        {
            var stopwatch = Stopwatch.StartNew();
            var datum = UsingIsland ? _settings.Datum : 0f;
            Grid = new TerrainGrid(_width, _height, MaterialTable.CreateDefault(), HeightStep, datum, _cellSize);
            // The simulator is built before the land so that generation queues every cell it
            // touches, and one settle at the end leaves nothing standing steeper than it should.
            _slump = new AngleOfReposeSimulator(Grid);
            Fill();
            DiscRadius = TerrainGenerator.DiscRadius(Grid) * Grid.CellSize;
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
                : new SmoothedTerrainRenderer(Grid, transform, material, _chunkSize, LevelsOfDetail());
            var built = stopwatch.Elapsed.TotalMilliseconds;

            Debug.Log(
                $"TerrainView: {_width}x{_height} cells, {_renderer} renderer, " +
                $"{_terrainRenderer.ChunkCountX * _terrainRenderer.ChunkCountZ} chunks, " +
                $"{_terrainRenderer.TriangleCount:N0} triangles. Generated in {generated:0} ms, meshed in {built:0} ms.");
        }

        /// <summary>
        /// Levels of detail for the smoothed renderer, starting from where the camera is now, so
        /// the first build makes each chunk once at the level it will be seen at.
        /// </summary>
        TerrainLod LevelsOfDetail()
        {
            if (!_levelsOfDetail)
                return null;
            var lod = new TerrainLod { Distances = _lodDistances, BuildsPerFrame = _lodBuildsPerFrame };
            lod.HasViewer = TryViewer(out lod.Viewer);
            return lod;
        }

        /// <summary>The main camera's position in the terrain's own space.</summary>
        bool TryViewer(out Vector3 viewer)
        {
            var camera = Camera.main;
            viewer = camera != null ? transform.InverseTransformPoint(camera.transform.position) : default;
            return camera != null;
        }

        /// <summary>Generates the land into the existing grid and settles it once.</summary>
        void Fill()
        {
            if (UsingIsland)
                Island = IslandGenerator.Generate(Grid, _settings);
            else
                TerrainGenerator.Generate(Grid, _seed);
            // One settle, so nothing the generator left standing too steep is a surprise later.
            // Every cell is queued by the generation; only the few that could slide are kept.
            _slump.DropSettled();
            _slump.RunUntilStable();
        }

        /// <summary>
        /// Rebuilds the land from a seed, in place: the grid, the renderers and everything holding
        /// a reference to them stay as they are, and every cell simply changes.
        /// </summary>
        public void Regenerate(int seed)
        {
            if (Grid == null)
                return;
            if (_settings != null)
                _settings.Seed = seed;
            _seed = seed;
            Fill();
            Regenerated?.Invoke();
        }

        static readonly ProfilerMarker SlumpMarker = new ProfilerMarker("TinyDiggers.SlumpTick");
        static readonly ProfilerMarker RebuildMarker = new ProfilerMarker("TinyDiggers.TerrainRebuild");

        void Update()
        {
            // Budgeted by the second rather than by the frame, and by *unscaled* time: a tip should
            // take the same moment to settle whether the clock is running at one times or twenty,
            // because what is being paced here is the eye, not the work.
            if (SlumpTilesPerSecond > 0f)
            {
                _slumpBudget += SlumpTilesPerSecond * Time.unscaledDeltaTime;
                var take = Mathf.FloorToInt(_slumpBudget);
                _slumpBudget -= take;
                _slump.MaxTilesPerTick = Mathf.Clamp(take, 0, SlumpTilesPerTick);
            }
            else
            {
                _slump.MaxTilesPerTick = SlumpTilesPerTick;
            }

            using (SlumpMarker.Auto())
                _slump.Tick();
        }

        void LateUpdate()
        {
            if (_levelsOfDetail && TryViewer(out var viewer))
                _terrainRenderer.UpdateLod(viewer);
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
