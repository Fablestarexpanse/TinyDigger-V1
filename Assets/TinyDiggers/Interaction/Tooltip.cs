using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TinyDiggers.Interaction
{
    /// <summary>The one tooltip box, which follows the pointer (Slice 17).</summary>
    public sealed class Tooltip : MonoBehaviour
    {
        /// <summary>Seconds of hovering before a tooltip shows.</summary>
        public const float Delay = 0.4f;

        static Tooltip _instance;
        RectTransform _box;
        Text _text;
        TooltipTrigger _over;
        float _since;

        public static Tooltip Create(Canvas canvas)
        {
            var holder = UiKit.NewRect(canvas.transform, "Tooltip");
            holder.anchorMin = holder.anchorMax = Vector2.zero;
            _instance = holder.gameObject.AddComponent<Tooltip>();
            _instance._box = UiKit.NewPanel(holder, "Box", Vector2.zero, Vector2.zero, new Vector2(200f, 28f), new Color(0.02f, 0.02f, 0.03f, 0.92f));
            _instance._box.pivot = new Vector2(0f, 0f);
            _instance._box.GetComponent<Image>().raycastTarget = false;
            _instance._text = UiKit.NewText(_instance._box, "", 15, TextAnchor.MiddleLeft);
            _instance._box.gameObject.SetActive(false);
            return _instance;
        }

        internal static void Enter(TooltipTrigger trigger)
        {
            if (_instance == null)
                return;
            _instance._over = trigger;
            _instance._since = Time.unscaledTime;
        }

        internal static void Exit(TooltipTrigger trigger)
        {
            if (_instance == null || _instance._over != trigger)
                return;
            _instance._over = null;
            _instance._box.gameObject.SetActive(false);
        }

        void Update()
        {
            if (_over == null || !_over.isActiveAndEnabled || Time.unscaledTime - _since < Delay)
            {
                if (_over != null && !_over.isActiveAndEnabled)
                    _over = null;
                _box.gameObject.SetActive(false);
                return;
            }

            var content = _over.Text?.Invoke() ?? "";
            if (content.Length == 0)
            {
                _box.gameObject.SetActive(false);
                return;
            }

            _text.text = content;
            _box.sizeDelta = new Vector2(_text.preferredWidth + 18f, 28f);
            var canvas = GetComponentInParent<Canvas>();
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            var pointer = UnityEngine.InputSystem.Mouse.current != null
                ? UnityEngine.InputSystem.Mouse.current.position.ReadValue()
                : Vector2.zero;
            // Above and to the right of the pointer, kept on screen.
            var at = pointer / scale + new Vector2(14f, 18f);
            var screen = new Vector2(Screen.width, Screen.height) / scale;
            at.x = Mathf.Min(at.x, screen.x - _box.sizeDelta.x - 4f);
            at.y = Mathf.Min(at.y, screen.y - _box.sizeDelta.y - 4f);
            _box.anchoredPosition = at;
            _box.gameObject.SetActive(true);
            _box.SetAsLastSibling();
        }
    }
}
