using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>What the left mouse button does. Hotkeys 1-7, Escape goes back to Select.</summary>
    public enum ToolMode
    {
        Select,
        Dig,
        Fill,
        DumpZone,
        Level,
        Road,
        Clear,
    }

    /// <summary>
    /// The player's hands. One tool is active at a time; the left button applies it, the right
    /// button cancels or clears, and Escape goes back to Select.
    ///
    /// - **Select** clicks a unit to follow it in the readout, or the ground to let it go.
    /// - **Dig** and **Fill** paint to the target height H over a brush of
    ///   <see cref="BrushRadius"/> cells ([ and ] resize it).
    /// - **Dump Zone**, **Level** and **Clear** are dragged out as rectangles: a zone capped at
    ///   H + <see cref="ZoneCapAbove"/>, a pad levelled to H by digging or filling whichever each
    ///   cell needs, and a rubber that takes every designation and zone off the cells it covers.
    /// - **Road** takes a run of clicked points, each at the ground height where it was clicked
    ///   (or H while H is locked), and lays a ribbon <see cref="RoadWidth"/> cells wide between
    ///   them, its height interpolated along each leg. The ghost shows the cut and fill it would
    ///   cost before anything is committed, and turns orange and refuses to confirm if any leg is
    ///   steeper than <see cref="MaxGrade"/>.
    ///
    /// H follows the hovered cell until PageUp or PageDown moves it, which locks it; the toolbar
    /// unlocks it again.
    /// </summary>
    public sealed class PlayerTools : MonoBehaviour
    {
        /// <summary>Metres of rise per metre a road may climb before the crew is asked to do too much.</summary>
        public const float MaxGrade = 0.25f;

        [SerializeField] TerrainView _terrain;
        [SerializeField] Camera _camera;
        [SerializeField] DesignationsView _designations;
        [SerializeField] CrewView _crew;
        [SerializeField] Material _overlayMaterial;
        [SerializeField] Color32 _previewColor = new Color32(255, 240, 160, 90);
        [SerializeField] Color32 _rectColor = new Color32(140, 220, 255, 90);
        [SerializeField] Color32 _roadColor = new Color32(120, 230, 160, 110);
        [SerializeField] Color32 _roadSteepColor = new Color32(255, 150, 40, 140);

        /// <summary>Cells either side of the hovered one that Dig and Fill paint.</summary>
        [Range(1, 5)] public int BrushRadius = 2;

        /// <summary>Cells across a road.</summary>
        [Range(3, 7)] public int RoadWidth = 3;

        /// <summary>Metres above H that spoil may be heaped on a Dump Zone marked now.</summary>
        [Min(0f)] public float ZoneCapAbove = 3f;

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();
        readonly List<RoadPoint> _roadPoints = new List<RoadPoint>();
        readonly List<PlannedCell> _plan = new List<PlannedCell>();
        readonly List<RoadPoint> _previewPoints = new List<RoadPoint>();
        readonly HashSet<int> _strokeCells = new HashSet<int>();

        Mesh _preview;
        bool _painting;
        float _strokeHeight;
        bool _dragging;
        Vector2Int _dragFrom;
        float _lastRoadClickTime;
        Vector2Int _lastRoadClickCell;

        public ToolMode Mode { get; private set; } = ToolMode.Select;

        public bool HasHover { get; private set; }

        public int HoverX { get; private set; }

        public int HoverZ { get; private set; }

        /// <summary>The height designations are set to.</summary>
        public float TargetHeight { get; private set; }

        /// <summary>False while H follows the hovered cell; true once PageUp/PageDown has set it.</summary>
        public bool HeightLocked { get; private set; }

        /// <summary>What the last action did, for the toolbar.</summary>
        public string LastAction { get; private set; } = "";

        /// <summary>Control points placed on the road being laid out.</summary>
        public int RoadPointCount => _roadPoints.Count;

        /// <summary>Cut and fill the shape under the cursor would cost, in m³.</summary>
        public float PlannedCut { get; private set; }

        public float PlannedFill { get; private set; }

        /// <summary>Whether the road as drawn is too steep to confirm.</summary>
        public bool RoadTooSteep { get; private set; }

        /// <summary>The steepest leg of the road being drawn, in metres per cell.</summary>
        public float RoadGrade { get; private set; }

        /// <summary>Whether a rectangle or a road is part-drawn.</summary>
        public bool IsDrawing => _dragging || _roadPoints.Count > 0;

        public DesignationMap Map => _designations.Map;

        void Start()
        {
            _preview = new Mesh { name = "Tool Preview" };
            _preview.MarkDynamic();
            var preview = new GameObject("Tool Preview") { hideFlags = HideFlags.DontSave };
            preview.transform.SetParent(_terrain.transform, false);
            preview.AddComponent<MeshFilter>().sharedMesh = _preview;
            var meshRenderer = preview.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _overlayMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        public void SetMode(ToolMode mode)
        {
            if (Mode == mode)
                return;
            CancelDrawing();
            Mode = mode;
            LastAction = mode == ToolMode.Select ? "Select: click a unit" : mode + " tool";
        }

        public void UnlockHeight()
        {
            HeightLocked = false;
        }

        /// <summary>Takes back an unfinished rectangle or road.</summary>
        public void CancelDrawing()
        {
            _dragging = false;
            _painting = false;
            _roadPoints.Clear();
            _plan.Clear();
            PlannedCut = 0f;
            PlannedFill = 0f;
            RoadTooSteep = false;
            RoadGrade = 0f;
        }

        void Update()
        {
            var grid = _terrain.Grid;
            if (grid == null)
                return;

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            ReadKeys(keyboard, grid);

            HasHover = false;
            if (mouse != null)
                Pick(grid, mouse.position.ReadValue());
            if (!HeightLocked && !_painting && !_dragging && HasHover)
                TargetHeight = grid.GetSurfaceHeight(HoverX, HoverZ);

            if (mouse != null)
                HandleMouse(mouse, grid);
            UpdatePreview(grid);
        }

        void ReadKeys(Keyboard keyboard, TerrainGrid grid)
        {
            if (keyboard == null)
                return;
            var step = grid.HeightStep > 0f ? grid.HeightStep : 1f;

            if (keyboard.digit1Key.wasPressedThisFrame)
                SetMode(ToolMode.Select);
            if (keyboard.digit2Key.wasPressedThisFrame)
                SetMode(ToolMode.Dig);
            if (keyboard.digit3Key.wasPressedThisFrame)
                SetMode(ToolMode.Fill);
            if (keyboard.digit4Key.wasPressedThisFrame)
                SetMode(ToolMode.DumpZone);
            if (keyboard.digit5Key.wasPressedThisFrame)
                SetMode(ToolMode.Level);
            if (keyboard.digit6Key.wasPressedThisFrame)
                SetMode(ToolMode.Road);
            if (keyboard.digit7Key.wasPressedThisFrame)
                SetMode(ToolMode.Clear);

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (IsDrawing)
                    CancelDrawing();
                else
                    SetMode(ToolMode.Select);
            }

            if (keyboard.pageUpKey.wasPressedThisFrame)
                NudgeHeight(step, grid);
            if (keyboard.pageDownKey.wasPressedThisFrame)
                NudgeHeight(-step, grid);

            if (keyboard.leftBracketKey.wasPressedThisFrame)
                Resize(-1);
            if (keyboard.rightBracketKey.wasPressedThisFrame)
                Resize(1);

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                FinishRoad();
        }

        void Resize(int by)
        {
            if (Mode == ToolMode.Road)
                RoadWidth = Mathf.Clamp(RoadWidth + by * 2, 3, 7);
            else
                BrushRadius = Mathf.Clamp(BrushRadius + by, 1, 5);
        }

        void NudgeHeight(float delta, TerrainGrid grid)
        {
            if (!HeightLocked && HasHover)
                TargetHeight = grid.GetSurfaceHeight(HoverX, HoverZ);
            TargetHeight += delta;
            HeightLocked = true;
        }

        void Pick(TerrainGrid grid, Vector2 screen)
        {
            var ray = _camera.ScreenPointToRay(screen);
            var terrainTransform = _terrain.transform;
            HasHover = TerrainPicker.TryPick(
                grid,
                terrainTransform.InverseTransformPoint(ray.origin),
                terrainTransform.InverseTransformDirection(ray.direction),
                out var x,
                out var z,
                out _) && grid.IsGround(x, z);
            if (!HasHover)
                return;
            HoverX = x;
            HoverZ = z;
        }

        void HandleMouse(Mouse mouse, TerrainGrid grid)
        {
            var leftDown = mouse.leftButton.wasPressedThisFrame;
            var leftHeld = mouse.leftButton.isPressed;
            var leftUp = mouse.leftButton.wasReleasedThisFrame;
            var rightDown = mouse.rightButton.wasPressedThisFrame;

            if (Mode == ToolMode.Select)
            {
                if (leftDown && _crew != null && !_crew.TrySelectAt(_camera.ScreenPointToRay(mouse.position.ReadValue())))
                    _crew.Deselect();
                return;
            }

            if (rightDown)
            {
                if (IsDrawing)
                {
                    CancelDrawing();
                    LastAction = "Cancelled";
                }
                else if (HasHover)
                {
                    var cleared = ApplyBrush(HoverX, HoverZ, cell => Map.Cancel(cell.x, cell.y));
                    LastAction = $"Cleared {cleared} cell{(cleared == 1 ? "" : "s")}";
                }

                return;
            }

            switch (Mode)
            {
                case ToolMode.Dig:
                case ToolMode.Fill:
                    PaintStroke(leftDown, leftHeld, leftUp);
                    break;
                case ToolMode.DumpZone:
                case ToolMode.Level:
                case ToolMode.Clear:
                    DragRectangle(leftDown, leftUp, grid);
                    break;
                case ToolMode.Road:
                    if (leftDown)
                        AddRoadPoint(grid);
                    break;
            }
        }

        void PaintStroke(bool down, bool held, bool up)
        {
            if (down)
            {
                _painting = true;
                _strokeHeight = TargetHeight;
                _strokeCells.Clear();
            }

            if (_painting && held && HasHover)
            {
                var kind = Mode == ToolMode.Dig ? DesignationKind.Dig : DesignationKind.Fill;
                var width = _terrain.Grid.Width;
                ApplyBrush(HoverX, HoverZ, cell =>
                {
                    if (!Map.Designate(cell.x, cell.y, kind, _strokeHeight))
                        return false;
                    _strokeCells.Add(cell.y * width + cell.x);
                    return true;
                });
            }

            if (!_painting || !up)
                return;
            _painting = false;
            var verb = Mode == ToolMode.Dig ? "dig" : "fill";
            var count = _strokeCells.Count;
            LastAction = count > 0
                ? $"Designated {count} cell{(count == 1 ? "" : "s")}: {verb} to {_strokeHeight:0.#} m"
                : $"Nothing to {verb} at {_strokeHeight:0.#} m";
        }

        void DragRectangle(bool down, bool up, TerrainGrid grid)
        {
            if (down && HasHover)
            {
                _dragging = true;
                _dragFrom = new Vector2Int(HoverX, HoverZ);
                _strokeHeight = TargetHeight;
            }

            if (!_dragging || !up)
                return;
            _dragging = false;
            if (!HasHover)
                return;

            var area = Rectangle(_dragFrom, new Vector2Int(HoverX, HoverZ));
            switch (Mode)
            {
                case ToolMode.DumpZone:
                {
                    var cap = _strokeHeight + ZoneCapAbove;
                    var marked = 0;
                    for (var z = area.yMin; z < area.yMax; z++)
                        for (var x = area.xMin; x < area.xMax; x++)
                            if (Map.SetDumpZone(x, z, true, cap))
                                marked++;
                    LastAction = $"Dump Zone: {marked} cells, capped at {cap:0.#} m";
                    break;
                }

                case ToolMode.Level:
                {
                    Blueprints.PlanLevel(grid, area, _strokeHeight, _plan);
                    var made = Blueprints.Apply(Map, _plan);
                    Blueprints.Volumes(_plan, out var cut, out var fill, _terrain.Grid.CellArea);
                    LastAction = $"Level to {_strokeHeight:0.#} m: {made} cells, {cut:0} m³ cut, {fill:0} m³ fill";
                    _plan.Clear();
                    break;
                }

                case ToolMode.Clear:
                {
                    var cleared = 0;
                    for (var z = area.yMin; z < area.yMax; z++)
                        for (var x = area.xMin; x < area.xMax; x++)
                            if (Map.Cancel(x, z))
                                cleared++;
                    LastAction = $"Cleared {cleared} cell{(cleared == 1 ? "" : "s")}";
                    break;
                }
            }
        }

        /// <summary>Places a road control point at a cell, as clicking it would. For scripted runs.</summary>
        public void PlaceRoadPoint(int x, int z, float? height = null)
        {
            var grid = _terrain.Grid;
            if (grid == null || !grid.IsGround(x, z))
                return;
            _roadPoints.Add(new RoadPoint(new Vector2Int(x, z), height ?? grid.GetSurfaceHeight(x, z)));
            RefreshRoadPlan(grid, null);
        }

        /// <summary>Lays the road that has been drawn, if it is not too steep. Returns whether it went down.</summary>
        public bool ConfirmRoad()
        {
            if (Mode != ToolMode.Road || _roadPoints.Count < 2)
                return false;
            RefreshRoadPlan(_terrain.Grid, null);
            if (RoadTooSteep)
                return false;
            FinishRoad();
            return true;
        }

        void AddRoadPoint(TerrainGrid grid)
        {
            if (!HasHover)
                return;
            var cell = new Vector2Int(HoverX, HoverZ);
            var height = HeightLocked ? TargetHeight : grid.GetSurfaceHeight(HoverX, HoverZ);

            // A second click on the same cell, or soon after the last one, finishes the road.
            if (_roadPoints.Count > 0 && cell == _lastRoadClickCell && Time.unscaledTime - _lastRoadClickTime < 0.4f)
            {
                FinishRoad();
                return;
            }

            _lastRoadClickCell = cell;
            _lastRoadClickTime = Time.unscaledTime;
            _roadPoints.Add(new RoadPoint(cell, height));
            LastAction = $"Road: {_roadPoints.Count} point{(_roadPoints.Count == 1 ? "" : "s")}, double-click or Enter to lay it";
        }

        void FinishRoad()
        {
            if (Mode != ToolMode.Road || _roadPoints.Count < 2)
                return;
            if (RoadTooSteep)
            {
                LastAction = $"Too steep: {RoadGrade:0.00} m per m, limit {MaxGrade:0.00}";
                return;
            }

            var made = Blueprints.Apply(Map, _plan);
            Blueprints.Volumes(_plan, out var cut, out var fill, _terrain.Grid.CellArea);
            LastAction = $"Road laid: {made} cells, {cut:0} m³ cut, {fill:0} m³ fill";
            CancelDrawing();
        }

        /// <summary>Runs <paramref name="apply"/> on every cell of the brush; returns how many it changed.</summary>
        int ApplyBrush(int centreX, int centreZ, System.Func<Vector2Int, bool> apply)
        {
            var grid = _terrain.Grid;
            var applied = 0;
            for (var dz = -BrushRadius; dz <= BrushRadius; dz++)
            {
                for (var dx = -BrushRadius; dx <= BrushRadius; dx++)
                {
                    if (dx * dx + dz * dz > BrushRadius * BrushRadius || !grid.IsGround(centreX + dx, centreZ + dz))
                        continue;
                    if (apply(new Vector2Int(centreX + dx, centreZ + dz)))
                        applied++;
                }
            }

            return applied;
        }

        static RectInt Rectangle(Vector2Int a, Vector2Int b)
        {
            var minX = Mathf.Min(a.x, b.x);
            var minZ = Mathf.Min(a.y, b.y);
            return new RectInt(minX, minZ, Mathf.Abs(a.x - b.x) + 1, Mathf.Abs(a.y - b.y) + 1);
        }

        // --- the ghost ------------------------------------------------------------------------

        void UpdatePreview(TerrainGrid grid)
        {
            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            PlannedCut = 0f;
            PlannedFill = 0f;

            switch (Mode)
            {
                case ToolMode.Dig:
                case ToolMode.Fill:
                    if (HasHover)
                    {
                        var height = _painting ? _strokeHeight : TargetHeight;
                        ApplyBrush(HoverX, HoverZ, cell =>
                        {
                            DesignationsView.AddTile(cell.x, cell.y, height, height, height, height, _previewColor, _vertices, _colors, _triangles, _terrain.Grid.CellSize);
                            return true;
                        });
                    }

                    break;
                case ToolMode.DumpZone:
                case ToolMode.Level:
                case ToolMode.Clear:
                    PreviewRectangle(grid);
                    break;
                case ToolMode.Road:
                    PreviewRoad(grid);
                    break;
            }

            _preview.Clear();
            _preview.SetVertices(_vertices);
            _preview.SetColors(_colors);
            _preview.SetTriangles(_triangles, 0, true);
        }

        void PreviewRectangle(TerrainGrid grid)
        {
            if (!HasHover)
                return;
            var from = _dragging ? _dragFrom : new Vector2Int(HoverX, HoverZ);
            var area = Rectangle(from, new Vector2Int(HoverX, HoverZ));
            var height = _dragging ? _strokeHeight : TargetHeight;
            if (Mode == ToolMode.Level)
            {
                Blueprints.PlanLevel(grid, area, height, _plan, includeSettled: true);
                Blueprints.Volumes(_plan, out var cut, out var fill, _terrain.Grid.CellArea);
                PlannedCut = cut;
                PlannedFill = fill;
            }

            for (var z = area.yMin; z < area.yMax; z++)
            {
                for (var x = area.xMin; x < area.xMax; x++)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    if (Mode == ToolMode.Level || Mode == ToolMode.DumpZone)
                        DesignationsView.AddTile(x, z, height, height, height, height, _rectColor, _vertices, _colors, _triangles, _terrain.Grid.CellSize);
                    else
                        DesignationsView.AddSurfaceTile(grid, x, z, _rectColor, _vertices, _colors, _triangles, 0.08f);
                }
            }
        }

        /// <summary>Works out the road as it would be with <paramref name="next"/> as its next point.</summary>
        void RefreshRoadPlan(TerrainGrid grid, RoadPoint? next)
        {
            _previewPoints.Clear();
            _previewPoints.AddRange(_roadPoints);
            if (next.HasValue)
                _previewPoints.Add(next.Value);

            RoadGrade = Blueprints.SteepestGrade(_previewPoints, out _, grid.CellSize);
            RoadTooSteep = RoadGrade > MaxGrade + 1e-4f;
            Blueprints.PlanRoad(grid, _previewPoints, RoadWidth, _plan, includeSettled: true);
            Blueprints.Volumes(_plan, out var cut, out var fill, _terrain.Grid.CellArea);
            PlannedCut = cut;
            PlannedFill = fill;
        }

        void PreviewRoad(TerrainGrid grid)
        {
            if (_roadPoints.Count == 0)
            {
                if (HasHover)
                    DesignationsView.AddSurfaceTile(grid, HoverX, HoverZ, _roadColor, _vertices, _colors, _triangles, 0.08f);
                return;
            }

            // The road as it would be if the cursor were the next point.
            RoadPoint? next = null;
            if (HasHover)
            {
                var height = HeightLocked ? TargetHeight : grid.GetSurfaceHeight(HoverX, HoverZ);
                next = new RoadPoint(new Vector2Int(HoverX, HoverZ), height);
            }

            RefreshRoadPlan(grid, next);
            var color = RoadTooSteep ? _roadSteepColor : _roadColor;
            foreach (var cell in _plan)
            {
                // Lie on the road bed, or on the ground where the road is cut into it, so the
                // ribbon reads as one line rather than disappearing into the hill it cuts.
                var height = Mathf.Max(cell.Height, grid.GetSurfaceHeight(cell.X, cell.Z)) + 0.06f;
                DesignationsView.AddTile(cell.X, cell.Z, height, height, height, height, color, _vertices, _colors, _triangles, _terrain.Grid.CellSize);
            }
        }
    }
}
