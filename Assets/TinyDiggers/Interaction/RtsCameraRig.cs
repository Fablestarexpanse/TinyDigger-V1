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
        /// <summary>Degrees below the horizon at the flattest the camera will go.</summary>
        public const float MinPitch = 10f;

        /// <summary>Degrees below the horizon looking as near straight down as it will go.</summary>
        public const float MaxPitch = 89f;

        public const float MinFov = 15f;

        public const float MaxFov = 60f;

        /// <summary>The ground point the camera orbits, in metres in the terrain's local space.</summary>
        public Vector3 Pivot;

        /// <summary>Degrees clockwise from +z.</summary>
        public float Yaw;

        /// <summary>Metres from the pivot.</summary>
        public float Distance = 100f;

        /// <summary>Degrees the player has nudged the pitch away from what the zoom asks for.</summary>
        public float PitchOffset;

        /// <summary>
        /// Whether the zoom drives the pitch, low down when close and looking down when far out.
        /// Off by default: the tilt is the player's, and the zoom only changes how far back the
        /// camera sits. <see cref="PitchOffset"/> still nudges the curve when this is on.
        /// </summary>
        public bool PitchFollowsZoom;

        /// <summary>Vertical field of view, in degrees. Narrow and far back reads as a model.</summary>
        public float Fov = 40f;

        public float MinDistance = 4f;

        public float MaxDistance = 600f;

        /// <summary>Fraction of the distance one scroll notch covers.</summary>
        public float ZoomStep = 0.15f;

        /// <summary>Metres a second of panning at the closest zoom.</summary>
        public float PanSpeed = 12f;

        /// <summary>How many times faster panning is at the furthest zoom.</summary>
        public float PanSpeedAtFar = 6f;

        public float ClosePitch = 35f;

        public float FarPitch = 60f;

        /// <summary>The middle of the disc of land, in metres.</summary>
        public Vector2 DiscCentre = Vector2.zero;

        /// <summary>Metres from the middle of the disc to its rim; the pivot never leaves it.</summary>
        public float DiscRadius = 128f;

        /// <summary>How far out the camera is, 0 closest and 1 furthest.</summary>
        public float ZoomFraction => Mathf.InverseLerp(MinDistance, MaxDistance, Distance);

        /// <summary>Degrees below the horizon when the pitch is the player's rather than the zoom's.</summary>
        float _pitch = 45f;

        /// <summary>
        /// How far over the camera is tilted: the player's own angle, or, with
        /// <see cref="PitchFollowsZoom"/> on, what the zoom asks for plus whatever they have
        /// nudged it by.
        /// </summary>
        public float Pitch => PitchFollowsZoom
            ? Mathf.Clamp(Mathf.Lerp(ClosePitch, FarPitch, ZoomFraction) + PitchOffset, MinPitch, MaxPitch)
            : _pitch;

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

        /// <summary>
        /// Tilts by <paramref name="degrees"/>, positive toward looking straight down. Free
        /// between <see cref="MinPitch"/> and <see cref="MaxPitch"/>; with
        /// <see cref="PitchFollowsZoom"/> on it moves the nudge away from the zoom's curve instead,
        /// which is what it always used to do.
        /// </summary>
        public void Tilt(float degrees)
        {
            if (PitchFollowsZoom)
            {
                PitchOffset = Mathf.Clamp(PitchOffset + degrees, -40f, 40f);
                return;
            }

            SetPitch(_pitch + degrees);
        }

        /// <summary>Puts the free pitch at an angle, clamped to the range the camera allows.</summary>
        public void SetPitch(float degrees)
        {
            _pitch = Mathf.Clamp(degrees, MinPitch, MaxPitch);
        }

        /// <summary>Widens or narrows the lens, clamped to <see cref="MinFov"/>..<see cref="MaxFov"/>.</summary>
        public void ChangeFov(float degrees)
        {
            Fov = Mathf.Clamp(Fov + degrees, MinFov, MaxFov);
        }

        /// <summary>
        /// Zooms by whole notches: each one closes or opens the distance by the same fraction, so
        /// the step is even at every scale. With a point given, the pivot slides toward it by as
        /// much of the way as the zoom closed, which is what makes the zoom follow the cursor.
        /// Zooming out slides the pivot back toward the middle of the disc by as much of the way
        /// as the zoom opened toward <see cref="MaxDistance"/>, so fully out is always the whole
        /// disc: a zoom in toward the rim used to leave the pivot there, and no amount of zooming
        /// out brought the far side back on screen.
        /// </summary>
        public void Zoom(float notches, Vector3? towards = null)
        {
            var before = Distance;
            Distance = Mathf.Clamp(Distance * Mathf.Pow(1f - ZoomStep, notches), MinDistance, MaxDistance);
            RecentreOnZoomOut(before);
            if (!towards.HasValue || before <= 1e-4f)
                return;
            var closed = 1f - Distance / before;
            if (closed > 0f)
                Pivot = ClampToDisc(Vector3.Lerp(Pivot, towards.Value, Mathf.Clamp01(closed)));
        }

        void RecentreOnZoomOut(float before)
        {
            if (Distance <= before || MaxDistance - before <= 1e-4f)
                return;
            var opened = Mathf.Clamp01((Distance - before) / (MaxDistance - before));
            var centre = new Vector3(DiscCentre.x, Pivot.y, DiscCentre.y);
            Pivot = Vector3.Lerp(Pivot, centre, opened);
        }

        /// <summary>
        /// Puts the camera over the middle of the map. With a preset it takes that preset's way of
        /// looking at the map; without one it falls back to fully zoomed out, as it used to.
        /// </summary>
        public void GoHome(CameraPreset preset = null)
        {
            Pivot = new Vector3(DiscCentre.x, Pivot.y, DiscCentre.y);
            Yaw = 45f;
            PitchOffset = 0f;
            if (preset != null)
            {
                preset.ApplyTo(this);
                return;
            }

            Distance = MaxDistance;
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
