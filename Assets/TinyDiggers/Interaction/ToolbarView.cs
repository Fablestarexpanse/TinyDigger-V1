using System;
using System.Collections.Generic;
using System.Text;
using TinyDiggers.Presentation;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The toolbar along the bottom of the screen. Five slots, left to right (Ronan, 2026-09-24:
    /// "re-evaluate our UI and menus"): Select | Terrain (Dig, Fill, Level, Terraform) | Zones (Dump
    /// Zone, Quarry, Worksite) | Road | Clear. A group's button shows the tool last used in it and
    /// opens a flyout of its tools, each with its name and hotkey; the hotkeys work as before. The
    /// island's seed, the settings (F2) and the debug readout (F3) moved to a menu top right, since
    /// they are setup, not play. Above the bar, the active tool's panel (<see cref="ToolPanelView"/>)
    /// and a status line saying what the last action did; top left, the units menu
    /// (<see cref="CrewPanelView"/>); bottom left, the selection card (<see cref="SelectionCardView"/>).
    ///
    /// Built in code into one Canvas, so there is nothing to wire up in the scene.
    /// </summary>
    public sealed class ToolbarView : MonoBehaviour
    {
        [SerializeField] PlayerTools _tools;
        [SerializeField] CrewView _crew;

        [Tooltip("The land, for the seed and Regenerate.")]
        [SerializeField] TerrainView _terrain;
        [SerializeField] Color _warningColor = new Color(1f, 0.55f, 0.2f);

        const float ButtonSize = 58f;
        const float GroupGap = 16f;
        const float FlyoutWidth = 210f;
        const float FlyoutRow = 44f;
        const float MenuButton = 44f;

        struct ToolButton
        {
            public ToolMode Mode;
            public ToolIcon Icon;
            public string Name;
            public string Key;
            public string Help;
        }

        /// <summary>A slot on the bar: one tool, or a group whose tools open in a flyout.</summary>
        struct Slot
        {
            public string Name;
            public ToolButton[] Tools;
            public bool Group => Tools.Length > 1;
        }

        static readonly Slot[] Slots =
        {
            new Slot { Name = "Select", Tools = new[] { new ToolButton { Mode = ToolMode.Select, Icon = ToolIcon.Select, Name = "Select",
                Key = "1", Help = "click or drag over units; right-click to send them" } } },
            new Slot
            {
                Name = "Terrain", Tools = new[]
                {
                    new ToolButton { Mode = ToolMode.Dig, Icon = ToolIcon.Dig, Name = "Dig", Key = "2", Help = "paint ground to be dug down to H" },
                    new ToolButton { Mode = ToolMode.Fill, Icon = ToolIcon.Fill, Name = "Fill", Key = "3", Help = "paint ground to be filled up to H" },
                    new ToolButton { Mode = ToolMode.Level, Icon = ToolIcon.Level, Name = "Level", Key = "5", Help = "drag a pad to be levelled to H" },
                    new ToolButton { Mode = ToolMode.Terraform, Icon = ToolIcon.Terraform, Name = "Terraform", Key = "9",
                        Help = "draw the shape you want the ground to be; the crew build it" },
                },
            },
            new Slot
            {
                Name = "Zones", Tools = new[]
                {
                    new ToolButton { Mode = ToolMode.DumpZone, Icon = ToolIcon.DumpZone, Name = "Dump Zone", Key = "4",
                        Help = "drag where spoil may be tipped" },
                    new ToolButton { Mode = ToolMode.Quarry, Icon = ToolIcon.Quarry, Name = "Quarry", Key = "8",
                        Help = "drag where the crew may dig for material to fill with" },
                    // The digger's glyph until the worksite building has an icon of its own.
                    new ToolButton { Mode = ToolMode.Worksite, Icon = ToolIcon.Digger, Name = "Worksite", Key = "0",
                        Help = "click round a work area (any shape) or drag a rectangle; assign units to it from the selection card" },
                },
            },
            new Slot { Name = "Road", Tools = new[] { new ToolButton { Mode = ToolMode.Road, Icon = ToolIcon.Road, Name = "Road", Key = "6",
                Help = "click points, double-click or Enter to lay it" } } },
            new Slot { Name = "Clear", Tools = new[] { new ToolButton { Mode = ToolMode.Clear, Icon = ToolIcon.Clear, Name = "Clear", Key = "7",
                Help = "drag to take designations off" } } },
        };

        sealed class SlotView
        {
            public Slot Slot;
            public int Current;
            public RectTransform Rect;
            public Image Background;
            public Image Icon;
            public RectTransform Flyout;
            public readonly List<(ToolMode Mode, Image Background, Image Icon, Text Label)> Rows = new List<(ToolMode, Image, Image, Text)>();
        }

        readonly List<SlotView> _slots = new List<SlotView>();
        RectTransform _menuButton;
        RectTransform _menu;
        Text _newIslandText;
        readonly StringBuilder _text = new StringBuilder(256);

        Canvas _canvas;
        Text _status;
        string _shown = "";
        int _dugVersion = -1;
        string _dug = string.Empty;
        float _seedArmedUntil;
        TerrainDebugReadout _debug;

        RectTransform _settingsPanel;
        InputField _seedField;
        Toggle _markers;
        Text _settingsSummary;

        /// <summary>The canvas the toolbar and its panels are drawn on.</summary>
        public Canvas Canvas => _canvas;

        void Start()
        {
            _canvas = UiKit.NewCanvas(transform, "Toolbar Canvas", 100);
            _debug = FindAnyObjectByType<TerrainDebugReadout>();

            // The bar: five slots, a gap between each.
            var width = 16f + Slots.Length * (ButtonSize + GroupGap) - GroupGap;
            var bar = UiKit.NewPanel(_canvas.transform, "Bar", new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(width, ButtonSize + 12f));
            var x = 8f;
            foreach (var slot in Slots)
            {
                var view = new SlotView { Slot = slot };
                var button = UiKit.NewButton(bar, "", () => SlotClicked(view), ToolIcons.Get(slot.Tools[0].Icon));
                view.Rect = (RectTransform)button.transform;
                UiKit.Place(view.Rect, x, 6f, ButtonSize, ButtonSize);
                view.Background = button.GetComponent<Image>();
                view.Icon = button.transform.Find("Icon").GetComponent<Image>();
                if (slot.Group)
                {
                    // A corner mark says there is more in it.
                    var mark = UiKit.NewText(button.transform, "▴", 11, TextAnchor.UpperRight);
                    mark.rectTransform.offsetMax = new Vector2(-3f, -1f);
                    mark.raycastTarget = false;
                    var captured = slot;
                    UiKit.AddTooltip(button, () => $"{captured.Name}: {Names(captured)}");
                }
                else
                {
                    var tool = slot.Tools[0];
                    UiKit.AddTooltip(button, () => $"{tool.Name}  ({tool.Key}): {tool.Help}");
                }

                _slots.Add(view);
                x += ButtonSize + GroupGap;
            }

            BuildMenu(_canvas.transform);

            var status = UiKit.NewPanel(_canvas.transform, "Status", new Vector2(0.5f, 0f), new Vector2(0f, ButtonSize + 28f),
                new Vector2(Mathf.Max(width, 760f), 26f), new Color(0.08f, 0.09f, 0.1f, 0.55f));
            _status = UiKit.NewText(status, "", 15, TextAnchor.MiddleCenter);

            BuildSettingsPanel(_canvas.transform);

            var panel = gameObject.AddComponent<ToolPanelView>();
            panel.Build(_canvas, _tools, new Vector2(0f, ButtonSize + 60f));
            var crewPanel = gameObject.AddComponent<CrewPanelView>();
            crewPanel.Build(_canvas, _crew, FindAnyObjectByType<RtsCamera>());
            gameObject.AddComponent<SelectionCardView>().Build(_canvas, _tools, _crew);

            // The flyouts go on last, so they open over the tool panel and the status line.
            foreach (var view in _slots)
                if (view.Slot.Group)
                    BuildFlyout(view);

            Tooltip.Create(_canvas);
        }

        static string Names(Slot slot)
        {
            var names = new List<string>();
            foreach (var tool in slot.Tools)
                names.Add($"{tool.Name} ({tool.Key})");
            return string.Join(", ", names);
        }

        void SlotClicked(SlotView view)
        {
            if (!view.Slot.Group)
            {
                CloseFlyouts();
                _menu.gameObject.SetActive(false);
                _tools.SetMode(view.Slot.Tools[0].Mode);
                return;
            }

            var open = !view.Flyout.gameObject.activeSelf;
            CloseFlyouts();
            _menu.gameObject.SetActive(false);
            view.Flyout.gameObject.SetActive(open);
        }

        /// <summary>A group's tools in a column above its button: icon, name and hotkey each.</summary>
        void BuildFlyout(SlotView view)
        {
            var tools = view.Slot.Tools;
            var height = tools.Length * (FlyoutRow + 2f) + 6f;
            // Over its button, left edges lined up.
            var bar = (RectTransform)view.Rect.parent;
            var left = -bar.sizeDelta.x * 0.5f + view.Rect.anchoredPosition.x;
            view.Flyout = UiKit.NewPanel(_canvas.transform, view.Slot.Name + " Tools", new Vector2(0.5f, 0f),
                new Vector2(left, 10f + ButtonSize + 16f), new Vector2(FlyoutWidth, height));
            view.Flyout.pivot = Vector2.zero;

            for (var i = 0; i < tools.Length; i++)
            {
                var tool = tools[i];
                var index = i;
                var button = UiKit.NewButton(view.Flyout, "", () =>
                {
                    view.Current = index;
                    _tools.SetMode(tool.Mode);
                    view.Flyout.gameObject.SetActive(false);
                });
                var rect = (RectTransform)button.transform;
                UiKit.Place(rect, 3f, 3f + i * (FlyoutRow + 2f), FlyoutWidth - 6f, FlyoutRow);
                var icon = UiKit.Place(UiKit.NewRect(rect, "Icon"), 6f, 5f, FlyoutRow - 10f, FlyoutRow - 10f).gameObject.AddComponent<Image>();
                icon.sprite = ToolIcons.Get(tool.Icon);
                icon.raycastTarget = false;
                var label = UiKit.NewLabel(rect, tool.Name, FlyoutRow + 2f, 0f, 120f, FlyoutRow, 15);
                var key = UiKit.NewLabel(rect, tool.Key, FlyoutWidth - 40f, 0f, 28f, FlyoutRow, 13);
                key.alignment = TextAnchor.MiddleRight;
                key.color = new Color(0.7f, 0.72f, 0.75f);
                UiKit.AddTooltip(button, () => $"{tool.Name}  ({tool.Key}): {tool.Help}");
                view.Rows.Add((tool.Mode, button.GetComponent<Image>(), icon, label));
            }

            view.Flyout.gameObject.SetActive(false);
        }

        void CloseFlyouts()
        {
            foreach (var view in _slots)
                if (view.Flyout != null)
                    view.Flyout.gameObject.SetActive(false);
        }

        /// <summary>
        /// The menu top right: a new island (click twice), the island settings (F2) and the debug
        /// readout (F3). Setup, not play, so off the bar.
        /// </summary>
        void BuildMenu(Transform parent)
        {
            var button = UiKit.NewButton((RectTransform)parent, "", () =>
            {
                CloseFlyouts();
                _menu.gameObject.SetActive(!_menu.gameObject.activeSelf);
            }, ToolIcons.Get(ToolIcon.Settings));
            _menuButton = (RectTransform)button.transform;
            _menuButton.anchorMin = _menuButton.anchorMax = _menuButton.pivot = new Vector2(1f, 1f);
            _menuButton.anchoredPosition = new Vector2(-12f, -12f);
            _menuButton.sizeDelta = new Vector2(MenuButton, MenuButton);
            UiKit.AddTooltip(button, () => "Menu: new island, island settings (F2), debug readout (F3)");

            const float rowWidth = 240f;
            _menu = UiKit.NewPanel(parent, "Menu", new Vector2(1f, 1f), new Vector2(-12f, -12f - MenuButton - 4f),
                new Vector2(rowWidth, 3 * (FlyoutRow + 2f) + 4f));
            _newIslandText = MenuRow(0, rowWidth, ToolIcon.Seed, "New island", ArmOrRegenerate,
                () => Time.unscaledTime < _seedArmedUntil
                    ? "Click again to make a new island (the one you have is lost)"
                    : $"New island: seed {(_terrain != null ? _terrain.Seed + 1 : 0)} (click twice)");
            MenuRow(1, rowWidth, ToolIcon.Settings, "Island settings   F2", () =>
            {
                _menu.gameObject.SetActive(false);
                ToggleSettings();
            }, () => "The island's seed, Regenerate, and the map markers");
            MenuRow(2, rowWidth, ToolIcon.Debug, "Debug readout   F3", () =>
            {
                _menu.gameObject.SetActive(false);
                ToggleDebug();
            }, () => "The terrain and crew readout");
            _menu.gameObject.SetActive(false);
        }

        Text MenuRow(int index, float width, ToolIcon glyph, string label, Action onClick, Func<string> tooltip)
        {
            var button = UiKit.NewButton(_menu, "", onClick);
            var rect = (RectTransform)button.transform;
            UiKit.Place(rect, 3f, 3f + index * (FlyoutRow + 2f), width - 6f, FlyoutRow);
            var icon = UiKit.Place(UiKit.NewRect(rect, "Icon"), 6f, 5f, FlyoutRow - 10f, FlyoutRow - 10f).gameObject.AddComponent<Image>();
            icon.sprite = ToolIcons.Get(glyph);
            icon.raycastTarget = false;
            var text = UiKit.NewLabel(rect, label, FlyoutRow + 2f, 0f, width - FlyoutRow - 10f, FlyoutRow, 15);
            UiKit.AddTooltip(button, tooltip);
            return text;
        }

        /// <summary>A click anywhere but on an open flyout, the menu or their buttons closes them.</summary>
        void CloseOnClickElsewhere()
        {
            var mouse = Mouse.current;
            if (mouse == null || !(mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                return;
            var at = mouse.position.ReadValue();
            bool Over(RectTransform rect) => rect != null && rect.gameObject.activeInHierarchy
                                             && RectTransformUtility.RectangleContainsScreenPoint(rect, at, null);
            foreach (var view in _slots)
                if (view.Flyout != null && view.Flyout.gameObject.activeSelf && !Over(view.Flyout) && !Over(view.Rect))
                    view.Flyout.gameObject.SetActive(false);
            if (_menu.gameObject.activeSelf && !Over(_menu) && !Over(_menuButton))
                _menu.gameObject.SetActive(false);
        }

        void ArmOrRegenerate()
        {
            if (Time.unscaledTime < _seedArmedUntil)
            {
                _seedArmedUntil = 0f;
                if (_terrain != null)
                    _terrain.Regenerate(_terrain.Seed + 1);
                RefreshSettings();
                return;
            }

            _seedArmedUntil = Time.unscaledTime + 2.5f;
        }

        void ToggleSettings()
        {
            var showing = !_settingsPanel.gameObject.activeSelf;
            _settingsPanel.gameObject.SetActive(showing);
            if (showing)
                RefreshSettings();
        }

        void ToggleDebug()
        {
            if (_debug != null)
                _debug.Visible = !_debug.Visible;
        }

        /// <summary>
        /// The F2 panel: the seed the island came from, and a button to make another one. Hidden
        /// until F2 is pressed, because it is a setup tool rather than something used while playing.
        /// </summary>
        void BuildSettingsPanel(Transform parent)
        {
            // Under the menu button, which has the corner now.
            _settingsPanel = UiKit.NewPanel(parent, "Settings", new Vector2(1f, 1f), new Vector2(-12f, -12f - MenuButton - 4f), new Vector2(320f, 162f));
            var title = UiKit.NewText(_settingsPanel, "Island  (F2)", 16, TextAnchor.UpperLeft);
            title.rectTransform.offsetMax = new Vector2(-6f, -6f);

            _seedField = UiKit.NewNumberField(_settingsPanel, null);
            _seedField.contentType = InputField.ContentType.IntegerNumber;
            UiKit.Place((RectTransform)_seedField.transform, 10f, 34f, 180f, 28f);

            var regenerate = UiKit.NewButton(_settingsPanel, "Regenerate", Regenerate);
            regenerate.GetComponent<Image>().color = UiKit.Accent;
            regenerate.GetComponentInChildren<Text>().color = Color.black;
            UiKit.Place((RectTransform)regenerate.transform, 198f, 34f, 112f, 28f);

            // What the map draws over the ground, so a finished cut can be looked at.
            _markers = UiKit.NewToggle(_settingsPanel, "Show markers  (M)", true, on => _tools.ShowMarkers = on);
            UiKit.Place((RectTransform)_markers.transform, 10f, 70f, 300f, 24f);
            UiKit.AddTooltip(_markers, () => "The dig, fill, dump zone and quarry sheets. They lie exactly where the work is, so hide them to see what the crew built");

            _settingsSummary = UiKit.NewText(_settingsPanel, "", 14, TextAnchor.LowerLeft);
            _settingsSummary.rectTransform.offsetMin = new Vector2(10f, 8f);
            _settingsSummary.rectTransform.offsetMax = new Vector2(-10f, -70f);

            _settingsPanel.gameObject.SetActive(false);
        }

        /// <summary>Builds the island again from whatever seed is in the box.</summary>
        void Regenerate()
        {
            if (_terrain == null)
                return;
            if (!int.TryParse(_seedField.text, out var seed))
                seed = _terrain.Seed + 1;
            _terrain.Regenerate(seed);
            RefreshSettings();
        }

        void RefreshSettings()
        {
            if (_terrain == null || _settingsSummary == null)
                return;
            _seedField.text = _terrain.Seed.ToString();
            var island = _terrain.Island;
            _settingsSummary.text = island == null
                ? "The old plateau generator is in use; there is no island."
                : $"Seed {_terrain.Seed}   peak ({island.Peak.x}, {island.Peak.y})   {island.Channels.Count} rivers and creeks";
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame && _settingsPanel != null)
                ToggleSettings();

            if (_tools == null || _status == null)
                return;

            // The M key changes it too, so the box follows the switch rather than owning it.
            if (_markers != null)
                _markers.SetIsOnWithoutNotify(_tools.ShowMarkers);

            CloseOnClickElsewhere();
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                CloseFlyouts();
                _menu.gameObject.SetActive(false);
            }

            // A dark glyph on the lit button: white on amber barely shows.
            var dark = new Color(0.1f, 0.1f, 0.12f);
            foreach (var view in _slots)
            {
                var active = false;
                for (var i = 0; i < view.Slot.Tools.Length; i++)
                    if (view.Slot.Tools[i].Mode == _tools.Mode)
                    {
                        // A hotkey picks from the group too, so the button follows it.
                        view.Current = i;
                        active = true;
                    }

                view.Icon.sprite = ToolIcons.Get(view.Slot.Tools[view.Current].Icon);
                view.Background.color = active ? UiKit.Accent : UiKit.ButtonColor;
                view.Icon.color = active ? dark : Color.white;
                foreach (var (mode, background, icon, label) in view.Rows)
                {
                    background.color = mode == _tools.Mode ? UiKit.Accent : UiKit.ButtonColor;
                    icon.color = label.color = mode == _tools.Mode ? dark : Color.white;
                }
            }

            _newIslandText.text = Time.unscaledTime < _seedArmedUntil ? "Click again: new island" : "New island";
            _newIslandText.color = Time.unscaledTime < _seedArmedUntil ? UiKit.Warning : Color.white;

            var map = _tools.Map;
            _text.Clear();
            if (_tools.LastAction.Length > 0)
                _text.Append(_tools.LastAction).Append("     ");
            _text.Append("designations ").Append(map.Count);
            if (map.DumpZoneCount > 0)
                _text.Append(", zone ").Append(map.DumpZoneCount);

            if (_crew != null && _crew.Dispatcher != null)
            {
                var ledger = _crew.Dispatcher.Ledger;
                if (ledger.Version != _dugVersion)
                {
                    _dugVersion = ledger.Version;
                    _dug = ledger.OreSummary(_crew.Dispatcher.Designations.Grid.Materials);
                }

                if (_dug.Length > 0)
                    _text.Append("     ").Append(_dug);
            }

            var line = _text.ToString();
            if (line == _shown)
                return;
            _shown = line;
            _status.text = line;
            _status.color = _tools.RoadTooSteep ? _warningColor : Color.white;
        }
    }
}
