using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The camera as a pivot on the ground plus a yaw, a fixed pitch and a distance. Panning
    /// moves the pivot relative to the current yaw, rotating orbits the pivot, zooming changes
    /// the distance. Plain C#: the controller feeds it input and copies <see cref="Position"/>
    /// and <see cref="Rotation"/> onto the camera.
    /// </summary>
    public sealed class RtsCameraRig
    {
        public Vector3 Pivot;
        public float Yaw;
        public float Pitch = 55f;
        public float Distance = 350f;

        public float MinDistance = 15f;
        public float MaxDistance = 700f;

        /// <summary>Pivot speed as a multiple of the current distance per second, so panning feels the same at every zoom.</summary>
        public float PanSpeed = 0.8f;

        /// <summary>Degrees per second.</summary>
        public float RotateSpeed = 90f;

        /// <summary>Fraction of the distance removed per zoom step.</summary>
        public float ZoomStep = 0.12f;

        /// <summary>How quickly the pivot's height catches up with the ground under it; higher is snappier.</summary>
        public float GroundFollowRate = 8f;

        public Vector2 BoundsMin;
        public Vector2 BoundsMax;

        public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        public Vector3 Position => Pivot - Rotation * Vector3.forward * Distance;

        /// <summary>
        /// Moves the pivot. <paramref name="input"/> x is right, y is forward, relative to the
        /// camera's yaw; it is clamped to length 1 so diagonals are not faster.
        /// </summary>
        public void Pan(Vector2 input, float deltaTime)
        {
            input = Vector2.ClampMagnitude(input, 1f);
            var yaw = Quaternion.Euler(0f, Yaw, 0f);
            var move = (yaw * Vector3.right * input.x + yaw * Vector3.forward * input.y) * (PanSpeed * Distance * deltaTime);
            Pivot.x = Mathf.Clamp(Pivot.x + move.x, BoundsMin.x, BoundsMax.x);
            Pivot.z = Mathf.Clamp(Pivot.z + move.z, BoundsMin.y, BoundsMax.y);
        }

        /// <summary>Positive steps zoom in, negative zoom out. Each step scales the distance, so zoom feels even.</summary>
        public void Zoom(float steps)
        {
            Distance = Mathf.Clamp(Distance * Mathf.Pow(1f - ZoomStep, steps), MinDistance, MaxDistance);
        }

        /// <summary>Positive direction orbits clockwise seen from above.</summary>
        public void Rotate(float direction, float deltaTime)
        {
            Yaw = Mathf.Repeat(Yaw + direction * RotateSpeed * deltaTime, 360f);
        }

        /// <summary>Eases the pivot's height towards the ground so the camera rides over hills without snapping.</summary>
        public void FollowGround(float groundHeight, float deltaTime)
        {
            Pivot.y = Mathf.Lerp(Pivot.y, groundHeight, 1f - Mathf.Exp(-GroundFollowRate * deltaTime));
        }
    }
}
