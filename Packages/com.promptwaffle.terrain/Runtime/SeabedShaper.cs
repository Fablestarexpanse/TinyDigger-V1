using System.Threading.Tasks;
using UnityEngine;

namespace PromptWaffle.Terrain
{
    /// <summary>
    /// Gives the open sea floor a shape, where the coast pass leaves it flat at ChannelDepth:
    /// - a broad rise and fall of the deep floor;
    /// - sandbanks: long ridges running one way, the crests coming up to SandbankTop;
    /// - shoals: rounded shallows up to ShoalTop;
    /// - reefs: rough rock crowns on banks and shoals already near the surface, up to about ReefTop.
    /// Nothing breaks the surface (SeabedClearDepth is always left over it), so the sea stays open
    /// and a shoal is the cheap place to reclaim. The features fade in SeabedShoreGap out from the
    /// island's shore and fade out SeabedRimGap in from the dam, so the island's shelf is as it was
    /// and the ships at the terminals keep deep water.
    ///
    /// Its noise has its own random stream, so turning it on leaves the island exactly as it was.
    /// </summary>
    public static class SeabedShaper
    {
        /// <summary>
        /// Shapes the sea cells of <paramref name="heights"/> in place. <paramref name="toLand"/> is
        /// the flood distance in cells from each cell to land. Returns which cells are reef.
        /// </summary>
        public static bool[] Shape(float[] heights, bool[] land, bool[] inDisc, float[] toLand,
            int width, int depth, float radius, Vector2 centre, TerrainGenSettings settings)
        {
            var reef = new bool[heights.Length];
            var random = new System.Random(settings.Seed * 7919 + 1301);
            Vector2 Offset() => new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);
            var floorOffset = Offset();
            var bankOffset = Offset();
            var bankMaskOffset = Offset();
            var shoalOffset = Offset();
            var reefOffset = Offset();
            var roughOffset = Offset();

            var angle = settings.SandbankAngle * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var across = new Vector2(-along.y, along.x);
            var clear = -Mathf.Max(0f, settings.SeabedClearDepth);
            var shoreGap = Mathf.Max(1f, settings.SeabedShoreGap);
            var rimGap = Mathf.Max(1f, settings.SeabedRimGap);

            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell] || land[cell])
                        continue;

                    // In from the shelf and out from the dam, over a gap's width either side.
                    var p = new Vector2(x + 0.5f, z + 0.5f) - centre;
                    var fromShore = Mathf.Clamp01((toLand[cell] - shoreGap) / shoreGap);
                    var fromRim = Mathf.Clamp01((radius - p.magnitude - rimGap) / rimGap);
                    var weight = Smooth(fromShore) * Smooth(fromRim);
                    if (weight <= 0f)
                        continue;

                    var floor = settings.ChannelDepth + (Noise(p, floorOffset, settings.SeabedFeatureSize) - 0.5f) * 2f * settings.SeabedRelief;
                    var shaped = floor;

                    // Sandbanks: folded noise stretched along the bank direction, so its creases
                    // run long and narrow; a second, broad noise decides where banks form at all.
                    var q = new Vector2(Vector2.Dot(p, along) / Mathf.Max(1f, settings.SandbankLength),
                        Vector2.Dot(p, across) / Mathf.Max(1f, settings.SandbankWidth));
                    var crease = 1f - Mathf.Abs(Mathf.PerlinNoise(bankOffset.x + q.x, bankOffset.y + q.y) * 2f - 1f);
                    // The upper part of the crease only, eased: a bank rises 13 m off the floor, and
                    // to keep its sides under about 20 degrees it needs about 40 m either side
                    // (hence the wide default). Sharpening the crease made knife edges with
                    // near-vertical sides.
                    var bankZone = Mathf.Clamp01(Cover(Noise(p, bankMaskOffset, settings.SandbankLength * 1.5f), settings.SandbankCoverage) * 2f);
                    var bank = Smooth(Mathf.Clamp01((crease - 0.3f) / 0.7f)) * bankZone;
                    shaped = Mathf.Max(shaped, Mathf.Lerp(floor, settings.SandbankTop, bank));

                    // Shoals: broad swells that follow the noise all the way up, so their sides stay
                    // gentle; only the highest crests reach ShoalTop.
                    var shoal = Cover(Noise(p, shoalOffset, settings.ShoalSize), settings.ShoalCoverage);
                    shaped = Mathf.Max(shaped, Mathf.Lerp(floor, settings.ShoalTop, Smooth(shoal)));

                    // Reefs: a rough rock crown on ground that is already shallow (a bank or a
                    // shoal within ReefBase of the surface), as real reefs grow. Raised straight off
                    // the deep floor they stood as sheer pillars.
                    var reefness = shaped > settings.ReefBase
                        ? Mathf.Clamp01(Cover(Noise(p, reefOffset, settings.ReefSize), settings.ReefCoverage) * 3f)
                        : 0f;
                    if (reefness > 0f)
                    {
                        var rough = Noise(p, roughOffset, settings.ReefSize * 0.2f);
                        var top = settings.ReefTop - rough * 1.5f;
                        shaped = Mathf.Max(shaped, Mathf.Lerp(shaped, top, Smooth(reefness)));
                        reef[cell] = reefness > 0.5f && weight > 0.5f;
                    }

                    var height = Mathf.Lerp(heights[cell], shaped, weight);
                    heights[cell] = Mathf.Min(height, clear);
                }
            });

            return reef;
        }

        /// <summary>
        /// 0 where a noise value is below the level that leaves about <paramref name="coverage"/>
        /// of the sea above it, rising to 1 at the top of the noise's range (Perlin noise sits
        /// mostly in 0.2..0.8). The rise follows the noise all the way up rather than jumping at
        /// the level, which is what keeps a feature's sides gentle: a narrow ramp at a threshold
        /// made sheer walls, and one wider than the noise's range never reached the top.
        /// </summary>
        static float Cover(float noise, float coverage)
        {
            if (coverage <= 0f)
                return 0f;
            var level = Mathf.Lerp(0.8f, 0.2f, Mathf.Clamp01(coverage));
            return Mathf.Clamp01((noise - level) / Mathf.Max(0.05f, 0.82f - level));
        }

        static float Noise(Vector2 at, Vector2 offset, float size)
        {
            var frequency = 1f / Mathf.Max(1f, size);
            return Mathf.PerlinNoise(offset.x + at.x * frequency, offset.y + at.y * frequency);
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
