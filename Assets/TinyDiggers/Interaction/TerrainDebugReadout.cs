using System.Text;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Top-left debug panel: frame time, controls, the unit's state, job and load, designation
    /// counts, the hovered cell's stack, and what the last designation did. The text is rebuilt
    /// only when something in it changes, not every frame.
    /// </summary>
    public sealed class TerrainDebugReadout : MonoBehaviour
    {
        const float FrameTimeWindow = 0.5f;

        [SerializeField] TerrainView _terrain;
        [SerializeField] PlayerTools _tool;
        [SerializeField] CrewView _crew;
        [SerializeField] DesignationsView _designations;
        [SerializeField] RtsCamera _camera;

        readonly TerrainCellReport _report = new TerrainCellReport();
        readonly InventoryReport _loadReport = new InventoryReport();
        readonly StringBuilder _unitText = new StringBuilder(512);
        string _unitBlock = "";
        int _shownUnitVersion = -1;
        int _shownLoadVersion = -1;
        int _shownDesignationVersion = -1;
        CrewUnit _shownUnit;

        float _shownTargetHeight = float.NaN;
        bool _shownLocked;

        readonly StringBuilder _text = new StringBuilder(1024);
        string _panel = "";
        string _cell = "";
        string _frameTime = "";
        string _cameraLine = "";
        string _shownAction;
        int _shownRadius = -1;
        bool _shownHover;
        int _shownX = -1;
        int _shownZ = -1;
        bool _cellChanged = true;
        bool _panelChanged = true;

        float _frameTimeAccumulated;
        int _framesCounted;

        GUIStyle _style;
        readonly GUIContent _content = new GUIContent();
        Vector2 _contentSize;
        bool _contentChanged = true;

        void Start()
        {
            _terrain.Grid.CellChanged += OnCellChanged;
        }

        void OnDestroy()
        {
            if (_terrain != null && _terrain.Grid != null)
                _terrain.Grid.CellChanged -= OnCellChanged;
        }

        void OnCellChanged(int x, int z)
        {
            if (x == _shownX && z == _shownZ)
                _cellChanged = true;
        }

        void Update()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.f3Key.wasPressedThisFrame)
                Visible = !Visible;
            if (!Visible)
                return;

            _frameTimeAccumulated += Time.unscaledDeltaTime;
            _framesCounted++;
            if (_frameTimeAccumulated >= FrameTimeWindow)
            {
                var milliseconds = _frameTimeAccumulated / _framesCounted * 1000f;
                _frameTime = $"{milliseconds:0.0} ms/frame ({1000f / milliseconds:0} fps), {_terrain.TriangleCount:N0} triangles, " +
                    $"slump queue {_terrain.SlumpPending}";
                _cameraLine = DescribeCamera();
                _frameTimeAccumulated = 0f;
                _framesCounted = 0;
                _panelChanged = true;
            }

            if (_tool.HasHover != _shownHover || _tool.HoverX != _shownX || _tool.HoverZ != _shownZ)
            {
                _shownHover = _tool.HasHover;
                _shownX = _tool.HasHover ? _tool.HoverX : -1;
                _shownZ = _tool.HasHover ? _tool.HoverZ : -1;
                _cellChanged = true;
            }

            if (_cellChanged)
            {
                _cell = _report.Describe(_terrain.Grid, _shownX, _shownZ);
                _cellChanged = false;
                _panelChanged = true;
            }

            var units = _crew.Units;
            var shown = _crew.Selected ?? _crew.Unit;
            var map = _designations.Map;
            var crewVersion = 0;
            var loadVersion = 0;
            foreach (var member in units)
            {
                crewVersion += member.Version;
                loadVersion += member.Inventory.Version;
            }

            if (shown != null && (crewVersion != _shownUnitVersion || loadVersion != _shownLoadVersion
                || map.Version != _shownDesignationVersion || _tool.TargetHeight != _shownTargetHeight || _tool.HeightLocked != _shownLocked
                || !ReferenceEquals(shown, _shownUnit)))
            {
                _shownUnitVersion = crewVersion;
                _shownLoadVersion = loadVersion;
                _shownDesignationVersion = map.Version;
                _shownTargetHeight = _tool.TargetHeight;
                _shownLocked = _tool.HeightLocked;
                _shownUnit = shown;

                var stuck = 0;
                foreach (var member in units)
                    if (member.State == CrewUnitState.NeedsSomewhereToTip)
                        stuck++;
                _unitText.Clear();
                if (stuck > 0)
                    _unitText.Append("!! ").Append(stuck).Append(stuck == 1 ? " unit is" : " units are")
                        .AppendLine(" loaded with nowhere to tip: mark a Dump Zone (Shift+RMB) or a fill");
                _unitText.Append("Crew: ").Append(units.Count).AppendLine(" units (click one to select, Escape to clear)");
                foreach (var member in units)
                {
                    _unitText.Append(ReferenceEquals(member, _crew.Selected) ? " >" : "  ")
                        .Append(member.Id).Append(' ').Append(UnitNames.Of(member.Role)).Append(": ").Append(member.State).Append("  ")
                        .Append(member.Inventory.Total.ToString("0.0")).Append('/')
                        .Append(member.Inventory.Capacity.ToString("0")).Append(" m³  ")
                        .AppendLine(member.Status);
                }

                _unitText.AppendLine()
                    .Append(ReferenceEquals(shown, _crew.Selected) ? "Selected " : "Unit ").Append(UnitNames.Of(shown.Role))
                    .Append(' ').Append(shown.Id)
                    .Append(": ").AppendLine(shown.Status)
                    .Append("  at (").Append(shown.Cell.x).Append(", ").Append(shown.Cell.y).Append("), dig reach ±")
                    .Append(shown.DigReachLevels).Append(" levels, path ").Append(Mathf.Max(0, shown.Path.Count - shown.PathIndex))
                    .AppendLine(" cells ahead")
                    .AppendLine(_loadReport.Describe(shown.Inventory, _terrain.Grid.Materials));
                _unitText.Append("Target: ");
                if (shown.Job == CrewJobKind.None)
                    _unitText.Append("none");
                else
                    _unitText.Append(shown.Job).Append(" (").Append(shown.JobTarget.x).Append(", ").Append(shown.JobTarget.y).Append(')')
                        .Append(" from (").Append(shown.JobStand.x).Append(", ").Append(shown.JobStand.y).Append(')');
                _unitText.AppendLine(shown.OnAutoRamp ? "   ON AUTO RAMP" : "");
                if (shown.Role == UnitRole.Worker)
                    _unitText.Append("Worker: carries its own barrow")
                        .AppendLine(shown.Holding ? "   HOLDING" + (shown.OrderTarget is Vector2Int to ? $" (ordered to ({to.x}, {to.y}))" : "") : "");
                else if (shown.WorksRoads)
                    _unitText.Append(shown.Role == UnitRole.Bulldozer ? "Bulldozer: graded " : "Paver: surfaced ")
                        .Append(shown.RoadCellsDone).AppendLine(" road cells");
                else if (shown.Role == UnitRole.Digger)
                    _unitText.Append("Hauler: ").Append(shown.Partner >= 0 ? shown.Partner.ToString() : "none")
                        .Append("   waited for one ").Append(shown.WaitedForHauler.ToString("0.0")).Append(" s over ")
                        .Append(shown.HaulerWaits).AppendLine(" waits");
                else
                    _unitText.Append("Serving digger: ").Append(shown.Partner >= 0 ? shown.Partner.ToString() : "none")
                        .Append("   carried ").Append(shown.Transferred.ToString("0.0")).AppendLine(" m³ so far");

                _unitText.Append("Benching ").Append(_crew.benching ? "on" : "off")
                    .Append(", auto ramp ").Append(_crew.autoRamp ? "on" : "off")
                    .Append(": ").Append(map.AutoCount).Append(" Auto designation").Append(map.AutoCount == 1 ? "" : "s");
                if (shown.HasRamp)
                    _unitText.Append(", ramp toward (").Append(shown.RampTarget.x).Append(", ").Append(shown.RampTarget.y).Append(')');
                _unitText.AppendLine();
                if (_crew.autoRamp && shown.RampNote.Length > 0)
                    _unitText.Append("  ").AppendLine(shown.RampNote);
                if (shown.UnreachableCount > 0)
                    _unitText.Append("UNREACHABLE designations: ").Append(shown.UnreachableCount)
                        .Append(", nearest ").AppendLine(shown.NearestUnreachable);
                if (shown.DumpZoneFull)
                    _unitText.AppendLine("Dump Zone full: tipping falls back to open ground");
                _unitText.Append("Designations: ").Append(map.Count)
                    .Append("   Dump Zone cells: ").Append(map.DumpZoneCount)
                    .Append("   H ").Append(_tool.TargetHeight.ToString("0.#")).Append(" m")
                    .Append(_tool.HeightLocked ? " (locked)" : " (follows cursor)");
                _unitBlock = _unitText.ToString();
                _panelChanged = true;
            }

            if (!ReferenceEquals(_tool.LastAction, _shownAction) || _tool.BrushRadius != _shownRadius)
            {
                _shownAction = _tool.LastAction;
                _shownRadius = _tool.BrushRadius;
                _panelChanged = true;
            }

            if (_panelChanged)
            {
                _text.Clear()
                    .AppendLine(_frameTime)
                    .Append("Brush ").Append(_tool.BrushRadius)
                    .AppendLine("   tools 1-7   LMB apply   RMB cancel/clear   Esc select")
                    .AppendLine("PgUp/PgDn H -/+ 1 step (locks)   [ ] brush or road width   F3 hides this")
                    .AppendLine("WASD pan   Q/E or MMB drag-x turn   R/F or MMB drag-y tilt   scroll zoom")
                    .AppendLine("Shift+scroll FOV   Home centre   F5 save camera   F6 load camera")
                    .AppendLine(_cameraLine)
                    .AppendLine()
                    .AppendLine(_unitBlock)
                    .AppendLine()
                    .AppendLine(_cell);
                if (!string.IsNullOrEmpty(_shownAction))
                    _text.AppendLine().Append(_shownAction);
                _panel = _text.ToString();
                _panelChanged = false;
                _contentChanged = true;
            }
        }

        /// <summary>Where the camera is looking: the numbers worth having while setting a preset up.</summary>
        string DescribeCamera()
        {
            if (_camera == null)
                return "Camera: not wired to the readout";
            var rig = _camera.Rig;
            return $"Camera: pitch {_camera.Pitch:0.#}°   yaw {_camera.Yaw:0.#}°   FOV {_camera.Fov:0.#}°   " +
                $"zoom {_camera.Distance:0.#} m" +
                (rig.PitchFollowsZoom ? "   pitch follows zoom" : "") +
                (_camera.Preset == null ? "   (no preset asset)" : "");
        }

        /// <summary>Whether the panel is shown. F3 toggles it; the toolbar is the everyday UI.</summary>
        public bool Visible;

        void OnGUI()
        {
            if (!Visible)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 14,
                    padding = new RectOffset(10, 10, 8, 8),
                    wordWrap = false,
                };
                // Monospaced so the layer thicknesses line up.
                _style.font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Menlo" }, 14);
                _style.normal.textColor = Color.white;
            }

            if (_contentChanged)
            {
                _content.text = _panel;
                _contentSize = _style.CalcSize(_content);
                _contentChanged = false;
            }

            // Top right, under the menu button: top left is the units menu now.
            GUI.Box(new Rect(Screen.width - _contentSize.x - 10f, Screen.height * 0.08f, _contentSize.x, _contentSize.y), _content, _style);
        }
    }
}
