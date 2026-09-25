using System.Collections.Generic;
using System.Globalization;
using PromptWaffle.Terrain;
using TinyDiggers.Units;
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
    /// - Quarry: H (the floor), the eyedropper, and what the marked quarries still hold.
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
        bool _builtStamp;

        // The stamp picker: one button a stamp in the library, made the first time it is shown.
        RectTransform _stampRow, _stampRow2, _stampRow3, _godRow, _stampShapeRow, _stampHeightRow;
        Toggle _god, _stampFlip;
        InputField _stampSize, _stampTurn, _stampHeight;
        readonly List<(HeightStamp Stamp, Image Background, Text Label)> _stampButtons = new List<(HeightStamp, Image, Text)>();

        RectTransform _brushRow, _heightRow, _followRow, _pickRow, _volumeRow, _capRow, _widthRow, _clearRow;
        RectTransform _roadBendRow;
        RectTransform _rampRow, _roadGradeRow, _roadOptionsRow, _roadShapeRow, _roadNodeRow, _roadHintRow, _quarryRow;
        Text _quarryNote;
        Toggle _ramp;
        InputField _rampField;
        Text _grades;
        InputField _maxGradeField, _minBendField, _nodeHeightField, _overGroundField;
        Toggle _holdGrade, _cutThrough;
        Text _bends;
        Toggle _snap45;
        Toggle _asBuilt;
        Toggle _lockNode;
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
        float _stockAt = -1f;
        float _quarryStock;

        public void Build(Canvas canvas, PlayerTools tools, Vector2 position)
        {
            _tools = tools;
            _panel = UiKit.NewPanel(canvas.transform, "Tool Panel", new Vector2(0.5f, 0f), position, new Vector2(Width, 100f));
            _title = UiKit.NewLabel(_panel, "", Pad, 4f, Width - 2 * Pad, 24f, 16);

            _stampRow = Row("Stamps");
            _stampRow2 = Row("Stamps, more");
            _stampRow3 = Row("Stamps, last");
            // Where it sits and how: size and turn on one row, height, flip and reset on the next.
            // Each is a box to type in with a step either side, and the keys and wheel still work.
            _stampShapeRow = Row("Stamp size and turn");
            _stampSize = Stepper(_stampShapeRow, "Size", 0f, "m", () => Stamp().ScaleStamp(1f / 1.1f), () => Stamp().ScaleStamp(1.1f),
                v => Stamp().SetStampSize(v), "Metres across   (Ctrl+wheel, or [ and ])");
            _stampTurn = Stepper(_stampShapeRow, "Turn", 226f, "°", () => Stamp().TurnStamp(-15f), () => Stamp().TurnStamp(15f),
                v => Stamp().SetStampTurn(v), "Degrees clockwise   (Alt+wheel, or comma and full stop)");

            _stampHeightRow = Row("Stamp height");
            _stampHeight = Stepper(_stampHeightRow, "Height", 0f, "m", () => Stamp().RaiseStamp(-0.5f), () => Stamp().RaiseStamp(0.5f),
                v => Stamp().SetStampHeight(v), "Metres high at its highest, or deep upside down   (PageUp / PageDown)");
            _stampFlip = UiKit.NewToggle(_stampHeightRow, "Upside down", false, on =>
            {
                if (Stamp().Form.Placement.Invert != on)
                    Stamp().FlipStamp();
            });
            UiKit.Place((RectTransform)_stampFlip.transform, 240f, 4f, 130f, RowHeight - 8f);
            UiKit.AddTooltip(_stampFlip, () => "A mound becomes a hollow the same shape   (I)");
            var reset = UiKit.NewButton(_stampHeightRow, "Reset", () => Stamp().ResetStamp(), null, 13);
            UiKit.Place((RectTransform)reset.transform, 380f, 2f, Width - 2 * Pad - 380f, RowHeight - 4f);
            UiKit.AddTooltip(reset, () => "Back to the stamp as it comes: its own size and height, unturned, right way up");

            _godRow = Row("God mode");
            _god = UiKit.NewToggle(_godRow, "God mode: shape the ground now, no crew  (G)", false,
                on => _tools.Terraform.GodMode = on);
            UiKit.Place((RectTransform)_god.transform, 0f, 4f, Width - 2 * Pad, RowHeight - 8f);
            UiKit.AddTooltip(_god, () => "On, a stamp you place is the ground at once — for laying out the land. Off, it is orders the crew build. Ctrl+Z takes back a god stamp");

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
            UiKit.NewLabel(_widthRow, "Road width", 0f, 0f, 90f, RowHeight);
            var x = 94f;
            foreach (var cells in new[] { 3, 5, 7 })
            {
                var captured = cells;
                var button = UiKit.NewButton(_widthRow, $"{cells} cells", () => _tools.RoadWidth = captured);
                UiKit.Place((RectTransform)button.transform, x, 2f, 64f, RowHeight - 4f);
                x += 68f;
            }

            _width = UiKit.NewLabel(_widthRow, "", x + 4f, 0f, 56f, RowHeight, 14);
            _asBuilt = UiKit.NewToggle(_widthRow, "As built", true, on => _tools.Roads.ShowAsBuilt = on);
            UiKit.Place((RectTransform)_asBuilt.transform, x + 64f, 4f, Width - 2 * Pad - x - 64f, RowHeight - 8f);
            UiKit.AddTooltip(_asBuilt, () => "Show the ground as it will be once the road is built; off shows the plan: grades over the cut and fill (V)");

            _rampRow = Row("Ramp");
            _ramp = UiKit.NewToggle(_rampRow, "Ramp to", false, on =>
            {
                _tools.LevelRamp = on;
                if (on && Mathf.Approximately(_tools.LevelHeightB, 0f))
                    _tools.LevelHeightB = _tools.TargetHeight;
            });
            UiKit.Place((RectTransform)_ramp.transform, 0f, 4f, 100f, RowHeight - 8f);
            var rampDown = UiKit.NewButton(_rampRow, "−", () => _tools.LevelHeightB -= _tools.HeightStep, null, 18);
            UiKit.Place((RectTransform)rampDown.transform, 124f, 2f, 34f, RowHeight - 4f);
            _rampField = UiKit.NewNumberField(_rampRow, SubmitRamp);
            UiKit.Place((RectTransform)_rampField.transform, 162f, 2f, 90f, RowHeight - 4f);
            var rampUp = UiKit.NewButton(_rampRow, "+", () => _tools.LevelHeightB += _tools.HeightStep, null, 18);
            UiKit.Place((RectTransform)rampUp.transform, 256f, 2f, 34f, RowHeight - 4f);
            UiKit.NewLabel(_rampRow, "m at the far edge", 296f, 0f, 170f, RowHeight, 14);
            UiKit.AddTooltip(_ramp, () => "Slope the pad: H at the edge you start dragging from, this height at the far edge");

            _roadGradeRow = Row("Grades");
            _grades = UiKit.NewLabel(_roadGradeRow, "", 0f, 0f, Width - 2 * Pad, RowHeight, 14);
            _roadBendRow = Row("Bends");
            _bends = UiKit.NewLabel(_roadBendRow, "", 0f, 0f, Width - 2 * Pad, RowHeight, 14);

            _roadOptionsRow = Row("Road options");
            UiKit.NewLabel(_roadOptionsRow, "Max grade", 0f, 0f, 80f, RowHeight);
            _maxGradeField = UiKit.NewNumberField(_roadOptionsRow, SubmitMaxGrade);
            UiKit.Place((RectTransform)_maxGradeField.transform, 84f, 2f, 56f, RowHeight - 4f);
            UiKit.NewLabel(_roadOptionsRow, "%", 144f, 0f, 20f, RowHeight);
            _snap45 = UiKit.NewToggle(_roadOptionsRow, "Snap 45°", true, on => _tools.Roads.Draft.Snap45 = on);
            UiKit.Place((RectTransform)_snap45.transform, 170f, 4f, 110f, RowHeight - 8f);
            _lockNode = UiKit.NewToggle(_roadOptionsRow, "Node on ground", true, on => _tools.Roads.SetActiveLocked(on));
            UiKit.Place((RectTransform)_lockNode.transform, 290f, 4f, 170f, RowHeight - 8f);
            UiKit.AddTooltip(_lockNode, () => "The selected node follows the ground; scroll over a node (or PageUp/PageDown) to set its height");

            // Shaping: how tight a bend the crew will take, and the two things that shape one.
            _roadShapeRow = Row("Road shaping");
            UiKit.NewLabel(_roadShapeRow, "Min bend", 0f, 0f, 80f, RowHeight);
            _minBendField = UiKit.NewNumberField(_roadShapeRow, SubmitMinBend);
            UiKit.Place((RectTransform)_minBendField.transform, 84f, 2f, 56f, RowHeight - 4f);
            UiKit.NewLabel(_roadShapeRow, "m", 144f, 0f, 20f, RowHeight);
            var smooth = UiKit.NewButton(_roadShapeRow, "Smooth", () => _tools.Roads.SmoothWholeRoad());
            UiKit.Place((RectTransform)smooth.transform, 170f, 2f, 90f, RowHeight - 4f);
            UiKit.AddTooltip(smooth, () => "Round every bend out to the minimum, cutting a corner into a proper arc where pulling its handle cannot reach (C, or Shift+C)");
            _cutThrough = UiKit.NewToggle(_roadShapeRow, "Cut through", false,
                on => _tools.Roads.Draft.FollowGround = !on);
            UiKit.Place((RectTransform)_cutThrough.transform, 268f, 4f, 130f, RowHeight - 8f);
            UiKit.AddTooltip(_cutThrough, () => "Nodes keep the level of the one before instead of climbing the ground: a rise becomes a cutting to dig out, a dip an embankment to fill");
            _holdGrade = UiKit.NewToggle(_roadShapeRow, "Hold the grade", false, on =>
            {
                var draft = _tools.Roads.Draft;
                draft.GradeLock = on ? draft.MaxGrade : (float?)null;
            });
            UiKit.Place((RectTransform)_holdGrade.transform, 400f, 4f, 150f, RowHeight - 8f);
            UiKit.AddTooltip(_holdGrade, () => "Nodes placed climb at the max grade instead of sitting on the ground (G); Shift+G re-cuts the road already drawn");

            // The selected node's own height: typed outright, or held over the ground as a causeway.
            _roadNodeRow = Row("Road node");
            UiKit.NewLabel(_roadNodeRow, "Node at", 0f, 0f, 70f, RowHeight);
            _nodeHeightField = UiKit.NewNumberField(_roadNodeRow, SubmitNodeHeight);
            UiKit.Place((RectTransform)_nodeHeightField.transform, 74f, 2f, 70f, RowHeight - 4f);
            UiKit.NewLabel(_roadNodeRow, "m", 148f, 0f, 20f, RowHeight);
            UiKit.NewLabel(_roadNodeRow, "over the ground", 172f, 0f, 130f, RowHeight);
            _overGroundField = UiKit.NewNumberField(_roadNodeRow, SubmitOverGround);
            UiKit.Place((RectTransform)_overGroundField.transform, 304f, 2f, 70f, RowHeight - 4f);
            UiKit.NewLabel(_roadNodeRow, "m", 378f, 0f, 20f, RowHeight);
            UiKit.AddTooltip(_overGroundField, () => "Carry the node this far over the land, still following it — a causeway (H, Shift+H)");

            _roadHintRow = Row("Road hints");
            UiKit.NewLabel(_roadHintRow, "Click: node (Alt: free) · drag · Ctrl+drag: raise · C: round · right-click: back · Ctrl+Z · V: view · Enter: lay",
                0f, 0f, Width - 2 * Pad, RowHeight, 12).color = new Color(0.75f, 0.77f, 0.8f);

            _clearRow = Row("Clear");
            var clearDig = UiKit.NewToggle(_clearRow, "Dig", _tools.ClearDig, on => _tools.ClearDig = on);
            UiKit.Place((RectTransform)clearDig.transform, 0f, 4f, 90f, RowHeight - 8f);
            var clearFill = UiKit.NewToggle(_clearRow, "Fill", _tools.ClearFill, on => _tools.ClearFill = on);
            UiKit.Place((RectTransform)clearFill.transform, 100f, 4f, 90f, RowHeight - 8f);
            var clearZones = UiKit.NewToggle(_clearRow, "Dump Zones", _tools.ClearZones, on => _tools.ClearZones = on);
            UiKit.Place((RectTransform)clearZones.transform, 200f, 4f, 140f, RowHeight - 8f);
            var clearQuarries = UiKit.NewToggle(_clearRow, "Quarries", _tools.ClearQuarries, on => _tools.ClearQuarries = on);
            UiKit.Place((RectTransform)clearQuarries.transform, 346f, 4f, 120f, RowHeight - 8f);

            _quarryRow = Row("Quarry");
            _quarryNote = UiKit.NewLabel(_quarryRow, "", 0f, 0f, Width - 2 * Pad, RowHeight, 14);

            // The selected worksite: what it has, and the three things done to it.
            _worksiteRow = Row("Worksite");
            _worksiteNote = UiKit.NewLabel(_worksiteRow, "", 0f, 0f, Width - 2 * Pad, RowHeight, 14);
            _worksiteButtons = Row("Worksite actions");
            var assign = UiKit.NewButton(_worksiteButtons, "Assign selected", () =>
            {
                var sites = _tools.Worksites;
                if (sites != null && sites.Selected != 0 && _tools.Crew != null)
                    _tools.Say($"Assigned {_tools.Crew.Assign(sites.Selected)} to worksite {sites.Selected}");
            }, null, 13);
            UiKit.Place((RectTransform)assign.transform, 0f, 2f, 150f, RowHeight - 4f);
            UiKit.AddTooltip(assign, () => "The units selected in the crew panel or on the map work this worksite's area and nothing else");
            var release = UiKit.NewButton(_worksiteButtons, "Release all", () =>
            {
                var sites = _tools.Worksites;
                if (sites != null)
                    _tools.Say($"Released {sites.ReleaseSelected()}; they park until they are assigned again");
            }, null, 13);
            UiKit.Place((RectTransform)release.transform, 156f, 2f, 130f, RowHeight - 4f);
            var remove = UiKit.NewButton(_worksiteButtons, "Remove", () => _tools.Worksites?.RemoveSelected(), null, 13);
            UiKit.Place((RectTransform)remove.transform, 292f, 2f, Width - 2 * Pad - 292f, RowHeight - 4f);
            _worksiteHint = Row("Worksite hints");
            UiKit.NewLabel(_worksiteHint, "Click round an area, Enter: new · drag: rectangle · drag nodes · Ctrl+click edge: add · Del · C: curve",
                0f, 0f, Width - 2 * Pad, RowHeight, 12).color = new Color(0.75f, 0.77f, 0.8f);
        }

        RectTransform _worksiteRow, _worksiteButtons, _worksiteHint;
        Text _worksiteNote;

        /// <summary>Cubic metres the marked quarries still hold above their floors, recounted four times a second.</summary>
        float QuarryStock()
        {
            var map = _tools.Map;
            if (map == null)
                return 0f;
            if (Time.unscaledTime - _stockAt < 0.25f)
                return _quarryStock;
            _stockAt = Time.unscaledTime;
            _quarryStock = 0f;
            var cells = map.QuarryCells;
            for (var i = 0; i < cells.Count; i++)
                _quarryStock += map.QuarryLeft(cells[i] % map.Grid.Width, cells[i] / map.Grid.Width);
            return _quarryStock;
        }

        LandformDraft Stamp() => _tools.Terraform.Draft;

        /// <summary>
        /// A label, a step down, a box to type a number in, a step up and the unit, from
        /// <paramref name="x"/> along <paramref name="row"/>: 230 px of it.
        /// </summary>
        InputField Stepper(RectTransform row, string label, float x, string unit, System.Action down, System.Action up,
            System.Action<float> typed, string tooltip)
        {
            UiKit.NewLabel(row, label, x, 0f, 52f, RowHeight);
            var less = UiKit.NewButton(row, "−", down, null, 18);
            UiKit.Place((RectTransform)less.transform, x + 54f, 2f, 30f, RowHeight - 4f);
            var field = UiKit.NewNumberField(row, text =>
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || float.TryParse(text, out value))
                    typed(value);
            });
            UiKit.Place((RectTransform)field.transform, x + 88f, 2f, 70f, RowHeight - 4f);
            var more = UiKit.NewButton(row, "+", up, null, 18);
            UiKit.Place((RectTransform)more.transform, x + 162f, 2f, 30f, RowHeight - 4f);
            UiKit.NewLabel(row, unit, x + 196f, 0f, 30f, RowHeight);
            UiKit.AddTooltip(less, () => tooltip);
            UiKit.AddTooltip(more, () => tooltip);
            UiKit.AddTooltip(field, () => tooltip);
            return field;
        }

        /// <summary>The library's stamps as a row of buttons, the one in hand lit.</summary>
        void BuildStampButtons()
        {
            var host = _tools.Terraform;
            if (host == null || _stampButtons.Count > 0)
                return;
            // Only reads the library: picking a stamp here would put the one in hand back to its
            // own size and height every time the panel is first shown.
            host.LoadStamps();
            // Six to a line, so the names fit; three lines hold eighteen.
            const int perLine = 6;
            var width = (Width - 2 * Pad - 70f) / perLine - 3f;
            UiKit.NewLabel(_stampRow, "Stamp", 0f, 0f, 66f, RowHeight);
            var lines = new[] { _stampRow, _stampRow2, _stampRow3 };
            for (var i = 0; i < host.Stamps.Count && i < lines.Length * perLine; i++)
            {
                var stamp = host.Stamps[i];
                var button = UiKit.NewButton(lines[i / perLine], stamp.DisplayName, () => host.ChooseStamp(stamp), null, 13);
                UiKit.Place((RectTransform)button.transform, 70f + i % perLine * (width + 3f), 2f, width, RowHeight - 4f);
                UiKit.AddTooltip(button, () => $"{stamp.DisplayName}: {stamp.NativeSize:0} m across, {stamp.NativeHeight:0.#} m high as it comes   (T steps through them)");
                _stampButtons.Add((stamp, button.GetComponent<Image>(), button.GetComponentInChildren<Text>()));
            }
        }

        RectTransform Row(string name)
        {
            var row = UiKit.NewRect(_panel, name);
            row.gameObject.SetActive(false);
            return row;
        }

        void Show(params RectTransform[] rows)
        {
            foreach (var row in new[] { _stampRow, _stampRow2, _stampRow3, _stampShapeRow, _stampHeightRow, _godRow, _brushRow, _heightRow, _followRow, _pickRow, _volumeRow, _capRow, _widthRow, _clearRow, _rampRow, _roadGradeRow, _roadBendRow, _roadOptionsRow, _roadShapeRow, _roadNodeRow, _roadHintRow, _quarryRow, _worksiteRow, _worksiteButtons, _worksiteHint })
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
            var stampInHand = mode == ToolMode.Terraform && _tools.Terraform != null
                                                         && _tools.Terraform.Draft.Form.Kind == LandformKind.Stamp;
            if (mode != _builtFor || stampInHand != _builtStamp)
            {
                _builtFor = mode;
                _builtStamp = stampInHand;
                switch (mode)
                {
                    case ToolMode.Dig:
                    case ToolMode.Fill:
                        Show(_brushRow, _heightRow, _followRow, _pickRow, _volumeRow);
                        break;
                    case ToolMode.Level:
                        Show(_heightRow, _rampRow, _followRow, _pickRow, _volumeRow);
                        break;
                    case ToolMode.DumpZone:
                        Show(_heightRow, _followRow, _pickRow, _capRow);
                        break;
                    case ToolMode.Road:
                        Show(_widthRow, _roadOptionsRow, _roadShapeRow, _roadNodeRow, _roadGradeRow, _roadBendRow, _volumeRow, _roadHintRow);
                        break;
                    case ToolMode.Quarry:
                        Show(_heightRow, _followRow, _pickRow, _quarryRow);
                        break;
                    case ToolMode.Terraform when stampInHand:
                        BuildStampButtons();
                        Show(_stampRow, _stampRow2, _stampRow3, _stampShapeRow, _stampHeightRow, _godRow, _volumeRow);
                        break;
                    case ToolMode.Terraform:
                        Show(_heightRow, _followRow, _pickRow, _volumeRow);
                        break;
                    case ToolMode.Clear:
                        Show(_clearRow);
                        break;
                    case ToolMode.Worksite:
                        Show(_worksiteRow, _worksiteButtons, _worksiteHint);
                        break;
                    default:
                        Show();
                        break;
                }

                _title.text = mode switch
                {
                    ToolMode.Dig => "Dig  —  paint where the crew digs down to H",
                    ToolMode.Fill => "Fill  —  paint where the crew fills up to H",
                    ToolMode.Level => "Level  —  drag a pad to H, or a ramp from H to the far edge",
                    ToolMode.DumpZone => "Dump Zone  —  drag where spoil may be tipped",
                    ToolMode.Road => "Road  —  a spline the crew builds; click a road to edit it",
                    ToolMode.Quarry => "Quarry  —  drag where the crew may dig for fill material, down to H",
                    ToolMode.Terraform when stampInHand => "Stamp  —  click to place it; the crew build it",
                    ToolMode.Terraform => "Terraform  —  click corners, Enter to commit; the crew build it to H",
                    ToolMode.Clear => "Clear  —  drag over designations to take them off",
                    ToolMode.Worksite => "Worksite  —  outline a work area, any shape; vehicles assigned to it work there",
                    _ => "",
                };
            }

            if (!_panel.gameObject.activeSelf)
                return;

            if (_stampShapeRow.gameObject.activeSelf)
            {
                var placement = Stamp().Form.Placement;
                if (!_stampSize.isFocused)
                    _stampSize.SetTextWithoutNotify(placement.Size.ToString("0.#", CultureInfo.InvariantCulture));
                if (!_stampTurn.isFocused)
                    _stampTurn.SetTextWithoutNotify(placement.Rotation.ToString("0", CultureInfo.InvariantCulture));
                if (!_stampHeight.isFocused)
                    _stampHeight.SetTextWithoutNotify(placement.Height.ToString("0.##", CultureInfo.InvariantCulture));
                _stampFlip.SetIsOnWithoutNotify(placement.Invert);
            }

            if (_godRow.gameObject.activeSelf)
            {
                _god.SetIsOnWithoutNotify(_tools.Terraform.GodMode);
                _title.text = _tools.Terraform.GodMode
                    ? "Stamp  —  GOD MODE: click to shape the ground now"
                    : "Stamp  —  click to place it; the crew build it";
            }

            if (_stampRow.gameObject.activeSelf)
            {
                var inHand = _tools.Terraform.Draft.Form.ResolveStamp();
                foreach (var (stamp, background, label) in _stampButtons)
                {
                    background.color = stamp == inHand ? UiKit.Accent : UiKit.ButtonColor;
                    label.color = stamp == inHand ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
                }
            }

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
                var what = mode == ToolMode.Dig || mode == ToolMode.Fill ? "Under the brush" : mode == ToolMode.Road ? "Road with its faces"
                    : _builtStamp ? "This stamp with its faces" : "This pad";
                _volume.text = cut < 0.05f && fill < 0.05f
                    ? $"{what}: nothing to move at H"
                    : $"{what}:  cut {cut:0.#} m³   fill {fill:0.#} m³   net {cut - fill:+0.#;-0.#;0} m³";
                _volume.color = Color.white;
                var god = _builtStamp && _tools.Terraform != null && _tools.Terraform.GodMode;
                if (god)
                    _volume.text = $"God stamp: raises about {fill:0.#} m³, lowers {cut:0.#} m³ — no crew, no quarry";
                if (!god && fill > 0.05f && QuarryStock() < fill - 0.05f)
                {
                    _volume.text += $"   —   needs {fill - QuarryStock():0.#} m³ from a quarry";
                    _volume.color = UiKit.Warning;
                }
            }

            if (_capRow.gameObject.activeSelf)
            {
                if (!_capField.isFocused)
                    _capField.SetTextWithoutNotify(_tools.ZoneCapAbove.ToString("0.##", CultureInfo.InvariantCulture));
                _capNote.text = $"m   (cap {_tools.TargetHeight + _tools.ZoneCapAbove:0.#} m)";
            }

            if (_quarryRow.gameObject.activeSelf)
            {
                var map = _tools.Map;
                var cells = map != null ? map.QuarryCount : 0;
                _quarryNote.text = cells == 0
                    ? "Nothing marked yet: drag out a quarry. The crew digs it only to fill something."
                    : $"{cells} cells marked, {QuarryStock():0.#} m³ above their floors — dug only when a Fill needs material";
                _quarryNote.color = cells == 0 ? UiKit.Warning : Color.white;
            }

            if (_worksiteRow.gameObject.activeSelf)
            {
                var sites = _tools.Worksites;
                var site = sites != null && sites.Sites != null ? sites.Sites.Get(sites.Selected) : null;
                _worksiteNote.text = site == null
                    ? (sites != null && sites.Sites != null && sites.Sites.Count > 0 ? "Click a worksite to select it" : "No worksites yet: drag one out")
                    : $"{site.Name}: {site.CellCount * _tools.CellSize * _tools.CellSize:#,0} m² — {sites.Crew(site)}";
            }

            if (_widthRow.gameObject.activeSelf)
            {
                _width.text = $"now {_tools.RoadWidth}";
                if (_tools.Roads != null)
                    _asBuilt.SetIsOnWithoutNotify(_tools.Roads.ShowAsBuilt);
            }

            if (_rampRow.gameObject.activeSelf)
            {
                _ramp.SetIsOnWithoutNotify(_tools.LevelRamp);
                if (!_rampField.isFocused)
                    _rampField.SetTextWithoutNotify(_tools.LevelHeightB.ToString("0.##", CultureInfo.InvariantCulture));
            }

            if (_roadOptionsRow.gameObject.activeSelf)
            {
                var roads = _tools.Roads;
                if (!_maxGradeField.isFocused)
                    _maxGradeField.SetTextWithoutNotify((roads.Draft.MaxGrade * 100f).ToString("0.#", CultureInfo.InvariantCulture));
                _snap45.SetIsOnWithoutNotify(roads.Draft.Snap45);
                _lockNode.SetIsOnWithoutNotify(roads.ActiveLocked);
                _lockNode.interactable = roads.ActiveNode >= 0;

                var text = new System.Text.StringBuilder();
                if (roads.Grades.Count == 0)
                {
                    text.Append(roads.Draft.EditingRoad != 0 ? $"Editing road {roads.Draft.EditingRoad}" : "Grades: place two nodes");
                }
                else
                {
                    text.Append("Grades: ");
                    for (var i = 0; i < roads.Grades.Count; i++)
                        text.Append(i > 0 ? "  " : "").Append((roads.Grades[i] * 100f).ToString("0.#")).Append('%');
                    if (roads.State == TinyDiggers.Units.RoadGradeState.Refused)
                        text.Append("   refused: over twice the limit");
                    else if (roads.State == TinyDiggers.Units.RoadGradeState.Steep)
                        text.Append("   steep");
                }

                _grades.text = text.ToString();
                _grades.color = roads.State == TinyDiggers.Units.RoadGradeState.Refused ? new Color(1f, 0.45f, 0.4f)
                    : roads.State == TinyDiggers.Units.RoadGradeState.Steep ? UiKit.Warning : Color.white;
            }

            if (_roadShapeRow.gameObject.activeSelf)
            {
                var roads = _tools.Roads;
                var draft = roads.Draft;
                if (!_minBendField.isFocused)
                    _minBendField.SetTextWithoutNotify(draft.MinTurnRadius.ToString("0.#", CultureInfo.InvariantCulture));
                _holdGrade.SetIsOnWithoutNotify(draft.GradeLock.HasValue);
                _cutThrough.SetIsOnWithoutNotify(!draft.FollowGround);

                var node = roads.ActiveNode >= 0 && roads.ActiveNode < draft.Nodes.Count ? draft.Nodes[roads.ActiveNode] : null;
                _nodeHeightField.interactable = node != null;
                _overGroundField.interactable = node != null;
                if (node != null && !_nodeHeightField.isFocused)
                    _nodeHeightField.SetTextWithoutNotify(node.Height.ToString("0.##", CultureInfo.InvariantCulture));
                if (node != null && !_overGroundField.isFocused)
                    _overGroundField.SetTextWithoutNotify(node.GroundOffset.ToString("0.##", CultureInfo.InvariantCulture));
                if (node == null)
                {
                    _nodeHeightField.SetTextWithoutNotify("");
                    _overGroundField.SetTextWithoutNotify("");
                }
            }

            if (_roadBendRow.gameObject.activeSelf)
            {
                var roads = _tools.Roads;
                var bends = new System.Text.StringBuilder();
                if (roads.Turns.Count == 0)
                {
                    bends.Append("Bends: place two nodes");
                }
                else
                {
                    bends.Append("Bends: ");
                    for (var i = 0; i < roads.Turns.Count; i++)
                        bends.Append(i > 0 ? "  " : "").Append(Bend(roads.Turns[i]));
                    if (roads.TurnState == TinyDiggers.Units.RoadGradeState.Refused)
                        bends.Append("   too tight to drive");
                    else if (roads.TurnState == TinyDiggers.Units.RoadGradeState.Steep)
                        bends.Append("   tighter than the minimum");
                }

                _bends.text = bends.ToString();
                _bends.color = roads.TurnState == TinyDiggers.Units.RoadGradeState.Refused ? new Color(1f, 0.45f, 0.4f)
                    : roads.TurnState == TinyDiggers.Units.RoadGradeState.Steep ? UiKit.Warning : Color.white;
            }
        }

        void SubmitHeight(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                || float.TryParse(text, out height))
                _tools.SetTargetHeight(height);
        }

        void SubmitRamp(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                || float.TryParse(text, out height))
                _tools.LevelHeightB = height;
        }

        void SubmitMaxGrade(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                || float.TryParse(text, out percent))
                _tools.Roads.Draft.MaxGrade = Mathf.Clamp(percent, 1f, 100f) / 100f;
        }

        /// <summary>A bend in metres, or "straight" — a road that does not turn has no radius.</summary>
        static string Bend(float radius) =>
            float.IsInfinity(radius) || radius > 999f ? "straight" : radius.ToString("0.#") + " m";

        void SubmitMinBend(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres)
                || float.TryParse(text, out metres))
                _tools.Roads.Draft.MinTurnRadius = Mathf.Clamp(metres, 0.5f, 200f);
        }

        void SubmitNodeHeight(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres)
                || float.TryParse(text, out metres))
                _tools.Roads.SetActiveHeight(metres);
        }

        void SubmitOverGround(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres)
                || float.TryParse(text, out metres))
                _tools.Roads.SetGroundOffset(_tools.Roads.ActiveNode, metres);
        }

        void SubmitCap(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var above)
                || float.TryParse(text, out above))
                _tools.ZoneCapAbove = Mathf.Max(0f, above);
        }
    }
}
