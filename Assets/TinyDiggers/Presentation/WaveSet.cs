using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>One Gerstner wave: a direction on the map, a height, a length and a shape.</summary>
    public struct Wave
    {
        /// <summary>Unit vector on the map (x, z) the crests travel along.</summary>
        public Vector2 Direction;

        /// <summary>Metres above still water at the crest.</summary>
        public float Amplitude;

        /// <summary>Metres from one crest to the next.</summary>
        public float Wavelength;

        /// <summary>
        /// Gerstner sharpness, already divided out so that the sum over the whole set can never
        /// fold a crest over itself.
        /// </summary>
        public float Steepness;

        /// <summary>Metres per second the crests move.</summary>
        public float Speed;

        /// <summary>Radians, so no two waves start in step.</summary>
        public float Phase;

        public float Number => 2f * Mathf.PI / Wavelength;
    }

    /// <summary>
    /// The swell: a seeded, random mix of Gerstner waves of different heights, lengths and
    /// directions. Ronan's ruling is that wave height is a mix, never one height, so the set always
    /// holds at least one wave near the top of the range and one near the bottom, and the rest are
    /// drawn in between.
    ///
    /// The shader does the same sum on the GPU; <see cref="Displacement"/> is here so the rules can
    /// be tested and so anything floating can ask how high the water is.
    /// </summary>
    public static class WaveSet
    {
        public const float Gravity = 9.81f;

        /// <summary>
        /// Real water cannot hold a wave taller than a seventh of its length from trough to crest,
        /// so a crest is never more than a fourteenth of the length above still water.
        /// </summary>
        public const float BreakingRatio = 1f / 14f;

        public static Wave[] Generate(WaterSettings settings)
        {
            var count = Mathf.Clamp(settings.WaveCount, 1, 8);
            var random = new System.Random(settings.Seed);
            var waves = new Wave[count];

            var minLength = Mathf.Max(1f, Mathf.Min(settings.MinWavelength, settings.MaxWavelength));
            var maxLength = Mathf.Max(minLength, settings.MaxWavelength);
            var minHeight = Mathf.Max(0f, Mathf.Min(settings.MinAmplitude, settings.MaxAmplitude));
            var maxHeight = Mathf.Max(minHeight, settings.MaxAmplitude);
            var wind = settings.WindDegrees * Mathf.Deg2Rad;

            for (var i = 0; i < count; i++)
            {
                // Which share of the height range this wave gets. The first is always big and the
                // second always small, so the set is a mix whatever the seed; the rest lean small,
                // because a sea is mostly small waves with the odd big one through it.
                float share;
                if (i == 0)
                    share = 0.85f + 0.15f * Next(random);
                else if (i == 1 && count > 1)
                    share = 0.15f * Next(random);
                else
                    share = Mathf.Pow(Next(random), 1.6f);

                // Big waves are long waves: a tall wave cannot be short, so the length follows the
                // height with some slack either way.
                var lengthShare = Mathf.Clamp01(share * 0.7f + Next(random) * 0.3f);
                var wavelength = Mathf.Exp(Mathf.Lerp(Mathf.Log(minLength), Mathf.Log(maxLength), lengthShare));
                var amplitude = Mathf.Min(Mathf.Lerp(minHeight, maxHeight, share), wavelength * BreakingRatio);

                var angle = wind + (Next(random) * 2f - 1f) * settings.Spread * Mathf.Deg2Rad;
                var number = 2f * Mathf.PI / wavelength;

                waves[i] = new Wave
                {
                    Direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)),
                    Amplitude = amplitude,
                    Wavelength = wavelength,
                    // Deep water: phase speed is sqrt(g / k).
                    Speed = Mathf.Sqrt(Gravity / number) * settings.SpeedScale,
                    Phase = Next(random) * 2f * Mathf.PI,
                    // Shared out so the sum of Q k A over the set is Steepness, at most 1: past
                    // that a crest folds over itself and the sheet turns inside out.
                    Steepness = amplitude > 0f
                        ? Mathf.Clamp01(settings.Steepness) / (number * amplitude * count)
                        : 0f,
                };
            }

            return waves;
        }

        /// <summary>
        /// How far the water surface at (<paramref name="x"/>, <paramref name="z"/>) is moved at
        /// <paramref name="time"/>, scaled by <paramref name="damping"/> (0 in the shallows, 1 out
        /// at sea). The same sum the shader does.
        /// </summary>
        public static Vector3 Displacement(IReadOnlyList<Wave> waves, float x, float z, float time, float damping = 1f)
        {
            var result = Vector3.zero;
            foreach (var wave in waves)
            {
                var number = wave.Number;
                var amplitude = wave.Amplitude * damping;
                var f = number * (wave.Direction.x * x + wave.Direction.y * z - wave.Speed * time) + wave.Phase;
                var cos = Mathf.Cos(f);
                result.x += wave.Steepness * amplitude * wave.Direction.x * cos;
                result.z += wave.Steepness * amplitude * wave.Direction.y * cos;
                result.y += amplitude * Mathf.Sin(f);
            }

            return result;
        }

        /// <summary>
        /// The sum of Q k A over the set. Gerstner crests fold over themselves past 1, so this is
        /// what keeps the sea from turning inside out.
        /// </summary>
        public static float Sharpness(IReadOnlyList<Wave> waves)
        {
            var sum = 0f;
            foreach (var wave in waves)
                sum += wave.Steepness * wave.Number * wave.Amplitude;
            return sum;
        }

        static float Next(System.Random random) => (float)random.NextDouble();
    }
}
