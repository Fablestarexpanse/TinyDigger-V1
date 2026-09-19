using System;
using System.Text;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Formats a <see cref="MaterialInventory"/> for the debug readout: fill level, free capacity,
    /// and contents top first (the order they will be tipped). Reuses its buffer; ask again only
    /// when <see cref="MaterialInventory.Version"/> changes.
    /// </summary>
    public sealed class InventoryReport
    {
        /// <summary>Free space below this counts as full for display.</summary>
        const float FullWithin = 1e-3f;

        readonly StringBuilder _builder = new StringBuilder(256);

        public string Describe(MaterialInventory inventory, MaterialTable materials)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            if (materials == null)
                throw new ArgumentNullException(nameof(materials));

            _builder.Clear();
            if (inventory.IsEmpty)
                return _builder.Append("Crew load empty (")
                    .Append(inventory.Capacity.ToString("0.0")).Append(" m³ free)").ToString();

            _builder.Append("Crew load ").Append(inventory.Total.ToString("0.0"))
                .Append(" / ").Append(inventory.Capacity.ToString("0.0")).Append(" m³ ");
            if (inventory.Remaining <= FullWithin)
                _builder.Append("- Full");
            else
                _builder.Append('(').Append(inventory.Remaining.ToString("0.0")).Append(" m³ free)");

            var stack = inventory.Stack;
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                _builder.AppendLine()
                    .Append("  ")
                    .Append(materials.Get(stack[i].Material).DisplayName.PadRight(11))
                    .Append(stack[i].Volume.ToString("0.00").PadLeft(7))
                    .Append(" m³");
                if (i == stack.Count - 1)
                    _builder.Append("  tips first");
            }

            return _builder.ToString();
        }
    }
}
