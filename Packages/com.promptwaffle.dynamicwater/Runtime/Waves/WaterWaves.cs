using System.Collections.Generic;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>One Gerstner wave: a direction on the map, a height, a length and a shape.</summary>
    public struct GerstnerWave
    {
        /// <summary>Unit vector on the map (x, z) the crests travel along.</summary>
        public Vector2 Direction;

        /// <summary>Metres above still water at the crest.</summary>
        public float Amplitude;

        /// <summary>Metres from one crest to the next.</summary>
        public float Wavelength;

        /// <summary>Gerstner sharpness, shared out so the sum over the set can never fold a crest over itself.</summary>
        public float Steepness;

        /// <summary>Metres per second the crests move.</summary>
        public float Speed;

        /// <summary>Radians, so no two waves start in step.</summary>
        public float Phase;

        public float Number => 2f * Mathf.PI / Wavelength;
    }

    /// <summary>
    /// Turns <see cref="WaterWaveSettings"/> into a wave set, and evaluates it on the CPU exactly as
    /// DynamicWaterSurface does on the GPU, so floating objects ride what is drawn.
    ///
    /// The set always holds one wave near the top of the height range and one near the bottom;
    /// the rest lean small, as a sea is mostly small waves with the odd big one. Long waves are the
    /// tall ones, and no crest stands more than a fourteenth of its length above still water (real
    /// water breaks past a seventh, trough to crest).
    /// </summary>
    public static class WaterWaves
    {
        public const int MaxWaves = 8;
        public const float Gravity = 9.81f;
        public const float BreakingRatio = 1f / 14f;

        public static GerstnerWave[] Generate(WaterWaveSettings settings)
        {
            var count = Mathf.Clamp(settings.WaveCount, 1, MaxWaves);
            var random = new System.Random(settings.Seed);
            var waves = new GerstnerWave[count];

            var minLength = Mathf.Max(1f, Mathf.Min(settings.MinWavelength, settings.MaxWavelength));
            var maxLength = Mathf.Max(minLength, settings.MaxWavelength);
            var minHeight = Mathf.Max(0f, Mathf.Min(settings.MinAmplitude, settings.MaxAmplitude));
            var maxHeight = Mathf.Max(minHeight, settings.MaxAmplitude);
            var wind = settings.WindDegrees * Mathf.Deg2Rad;

            for (var i = 0; i < count; i++)
            {
                float share;
                if (i == 0)
                    share = 0.85f + 0.15f * Next(random);
                else if (i == 1)
                    share = 0.15f * Next(random);
                else
                    share = Mathf.Pow(Next(random), 1.6f);

                var lengthShare = Mathf.Clamp01(share * 0.7f + Next(random) * 0.3f);
                var wavelength = Mathf.Exp(Mathf.Lerp(Mathf.Log(minLength), Mathf.Log(maxLength), lengthShare));
                var amplitude = Mathf.Min(Mathf.Lerp(minHeight, maxHeight, share), wavelength * BreakingRatio);
                var angle = wind + (Next(random) * 2f - 1f) * settings.Spread * Mathf.Deg2Rad;
                var number = 2f * Mathf.PI / wavelength;

                waves[i] = new GerstnerWave
                {
                    Direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)),
                    Amplitude = amplitude,
                    Wavelength = wavelength,
                    // Deep water: phase speed is sqrt(g / k).
                    Speed = Mathf.Sqrt(Gravity / number) * settings.SpeedScale,
                    Phase = Next(random) * 2f * Mathf.PI,
                    Steepness = amplitude > 0f ? Mathf.Clamp01(settings.Steepness) / (number * amplitude * count) : 0f,
                };
            }

            return waves;
        }

        /// <summary>
        /// How much of the swell survives where the water is <paramref name="depth"/> deep and the
        /// wind patch at (x, z) allows: none in the shallows, full in deep water in a rough patch.
        /// Matches DynamicWaterSurface.
        /// </summary>
        public static float Damping(WaterWaveSettings settings, float depth, float x, float z)
        {
            var shallow = Mathf.Clamp01(depth / Mathf.Max(0.01f, settings.DampDepth));
            var gust = Mathf.Lerp(settings.GustCalm, 1f, Gust(x / Mathf.Max(1f, settings.GustSize), z / Mathf.Max(1f, settings.GustSize)));
            return shallow * gust;
        }

        /// <summary>How far the surface at (x, z) is moved at <paramref name="time"/>, scaled by <paramref name="damping"/>.</summary>
        public static Vector3 Displacement(IReadOnlyList<GerstnerWave> waves, float x, float z, float time, float damping = 1f)
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

        /// <summary>The sum of Q k A over the set; Gerstner crests fold over themselves past 1.</summary>
        public static float Sharpness(IReadOnlyList<GerstnerWave> waves)
        {
            var sum = 0f;
            foreach (var wave in waves)
                sum += wave.Steepness * wave.Number * wave.Amplitude;
            return sum;
        }

        /// <summary>Packs the set for the shader: A = (dir x, dir z, amplitude, number), B = (steepness, speed, phase, 0).</summary>
        public static void Pack(IReadOnlyList<GerstnerWave> waves, Vector4[] a, Vector4[] b)
        {
            for (var i = 0; i < MaxWaves; i++)
            {
                if (i < waves.Count)
                {
                    var w = waves[i];
                    a[i] = new Vector4(w.Direction.x, w.Direction.y, w.Amplitude, w.Number);
                    b[i] = new Vector4(w.Steepness, w.Speed, w.Phase, 0f);
                }
                else
                {
                    a[i] = Vector4.zero;
                    b[i] = Vector4.zero;
                }
            }
        }

        /// <summary>Smooth value noise in 0..1, the same hash as the shader's so patches match.</summary>
        public static float Gust(float x, float z)
        {
            var ix = Mathf.Floor(x);
            var iz = Mathf.Floor(z);
            var fx = x - ix;
            var fz = z - iz;
            fx = fx * fx * (3f - 2f * fx);
            fz = fz * fz * (3f - 2f * fz);
            var a = Hash(ix, iz);
            var b = Hash(ix + 1f, iz);
            var c = Hash(ix, iz + 1f);
            var d = Hash(ix + 1f, iz + 1f);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
        }

        static float Hash(float x, float z)
        {
            var px = Frac(x * 123.34f);
            var pz = Frac(z * 456.21f);
            var dot = px * (px + 45.32f) + pz * (pz + 45.32f);
            px += dot;
            pz += dot;
            return Frac(px * pz);
        }

        static float Frac(float v) => v - Mathf.Floor(v);

        static float Next(System.Random random) => (float)random.NextDouble();
    }
}
