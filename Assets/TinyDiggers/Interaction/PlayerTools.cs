using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

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
    ///   cell needs (or, with <see cref="LevelRamp"/>, a ramp from H at the edge the drag started
    ///   from to <see cref="LevelHeightB"/> at the far edge), and a rubber that takes designations
    ///   and zones off the cells it covers.
    /// - **Road** is a spline tool (Slice 17 Part B): see <see cref="RoadsHost"/>.
    ///
    /// H follows the hovered cell until PageUp or PageDown moves it, which locks it; the toolbar
    /// unlocks it again.
    ///
    /// Slice 17: every designation edit goes through <see cref="History"/> (Ctrl+Z, Ctrl+Y);
    /// Alt-click with a height tool picks the ground height into H; clicks over the UI never
    /// reach the ground, and hotkeys wait while a text field has the keyboard.
    /// </summary>
    public sealed class PlayerTools : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] Camera _camera;
        [SerializeField] DesignationsView _designations;
        [SerializeField] CrewView _crew;
        [SerializeField] Material _overlayMaterial;
        [SerializeField] Color32 _previewColor = new Color32(255, 240, 160, 90);
        [SerializeField] Color32 _rectColor = new Color32(140, 220, 255, 90);

        /// <summary>Cells either side of the hovered one that Dig and Fill paint.</summary>
        [Range(BrushPlan.MinRadius, BrushPlan.MaxRadius)] public int BrushRadius = 2;

        /// <summary>Which designations the Clear tool takes off: the panel's checkboxes.</summary>
        public bool ClearDig = true;
        public bool ClearFill = true;
        public bool ClearZones = true;

        /// <summary>Cells across the road being drawn: 3, 5 or 7.</summary>
        public int RoadWidth
        {
            get => Roads != null ? Roads.Draft.Width : 3;
            set
            {
                if (Roads != null)
                    Roads.Draft.Width = Mathf.Clamp(value, 3, 7) | 1;
            }
        }

        /// <summary>Level makes a ramp: H at the edge the drag started from, <see cref="LevelHeightB"/> at the far edge.</summary>
        public bool LevelRamp;

        /// <summary>The ramp's height at the far edge, in metres.</summary>
        public float LevelHeightB;

        /// <summary>Metres above H that spoil may be heaped on a Dump Zone marked now.</summary>
        [Min(0f)] public float ZoneCapAbove = 3f;

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();
        readonly List<PlannedCell> _plan = new List<PlannedCell>();
        readonly HashSet<int> _strokeCells = new HashSet<int>();
        readonly List<Vector2Int> _brushCells = new List<Vector2Int>();
        DesignationMap _historyFor;

        Mesh _preview;
        bool _painting;
        float _strokeHeight;
        bool _dragging;
        Vector2Int _dragFrom;

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

        /// <summary>Cut and fill the shape under the cursor would cost, in m³.</summary>
        public float PlannedCut { get; private set; }

        public float PlannedFill { get; private set; }

        /// <summary>Whether the road being drawn has a segment over twice the max grade, which refuses it.</summary>
        public bool RoadTooSteep => Roads != null && Roads.State == RoadGradeState.Refused;

        /// <summary>The island's roads and the Road tool (Slice 17 Part B).</summary>
        public RoadsHost Roads { get; private set; }

        /// <summary>The camera the tools pick through.</summary>
        public Camera Camera => _camera;

        /// <summary>The cursor on the ground in cells, continuous (a cell's centre is at .5).</summary>
        public Vector2 HoverCells { get; private set; }

        /// <summary>Puts a line in the status bar.</summary>
        public void Say(string message) => LastAction = message;

        /// <summary>Whether a rectangle or a road is part-drawn.</summary>
        public bool IsDrawing => _dragging || Roads != null && Roads.IsDrawing;

        public DesignationMap Map => _designations.Map;

        /// <summary>
        /// A screen point (Input System pixels) the tools aim at instead of the mouse, for scripted
        /// captures; null for the mouse. Aims only: it clicks nothing.
        /// </summary>
        public Vector2? PointerOverride { get; set; }

        /// <summary>Undo and redo for the player's designation edits, for the map now in use.</summary>
        public DesignationHistory History { get; private set; }

        /// <summary>Cut and fill the brush would designate where it is now, in m³ (Dig and Fill).</summary>
        public float BrushCut { get; private set; }

        public float BrushFill { get; private set; }

        /// <summary>Whether a Dig or Fill stroke is being painted, and the height it paints to.</summary>
        public bool IsPainting => _painting;

        public float StrokeHeight => _strokeHeight;

        /// <summary>The height last picked with Alt-click, and where; NaN before the first pick.</summary>
        public float PickedHeight { get; private set; } = float.NaN;

        public Vector2Int PickedCell { get; private set; }

        /// <summary>Whether the tool uses the target height H.</summary>
        public bool UsesHeight => Mode == ToolMode.Dig || Mode == ToolMode.Fill || Mode == ToolMode.Level
            || Mode == ToolMode.DumpZone || Mode == ToolMode.Road;

        /// <summary>Whether H follows the hovered cell (the panel's toggle); false once H is set.</summary>
        public bool HeightFollowsCursor
        {
            get => !HeightLocked;
            set => HeightLocked = !value;
        }

        /// <summary>Sets H, which stops it following the cursor.</summary>
        public void SetTargetHeight(float height)
        {
            TargetHeight = height;
            HeightLocked = true;
        }

        /// <summary>Moves H up or down, as PageUp and PageDown do.</summary>
        public void NudgeTargetHeight(float delta)
        {
            if (_terrain.Grid != null)
                NudgeHeight(delta, _terrain.Grid);
        }

        /// <summary>The height step H moves by: the grid's, or a metre.</summary>
        public float HeightStep => _terrain.Grid != null && _terrain.Grid.HeightStep > 0f ? _terrain.Grid.HeightStep : 1f;

        /// <summary>Takes back the last designation edit.</summary>
        public void Undo()
        {
            CancelDrawing();
            var label = History?.Undo();
            LastAction = label != null ? $"Undid {label}" : "Nothing to undo";
        }

        /// <summary>Does the last undone edit again.</summary>
        public void Redo()
        {
            CancelDrawing();
            var label = History?.Redo();
            LastAction = label != null ? $"Redid {label}" : "Nothing to redo";
        }

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

            gameObject.AddComponent<BrushCursorView>().Init(this, _terrain, _overlayMaterial);
            Roads = gameObject.AddComponent<RoadsHost>();
            Roads.Init(this, _terrain, _overlayMaterial);
        }

        public void SetMode(ToolMode mode)
        {
            if (Mode == mode)
                return;
            CancelDrawing();
            Mode = mode;
            LastAction = mode == ToolMode.Select ? "Select: click or drag over units, right-click to send them" : mode + " tool";
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
            Roads?.Cancel();
            _plan.Clear();
            PlannedCut = 0f;
            PlannedFill = 0f;
        }

        void Update()
        {
            var grid = _terrain.Grid;
            if (grid == null)
                return;

            if (!ReferenceEquals(Map, _historyFor))
            {
                History?.Dispose();
                _historyFor = Map;
                History = Map != null ? new DesignationHistory(Map) : null;
            }

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (!TypingInField())
                ReadKeys(keyboard, grid);

            // Over the toolbar or a panel, the mouse belongs to the UI, not the ground; a stroke or
            // a drag already under way carries on.
            var overUi = mouse != null && !_painting && !_dragging && !_boxPressed && PointerOverUi();
            HasHover = false;
            if (PointerOverride.HasValue)
                Pick(grid, PointerOverride.Value);
            else if (mouse != null && !overUi)
                Pick(grid, mouse.position.ReadValue());
            if (!HeightLocked && !_painting && !_dragging && HasHover)
                TargetHeight = grid.GetSurfaceHeight(HoverX, HoverZ);

            if (mouse != null && !overUi && !PointerOverride.HasValue)
                HandleMouse(mouse, grid);
            UpdatePreview(grid);
        }

        static bool PointerOverUi() => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        /// <summary>Whether a text field has the keyboard, so keys are typing rather than hotkeys.</summary>
        static bool TypingInField()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.TryGetComponent<InputField>(out var field) && field.isFocused;
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

            if (Mode == ToolMode.Road && Roads != null && Roads.HandleKeys(keyboard))
                return;

            if (keyboard.pageUpKey.wasPressedThisFrame)
                NudgeHeight(step, grid);
            if (keyboard.pageDownKey.wasPressedThisFrame)
                NudgeHeight(-step, grid);

            if (keyboard.leftBracketKey.wasPressedThisFrame)
                Resize(-1);
            if (keyboard.rightBracketKey.wasPressedThisFrame)
                Resize(1);

            var ctrl = keyboard.ctrlKey.isPressed;
            if (ctrl && keyboard.zKey.wasPressedThisFrame)
            {
                if (keyboard.shiftKey.isPressed)
                    Redo();
                else
                    Undo();
            }

            if (ctrl && keyboard.yKey.wasPressedThisFrame)
                Redo();
        }

        void Resize(int by)
        {
            if (Mode == ToolMode.Road)
                RoadWidth = RoadWidth + by * 2;
            else
                BrushRadius = Mathf.Clamp(BrushRadius + by, BrushPlan.MinRadius, BrushPlan.MaxRadius);
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
                out var point) && grid.IsGround(x, z);
            if (!HasHover)
                return;
            HoverX = x;
            HoverZ = z;
            HoverCells = new Vector2(point.x / grid.CellSize, point.z / grid.CellSize);
        }

        /// <summary>Whether a selection box is being dragged, and where (Input System pixels).</summary>
        public bool BoxActive { get; private set; }

        public Rect Box => SelectionBox.FromCorners(_boxStart, _boxNow);

        bool _boxPressed;
        Vector2 _boxStart;
        Vector2 _boxNow;

        /// <summary>
        /// The RTS controls: left-click selects a unit (shift adds or drops it; empty ground
        /// clears the selection), left-drag selects every unit in the box (shift adds), and
        /// right-click on the ground sends the selection there.
        /// </summary>
        void HandleSelect(Mouse mouse, bool leftDown, bool leftHeld, bool leftUp, bool rightDown)
        {
            if (_crew == null)
                return;
            var at = mouse.position.ReadValue();
            var keyboard = Keyboard.current;
            var add = keyboard != null && keyboard.shiftKey.isPressed;

            if (leftDown)
            {
                _boxPressed = true;
                _boxStart = _boxNow = at;
            }

            if (_boxPressed && leftHeld)
            {
                _boxNow = at;
                BoxActive = SelectionBox.IsDrag(_boxStart, _boxNow);
            }

            if (_boxPressed && leftUp)
            {
                _boxNow = at;
                if (SelectionBox.IsDrag(_boxStart, _boxNow))
                {
                    var count = _crew.SelectInScreenRect(Box, _camera, add);
                    LastAction = $"Selected {count} unit{(count == 1 ? "" : "s")}";
                }
                else if (!_crew.TrySelectAt(_camera.ScreenPointToRay(at), add) && !add)
                {
                    _crew.Deselect();
                }

                _boxPressed = false;
                BoxActive = false;
            }

            if (rightDown && HasHover && _crew.SelectedCount > 0)
            {
                var sent = _crew.OrderSelectedTo(HoverX, HoverZ);
                LastAction = sent > 0
                    ? $"Sent {sent} unit{(sent == 1 ? "" : "s")} to ({HoverX}, {HoverZ})"
                    : $"No way to ({HoverX}, {HoverZ})";
            }
        }

        static Texture2D _boxTexture;

        void OnGUI()
        {
            if (!BoxActive || Event.current.type != EventType.Repaint)
                return;
            if (_boxTexture == null)
            {
                _boxTexture = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave };
                _boxTexture.SetPixel(0, 0, Color.white);
                _boxTexture.Apply();
            }

            var rect = SelectionBox.ToGui(Box, Screen.height);
            var old = GUI.color;
            GUI.color = new Color(0.45f, 1f, 0.9f, 0.15f);
            GUI.DrawTexture(rect, _boxTexture);
            GUI.color = new Color(0.45f, 1f, 0.9f, 0.9f);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, 1f), _boxTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - 1f, rect.width, 1f), _boxTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, 1f, rect.height), _boxTexture);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.yMin, 1f, rect.height), _boxTexture);
            GUI.color = old;
        }

        void HandleMouse(Mouse mouse, TerrainGrid grid)
        {
            var leftDown = mouse.leftButton.wasPressedThisFrame;
            var leftHeld = mouse.leftButton.isPressed;
            var leftUp = mouse.leftButton.wasReleasedThisFrame;
            var rightDown = mouse.rightButton.wasPressedThisFrame;

            if (Mode == ToolMode.Select)
            {
                HandleSelect(mouse, leftDown, leftHeld, leftUp, rightDown);
                return;
            }

            // The eyedropper: Alt-click samples the ground into H.
            var keyboard = Keyboard.current;
            if (leftDown && UsesHeight && !IsDrawing && keyboard != null && keyboard.altKey.isPressed)
            {
                if (HasHover)
                {
                    PickedHeight = grid.GetSurfaceHeight(HoverX, HoverZ);
                    PickedCell = new Vector2Int(HoverX, HoverZ);
                    SetTargetHeight(PickedHeight);
                    LastAction = $"Picked H = {PickedHeight:0.##} m at ({HoverX}, {HoverZ})";
                }

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
                    History?.Begin("clear");
                    var cleared = ApplyBrush(HoverX, HoverZ, cell => Map.Cancel(cell.x, cell.y));
                    History?.Commit();
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
                    Roads.HandleMouse(mouse, HasHover, HoverCells);
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
                History?.Begin(Mode == ToolMode.Dig ? "dig" : "fill");
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
            History?.Commit();
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
            History?.Begin(Mode == ToolMode.DumpZone ? "dump zone" : Mode == ToolMode.Level ? "level" : "clear");
            try
            {
                ApplyRectangle(area, grid);
            }
            finally
            {
                History?.Commit();
            }
        }

        void ApplyRectangle(RectInt area, TerrainGrid grid)
        {
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
                    PlanPad(grid, area, _dragFrom, _strokeHeight, includeSettled: false);
                    var made = Blueprints.Apply(Map, _plan);
                    Blueprints.Volumes(_plan, out var cut, out var fill, _terrain.Grid.CellArea);
                    LastAction = LevelRamp
                        ? $"Ramp {_strokeHeight:0.#} m to {LevelHeightB:0.#} m: {made} cells, {cut:0} m³ cut, {fill:0} m³ fill"
                        : $"Level to {_strokeHeight:0.#} m: {made} cells, {cut:0} m³ cut, {fill:0} m³ fill";
                    _plan.Clear();
                    break;
                }

                case ToolMode.Clear:
                {
                    var cleared = 0;
                    for (var z = area.yMin; z < area.yMax; z++)
                        for (var x = area.xMin; x < area.xMax; x++)
                            if (ClearCell(x, z))
                                cleared++;
                    LastAction = $"Cleared {cleared} cell{(cleared == 1 ? "" : "s")}";
                    break;
                }
            }
        }

        /// <summary>A level pad, or with <see cref="LevelRamp"/> a ramp from the drag's start edge up or down to <see cref="LevelHeightB"/>.</summary>
        void PlanPad(TerrainGrid grid, RectInt area, Vector2Int from, float height, bool includeSettled)
        {
            if (LevelRamp)
                Blueprints.PlanRamp(grid, area, from, height, LevelHeightB, _plan, includeSettled);
            else
                Blueprints.PlanLevel(grid, area, height, _plan, includeSettled);
        }

        /// <summary>Takes off what the Clear tool's checkboxes say: dig, fill, Dump Zone. Returns whether anything went.</summary>
        bool ClearCell(int x, int z)
        {
            var kind = Map.GetKind(x, z);
            var changed = false;
            if (kind == DesignationKind.Dig && ClearDig || kind == DesignationKind.Fill && ClearFill)
                changed = Map.CancelDesignation(x, z);
            if (ClearZones && Map.SetDumpZone(x, z, false))
                changed = true;
            return changed;
        }

        /// <summary>Runs <paramref name="apply"/> on every cell of the brush; returns how many it changed.</summary>
        int ApplyBrush(int centreX, int centreZ, System.Func<Vector2Int, bool> apply)
        {
            BrushPlan.Cells(_terrain.Grid, centreX, centreZ, BrushRadius, _brushCells);
            var applied = 0;
            foreach (var cell in _brushCells)
                if (apply(cell))
                    applied++;
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
            BrushCut = 0f;
            BrushFill = 0f;

            switch (Mode)
            {
                case ToolMode.Dig:
                case ToolMode.Fill:
                    // The ring and the ghost disc are BrushCursorView's; this is the cost.
                    if (HasHover)
                    {
                        var height = _painting ? _strokeHeight : TargetHeight;
                        BrushPlan.Cells(grid, HoverX, HoverZ, BrushRadius, _brushCells);
                        BrushPlan.Volumes(grid, _brushCells, height, Mode == ToolMode.Dig, Mode == ToolMode.Fill, out var cut, out var fill);
                        BrushCut = cut;
                        BrushFill = fill;
                        PlannedCut = cut;
                        PlannedFill = fill;
                    }

                    break;
                case ToolMode.DumpZone:
                case ToolMode.Level:
                case ToolMode.Clear:
                    PreviewRectangle(grid);
                    break;
                case ToolMode.Road:
                    if (Roads != null)
                    {
                        PlannedCut = Roads.Cut;
                        PlannedFill = Roads.Fill;
                    }

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
                PlanPad(grid, area, from, height, includeSettled: true);
                Blueprints.Volumes(_plan, out var cut, out var fill, _terrain.Grid.CellArea);
                PlannedCut = cut;
                PlannedFill = fill;
                // The pad (or ramp) as it will be, cell by cell.
                foreach (var cell in _plan)
                    DesignationsView.AddTile(cell.X, cell.Z, cell.Height, cell.Height, cell.Height, cell.Height,
                        ToolColor(Mode, 90), _vertices, _colors, _triangles, _terrain.Grid.CellSize);
                return;
            }

            for (var z = area.yMin; z < area.yMax; z++)
            {
                for (var x = area.xMin; x < area.xMax; x++)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    var color = ToolColor(Mode, 90);
                    if (Mode == ToolMode.Level || Mode == ToolMode.DumpZone)
                        DesignationsView.AddTile(x, z, height, height, height, height, color, _vertices, _colors, _triangles, _terrain.Grid.CellSize);
                    else
                        DesignationsView.AddSurfaceTile(grid, x, z, color, _vertices, _colors, _triangles, 0.08f);
                }
            }
        }

        /// <summary>The colour each tool marks the ground in: red dig, blue fill, purple level, green dump.</summary>
        public static Color32 ToolColor(ToolMode mode, byte alpha)
        {
            switch (mode)
            {
                case ToolMode.Dig: return new Color32(235, 70, 60, alpha);
                case ToolMode.Fill: return new Color32(70, 140, 245, alpha);
                case ToolMode.Level: return new Color32(170, 90, 230, alpha);
                case ToolMode.DumpZone: return new Color32(80, 205, 95, alpha);
                case ToolMode.Road: return new Color32(120, 230, 160, alpha);
                default: return new Color32(140, 220, 255, alpha);
            }
        }
    }
}
