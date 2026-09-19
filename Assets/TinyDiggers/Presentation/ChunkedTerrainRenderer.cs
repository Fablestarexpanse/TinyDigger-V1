using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Shared plumbing for renderers that draw a <see cref="TerrainGrid"/> as one mesh per square
    /// chunk of cells: the chunk objects, the dirty set, the material palette and the mesh upload.
    /// Subclasses decide which chunks an edit dirties and what geometry a chunk gets.
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

        /// <summary>Chunks flagged since the last <see cref="Rebuild"/>.</summary>
        public int PendingChunkCount => _dirtyChunks.Count;

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

        /// <summary>Flags every chunk whose mesh depends on cell (x, z).</summary>
        public abstract void MarkDirty(int x, int z);

        public void Rebuild()
        {
            if (_disposed)
                return;

            foreach (var index in _dirtyChunks)
            {
                var chunk = _chunks[index];
                var originX = chunk.X * ChunkSize;
                var originZ = chunk.Z * ChunkSize;
                _builder.Clear();
                BuildChunk(
                    originX,
                    originZ,
                    Math.Min(ChunkSize, Grid.Width - originX),
                    Math.Min(ChunkSize, Grid.Height - originZ),
                    _builder);
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

    /// <summary>
    /// Scratch geometry for one chunk. Every quad carries two colours: the vertex colour, which
    /// is what a gentle face shows, and an "exposed" colour in UV1, which the terrain shader
    /// blends towards on steep faces. The lists keep their capacity, so after the first few
    /// builds rebuilding allocates nothing on the managed heap.
    /// </summary>
    public sealed class TerrainMeshBuilder
    {
        readonly List<Vector3> _vertices;
        readonly List<Vector3> _normals;
        readonly List<Color32> _colors;
        readonly List<Vector4> _exposed;
        readonly List<int> _triangles;
        float _minY;
        float _maxY;
        float _maxX;
        float _maxZ;

        public TerrainMeshBuilder(int initialVertexCapacity)
        {
            _vertices = new List<Vector3>(initialVertexCapacity);
            _normals = new List<Vector3>(initialVertexCapacity);
            _colors = new List<Color32>(initialVertexCapacity);
            _exposed = new List<Vector4>(initialVertexCapacity);
            _triangles = new List<int>(initialVertexCapacity / 4 * 6);
        }

        public int VertexCount => _vertices.Count;

        public void Clear()
        {
            _vertices.Clear();
            _normals.Clear();
            _colors.Clear();
            _exposed.Clear();
            _triangles.Clear();
            _minY = float.MaxValue;
            _maxY = float.MinValue;
            _maxX = 0f;
            _maxZ = 0f;
        }

        /// <summary>Corners in clockwise order as seen from the front, which is Unity's front face.</summary>
        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 color, Color32 exposed)
        {
            var first = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            var exposedValue = new Vector4(exposed.r / 255f, exposed.g / 255f, exposed.b / 255f, 1f);
            for (var k = 0; k < 4; k++)
            {
                _normals.Add(normal);
                _colors.Add(color);
                _exposed.Add(exposedValue);
            }

            _triangles.Add(first);
            _triangles.Add(first + 1);
            _triangles.Add(first + 2);
            _triangles.Add(first);
            _triangles.Add(first + 2);
            _triangles.Add(first + 3);

            Track(a);
            Track(b);
            Track(c);
            Track(d);
        }

        /// <summary>Uploads the geometry and returns its triangle count.</summary>
        public int ApplyTo(Mesh mesh)
        {
            mesh.Clear();
            mesh.indexFormat = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.SetUVs(1, _exposed);
            mesh.SetTriangles(_triangles, 0, false);
            if (_vertices.Count > 0)
            {
                var min = new Vector3(0f, _minY, 0f);
                var max = new Vector3(_maxX, _maxY, _maxZ);
                mesh.bounds = new Bounds((min + max) * 0.5f, max - min);
            }

            return _triangles.Count / 3;
        }

        void Track(Vector3 p)
        {
            _minY = Math.Min(_minY, p.y);
            _maxY = Math.Max(_maxY, p.y);
            _maxX = Math.Max(_maxX, p.x);
            _maxZ = Math.Max(_maxZ, p.z);
        }
    }
}
