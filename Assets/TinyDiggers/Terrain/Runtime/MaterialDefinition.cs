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

        /// <summary>Vertex colour used by the terrain mesh where this material is on top.</summary>
        public Color32 Color { get; }

        /// <summary>0..1. Higher means slower to dig. Unused by the grid itself.</summary>
        public float Hardness { get; }

        /// <summary>Degrees. The steepest stable slope for this material. Unused until collapse exists.</summary>
        public float AngleOfRepose { get; }

        /// <summary>False for bedrock: <see cref="TerrainGrid.Remove"/> will not pop it.</summary>
        public bool IsDiggable { get; }

        public MaterialDefinition(
            MaterialId id,
            string displayName,
            Color32 color,
            float hardness,
            float angleOfRepose,
            bool isDiggable = true)
        {
            Id = id;
            DisplayName = displayName;
            Color = color;
            Hardness = hardness;
            AngleOfRepose = angleOfRepose;
            IsDiggable = isDiggable;
        }

        public override string ToString() => DisplayName;
    }
}
