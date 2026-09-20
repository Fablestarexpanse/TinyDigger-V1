using System;
using System.Collections.Generic;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Lets material slump down slopes steeper than its angle of repose.
    ///
    /// Only cells near an edit are ever examined: every <see cref="TerrainGrid.CellChanged"/>
    /// queues the changed cell and its eight neighbours, because lowering a cell can undercut the
    /// ones around it. <see cref="Tick"/> works through at most <see cref="MaxTilesPerTick"/>
    /// queued cells and leaves the rest for the next tick, so a big collapse spreads over several
    /// frames instead of stalling one. Material moved by a slump changes cells too, so a collapse
    /// keeps itself going until everything it touched is stable.
    ///
    /// Each examined cell may shed one height step of its top material per tick to each of its
    /// eight neighbours in turn, steepest first, while the drop to that neighbour exceeds the
    /// top material's effective angle. Checking all eight, not only the lowest, makes collapses
    /// spread in round patterns rather than along the grid axes.
    ///
    /// The effective angle is steepened for thin loose top layers (<see cref="ThinLayerDepth"/>), so
    /// a skin of loose material clings to a slope instead of all of it sliding to the bottom.
    ///
    /// Slides are not bulked: bulking happens once, when ground is dug.
    ///
    /// Deterministic: a FIFO queue, a fixed neighbour order and no randomness.
    /// </summary>
    public sealed class AngleOfReposeSimulator : IDisposable
    {
        /// <summary>At or above this angle a material never slumps.</summary>
        const float NeverSlumps = 89.9f;

        /// <summary>Slack for float sums of layer thicknesses that should land on the step grid.</summary>
        const float Tolerance = 1e-3f;

        static readonly int[] NeighbourDx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] NeighbourDz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        static readonly float Diagonal = (float)Math.Sqrt(2.0);

        readonly TerrainGrid _grid;
        readonly Queue<int> _queue = new Queue<int>();
        readonly bool[] _queued;
        readonly MaterialVolume[] _moved = new MaterialVolume[TerrainGrid.MaxLayersPerCell];
        readonly Layer[] _carried = new Layer[TerrainGrid.MaxLayersPerCell];
        readonly int[] _order = new int[8];
        readonly float[] _slopes = new float[8];

        bool _disposed;

        /// <summary>Most queued cells examined per <see cref="Tick"/>; the rest carry over.</summary>
        public int MaxTilesPerTick = 1000;

        /// <summary>
        /// Metres. A top layer this thin or thinner has its collapse angle raised towards vertical,
        /// in proportion to how thin it is; thicker layers use their plain angle of repose.
        /// </summary>
        public float ThinLayerDepth = 2f;

        /// <summary>How much material moves per slump when the grid has no height step.</summary>
        public float ContinuousMoveUnit = 0.25f;

        public AngleOfReposeSimulator(TerrainGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _queued = new bool[grid.Width * grid.Height];
            _grid.CellChanged += OnCellChanged;
        }

        /// <summary>Cells waiting to be examined.</summary>
        public int PendingCount => _queue.Count;

        /// <summary>Examines up to <see cref="MaxTilesPerTick"/> queued cells. Returns how many it examined.</summary>
        public int Tick()
        {
            var examined = 0;
            while (examined < MaxTilesPerTick && _queue.Count > 0)
            {
                var cell = _queue.Dequeue();
                _queued[cell] = false;
                Settle(cell % _grid.Width, cell / _grid.Width);
                examined++;
            }

            return examined;
        }

        /// <summary>Ticks until nothing is queued or <paramref name="maxTicks"/> is reached. Returns the ticks used.</summary>
        public int RunUntilStable(int maxTicks = 100000)
        {
            var ticks = 0;
            while (_queue.Count > 0 && ticks < maxTicks)
            {
                Tick();
                ticks++;
            }

            return ticks;
        }

        /// <summary>
        /// The angle at which this cell's top layer gives way: its material's angle of repose,
        /// raised towards vertical when it is loose material thinner than
        /// <see cref="ThinLayerDepth"/>. Undisturbed ground gets no bias: a thin topsoil cap does
        /// not hold up a cut wall, so digging straight down without stepping the edges makes the
        /// walls slump in (TERRAIN_REFERENCE.md section 3).
        /// </summary>
        public float EffectiveAngle(MaterialId material, float thickness)
        {
            var definition = _grid.Materials.Get(material);
            var repose = definition.AngleOfRepose;
            if (repose >= NeverSlumps || ThinLayerDepth <= 0f || !definition.IsLoose)
                return repose;

            var thinness = 1f - Math.Min(Math.Max(thickness / ThinLayerDepth, 0f), 1f);
            return repose + (90f - repose) * thinness;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
        }

        void OnCellChanged(int x, int z)
        {
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    Enqueue(x + dx, z + dz);
        }

        void Enqueue(int x, int z)
        {
            if (!_grid.InBounds(x, z))
                return;
            var cell = z * _grid.Width + x;
            if (_queued[cell])
                return;
            _queued[cell] = true;
            _queue.Enqueue(cell);
        }

        void Settle(int x, int z)
        {
            if (!_grid.IsGround(x, z))
                return;
            var unit = _grid.HeightStep > 0f ? _grid.HeightStep : ContinuousMoveUnit;

            // Rank the neighbours by how steeply they fall away, steepest first.
            var height = _grid.GetSurfaceHeight(x, z);
            var candidates = 0;
            for (var n = 0; n < 8; n++)
            {
                var nx = x + NeighbourDx[n];
                var nz = z + NeighbourDz[n];
                if (!_grid.IsGround(nx, nz))
                    continue;
                var distance = n < 4 ? 1f : Diagonal;
                var slope = (height - _grid.GetSurfaceHeight(nx, nz)) / distance;
                if (slope <= 0f)
                    continue;

                var at = candidates++;
                while (at > 0 && _slopes[at - 1] < slope)
                {
                    _slopes[at] = _slopes[at - 1];
                    _order[at] = _order[at - 1];
                    at--;
                }

                _slopes[at] = slope;
                _order[at] = n;
            }

            for (var c = 0; c < candidates; c++)
            {
                var n = _order[c];
                TryShed(x, z, x + NeighbourDx[n], z + NeighbourDz[n], n < 4 ? 1f : Diagonal, unit);
            }
        }

        /// <summary>Moves one unit of the top of (x, z) onto (nx, nz) if the drop between them is too steep.</summary>
        void TryShed(int x, int z, int nx, int nz, float distance, float unit)
        {
            var count = _grid.GetLayerCount(x, z);
            if (count == 0)
                return;
            var top = _grid.GetLayer(x, z, count - 1);
            if (!_grid.Materials.IsDiggable(top.Material))
                return;

            // One slide carries a whole unit off the top, which can span several layers. It only
            // goes if every layer in it would fail: a thin soil cap does not drag the rock under
            // it down at soil's angle. The top layer keeps its thin-skin bias; the ones below it
            // are judged on their own thickness.
            var angle = EffectiveAngle(top.Material, top.Thickness);
            var carried = _grid.PeekRemove(x, z, unit, _carried);
            for (var i = 1; i < carried && angle < NeverSlumps; i++)
            {
                var layer = _grid.GetLayer(x, z, count - 1 - i);
                angle = Math.Max(angle, EffectiveAngle(layer.Material, layer.Thickness));
            }

            if (angle >= NeverSlumps)
                return;

            // Moving one unit lowers the source and raises the target by that much, so the drop has
            // to be at least two units or the slope would just flip the other way.
            var drop = _grid.GetSurfaceHeight(x, z) - _grid.GetSurfaceHeight(nx, nz);
            if (drop < 2f * unit - Tolerance)
                return;
            if (drop / distance <= (float)Math.Tan(angle * Math.PI / 180.0))
                return;

            if (!TargetCanTake(x, z, count, nx, nz, unit))
                return;

            // Unbulked: a slide moves one step off the source and lands one step on the target, so
            // both stay on the height grid and total volume is conserved.
            var pieces = _grid.Remove(x, z, unit, _moved, bulk: false);
            for (var i = 0; i < pieces; i++)
                _grid.Add(nx, nz, _moved[i].Material, _moved[i].Volume);
        }

        /// <summary>
        /// Whether (nx, nz) has room for everything one unit off the top of (x, z) would turn into,
        /// checked before anything moves so a full neighbour can never make material vanish.
        /// </summary>
        bool TargetCanTake(int x, int z, int sourceCount, int nx, int nz, float unit)
        {
            var materials = _grid.Materials;
            var targetCount = _grid.GetLayerCount(nx, nz);
            var targetTop = targetCount == 0 ? MaterialId.None : _grid.GetLayer(nx, nz, targetCount - 1).Material;

            var remaining = unit;
            for (var i = sourceCount - 1; i >= 0 && remaining > Tolerance; i--)
            {
                var layer = _grid.GetLayer(x, z, i);
                if (!materials.IsDiggable(layer.Material))
                    break;

                // Add() places the disturbed form of what Remove() hands out, which may itself be
                // disturbed again (topsoil comes out as dirt and lands as loose dirt).
                var lands = materials.GetDisturbed(materials.GetDisturbed(layer.Material));
                if (lands != targetTop)
                {
                    if (targetCount >= TerrainGrid.MaxLayersPerCell)
                        return false;
                    targetCount++;
                    targetTop = lands;
                }

                remaining -= layer.Thickness;
            }

            return true;
        }
    }
}
