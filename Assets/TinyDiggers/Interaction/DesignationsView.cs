using System.Collections.Generic;
using TinyDiggers.Presentation;
using PromptWaffle.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Owns the <see cref="DesignationMap"/> for the scene and draws it: a translucent tile on
    /// each designated cell, lying on the drawn surface. Red is dig, blue fill, green a Dump
    /// Zone; a unit's Auto ramp steps are red hatched, so the player can tell them from their own
    /// and cancel them. The overlay mesh is rebuilt at most once a frame, and only when a
    /// designation or a designated cell's ground changed.
    /// </summary>
    public sealed class DesignationsView : MonoBehaviour
    {
        /// <summary>Lift above the surface so tiles do not fight the terrain for depth.</summary>
        const float Lift = 0.05f;

        [SerializeField] TerrainView _terrain;
        [SerializeField] Material _overlayMaterial;
        [SerializeField] Color32 _digColor = new Color32(230, 50, 40, 110);
        [SerializeField] Color32 _fillColor = new Color32(40, 110, 240, 110);
        [SerializeField] Color32 _dumpZoneColor = new Color32(60, 200, 80, 100);

        /// <summary>Amber, matching the Quarry tool's own colour so the mark and the tool agree.</summary>
        [SerializeField] Color32 _quarryColor = new Color32(220, 160, 70, 90);
        [SerializeField] Color32 _autoStripeColor = new Color32(230, 50, 40, 190);
        [SerializeField, Range(2, 8)] int _autoStripes = 4;

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();

        DesignationMap _map;
        Mesh _mesh;
        bool _dirty = true;

        /// <summary>Created on first use, after the terrain's grid exists.</summary>
        public DesignationMap Map
        {
            get
            {
                if (_map == null)
                {
                    _map = new DesignationMap(_terrain.Grid);
                    _map.Changed += (x, z) => _dirty = true;
                    _terrain.Grid.CellChanged += OnCellChanged;
                }

                return _map;
            }
        }

        MeshRenderer _overlayRenderer;
        bool _showOverlay = true;

        /// <summary>
        /// Whether the coloured sheet over designated and zoned ground is drawn.
        ///
        /// Turning the component off does not do it — the sheet is a child object with its own
        /// renderer — and the sheet sits exactly where the spoil is, so any look at what a tip
        /// actually built is a look at bright green instead (2026-09-22).
        /// </summary>
        public bool ShowOverlay
        {
            get => _showOverlay;
            set
            {
                _showOverlay = value;
                if (_overlayRenderer != null)
                    _overlayRenderer.enabled = value;
            }
        }

        void Start()
        {
            _mesh = new Mesh { name = "Designation Overlay" };
            _mesh.MarkDynamic();
            var overlay = new GameObject("Designation Overlay") { hideFlags = HideFlags.DontSave };
            overlay.transform.SetParent(_terrain.transform, false);
            overlay.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _overlayRenderer = overlay.AddComponent<MeshRenderer>();
            _overlayRenderer.sharedMaterial = _overlayMaterial;
            _overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _overlayRenderer.enabled = ShowOverlay;
            _ = Map;
        }

        void OnDestroy()
        {
            if (_map != null)
            {
                _terrain.Grid.CellChanged -= OnCellChanged;
                _map.Dispose();
            }
        }

        void OnCellChanged(int x, int z)
        {
            // Designated cells (and their neighbours, whose heights shape the tile's corners).
            for (var dz = -1; dz <= 1 && !_dirty; dz++)
                for (var dx = -1; dx <= 1 && !_dirty; dx++)
                    if (_terrain.Grid.InBounds(x + dx, z + dz)
                        && (_map.GetKind(x + dx, z + dz) != DesignationKind.None
                            || _map.IsDumpZone(x + dx, z + dz)
                            || _map.IsQuarry(x + dx, z + dz)))
                        _dirty = true;
        }

        void LateUpdate()
        {
            if (!_dirty || _mesh == null)
                return;
            _dirty = false;

            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            var grid = _terrain.Grid;
            // Quarries under everything else. They were drawn nowhere at all until now, which was
            // survivable while a quarry was a dragged rectangle you had just made and could
            // remember, and is not once a pit is a shape with sixteen hundred cells and benches in
            // it: you commit one and the only thing on screen is a thin outline (2026-09-23).
            foreach (var cell in _map.QuarryCells)
                AddSurfaceTile(grid, cell % grid.Width, cell / grid.Width, _quarryColor, _vertices, _colors, _triangles, Lift);
            foreach (var cell in _map.DumpZoneCells)
                AddSurfaceTile(grid, cell % grid.Width, cell / grid.Width, _dumpZoneColor, _vertices, _colors, _triangles, Lift);
            foreach (var cell in _map.ActiveCells)
            {
                var x = cell % grid.Width;
                var z = cell / grid.Width;
                if (_map.IsAuto(x, z))
                {
                    AddHatchedTile(grid, x, z, _autoStripeColor, _autoStripes, _vertices, _colors, _triangles, Lift * 2f);
                    continue;
                }

                var color = _map.GetKind(x, z) == DesignationKind.Dig ? _digColor : _fillColor;
                AddSurfaceTile(grid, x, z, color, _vertices, _colors, _triangles, Lift * 2f);
            }

            _mesh.Clear();
            _mesh.indexFormat = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        /// <summary>A quad covering cell (x, z) on the drawn surface, lifted slightly.</summary>
        public static void AddSurfaceTile(TerrainGrid grid, int x, int z, Color32 color, List<Vector3> vertices, List<Color32> colors, List<int> triangles, float lift)
        {
            AddTile(x, z,
                TerrainSurface.CornerHeight(grid, x, z) + lift,
                TerrainSurface.CornerHeight(grid, x, z + 1) + lift,
                TerrainSurface.CornerHeight(grid, x + 1, z + 1) + lift,
                TerrainSurface.CornerHeight(grid, x + 1, z) + lift,
                color, vertices, colors, triangles, grid.CellSize);
        }

        /// <summary>
        /// Stripes across cell (x, z) on the drawn surface, running along x: every other one of
        /// <paramref name="stripes"/> * 2 bands, so the cell reads as hatched rather than filled.
        /// </summary>
        public static void AddHatchedTile(TerrainGrid grid, int x, int z, Color32 color, int stripes, List<Vector3> vertices, List<Color32> colors, List<int> triangles, float lift)
        {
            var sw = TerrainSurface.CornerHeight(grid, x, z) + lift;
            var nw = TerrainSurface.CornerHeight(grid, x, z + 1) + lift;
            var ne = TerrainSurface.CornerHeight(grid, x + 1, z + 1) + lift;
            var se = TerrainSurface.CornerHeight(grid, x + 1, z) + lift;
            var bands = stripes * 2;
            for (var band = 0; band < bands; band += 2)
            {
                var v0 = (float)band / bands;
                var v1 = (float)(band + 1) / bands;
                var first = vertices.Count;
                var s = grid.CellSize;
                vertices.Add(new Vector3(x * s, Mathf.Lerp(sw, nw, v0), (z + v0) * s));
                vertices.Add(new Vector3(x * s, Mathf.Lerp(sw, nw, v1), (z + v1) * s));
                vertices.Add(new Vector3((x + 1) * s, Mathf.Lerp(se, ne, v1), (z + v1) * s));
                vertices.Add(new Vector3((x + 1) * s, Mathf.Lerp(se, ne, v0), (z + v0) * s));
                for (var k = 0; k < 4; k++)
                    colors.Add(color);
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first);
                triangles.Add(first + 2);
                triangles.Add(first + 3);
            }
        }

        /// <summary>
        /// A quad over cell (x, z) with the given corner heights (sw, nw, ne, se), on cells
        /// <paramref name="cellSize"/> metres across.
        /// </summary>
        public static void AddTile(int x, int z, float sw, float nw, float ne, float se, Color32 color, List<Vector3> vertices, List<Color32> colors, List<int> triangles, float cellSize = 1f)
        {
            var first = vertices.Count;
            vertices.Add(new Vector3(x * cellSize, sw, z * cellSize));
            vertices.Add(new Vector3(x * cellSize, nw, (z + 1) * cellSize));
            vertices.Add(new Vector3((x + 1) * cellSize, ne, (z + 1) * cellSize));
            vertices.Add(new Vector3((x + 1) * cellSize, se, z * cellSize));
            for (var k = 0; k < 4; k++)
                colors.Add(color);
            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
        }
    }
}
