using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// A camera the player liked: how far back it sits, how far over it is tilted, how wide its
    /// lens is, and whether the tilt follows the zoom. Saved with F5, reloaded with F6, and used
    /// by Home — so whatever is in <c>Assets/TinyDiggers/Camera/Default.asset</c> is the camera the
    /// game starts with.
    ///
    /// The pivot and the heading are deliberately not in here: a preset is a way of looking at the
    /// map, not a place on it.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Camera Preset", fileName = "Default")]
    public sealed class CameraPreset : ScriptableObject
    {
        [Range(RtsCameraRig.MinPitch, RtsCameraRig.MaxPitch)]
        [Tooltip("Degrees below the horizon. 10 is almost level with the ground, 89 is straight down.")]
        public float Pitch = 45f;

        [Range(RtsCameraRig.MinFov, RtsCameraRig.MaxFov)]
        [Tooltip("Vertical field of view. Narrow and far back is what gives the model-on-a-table look.")]
        public float Fov = 40f;

        [Min(1f)]
        [Tooltip("Metres from the pivot.")]
        public float Distance = 120f;

        [Tooltip("Let the zoom drive the pitch, as it did before the tilt was freed. Off by default.")]
        public bool PitchFollowsZoom;

        /// <summary>Reads a rig into this preset.</summary>
        public void CaptureFrom(RtsCameraRig rig)
        {
            Pitch = rig.Pitch;
            Fov = rig.Fov;
            Distance = rig.Distance;
            PitchFollowsZoom = rig.PitchFollowsZoom;
        }

        /// <summary>Writes this preset onto a rig, leaving the pivot and the heading alone.</summary>
        public void ApplyTo(RtsCameraRig rig)
        {
            rig.PitchFollowsZoom = PitchFollowsZoom;
            rig.SetPitch(Pitch);
            rig.Fov = Mathf.Clamp(Fov, RtsCameraRig.MinFov, RtsCameraRig.MaxFov);
            rig.Distance = Mathf.Clamp(Distance, rig.MinDistance, rig.MaxDistance);
        }
    }
}
