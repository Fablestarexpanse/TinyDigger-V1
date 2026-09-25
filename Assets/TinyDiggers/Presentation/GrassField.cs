using System.Collections.Generic;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Where grass tufts stand, worked out from the land and nothing else, so it can be tested
    /// without a scene. The map is cut into square chunks; each chunk lists its tufts as
    /// transforms, and a chunk is rebuilt when a cell in it changes, so digging a cell takes its
    /// grass with it.
    ///
    /// A cell grows grass when its top is topsoil and it is dry land. Placement is seeded per
    /// cell, so the same map always grows the same meadow and a rebuilt chunk does not shuffle.
    /// </summary>
    public sealed class GrassField
    {
        public readonly int ChunkSize;
        public readonly int ChunksX;
        public readonly int ChunksZ;

        readonly TerrainGrid _grid;
        readonly float _density;
        readonly int _seed;
        readonly Vector2 _scale;
        readonly List<Matrix4x4>[] _chunks;
        readonly Bounds[] _bounds;

        /// <param name="density">Tufts per grass cell, on average (fractions are spread by the seed).</param>
        /// <param name="scale">Smallest and largest tuft scale.</param>
        public GrassField(TerrainGrid grid, float density = 1.5f, int chunkSize = 32, int seed = 1, Vector2? scale = null)
        {
            _grid = grid;
            _density = Mathf.Max(0f, density);
            _seed = seed;
            _scale = scale ?? new Vector2(0.75f, 1.25f);
            ChunkSize = Mathf.Max(4, chunkSize);
            ChunksX = (grid.Width + ChunkSize - 1) / ChunkSize;
            ChunksZ = (grid.Height + ChunkSize - 1) / ChunkSize;
            _chunks = new List<Matrix4x4>[ChunksX * ChunksZ];
            _bounds = new Bounds[_chunks.Length];
            for (var i = 0; i < _chunks.Length; i++)
                Rebuild(i % ChunksX, i / ChunksX);
        }

        public int TuftCount
        {
            get
            {
                var total = 0;
                foreach (var chunk in _chunks)
                    total += chunk.Count;
                return total;
            }
        }

        public IReadOnlyList<Matrix4x4> Chunk(int cx, int cz) => _chunks[cz * ChunksX + cx];

        public Bounds ChunkBounds(int cx, int cz) => _bounds[cz * ChunksX + cx];

        /// <summary>Whether grass grows on this cell: dry land with topsoil on top.</summary>
        public bool Grows(int x, int z) =>
            _grid.IsGround(x, z) && !_grid.IsWater(x, z) && _grid.GetTopMaterial(x, z) == MaterialTable.Topsoil;

        /// <summary>Call when a cell changes; rebuilds the chunk it is in. Returns that chunk's index.</summary>
        public int OnCellChanged(int x, int z)
        {
            var cx = Mathf.Clamp(x / ChunkSize, 0, ChunksX - 1);
            var cz = Mathf.Clamp(z / ChunkSize, 0, ChunksZ - 1);
            Rebuild(cx, cz);
            return cz * ChunksX + cx;
        }

        void Rebuild(int cx, int cz)
        {
            var index = cz * ChunksX + cx;
            var list = _chunks[index] ??= new List<Matrix4x4>();
            list.Clear();
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (var z = cz * ChunkSize; z < Mathf.Min(_grid.Height, (cz + 1) * ChunkSize); z++)
            {
                for (var x = cx * ChunkSize; x < Mathf.Min(_grid.Width, (cx + 1) * ChunkSize); x++)
                {
                    if (!Grows(x, z))
                        continue;

                    var random = new System.Random(unchecked(_seed * 73856093 ^ x * 19349663 ^ z * 83492791));
                    // Density is tufts per m², so the count per cell follows the cell's area.
                    var perCell = _density * _grid.CellArea;
                    var count = (int)perCell;
                    if (random.NextDouble() < perCell - count)
                        count++;
                    var height = _grid.GetSurfaceHeight(x, z);
                    for (var i = 0; i < count; i++)
                    {
                        var position = new Vector3((x + (float)random.NextDouble()) * _grid.CellSize, height, (z + (float)random.NextDouble()) * _grid.CellSize);
                        var rotation = Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
                        var size = Mathf.Lerp(_scale.x, _scale.y, (float)random.NextDouble());
                        list.Add(Matrix4x4.TRS(position, rotation, Vector3.one * size));
                        min = Vector3.Min(min, position);
                        max = Vector3.Max(max, position);
                    }
                }
            }

            _bounds[index] = list.Count == 0
                ? new Bounds(new Vector3((cx + 0.5f) * ChunkSize * _grid.CellSize, 0f, (cz + 0.5f) * ChunkSize * _grid.CellSize), Vector3.zero)
                : new Bounds((min + max) * 0.5f, max - min + new Vector3(2f, 2.5f, 2f));
        }
    }
}
