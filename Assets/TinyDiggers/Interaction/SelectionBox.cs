using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The screen-space maths of box selection: a press and release further apart than
    /// <see cref="DragThreshold"/> pixels is a box, anything shorter is a click.
    /// </summary>
    public static class SelectionBox
    {
        /// <summary>Pixels the mouse must travel with the button held before it counts as a box.</summary>
        public const float DragThreshold = 6f;

        public static bool IsDrag(Vector2 pressed, Vector2 now) =>
            (now - pressed).sqrMagnitude >= DragThreshold * DragThreshold;

        /// <summary>The rectangle between two corners, whichever way it was dragged.</summary>
        public static Rect FromCorners(Vector2 a, Vector2 b) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        /// <summary>
        /// Whether a unit, at <paramref name="screen"/> as a camera's WorldToScreenPoint gives it
        /// (z is the distance in front), is in the box. Anything behind the camera never is.
        /// </summary>
        public static bool Contains(Rect box, Vector3 screen) => screen.z > 0f && box.Contains(new Vector2(screen.x, screen.y));

        /// <summary>The same rectangle for IMGUI, whose y runs down from the top of the screen.</summary>
        public static Rect ToGui(Rect box, float screenHeight) =>
            new Rect(box.xMin, screenHeight - box.yMax, box.width, box.height);
    }
}
