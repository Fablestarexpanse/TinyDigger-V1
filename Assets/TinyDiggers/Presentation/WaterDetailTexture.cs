using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The ripple texture for the water, made here rather than imported: a tileable field of
    /// rounded noise, with its normal in red and green and its height in blue. The shader scrolls
    /// two copies of it across each other for ripples, and uses the height for foam and caustics.
    ///
    /// It tiles because every octave of noise repeats on a whole number of lattice cells across the
    /// texture. Mipmapped and trilinear, so in the distance it softens rather than aliasing into a
    /// grid, which is what the old sine ripples did.
    /// </summary>
    public static class WaterDetailTexture
    {
        public static Texture2D Create(int size = 256, int seed = 1)
        {
            var heights = Heights(size, seed);
            var pixels = new Color32[size * size];
            // Ripples read as ripples when their slopes are strong; this scales the finite
            // difference to roughly unit slope at the steepest point.
            const float Strength = 3.5f;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var left = heights[y * size + (x + size - 1) % size];
                    var right = heights[y * size + (x + 1) % size];
                    var down = heights[(y + size - 1) % size * size + x];
                    var up = heights[(y + 1) % size * size + x];
                    var normal = new Vector3((left - right) * Strength * size / 64f, 1f, (down - up) * Strength * size / 64f).normalized;
                    pixels[y * size + x] = new Color32(
                        (byte)Mathf.RoundToInt((normal.x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((normal.z * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(heights[y * size + x]) * 255f),
                        255);
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "WaterDetail",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontSave,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>Tileable fBm in 0..1: octaves of value noise on lattices that divide the texture.</summary>
        static float[] Heights(int size, int seed)
        {
            var heights = new float[size * size];
            var min = float.MaxValue;
            var max = float.MinValue;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = 0f;
                    var amplitude = 1f;
                    var cells = 4;
                    for (var octave = 0; octave < 5; octave++)
                    {
                        // Ridged a little: water ripples have sharper crests than troughs.
                        var n = Noise((float)x / size * cells, (float)y / size * cells, cells, seed + octave * 131);
                        sum += (1f - Mathf.Abs(n * 2f - 1f)) * amplitude;
                        amplitude *= 0.5f;
                        cells *= 2;
                    }

                    heights[y * size + x] = sum;
                    min = Mathf.Min(min, sum);
                    max = Mathf.Max(max, sum);
                }
            }

            var range = Mathf.Max(1e-5f, max - min);
            for (var i = 0; i < heights.Length; i++)
                heights[i] = (heights[i] - min) / range;
            return heights;
        }

        /// <summary>Value noise on a lattice that wraps every <paramref name="period"/> cells.</summary>
        static float Noise(float x, float y, int period, int seed)
        {
            var ix = Mathf.FloorToInt(x);
            var iy = Mathf.FloorToInt(y);
            var fx = x - ix;
            var fy = y - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            var a = Hash(ix, iy, period, seed);
            var b = Hash(ix + 1, iy, period, seed);
            var c = Hash(ix, iy + 1, period, seed);
            var d = Hash(ix + 1, iy + 1, period, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        static float Hash(int x, int y, int period, int seed)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            unchecked
            {
                var h = (uint)(x * 374761393 + y * 668265263 + seed * 144665);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }
    }
}
