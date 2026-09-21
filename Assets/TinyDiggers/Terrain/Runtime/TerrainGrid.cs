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
    /// Digging and tipping deal in disturbed material: <see cref="Remove"/> reports what came out
    /// as its disturbed form (rock comes out as loose rock), and <see cref="Add"/> places the
    /// disturbed form of whatever it is given. Only <see cref="SetColumn"/> writes undisturbed
    /// ground, which is what generation uses.
    ///
    /// With a <see cref="HeightStep"/>, every volume passed to <see cref="Add"/> and
    /// <see cref="Remove"/> is rounded to a whole number of steps, so surfaces that start on the
    /// step grid stay on it.
    ///
    /// Layers live in one flat array rather than per-cell lists: mutation allocates nothing, and
    /// a chunk rebuild walks contiguous memory.
    /// </summary>
    public sealed class TerrainGrid
    {
        /// <summary>
        /// Sixteen, not eight: an island's column carries bedrock, granite, rock, clay, dirt, sand
        /// and topsoil before anything is dug or tipped on it.
        /// </summary>
        public const int MaxLayersPerCell = 16;

        /// <summary>Thicknesses below this are treated as zero, so digging cannot leave slivers.</summary>
        const float Epsilon = 1e-5f;

        readonly Layer[] _layers;
        readonly byte[] _layerCounts;
        readonly float[] _surfaceHeights;
        readonly MaterialId[] _topMaterials;
        readonly bool[] _void;
        readonly bool[] _blocked;

        public TerrainGrid(int width, int height, MaterialTable materials, float heightStep = 0f, float datum = 0f)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (!(heightStep >= 0f))
                throw new ArgumentOutOfRangeException(nameof(heightStep));

            Width = width;
            Height = height;
            Materials = materials ?? throw new ArgumentNullException(nameof(materials));
            HeightStep = heightStep;
            Datum = datum;

            var cellCount = width * height;
            _layers = new Layer[cellCount * MaxLayersPerCell];
            _layerCounts = new byte[cellCount];
            _surfaceHeights = new float[cellCount];
            _topMaterials = new MaterialId[cellCount];
            _void = new bool[cellCount];
            _blocked = new bool[cellCount];
            // An empty column is under the datum, which is below the sea: every cell starts blocked
            // and opens up as soon as it is filled above sea level.
            for (var i = 0; i < cellCount; i++)
                _blocked[i] = true;
        }

        public int Width { get; }

        public int Height { get; }

        public MaterialTable Materials { get; }

        /// <summary>
        /// Metres. Edits move material in whole multiples of this; 0 means volumes are used as
        /// given. Generation also snaps surfaces to it.
        /// </summary>
        public float HeightStep { get; }

        /// <summary>
        /// Metres above sea level that the bottom of every column sits at. An island puts this
        /// well below sea level so a seabed can be below it and still be a stack of layers, and so
        /// the bedrock base has somewhere to be.
        /// </summary>
        public float Datum { get; }

        /// <summary>
        /// Whether the cell is under the sea: a cell counts as land only once its surface has
        /// reached one whole step above sea level, so tipping into the shallows has to actually
        /// break the surface before anything can stand there.
        /// </summary>
        public bool IsWater(int x, int z)
        {
            if (!IsGround(x, z))
                return false;
            return _surfaceHeights[z * Width + x] < LandAt;
        }

        /// <summary>The height a surface must reach to count as land rather than seabed.</summary>
        float LandAt => World.SeaLevel + LandStep;

        /// <summary>Rounds a height to the nearest millimetre, which is what keeps float drift out.</summary>
        static float Snap(float height) => (float)Math.Round(height * 1000.0) / 1000f;

        /// <summary>Metres of water over a cell, or 0 where the ground is dry.</summary>
        public float WaterDepth(int x, int z)
        {
            var surface = GetSurfaceHeight(x, z);
            return surface < World.SeaLevel ? World.SeaLevel - surface : 0f;
        }

        /// <summary>How far above sea level a cell's surface must reach to count as land.</summary>
        float LandStep => HeightStep > 0f ? HeightStep : 1f;

        /// <summary>
        /// Rounds <paramref name="volume"/> to the nearest whole number of height steps, or returns
        /// it unchanged when there is no step.
        /// </summary>
        public float Quantize(float volume)
        {
            if (HeightStep <= 0f)
                return volume;
            return (float)Math.Round(volume / HeightStep) * HeightStep;
        }

        /// <summary>Raised after a cell's stack changes, with its x and z. Renderers subscribe to this.</summary>
        public event Action<int, int> CellChanged;

        public bool InBounds(int x, int z) => (uint)x < (uint)Width && (uint)z < (uint)Height;

        /// <summary>
        /// Whether the cell is off the edge of the world: the map is a disc on a table, and the
        /// cells of the square grid outside it hold nothing. Void cells are not drawn, cannot be
        /// walked on, designated or slumped into, and hold no layers.
        /// </summary>
        public bool IsVoid(int x, int z) => _void[RequireIndex(x, z)];

        /// <summary>Void flags, indexed <c>z * Width + x</c>, for hot loops.</summary>
        public ReadOnlySpan<bool> VoidCells => _void;

        /// <summary>Whether the cell is on the map and not void: real ground, wet or dry.</summary>
        public bool IsGround(int x, int z) => InBounds(x, z) && !_void[z * Width + x];

        /// <summary>
        /// Whether anything can stand or travel here: on the map, not void, and not under the sea.
        /// This is what the pathfinder, the regions and the crew ask; <see cref="IsGround"/> is the
        /// weaker question of whether the cell is part of the world at all, which is what a fill
        /// designation or a dump zone needs, because reclaiming the shallows is the point of them.
        /// </summary>
        public bool IsPassableGround(int x, int z) => InBounds(x, z) && !_blocked[z * Width + x];

        /// <summary>
        /// Void-or-water flags, indexed <c>z * Width + x</c>, for hot loops. Water blocks travel
        /// exactly as the void does, so a flood fill can read one array instead of two.
        /// </summary>
        public ReadOnlySpan<bool> BlockedCells => _blocked;

        /// <summary>Cells on the map that are not void. Counted on demand.</summary>
        public int GroundCellCount
        {
            get
            {
                var count = 0;
                foreach (var isVoid in _void)
                    if (!isVoid)
                        count++;
                return count;
            }
        }

        /// <summary>
        /// Marks a cell as off the map, or back on it. Voiding a cell empties its column, so
        /// nothing is left hanging in the air.
        /// </summary>
        public void SetVoid(int x, int z, bool isVoid)
        {
            var cell = RequireIndex(x, z);
            if (_void[cell] == isVoid)
                return;
            _void[cell] = isVoid;
            if (isVoid)
            {
                _layerCounts[cell] = 0;
                var layerBase = cell * MaxLayersPerCell;
                for (var i = 0; i < MaxLayersPerCell; i++)
                    _layers[layerBase + i] = default;
            }

            OnCellMutated(cell, x, z);
        }

        /// <summary>Height of the top of the column in metres. Cached, so this is a single array read.</summary>
        public float GetSurfaceHeight(int x, int z) => _surfaceHeights[RequireIndex(x, z)];

        /// <summary>
        /// Every cell's surface height, indexed <c>z * Width + x</c>. For hot loops (mesh building)
        /// that read many cells and would otherwise pay a bounds check per read.
        /// </summary>
        public ReadOnlySpan<float> SurfaceHeights => _surfaceHeights;

        /// <summary>Every cell's top material, indexed <c>z * Width + x</c>; cached like the heights.</summary>
        public ReadOnlySpan<MaterialId> TopMaterials => _topMaterials;

        /// <summary>The material of the topmost layer, or <see cref="MaterialId.None"/> if the column is empty.</summary>
        public MaterialId GetTopMaterial(int x, int z) => _topMaterials[RequireIndex(x, z)];

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
        /// The material of the layer at <paramref name="height"/> in this column: what a cut face
        /// shows at that height. Below zero it is the bottom layer, at or above the surface the
        /// top one. <see cref="MaterialId.None"/> for an empty column.
        /// </summary>
        public MaterialId GetMaterialAt(int x, int z, float height)
        {
            var cell = RequireIndex(x, z);
            int count = _layerCounts[cell];
            if (count == 0)
                return MaterialId.None;

            var layerBase = cell * MaxLayersPerCell;
            var top = 0f;
            for (var i = 0; i < count - 1; i++)
            {
                top += _layers[layerBase + i].Thickness;
                if (height < top)
                    return _layers[layerBase + i].Material;
            }

            return _layers[layerBase + count - 1].Material;
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
        /// Digs <paramref name="volume"/> (rounded to the height step) off the top of the column,
        /// writing what came out into <paramref name="removed"/> as disturbed material and
        /// returning how many entries were written. Consecutive layers that come out as the same
        /// material merge into one entry, so <see cref="MaxLayersPerCell"/> entries always suffice.
        ///
        /// Stops early, having removed less than asked, when the column runs out or when the next
        /// layer down is not diggable. Bedrock is not diggable, so the world has a floor.
        ///
        /// <paramref name="volume"/> is in-place volume, how far the surface drops. The reported
        /// volumes are loose, swollen by each layer's <see cref="MaterialDefinition.BulkingFactor"/>
        /// unless <paramref name="bulk"/> is false, which the slump simulator uses so that material
        /// sliding between cells stays on the height step.
        /// </summary>
        public int Remove(int x, int z, float volume, Span<MaterialVolume> removed, bool bulk = true)
        {
            var cell = RequireIndex(x, z);
            volume = Quantize(volume);
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

                var comesOutAs = Materials.GetDisturbed(layer.Material);

                // Refuse to remove material we have nowhere to report, rather than lose it silently.
                var merges = written > 0 && removed[written - 1].Material == comesOutAs;
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
                var loose = bulk ? taken * Materials.Get(layer.Material).BulkingFactor : taken;
                if (merges)
                    removed[written - 1] = new MaterialVolume(comesOutAs, removed[written - 1].Volume + loose);
                else
                    removed[written++] = new MaterialVolume(comesOutAs, loose);
            }

            if (written > 0)
            {
                _layerCounts[cell] = (byte)count;
                OnCellMutated(cell, x, z);
            }

            return written;
        }

        /// <summary>
        /// Lists, top first, the in-place layers that <see cref="Remove(int,int,float,Span{MaterialVolume},bool)"/>
        /// would take for <paramref name="volume"/>, without changing anything: same rounding to
        /// the height step, same no-slivers rule, same stop at undiggable layers. Callers use it to
        /// check whether the loose result will fit somewhere before digging. Returns the count.
        /// </summary>
        public int PeekRemove(int x, int z, float volume, Span<Layer> taken)
        {
            var cell = RequireIndex(x, z);
            volume = Quantize(volume);
            if (volume <= Epsilon)
                return 0;

            var layerBase = cell * MaxLayersPerCell;
            int count = _layerCounts[cell];
            var remaining = volume;
            var written = 0;
            while (remaining > Epsilon && count > 0 && written < taken.Length)
            {
                var layer = _layers[layerBase + count - 1];
                if (!Materials.IsDiggable(layer.Material))
                    break;

                var thickness = Math.Min(remaining, layer.Thickness);
                if (layer.Thickness - thickness <= Epsilon)
                    thickness = layer.Thickness;
                taken[written++] = new Layer(layer.Material, thickness);
                remaining -= thickness;
                count--;
            }

            return written;
        }

        /// <summary>
        /// Convenience overload for callers that would rather not deal in spans. Clears
        /// <paramref name="removed"/> first and reuses its capacity, so it allocates nothing after
        /// the first call.
        /// </summary>
        public int Remove(int x, int z, float volume, List<MaterialVolume> removed, bool bulk = true)
        {
            if (removed == null)
                throw new ArgumentNullException(nameof(removed));

            removed.Clear();
            Span<MaterialVolume> buffer = stackalloc MaterialVolume[MaxLayersPerCell];
            var count = Remove(x, z, volume, buffer, bulk);
            for (var i = 0; i < count; i++)
                removed.Add(buffer[i]);
            return count;
        }

        /// <summary>
        /// Tips <paramref name="volume"/> (rounded to the height step) of the disturbed form of
        /// <paramref name="material"/> onto the top of the column, merging into the current top
        /// layer when that matches. Returns the volume actually added, which is zero when the
        /// stack is full and the top is a different material.
        /// </summary>
        public float Add(int x, int z, MaterialId material, float volume)
        {
            var cell = RequireIndex(x, z);
            if (material.IsNone)
                throw new ArgumentException("Cannot add MaterialId.None.", nameof(material));
            material = Materials.GetDisturbed(material); // also throws if the id is not in the table
            volume = Quantize(volume);
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
        /// Tips several pieces onto the column in one go, first piece lowest, each as its disturbed
        /// form and merging with whatever is beneath when the material matches. The pieces are not
        /// rounded one by one; only their sum has to be a whole number of height steps, so a load of
        /// small mixed pieces can still land and keep the surface on the step grid.
        ///
        /// All or nothing: returns false and changes nothing if the sum is off the step grid or the
        /// column has no room for the new layers. Raises one <see cref="CellChanged"/>.
        /// </summary>
        public bool AddStack(int x, int z, ReadOnlySpan<MaterialVolume> pieces)
        {
            var cell = RequireIndex(x, z);
            var total = 0f;
            foreach (var piece in pieces)
            {
                if (piece.Material.IsNone)
                    throw new ArgumentException("Cannot add MaterialId.None.", nameof(pieces));
                if (!(piece.Volume >= 0f))
                    throw new ArgumentOutOfRangeException(nameof(pieces), "Piece volumes must be zero or positive.");
                total += piece.Volume;
            }

            if (total <= Epsilon)
                return false;
            if (HeightStep > 0f && Math.Abs(Quantize(total) - total) > 1e-3f)
                return false;

            // Dry run: count the layer slots the pieces need after merging.
            var layerBase = cell * MaxLayersPerCell;
            int count = _layerCounts[cell];
            var top = count == 0 ? MaterialId.None : _layers[layerBase + count - 1].Material;
            var slots = count;
            foreach (var piece in pieces)
            {
                if (piece.Volume <= Epsilon)
                    continue;
                var lands = Materials.GetDisturbed(piece.Material);
                if (lands != top)
                {
                    slots++;
                    top = lands;
                }
            }

            if (slots > MaxLayersPerCell)
                return false;

            foreach (var piece in pieces)
            {
                if (piece.Volume <= Epsilon)
                    continue;
                var lands = Materials.GetDisturbed(piece.Material);
                if (count > 0 && _layers[layerBase + count - 1].Material == lands)
                {
                    var at = layerBase + count - 1;
                    _layers[at] = new Layer(lands, _layers[at].Thickness + piece.Volume);
                }
                else
                {
                    _layers[layerBase + count] = new Layer(lands, piece.Volume);
                    count++;
                }
            }

            _layerCounts[cell] = (byte)count;
            OnCellMutated(cell, x, z);
            return true;
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
            // Snapped to the millimetre. A column's height is a sum of floats over a datum tens of
            // metres below it, so the sum drifts by a few millionths and a cell built to exactly one
            // step above the sea can come out a hair under it — which made it water, and one of
            // those in the middle of a field is a hole the crew cannot cross. The millimetre is
            // fine enough that nothing real is moved: the slump's own moves are quarter-metres.
            //
            // Snapping to the height step itself would be wrong: a cell part way through a slump is
            // legitimately between steps, and rounding it would make material appear or vanish.
            var surface = Snap(Datum + height);
            _surfaceHeights[cell] = surface;
            _blocked[cell] = _void[cell] || surface < LandAt;
            _topMaterials[cell] = count == 0 ? MaterialId.None : _layers[layerBase + count - 1].Material;

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
