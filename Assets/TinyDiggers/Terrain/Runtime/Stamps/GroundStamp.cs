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
    /// Raised land takes the surface of the land round the stamp — the commonest top material on its
    /// rim. A column whose own top is something else (a river's gravel bed, a patch of beach) gets a
    /// skin of that surface when it comes up, so a hill stamped across a river is grassed over it
    /// rather than striped grey (2026-09-24), and one stamped in the sea is the sea's own ground.
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

            /// <summary>
            /// Each cell whose surface moved, and by how much (up positive). What the water needs to
            /// know: land raised out of a sea must not carry the sea up with it.
            /// </summary>
            public readonly List<(int X, int Z, float Change)> Changes = new List<(int, int, float)>();

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

            var surface = RimSurface(grid, placement);
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
                count = change > 0f ? Raise(top, count, change, surface) : Lower(top, count, -change);

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
                edit.Changes.Add((x, z, moved));
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

        /// <summary>Metres of skin a raise lays when the column's own top is not the land's surface.</summary>
        const float Skin = 0.5f;

        /// <summary>
        /// The land's surface round the stamp: the commonest top material on its rim, sampled every
        /// 7.5°. None when the rim is off the map.
        /// </summary>
        static MaterialId RimSurface(TerrainGrid grid, StampPlacement placement)
        {
            var counts = new Dictionary<MaterialId, int>();
            var radius = placement.Size * 0.5f / grid.CellSize;
            for (var i = 0; i < 48; i++)
            {
                var angle = i * Math.PI * 2.0 / 48;
                var x = (int)Math.Floor(placement.Centre.x + Math.Cos(angle) * radius);
                var z = (int)Math.Floor(placement.Centre.y + Math.Sin(angle) * radius);
                if (!grid.IsGround(x, z) || grid.GetLayerCount(x, z) == 0)
                    continue;
                var material = grid.GetTopMaterial(x, z);
                counts[material] = counts.TryGetValue(material, out var n) ? n + 1 : 1;
            }

            var best = MaterialId.None;
            var most = 0;
            foreach (var pair in counts)
                if (pair.Value > most)
                {
                    most = pair.Value;
                    best = pair.Key;
                }

            return best;
        }

        /// <summary>
        /// Raises the column by <paramref name="metres"/>, top first. Where its top is already the
        /// land's surface (or there is none to match), the body layer thickens. Otherwise the rise is
        /// built as the land is: a skin of the surface over a body under it — dirt under topsoil,
        /// the surface itself under anything else.
        /// </summary>
        static int Raise(Layer[] top, int count, float metres, MaterialId surface)
        {
            if (surface.IsNone || top[0].Material == surface || metres < Skin || count + 2 > top.Length)
            {
                var body = Body(top, count);
                top[body] = new Layer(top[body].Material, top[body].Thickness + metres);
                return count;
            }

            for (var i = count - 1; i >= 0; i--)
                top[i + 2] = top[i];
            top[0] = new Layer(surface, Skin);
            top[1] = new Layer(surface == MaterialTable.Topsoil ? MaterialTable.Dirt : surface, metres - Skin);
            return count + 2;
        }

        /// <summary>
        /// Takes <paramref name="metres"/> out from under the skin, layer by layer, stopping at bedrock.
        /// Once the body is gone the skin itself goes, so nothing floats over a hole.
        /// </summary>
        static int Lower(Layer[] top, int count, float metres)
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
