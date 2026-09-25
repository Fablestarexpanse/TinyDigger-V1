using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace PromptWaffle.Terrain.Rendering
{
    /// <summary>
    /// How far each cell lies in a hollow or on a ridge, for the terrain shader (Ronan, 2026-09-24:
    /// "do the hollows darkening first", after his references, which run deep green in every fold of
    /// the ground). One byte a cell: 128 is ground that sits level with what is round it, above that
    /// a hollow, below a ridge — the ground's height averaged over 2 m against its average within 6 m
    /// and within 15 m, so a gully and a whole valley floor both read. The 2 m average is what keeps
    /// the grid's half-metre height steps out of it: compared cell by cell, every terrace edge read as
    /// a little hollow and the map drew the steps as dark contour bands (first play run, 2026-09-24).
    ///
    /// Worked out on the CPU from the grid's heights, the whole map once and then only round what
    /// changes, like the cell map; the shader samples it bilinear, so it is smooth between cells.
    /// </summary>
    public sealed class TerrainHollowMap : IDisposable
    {
        /// <summary>Metres the ground is smoothed over first, then the near and far averages it is held against.</summary>
        public const float SmoothMetres = 2f;

        public const float NearMetres = 6f;

        public const float FarMetres = 15f;

        readonly TerrainGrid _grid;
        readonly Texture2D _texture;
        readonly byte[] _pixels;
        readonly float[] _heights;
        readonly int _smooth;
        readonly int _near;
        readonly int _far;
        readonly float _depth;
        RectInt _dirtyRect;
        bool _dirty;
        bool _disposed;

        /// <param name="depth">Metres below the average ground at which a cell counts as fully in a hollow.</param>
        public TerrainHollowMap(TerrainGrid grid, float depth)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _depth = Mathf.Max(0.05f, depth);
            _smooth = Mathf.Max(1, Mathf.RoundToInt(SmoothMetres / grid.CellSize));
            _near = Mathf.Max(_smooth + 1, Mathf.RoundToInt(NearMetres / grid.CellSize));
            _far = Mathf.Max(_near + 1, Mathf.RoundToInt(FarMetres / grid.CellSize));
            _texture = new Texture2D(grid.Width, grid.Height, TextureFormat.R8, mipChain: false, linear: true)
            {
                name = "Terrain Hollows",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            _pixels = new byte[grid.Width * grid.Height];
            _heights = new float[grid.Width * grid.Height];
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    _heights[z * grid.Width + x] = grid.GetSurfaceHeight(x, z);
            Hollows.Compute(_heights, grid.Width, grid.Height, new RectInt(0, 0, grid.Width, grid.Height), _smooth, _near, _far, _depth, _pixels);
            Upload();
            _grid.CellChanged += OnCellChanged;
        }

        public Texture2D Texture => _texture;

        /// <summary>Recomputes round every cell whose height changed since the last call, and uploads.</summary>
        public void Flush()
        {
            if (!_dirty)
                return;
            var region = new RectInt(_dirtyRect.xMin - _far, _dirtyRect.yMin - _far, _dirtyRect.width + 2 * _far, _dirtyRect.height + 2 * _far);
            region.SetMinMax(Vector2Int.Max(region.min, Vector2Int.zero), Vector2Int.Min(region.max, new Vector2Int(_grid.Width, _grid.Height)));
            Hollows.Compute(_heights, _grid.Width, _grid.Height, region, _smooth, _near, _far, _depth, _pixels);
            Upload();
        }

        void OnCellChanged(int x, int z)
        {
            var cell = z * _grid.Width + x;
            var height = _grid.GetSurfaceHeight(x, z);
            if (Mathf.Approximately(height, _heights[cell]))
                return;
            _heights[cell] = height;
            if (!_dirty)
            {
                _dirtyRect = new RectInt(x, z, 1, 1);
                _dirty = true;
                return;
            }

            _dirtyRect.SetMinMax(Vector2Int.Min(_dirtyRect.min, new Vector2Int(x, z)), Vector2Int.Max(_dirtyRect.max, new Vector2Int(x + 1, z + 1)));
        }

        void Upload()
        {
            _texture.SetPixelData(_pixels, 0);
            _texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _dirty = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
            if (_texture == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(_texture);
            else
                UnityEngine.Object.DestroyImmediate(_texture);
        }
    }

    /// <summary>The arithmetic of <see cref="TerrainHollowMap"/>, free of textures so it can be tested.</summary>
    public static class Hollows
    {
        /// <summary>
        /// Writes one byte a cell inside <paramref name="region"/>: 128 + 127 × clamp(d / depth), where d
        /// is how far the ground, averaged within <paramref name="smooth"/> cells, lies below the ground
        /// round it — half the way to the average within <paramref name="near"/> cells and half to the
        /// one within <paramref name="far"/>. Averages count only cells on the map, so the edge is not
        /// read as a slope.
        /// </summary>
        public static void Compute(float[] heights, int width, int height, RectInt region, int smooth, int near, int far, float depth, byte[] into)
        {
            var here = BoxMean(heights, width, height, region, smooth);
            var nearMean = BoxMean(heights, width, height, region, near);
            var farMean = BoxMean(heights, width, height, region, far);
            for (var z = 0; z < region.height; z++)
            {
                for (var x = 0; x < region.width; x++)
                {
                    var cell = (region.yMin + z) * width + region.xMin + x;
                    var local = z * region.width + x;
                    var below = 0.5f * (nearMean[local] - here[local]) + 0.5f * (farMean[local] - here[local]);
                    var value = 128f + 127f * Mathf.Clamp(below / depth, -1f, 1f);
                    into[cell] = (byte)Mathf.Clamp(Mathf.RoundToInt(value), 0, 255);
                }
            }
        }

        /// <summary>
        /// The average of the square of side 2r+1 round each cell of <paramref name="region"/>, clipped
        /// to the map: two running-sum passes, rows then columns, so the cost does not grow with r.
        /// </summary>
        static float[] BoxMean(float[] heights, int width, int height, RectInt region, int r)
        {
            var z0 = Mathf.Max(0, region.yMin - r);
            var z1 = Mathf.Min(height, region.yMax + r);
            var rows = z1 - z0;
            var sums = new float[rows * region.width];
            var counts = new int[region.width];

            // Rows: the sum over x-r..x+r for each output column, on every row the columns need.
            for (var z = z0; z < z1; z++)
            {
                var rowBase = z * width;
                var xStart = Mathf.Max(0, region.xMin - r);
                var xEnd = Mathf.Min(width - 1, region.xMin + r);
                var sum = 0.0;
                for (var x = xStart; x <= xEnd; x++)
                    sum += heights[rowBase + x];
                for (var x = 0; x < region.width; x++)
                {
                    var cx = region.xMin + x;
                    sums[(z - z0) * region.width + x] = (float)sum;
                    if (z == z0)
                        counts[x] = Mathf.Min(width - 1, cx + r) - Mathf.Max(0, cx - r) + 1;
                    var leaving = cx - r;
                    var entering = cx + r + 1;
                    if (leaving >= 0)
                        sum -= heights[rowBase + leaving];
                    if (entering < width)
                        sum += heights[rowBase + entering];
                }
            }

            // Columns: the sum of those over z-r..z+r, divided by how many cells went in.
            var mean = new float[region.width * region.height];
            for (var x = 0; x < region.width; x++)
            {
                var zStart = Mathf.Max(0, region.yMin - r);
                var zEnd = Mathf.Min(height - 1, region.yMin + r);
                var sum = 0.0;
                for (var z = zStart; z <= zEnd; z++)
                    sum += sums[(z - z0) * region.width + x];
                for (var z = 0; z < region.height; z++)
                {
                    var cz = region.yMin + z;
                    var cellsDown = Mathf.Min(height - 1, cz + r) - Mathf.Max(0, cz - r) + 1;
                    mean[z * region.width + x] = (float)(sum / (cellsDown * counts[x]));
                    var leaving = cz - r;
                    var entering = cz + r + 1;
                    if (leaving >= z0 && leaving >= 0)
                        sum -= sums[(leaving - z0) * region.width + x];
                    if (entering < z1)
                        sum += sums[(entering - z0) * region.width + x];
                }
            }

            return mean;
        }
    }
}
