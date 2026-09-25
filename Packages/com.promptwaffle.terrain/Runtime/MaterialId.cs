using System;

namespace PromptWaffle.Terrain
{
    /// <summary>
    /// Identifies a terrain material. Wraps a byte so that a layer stays small enough to keep
    /// whole chunks of the grid in cache.
    /// </summary>
    public readonly struct MaterialId : IEquatable<MaterialId>
    {
        /// <summary>Reserved value meaning "no material", used for empty layer slots.</summary>
        public static readonly MaterialId None = new MaterialId(0);

        public readonly byte Value;

        public MaterialId(byte value)
        {
            Value = value;
        }

        public bool IsNone => Value == 0;

        public bool Equals(MaterialId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is MaterialId other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(MaterialId a, MaterialId b) => a.Value == b.Value;

        public static bool operator !=(MaterialId a, MaterialId b) => a.Value != b.Value;
    }
}
