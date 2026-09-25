using System;
using System.Collections.Generic;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// God mode for stamps (Ronan, 2026-09-24: "a god mode you can enable to use stamps to just place
    /// landmass stamps without having to build it"): the stamp reshapes the ground itself, now, with
    /// no crew and no orders.
    ///
    /// It is landmass, not spoil. A raise thickens the ground the column is made of, so the new
    /// hill is as solid as the old one and the slump leaves it standing; a lowering takes it away
    /// again. Either way the thin topsoil skin stays on top, so a new hill is grassed like its
    /// neighbours rather than a bare scar. Bedrock is the floor, as it is for digging.
    ///
    /// Every column it touches is kept as it was, so the edit can be taken back
    /// (<see cref="Edit.Undo"/>).
    /// </summary>
    public static class GroundStamp
    {
        /// <summary>One god-mode stamp: what it did, and the columns as they were before it.</summary>
        public sealed class Edit
        {
            internal readonly List<(int X, int Z, Layer[] Column)> Before = new List<(int, int, Layer[])>();

            /// <summary>Cells whose surface moved.</summary>
            public int Cells { get; internal set; }

            /// <summary>Metres × cell area added and taken away, in m³.</summary>
            public float Raised { get; internal set; }

            public float Lowered { get; internal set; }

            /// <summary>Puts every column back as it was before the stamp.</summary>
            public void Undo(TerrainGrid grid)
            {
                for (var i = Before.Count - 1; i >= 0; i--)
                {
                    var (x, z, column) = Before[i];
                    grid.SetColumn(x, z, column);
                }
            }
        }

        /// <summary>Stamps the ground itself. Returns what was done, ready to be undone.</summary>
        public static Edit Apply(TerrainGrid grid, HeightStamp stamp, StampPlacement placement)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            // Every target is worked out from the ground as it was, before any of it is changed.
            var targets = new List<(int X, int Z, float Height)>();
            StampRaster.Apply(stamp, placement, grid.CellSize, grid.HeightStep, grid.IsGround, grid.GetSurfaceHeight,
                (x, z, height) => targets.Add((x, z, height)));

            var edit = new Edit();
            var top = new Layer[TerrainGrid.MaxLayersPerCell];
            var column = new Layer[TerrainGrid.MaxLayersPerCell];
            foreach (var (x, z, target) in targets)
            {
                var count = grid.CopyLayers(x, z, top);
                if (count == 0)
                    continue;

                var before = new Layer[count];
                for (var i = 0; i < count; i++)
                    before[i] = top[count - 1 - i];

                var now = grid.GetSurfaceHeight(x, z);
                var change = target - now;
                count = change > 0f ? Raise(grid, top, count, change) : Lower(grid, top, count, -change);

                // Back to bottom first, leaving out anything peeled to nothing.
                var written = 0;
                for (var i = count - 1; i >= 0; i--)
                    if (top[i].Thickness > 1e-4f)
                        column[written++] = top[i];
                if (written == 0)
                    continue;

                grid.SetColumn(x, z, new ReadOnlySpan<Layer>(column, 0, written));
                var moved = grid.GetSurfaceHeight(x, z) - now;
                if (Math.Abs(moved) < 1e-4f)
                    continue;

                edit.Before.Add((x, z, before));
                edit.Cells++;
                if (moved > 0f)
                    edit.Raised += moved * grid.CellArea;
                else
                    edit.Lowered -= moved * grid.CellArea;
            }

            return edit;
        }

        /// <summary>The layer a stamp builds in: the one under a topsoil skin, or the top one.</summary>
        static int Body(Layer[] top, int count) => count > 1 && top[0].Material == MaterialTable.Topsoil ? 1 : 0;

        /// <summary>Thickens the body layer by <paramref name="metres"/>. The column is top first.</summary>
        static int Raise(TerrainGrid grid, Layer[] top, int count, float metres)
        {
            var body = Body(top, count);
            top[body] = new Layer(top[body].Material, top[body].Thickness + metres);
            return count;
        }

        /// <summary>
        /// Takes <paramref name="metres"/> out from under the skin, layer by layer, stopping at bedrock.
        /// Once the body is gone the skin itself goes, so nothing floats over a hole.
        /// </summary>
        static int Lower(TerrainGrid grid, Layer[] top, int count, float metres)
        {
            var skin = Body(top, count) == 1 ? 1 : 0;
            for (var i = skin; i < count && metres > 1e-4f; i++)
            {
                if (top[i].Material == MaterialTable.Bedrock)
                    break;
                var take = Math.Min(metres, top[i].Thickness);
                top[i] = new Layer(top[i].Material, top[i].Thickness - take);
                metres -= take;
            }

            if (skin == 1 && metres > 1e-4f)
            {
                var take = Math.Min(metres, top[0].Thickness);
                top[0] = new Layer(top[0].Material, top[0].Thickness - take);
            }

            return count;
        }
    }
}
