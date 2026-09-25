using PromptWaffle.DynamicWater;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The TinyDiggers column grid as ground for PromptWaffle Dynamic Water. Each zone cell reads
    /// the surface height of the grid cell under its centre; void cells (off the disc) are walls.
    /// Every edit to the grid (a dig, a tip, a slump) is gathered into one world rectangle a frame
    /// and passed on, so a trench dug to the sea floods.
    /// </summary>
    public sealed class TerrainGridWaterGround : WaterGroundProvider
    {
        [SerializeField] TerrainView _terrain;

        TerrainGrid _grid;
        bool _dirty;
        int _minX, _minZ, _maxX, _maxZ;

        public override bool IsReady => _terrain != null && _terrain.Grid != null;

        public TerrainView Terrain => _terrain;

        void Update()
        {
            if (_grid == null && IsReady)
            {
                _grid = _terrain.Grid;
                _grid.CellChanged += OnCellChanged;
            }

            if (!_dirty)
                return;
            _dirty = false;
            var cell = _grid.CellSize;
            var min = _terrain.transform.TransformPoint(new Vector3(_minX * cell, 0f, _minZ * cell));
            var max = _terrain.transform.TransformPoint(new Vector3((_maxX + 1) * cell, 0f, (_maxZ + 1) * cell));
            RaiseChanged(Rect.MinMaxRect(min.x, min.z, max.x, max.z));
        }

        void OnDestroy()
        {
            if (_grid != null)
                _grid.CellChanged -= OnCellChanged;
        }

        void OnCellChanged(int x, int z)
        {
            if (!_dirty)
            {
                _dirty = true;
                _minX = _maxX = x;
                _minZ = _maxZ = z;
                return;
            }

            _minX = Mathf.Min(_minX, x);
            _maxX = Mathf.Max(_maxX, x);
            _minZ = Mathf.Min(_minZ, z);
            _maxZ = Mathf.Max(_maxZ, z);
        }

        public override void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into)
        {
            var grid = _terrain.Grid;
            var terrain = _terrain.transform;
            var lift = terrain.position.y;
            var i = 0;
            for (var z = region.yMin; z < region.yMax; z++)
            {
                for (var x = region.xMin; x < region.xMax; x++)
                {
                    var centre = desc.CellCentre(x, z);
                    var local = terrain.InverseTransformPoint(new Vector3(centre.x, 0f, centre.y));
                    var at = TerrainSpace.CellAt(grid, local);
                    into[i++] = grid.InBounds(at.x, at.y) && !grid.IsVoid(at.x, at.y)
                        ? grid.GetSurfaceHeight(at.x, at.y) + lift
                        : WaterGround.Wall;
                }
            }
        }
    }
}
