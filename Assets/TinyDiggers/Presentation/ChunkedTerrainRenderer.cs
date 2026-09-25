using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PromptWaffle.Terrain;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Shared plumbing for renderers that draw a <see cref="TerrainGrid"/> as one mesh per square
    /// chunk of cells: the chunk objects, the dirty sets, the material palette and the mesh upload.
    /// Subclasses decide which chunks an edit dirties and what geometry a chunk gets.
    ///
    /// Edits are batched in two stages. <see cref="MarkDirty"/> only records the cell in a
    /// deduplicated dirty-cell set, which is cheap however many times a slump tick touches the same
    /// cell. <see cref="Rebuild"/> then expands each dirty cell into the chunks it affects, and
    /// rebuilds each of those chunks once, at the level of detail it is showing.
    ///
    /// Levels of detail (optional, <see cref="TerrainLod"/>): a chunk can be drawn with one quad a
    /// cell (level 0) or one per 2, 4 or 8 cells square, chosen by its distance from the viewer
    /// (<see cref="UpdateLod"/>). A level is built only when first wanted, at most
    /// <see cref="TerrainLod.BuildsPerFrame"/> a frame, nearest first; until then the chunk keeps
    /// what it shows. An edit rebuilds the shown level and marks the others stale. Without a
    /// <see cref="TerrainLod"/> every chunk is level 0, as before.
    ///
    /// A big rebuild (start-up, a regenerate: <see cref="ParallelFrom"/> chunks or more) works out
    /// the chunks' geometry on worker threads, a batch at a time, when the subclass says it can
    /// (<see cref="ParallelWorkers"/>); the meshes are still uploaded on the main thread.
    ///
    /// Plain C#, not a MonoBehaviour: the owner calls <see cref="Rebuild"/> once a frame and
    /// <see cref="Dispose"/> when done. Subclasses must call <see cref="Rebuild"/> at the end of
    /// their constructor, once their own scratch state exists.
    /// </summary>
    public abstract class ChunkedTerrainRenderer : ITerrainRenderer, IDisposable
    {
        public const int DefaultChunkSize = 32;

        /// <summary>A sanity bound: much larger and a single chunk rebuild is long enough to hitch.</summary>
        public const int MaxChunkSize = 127;

        /// <summary>Most levels of detail a chunk can have: one quad per 1, 2, 4 and 8 cells square.</summary>
        public const int MaxLodLevels = 4;

        static readonly Color32 MissingMaterialColor = new Color32(255, 0, 255, 255);

        readonly Chunk[] _chunks;
        readonly bool[] _dirty;
        readonly List<int> _dirtyChunks = new List<int>();
        readonly bool[] _dirtyCell;
        readonly List<int> _dirtyCells = new List<int>();
        readonly TerrainMeshBuilder _builder;
        readonly List<Work> _work = new List<Work>();
        readonly int[] _workedOn;
        readonly List<int> _lodCandidates = new List<int>();
        readonly float[] _distance;
        readonly TerrainLod _lod;
        readonly int _levels;
        int _frame;
        bool _disposed;

        readonly struct Work
        {
            public Work(int chunk, int level)
            {
                Chunk = chunk;
                Level = level;
            }

            public int Chunk { get; }

            public int Level { get; }
        }

        protected ChunkedTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize, TerrainLod lod = null)
        {
            if (chunkSize < 1 || chunkSize > MaxChunkSize)
                throw new ArgumentOutOfRangeException(nameof(chunkSize), $"Chunk size must be 1..{MaxChunkSize}.");

            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            ChunkSize = chunkSize;
            ChunkCountX = (grid.Width + chunkSize - 1) / chunkSize;
            ChunkCountZ = (grid.Height + chunkSize - 1) / chunkSize;

            Palette = new Color32[grid.Materials.MaxId + 1];
            for (var id = 0; id < Palette.Length; id++)
            {
                var materialId = new MaterialId((byte)id);
                Palette[id] = grid.Materials.Contains(materialId)
                    ? grid.Materials.Get(materialId).Color
                    : MissingMaterialColor;
            }

            _lod = lod;
            _levels = lod != null ? Math.Max(1, Math.Min(LodLevels, MaxLodLevels)) : 1;
            _builder = new TerrainMeshBuilder(chunkSize * chunkSize * 4);
            _chunks = new Chunk[ChunkCountX * ChunkCountZ];
            _dirty = new bool[_chunks.Length];
            _workedOn = new int[_chunks.Length];
            _distance = new float[_chunks.Length];
            _dirtyCell = new bool[grid.Width * grid.Height];
            for (var cz = 0; cz < ChunkCountZ; cz++)
            {
                for (var cx = 0; cx < ChunkCountX; cx++)
                {
                    var index = cz * ChunkCountX + cx;
                    _chunks[index] = new Chunk(cx, cz, chunkSize, grid.CellSize, parent, material, _levels);
                    MarkChunkDirty(index);
                }
            }

            // The first build makes each chunk once, at the level the viewer wants it; with no
            // viewer yet, at the coarsest.
            if (lod != null && lod.HasViewer)
                ChooseLevels(lod.Viewer);
            else
                foreach (var chunk in _chunks)
                    chunk.Wanted = _levels - 1;

            Grid.CellChanged += MarkDirty;
        }

        protected TerrainGrid Grid { get; }

        /// <summary>Colour per material id.</summary>
        protected Color32[] Palette { get; }

        public int ChunkSize { get; }

        public int ChunkCountX { get; }

        public int ChunkCountZ { get; }

        /// <summary>Levels of detail in use: 1 without a <see cref="TerrainLod"/>.</summary>
        public int LevelCount => _levels;

        /// <summary>Chunks that the next <see cref="Rebuild"/> will rebuild because of edits.</summary>
        public int PendingChunkCount
        {
            get
            {
                ResolveDirtyCells();
                return _dirtyChunks.Count;
            }
        }

        /// <summary>Cells changed since the last <see cref="Rebuild"/> and not yet expanded into chunks.</summary>
        public int PendingCellCount => _dirtyCells.Count;

        /// <summary>Triangles across all chunks as shown. For perf reporting.</summary>
        public long TriangleCount
        {
            get
            {
                long total = 0;
                foreach (var chunk in _chunks)
                    total += chunk.TriangleCount;
                return total;
            }
        }

        /// <summary>Chunks still waiting for the level of detail they want.</summary>
        public int LodPending
        {
            get
            {
                var pending = 0;
                foreach (var chunk in _chunks)
                    if (chunk.Wanted != chunk.Level)
                        pending++;
                return pending;
            }
        }

        /// <summary>The level of detail a chunk is showing (-1 before its first build). For tests and readouts.</summary>
        public int GetChunkLevel(int chunkX, int chunkZ) => _chunks[chunkZ * ChunkCountX + chunkX].Level;

        /// <summary>The mesh a chunk is showing. For tests and debugging.</summary>
        public Mesh GetChunkMesh(int chunkX, int chunkZ) => _chunks[chunkZ * ChunkCountX + chunkX].ShownMesh;

        /// <summary>Records that cell (x, z) changed. O(1) and deduplicated; the chunks come later.</summary>
        public void MarkDirty(int x, int z)
        {
            if (!Grid.InBounds(x, z))
                return;
            var cell = z * Grid.Width + x;
            if (_dirtyCell[cell])
                return;
            _dirtyCell[cell] = true;
            _dirtyCells.Add(cell);
        }

        /// <summary>Flags, via <see cref="MarkCellsChunkDirty"/>, every chunk whose mesh depends on cell (x, z).</summary>
        protected abstract void MarkChunksAffectedBy(int x, int z);

        void ResolveDirtyCells()
        {
            foreach (var cell in _dirtyCells)
            {
                _dirtyCell[cell] = false;
                MarkChunksAffectedBy(cell % Grid.Width, cell / Grid.Width);
            }

            _dirtyCells.Clear();
        }

        /// <summary>
        /// Chooses each chunk's level from how far its middle is from <paramref name="viewer"/>,
        /// a point in the terrain's own space (metres). Call once a frame before
        /// <see cref="Rebuild"/>; does nothing without a <see cref="TerrainLod"/>.
        /// </summary>
        public void UpdateLod(Vector3 viewer)
        {
            if (_lod == null || _disposed)
                return;
            ChooseLevels(viewer);
        }

        void ChooseLevels(Vector3 viewer)
        {
            var span = ChunkSize * Grid.CellSize;
            var distances = _lod.Distances ?? Array.Empty<float>();
            var hysteresis = 1f + Mathf.Max(0f, _lod.Hysteresis);
            for (var i = 0; i < _chunks.Length; i++)
            {
                var chunk = _chunks[i];
                var dx = (chunk.X + 0.5f) * span - viewer.x;
                var dz = (chunk.Z + 0.5f) * span - viewer.z;
                var d = Mathf.Sqrt(dx * dx + dz * dz + viewer.y * viewer.y);
                _distance[i] = d;

                var level = 0;
                while (level < _levels - 1 && level < distances.Length && d > distances[level])
                    level++;
                // Stay a step finer until the chunk is well past the boundary, so one sitting on
                // it does not flick between two levels as the camera drifts.
                if (chunk.Level >= 0 && level == chunk.Level + 1 && d <= distances[chunk.Level] * hysteresis)
                    level = chunk.Level;
                chunk.Wanted = level;
            }
        }

        /// <summary>Chunks rebuilt by the last <see cref="Rebuild"/>. For perf reporting.</summary>
        public int LastRebuiltChunkCount { get; private set; }

        static readonly ProfilerMarker BuildMarker = new ProfilerMarker("TinyDiggers.ChunkBuild");
        static readonly ProfilerMarker UploadMarker = new ProfilerMarker("TinyDiggers.ChunkUpload");

        /// <summary>Chunks in one rebuild at or above which their geometry is built on worker threads.</summary>
        public const int ParallelFrom = 64;

        /// <summary>Chunks built on the workers before the main thread uploads them.</summary>
        const int ParallelBatch = 256;

        /// <summary>
        /// How many threads may build chunks at once through <see cref="BuildChunk(int, int, int, int, int, TerrainMeshBuilder)"/>.
        /// 1, the default, keeps every build on the main thread.
        /// </summary>
        protected virtual int ParallelWorkers => 1;

        /// <summary>Levels of detail the subclass can build (<see cref="BuildChunkLevel"/>); 1 means level 0 only.</summary>
        protected virtual int LodLevels => 1;

        public void Rebuild()
        {
            if (_disposed)
                return;

            _frame++;
            _work.Clear();

            // Edits first, all of them: a stale mesh on screen is a bug, not a detail.
            ResolveDirtyCells();
            foreach (var index in _dirtyChunks)
            {
                var chunk = _chunks[index];
                chunk.MarkStale();
                _work.Add(new Work(index, chunk.Level >= 0 ? chunk.Level : chunk.Wanted));
                _workedOn[index] = _frame;
                _dirty[index] = false;
            }

            _dirtyChunks.Clear();

            if (_lod != null)
                CollectLodWork();

            LastRebuiltChunkCount = _work.Count;
            if (_work.Count == 0)
                return;

            var workers = ParallelWorkers;
            if (workers > 1 && _work.Count >= ParallelFrom)
                BuildInParallel(workers);
            else
                BuildInSeries();
        }

        /// <summary>Swaps in levels already built, and queues the nearest few that are not.</summary>
        void CollectLodWork()
        {
            _lodCandidates.Clear();
            for (var i = 0; i < _chunks.Length; i++)
            {
                var chunk = _chunks[i];
                if (chunk.Wanted == chunk.Level || _workedOn[i] == _frame)
                    continue;
                if (chunk.IsReady(chunk.Wanted))
                    chunk.Show(chunk.Wanted);
                else
                    _lodCandidates.Add(i);
            }

            if (_lodCandidates.Count == 0)
                return;
            var budget = Math.Max(1, _lod.BuildsPerFrame);
            if (_lodCandidates.Count > budget)
                _lodCandidates.Sort((a, b) => _distance[a].CompareTo(_distance[b]));
            for (var k = 0; k < _lodCandidates.Count && k < budget; k++)
            {
                var index = _lodCandidates[k];
                _work.Add(new Work(index, _chunks[index].Wanted));
                _workedOn[index] = _frame;
            }
        }

        void BuildInSeries()
        {
            foreach (var work in _work)
            {
                _builder.Clear();
                using (BuildMarker.Auto())
                    BuildWork(0, work, _builder);
                Finish(work, _builder);
            }
        }

        void BuildInParallel(int workers)
        {
            BeforeParallelBuild(workers);
            var builders = new TerrainMeshBuilder[Math.Min(ParallelBatch, _work.Count)];
            for (var i = 0; i < builders.Length; i++)
                builders[i] = new TerrainMeshBuilder(ChunkSize * ChunkSize * 4);

            for (var start = 0; start < _work.Count; start += builders.Length)
            {
                var count = Math.Min(builders.Length, _work.Count - start);
                // Each worker takes every workers-th chunk of the batch, with its own scratch.
                Parallel.For(0, workers, worker =>
                {
                    for (var k = worker; k < count; k += workers)
                    {
                        var builder = builders[k];
                        builder.Clear();
                        using (BuildMarker.Auto())
                            BuildWork(worker, _work[start + k], builder);
                    }
                });

                for (var k = 0; k < count; k++)
                    Finish(_work[start + k], builders[k]);
            }
        }

        void BuildWork(int worker, Work work, TerrainMeshBuilder builder)
        {
            var chunk = _chunks[work.Chunk];
            var originX = chunk.X * ChunkSize;
            var originZ = chunk.Z * ChunkSize;
            var width = Math.Min(ChunkSize, Grid.Width - originX);
            var depth = Math.Min(ChunkSize, Grid.Height - originZ);
            if (work.Level == 0)
                BuildChunk(worker, originX, originZ, width, depth, builder);
            else
                BuildChunkLevel(worker, work.Level, originX, originZ, width, depth, builder);
        }

        void Finish(Work work, TerrainMeshBuilder builder)
        {
            var chunk = _chunks[work.Chunk];
            using (UploadMarker.Auto())
                chunk.Built(work.Level, builder.ApplyTo(chunk.MeshFor(work.Level)));
            chunk.Show(work.Level);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Grid.CellChanged -= MarkDirty;
            foreach (var chunk in _chunks)
                chunk.Destroy();
        }

        /// <summary>
        /// Fills <paramref name="builder"/> with the chunk whose first cell is (originX, originZ).
        /// Positions are local to the chunk: cell (originX + i, originZ + j) spans i..i+1, j..j+1,
        /// in cells; the chunk's transform scales them to metres.
        /// </summary>
        protected abstract void BuildChunk(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder);

        /// <summary>
        /// As <see cref="BuildChunk(int, int, int, int, TerrainMeshBuilder)"/>, on worker thread
        /// <paramref name="worker"/> (0 .. <see cref="ParallelWorkers"/> - 1), while the grid is not
        /// being changed. Overridden by renderers that set <see cref="ParallelWorkers"/> above 1:
        /// two workers never share scratch.
        /// </summary>
        protected virtual void BuildChunk(int worker, int originX, int originZ, int width, int depth, TerrainMeshBuilder builder) =>
            BuildChunk(originX, originZ, width, depth, builder);

        /// <summary>
        /// The chunk at level of detail <paramref name="level"/> (1 or more): one quad per
        /// 2^level cells square, same positions as level 0. Only called for levels below
        /// <see cref="LodLevels"/>.
        /// </summary>
        protected virtual void BuildChunkLevel(int worker, int level, int originX, int originZ, int width, int depth, TerrainMeshBuilder builder) =>
            throw new NotSupportedException($"{GetType().Name} has no level {level}.");

        /// <summary>Called on the main thread before a parallel build, so a subclass can make each worker's scratch.</summary>
        protected virtual void BeforeParallelBuild(int workers)
        {
        }

        /// <summary>Flags the chunk containing cell (x, z); cells off the grid are ignored.</summary>
        protected void MarkCellsChunkDirty(int x, int z)
        {
            if (Grid.InBounds(x, z))
                MarkChunkDirty(z / ChunkSize * ChunkCountX + x / ChunkSize);
        }

        void MarkChunkDirty(int index)
        {
            if (_dirty[index])
                return;
            _dirty[index] = true;
            _dirtyChunks.Add(index);
        }

        sealed class Chunk
        {
            readonly GameObject _gameObject;
            readonly MeshFilter _filter;
            readonly Mesh[] _meshes;
            readonly bool[] _built;
            readonly bool[] _stale;
            readonly int[] _triangles;
            readonly string _name;

            public Chunk(int x, int z, int chunkSize, float cellSize, Transform parent, Material material, int levels)
            {
                X = x;
                Z = z;
                _name = $"Terrain Chunk {x},{z}";
                _meshes = new Mesh[levels];
                _built = new bool[levels];
                _stale = new bool[levels];
                _triangles = new int[levels];

                _gameObject = new GameObject($"Chunk {x},{z}") { hideFlags = HideFlags.DontSave };
                _gameObject.transform.SetParent(parent, false);
                // Meshes are built in cells (one unit per cell, heights in metres); the chunk's
                // scale turns cells into metres. Unity transforms the normals by the inverse
                // scale, so a slope built per cell comes out right per metre.
                _gameObject.transform.localPosition = new Vector3(x * chunkSize * cellSize, 0f, z * chunkSize * cellSize);
                _gameObject.transform.localScale = new Vector3(cellSize, 1f, cellSize);
                _filter = _gameObject.AddComponent<MeshFilter>();
                var meshRenderer = _gameObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = material;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            public int X { get; }

            public int Z { get; }

            /// <summary>The level on screen, or -1 before the first build.</summary>
            public int Level { get; private set; } = -1;

            /// <summary>The level the viewer's distance asks for.</summary>
            public int Wanted { get; set; }

            public Mesh ShownMesh => Level >= 0 ? _meshes[Level] : null;

            public int TriangleCount => Level >= 0 ? _triangles[Level] : 0;

            public Mesh MeshFor(int level)
            {
                if (_meshes[level] == null)
                {
                    _meshes[level] = new Mesh { name = level == 0 ? _name : $"{_name} L{level}" };
                    _meshes[level].MarkDynamic();
                }

                return _meshes[level];
            }

            public bool IsReady(int level) => _built[level] && !_stale[level];

            public void Built(int level, int triangles)
            {
                _built[level] = true;
                _stale[level] = false;
                _triangles[level] = triangles;
            }

            public void MarkStale()
            {
                for (var level = 0; level < _stale.Length; level++)
                    _stale[level] = true;
            }

            public void Show(int level)
            {
                Level = level;
                _filter.sharedMesh = _meshes[level];
            }

            public void Destroy()
            {
                DestroyObject(_gameObject);
                foreach (var mesh in _meshes)
                    DestroyObject(mesh);
            }

            static void DestroyObject(Object target)
            {
                if (target == null)
                    return;
                if (Application.isPlaying)
                    Object.Destroy(target);
                else
                    Object.DestroyImmediate(target);
            }
        }
    }

    /// <summary>
    /// Level-of-detail settings for a <see cref="ChunkedTerrainRenderer"/>: how far from the viewer
    /// each coarser level starts, and how many chunk levels may be built a frame.
    /// </summary>
    public sealed class TerrainLod
    {
        /// <summary>
        /// Metres from the viewer to a chunk's middle beyond which level 1 (2 x 2 cells a quad),
        /// level 2 (4 x 4) and level 3 (8 x 8) are used.
        /// </summary>
        public float[] Distances = { 120f, 300f, 650f };

        /// <summary>Share past a boundary a chunk must go before it drops to the coarser level.</summary>
        public float Hysteresis = 0.1f;

        /// <summary>Most chunk levels built in one frame (edits are always rebuilt, and do not count).</summary>
        public int BuildsPerFrame = 48;

        /// <summary>Where the viewer is at start-up, in the terrain's own space, if known.</summary>
        public Vector3 Viewer;

        public bool HasViewer;
    }
}
