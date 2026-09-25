using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// What each cell is made of, as a texture the shader can read: one texel per cell, red the
    /// material on top and green the material a cut through it would expose. Blue is how much
    /// stone there is around the cell (a tent-weighted average of the stone flags within about two
    /// metres) and alpha
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

            // Two metres whatever the cell size, so a rock edge is as smooth on half-metre cells
            // as it was on metre ones: 2 cells at 1 m, 4 at 0.5 m.
            Reach = Mathf.Max(2, Mathf.RoundToInt(2f / grid.CellSize));
            _pixels = new byte[grid.Width * grid.Height * 4];
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    WriteCell(x, z);
            _changedMark = new bool[grid.Width * grid.Height];
            _fieldMark = new bool[grid.Width * grid.Height];
            WriteWholeStoneField();
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
            UpdateStoneField();
            Upload();
        }

        void OnCellChanged(int x, int z)
        {
            WriteCell(x, z);
            // The blue field is caught up once, at the flush: a regenerate changes every cell,
            // and redoing each one's whole neighbourhood per change cost seconds at 1024².
            var cell = z * _grid.Width + x;
            if (!_changedMark[cell])
            {
                _changedMark[cell] = true;
                _changed.Add(cell);
            }

            _dirty = true;
        }

        readonly bool[] _changedMark;
        readonly bool[] _fieldMark;
        readonly System.Collections.Generic.List<int> _changed = new System.Collections.Generic.List<int>();
        readonly System.Collections.Generic.List<int> _field = new System.Collections.Generic.List<int>();

        /// <summary>Recomputes blue around every cell changed since the last flush.</summary>
        void UpdateStoneField()
        {
            if (_changed.Count == 0)
                return;

            // Past an eighth of the map it is cheaper to redo the lot, in parallel.
            if (_changed.Count > _changedMark.Length / 8)
            {
                WriteWholeStoneField();
            }
            else
            {
                var width = _grid.Width;
                foreach (var cell in _changed)
                {
                    var x = cell % width;
                    var z = cell / width;
                    for (var dz = -Reach; dz <= Reach; dz++)
                        for (var dx = -Reach; dx <= Reach; dx++)
                        {
                            if (!_grid.InBounds(x + dx, z + dz))
                                continue;
                            var near = (z + dz) * width + x + dx;
                            if (_fieldMark[near])
                                continue;
                            _fieldMark[near] = true;
                            _field.Add(near);
                        }
                }

                foreach (var cell in _field)
                {
                    WriteStoneField(cell % width, cell / width);
                    _fieldMark[cell] = false;
                }

                _field.Clear();
            }

            foreach (var cell in _changed)
                _changedMark[cell] = false;
            _changed.Clear();
        }

        /// <summary>Blue for every texel. Each reads only alpha, so a row per task.</summary>
        void WriteWholeStoneField()
        {
            System.Threading.Tasks.Parallel.For(0, _grid.Height, z =>
            {
                for (var x = 0; x < _grid.Width; x++)
                    WriteStoneField(x, z);
            });
        }

        readonly int Reach;

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
            _pixels[at + 3] = TinyDiggersMaterials.IsStone(top) ? (byte)255 : (byte)0;
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
