using System;
using System.Text;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Formats one cell for the debug readout: coordinates, surface height, and the layer stack
    /// top to bottom. Reuses its buffers, so the only allocation per call is the returned string;
    /// callers should ask again only when the hovered cell or its contents change.
    /// </summary>
    public sealed class TerrainCellReport
    {
        readonly StringBuilder _builder = new StringBuilder(256);
        readonly Layer[] _layers = new Layer[TerrainGrid.MaxLayersPerCell];

        public string Describe(TerrainGrid grid, int x, int z)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            _builder.Clear();
            if (!grid.InBounds(x, z))
                return "No cell under the cursor";

            _builder.Append("Cell (").Append(x).Append(", ").Append(z).Append(')');
            var water = grid.WaterDepth(x, z);
            if (grid.IsWater(x, z))
                _builder.Append("   Water (depth ").Append(water.ToString("0.0")).Append(" m)");
            else if (water >= 0.05f)
                _builder.Append("   Shallow water (").Append(water.ToString("0.00")).Append(" m)");
            _builder.AppendLine();
            _builder.Append("Surface ").Append(grid.GetSurfaceHeight(x, z).ToString("0.00")).Append(" m").AppendLine();
            _builder.Append("Layers, top to bottom:");

            var count = grid.CopyLayers(x, z, _layers);
            for (var i = 0; i < count; i++)
            {
                var definition = grid.Materials.Get(_layers[i].Material);
                _builder.AppendLine()
                    .Append("  ")
                    .Append(definition.DisplayName.PadRight(11))
                    .Append(_layers[i].Thickness.ToString("0.00").PadLeft(7))
                    .Append(" m");
            }

            return _builder.ToString();
        }
    }
}
