using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>How a stamp's height meets the ground under it.</summary>
    public enum StampBlend
    {
        /// <summary>Added to the ground, so it follows the lie of the land (a mound on a slope).</summary>
        Add,

        /// <summary>Raises the ground to the stamp and never lowers it (a mesa, a bank).</summary>
        Max,

        /// <summary>Lowers the ground to the stamp and never raises it (a crater, a cutting).</summary>
        Min,

        /// <summary>The stamp's surface, eased into the ground at its edge.</summary>
        Replace,
    }

    /// <summary>What a stamp is offered to.</summary>
    [Flags]
    public enum StampUse
    {
        Game = 1,
        Generator = 2,
        Both = Game | Generator,
    }

    /// <summary>
    /// A heightmap shape (Ronan, 2026-09-24: "one library, two uses": the island generator and the
    /// terraform tool place the same assets). Heights are unitless, 0 to 1 over a square, and the
    /// edge eases to nothing over <see cref="EdgeFalloff"/> of the radius so a stamp sits in the
    /// ground instead of on it. How big and how tall it is belongs to where it is placed
    /// (<see cref="StampPlacement"/>); <see cref="NativeSize"/> and <see cref="NativeHeight"/> are
    /// the defaults a placement starts from.
    ///
    /// The heights come from <see cref="Heightmap"/> (a greyscale heightmap from ComfyUI, cleaned
    /// up) and are read into a float array once; tests and bakers set them directly with
    /// <see cref="SetHeights"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Height Stamp")]
    public sealed class HeightStamp : ScriptableObject
    {
        public string DisplayName = "Stamp";

        [Tooltip("Greyscale heightmap, black low, white high. Must be readable.")]
        public Texture2D Heightmap;

        [Tooltip("Metres across at a placement's default size.")]
        public float NativeSize = 40f;

        [Tooltip("Metres at full white, at a placement's default height.")]
        public float NativeHeight = 8f;

        public StampBlend Blend = StampBlend.Add;

        [Range(0f, 1f), Tooltip("The share of the radius, from the rim in, over which the stamp eases to nothing.")]
        public float EdgeFalloff = 0.2f;

        public StampUse Use = StampUse.Both;

        [Tooltip("A hollow rather than a hill (a canyon, a river bed, a sinkhole): the heights are how far below the plain, and it is placed upside down unless flipped.")]
        public bool Negative;

        // The heights read out of the texture, cached. Never serialised: a domain reload brought the
        // cache back as an empty array with the old resolution beside it, and every stamp but the one
        // never sampled threw out of range (2026-09-24).
        [NonSerialized] float[] _heights;
        [NonSerialized] int _resolution;

        /// <summary>Samples a side of the height array.</summary>
        public int Resolution
        {
            get
            {
                EnsureHeights();
                return _resolution;
            }
        }

        /// <summary>Sets the heights directly, row by row from the bottom, <paramref name="resolution"/> a side.</summary>
        public void SetHeights(int resolution, float[] heights)
        {
            if (resolution < 2 || heights == null || heights.Length != resolution * resolution)
                throw new ArgumentException("heights must be resolution × resolution, resolution at least 2");
            _resolution = resolution;
            _heights = heights;
        }

        void EnsureHeights()
        {
            if (_heights != null && _resolution >= 2 && _heights.Length == _resolution * _resolution)
                return;
            if (Heightmap == null)
                throw new InvalidOperationException($"Height stamp '{name}' has no heightmap and no heights");

            // Square only: a stamp is rotated freely, so its corners are never trusted anyway.
            var size = Mathf.Min(Heightmap.width, Heightmap.height);
            var pixels = Heightmap.GetPixels();
            var heights = new float[size * size];
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    heights[z * size + x] = pixels[z * Heightmap.width + x].r;
            _resolution = size;
            _heights = heights;
        }

        /// <summary>
        /// The stamp at <paramref name="uv"/> (0 to 1 across), bilinear: its height, and how much it
        /// counts there, 1 inside the eased rim falling to 0 at the inscribed circle. Outside the
        /// circle both are 0.
        /// </summary>
        public (float Height, float Weight) Sample(Vector2 uv)
        {
            EnsureHeights();
            var weight = Weight(uv);
            if (weight <= 0f)
                return (0f, 0f);

            var fx = Mathf.Clamp01(uv.x) * (_resolution - 1);
            var fz = Mathf.Clamp01(uv.y) * (_resolution - 1);
            var x0 = Mathf.Min((int)fx, _resolution - 2);
            var z0 = Mathf.Min((int)fz, _resolution - 2);
            var tx = fx - x0;
            var tz = fz - z0;
            var row0 = z0 * _resolution;
            var row1 = row0 + _resolution;
            var bottom = Mathf.Lerp(_heights[row0 + x0], _heights[row0 + x0 + 1], tx);
            var top = Mathf.Lerp(_heights[row1 + x0], _heights[row1 + x0 + 1], tx);
            return (Mathf.Lerp(bottom, top, tz), weight);
        }

        /// <summary>1 inside the eased rim, falling smoothly to 0 at the inscribed circle.</summary>
        float Weight(Vector2 uv)
        {
            var d = (uv - new Vector2(0.5f, 0.5f)).magnitude * 2f;
            if (d >= 1f)
                return 0f;
            if (EdgeFalloff <= 0f)
                return 1f;
            var t = Mathf.Clamp01((1f - d) / EdgeFalloff);
            return t * t * (3f - 2f * t);
        }
    }
}
