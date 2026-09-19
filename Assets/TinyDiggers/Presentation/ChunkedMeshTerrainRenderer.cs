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
    /// Every column is drawn as it is stored: a flat top at its true surface height, coloured by
    /// its top material, and vertical walls wherever it stands above a neighbour or the edge of
    /// the world. Walls are banded by the layers they cut through, so a cut through topsoil into
    /// rock shows a brown band over a grey one.
    ///
    /// Plain C#, not a MonoBehaviour: the owner calls <see cref="Rebuild"/> once a frame and
    /// <see cref="Dispose"/> when done.
    /// </summary>
    public sealed class ChunkedMeshTerrainRenderer : ITerrainRenderer, IDisposable
    {
        public const int DefaultChunkSize = 32;

        /// <summary>A sanity bound: much larger and a single chunk rebuild is long enough to hitch.</summary>
        public const int MaxChunkSize = 127;

        /// <summary>Wall bands thinner than this are not worth two triangles.</summary>
        const float MinWallHeight = 1e-4f;

        static readonly Color32 MissingMaterialColor = new Color32(255, 0, 255, 255);

        readonly TerrainGrid _grid;
        readonly int _chunkSize;
        readonly Chunk[] _chunks;
        readonly bool[] _dirty;
        readonly List<int> _dirtyChunks = new List<int>();
        readonly Color32[] _palette;

        // Scratch lists reused by every build: they keep their capacity, so after the first few
        // builds rebuilding allocates nothing on the managed heap.
        readonly List<Vector3> _vertices;
        readonly List<Vector3> _normals;
        readonly List<Color32> _colors;
        readonly List<int> _triangles;
        float _minY;
        float _maxY;

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

            // Sized for a typical chunk: a top and a wall band on each owned edge per cell.
            var typicalVertices = chunkSize * chunkSize * 12;
            _vertices = new List<Vector3>(typicalVertices);
            _normals = new List<Vector3>(typicalVertices);
            _colors = new List<Color32>(typicalVertices);
            _triangles = new List<int>(typicalVertices / 4 * 6);

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

        public void MarkDirty(int x, int z)
        {
            // A cell draws its own top plus the walls on its +x and +z edges. Its -x and -z edges
            // are drawn by the neighbours on those sides, whose walls take their height and
            // colours from this cell — so those neighbours' chunks must rebuild too.
            MarkCellsChunkDirty(x, z);
            if (x > 0)
                MarkCellsChunkDirty(x - 1, z);
            if (z > 0)
                MarkCellsChunkDirty(x, z - 1);
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

        void MarkCellsChunkDirty(int x, int z) => MarkChunkDirty(z / _chunkSize * ChunkCountX + x / _chunkSize);

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

            _vertices.Clear();
            _normals.Clear();
            _colors.Clear();
            _triangles.Clear();
            _minY = float.MaxValue;
            _maxY = float.MinValue;

            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var x = originX + i;
                    var z = originZ + j;
                    var height = _grid.GetSurfaceHeight(x, z);

                    AddQuad(
                        new Vector3(i, height, j),
                        new Vector3(i, height, j + 1),
                        new Vector3(i + 1, height, j + 1),
                        new Vector3(i + 1, height, j),
                        Vector3.up,
                        _palette[_grid.GetTopMaterial(x, z).Value]);

                    // The +x edge. Past the far border the world drops to zero, so the whole
                    // layer cake shows along the edge of the map.
                    var eastHeight = _grid.InBounds(x + 1, z) ? _grid.GetSurfaceHeight(x + 1, z) : 0f;
                    if (height > eastHeight)
                        AddWall(x, z, eastHeight, height, i + 1, j, i + 1, j + 1, Vector3.right);
                    else if (eastHeight > height)
                        AddWall(x + 1, z, height, eastHeight, i + 1, j + 1, i + 1, j, Vector3.left);

                    // The +z edge.
                    var northHeight = _grid.InBounds(x, z + 1) ? _grid.GetSurfaceHeight(x, z + 1) : 0f;
                    if (height > northHeight)
                        AddWall(x, z, northHeight, height, i + 1, j + 1, i, j + 1, Vector3.forward);
                    else if (northHeight > height)
                        AddWall(x, z + 1, height, northHeight, i, j + 1, i + 1, j + 1, Vector3.back);

                    // The near borders have no neighbour to own them, so the edge cell does.
                    if (x == 0)
                        AddWall(x, z, 0f, height, i, j + 1, i, j, Vector3.left);
                    if (z == 0)
                        AddWall(x, z, 0f, height, i, j, i + 1, j, Vector3.back);
                }
            }

            var mesh = chunk.Mesh;
            mesh.Clear();
            mesh.indexFormat = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.SetTriangles(_triangles, 0, false);

            if (_vertices.Count > 0)
            {
                var centre = new Vector3(width * 0.5f, (_minY + _maxY) * 0.5f, depth * 0.5f);
                mesh.bounds = new Bounds(centre, new Vector3(width, _maxY - _minY, depth));
            }

            chunk.TriangleCount = _triangles.Count / 3;
        }

        /// <summary>
        /// Adds the face of column (<paramref name="columnX"/>, <paramref name="columnZ"/>) between
        /// heights <paramref name="low"/> and <paramref name="high"/>, one band per layer it
        /// crosses. (ax, az) and (bx, bz) are the bottom edge's ends, left then right as seen
        /// from the side the wall faces.
        /// </summary>
        void AddWall(int columnX, int columnZ, float low, float high, float ax, float az, float bx, float bz, Vector3 normal)
        {
            if (high - low <= MinWallHeight)
                return;

            var layerCount = _grid.GetLayerCount(columnX, columnZ);
            var layerBase = 0f;
            for (var k = 0; k < layerCount && layerBase < high; k++)
            {
                var layer = _grid.GetLayer(columnX, columnZ, k);
                var layerTop = layerBase + layer.Thickness;
                var bandBottom = Math.Max(layerBase, low);
                var bandTop = Math.Min(layerTop, high);
                if (bandTop - bandBottom > MinWallHeight)
                {
                    AddQuad(
                        new Vector3(ax, bandBottom, az),
                        new Vector3(ax, bandTop, az),
                        new Vector3(bx, bandTop, bz),
                        new Vector3(bx, bandBottom, bz),
                        normal,
                        _palette[layer.Material.Value]);
                }

                layerBase = layerTop;
            }
        }

        /// <summary>Corners in clockwise order as seen from the front, which is Unity's front face.</summary>
        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 color)
        {
            var first = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            for (var k = 0; k < 4; k++)
            {
                _normals.Add(normal);
                _colors.Add(color);
            }

            _triangles.Add(first);
            _triangles.Add(first + 1);
            _triangles.Add(first + 2);
            _triangles.Add(first);
            _triangles.Add(first + 2);
            _triangles.Add(first + 3);

            _minY = Math.Min(_minY, Math.Min(a.y, b.y));
            _maxY = Math.Max(_maxY, Math.Max(b.y, c.y));
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
