using System;
using System.Collections.Generic;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// A 2D grid of terrain columns. Each cell holds an ordered stack of material layers, bottom
    /// to top, and the surface height is the sum of their thicknesses. There are no overhangs or
    /// tunnels by construction.
    ///
    /// Cells are 1x1 metres, so one unit of volume equals one metre of thickness.
    ///
    /// Layers live in one flat array rather than per-cell lists: mutation allocates nothing, and
    /// a chunk rebuild walks contiguous memory.
    /// </summary>
    public sealed class TerrainGrid
    {
        public const int MaxLayersPerCell = 8;

        /// <summary>Thicknesses below this are treated as zero, so digging cannot leave slivers.</summary>
        const float Epsilon = 1e-5f;

        readonly Layer[] _layers;
        readonly byte[] _layerCounts;
        readonly float[] _surfaceHeights;

        public TerrainGrid(int width, int height, MaterialTable materials)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            Materials = materials ?? throw new ArgumentNullException(nameof(materials));

            var cellCount = width * height;
            _layers = new Layer[cellCount * MaxLayersPerCell];
            _layerCounts = new byte[cellCount];
            _surfaceHeights = new float[cellCount];
        }

        public int Width { get; }

        public int Height { get; }

        public MaterialTable Materials { get; }

        /// <summary>Raised after a cell's stack changes, with its x and z. Renderers subscribe to this.</summary>
        public event Action<int, int> CellChanged;

        public bool InBounds(int x, int z) => (uint)x < (uint)Width && (uint)z < (uint)Height;

        /// <summary>Height of the top of the column in metres. Cached, so this is a single array read.</summary>
        public float GetSurfaceHeight(int x, int z) => _surfaceHeights[RequireIndex(x, z)];

        /// <summary>The material of the topmost layer, or <see cref="MaterialId.None"/> if the column is empty.</summary>
        public MaterialId GetTopMaterial(int x, int z)
        {
            var cell = RequireIndex(x, z);
            var count = _layerCounts[cell];
            return count == 0 ? MaterialId.None : _layers[cell * MaxLayersPerCell + count - 1].Material;
        }

        public int GetLayerCount(int x, int z) => _layerCounts[RequireIndex(x, z)];

        /// <summary>Reads one layer. Index 0 is the bottom of the column.</summary>
        public Layer GetLayer(int x, int z, int index)
        {
            var cell = RequireIndex(x, z);
            if ((uint)index >= _layerCounts[cell])
                throw new ArgumentOutOfRangeException(nameof(index));
            return _layers[cell * MaxLayersPerCell + index];
        }

        /// <summary>
        /// Copies the column into <paramref name="destination"/> top first, which is the order the
        /// debug readout wants. Returns the number of layers written.
        /// </summary>
        public int CopyLayers(int x, int z, Span<Layer> destination)
        {
            var cell = RequireIndex(x, z);
            int count = _layerCounts[cell];
            if (destination.Length < count)
                throw new ArgumentException(
                    $"Destination holds {destination.Length} layers but the column has {count}.",
                    nameof(destination));

            var layerBase = cell * MaxLayersPerCell;
            for (var i = 0; i < count; i++)
                destination[i] = _layers[layerBase + count - 1 - i];
            return count;
        }

        /// <summary>
        /// Digs <paramref name="volume"/> off the top of the column, writing what came out into
        /// <paramref name="removed"/> and returning how many entries were written. Consecutive
        /// layers of the same material are merged into one entry, so <see cref="MaxLayersPerCell"/>
        /// entries always suffice.
        ///
        /// Stops early, having removed less than asked, when the column runs out or when the next
        /// layer down is not diggable. Bedrock is not diggable, so the world has a floor.
        /// </summary>
        public int Remove(int x, int z, float volume, Span<MaterialVolume> removed)
        {
            var cell = RequireIndex(x, z);
            if (volume <= Epsilon)
                return 0;

            var layerBase = cell * MaxLayersPerCell;
            int count = _layerCounts[cell];
            var remaining = volume;
            var written = 0;

            while (remaining > Epsilon && count > 0)
            {
                var top = layerBase + count - 1;
                var layer = _layers[top];
                if (!Materials.IsDiggable(layer.Material))
                    break;

                // Refuse to remove material we have nowhere to report, rather than lose it silently.
                var merges = written > 0 && removed[written - 1].Material == layer.Material;
                if (!merges && written == removed.Length)
                    break;

                var taken = Math.Min(remaining, layer.Thickness);
                if (layer.Thickness - taken <= Epsilon)
                {
                    taken = layer.Thickness;
                    _layers[top] = default;
                    count--;
                }
                else
                {
                    _layers[top] = new Layer(layer.Material, layer.Thickness - taken);
                }

                remaining -= taken;
                if (merges)
                    removed[written - 1] = new MaterialVolume(layer.Material, removed[written - 1].Volume + taken);
                else
                    removed[written++] = new MaterialVolume(layer.Material, taken);
            }

            if (written > 0)
            {
                _layerCounts[cell] = (byte)count;
                OnCellMutated(cell, x, z);
            }

            return written;
        }

        /// <summary>
        /// Convenience overload for callers that would rather not deal in spans. Clears
        /// <paramref name="removed"/> first and reuses its capacity, so it allocates nothing after
        /// the first call.
        /// </summary>
        public int Remove(int x, int z, float volume, List<MaterialVolume> removed)
        {
            if (removed == null)
                throw new ArgumentNullException(nameof(removed));

            removed.Clear();
            Span<MaterialVolume> buffer = stackalloc MaterialVolume[MaxLayersPerCell];
            var count = Remove(x, z, volume, buffer);
            for (var i = 0; i < count; i++)
                removed.Add(buffer[i]);
            return count;
        }

        /// <summary>
        /// Tips <paramref name="volume"/> of material onto the top of the column, merging into the
        /// current top layer when the material matches. Returns the volume actually added, which
        /// is zero when the stack is full and the top is a different material.
        /// </summary>
        public float Add(int x, int z, MaterialId material, float volume)
        {
            var cell = RequireIndex(x, z);
            if (material.IsNone)
                throw new ArgumentException("Cannot add MaterialId.None.", nameof(material));
            Materials.Get(material); // throws if the id is not in the table
            if (volume <= Epsilon)
                return 0f;

            var layerBase = cell * MaxLayersPerCell;
            int count = _layerCounts[cell];

            if (count > 0 && _layers[layerBase + count - 1].Material == material)
            {
                var top = layerBase + count - 1;
                _layers[top] = new Layer(material, _layers[top].Thickness + volume);
            }
            else if (count < MaxLayersPerCell)
            {
                _layers[layerBase + count] = new Layer(material, volume);
                _layerCounts[cell] = (byte)(count + 1);
            }
            else
            {
                return 0f;
            }

            OnCellMutated(cell, x, z);
            return volume;
        }

        /// <summary>
        /// Replaces a whole column at once, bottom layer first. Used by generation, where calling
        /// <see cref="Add"/> per layer would fire one change event per layer.
        /// </summary>
        public void SetColumn(int x, int z, ReadOnlySpan<Layer> layers)
        {
            var cell = RequireIndex(x, z);
            if (layers.Length > MaxLayersPerCell)
                throw new ArgumentException(
                    $"A column holds at most {MaxLayersPerCell} layers, got {layers.Length}.",
                    nameof(layers));

            // Validate the whole column before touching anything, so a bad layer halfway down
            // cannot leave the cell half written.
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.Material.IsNone)
                    throw new ArgumentException($"Layer {i} has no material.", nameof(layers));
                if (layer.Thickness <= Epsilon)
                    throw new ArgumentException($"Layer {i} has no thickness.", nameof(layers));
                Materials.Get(layer.Material);
            }

            var layerBase = cell * MaxLayersPerCell;
            for (var i = 0; i < layers.Length; i++)
                _layers[layerBase + i] = layers[i];

            for (var i = layers.Length; i < MaxLayersPerCell; i++)
                _layers[layerBase + i] = default;

            _layerCounts[cell] = (byte)layers.Length;
            OnCellMutated(cell, x, z);
        }

        void OnCellMutated(int cell, int x, int z)
        {
            // Resumming beats keeping a running total: eight adds is nothing, and the height never
            // drifts away from the layers it is meant to describe.
            var layerBase = cell * MaxLayersPerCell;
            int count = _layerCounts[cell];
            var height = 0f;
            for (var i = 0; i < count; i++)
                height += _layers[layerBase + i].Thickness;
            _surfaceHeights[cell] = height;

            CellChanged?.Invoke(x, z);
        }

        int RequireIndex(int x, int z)
        {
            if (!InBounds(x, z))
                throw new ArgumentOutOfRangeException(nameof(x), $"Cell ({x}, {z}) is outside {Width}x{Height}.");
            return z * Width + x;
        }
    }
}
