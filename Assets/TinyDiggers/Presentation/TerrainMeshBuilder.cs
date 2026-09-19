using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Scratch geometry for one chunk. Every quad carries:
    /// - the vertex colour, which is what a gentle face shows;
    /// - UV0, the vertex's position within the quad (0..1 on each axis);
    /// - UV1, an "exposed" colour the terrain shader blends towards on steep faces;
    /// - UV2, which of the quad's edges (west, east, south, north) border a different top
    ///   material, for the shader's faint edge line.
    ///
    /// Plain arrays, grown on demand and reused for every chunk, so after warm-up building
    /// allocates nothing. Quads are always indexed the same way (0,1,2 / 0,2,3), so the index
    /// buffer depends only on the quad count: <see cref="ApplyTo"/> skips re-uploading it when a
    /// chunk keeps the same number of quads, which the smoothed renderer always does.
    /// </summary>
    public sealed class TerrainMeshBuilder
    {
        const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        static readonly Vector2 CornerA = new Vector2(0f, 0f);
        static readonly Vector2 CornerB = new Vector2(0f, 1f);
        static readonly Vector2 CornerC = new Vector2(1f, 1f);
        static readonly Vector2 CornerD = new Vector2(1f, 0f);

        Vector3[] _vertices;
        Vector3[] _normals;
        Color32[] _colors;
        Vector2[] _local;
        Vector4[] _exposed;
        Vector4[] _edges;
        int[] _indices = new int[0];
        int _count;
        float _minY;
        float _maxY;
        float _maxX;
        float _maxZ;

        public TerrainMeshBuilder(int initialVertexCapacity)
        {
            Allocate(Math.Max(initialVertexCapacity, 4));
            Clear();
        }

        public int VertexCount => _count;

        public void Clear()
        {
            _count = 0;
            _minY = float.MaxValue;
            _maxY = float.MinValue;
            _maxX = 0f;
            _maxZ = 0f;
        }

        /// <summary>A flat-shaded quad with no edge lines. Corners clockwise as seen from the front.</summary>
        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 color, Color32 exposed)
        {
            AddQuad(a, b, c, d, normal, normal, normal, normal, color, exposed, Vector4.zero);
        }

        /// <summary>
        /// A quad with a normal per corner. Corners clockwise as seen from the front, which is
        /// Unity's front face; for a terrain top that is (i, j), (i, j+1), (i+1, j+1), (i+1, j).
        /// <paramref name="edges"/> flags the west, east, south and north edges (1 = draw a line).
        /// </summary>
        public void AddQuad(
            Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 normalA, Vector3 normalB, Vector3 normalC, Vector3 normalD,
            Color32 color, Color32 exposed, Vector4 edges)
        {
            if (_count + 4 > _vertices.Length)
                Grow(_vertices.Length * 2);

            var v = _count;
            _vertices[v] = a;
            _vertices[v + 1] = b;
            _vertices[v + 2] = c;
            _vertices[v + 3] = d;
            _normals[v] = normalA;
            _normals[v + 1] = normalB;
            _normals[v + 2] = normalC;
            _normals[v + 3] = normalD;
            _local[v] = CornerA;
            _local[v + 1] = CornerB;
            _local[v + 2] = CornerC;
            _local[v + 3] = CornerD;
            var exposedValue = new Vector4(exposed.r / 255f, exposed.g / 255f, exposed.b / 255f, 1f);
            for (var k = 0; k < 4; k++)
            {
                _colors[v + k] = color;
                _exposed[v + k] = exposedValue;
                _edges[v + k] = edges;
            }

            _count += 4;
            Track(a);
            Track(b);
            Track(c);
            Track(d);
        }

        /// <summary>Uploads the geometry and returns its triangle count.</summary>
        public int ApplyTo(Mesh mesh)
        {
            var quads = _count / 4;
            var indexCount = quads * 6;
            var sameTopology = mesh.vertexCount == _count && mesh.subMeshCount == 1 && mesh.GetIndexCount(0) == indexCount;

            if (!sameTopology)
            {
                mesh.Clear();
                mesh.indexFormat = _count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            }

            mesh.SetVertices(_vertices, 0, _count, UploadFlags);
            mesh.SetNormals(_normals, 0, _count, UploadFlags);
            mesh.SetColors(_colors, 0, _count, UploadFlags);
            mesh.SetUVs(0, _local, 0, _count, UploadFlags);
            mesh.SetUVs(1, _exposed, 0, _count, UploadFlags);
            mesh.SetUVs(2, _edges, 0, _count, UploadFlags);

            if (!sameTopology)
            {
                EnsureIndices(indexCount);
                mesh.SetTriangles(_indices, 0, indexCount, 0, false);
            }

            if (_count > 0)
            {
                var min = new Vector3(0f, _minY, 0f);
                var max = new Vector3(_maxX, _maxY, _maxZ);
                mesh.bounds = new Bounds((min + max) * 0.5f, max - min);
            }

            return quads * 2;
        }

        void EnsureIndices(int indexCount)
        {
            if (_indices.Length >= indexCount)
                return;

            var indices = new int[Math.Max(indexCount, _indices.Length * 2)];
            for (int i = 0, quad = 0; i + 5 < indices.Length; i += 6, quad += 4)
            {
                indices[i] = quad;
                indices[i + 1] = quad + 1;
                indices[i + 2] = quad + 2;
                indices[i + 3] = quad;
                indices[i + 4] = quad + 2;
                indices[i + 5] = quad + 3;
            }

            _indices = indices;
        }

        void Allocate(int capacity)
        {
            _vertices = new Vector3[capacity];
            _normals = new Vector3[capacity];
            _colors = new Color32[capacity];
            _local = new Vector2[capacity];
            _exposed = new Vector4[capacity];
            _edges = new Vector4[capacity];
        }

        void Grow(int capacity)
        {
            Array.Resize(ref _vertices, capacity);
            Array.Resize(ref _normals, capacity);
            Array.Resize(ref _colors, capacity);
            Array.Resize(ref _local, capacity);
            Array.Resize(ref _exposed, capacity);
            Array.Resize(ref _edges, capacity);
        }

        void Track(Vector3 p)
        {
            if (p.y < _minY) _minY = p.y;
            if (p.y > _maxY) _maxY = p.y;
            if (p.x > _maxX) _maxX = p.x;
            if (p.z > _maxZ) _maxZ = p.z;
        }
    }
}
