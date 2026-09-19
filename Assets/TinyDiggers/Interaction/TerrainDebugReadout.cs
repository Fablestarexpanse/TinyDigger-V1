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
        [SerializeField] DesignationTool _tool;
        [SerializeField] CrewUnitView _unit;
        [SerializeField] DesignationsView _designations;

        readonly TerrainCellReport _report = new TerrainCellReport();
        readonly InventoryReport _loadReport = new InventoryReport();
        readonly StringBuilder _unitText = new StringBuilder(512);
        string _unitBlock = "";
        int _shownUnitVersion = -1;
        int _shownLoadVersion = -1;
        int _shownDesignationVersion = -1;
        float _shownTargetHeight = float.NaN;
        bool _shownLocked;

        readonly StringBuilder _text = new StringBuilder(1024);
        string _panel = "";
        string _cell = "";
        string _frameTime = "";
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
            _frameTimeAccumulated += Time.unscaledDeltaTime;
            _framesCounted++;
            if (_frameTimeAccumulated >= FrameTimeWindow)
            {
                var milliseconds = _frameTimeAccumulated / _framesCounted * 1000f;
                _frameTime = $"{milliseconds:0.0} ms/frame ({1000f / milliseconds:0} fps), {_terrain.TriangleCount:N0} triangles, " +
                    $"slump queue {_terrain.SlumpPending}";
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

            var unit = _unit.Unit;
            var map = _designations.Map;
            if (unit != null && (unit.Version != _shownUnitVersion || unit.Inventory.Version != _shownLoadVersion
                || map.Version != _shownDesignationVersion || _tool.TargetHeight != _shownTargetHeight || _tool.HeightLocked != _shownLocked))
            {
                _shownUnitVersion = unit.Version;
                _shownLoadVersion = unit.Inventory.Version;
                _shownDesignationVersion = map.Version;
                _shownTargetHeight = _tool.TargetHeight;
                _shownLocked = _tool.HeightLocked;

                _unitText.Clear()
                    .Append("Unit: ").AppendLine(unit.Status)
                    .Append("  at (").Append(unit.Cell.x).Append(", ").Append(unit.Cell.y).Append("), dig reach ±")
                    .Append(unit.DigReachLevels).AppendLine(" levels")
                    .AppendLine(_loadReport.Describe(unit.Inventory, _terrain.Grid.Materials));
                if (unit.UnreachableCount > 0)
                    _unitText.Append("UNREACHABLE designations: ").Append(unit.UnreachableCount)
                        .Append(", nearest ").AppendLine(unit.NearestUnreachable);
                _unitText.Append("Designations: ").Append(map.Count)
                    .Append("   H ").Append(_tool.TargetHeight.ToString("0.#")).Append(" m")
                    .Append(_tool.HeightLocked ? " (locked)" : " (follows cursor)");
                _unitBlock = _unitText.ToString();
                _panelChanged = true;
            }

            if (!ReferenceEquals(_tool.LastAction, _shownAction) || _terrain.BrushRadius != _shownRadius)
            {
                _shownAction = _tool.LastAction;
                _shownRadius = _terrain.BrushRadius;
                _panelChanged = true;
            }

            if (_panelChanged)
            {
                _text.Clear()
                    .AppendLine(_frameTime)
                    .Append("Brush radius ").Append(_terrain.BrushRadius)
                    .AppendLine("   LMB dig to H   RMB fill to H   MMB click clear")
                    .AppendLine("Q/E H -/+ 1 step (locks)   R H follows cursor")
                    .AppendLine("WASD pan   MMB drag rotate   scroll zoom")
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

        void OnGUI()
        {
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

            GUI.Box(new Rect(10f, 10f, _contentSize.x, _contentSize.y), _content, _style);
        }
    }
}
