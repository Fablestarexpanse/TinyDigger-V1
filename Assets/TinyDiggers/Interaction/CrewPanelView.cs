using System.Collections.Generic;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The crew, top left (Slice 17): a row per unit with its role's icon, what it is doing and how
    /// full its scoop, bed or barrow is. Clicking a row selects the unit; double-clicking also
    /// takes the camera to it. While any unit is loaded with nowhere it may tip, a yellow banner
    /// across the top says so: that is a question for the player, not a fault.
    /// </summary>
    public sealed class CrewPanelView : MonoBehaviour
    {
        const float Width = 330f;
        const float RowHeight = 40f;
        const float BannerHeight = 30f;
        const float HireHeight = 30f;
        const float DoubleClickSeconds = 0.35f;

        sealed class Row
        {
            public RectTransform Rect;
            public Image Background;
            public Image Icon;
            public Text Name;
            public Text State;
            public RectTransform Load;
            public Image LoadFill;
        }

        CrewView _crew;
        RtsCamera _camera;
        RectTransform _panel;
        RectTransform _banner;
        Text _bannerText;
        readonly List<Row> _rows = new List<Row>();
        int _lastClicked = -1;
        float _lastClickTime;

        public void Build(Canvas canvas, CrewView crew, RtsCamera rtsCamera)
        {
            _crew = crew;
            _camera = rtsCamera;
            _panel = UiKit.NewPanel(canvas.transform, "Crew Panel", new Vector2(0f, 1f), new Vector2(12f, -12f), new Vector2(Width, 40f));
            UiKit.NewLabel(_panel, "Crew", 10f, 4f, 200f, 22f, 16);

            _banner = UiKit.NewPanel(_panel, "Needs a Dump Zone", new Vector2(0f, 1f), Vector2.zero, new Vector2(Width, BannerHeight), UiKit.Warning);
            UiKit.Place(_banner, 0f, 28f, Width, BannerHeight);
            var bannerIcon = UiKit.Place(UiKit.NewRect(_banner, "Icon"), 6f, 3f, 24f, 24f).gameObject.AddComponent<Image>();
            bannerIcon.sprite = ToolIcons.Get(ToolIcon.Warning);
            bannerIcon.color = Color.black;
            bannerIcon.raycastTarget = false;
            _bannerText = UiKit.NewLabel(_banner, "", 36f, 0f, Width - 40f, BannerHeight, 14);
            _bannerText.color = Color.black;
            _banner.gameObject.SetActive(false);

            // Taking someone on. The counts in the inspector only ever applied at load, so trying
            // a second dumper meant stopping the game, editing and starting over (Ronan,
            // 2026-09-23). The row sits under the crew, where the crew it adds to is.
            _hireRow = UiKit.Place(UiKit.NewRect(_panel, "Hire"), 4f, 0f, Width - 8f, HireHeight);
            UiKit.NewLabel(_hireRow, "Take on", 6f, 0f, 56f, HireHeight, 14);
            // One word a button, not the full name: three full names side by side ran into each
            // other at this width (2026-09-23). The full name is in the tooltip. Five now, with the
            // road machines (2026-09-24), so narrower buttons in a smaller hand.
            var x = 60f;
            foreach (var role in new[] { UnitRole.Worker, UnitRole.Digger, UnitRole.Hauler, UnitRole.Bulldozer, UnitRole.Paver })
            {
                var captured = role;
                var button = UiKit.NewButton(_hireRow, UnitNames.Short(role), () => _crew.Hire(captured), null, 12);
                UiKit.Place((RectTransform)button.transform, x, 2f, 49f, HireHeight - 4f);
                UiKit.AddTooltip(button, () => $"Take on one more {UnitNames.Of(captured).ToLowerInvariant()}; it turns up in the yard with the rest");
                x += 52f;
            }
        }

        RectTransform _hireRow;

        Row AddRow(int index)
        {
            var row = new Row();
            var captured = index;
            var button = UiKit.NewButton(_panel, "", () => Clicked(captured));
            row.Rect = (RectTransform)button.transform;
            row.Background = button.GetComponent<Image>();
            UiKit.AddTooltip(button, () => captured < _crew.Units.Count ? _crew.Units[captured].Status + "   (double-click to go to it)" : "");

            var icon = UiKit.Place(UiKit.NewRect(row.Rect, "Role"), 6f, 4f, 32f, 32f).gameObject.AddComponent<Image>();
            icon.raycastTarget = false;
            row.Icon = icon;
            row.Name = UiKit.NewLabel(row.Rect, "", 44f, 2f, 110f, 18f, 14);
            row.State = UiKit.NewLabel(row.Rect, "", 44f, 20f, Width - 150f, 18f, 12);
            row.State.color = new Color(0.8f, 0.82f, 0.85f);

            // Letting one go, on the row of the one going: the alternative is a button somewhere
            // else that acts on "the selected unit", which is a button you can press by accident
            // with the wrong thing selected (2026-09-23).
            var dismiss = UiKit.NewButton(row.Rect, "×", () =>
            {
                if (captured < _crew.Units.Count)
                    _crew.Dismiss(captured);
            });
            UiKit.Place((RectTransform)dismiss.transform, Width - 40f, 8f, 24f, 22f);
            UiKit.AddTooltip(dismiss, () => captured < _crew.Units.Count
                ? $"Let {UnitNames.Of(_crew.Units[captured].Role).ToLowerInvariant()} go — anything it is carrying goes with it"
                : "");

            var loadBack = UiKit.Place(UiKit.NewRect(row.Rect, "Load"), Width - 136f, 8f, 88f, 10f);
            loadBack.gameObject.AddComponent<Image>().color = UiKit.FieldColor;
            row.Load = UiKit.NewRect(loadBack, "Fill");
            row.Load.anchorMin = Vector2.zero;
            row.Load.anchorMax = new Vector2(0f, 1f);
            row.Load.offsetMin = row.Load.offsetMax = Vector2.zero;
            row.LoadFill = row.Load.gameObject.AddComponent<Image>();
            row.LoadFill.color = UiKit.Accent;
            _rows.Add(row);
            return row;
        }

        static ToolIcon RoleIcon(UnitRole role) =>
            // The road machines are on the dumper's chassis until they have icons of their own.
            role == UnitRole.Hauler || role == UnitRole.Bulldozer || role == UnitRole.Paver ? ToolIcon.Hauler
            : role == UnitRole.Digger ? ToolIcon.Digger : ToolIcon.Worker;

        void Clicked(int index)
        {
            var add = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;
            _crew.Select(index, add);
            if (index == _lastClicked && Time.unscaledTime - _lastClickTime < DoubleClickSeconds && _camera != null)
                _camera.FocusOn(_crew.BodyPosition(index));
            _lastClicked = index;
            _lastClickTime = Time.unscaledTime;
        }

        void Update()
        {
            if (_crew == null || _panel == null)
                return;
            var units = _crew.Units;
            while (_rows.Count < units.Count)
                AddRow(_rows.Count);
            for (var i = units.Count; i < _rows.Count; i++)
                _rows[i].Rect.gameObject.SetActive(false);

            int stuck = 0, needMaterial = 0;
            foreach (var unit in units)
            {
                if (unit.State == CrewUnitState.NeedsSomewhereToTip)
                    stuck++;
                else if (unit.State == CrewUnitState.NeedsMaterial)
                    needMaterial++;
            }

            _banner.gameObject.SetActive(stuck > 0 || needMaterial > 0);
            if (stuck > 0)
                _bannerText.text = stuck == 1 ? "1 unit needs a Dump Zone or a Fill to tip into" : $"{stuck} units need a Dump Zone or a Fill to tip into";
            else if (needMaterial > 0)
                _bannerText.text = needMaterial == 1 ? "1 unit has nothing to fill with: mark a Quarry" : $"{needMaterial} units have nothing to fill with: mark a Quarry";

            var y = stuck > 0 || needMaterial > 0 ? 28f + BannerHeight + 4f : 28f;
            var counts = new Dictionary<UnitRole, int>();
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                var row = _rows[i];
                row.Rect.gameObject.SetActive(true);
                UiKit.Place(row.Rect, 4f, y, Width - 8f, RowHeight - 2f);
                y += RowHeight;

                // The role at a given row changes when a unit is let go, so the icon is set every
                // frame rather than once when the row is built: a row read "Dumper mech 1" beside
                // a robot's icon after a dismissal (2026-09-23).
                row.Icon.sprite = ToolIcons.Get(RoleIcon(unit.Role));

                counts.TryGetValue(unit.Role, out var n);
                counts[unit.Role] = ++n;
                row.Name.text = $"{UnitNames.Of(unit.Role)} {n}";
                row.State.text = Short(unit);
                row.State.color = unit.State == CrewUnitState.NeedsSomewhereToTip || unit.State == CrewUnitState.NeedsMaterial
                    ? UiKit.Warning : new Color(0.8f, 0.82f, 0.85f);
                var full = unit.Inventory.Capacity > 0f ? Mathf.Clamp01(unit.Inventory.Total / unit.Inventory.Capacity) : 0f;
                row.Load.anchorMax = new Vector2(full, 1f);
                row.Background.color = _crew.IsSelected(i) ? new Color(0.3f, 0.27f, 0.12f, 0.95f) : UiKit.ButtonColor;
            }

            UiKit.Place(_hireRow, 4f, y + 2f, Width - 8f, HireHeight);
            _panel.sizeDelta = new Vector2(Width, y + HireHeight + 8f);
        }

        /// <summary>The unit's state in a few words, without the detail its full status carries.</summary>
        static string Short(CrewUnit unit)
        {
            var status = unit.Status ?? unit.State.ToString();
            return status.Length > 34 ? status.Substring(0, 33) + "…" : status;
        }
    }
}
