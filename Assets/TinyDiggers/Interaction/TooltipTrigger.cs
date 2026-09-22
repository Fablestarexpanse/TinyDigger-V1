using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TinyDiggers.Interaction
{
    /// <summary>Put on a UI element to give it a tooltip.</summary>
    public sealed class TooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Func<string> Text;

        public void OnPointerEnter(PointerEventData eventData) => Tooltip.Enter(this);

        public void OnPointerExit(PointerEventData eventData) => Tooltip.Exit(this);

        void OnDisable() => Tooltip.Exit(this);
    }
}
