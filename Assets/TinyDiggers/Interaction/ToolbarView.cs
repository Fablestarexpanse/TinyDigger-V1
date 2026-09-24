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
    /// The toolbar along the bottom of the screen (Slice 17: made for the mouse). Icon buttons in
    /// groups, left to right: Select | Dig Fill Level | Road | Dump Zone | Clear | Seed, Settings
    /// (F2), Debug (F3) on the far right. Hovering a button for <see cref="Tooltip.Delay"/> shows
    /// what it is and its hotkey; the active tool is lit. Above the bar, the active tool's panel
    /// (<see cref="ToolPanelView"/>) and a status line saying what the last action did; top left,
    /// the crew (<see cref="CrewPanelView"/>).
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

        struct ToolButton
        {
            public ToolMode Mode;
            public ToolIcon Icon;
            public string Name;
            public string Key;
            public string Help;
        }

        static readonly ToolButton[][] Groups =
        {
            new[] { new ToolButton { Mode = ToolMode.Select, Icon = ToolIcon.Select, Name = "Select", Key = "1",
                Help = "click or drag over units; right-click to send them" } },
            new[]
            {
                new ToolButton { Mode = ToolMode.Dig, Icon = ToolIcon.Dig, Name = "Dig", Key = "2", Help = "paint ground to be dug down to H" },
                new ToolButton { Mode = ToolMode.Fill, Icon = ToolIcon.Fill, Name = "Fill", Key = "3", Help = "paint ground to be filled up to H" },
                new ToolButton { Mode = ToolMode.Level, Icon = ToolIcon.Level, Name = "Level", Key = "5", Help = "drag a pad to be levelled to H" },
            },
            new[]
            {
                new ToolButton { Mode = ToolMode.Road, Icon = ToolIcon.Road, Name = "Road", Key = "6",
                    Help = "click points, double-click or Enter to lay it" },
                new ToolButton { Mode = ToolMode.Terraform, Icon = ToolIcon.Terraform, Name = "Terraform", Key = "9",
                    Help = "draw the shape you want the ground to be; the crew build it" },
            },
            new[]
            {
                new ToolButton { Mode = ToolMode.DumpZone, Icon = ToolIcon.DumpZone, Name = "Dump Zone", Key = "4",
                    Help = "drag where spoil may be tipped" },
                new ToolButton { Mode = ToolMode.Quarry, Icon = ToolIcon.Quarry, Name = "Quarry", Key = "8",
                    Help = "drag where the crew may dig for material to fill with" },
            },
            new[] { new ToolButton { Mode = ToolMode.Clear, Icon = ToolIcon.Clear, Name = "Clear", Key = "7",
                Help = "drag to take designations off" } },
            // The digger's glyph until the worksite building has an icon of its own.
            new[] { new ToolButton { Mode = ToolMode.Worksite, Icon = ToolIcon.Digger, Name = "Worksite", Key = "0",
                Help = "click round a work area (any shape) or drag a rectangle; right-click it with units selected to assign them" } },
        };

        readonly List<(ToolMode Mode, Image Image, Image Icon)> _toolImages = new List<(ToolMode, Image, Image)>();
        readonly StringBuilder _text = new StringBuilder(256);

        Canvas _canvas;
        Text _status;
        string _shown = "";
        int _dugVersion = -1;
        string _dug = string.Empty;
        float _seedArmedUntil;
        Text _seedLabel;
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

            // Work out the bar's width: the buttons, the gaps between groups, and the right-hand group.
            var toolCount = 0;
            foreach (var group in Groups)
                toolCount += group.Length;
            var width = 12f + toolCount * (ButtonSize + 4f) + Groups.Length * GroupGap + GroupGap * 2f + 3 * (ButtonSize + 4f);
            var bar = UiKit.NewPanel(_canvas.transform, "Bar", new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(width, ButtonSize + 12f));

            var x = 8f;
            foreach (var group in Groups)
            {
                foreach (var tool in group)
                {
                    var captured = tool;
                    var button = UiKit.NewButton(bar, "", () => _tools.SetMode(captured.Mode), ToolIcons.Get(tool.Icon));
                    UiKit.Place((RectTransform)button.transform, x, 6f, ButtonSize, ButtonSize);
                    UiKit.AddTooltip(button, () => $"{captured.Name}  ({captured.Key}): {captured.Help}");
                    _toolImages.Add((tool.Mode, button.GetComponent<Image>(), button.transform.Find("Icon").GetComponent<Image>()));
                    x += ButtonSize + 4f;
                }

                x += GroupGap;
            }

            // The spacer, then the island and the debug readout, pinned to the right.
            x = width - 8f - 3 * (ButtonSize + 4f);
            var seed = UiKit.NewButton(bar, "", ArmOrRegenerate, ToolIcons.Get(ToolIcon.Seed));
            UiKit.Place((RectTransform)seed.transform, x, 6f, ButtonSize, ButtonSize);
            UiKit.AddTooltip(seed, () => Time.unscaledTime < _seedArmedUntil
                ? "Click again to make a new island (the one you have is lost)"
                : $"New island: seed {(_terrain != null ? _terrain.Seed + 1 : 0)} (click twice)");
            _seedLabel = UiKit.NewText(seed.transform, "", 12, TextAnchor.LowerCenter, UiKit.Warning);
            x += ButtonSize + 4f;

            var settings = UiKit.NewButton(bar, "", ToggleSettings, ToolIcons.Get(ToolIcon.Settings));
            UiKit.Place((RectTransform)settings.transform, x, 6f, ButtonSize, ButtonSize);
            UiKit.AddTooltip(settings, () => "Settings  (F2): the island's seed and Regenerate");
            x += ButtonSize + 4f;

            var debug = UiKit.NewButton(bar, "", ToggleDebug, ToolIcons.Get(ToolIcon.Debug));
            UiKit.Place((RectTransform)debug.transform, x, 6f, ButtonSize, ButtonSize);
            UiKit.AddTooltip(debug, () => "Debug  (F3): the terrain and crew readout");

            var status = UiKit.NewPanel(_canvas.transform, "Status", new Vector2(0.5f, 0f), new Vector2(0f, ButtonSize + 28f),
                new Vector2(width, 26f), new Color(0.08f, 0.09f, 0.1f, 0.55f));
            _status = UiKit.NewText(status, "", 15, TextAnchor.MiddleCenter);

            BuildSettingsPanel(_canvas.transform);

            var panel = gameObject.AddComponent<ToolPanelView>();
            panel.Build(_canvas, _tools, new Vector2(0f, ButtonSize + 60f));
            var crewPanel = gameObject.AddComponent<CrewPanelView>();
            crewPanel.Build(_canvas, _crew, FindAnyObjectByType<RtsCamera>());

            Tooltip.Create(_canvas);
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
            _settingsPanel = UiKit.NewPanel(parent, "Settings", new Vector2(1f, 1f), new Vector2(-12f, -12f), new Vector2(320f, 162f));
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

            foreach (var (mode, image, icon) in _toolImages)
            {
                var active = mode == _tools.Mode;
                image.color = active ? UiKit.Accent : UiKit.ButtonColor;
                // A dark glyph on the lit button: white on amber barely shows.
                icon.color = active ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
            }
            _seedLabel.text = Time.unscaledTime < _seedArmedUntil ? "again?" : "";

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
