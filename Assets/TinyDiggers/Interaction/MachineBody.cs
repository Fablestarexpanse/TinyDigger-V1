using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// What <see cref="CrewView"/> needs to know about a machine model's clips that the clips
    /// themselves cannot say: how far one loop of its drive clip carries it over the ground, so the
    /// tracks or wheels turn at the speed the machine is going instead of skidding, and how much
    /// faster than authored its Work clip plays.
    ///
    /// The Work speed exists because the machine-forge clips are longer than the ones they replaced
    /// (dig 6.1 s against 2.97 s, tip 5.2 s against 1.77 s), and the crew logic takes its dig and
    /// tip times from the clip. Played as authored they would have halved a digger's throughput;
    /// the Animator's Work state plays at this speed and the crew reads the same number, so the
    /// earth still moves when the bucket does (2026-09-24).
    /// </summary>
    public class MachineBody : MonoBehaviour
    {
        [Tooltip("Metres the machine travels in one loop of its drive clip, at the size it is drawn.")]
        [Min(0.01f)] public float MetresPerDriveLoop = 1f;

        [Tooltip("Seconds one loop of the drive clip lasts.")]
        [Min(0.01f)] public float DriveLoopSeconds = 1f;

        [Tooltip("How much faster than authored the Work clip plays; the Animator's Work state has the same speed.")]
        [Min(0.01f)] public float WorkSpeed = 1f;
    }
}
