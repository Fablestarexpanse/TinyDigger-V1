using System.Collections.Generic;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The units menu, top left (Ronan, 2026-09-24: "putting units in their own menu to free up
    /// screen space"). Closed, it is one button: "Units", how many, and a warning mark when one is
    /// stuck. Open (the button, or U), it is the roster grouped by kind, each kind a header that
    /// selects all of them with what they are doing summed up ("2 working, 2 idle"), the units under
    /// it a slim row each, the landing craft in its own group, the idle warnings rolled into one
    /// line, and hiring at the bottom.
    ///
    /// It replaced a list that was always open: eight rows each repeating "Parked: no worksite —
    /// assign it t…" and a dismiss cross on every row. Dismissing is for the selection now (the
    /// cross shows on a selected row only), and a stuck unit's warning shows whether the menu is
    /// open or not: that is a question for the player, not a detail.
    /// </summary>
    public sealed class CrewPanelView : MonoBehaviour
    {
        const float Width = 300f;
        const float ButtonHeight = 30f;
        const float HeaderHeight = 26f;
        const float RowHeight = 24f;
        const float BannerHeight = 26f;
        const float DoubleClickSeconds = 0.35f;

        static readonly UnitRole[] Kinds = { UnitRole.Worker, UnitRole.Digger, UnitRole.Hauler, UnitRole.Bulldozer, UnitRole.Paver };

        sealed class Row
        {
            public RectTransform Rect;
            public Image Background;
            public Text Name;
            public Text State;
            public RectTransform Load;
            public Button Dismiss;
        }

        sealed class Header
        {
            public RectTransform Rect;
            public Image Icon;
            public Text Name;
            public Text Summary;
        }

        CrewView _crew;
        RtsCamera _camera;
        FerryHost _ferries;
        RectTransform _root;
        Button _toggle;
        Text _toggleText;
        RectTransform _banner;
        Text _bannerText;
        RectTransform _panel;
        Text _idleLine;
        RectTransform _hireRow;
        float _hireHeight;
        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<UnitRole, Header> _headers = new Dictionary<UnitRole, Header>();
        Header _craftHeader;
        int _lastClicked = -1;
        float _lastClickTime;

        /// <summary>Whether the roster is open.</summary>
        public bool Open { get; private set; }

        public void Build(Canvas canvas, CrewView crew, RtsCamera rtsCamera)
        {
            _crew = crew;
            _camera = rtsCamera;
            _root = UiKit.NewRect(canvas.transform, "Units Menu");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0f, 1f);
            _root.anchoredPosition = new Vector2(12f, -12f);
            _root.sizeDelta = new Vector2(Width, ButtonHeight);

            // A plain button with its own icon and label: one built with an icon has no text in it.
            _toggle = UiKit.NewButton(_root, "", () => Open = !Open);
            var toggleRect = (RectTransform)_toggle.transform;
            UiKit.Place(toggleRect, 0f, 0f, 150f, ButtonHeight);
            var toggleIcon = UiKit.Place(UiKit.NewRect(toggleRect, "Icon"), 6f, 4f, 22f, 22f).gameObject.AddComponent<Image>();
            toggleIcon.sprite = ToolIcons.Get(ToolIcon.Worker);
            toggleIcon.raycastTarget = false;
            _toggleText = UiKit.NewLabel(toggleRect, "", 34f, 0f, 112f, ButtonHeight, 15);
            UiKit.AddTooltip(_toggle, () => (Open ? "Close" : "Open") + " the units: who you have, what they are doing, and hiring  (U)");

            // A stuck unit is a question for the player, so it shows with the menu closed too.
            _banner = UiKit.NewPanel(_root, "Units Warning", new Vector2(0f, 1f), Vector2.zero, new Vector2(Width, BannerHeight), UiKit.Warning);
            var bannerIcon = UiKit.Place(UiKit.NewRect(_banner, "Icon"), 5f, 3f, 20f, 20f).gameObject.AddComponent<Image>();
            bannerIcon.sprite = ToolIcons.Get(ToolIcon.Warning);
            bannerIcon.color = Color.black;
            bannerIcon.raycastTarget = false;
            _bannerText = UiKit.NewLabel(_banner, "", 30f, 0f, Width - 34f, BannerHeight, 13);
            _bannerText.color = Color.black;
            _banner.gameObject.SetActive(false);

            _panel = UiKit.NewPanel(_root, "Units", new Vector2(0f, 1f), Vector2.zero, new Vector2(Width, 40f));
            foreach (var kind in Kinds)
                _headers[kind] = NewHeader(kind);
            _craftHeader = NewHeader(null);
            _idleLine = UiKit.NewLabel(_panel, "", 8f, 0f, Width - 16f, 20f, 12);
            _idleLine.color = UiKit.Warning;

            // Hiring, at the foot of the list it adds to (2026-09-23: the inspector counts only
            // applied at load). Full names now there is the width for them.
            _hireRow = UiKit.NewRect(_panel, "Hire");
            UiKit.NewLabel(_hireRow, "Hire", 4f, 0f, 40f, 24f, 13);
            // Three to a line, and the row as tall as the lines it took, so none spill out of the panel.
            const int perLine = 3;
            var buttonWidth = (Width - 8f - 40f - 2f * perLine) / perLine;
            var placed = 0;
            foreach (var kind in Kinds)
            {
                var captured = kind;
                var button = UiKit.NewButton(_hireRow, UnitNames.Short(captured), () => _crew.Hire(captured), null, 12);
                UiKit.Place((RectTransform)button.transform, 40f + placed % perLine * (buttonWidth + 2f), placed / perLine * 26f, buttonWidth, 24f);
                UiKit.AddTooltip(button, () => $"Take on one more {UnitNames.Of(captured).ToLowerInvariant()}; it turns up in the yard with the rest");
                placed++;
            }

            _hireHeight = (placed + perLine - 1) / perLine * 26f;

            _panel.gameObject.SetActive(false);
        }

        Header NewHeader(UnitRole? kind)
        {
            var header = new Header();
            var button = UiKit.NewButton(_panel, "", () =>
            {
                if (kind.HasValue)
                    SelectKind(kind.Value);
                else if (_ferries != null)
                    _ferries.Select();
            });
            header.Rect = (RectTransform)button.transform;
            header.Icon = UiKit.Place(UiKit.NewRect(header.Rect, "Icon"), 4f, 2f, 22f, 22f).gameObject.AddComponent<Image>();
            header.Icon.sprite = ToolIcons.Get(kind.HasValue ? RoleIcon(kind.Value) : ToolIcon.Hauler);
            header.Icon.raycastTarget = false;
            header.Name = UiKit.NewLabel(header.Rect, "", 32f, 0f, 130f, HeaderHeight, 14);
            header.Summary = UiKit.NewLabel(header.Rect, "", 150f, 0f, Width - 160f, HeaderHeight, 12);
            header.Summary.alignment = TextAnchor.MiddleRight;
            header.Summary.color = new Color(0.8f, 0.82f, 0.85f);
            UiKit.AddTooltip(button, () => kind.HasValue
                ? $"Select every {UnitNames.Of(kind.Value).ToLowerInvariant()}"
                : "Select the landing craft: then right-click a shore to send it there");
            return header;
        }

        Row AddRow(int index)
        {
            var row = new Row();
            var captured = index;
            var button = UiKit.NewButton(_panel, "", () => Clicked(captured));
            row.Rect = (RectTransform)button.transform;
            row.Background = button.GetComponent<Image>();
            UiKit.AddTooltip(button, () => captured < _crew.Units.Count ? _crew.Units[captured].Status + "   (double-click to go to it)" : "");
            row.Name = UiKit.NewLabel(row.Rect, "", 32f, 0f, 90f, RowHeight, 13);
            row.State = UiKit.NewLabel(row.Rect, "", 122f, 0f, Width - 222f, RowHeight, 11);
            row.State.color = new Color(0.8f, 0.82f, 0.85f);

            var loadBack = UiKit.Place(UiKit.NewRect(row.Rect, "Load"), Width - 96f, 8f, 50f, 8f);
            loadBack.gameObject.AddComponent<Image>().color = UiKit.FieldColor;
            row.Load = UiKit.NewRect(loadBack, "Fill");
            row.Load.anchorMin = Vector2.zero;
            row.Load.anchorMax = new Vector2(0f, 1f);
            row.Load.offsetMin = row.Load.offsetMax = Vector2.zero;
            row.Load.gameObject.AddComponent<Image>().color = UiKit.Accent;

            // Only on a row that is selected: a cross on every row was a unit let go by a slip.
            row.Dismiss = UiKit.NewButton(row.Rect, "×", () =>
            {
                if (captured < _crew.Units.Count)
                    _crew.Dismiss(captured);
            });
            UiKit.Place((RectTransform)row.Dismiss.transform, Width - 38f, 2f, 22f, RowHeight - 4f);
            UiKit.AddTooltip(row.Dismiss, () => captured < _crew.Units.Count
                ? $"Let this {UnitNames.Of(_crew.Units[captured].Role).ToLowerInvariant()} go — anything it is carrying goes with it"
                : "");
            _rows.Add(row);
            return row;
        }

        static ToolIcon RoleIcon(UnitRole role) =>
            // The road machines share the truck's icon until they have their own.
            role == UnitRole.Hauler || role == UnitRole.Bulldozer || role == UnitRole.Paver ? ToolIcon.Hauler
            : role == UnitRole.Digger ? ToolIcon.Digger : ToolIcon.Worker;

        void SelectKind(UnitRole kind)
        {
            var add = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            if (!add)
                _crew.Deselect();
            _ferries?.Deselect();
            for (var i = 0; i < _crew.Units.Count; i++)
                if (_crew.Units[i].Role == kind)
                    _crew.Select(i, true);
        }

        void Clicked(int index)
        {
            var add = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            _ferries?.Deselect();
            _crew.Select(index, add);
            if (index == _lastClicked && Time.unscaledTime - _lastClickTime < DoubleClickSeconds && _camera != null)
                _camera.FocusOn(_crew.BodyPosition(index));
            _lastClicked = index;
            _lastClickTime = Time.unscaledTime;
        }

        static bool Idle(CrewUnit unit) =>
            unit.State == CrewUnitState.Idle || unit.State == CrewUnitState.Parked && unit.Job == CrewJobKind.None;

        static bool Busy(CrewUnit unit) =>
            unit.State == CrewUnitState.Digging || unit.State == CrewUnitState.Tipping || unit.State == CrewUnitState.Moving
            || unit.State == CrewUnitState.Transferring || unit.State == CrewUnitState.RoadWork || unit.State == CrewUnitState.Parked
            || unit.OnFerry;

        void Update()
        {
            if (_crew == null || _root == null)
                return;
            if (_ferries == null)
                _ferries = FindAnyObjectByType<FerryHost>();
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.uKey.wasPressedThisFrame)
                Open = !Open;

            var units = _crew.Units;
            int stuck = 0, needMaterial = 0, noWorksite = 0;
            foreach (var unit in units)
            {
                if (unit.State == CrewUnitState.NeedsSomewhereToTip)
                    stuck++;
                else if (unit.State == CrewUnitState.NeedsMaterial)
                    needMaterial++;
                if (unit.Status != null && unit.Status.Contains("no worksite"))
                    noWorksite++;
            }

            var warning = stuck > 0 || needMaterial > 0;
            _toggleText.text = $"Units  {units.Count}" + (warning ? "   !" : "") + (Open ? "   ▴" : "   ▾");
            _banner.gameObject.SetActive(warning);
            if (stuck > 0)
                _bannerText.text = stuck == 1 ? "1 unit needs a Dump Zone or a Fill to tip into" : $"{stuck} units need a Dump Zone or a Fill to tip into";
            else if (needMaterial > 0)
                _bannerText.text = needMaterial == 1 ? "1 unit has nothing to fill with: mark a Quarry" : $"{needMaterial} units have nothing to fill with: mark a Quarry";
            var top = ButtonHeight + 4f;
            if (warning)
            {
                UiKit.Place(_banner, 0f, top, Width, BannerHeight);
                top += BannerHeight + 4f;
            }

            _panel.gameObject.SetActive(Open);
            if (!Open)
            {
                _root.sizeDelta = new Vector2(Width, top);
                return;
            }

            UiKit.Place(_panel, 0f, top, Width, 40f);
            while (_rows.Count < units.Count)
                AddRow(_rows.Count);
            foreach (var row in _rows)
                row.Rect.gameObject.SetActive(false);

            var y = 6f;
            foreach (var kind in Kinds)
            {
                var header = _headers[kind];
                int count = 0, busy = 0, idle = 0;
                foreach (var unit in units)
                    if (unit.Role == kind)
                    {
                        count++;
                        if (Busy(unit))
                            busy++;
                        else if (Idle(unit))
                            idle++;
                    }

                header.Rect.gameObject.SetActive(count > 0);
                if (count == 0)
                    continue;
                UiKit.Place(header.Rect, 4f, y, Width - 8f, HeaderHeight);
                header.Name.text = count > 1 ? $"{UnitNames.Of(kind)}  ×{count}" : UnitNames.Of(kind);
                header.Summary.text = busy > 0 && idle > 0 ? $"{busy} working · {idle} idle"
                    : busy > 0 ? (count > 1 ? "all working" : "working")
                    : idle > 0 ? (count > 1 ? "all idle" : "idle") : "";
                y += HeaderHeight + 2f;

                var number = 0;
                for (var i = 0; i < units.Count; i++)
                {
                    var unit = units[i];
                    if (unit.Role != kind)
                        continue;
                    number++;
                    var row = _rows[i];
                    row.Rect.gameObject.SetActive(true);
                    UiKit.Place(row.Rect, 4f, y, Width - 8f, RowHeight);
                    y += RowHeight + 1f;
                    row.Name.text = unit.Site != 0 ? $"{number} · WS{unit.Site}" : $"{number}";
                    row.State.text = Short(unit);
                    row.State.color = unit.State == CrewUnitState.NeedsSomewhereToTip || unit.State == CrewUnitState.NeedsMaterial
                        ? UiKit.Warning : new Color(0.8f, 0.82f, 0.85f);
                    var full = unit.Inventory.Capacity > 0f ? Mathf.Clamp01(unit.Inventory.Total / unit.Inventory.Capacity) : 0f;
                    row.Load.anchorMax = new Vector2(full, 1f);
                    var selected = _crew.IsSelected(i);
                    row.Background.color = selected ? new Color(0.3f, 0.27f, 0.12f, 0.95f) : UiKit.ButtonColor;
                    row.Dismiss.gameObject.SetActive(selected);
                }

                y += 4f;
            }

            // The landing craft, its own group.
            var craft = _ferries != null ? _ferries.Craft : null;
            _craftHeader.Rect.gameObject.SetActive(craft != null);
            if (craft != null)
            {
                UiKit.Place(_craftHeader.Rect, 4f, y, Width - 8f, HeaderHeight);
                _craftHeader.Name.text = "Landing craft";
                _craftHeader.Summary.text = craft.Status;
                ((Image)_craftHeader.Rect.GetComponent<Image>()).color = _ferries.Selected ? new Color(0.3f, 0.27f, 0.12f, 0.95f) : UiKit.ButtonColor;
                y += HeaderHeight + 6f;
            }

            // Everyone who is waiting for a worksite, said once.
            _idleLine.gameObject.SetActive(noWorksite > 0);
            if (noWorksite > 0)
            {
                _idleLine.text = noWorksite == 1 ? "1 unit idle: no worksite — assign it to one" : $"{noWorksite} units idle: no worksite — assign them to one";
                UiKit.Place((RectTransform)_idleLine.transform, 8f, y, Width - 16f, 18f);
                y += 22f;
            }

            UiKit.Place(_hireRow, 4f, y, Width - 8f, _hireHeight);
            y += _hireHeight + 6f;
            _panel.sizeDelta = new Vector2(Width, y);
            _root.sizeDelta = new Vector2(Width, top + y);
        }

        /// <summary>The unit's state in a few words, without the detail its full status carries.</summary>
        static string Short(CrewUnit unit)
        {
            var status = unit.Status ?? unit.State.ToString();
            if (status.Contains("no worksite"))
                status = "idle";
            return status.Length > 26 ? status.Substring(0, 25) + "…" : status;
        }
    }
}
