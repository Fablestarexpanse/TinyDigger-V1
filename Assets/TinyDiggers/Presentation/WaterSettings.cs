using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// How the water looks and moves, in one asset: the colour of the water column, refraction,
    /// the swell, the waves running up the beach, foam and the sky it reflects.
    ///
    /// Wave heights are a seeded random mix on purpose, never one height: a sea where every wave is
    /// the same size reads as a pattern, not as water. <see cref="WaveSet"/> turns these ranges into
    /// the actual waves.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Water Settings", fileName = "Water")]
    public sealed class WaterSettings : ScriptableObject
    {
        [Header("Water column")]
        [Tooltip("Colour the water scatters where it is shallow.")]
        public Color ScatterShallow = new Color(0.20f, 0.62f, 0.62f);

        [Tooltip("Colour the water scatters where it is deep.")]
        public Color ScatterDeep = new Color(0.04f, 0.20f, 0.33f);

        [Tooltip("How quickly the water swallows red, green and blue, per metre of water the eye " +
            "looks through. Red goes first, which is what makes water blue-green.")]
        public Vector3 Absorption = new Vector3(0.45f, 0.16f, 0.11f);

        [Tooltip("How quickly the scatter colour builds up with depth, per metre.")]
        [Min(0f)] public float ScatterDensity = 0.12f;

        [Tooltip("How far the ripples bend the seabed seen through the water, in screen fraction.")]
        [Range(0f, 0.1f)] public float Refraction = 0.015f;

        [Header("Swell")]
        public int Seed = 7;

        [Range(1, 8)] public int WaveCount = 6;

        [Tooltip("Metres. The smallest wave's height above still water.")]
        [Min(0f)] public float MinAmplitude = 0.05f;

        [Tooltip("Metres. The biggest wave's height above still water.")]
        [Min(0f)] public float MaxAmplitude = 0.45f;

        [Min(1f)] public float MinWavelength = 7f;

        [Min(1f)] public float MaxWavelength = 45f;

        [Tooltip("Degrees on the map the wind blows towards. The swell and the ripples follow it.")]
        [Range(0f, 360f)] public float WindDegrees = 35f;

        [Tooltip("Degrees either side of the wind a wave may run.")]
        [Range(0f, 180f)] public float Spread = 60f;

        [Tooltip("0 is round sine waves, 1 is sharp crests. Kept under 1 so crests never loop.")]
        [Range(0f, 1f)] public float Steepness = 0.55f;

        [Tooltip("Multiplier on the speed deep water waves really travel at their length.")]
        [Min(0f)] public float SpeedScale = 0.7f;

        [Tooltip("Metres across the patches where the sea is rougher or calmer, so heights vary " +
            "over the map as well as between waves.")]
        [Min(1f)] public float GustSize = 140f;

        [Tooltip("How calm the calmest patch is, as a fraction of full swell.")]
        [Range(0f, 1f)] public float GustCalm = 0.3f;

        [Tooltip("Metres of water under which the swell dies away, so the shallows lie flat and no " +
            "wave ever floods a beach.")]
        [Min(0.1f)] public float DampDepth = 4f;

        [Tooltip("Metres from the shore over which the swell dies away.")]
        [Min(0.1f)] public float DampDistance = 6f;

        [Tooltip("Crest height, as a fraction of the local swell, at which a crest turns white.")]
        [Range(0f, 1.5f)] public float Whitecaps = 0.75f;

        [Header("Waves on the beach")]
        [Tooltip("Metres out from the shore that waves run in from.")]
        [Min(0f)] public float ShoreReach = 16f;

        [Tooltip("Metres between one breaking wave and the next.")]
        [Min(0.5f)] public float ShoreSpacing = 6f;

        [Tooltip("Waves reaching the shore per second, roughly.")]
        [Min(0f)] public float ShoreSpeed = 0.18f;

        [Tooltip("Metres the surface lifts under a wave running in.")]
        [Min(0f)] public float ShoreLift = 0.12f;

        [Range(0f, 1f)] public float ShoreFoam = 0.9f;

        [Header("Ripples")]
        [Tooltip("Metres across one tile of the large ripple layer.")]
        [Min(0.5f)] public float RippleSize = 9f;

        [Tooltip("Metres across one tile of the small ripple layer.")]
        [Min(0.5f)] public float RippleDetailSize = 3.1f;

        [Range(0f, 2f)] public float RippleStrength = 0.35f;

        [Tooltip("Metres per second the ripples drift down the wind.")]
        [Min(0f)] public float RippleSpeed = 0.35f;

        [Tooltip("Metres from the camera at which ripples have eased to a third, so they never " +
            "alias into a grid in the distance.")]
        [Min(1f)] public float RippleFade = 220f;

        [Tooltip("Metres per second a river runs on the flat. Steeper reaches run faster.")]
        [Min(0f)] public float FlowSpeed = 1.2f;

        [Header("Light")]
        public Color SkyHorizon = new Color(0.78f, 0.84f, 0.88f);

        public Color SkyZenith = new Color(0.38f, 0.56f, 0.78f);

        [Range(0f, 1f)] public float Reflection = 0.8f;

        [Range(1f, 10f)] public float FresnelPower = 5f;

        [Range(0f, 10f)] public float SunGlint = 3f;

        [Range(8f, 2048f)] public float SunSharpness = 600f;

        [Range(0f, 2f)] public float Caustics = 0.45f;

        [Tooltip("Metres across a caustic cell on the seabed.")]
        [Min(0.5f)] public float CausticSize = 4f;

        [Header("Foam")]
        public Color Foam = new Color(0.95f, 0.97f, 0.96f);

        [Tooltip("Metres of water at the edge of anything standing in it that foams.")]
        [Min(0f)] public float ContactFoam = 0.6f;
    }
}
