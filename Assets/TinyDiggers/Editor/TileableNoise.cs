using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Periodic noise for tileable textures: every function takes (x, y) in 0..1 across the tile
    /// and a lattice period, and wraps so a tile meets itself exactly. Shared by the terrain and
    /// dam texture generators.
    /// </summary>
    public static class TileableNoise
    {
        /// <summary>Periodic value noise in 0..1; <paramref name="period"/> lattice cells across the tile.</summary>
        public static float Noise(float x, float y, int period)
        {
            var fx = x * period;
            var fy = y * period;
            var x0 = Mathf.FloorToInt(fx);
            var y0 = Mathf.FloorToInt(fy);
            var tx = Smooth(fx - x0);
            var ty = Smooth(fy - y0);
            var a = Hash(x0, y0, period);
            var b = Hash(x0 + 1, y0, period);
            var c = Hash(x0, y0 + 1, period);
            var d = Hash(x0 + 1, y0 + 1, period);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        /// <summary>Several octaves of <see cref="Noise"/>, each twice as fine and half as strong.</summary>
        public static float Fbm(float x, float y, int period, int octaves)
        {
            var sum = 0f;
            var weight = 0f;
            var amplitude = 1f;
            for (var i = 0; i < octaves; i++)
            {
                sum += Noise(x, y, period << i) * amplitude;
                weight += amplitude;
                amplitude *= 0.5f;
            }

            return sum / weight;
        }

        /// <summary>Periodic Worley (cellular) noise: 0 at a feature point, 1 far from every one.</summary>
        public static float Worley(float x, float y, int period)
        {
            var fx = x * period;
            var fy = y * period;
            var cellX = Mathf.FloorToInt(fx);
            var cellY = Mathf.FloorToInt(fy);
            var nearest = float.MaxValue;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var gx = cellX + dx;
                    var gy = cellY + dy;
                    var pointX = gx + Hash(gx, gy, period);
                    var pointY = gy + Hash(gy + 41, gx - 17, period);
                    var deltaX = pointX - fx;
                    var deltaY = pointY - fy;
                    nearest = Mathf.Min(nearest, deltaX * deltaX + deltaY * deltaY);
                }
            }

            return Mathf.Clamp01(Mathf.Sqrt(nearest));
        }

        public static float Smooth(float t) => t * t * (3f - 2f * t);

        /// <summary>A stable 0..1 hash of a lattice point, wrapped so the pattern tiles.</summary>
        public static float Hash(int x, int y, int period)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            var h = x * 374761393 + y * 668265263 + period * 2147483647;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}
