using System.Collections.Generic;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The selection card, bottom left, shown only while something is selected (Ronan,
    /// 2026-09-24). One unit: what it is, what it is doing, its load and its worksite. Several: how
    /// many of each kind and a line each. The landing craft: its state and who is aboard. Under it
    /// the orders for everything selected: Worksite (a list of the worksites), Board the landing
    /// craft, Hold or Release, and Dismiss, which asks for a second click as the seed button does.
    /// </summary>
    public sealed class SelectionCardView : MonoBehaviour
    {
        const float Width = 340f;
        const float Pad = 8f;
        const float TitleHeight = 28f;
        const float LineHeight = 18f;
        const float ButtonHeight = 26f;
        const int MaxLines = 6;
        const float ConfirmSeconds = 2.5f;

        static readonly UnitRole[] Kinds = { UnitRole.Worker, UnitRole.Digger, UnitRole.Hauler, UnitRole.Bulldozer, UnitRole.Paver };

        PlayerTools _tools;
        CrewView _crew;
        RectTransform _card;
        Image _icon;
        Text _title;
        Text _subtitle;
        readonly List<Text> _lines = new List<Text>();
        RectTransform _loadBack;
        RectTransform _load;
        RectTransform _buttons;
        Button _worksite;
        Button _board;
        Button _hold;
        Text _holdText;
        Button _dismiss;
        Text _dismissText;
        RectTransform _sitePicker;
        readonly List<Button> _siteButtons = new List<Button>();
        float _dismissArmedUntil;
        string _boardRefusal;

        public void Build(Canvas canvas, PlayerTools tools, CrewView crew)
        {
            _tools = tools;
            _crew = crew;
            _card = UiKit.NewPanel(canvas.transform, "Selection Card", Vector2.zero, new Vector2(12f, 12f), new Vector2(Width, 100f));

            _icon = UiKit.Place(UiKit.NewRect(_card, "Icon"), Pad, 4f, 22f, 22f).gameObject.AddComponent<Image>();
            _icon.raycastTarget = false;
            _title = UiKit.NewLabel(_card, "", Pad + 28f, 0f, 190f, TitleHeight, 16);
            _subtitle = UiKit.NewLabel(_card, "", Width - 130f - Pad, 0f, 130f, TitleHeight, 12);
            _subtitle.alignment = TextAnchor.MiddleRight;
            _subtitle.color = new Color(0.8f, 0.82f, 0.85f);

            for (var i = 0; i < MaxLines + 1; i++)
            {
                var line = UiKit.NewLabel(_card, "", Pad, 0f, Width - 2 * Pad, LineHeight, 12);
                line.color = new Color(0.86f, 0.88f, 0.9f);
                _lines.Add(line);
            }

            _loadBack = UiKit.NewRect(_card, "Load");
            _loadBack.gameObject.AddComponent<Image>().color = UiKit.FieldColor;
            _load = UiKit.NewRect(_loadBack, "Fill");
            _load.anchorMin = Vector2.zero;
            _load.anchorMax = new Vector2(0f, 1f);
            _load.offsetMin = _load.offsetMax = Vector2.zero;
            _load.gameObject.AddComponent<Image>().color = UiKit.Accent;

            _buttons = UiKit.NewRect(_card, "Orders");
            var width = (Width - 2 * Pad - 3 * 4f) / 4f;
            _worksite = Order(0, width, "Worksite ▴", ToggleSitePicker,
                () => "Put them on a worksite: they work its area and nothing else");
            _board = Order(1, width, "Board", Board,
                () => _boardRefusal != null ? "Can't board: " + _boardRefusal : "Send them aboard the landing craft");
            _hold = Order(2, width, "Hold", HoldOrRelease,
                () => AnyHolding() ? "Let them go back to their own work" : "Stop where they are and take no work until released");
            _holdText = _hold.GetComponentInChildren<Text>();
            _dismiss = Order(3, width, "Dismiss", Dismiss,
                () => "Let them go: anything they are carrying goes with them (click twice)");
            _dismissText = _dismiss.GetComponentInChildren<Text>();

            _sitePicker = UiKit.NewPanel(_card, "Worksites", new Vector2(0f, 1f), Vector2.zero, new Vector2(160f, 40f));
            _sitePicker.gameObject.SetActive(false);
            _card.gameObject.SetActive(false);
        }

        Button Order(int slot, float width, string label, System.Action onClick, System.Func<string> tooltip)
        {
            var button = UiKit.NewButton(_buttons, label, onClick, null, 12);
            UiKit.Place((RectTransform)button.transform, slot * (width + 4f), 0f, width, ButtonHeight);
            UiKit.AddTooltip(button, tooltip);
            return button;
        }

        bool AnyHolding()
        {
            foreach (var unit in _crew.Selection)
                if (unit.Holding)
                    return true;
            return false;
        }

        void ToggleSitePicker()
        {
            var showing = !_sitePicker.gameObject.activeSelf;
            _sitePicker.gameObject.SetActive(showing);
            if (!showing)
                return;

            foreach (var button in _siteButtons)
                Destroy(button.gameObject);
            _siteButtons.Clear();
            var sites = _tools.Worksites != null ? _tools.Worksites.Sites : null;
            if (sites != null)
                foreach (var site in sites.All)
                    AddSiteButton(site.Name, site.Id);
            if (sites == null || sites.All.Count == 0)
            {
                // Said rather than an empty list: the Worksite tool is where they come from.
                var none = UiKit.NewButton(_sitePicker, "None yet: draw one", () => { }, null, 12);
                none.interactable = false;
                UiKit.AddTooltip(none, () => "Draw a worksite with the Worksite tool, then put them on it here");
                _siteButtons.Add(none);
            }

            AddSiteButton("No worksite", 0);

            var height = _siteButtons.Count * (ButtonHeight + 2f) + 6f;
            for (var i = 0; i < _siteButtons.Count; i++)
                UiKit.Place((RectTransform)_siteButtons[i].transform, 3f, 3f + i * (ButtonHeight + 2f), 154f, ButtonHeight);
            UiKit.Place(_sitePicker, 0f, -height - 4f, 160f, height);
        }

        void AddSiteButton(string label, int site)
        {
            var button = UiKit.NewButton(_sitePicker, label, () =>
            {
                var told = _crew.Assign(site);
                _tools.Say(site == 0 ? $"{told} taken off their worksite" : $"{told} put on Worksite {site}");
                _sitePicker.gameObject.SetActive(false);
            }, null, 12);
            _siteButtons.Add(button);
        }

        void Board()
        {
            var craft = _tools.Ferries != null ? _tools.Ferries.Craft : null;
            if (craft == null)
                return;
            var went = _crew.BoardSelected(craft, out var why);
            _tools.Say(went > 0 ? $"{went} going aboard the landing craft" + (why != null ? $"; the rest: {why}" : "")
                : "Can't board: " + why);
        }

        void HoldOrRelease()
        {
            if (AnyHolding())
                _tools.Say($"{_crew.ReleaseSelected()} back to work");
            else
                _tools.Say($"{_crew.HoldSelected()} holding");
        }

        void Dismiss()
        {
            if (Time.unscaledTime < _dismissArmedUntil)
            {
                _dismissArmedUntil = 0f;
                _tools.Say($"{_crew.DismissSelected()} dismissed");
                return;
            }

            _dismissArmedUntil = Time.unscaledTime + ConfirmSeconds;
        }

        /// <summary>Why the selection can't board now, or null when it can.</summary>
        string BoardRefusal(Ferry craft)
        {
            if (craft == null)
                return "there is no landing craft";
            if (craft.State != FerryState.RampDown)
                return "its ramp is not down";
            var free = 0;
            foreach (var lane in craft.Lanes)
                if (lane < 0)
                    free++;
            return free == 0 ? "it is full" : null;
        }

        void Update()
        {
            if (_crew == null || _card == null)
                return;
            var ferries = _tools.Ferries;
            var units = _crew.SelectedCount;
            var craftSelected = units == 0 && ferries != null && ferries.Selected && ferries.Craft != null;
            _card.gameObject.SetActive(units > 0 || craftSelected);
            if (units == 0)
            {
                _sitePicker.gameObject.SetActive(false);
                _dismissArmedUntil = 0f;
            }

            if (units > 0)
                ShowUnits();
            else if (craftSelected)
                ShowCraft(ferries.Craft);
        }

        void ShowUnits()
        {
            var selection = new List<CrewUnit>(_crew.Selection);
            var y = TitleHeight + 2f;
            var shown = 0;
            if (selection.Count == 1)
            {
                var unit = selection[0];
                _icon.sprite = ToolIcons.Get(CrewPanelView.RoleIcon(unit.Role));
                _title.text = $"{UnitNames.Of(unit.Role)} {Number(unit)}";
                _subtitle.text = unit.Holding ? "holding" : unit.Site != 0 ? $"Worksite {unit.Site}" : "no worksite";
                Line(shown++, ref y, unit.Status ?? unit.State.ToString(), Colour(unit));
                var full = unit.Inventory.Capacity > 0f ? Mathf.Clamp01(unit.Inventory.Total / unit.Inventory.Capacity) : 0f;
                _loadBack.gameObject.SetActive(unit.Inventory.Capacity > 0f);
                if (unit.Inventory.Capacity > 0f)
                {
                    UiKit.Place(_loadBack, Pad, y + 4f, Width - 2 * Pad, 8f);
                    _load.anchorMax = new Vector2(full, 1f);
                    y += 16f;
                }
            }
            else
            {
                _loadBack.gameObject.SetActive(false);
                _icon.sprite = ToolIcons.Get(ToolIcon.Worker);
                _title.text = $"{selection.Count} units";
                var kinds = new List<string>();
                foreach (var kind in Kinds)
                {
                    var count = 0;
                    foreach (var unit in selection)
                        if (unit.Role == kind)
                            count++;
                    if (count > 0)
                        kinds.Add($"{count} {UnitNames.Short(kind)}");
                }

                _subtitle.text = "";
                Line(shown++, ref y, string.Join(", ", kinds), new Color(0.8f, 0.82f, 0.85f));
                for (var i = 0; i < selection.Count && shown < MaxLines; i++)
                {
                    var unit = selection[i];
                    var text = $"{UnitNames.Short(unit.Role)} {Number(unit)}: {unit.Status ?? unit.State.ToString()}";
                    if (shown == MaxLines - 1 && selection.Count - i > 1)
                        text = $"+{selection.Count - i} more";
                    Line(shown++, ref y, text, Colour(unit));
                }
            }

            for (var i = shown; i < _lines.Count; i++)
                _lines[i].gameObject.SetActive(false);

            _buttons.gameObject.SetActive(true);
            var craft = _tools.Ferries != null ? _tools.Ferries.Craft : null;
            _boardRefusal = BoardRefusal(craft);
            _board.gameObject.SetActive(craft != null);
            _board.interactable = _boardRefusal == null;
            _holdText.text = AnyHolding() ? "Release" : "Hold";
            _dismissText.text = Time.unscaledTime < _dismissArmedUntil ? "Sure?" : "Dismiss";
            _dismissText.color = Time.unscaledTime < _dismissArmedUntil ? UiKit.Warning : Color.white;
            UiKit.Place(_buttons, Pad, y + 6f, Width - 2 * Pad, ButtonHeight);
            y += ButtonHeight + 6f + Pad;
            _card.sizeDelta = new Vector2(Width, y);
        }

        void ShowCraft(Ferry craft)
        {
            _icon.sprite = ToolIcons.Get(ToolIcon.Hauler);
            _title.text = "Landing craft";
            _subtitle.text = "";
            _loadBack.gameObject.SetActive(false);
            _buttons.gameObject.SetActive(false);
            _sitePicker.gameObject.SetActive(false);

            var y = TitleHeight + 2f;
            var shown = 0;
            Line(shown++, ref y, craft.Status, new Color(0.86f, 0.88f, 0.9f));
            var aboard = new List<string>();
            var free = 0;
            foreach (var lane in craft.Lanes)
            {
                if (lane < 0)
                {
                    free++;
                    continue;
                }

                foreach (var unit in _crew.Units)
                    if (unit.Id == lane)
                        aboard.Add($"{UnitNames.Short(unit.Role)} {Number(unit)}");
            }

            Line(shown++, ref y, aboard.Count > 0 ? "Aboard: " + string.Join(", ", aboard) : "Empty", new Color(0.8f, 0.82f, 0.85f));
            Line(shown++, ref y, free == 1 ? "1 lane free" : $"{free} lanes free", new Color(0.8f, 0.82f, 0.85f));
            Line(shown++, ref y, "Right-click a shore to send it there", new Color(0.7f, 0.72f, 0.75f));
            for (var i = shown; i < _lines.Count; i++)
                _lines[i].gameObject.SetActive(false);
            _card.sizeDelta = new Vector2(Width, y + Pad);
        }

        void Line(int index, ref float y, string text, Color colour)
        {
            var line = _lines[index];
            line.gameObject.SetActive(true);
            line.text = text.Length > 52 ? text.Substring(0, 51) + "…" : text;
            line.color = colour;
            UiKit.Place((RectTransform)line.transform, Pad, y, Width - 2 * Pad, LineHeight);
            y += LineHeight;
        }

        /// <summary>The unit's number among its kind, as the units menu counts them.</summary>
        int Number(CrewUnit unit)
        {
            var number = 0;
            foreach (var other in _crew.Units)
            {
                if (other.Role == unit.Role)
                    number++;
                if (other == unit)
                    return number;
            }

            return number;
        }

        static Color Colour(CrewUnit unit) =>
            unit.State == CrewUnitState.NeedsSomewhereToTip || unit.State == CrewUnitState.NeedsMaterial
                ? UiKit.Warning : new Color(0.86f, 0.88f, 0.9f);
    }
}
