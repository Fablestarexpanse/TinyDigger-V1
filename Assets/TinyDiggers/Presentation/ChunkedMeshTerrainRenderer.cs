using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Draws a <see cref="TerrainGrid"/> as one mesh per square chunk of cells, rebuilding only
    /// the chunks whose cells changed.
    ///
    /// Heights live at cell corners: each corner is the average surface height of the up to four
    /// cells that touch it, so the surface is continuous. Each cell is its own quad with its own
    /// four vertices, which lets it carry its top material's colour and a flat normal without
    /// bleeding into its neighbours — the low-poly look.
    ///
    /// Plain C#, not a MonoBehaviour: the owner calls <see cref="Rebuild"/> once a frame and
    /// <see cref="Dispose"/> when done.
    /// </summary>
    public sealed class ChunkedMeshTerrainRenderer : ITerrainRenderer, IDisposable
    {
        public const int DefaultChunkSize = 32;

        /// <summary>Four vertices per cell must fit a 16-bit index buffer.</summary>
        public const int MaxChunkSize = 127;

        static readonly Color32 MissingMaterialColor = new Color32(255, 0, 255, 255);

        readonly TerrainGrid _grid;
        readonly int _chunkSize;
        readonly Chunk[] _chunks;
        readonly bool[] _dirty;
        readonly List<int> _dirtyChunks = new List<int>();
        readonly Color32[] _palette;

        // Scratch buffers sized for a full chunk and reused by every build, so rebuilding
        // allocates nothing on the managed heap.
        readonly float[] _cornerHeights;
        readonly Vector3[] _vertices;
        readonly Vector3[] _normals;
        readonly Color32[] _colors;
        readonly ushort[] _indices;

        bool _disposed;

        public ChunkedMeshTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize = DefaultChunkSize)
        {
            if (chunkSize < 1 || chunkSize > MaxChunkSize)
                throw new ArgumentOutOfRangeException(nameof(chunkSize), $"Chunk size must be 1..{MaxChunkSize}.");

            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _chunkSize = chunkSize;
            ChunkCountX = (grid.Width + chunkSize - 1) / chunkSize;
            ChunkCountZ = (grid.Height + chunkSize - 1) / chunkSize;

            _palette = new Color32[grid.Materials.MaxId + 1];
            for (var id = 0; id < _palette.Length; id++)
            {
                var materialId = new MaterialId((byte)id);
                _palette[id] = grid.Materials.Contains(materialId)
                    ? grid.Materials.Get(materialId).Color
                    : MissingMaterialColor;
            }

            var cellsPerChunk = chunkSize * chunkSize;
            _cornerHeights = new float[(chunkSize + 1) * (chunkSize + 1)];
            _vertices = new Vector3[cellsPerChunk * 4];
            _normals = new Vector3[cellsPerChunk * 4];
            _colors = new Color32[cellsPerChunk * 4];
            _indices = new ushort[cellsPerChunk * 6];

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

            _grid.CellChanged += MarkDirty;
            Rebuild();
        }

        public int ChunkSize => _chunkSize;

        public int ChunkCountX { get; }

        public int ChunkCountZ { get; }

        /// <summary>Chunks flagged since the last <see cref="Rebuild"/>.</summary>
        public int PendingChunkCount => _dirtyChunks.Count;

        /// <summary>The mesh for one chunk. For tests and debugging.</summary>
        public Mesh GetChunkMesh(int chunkX, int chunkZ) => _chunks[chunkZ * ChunkCountX + chunkX].Mesh;

        public void MarkDirty(int x, int z)
        {
            // A cell sets the four corners around it, and those corners are shared with its eight
            // neighbours, any of which may sit in the next chunk over.
            var minChunkX = Math.Max(x - 1, 0) / _chunkSize;
            var maxChunkX = Math.Min(x + 1, _grid.Width - 1) / _chunkSize;
            var minChunkZ = Math.Max(z - 1, 0) / _chunkSize;
            var maxChunkZ = Math.Min(z + 1, _grid.Height - 1) / _chunkSize;

            for (var cz = minChunkZ; cz <= maxChunkZ; cz++)
                for (var cx = minChunkX; cx <= maxChunkX; cx++)
                    MarkChunkDirty(cz * ChunkCountX + cx);
        }

        public void Rebuild()
        {
            if (_disposed)
                return;

            foreach (var index in _dirtyChunks)
            {
                BuildChunk(_chunks[index]);
                _dirty[index] = false;
            }

            _dirtyChunks.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _grid.CellChanged -= MarkDirty;
            foreach (var chunk in _chunks)
                chunk.Destroy();
        }

        void MarkChunkDirty(int index)
        {
            if (_dirty[index])
                return;
            _dirty[index] = true;
            _dirtyChunks.Add(index);
        }

        void BuildChunk(Chunk chunk)
        {
            var originX = chunk.X * _chunkSize;
            var originZ = chunk.Z * _chunkSize;
            var width = Math.Min(_chunkSize, _grid.Width - originX);
            var depth = Math.Min(_chunkSize, _grid.Height - originZ);

            var cornersPerRow = width + 1;
            for (var j = 0; j <= depth; j++)
                for (var i = 0; i <= width; i++)
                    _cornerHeights[j * cornersPerRow + i] = CornerHeight(originX + i, originZ + j);

            var minHeight = float.MaxValue;
            var maxHeight = float.MinValue;
            var vertex = 0;
            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var h00 = _cornerHeights[j * cornersPerRow + i];
                    var h10 = _cornerHeights[j * cornersPerRow + i + 1];
                    var h01 = _cornerHeights[(j + 1) * cornersPerRow + i];
                    var h11 = _cornerHeights[(j + 1) * cornersPerRow + i + 1];

                    // Cross of the two diagonals: flat per quad, whatever the corners do.
                    var rise = h11 - h00;
                    var fall = h01 - h10;
                    var normal = new Vector3(fall - rise, 2f, -(rise + fall)).normalized;
                    var color = _palette[_grid.GetTopMaterial(originX + i, originZ + j).Value];

                    _vertices[vertex] = new Vector3(i, h00, j);
                    _vertices[vertex + 1] = new Vector3(i, h01, j + 1);
                    _vertices[vertex + 2] = new Vector3(i + 1, h11, j + 1);
                    _vertices[vertex + 3] = new Vector3(i + 1, h10, j);
                    for (var k = 0; k < 4; k++)
                    {
                        _normals[vertex + k] = normal;
                        _colors[vertex + k] = color;
                    }

                    minHeight = Math.Min(minHeight, Math.Min(Math.Min(h00, h01), Math.Min(h10, h11)));
                    maxHeight = Math.Max(maxHeight, Math.Max(Math.Max(h00, h01), Math.Max(h10, h11)));
                    vertex += 4;
                }
            }

            var mesh = chunk.Mesh;
            var topologyChanged = mesh.vertexCount != vertex;
            if (topologyChanged)
                mesh.Clear();

            mesh.SetVertices(_vertices, 0, vertex);
            mesh.SetNormals(_normals, 0, vertex);
            mesh.SetColors(_colors, 0, vertex);

            // Every cell is a quad in a fixed order, so the index buffer depends only on the
            // chunk's size and is written once.
            if (topologyChanged)
            {
                var index = 0;
                for (var quad = 0; quad < vertex; quad += 4)
                {
                    _indices[index++] = (ushort)quad;
                    _indices[index++] = (ushort)(quad + 1);
                    _indices[index++] = (ushort)(quad + 2);
                    _indices[index++] = (ushort)quad;
                    _indices[index++] = (ushort)(quad + 2);
                    _indices[index++] = (ushort)(quad + 3);
                }

                mesh.SetIndexBufferParams(index, IndexFormat.UInt16);
                mesh.SetIndexBufferData(_indices, 0, 0, index, MeshUpdateFlags.DontRecalculateBounds);
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, index), MeshUpdateFlags.DontRecalculateBounds);
            }

            var centre = new Vector3(width * 0.5f, (minHeight + maxHeight) * 0.5f, depth * 0.5f);
            var size = new Vector3(width, maxHeight - minHeight, depth);
            mesh.bounds = new Bounds(centre, size);
        }

        float CornerHeight(int cornerX, int cornerZ)
        {
            var sum = 0f;
            var count = 0;
            for (var z = cornerZ - 1; z <= cornerZ; z++)
            {
                for (var x = cornerX - 1; x <= cornerX; x++)
                {
                    if (!_grid.InBounds(x, z))
                        continue;
                    sum += _grid.GetSurfaceHeight(x, z);
                    count++;
                }
            }

            return sum / count;
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
