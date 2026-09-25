using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The drawn terrain surface as a texture the GPU can read: one texel per grid corner, each the
    /// average of the cells touching it (<see cref="TerrainSurface.CornerHeight"/>), filtered
    /// bilinear — which is exactly how the smoothed renderer draws the ground and how units ride it.
    /// Grass is placed on the GPU (2026-09-24) and needs to stand on that surface, not on the stepped
    /// column tops. Kept in sync like the cell map: the corners round a changed cell are rewritten and
    /// the texture re-sent once a frame.
    /// </summary>
    public sealed class TerrainHeightTexture : IDisposable
    {
        readonly TerrainGrid _grid;
        readonly Texture2D _texture;
        readonly float[] _corners;
        readonly int _stride;
        bool _dirty;
        bool _disposed;

        public TerrainHeightTexture(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _stride = grid.Width + 1;
            _texture = new Texture2D(_stride, grid.Height + 1, TextureFormat.RFloat, mipChain: false, linear: true)
            {
                name = "Terrain Corner Heights",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            _corners = new float[_stride * (grid.Height + 1)];
            for (var z = 0; z <= grid.Height; z++)
                for (var x = 0; x <= grid.Width; x++)
                    _corners[z * _stride + x] = TerrainSurface.CornerHeight(grid, x, z);
            Upload();
            _grid.CellChanged += OnCellChanged;
        }

        public Texture2D Texture => _texture;

        /// <summary>The height stored for corner (x, z), for tests.</summary>
        public float Corner(int x, int z) => _corners[z * _stride + x];

        public void Flush()
        {
            if (_dirty)
                Upload();
        }

        void OnCellChanged(int x, int z)
        {
            // A cell touches its own four corners.
            for (var dz = 0; dz <= 1; dz++)
                for (var dx = 0; dx <= 1; dx++)
                    _corners[(z + dz) * _stride + x + dx] = TerrainSurface.CornerHeight(_grid, x + dx, z + dz);
            _dirty = true;
        }

        void Upload()
        {
            _texture.SetPixelData(_corners, 0);
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
}
