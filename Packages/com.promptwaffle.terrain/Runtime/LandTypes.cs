using System;
using UnityEngine;

namespace PromptWaffle.Terrain
{
    /// <summary>How much of an island is plains, rolling hills and mountains. The three add up to 1.</summary>
    [Serializable]
    public struct LandMix
    {
        public float Plains;
        public float Hills;
        public float Mountains;

        public LandMix(float plains, float hills, float mountains)
        {
            Plains = plains;
            Hills = hills;
            Mountains = mountains;
        }

        public override string ToString() => $"plains {Plains:P0}, hills {Hills:P0}, mountains {Mountains:P0}";
    }

    /// <summary>
    /// Natural terrain, phase 2 (Ronan, 2026-09-21): an island is not one kind of land all over. A
    /// seed draws its own mix of plains, hills and mountains ("each generation should be random"),
    /// a slowly varying field decides where each lies, and each gets its own relief: plains
    /// nearly flat, hills soft and broad, mountains rugged.
    /// </summary>
    public static class LandTypes
    {
        /// <summary>A mix within the settings' ranges; hills take what the other two leave.</summary>
        public static LandMix Draw(System.Random random, TerrainGenSettings settings)
        {
            var plains = Mathf.Lerp(settings.PlainsShareMin, settings.PlainsShareMax, (float)random.NextDouble());
            var mountains = Mathf.Lerp(settings.MountainShareMin, settings.MountainShareMax, (float)random.NextDouble());
            // Hills always get a real share: a border between plains and mountains needs somewhere to be.
            const float minimumHills = 0.15f;
            var over = plains + mountains + minimumHills - 1f;
            if (over > 0f)
            {
                var scale = (plains + mountains - over) / (plains + mountains);
                plains *= scale;
                mountains *= scale;
            }

            return new LandMix(plains, 1f - plains - mountains, mountains);
        }

        /// <summary>
        /// The two cut points of the type field that split its values into the mix: below the
        /// first is plains, above the second is mountains. <paramref name="values"/> is a sample of
        /// the field over the land; it is sorted in place.
        /// </summary>
        public static (float plainsBelow, float mountainsAbove) Thresholds(float[] values, int count, LandMix mix)
        {
            if (count <= 0)
                return (0f, 1f);
            Array.Sort(values, 0, count);
            float Quantile(float q) => values[Mathf.Clamp(Mathf.RoundToInt(q * (count - 1)), 0, count - 1)];
            return (Quantile(mix.Plains), Quantile(1f - mix.Mountains));
        }

        /// <summary>
        /// How much a point of the type field is plains, hills and mountains, blending over
        /// <paramref name="blend"/> either side of each cut so borders are gradual. The three
        /// weights add up to 1.
        /// </summary>
        public static (float plains, float hills, float mountains) Weights(float value, float plainsBelow, float mountainsAbove, float blend)
        {
            blend = Mathf.Max(1e-4f, blend);
            var plains = 1f - SmoothStep(plainsBelow - blend, plainsBelow + blend, value);
            var mountains = SmoothStep(mountainsAbove - blend, mountainsAbove + blend, value);
            // Where the cuts sit closer than the blend, the two could overlap; mountains win.
            plains = Mathf.Min(plains, 1f - mountains);
            return (plains, 1f - plains - mountains, mountains);
        }

        static float SmoothStep(float from, float to, float value)
        {
            var t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }
    }
}
