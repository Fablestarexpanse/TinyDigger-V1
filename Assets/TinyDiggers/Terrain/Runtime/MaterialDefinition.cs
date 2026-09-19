using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Static description of one terrain material. Immutable; one instance per material, shared
    /// by every cell that contains it.
    /// </summary>
    public sealed class MaterialDefinition
    {
        public MaterialId Id { get; }

        public string DisplayName { get; }

        /// <summary>Vertex colour used by the terrain mesh where this material shows.</summary>
        public Color32 Color { get; }

        /// <summary>0..1. Higher means slower to dig. Unused by the grid itself.</summary>
        public float Hardness { get; }

        /// <summary>
        /// Degrees. The steepest slope this material holds before it slumps. 90 or more means it
        /// never slumps.
        /// </summary>
        public float AngleOfRepose { get; }

        /// <summary>False for bedrock: <see cref="TerrainGrid.Remove"/> will not pop it.</summary>
        public bool IsDiggable { get; }

        /// <summary>
        /// What this material becomes once dug up or tipped: rock turns into loose rock, and so on.
        /// <see cref="MaterialId.None"/> means it stays itself.
        /// </summary>
        public MaterialId Disturbed { get; }

        /// <summary>
        /// Loose volume per unit of in-place volume dug out: rock swells to about 1.5x once
        /// broken up. 1 for material that is already loose.
        /// </summary>
        public float BulkingFactor { get; }

        /// <summary>
        /// Already-loose material (spoil, sand). Only loose material gets the thin-layer bias
        /// that lets a skin of it cling to a slope; undisturbed ground fails on its angle alone.
        /// </summary>
        public bool IsLoose { get; }

        public MaterialDefinition(
            MaterialId id,
            string displayName,
            Color32 color,
            float hardness,
            float angleOfRepose,
            bool isDiggable = true,
            MaterialId disturbed = default,
            float bulkingFactor = 1f,
            bool isLoose = false)
        {
            if (!(bulkingFactor >= 1f))
                throw new System.ArgumentOutOfRangeException(nameof(bulkingFactor), "Digging never shrinks material.");

            Id = id;
            DisplayName = displayName;
            Color = color;
            Hardness = hardness;
            AngleOfRepose = angleOfRepose;
            IsDiggable = isDiggable;
            Disturbed = disturbed;
            BulkingFactor = bulkingFactor;
            IsLoose = isLoose;
        }

        public override string ToString() => DisplayName;
    }
}
