using PromptWaffle.Terrain;
using System;

namespace PromptWaffle.Terrain.Generation
{
    /// <summary>
    /// What ore lies under each cell, as the ore view shows it: the nearest ore to the surface
    /// within <see cref="Depth"/> metres, how far down its top is, and how thick it is there.
    /// Plain C#, so it can be tested; the view only draws what this finds.
    ///
    /// It follows the land: a changed cell is resurveyed, so digging an ore out clears its mark.
    /// </summary>
    public sealed class OreSurvey : IDisposable
    {
        public struct Find
        {
            public MaterialId Ore;
            /// <summary>Metres from the surface down to the top of the ore.</summary>
            public float DepthToTop;
            public float Thickness;

            public bool IsNone => Ore.IsNone;
        }

        readonly TerrainGrid _grid;
        readonly Find[] _finds;

        /// <summary>Metres below the surface the survey looks.</summary>
        public readonly float Depth;

        /// <summary>Raised with the cell index whenever a cell's find changes.</summary>
        public event Action<int> Changed;

        public OreSurvey(TerrainGrid grid, float depth = 20f)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Depth = depth;
            _finds = new Find[grid.Width * grid.Height];
            for (var z = 0; z < grid.Height; z++)
                for (var x = 0; x < grid.Width; x++)
                    _finds[z * grid.Width + x] = Survey(x, z);
            _grid.CellChanged += OnCellChanged;
        }

        public Find At(int x, int z) => _finds[z * _grid.Width + x];

        /// <summary>Cells with any ore in reach, for counting and tests.</summary>
        public int CellsWithOre
        {
            get
            {
                var count = 0;
                foreach (var find in _finds)
                    if (!find.IsNone)
                        count++;
                return count;
            }
        }

        void OnCellChanged(int x, int z)
        {
            var cell = z * _grid.Width + x;
            var find = Survey(x, z);
            if (find.Ore == _finds[cell].Ore && Math.Abs(find.DepthToTop - _finds[cell].DepthToTop) < 1e-3f
                && Math.Abs(find.Thickness - _finds[cell].Thickness) < 1e-3f)
                return;
            _finds[cell] = find;
            Changed?.Invoke(cell);
        }

        /// <summary>Walks the column top down and returns the first ore within reach.</summary>
        Find Survey(int x, int z)
        {
            if (_grid.IsVoid(x, z))
                return default;
            var count = _grid.GetLayerCount(x, z);
            var depth = 0f;
            for (var i = count - 1; i >= 0; i--)
            {
                if (depth > Depth)
                    break;
                var layer = _grid.GetLayer(x, z, i);
                if (_grid.Materials.IsOre(layer.Material))
                    return new Find { Ore = layer.Material, DepthToTop = depth, Thickness = layer.Thickness };
                depth += layer.Thickness;
            }

            return default;
        }

        public void Dispose() => _grid.CellChanged -= OnCellChanged;
    }
}
