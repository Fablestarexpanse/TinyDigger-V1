using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
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
    /// rebuilds each of those chunks once.
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

        static readonly Color32 MissingMaterialColor = new Color32(255, 0, 255, 255);

        readonly Chunk[] _chunks;
        readonly bool[] _dirty;
        readonly List<int> _dirtyChunks = new List<int>();
        readonly bool[] _dirtyCell;
        readonly List<int> _dirtyCells = new List<int>();
        readonly TerrainMeshBuilder _builder;
        bool _disposed;

        protected ChunkedTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize)
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

            _builder = new TerrainMeshBuilder(chunkSize * chunkSize * 4);
            _chunks = new Chunk[ChunkCountX * ChunkCountZ];
            _dirty = new bool[_chunks.Length];
            _dirtyCell = new bool[grid.Width * grid.Height];
            for (var cz = 0; cz < ChunkCountZ; cz++)
            {
                for (var cx = 0; cx < ChunkCountX; cx++)
                {
                    var index = cz * ChunkCountX + cx;
                    _chunks[index] = new Chunk(cx, cz, chunkSize, parent, material);
                    MarkChunkDirty(index);
                }
            }

            Grid.CellChanged += MarkDirty;
        }

        protected TerrainGrid Grid { get; }

        /// <summary>Colour per material id.</summary>
        protected Color32[] Palette { get; }

        public int ChunkSize { get; }

        public int ChunkCountX { get; }

        public int ChunkCountZ { get; }

        /// <summary>Chunks that the next <see cref="Rebuild"/> will rebuild.</summary>
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

        /// <summary>Triangles across all chunks as last built. For perf reporting.</summary>
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

        /// <summary>The mesh for one chunk. For tests and debugging.</summary>
        public Mesh GetChunkMesh(int chunkX, int chunkZ) => _chunks[chunkZ * ChunkCountX + chunkX].Mesh;

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

        /// <summary>Chunks rebuilt by the last <see cref="Rebuild"/>. For perf reporting.</summary>
        public int LastRebuiltChunkCount { get; private set; }

        static readonly ProfilerMarker BuildMarker = new ProfilerMarker("TinyDiggers.ChunkBuild");
        static readonly ProfilerMarker UploadMarker = new ProfilerMarker("TinyDiggers.ChunkUpload");

        public void Rebuild()
        {
            if (_disposed)
                return;

            ResolveDirtyCells();
            LastRebuiltChunkCount = _dirtyChunks.Count;
            foreach (var index in _dirtyChunks)
            {
                var chunk = _chunks[index];
                var originX = chunk.X * ChunkSize;
                var originZ = chunk.Z * ChunkSize;
                _builder.Clear();
                using (BuildMarker.Auto())
                {
                    BuildChunk(
                        originX,
                        originZ,
                        Math.Min(ChunkSize, Grid.Width - originX),
                        Math.Min(ChunkSize, Grid.Height - originZ),
                        _builder);
                }

                using (UploadMarker.Auto())
                    chunk.TriangleCount = _builder.ApplyTo(chunk.Mesh);
                _dirty[index] = false;
            }

            _dirtyChunks.Clear();
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
        /// Positions are local to the chunk: cell (originX + i, originZ + j) spans i..i+1, j..j+1.
        /// </summary>
        protected abstract void BuildChunk(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder);

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

            public Chunk(int x, int z, int chunkSize, Transform parent, Material material)
            {
                X = x;
                Z = z;

                Mesh = new Mesh { name = $"Terrain Chunk {x},{z}" };
                Mesh.MarkDynamic();

                _gameObject = new GameObject($"Chunk {x},{z}") { hideFlags = HideFlags.DontSave };
                _gameObject.transform.SetParent(parent, false);
                _gameObject.transform.localPosition = new Vector3(x * chunkSize, 0f, z * chunkSize);
                _gameObject.AddComponent<MeshFilter>().sharedMesh = Mesh;
                var meshRenderer = _gameObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = material;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            public int X { get; }

            public int Z { get; }

            public Mesh Mesh { get; }

            public int TriangleCount { get; set; }

            public void Destroy()
            {
                DestroyObject(_gameObject);
                DestroyObject(Mesh);
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

}
