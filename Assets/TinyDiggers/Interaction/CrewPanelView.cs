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
        const float DoubleClickSeconds = 0.35f;

        sealed class Row
        {
            public RectTransform Rect;
            public Image Background;
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
        }

        Row AddRow(int index)
        {
            var row = new Row();
            var captured = index;
            var button = UiKit.NewButton(_panel, "", () => Clicked(captured));
            row.Rect = (RectTransform)button.transform;
            row.Background = button.GetComponent<Image>();
            UiKit.AddTooltip(button, () => captured < _crew.Units.Count ? _crew.Units[captured].Status + "   (double-click to go to it)" : "");

            var icon = UiKit.Place(UiKit.NewRect(row.Rect, "Role"), 6f, 4f, 32f, 32f).gameObject.AddComponent<Image>();
            icon.sprite = ToolIcons.Get(RoleIcon(_crew.Units[index].Role));
            icon.raycastTarget = false;
            row.Name = UiKit.NewLabel(row.Rect, "", 44f, 2f, 110f, 18f, 14);
            row.State = UiKit.NewLabel(row.Rect, "", 44f, 20f, Width - 150f, 18f, 12);
            row.State.color = new Color(0.8f, 0.82f, 0.85f);

            var loadBack = UiKit.Place(UiKit.NewRect(row.Rect, "Load"), Width - 100f, 8f, 88f, 10f);
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
            role == UnitRole.Hauler ? ToolIcon.Hauler : role == UnitRole.Digger ? ToolIcon.Digger : ToolIcon.Worker;

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

            var stuck = 0;
            foreach (var unit in units)
                if (unit.State == CrewUnitState.NeedsSomewhereToTip)
                    stuck++;
            _banner.gameObject.SetActive(stuck > 0);
            if (stuck > 0)
                _bannerText.text = stuck == 1 ? "1 unit needs a Dump Zone or a Fill to tip into" : $"{stuck} units need a Dump Zone or a Fill to tip into";

            var y = stuck > 0 ? 28f + BannerHeight + 4f : 28f;
            var counts = new Dictionary<UnitRole, int>();
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                var row = _rows[i];
                row.Rect.gameObject.SetActive(true);
                UiKit.Place(row.Rect, 4f, y, Width - 8f, RowHeight - 2f);
                y += RowHeight;

                counts.TryGetValue(unit.Role, out var n);
                counts[unit.Role] = ++n;
                row.Name.text = $"{unit.Role} {n}";
                row.State.text = Short(unit);
                row.State.color = unit.State == CrewUnitState.NeedsSomewhereToTip ? UiKit.Warning : new Color(0.8f, 0.82f, 0.85f);
                var full = unit.Inventory.Capacity > 0f ? Mathf.Clamp01(unit.Inventory.Total / unit.Inventory.Capacity) : 0f;
                row.Load.anchorMax = new Vector2(full, 1f);
                row.Background.color = _crew.IsSelected(i) ? new Color(0.3f, 0.27f, 0.12f, 0.95f) : UiKit.ButtonColor;
            }

            _panel.sizeDelta = new Vector2(Width, y + 6f);
        }

        /// <summary>The unit's state in a few words, without the detail its full status carries.</summary>
        static string Short(CrewUnit unit)
        {
            var status = unit.Status ?? unit.State.ToString();
            return status.Length > 34 ? status.Substring(0, 33) + "…" : status;
        }
    }
}
