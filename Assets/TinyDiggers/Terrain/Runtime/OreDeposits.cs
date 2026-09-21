using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>How one ore is laid down. Depths are metres below the surface; sizes are metres.</summary>
    [Serializable]
    public sealed class OreSpec
    {
        [Tooltip("0 none, 1 everywhere: how much of the map the ore's patches cover.")]
        [Range(0f, 1f)] public float Abundance = 0.3f;

        [Tooltip("Metres across a typical patch.")]
        [Min(4f)] public float PatchSize = 60f;

        [Tooltip("Metres below the surface the ore starts at its shallowest.")]
        [Min(0f)] public float DepthMin = 4f;

        [Tooltip("Metres below the surface the ore starts at its deepest.")]
        [Min(0f)] public float DepthMax = 14f;

        [Tooltip("Metres thick at the heart of a patch.")]
        [Min(0.5f)] public float MaxThickness = 3f;

        public OreSpec() { }

        public OreSpec(float abundance, float patchSize, float depthMin, float depthMax, float maxThickness)
        {
            Abundance = abundance;
            PatchSize = patchSize;
            DepthMin = depthMin;
            DepthMax = depthMax;
            MaxThickness = maxThickness;
        }
    }

    /// <summary>
    /// Ore underground (slice 10): turns stone inside each ore's patches and depth window into ore,
    /// column by column, after the strata are built. Only rock, granite and bedrock are converted, and
    /// a layer is split rather than moved, so the surface height never changes and the land keeps
    /// every shape the generator gave it. Soil and sand sit on top as before, so ore is buried under
    /// grassland and only crops out where rock is bare: on cliffs and ridges.
    ///
    /// Each ore has its own habit:
    /// - Coal: a seam at one height across the island, gently undulating, so a cliff cut shows it
    ///   as a band.
    /// - Limestone: thick, shallow beds under the lowlands.
    /// - Iron ore: lenses in the rock under hills, at a varying depth.
    /// - Copper ore: small rich bodies near the ridges.
    /// </summary>
    public static class OreDeposits
    {
        /// <summary>The noise offsets for one island; drawn from the island's seed.</summary>
        public struct Fields
        {
            public Vector2 Coal, Iron, Copper, Limestone, Depth;

            public static Fields Draw(System.Random random)
            {
                Vector2 Next() => new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);
                return new Fields { Coal = Next(), Iron = Next(), Copper = Next(), Limestone = Next(), Depth = Next() };
            }
        }

        /// <summary>
        /// Converts stone in <paramref name="column"/> (bottom first, <paramref name="count"/>
        /// layers, base at <paramref name="datum"/>) into ore where this cell lies in a deposit.
        /// <paramref name="high"/> is how high-ground the cell is (0..1).
        /// Never lets the column exceed <paramref name="column"/>'s length.
        /// </summary>
        public static void Apply(Span<Layer> column, ref int count, float x, float z, float surface, float datum,
            float high, Fields fields, TerrainGenSettings settings)
        {
            // Under the sea there is nobody to dig it, and the budget is half a second.
            if (!settings.Ores || surface < World.SeaLevel)
                return;

            var at = new Vector2(x, z);
            var wander = -1f;
            float Wander() => wander >= 0f ? wander : wander = Noise(at, fields.Depth, 50f / settings.GenerationCellSize);

            // Coal: one seam height for the island, undulating a few metres.
            var coal = Strength(at, fields.Coal, settings.CoalOre);
            if (coal > 0f)
            {
                var wanderAt = Wander();
                var top = settings.CoalSeamHeight + (wanderAt - 0.5f) * 6f;
                if (surface - top >= settings.CoalOre.DepthMin)
                    Convert(column, ref count, datum, top - Thickness(coal, settings.CoalOre), top, MaterialTable.Coal);
            }

            // Limestone: shallow beds, strongest under the lowlands.
            var lowland = Mathf.Clamp01(1f - surface / Mathf.Max(1f, settings.LimestoneBelowHeight));
            var limestone = lowland > 0.05f ? Strength(at, fields.Limestone, settings.LimestoneOre) * lowland : 0f;
            if (limestone > 0.05f)
                ConvertAtDepth(column, ref count, datum, surface, Wander(), limestone, settings.LimestoneOre, MaterialTable.Limestone);

            // Iron: lenses under hills at a varying depth.
            var iron = Strength(at, fields.Iron, settings.IronOre);
            if (iron > 0f)
                ConvertAtDepth(column, ref count, datum, surface, 1f - Wander(), iron, settings.IronOre, MaterialTable.IronOre);

            // Copper: small bodies near the ridges.
            var ridge = Mathf.Clamp01(high * 2f);
            var copper = ridge > 0.05f ? Strength(at, fields.Copper, settings.CopperOre) * ridge : 0f;
            if (copper > 0.05f)
                ConvertAtDepth(column, ref count, datum, surface, Wander(), copper, settings.CopperOre, MaterialTable.CopperOre);
        }

        /// <summary>0 outside a patch, rising to 1 at its heart.</summary>
        public static float Strength(Vector2 at, Vector2 offset, OreSpec spec)
        {
            if (spec.Abundance <= 0f)
                return 0f;
            var threshold = 1f - spec.Abundance;
            var coarse = Noise(at, offset, spec.PatchSize) * 0.75f;
            // The fine octave adds at most 0.25: skip it where the patch cannot reach the threshold,
            // which is most of the map.
            if (coarse + 0.25f <= threshold)
                return 0f;
            var n = coarse + Noise(at, offset + new Vector2(131f, 71f), spec.PatchSize * 0.35f) * 0.25f;
            return Mathf.Clamp01((n - threshold) / Mathf.Max(0.05f, 1f - threshold) * 1.6f);
        }

        static float Thickness(float strength, OreSpec spec) => Mathf.Max(0.5f, strength * spec.MaxThickness);

        static void ConvertAtDepth(Span<Layer> column, ref int count, float datum, float surface, float depthNoise,
            float strength, OreSpec spec, MaterialId ore)
        {
            var top = surface - Mathf.Lerp(spec.DepthMin, Mathf.Max(spec.DepthMin, spec.DepthMax), depthNoise);
            Convert(column, ref count, datum, top - Thickness(strength, spec), top, ore);
        }

        /// <summary>
        /// Turns the rock, granite and bedrock between heights <paramref name="bottom"/> and
        /// <paramref name="top"/> into <paramref name="ore"/>, splitting layers as needed.
        /// Does nothing if the split would overflow the column.
        /// </summary>
        public static void Convert(Span<Layer> column, ref int count, float datum, float bottom, float top, MaterialId ore)
        {
            if (top - bottom < 0.25f)
                return;

            Span<Layer> result = stackalloc Layer[column.Length];
            var written = 0;
            var baseHeight = datum;
            for (var i = 0; i < count; i++)
            {
                var layer = column[i];
                var layerTop = baseHeight + layer.Thickness;
                // Bedrock too: under grassland the rock is only a few metres thick, and a lens
                // that stopped at the rock would barely exist. Ore is diggable, so a crew reaches it
                // by digging down through the rock above.
                var convertible = layer.Material == MaterialTable.Rock || layer.Material == MaterialTable.Granite
                    || layer.Material == MaterialTable.Bedrock;
                var from = Mathf.Max(bottom, baseHeight);
                var to = Mathf.Min(top, layerTop);
                if (!convertible || to - from < 0.25f)
                {
                    if (!Push(result, ref written, layer))
                        return;
                }
                else
                {
                    if (!Push(result, ref written, new Layer(layer.Material, from - baseHeight))
                        || !Push(result, ref written, new Layer(ore, to - from))
                        || !Push(result, ref written, new Layer(layer.Material, layerTop - to)))
                        return;
                }

                baseHeight = layerTop;
            }

            result.Slice(0, written).CopyTo(column);
            count = written;
        }

        /// <summary>
        /// Appends a layer, merging with the one below if it is the same; false if full. A sliver
        /// under 5 cm joins the layer below rather than being dropped: dropping it lowered the
        /// surface by the sliver and moved the land off the height step.
        /// </summary>
        static bool Push(Span<Layer> layers, ref int count, Layer layer)
        {
            if (layer.Thickness <= 0f)
                return true;
            if (layer.Thickness < 0.05f && count > 0)
            {
                layers[count - 1] = new Layer(layers[count - 1].Material, layers[count - 1].Thickness + layer.Thickness);
                return true;
            }

            if (count > 0 && layers[count - 1].Material == layer.Material)
            {
                layers[count - 1] = new Layer(layer.Material, layers[count - 1].Thickness + layer.Thickness);
                return true;
            }

            if (count >= layers.Length)
                return false;
            layers[count++] = layer;
            return true;
        }

        static float Noise(Vector2 at, Vector2 offset, float size) =>
            Mathf.PerlinNoise(at.x / size + offset.x, at.y / size + offset.y);
    }
}
