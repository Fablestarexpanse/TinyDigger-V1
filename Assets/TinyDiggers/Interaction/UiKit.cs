using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The handful of uGUI pieces the toolbar, the tool panel and the crew panel are built from
    /// (Slice 17), made in code in one look: dark translucent panels, white text, the amber
    /// accent. Nothing here holds state.
    /// </summary>
    public static class UiKit
    {
        public static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.1f, 0.82f);
        public static readonly Color ButtonColor = new Color(0.17f, 0.18f, 0.2f, 0.95f);
        public static readonly Color ButtonHover = new Color(0.26f, 0.28f, 0.31f, 0.98f);
        public static readonly Color Accent = new Color(0.95f, 0.78f, 0.25f, 1f);
        public static readonly Color Warning = new Color(1f, 0.84f, 0.2f, 0.95f);
        public static readonly Color FieldColor = new Color(0.12f, 0.13f, 0.15f, 1f);

        static Font _font;

        public static Font Font => _font != null ? _font
            : _font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Helvetica" }, 16);

        /// <summary>A screen-space canvas scaled for 1920 x 1080, with a raycaster so its buttons take clicks.</summary>
        public static Canvas NewCanvas(Transform parent, string name, int order)
        {
            var canvasObject = new GameObject(name) { hideFlags = HideFlags.DontSave };
            canvasObject.transform.SetParent(parent, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();
            return canvas;
        }

        /// <summary>
        /// Makes the one <see cref="EventSystem"/> the UI needs, if the scene has none.
        ///
        /// Without it no uGUI raycast ever happens, so every button, toggle and typed field in the
        /// game is dead to the mouse — and worse than dead: <c>PlayerTools.PointerOverUi</c> asks
        /// <see cref="EventSystem.current"/> whether the pointer is over the interface, gets null,
        /// and says no, so a click meant for a panel digs the ground behind it. The scene had none
        /// and nobody made one (Ronan, 2026-09-23: "I can't use my mouse on any of the tools").
        ///
        /// It is made here rather than put in the scene so it cannot go missing again: every
        /// canvas in the game comes through this method.
        /// </summary>
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            // Resources.FindObjectsOfTypeAll, not FindAnyObjectByType: the one made here is marked
            // DontSave, and FindAnyObjectByType cannot see objects with that flag — so asking it
            // made a second event system every time a second canvas was built, and two of them
            // fight over the input (2026-09-23).
            foreach (var existing in Resources.FindObjectsOfTypeAll<EventSystem>())
                if (existing.gameObject.scene.IsValid())
                    return;

            var holder = new GameObject("Event System") { hideFlags = HideFlags.DontSave };
            holder.AddComponent<EventSystem>();
            // The project is Input System only (activeInputHandler = 1), so the old
            // StandaloneInputModule would throw on the first frame it tried to read the mouse.
            var module = holder.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }

        public static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>A panel anchored and pivoted at <paramref name="anchor"/>, <paramref name="size"/> big.</summary>
        public static RectTransform NewPanel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Color? color = null)
        {
            var rect = NewRect(parent, name);
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.gameObject.AddComponent<Image>().color = color ?? PanelColor;
            return rect;
        }

        /// <summary>Text filling its parent, inset a little.</summary>
        public static Text NewText(Transform parent, string content, int size, TextAnchor alignment, Color? color = null)
        {
            var rect = NewRect(parent, "Text");
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 2f);
            rect.offsetMax = new Vector2(-6f, -2f);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = Font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = color ?? Color.white;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        /// <summary>Places a child rect at a fixed position and size inside its parent, from the top left.</summary>
        public static RectTransform Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        public static Text NewLabel(RectTransform parent, string content, float x, float y, float width, float height, int size = 15,
            TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var holder = Place(NewRect(parent, "Label"), x, y, width, height);
            var text = NewText(holder, content, size, alignment);
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            return text;
        }

        /// <summary>A flat button with an optional icon and label, hovering lighter.</summary>
        public static Button NewButton(RectTransform parent, string label, Action onClick, Sprite icon = null, int fontSize = 15)
        {
            var rect = NewRect(parent, label.Length > 0 ? label : "Button");
            var image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColor;
            var button = rect.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.colorMultiplier = 1.2f;
            button.colors = colors;
            button.onClick.AddListener(() => onClick?.Invoke());

            if (icon != null)
            {
                var iconRect = NewRect(rect, "Icon");
                iconRect.anchorMin = iconRect.anchorMax = new Vector2(0.5f, 0.5f);
                iconRect.sizeDelta = new Vector2(32f, 32f);
                iconRect.anchoredPosition = label.Length > 0 ? new Vector2(0f, 6f) : Vector2.zero;
                var iconImage = iconRect.gameObject.AddComponent<Image>();
                iconImage.sprite = icon;
                iconImage.raycastTarget = false;
                iconImage.preserveAspect = true;
            }

            if (label.Length > 0)
            {
                var text = NewText(rect, label, fontSize, icon != null ? TextAnchor.LowerCenter : TextAnchor.MiddleCenter);
                text.rectTransform.offsetMin = new Vector2(2f, 1f);
            }

            return button;
        }

        /// <summary>A horizontal slider of whole numbers from <paramref name="min"/> to <paramref name="max"/>.</summary>
        public static Slider NewSlider(RectTransform parent, float min, float max, bool wholeNumbers, Action<float> onChange)
        {
            var rect = NewRect(parent, "Slider");
            var background = NewRect(rect, "Background");
            background.anchorMin = new Vector2(0f, 0.4f);
            background.anchorMax = new Vector2(1f, 0.6f);
            background.offsetMin = background.offsetMax = Vector2.zero;
            background.gameObject.AddComponent<Image>().color = FieldColor;

            var fillArea = NewRect(rect, "Fill Area");
            fillArea.anchorMin = new Vector2(0f, 0.4f);
            fillArea.anchorMax = new Vector2(1f, 0.6f);
            fillArea.offsetMin = new Vector2(4f, 0f);
            fillArea.offsetMax = new Vector2(-8f, 0f);
            var fill = NewRect(fillArea, "Fill");
            fill.sizeDelta = new Vector2(8f, 0f);
            fill.gameObject.AddComponent<Image>().color = Accent;

            var handleArea = NewRect(rect, "Handle Area");
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(8f, 0f);
            handleArea.offsetMax = new Vector2(-8f, 0f);
            var handle = NewRect(handleArea, "Handle");
            handle.sizeDelta = new Vector2(14f, 0f);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = Color.white;

            var slider = rect.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.onValueChanged.AddListener(v => onChange?.Invoke(v));
            return slider;
        }

        /// <summary>A one-line number field; <paramref name="onSubmit"/> gets what was typed on Enter or leaving it.</summary>
        public static InputField NewNumberField(RectTransform parent, Action<string> onSubmit)
        {
            var rect = NewRect(parent, "Field");
            rect.gameObject.AddComponent<Image>().color = FieldColor;
            var field = rect.gameObject.AddComponent<InputField>();
            var text = NewText(rect, "", 15, TextAnchor.MiddleCenter);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            field.textComponent = text;
            field.contentType = InputField.ContentType.DecimalNumber;
            field.onEndEdit.AddListener(v => onSubmit?.Invoke(v));
            return field;
        }

        /// <summary>A checkbox with a label to its right.</summary>
        public static Toggle NewToggle(RectTransform parent, string label, bool on, Action<bool> onChange)
        {
            var rect = NewRect(parent, label);
            var box = NewRect(rect, "Box");
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(0f, 0.5f);
            box.sizeDelta = new Vector2(18f, 18f);
            var boxImage = box.gameObject.AddComponent<Image>();
            boxImage.color = FieldColor;
            var tick = NewRect(box, "Tick");
            tick.anchorMin = Vector2.zero;
            tick.anchorMax = Vector2.one;
            tick.offsetMin = new Vector2(4f, 4f);
            tick.offsetMax = new Vector2(-4f, -4f);
            var tickImage = tick.gameObject.AddComponent<Image>();
            tickImage.color = Accent;

            var text = NewText(rect, label, 15, TextAnchor.MiddleLeft);
            text.rectTransform.offsetMin = new Vector2(24f, 0f);

            var toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = tickImage;
            toggle.isOn = on;
            toggle.onValueChanged.AddListener(v => onChange?.Invoke(v));
            return toggle;
        }

        /// <summary>Gives a UI element a hover tooltip, shown after <see cref="Tooltip.Delay"/>.</summary>
        public static TooltipTrigger AddTooltip(Component target, Func<string> text)
        {
            var trigger = target.gameObject.AddComponent<TooltipTrigger>();
            trigger.Text = text;
            return trigger;
        }
    }
}
