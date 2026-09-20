using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Where the camera wants to be: a pivot on the ground, a distance back from it, a heading
    /// and a pitch. Plain C# and frame-independent, so the arithmetic — panning that scales with
    /// zoom, zooming toward a point, pitch following zoom, and keeping the pivot on the disc —
    /// can be tested without a scene. <see cref="RtsCamera"/> reads input into this and eases the
    /// camera toward it.
    /// </summary>
    public sealed class RtsCameraRig
    {
        /// <summary>The ground point the camera orbits, in cells.</summary>
        public Vector3 Pivot;

        /// <summary>Degrees clockwise from +z.</summary>
        public float Yaw;

        /// <summary>Metres from the pivot.</summary>
        public float Distance = 100f;

        /// <summary>Degrees the player has nudged the pitch away from what the zoom asks for.</summary>
        public float PitchOffset;

        public float MinDistance = 4f;

        public float MaxDistance = 600f;

        /// <summary>Fraction of the distance one scroll notch covers.</summary>
        public float ZoomStep = 0.15f;

        /// <summary>Cells a second of panning at the closest zoom.</summary>
        public float PanSpeed = 12f;

        /// <summary>How many times faster panning is at the furthest zoom.</summary>
        public float PanSpeedAtFar = 6f;

        public float ClosePitch = 35f;

        public float FarPitch = 60f;

        /// <summary>The middle of the disc of land, in cells.</summary>
        public Vector2 DiscCentre = Vector2.zero;

        /// <summary>Cells from the middle of the disc to its rim; the pivot never leaves it.</summary>
        public float DiscRadius = 128f;

        /// <summary>How far out the camera is, 0 closest and 1 furthest.</summary>
        public float ZoomFraction => Mathf.InverseLerp(MinDistance, MaxDistance, Distance);

        /// <summary>The pitch the current zoom asks for, plus whatever the player has nudged it by.</summary>
        public float Pitch => Mathf.Clamp(Mathf.Lerp(ClosePitch, FarPitch, ZoomFraction) + PitchOffset, 5f, 89f);

        public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        /// <summary>Where the camera sits to look at the pivot from <see cref="Distance"/> away.</summary>
        public Vector3 CameraPosition => Pivot - Rotation * Vector3.forward * Distance;

        /// <summary>
        /// Moves the pivot in screen-relative directions on the ground: x is right, y is away from
        /// the viewer, both turned by the current heading. Speed rises with the zoom, so a given
        /// input covers a similar share of the screen at any distance.
        /// </summary>
        public void Pan(Vector2 input, float deltaTime)
        {
            if (input.sqrMagnitude < 1e-6f)
                return;
            var speed = PanSpeed * Mathf.Lerp(1f, PanSpeedAtFar, ZoomFraction);
            var move = Quaternion.Euler(0f, Yaw, 0f) * new Vector3(input.x, 0f, input.y) * (speed * deltaTime);
            Pivot = ClampToDisc(Pivot + move);
        }

        public void Turn(float degrees)
        {
            Yaw += degrees;
        }

        public void NudgePitch(float degrees)
        {
            PitchOffset = Mathf.Clamp(PitchOffset + degrees, -30f, 30f);
        }

        /// <summary>
        /// Zooms by whole notches: each one closes or opens the distance by the same fraction, so
        /// the step is even at every scale. With a point given, the pivot slides toward it by as
        /// much of the way as the zoom closed, which is what makes the zoom follow the cursor.
        /// </summary>
        public void Zoom(float notches, Vector3? towards = null)
        {
            var before = Distance;
            Distance = Mathf.Clamp(Distance * Mathf.Pow(1f - ZoomStep, notches), MinDistance, MaxDistance);
            if (!towards.HasValue || before <= 1e-4f)
                return;
            var closed = 1f - Distance / before;
            if (closed > 0f)
                Pivot = ClampToDisc(Vector3.Lerp(Pivot, towards.Value, Mathf.Clamp01(closed)));
        }

        /// <summary>Puts the camera over the middle of the map, fully zoomed out.</summary>
        public void GoHome()
        {
            Pivot = new Vector3(DiscCentre.x, Pivot.y, DiscCentre.y);
            Distance = MaxDistance;
            Yaw = 45f;
            PitchOffset = 0f;
        }

        /// <summary>Keeps a point on the disc, so the land can never be pushed off the table.</summary>
        public Vector3 ClampToDisc(Vector3 point)
        {
            var flat = new Vector2(point.x - DiscCentre.x, point.z - DiscCentre.y);
            if (flat.magnitude > DiscRadius)
                flat = flat.normalized * DiscRadius;
            return new Vector3(DiscCentre.x + flat.x, point.y, DiscCentre.y + flat.y);
        }
    }
}
