using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The panel above the toolbar for the tool in hand (Slice 17):
    /// - Dig, Fill: the brush radius (1–8), H with − / + and a field, "H follows cursor", the
    ///   Alt-click eyedropper, and the cut / fill / net volume under the brush.
    /// - Level: H, the eyedropper, and the cut / fill of the pad being dragged.
    /// - Dump Zone: H and the cap above it, − / + and a field.
    /// - Road: H, the width, and the cut / fill of the road drawn so far.
    /// - Clear: which kinds of designation it takes off.
    /// Select has no panel. Built once; each tool shows the rows it uses, stacked.
    /// </summary>
    public sealed class ToolPanelView : MonoBehaviour
    {
        const float Width = 470f;
        const float RowHeight = 32f;
        const float Pad = 8f;

        PlayerTools _tools;
        RectTransform _panel;
        Text _title;
        ToolMode _builtFor = (ToolMode)(-1);

        RectTransform _brushRow, _heightRow, _followRow, _pickRow, _volumeRow, _capRow, _widthRow, _clearRow;
        Slider _brush;
        Text _brushValue;
        InputField _heightField;
        Toggle _follow;
        Text _pick;
        Text _volume;
        InputField _capField;
        Text _capNote;
        Text _width;
        readonly List<RectTransform> _shown = new List<RectTransform>();

        public void Build(Canvas canvas, PlayerTools tools, Vector2 position)
        {
            _tools = tools;
            _panel = UiKit.NewPanel(canvas.transform, "Tool Panel", new Vector2(0.5f, 0f), position, new Vector2(Width, 100f));
            _title = UiKit.NewLabel(_panel, "", Pad, 4f, Width - 2 * Pad, 24f, 16);

            _brushRow = Row("Brush");
            UiKit.NewLabel(_brushRow, "Brush radius", 0f, 0f, 120f, RowHeight);
            _brush = UiKit.NewSlider(_brushRow, BrushPlan.MinRadius, BrushPlan.MaxRadius, true, v => _tools.BrushRadius = Mathf.RoundToInt(v));
            UiKit.Place((RectTransform)_brush.transform, 124f, 4f, 250f, RowHeight - 8f);
            _brushValue = UiKit.NewLabel(_brushRow, "", 384f, 0f, 70f, RowHeight);

            _heightRow = Row("Height");
            UiKit.NewLabel(_heightRow, "Target height H", 0f, 0f, 120f, RowHeight);
            var down = UiKit.NewButton(_heightRow, "−", () => _tools.NudgeTargetHeight(-_tools.HeightStep), null, 18);
            UiKit.Place((RectTransform)down.transform, 124f, 2f, 34f, RowHeight - 4f);
            UiKit.AddTooltip(down, () => "Lower H one step  (PageDown)");
            _heightField = UiKit.NewNumberField(_heightRow, SubmitHeight);
            UiKit.Place((RectTransform)_heightField.transform, 162f, 2f, 90f, RowHeight - 4f);
            var up = UiKit.NewButton(_heightRow, "+", () => _tools.NudgeTargetHeight(_tools.HeightStep), null, 18);
            UiKit.Place((RectTransform)up.transform, 256f, 2f, 34f, RowHeight - 4f);
            UiKit.AddTooltip(up, () => "Raise H one step  (PageUp)");
            UiKit.NewLabel(_heightRow, "m", 296f, 0f, 30f, RowHeight);

            _followRow = Row("Follow");
            _follow = UiKit.NewToggle(_followRow, "H follows cursor", true, on => _tools.HeightFollowsCursor = on);
            UiKit.Place((RectTransform)_follow.transform, 0f, 4f, 200f, RowHeight - 8f);

            _pickRow = Row("Pick");
            var pickIcon = UiKit.Place(UiKit.NewRect(_pickRow, "Eyedropper"), 0f, 6f, 20f, 20f);
            var pickImage = pickIcon.gameObject.AddComponent<Image>();
            pickImage.sprite = ToolIcons.Get(ToolIcon.Eyedropper);
            pickImage.raycastTarget = false;
            _pick = UiKit.NewLabel(_pickRow, "", 26f, 0f, Width - 40f, RowHeight, 14);

            _volumeRow = Row("Volume");
            _volume = UiKit.NewLabel(_volumeRow, "", 0f, 0f, Width - 2 * Pad, RowHeight);

            _capRow = Row("Cap");
            UiKit.NewLabel(_capRow, "Cap above H", 0f, 0f, 120f, RowHeight);
            var capDown = UiKit.NewButton(_capRow, "−", () => _tools.ZoneCapAbove = Mathf.Max(0f, _tools.ZoneCapAbove - _tools.HeightStep), null, 18);
            UiKit.Place((RectTransform)capDown.transform, 124f, 2f, 34f, RowHeight - 4f);
            _capField = UiKit.NewNumberField(_capRow, SubmitCap);
            UiKit.Place((RectTransform)_capField.transform, 162f, 2f, 90f, RowHeight - 4f);
            var capUp = UiKit.NewButton(_capRow, "+", () => _tools.ZoneCapAbove += _tools.HeightStep, null, 18);
            UiKit.Place((RectTransform)capUp.transform, 256f, 2f, 34f, RowHeight - 4f);
            _capNote = UiKit.NewLabel(_capRow, "", 296f, 0f, 170f, RowHeight, 14);

            _widthRow = Row("Width");
            UiKit.NewLabel(_widthRow, "Road width", 0f, 0f, 120f, RowHeight);
            var x = 124f;
            foreach (var cells in new[] { 3, 5, 7 })
            {
                var captured = cells;
                var button = UiKit.NewButton(_widthRow, $"{cells} cells", () => _tools.RoadWidth = captured);
                UiKit.Place((RectTransform)button.transform, x, 2f, 76f, RowHeight - 4f);
                x += 80f;
            }

            _width = UiKit.NewLabel(_widthRow, "", x + 4f, 0f, 100f, RowHeight, 14);

            _clearRow = Row("Clear");
            var clearDig = UiKit.NewToggle(_clearRow, "Dig", _tools.ClearDig, on => _tools.ClearDig = on);
            UiKit.Place((RectTransform)clearDig.transform, 0f, 4f, 90f, RowHeight - 8f);
            var clearFill = UiKit.NewToggle(_clearRow, "Fill", _tools.ClearFill, on => _tools.ClearFill = on);
            UiKit.Place((RectTransform)clearFill.transform, 100f, 4f, 90f, RowHeight - 8f);
            var clearZones = UiKit.NewToggle(_clearRow, "Dump Zones", _tools.ClearZones, on => _tools.ClearZones = on);
            UiKit.Place((RectTransform)clearZones.transform, 200f, 4f, 140f, RowHeight - 8f);
        }

        RectTransform Row(string name)
        {
            var row = UiKit.NewRect(_panel, name);
            row.gameObject.SetActive(false);
            return row;
        }

        void Show(params RectTransform[] rows)
        {
            foreach (var row in new[] { _brushRow, _heightRow, _followRow, _pickRow, _volumeRow, _capRow, _widthRow, _clearRow })
                row.gameObject.SetActive(false);
            _shown.Clear();
            var y = 30f;
            foreach (var row in rows)
            {
                row.gameObject.SetActive(true);
                UiKit.Place(row, Pad, y, Width - 2 * Pad, RowHeight);
                y += RowHeight;
                _shown.Add(row);
            }

            _panel.sizeDelta = new Vector2(Width, y + 6f);
            _panel.gameObject.SetActive(rows.Length > 0);
        }

        void Update()
        {
            if (_tools == null || _panel == null)
                return;

            var mode = _tools.Mode;
            if (mode != _builtFor)
            {
                _builtFor = mode;
                switch (mode)
                {
                    case ToolMode.Dig:
                    case ToolMode.Fill:
                        Show(_brushRow, _heightRow, _followRow, _pickRow, _volumeRow);
                        break;
                    case ToolMode.Level:
                        Show(_heightRow, _followRow, _pickRow, _volumeRow);
                        break;
                    case ToolMode.DumpZone:
                        Show(_heightRow, _followRow, _pickRow, _capRow);
                        break;
                    case ToolMode.Road:
                        Show(_widthRow, _heightRow, _followRow, _pickRow, _volumeRow);
                        break;
                    case ToolMode.Clear:
                        Show(_clearRow);
                        break;
                    default:
                        Show();
                        break;
                }

                _title.text = mode switch
                {
                    ToolMode.Dig => "Dig  —  paint where the crew digs down to H",
                    ToolMode.Fill => "Fill  —  paint where the crew fills up to H",
                    ToolMode.Level => "Level  —  drag a pad to be levelled to H",
                    ToolMode.DumpZone => "Dump Zone  —  drag where spoil may be tipped",
                    ToolMode.Road => "Road  —  click points; double-click or Enter lays it",
                    ToolMode.Clear => "Clear  —  drag over designations to take them off",
                    _ => "",
                };
            }

            if (!_panel.gameObject.activeSelf)
                return;

            if (_brushRow.gameObject.activeSelf)
            {
                _brush.SetValueWithoutNotify(_tools.BrushRadius);
                _brushValue.text = $"{_tools.BrushRadius} cells";
            }

            if (_heightRow.gameObject.activeSelf && !_heightField.isFocused)
                _heightField.SetTextWithoutNotify(_tools.TargetHeight.ToString("0.##", CultureInfo.InvariantCulture));
            if (_followRow.gameObject.activeSelf)
                _follow.SetIsOnWithoutNotify(_tools.HeightFollowsCursor);
            if (_pickRow.gameObject.activeSelf)
                _pick.text = float.IsNaN(_tools.PickedHeight)
                    ? "Alt-click the ground to pick its height into H"
                    : $"Alt-click picks H   (last: {_tools.PickedHeight:0.##} m at {_tools.PickedCell.x}, {_tools.PickedCell.y})";

            if (_volumeRow.gameObject.activeSelf)
            {
                var cut = _tools.PlannedCut;
                var fill = _tools.PlannedFill;
                var what = mode == ToolMode.Dig || mode == ToolMode.Fill ? "Under the brush" : mode == ToolMode.Road ? "Road so far" : "This pad";
                _volume.text = cut < 0.05f && fill < 0.05f
                    ? $"{what}: nothing to move at H"
                    : $"{what}:  cut {cut:0.#} m³   fill {fill:0.#} m³   net {cut - fill:+0.#;-0.#;0} m³";
                _volume.color = mode == ToolMode.Road && _tools.RoadTooSteep ? UiKit.Warning : Color.white;
                if (mode == ToolMode.Road && _tools.RoadTooSteep)
                    _volume.text += $"   too steep ({_tools.RoadGrade:0.00} m/m)";
            }

            if (_capRow.gameObject.activeSelf)
            {
                if (!_capField.isFocused)
                    _capField.SetTextWithoutNotify(_tools.ZoneCapAbove.ToString("0.##", CultureInfo.InvariantCulture));
                _capNote.text = $"m   (cap {_tools.TargetHeight + _tools.ZoneCapAbove:0.#} m)";
            }

            if (_widthRow.gameObject.activeSelf)
                _width.text = $"now {_tools.RoadWidth}";
        }

        void SubmitHeight(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                || float.TryParse(text, out height))
                _tools.SetTargetHeight(height);
        }

        void SubmitCap(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var above)
                || float.TryParse(text, out above))
                _tools.ZoneCapAbove = Mathf.Max(0f, above);
        }
    }
}
