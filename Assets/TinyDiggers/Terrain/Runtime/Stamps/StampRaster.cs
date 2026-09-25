using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Where one stamp goes and how big: the same for the generator and the terraform tool.
    /// </summary>
    [Serializable]
    public struct StampPlacement
    {
        /// <summary>The middle, in cells (x, z), fractional.</summary>
        public Vector2 Centre;

        /// <summary>Metres across.</summary>
        public float Size;

        /// <summary>Metres at full white.</summary>
        public float Height;

        /// <summary>Degrees, clockwise seen from above.</summary>
        public float Rotation;

        /// <summary>Upside down: a mound becomes a hollow.</summary>
        public bool Invert;

        /// <summary>
        /// The level the stamp stands on, in metres, for <see cref="StampBlend.Max"/>,
        /// <see cref="StampBlend.Min"/> and <see cref="StampBlend.Replace"/>; <see cref="StampBlend.Add"/>
        /// follows the ground instead.
        /// </summary>
        public float Base;

        public static StampPlacement Default(HeightStamp stamp, Vector2 centre, float groundAtCentre) => new StampPlacement
        {
            Centre = centre,
            Size = stamp.NativeSize,
            Height = stamp.NativeHeight,
            Invert = stamp.Negative,
            Base = groundAtCentre,
        };
    }

    /// <summary>
    /// Turns a placed stamp into target heights per cell, without touching the grid: the generator
    /// writes them into its height field, the terraform tool turns them into dig and fill for the
    /// crew. Targets are rounded to the grid's height step, as every other planned height is, and
    /// only cells whose target differs from the ground by at least half a step are given.
    /// </summary>
    public static class StampRaster
    {
        /// <summary>
        /// Calls <paramref name="emit"/>(x, z, target) for every cell the stamp changes. The cell
        /// size and height step are the grid's; <paramref name="ground"/> is the height now, and
        /// <paramref name="inBounds"/> keeps it on the map. Returns how many cells it gave.
        /// </summary>
        public static int Apply(HeightStamp stamp, StampPlacement placement, float cellSize, float heightStep,
            Func<int, int, bool> inBounds, Func<int, int, float> ground, Action<int, int, float> emit)
        {
            if (stamp == null || placement.Size <= 0f || cellSize <= 0f)
                return 0;

            var across = placement.Size / cellSize;
            // The inscribed circle is all that is ever non-zero, so its box is enough whatever the rotation.
            var reach = across * 0.5f;
            var minX = Mathf.FloorToInt(placement.Centre.x - reach);
            var maxX = Mathf.CeilToInt(placement.Centre.x + reach);
            var minZ = Mathf.FloorToInt(placement.Centre.y - reach);
            var maxZ = Mathf.CeilToInt(placement.Centre.y + reach);

            var radians = placement.Rotation * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            var sign = placement.Invert ? -1f : 1f;
            var given = 0;

            for (var z = minZ; z <= maxZ; z++)
                for (var x = minX; x <= maxX; x++)
                {
                    if (!inBounds(x, z))
                        continue;

                    // Into the stamp's own frame: undo the rotation, then 0..1 across.
                    var dx = x + 0.5f - placement.Centre.x;
                    var dz = z + 0.5f - placement.Centre.y;
                    var u = (dx * cos - dz * sin) / across + 0.5f;
                    var v = (dx * sin + dz * cos) / across + 0.5f;
                    var (height, weight) = stamp.Sample(new Vector2(u, v));
                    if (weight <= 0f)
                        continue;

                    var now = ground(x, z);
                    var target = Blend(stamp.Blend, now, placement.Base, sign * height * placement.Height, weight);
                    if (heightStep > 0f)
                        target = Mathf.Round(target / heightStep) * heightStep;
                    if (Mathf.Abs(target - now) < heightStep * 0.5f || Mathf.Approximately(target, now))
                        continue;

                    emit(x, z, target);
                    given++;
                }

            return given;
        }

        /// <summary>
        /// The target for one cell: the ground <paramref name="now"/>, the stamp's rise there
        /// <paramref name="rise"/> in metres, and how much the stamp counts there
        /// <paramref name="weight"/>. Every blend eases from the ground by the weight, so none of them
        /// leaves a step at the rim.
        /// </summary>
        public static float Blend(StampBlend blend, float now, float baseLevel, float rise, float weight)
        {
            var surface = baseLevel + rise;
            switch (blend)
            {
                case StampBlend.Max:
                    return Mathf.Lerp(now, Mathf.Max(now, surface), weight);
                case StampBlend.Min:
                    return Mathf.Lerp(now, Mathf.Min(now, surface), weight);
                case StampBlend.Replace:
                    return Mathf.Lerp(now, surface, weight);
                default:
                    return now + rise * weight;
            }
        }
    }
}
