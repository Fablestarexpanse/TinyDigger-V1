using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// What each cell is made of, as a texture the shader can read: one texel per cell, red the
    /// material on top and green the material a cut through it would expose. Blue is how much
    /// stone there is around the cell (a tent-weighted 5 x 5 average of the stone flags) and alpha
    /// is whether the cell itself is stone; the shader draws the rock edge along the 0.5 contour
    /// of the blue field, so it follows a smooth line instead of the cell staircase. The shader looks the
    /// material up at the fragment rather than the vertex, which is what lets material edges be
    /// blended across half a cell instead of following the triangles.
    ///
    /// Edits only touch the texels of the cells that changed: the grid's change event marks them,
    /// and one upload a frame carries whatever has been marked. Nothing is uploaded on a frame
    /// with no edits.
    /// </summary>
    public sealed class TerrainCellMap : IDisposable
    {
        readonly TerrainGrid _grid;
        readonly Texture2D _texture;
        readonly byte[] _pixels;
        readonly Layer[] _column = new Layer[TerrainGrid.MaxLayersPerCell];
        bool _dirty;
        bool _disposed;

        public TerrainCellMap(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _texture = new Texture2D(grid.Width, grid.Height, TextureFormat.RGBA32, mipChain: false, linear: true)
            {
                name = "Terrain Cell Materials",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };

            _pixels = new byte[grid.Width * grid.Height * 4];
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    WriteCell(x, z);
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    WriteStoneField(x, z);
            Upload();
            _grid.CellChanged += OnCellChanged;
        }

        public Texture2D Texture => _texture;

        /// <summary>How many times the map has been uploaded to the GPU. For perf reporting.</summary>
        public int UploadCount { get; private set; }

        /// <summary>Sends any cells changed since the last call. Cheap and does nothing when nothing changed.</summary>
        public void Flush()
        {
            if (!_dirty)
                return;
            Upload();
        }

        void OnCellChanged(int x, int z)
        {
            WriteCell(x, z);
            for (var dz = -Reach; dz <= Reach; dz++)
                for (var dx = -Reach; dx <= Reach; dx++)
                    if (_grid.InBounds(x + dx, z + dz))
                        WriteStoneField(x + dx, z + dz);
            _dirty = true;
        }

        const int Reach = 2;

        /// <summary>The blue channel: tent-weighted share of stone cells within <see cref="Reach"/>.</summary>
        void WriteStoneField(int x, int z)
        {
            var total = 0f;
            var stone = 0f;
            for (var dz = -Reach; dz <= Reach; dz++)
            {
                for (var dx = -Reach; dx <= Reach; dx++)
                {
                    var nx = Mathf.Clamp(x + dx, 0, _grid.Width - 1);
                    var nz = Mathf.Clamp(z + dz, 0, _grid.Height - 1);
                    var weight = (Reach + 1 - Mathf.Abs(dx)) * (Reach + 1 - Mathf.Abs(dz));
                    total += weight;
                    if (_pixels[(nz * _grid.Width + nx) * 4 + 3] == 255 && !_grid.IsVoid(nx, nz))
                        stone += weight;
                }
            }

            _pixels[(z * _grid.Width + x) * 4 + 2] = (byte)Mathf.RoundToInt(stone / total * 255f);
        }

        void WriteCell(int x, int z)
        {
            var at = (z * _grid.Width + x) * 4;
            if (_grid.IsVoid(x, z))
            {
                _pixels[at] = 0;
                _pixels[at + 1] = 0;
                _pixels[at + 3] = 0;
                return;
            }

            var top = _grid.GetTopMaterial(x, z);
            _pixels[at] = top.Value;
            _pixels[at + 1] = Exposed(x, z, top.Value);
            _pixels[at + 3] = MaterialTable.IsStone(top) ? (byte)255 : (byte)0;
        }

        /// <summary>
        /// The material a cut into this cell would show: the layer under the top one, if the top
        /// layer is thin enough to be cut straight through, else the top material itself.
        /// </summary>
        byte Exposed(int x, int z, byte top)
        {
            var count = _grid.GetLayerCount(x, z);
            if (count < 2)
                return top;
            var topLayer = _grid.GetLayer(x, z, count - 1);
            var step = _grid.HeightStep > 0f ? _grid.HeightStep : 1f;
            if (topLayer.Thickness > step)
                return top;
            return _grid.GetLayer(x, z, count - 2).Material.Value;
        }

        void Upload()
        {
            _texture.SetPixelData(_pixels, 0);
            _texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _dirty = false;
            UploadCount++;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
            DestroyObject(_texture);
            Array.Clear(_column, 0, _column.Length);
        }

        /// <summary>Destroys a runtime object the way the editor allows outside play mode.</summary>
        static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(target);
            else
                UnityEngine.Object.DestroyImmediate(target);
        }

    }
}
