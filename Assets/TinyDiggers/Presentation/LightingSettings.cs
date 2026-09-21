using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// How the world is lit, in one asset, because lighting is the thing that decides whether a
    /// terrace reads as a terrace or as a smudge.
    ///
    /// The sun is placed relative to the camera's heading rather than in world space: what matters
    /// is that the light comes over the player's shoulder from one side, so every slope facing them
    /// is lit and every slope facing away is not.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Lighting Settings", fileName = "Lighting")]
    public sealed class LightingSettings : ScriptableObject
    {
        [Header("Sun")]
        [Tooltip("Degrees above the horizon. Low enough for long shadows, high enough to reach a pit floor.")]
        [Range(10f, 80f)] public float Elevation = 38f;

        [Tooltip("Degrees from the camera's heading. Negative is to the viewer's left.")]
        [Range(-180f, 180f)] public float AzimuthFromCamera = -35f;

        [Tooltip("Colour temperature of the sun, in kelvin. Lower is warmer.")]
        [Range(2000f, 10000f)] public float Temperature = 4800f;

        [Min(0f)] public float Intensity = 1.05f;

        [Header("Shadows")]
        public bool SoftShadows = true;

        [Min(10f)] public float ShadowDistance = 300f;

        [Range(1, 4)] public int ShadowCascades = 4;

        [Tooltip("Shadow map resolution. 4096 keeps a one-metre terrace edge crisp at RTS distance.")]
        public int ShadowResolution = 4096;

        [Range(0f, 2f)] public float ShadowStrength = 0.65f;

        [Tooltip("Too small and a stepped heightfield shadows itself everywhere, which reads as a " +
            "dark, dirty island rather than as terraces.")]
        [Range(0f, 1f)] public float ShadowBias = 0.1f;

        [Range(0f, 3f)] public float ShadowNormalBias = 0.6f;

        [Header("Ambient")]
        [Tooltip("The light from the sky, which fills everything the sun does not reach. Raised " +
            "well past neutral on purpose: a fully shadowed slope should still read as green or " +
            "brown, never as grey, and this is the single biggest lever on that.")]
        public Color SkyColour = new Color(0.68f, 0.76f, 0.86f);

        [Tooltip("Light bouncing off the ground: warmer, so shaded slopes do not go blue-grey.")]
        public Color GroundColour = new Color(0.62f, 0.55f, 0.44f);

        public Color EquatorColour = new Color(0.76f, 0.73f, 0.68f);

        [Range(0f, 3f)] public float AmbientIntensity = 1.5f;

        [Header("Ambient occlusion")]
        [Tooltip("Screen-space ambient occlusion. This is what makes a terrace crease read.")]
        public bool AmbientOcclusion = true;

        [Tooltip("Metres. Small, because the creases we care about are one metre deep.")]
        [Range(0.1f, 4f)] public float OcclusionRadius = 0.6f;

        [Tooltip("Gentle on purpose: the radius is in world metres, so from far out a strong one " +
            "shades the whole island rather than its creases.")]
        [Range(0f, 4f)] public float OcclusionIntensity = 0.25f;

        [Header("Terrain shading")]
        [Tooltip("How far a steep face is tinted toward a cool grey. This reads as slope even in shadow.")]
        [Range(0f, 0.6f)] public float SlopeTint = 0.12f;

        [Tooltip("The colour steep ground is tinted toward. Warm, not cool: a cool tint on a warm " +
            "palette reads as wet slate.")]
        public Color SlopeColour = new Color(0.72f, 0.68f, 0.62f);

        [Tooltip("Degrees of slope at which the tint is at full strength.")]
        [Range(10f, 80f)] public float SlopeFullAt = 45f;

        [Header("Contours (F4)")]
        public bool Contours;

        [Tooltip("Metres between the strong contour lines.")]
        [Min(1f)] public float MajorContour = 5f;

        [Tooltip("Metres between the faint ones.")]
        [Min(0.25f)] public float MinorContour = 1f;

        [Range(0f, 1f)] public float ContourStrength = 0.35f;
    }
}
