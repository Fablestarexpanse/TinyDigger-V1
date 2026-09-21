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
    /// The toolbar along the bottom of the screen: one button per tool, and a line above it that
    /// says what the tool will do — the brush or road width, the target height H and whether it
    /// is locked, what the shape under the cursor would cost in cut and fill, how much is
    /// designated, and a warning while any unit is loaded with nowhere to tip.
    ///
    /// Built in code into one Canvas, so there is nothing to wire up in the scene.
    /// </summary>
    public sealed class ToolbarView : MonoBehaviour
    {
        [SerializeField] PlayerTools _tools;
        [SerializeField] CrewView _crew;

        int _dugVersion = -1;
        string _dug = string.Empty;

        [Tooltip("The land, for the F2 settings panel's seed and Regenerate.")]
        [SerializeField] TerrainView _terrain;
        [SerializeField] Color _idleColor = new Color(0.16f, 0.17f, 0.19f, 0.9f);
        [SerializeField] Color _activeColor = new Color(0.95f, 0.78f, 0.25f, 0.95f);
        [SerializeField] Color _warningColor = new Color(1f, 0.55f, 0.2f);

        static readonly (ToolMode Mode, string Label)[] Tools =
        {
            (ToolMode.Select, "1 Select"),
            (ToolMode.Dig, "2 Dig"),
            (ToolMode.Fill, "3 Fill"),
            (ToolMode.DumpZone, "4 Dump Zone"),
            (ToolMode.Level, "5 Level"),
            (ToolMode.Road, "6 Road"),
            (ToolMode.Clear, "7 Clear"),
        };

        readonly List<Image> _buttons = new List<Image>();
        readonly List<Text> _buttonLabels = new List<Text>();
        readonly StringBuilder _text = new StringBuilder(256);

        Text _status;
        Font _font;
        string _shown = "";

        RectTransform _settingsPanel;
        InputField _seedField;
        Text _settingsSummary;

        void Start()
        {
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Helvetica" }, 16);

            var canvasObject = new GameObject("Toolbar Canvas") { hideFlags = HideFlags.DontSave };
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasObject.AddComponent<GraphicRaycaster>();

            var bar = NewPanel(canvasObject.transform, "Bar", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 12f), new Vector2(Tools.Length * 150f + 16f, 56f), new Color(0.08f, 0.09f, 0.1f, 0.75f));
            var layout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            foreach (var tool in Tools)
                AddButton(bar.transform, tool.Mode, tool.Label);

            BuildSettingsPanel(canvasObject.transform);

            var statusPanel = NewPanel(canvasObject.transform, "Status", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 74f), new Vector2(Tools.Length * 150f + 16f, 30f), new Color(0.08f, 0.09f, 0.1f, 0.6f));
            _status = NewText(statusPanel.transform, "", 16, TextAnchor.MiddleCenter);
        }

        /// <summary>
        /// The F2 panel: the seed the island came from, and a button to make another one. Hidden
        /// until F2 is pressed, because it is a setup tool rather than something used while playing.
        /// </summary>
        void BuildSettingsPanel(Transform parent)
        {
            _settingsPanel = NewPanel(parent, "Settings", new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -12f), new Vector2(320f, 132f), new Color(0.08f, 0.09f, 0.1f, 0.85f));
            _settingsPanel.pivot = new Vector2(0f, 1f);
            _settingsPanel.anchoredPosition = new Vector2(12f, -12f);

            var title = NewText(_settingsPanel.transform, "Island  (F2)", 16, TextAnchor.UpperLeft);
            title.rectTransform.offsetMax = new Vector2(-6f, -6f);

            var fieldObject = new GameObject("Seed", typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            fieldObject.transform.SetParent(_settingsPanel.transform, false);
            var fieldRect = fieldObject.GetComponent<RectTransform>();
            fieldRect.anchorMin = new Vector2(0f, 1f);
            fieldRect.anchorMax = new Vector2(0f, 1f);
            fieldRect.pivot = new Vector2(0f, 1f);
            fieldRect.anchoredPosition = new Vector2(10f, -34f);
            fieldRect.sizeDelta = new Vector2(180f, 28f);
            var fieldImage = fieldObject.AddComponent<Image>();
            fieldImage.color = new Color(0.16f, 0.17f, 0.19f, 0.95f);
            _seedField = fieldObject.AddComponent<InputField>();
            _seedField.textComponent = NewText(fieldObject.transform, "", 16, TextAnchor.MiddleLeft);
            _seedField.contentType = InputField.ContentType.IntegerNumber;

            var buttonObject = new GameObject("Regenerate", typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            buttonObject.transform.SetParent(_settingsPanel.transform, false);
            var buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0f, 1f);
            buttonRect.anchorMax = new Vector2(0f, 1f);
            buttonRect.pivot = new Vector2(0f, 1f);
            buttonRect.anchoredPosition = new Vector2(198f, -34f);
            buttonRect.sizeDelta = new Vector2(112f, 28f);
            var buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.color = _activeColor;
            var regenerate = buttonObject.AddComponent<Button>();
            regenerate.onClick.AddListener(Regenerate);
            var buttonLabel = NewText(buttonObject.transform, "Regenerate", 15, TextAnchor.MiddleCenter);
            buttonLabel.color = Color.black;

            _settingsSummary = NewText(_settingsPanel.transform, "", 14, TextAnchor.LowerLeft);
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
                : $"Seed {_terrain.Seed}   peak ({island.Peak.x}, {island.Peak.y})   river {island.River.Count} points";
        }

        void AddButton(Transform parent, ToolMode mode, string label)
        {
            var buttonObject = new GameObject(label, typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            buttonObject.transform.SetParent(parent, false);
            var image = buttonObject.AddComponent<Image>();
            image.color = _idleColor;
            var button = buttonObject.AddComponent<Button>();
            var captured = mode;
            button.onClick.AddListener(() => _tools.SetMode(captured));
            _buttons.Add(image);
            _buttonLabels.Add(NewText(buttonObject.transform, label, 16, TextAnchor.MiddleCenter));
        }

        static RectTransform NewPanel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size, Color color)
        {
            var panel = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            panel.transform.SetParent(parent, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var image = panel.AddComponent<Image>();
            image.color = color;
            return rect;
        }

        Text NewText(Transform parent, string content, int size, TextAnchor alignment)
        {
            var textObject = new GameObject("Text", typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            textObject.transform.SetParent(parent, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 2f);
            rect.offsetMax = new Vector2(-6f, -2f);
            var text = textObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.white;
            text.text = content;
            text.raycastTarget = false;
            return text;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame && _settingsPanel != null)
            {
                var showing = !_settingsPanel.gameObject.activeSelf;
                _settingsPanel.gameObject.SetActive(showing);
                if (showing)
                    RefreshSettings();
            }

            if (_tools == null || _status == null)
                return;

            for (var i = 0; i < _buttons.Count; i++)
            {
                var active = Tools[i].Mode == _tools.Mode;
                _buttons[i].color = active ? _activeColor : _idleColor;
                _buttonLabels[i].color = active ? Color.black : Color.white;
            }

            var map = _tools.Map;
            _text.Clear().Append(_tools.Mode);
            switch (_tools.Mode)
            {
                case ToolMode.Dig:
                case ToolMode.Fill:
                    _text.Append("  brush ").Append(_tools.BrushRadius).Append(" ([ ])");
                    break;
                case ToolMode.Road:
                    _text.Append("  width ").Append(_tools.RoadWidth).Append(" ([ ])  points ").Append(_tools.RoadPointCount);
                    break;
                case ToolMode.DumpZone:
                    _text.Append("  cap H+").Append(_tools.ZoneCapAbove.ToString("0.#"));
                    break;
            }

            if (_tools.Mode != ToolMode.Select && _tools.Mode != ToolMode.Clear)
                _text.Append("   H ").Append(_tools.TargetHeight.ToString("0.#")).Append(" m")
                    .Append(_tools.HeightLocked ? " (locked, PgUp/PgDn)" : " (follows cursor)");

            if (_tools.PlannedCut > 0.05f || _tools.PlannedFill > 0.05f)
                _text.Append("   cut ").Append(_tools.PlannedCut.ToString("0")).Append(" m³, fill ")
                    .Append(_tools.PlannedFill.ToString("0")).Append(" m³");
            if (_tools.Mode == ToolMode.Road && _tools.RoadTooSteep)
                _text.Append("   TOO STEEP ").Append(_tools.RoadGrade.ToString("0.00")).Append(" m/cell");

            _text.Append("   designations ").Append(map.Count);
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
                    _text.Append("   ").Append(_dug);
            }

            var stuck = 0;
            if (_crew != null)
                foreach (var unit in _crew.Units)
                    if (unit.State == CrewUnitState.NeedsSomewhereToTip)
                        stuck++;
            if (stuck > 0)
                _text.Append("   !! ").Append(stuck).Append(stuck == 1 ? " unit needs" : " units need").Append(" a Dump Zone");

            var line = _text.ToString();
            if (line == _shown)
                return;
            _shown = line;
            _status.text = line;
            _status.color = stuck > 0 || _tools.RoadTooSteep ? _warningColor : Color.white;
        }
    }
}
