using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The wind everything that sways shares: grass, leaves and anything else drawn with the
    /// TinyDiggers/Foliage shader. One asset, so grass and trees always lean the same way.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Wind Settings", fileName = "Wind")]
    public sealed class WindSettings : ScriptableObject
    {
        [Tooltip("Degrees on the map the wind blows towards. Match the water's wind to keep the sea " +
            "and the grass telling the same story.")]
        [Range(0f, 360f)] public float Degrees = 35f;

        [Tooltip("Metres a plant's tip leans at full weight, before gusts.")]
        [Min(0f)] public float Strength = 0.35f;

        [Tooltip("How fast plants sway, in radians per second.")]
        [Min(0f)] public float Speed = 1.6f;

        [Tooltip("Metres across a gust, the patch of stronger wind rolling over the grass.")]
        [Min(1f)] public float GustSize = 30f;

        [Tooltip("How much gusts change the wind: 0 even, 1 strong patches and lulls.")]
        [Range(0f, 1f)] public float GustStrength = 0.7f;

        [Tooltip("Metres per second gusts travel down the wind.")]
        [Min(0f)] public float GustSpeed = 6f;
    }
}
