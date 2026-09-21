using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// The swell drawn on top of a zone's simulated water: a seeded, random mix of Gerstner waves.
    /// Heights are a mix on purpose, never one height (a sea of same-sized waves reads as a
    /// pattern, not water), and they vary over the map in wind patches as well as between waves.
    /// The swell is visual and sampled (<see cref="WaterZone.TrySample"/>); it does not move water
    /// in the simulation.
    /// </summary>
    [CreateAssetMenu(menuName = "PromptWaffle/Dynamic Water/Wave Settings", fileName = "WaterWaves")]
    public sealed class WaterWaveSettings : ScriptableObject
    {
        public int Seed = 7;

        [Range(1, WaterWaves.MaxWaves)] public int WaveCount = 6;

        [Tooltip("Metres. The smallest wave's height above still water.")]
        [Min(0f)] public float MinAmplitude = 0.05f;

        [Tooltip("Metres. The biggest wave's height above still water.")]
        [Min(0f)] public float MaxAmplitude = 0.45f;

        [Min(1f)] public float MinWavelength = 7f;

        [Min(1f)] public float MaxWavelength = 45f;

        [Tooltip("Degrees on the map (from +x toward +z) the wind blows towards.")]
        [Range(0f, 360f)] public float WindDegrees = 35f;

        [Tooltip("Degrees either side of the wind a wave may run.")]
        [Range(0f, 180f)] public float Spread = 60f;

        [Tooltip("0 is round sine waves, 1 is sharp crests. Kept under 1 so crests never loop.")]
        [Range(0f, 1f)] public float Steepness = 0.55f;

        [Tooltip("Multiplier on the speed deep-water waves really travel at their length.")]
        [Min(0f)] public float SpeedScale = 0.7f;

        [Tooltip("Metres across the patches where the sea is rougher or calmer.")]
        [Min(1f)] public float GustSize = 140f;

        [Tooltip("How calm the calmest patch is, as a fraction of full swell.")]
        [Range(0f, 1f)] public float GustCalm = 0.3f;

        [Tooltip("Metres of water under which the swell dies away, so the shallows lie flat and no wave floods a beach.")]
        [Min(0.1f)] public float DampDepth = 4f;

        [Tooltip("Crest height, as a fraction of the local swell, at which a crest turns white.")]
        [Range(0f, 1.5f)] public float Whitecaps = 0.75f;

        /// <summary>Bumped whenever the settings are edited, so zones know to regenerate their wave set.</summary>
        [System.NonSerialized] public int Version;

        void OnValidate() => Version++;

        /// <summary>Call after changing the settings from code.</summary>
        public void MarkChanged() => Version++;
    }
}
