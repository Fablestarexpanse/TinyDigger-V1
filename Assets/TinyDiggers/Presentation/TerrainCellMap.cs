using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// What each cell is made of, as a texture the shader can read: one texel per cell, red the
    /// material on top and green the material a cut through it would expose. The shader looks the
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
            _dirty = true;
        }

        void WriteCell(int x, int z)
        {
            var at = (z * _grid.Width + x) * 4;
            if (_grid.IsVoid(x, z))
            {
                _pixels[at] = 0;
                _pixels[at + 1] = 0;
                _pixels[at + 2] = 0;
                _pixels[at + 3] = 255;
                return;
            }

            var top = _grid.GetTopMaterial(x, z).Value;
            _pixels[at] = top;
            _pixels[at + 1] = Exposed(x, z, top);
            _pixels[at + 2] = 0;
            _pixels[at + 3] = 255;
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
