using System;
using System.Collections.Generic;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Moving material between the terrain and a <see cref="MaterialInventory"/>: the two things
    /// a digger does. Stateless; whoever owns the inventory (a <see cref="CrewUnit"/>) calls these.
    ///
    /// Digging moves material out of the ground into the inventory. Dug material comes out
    /// bulked, so the load fills faster than the hole grows. Nothing is dug that would not fit:
    /// each cell is previewed first, and skipped if its loose output would overflow the load.
    ///
    /// Tipping empties the load onto one cell, newest material first, in whole height steps.
    /// Anything less than a step stays in the load until the next dig tops it up.
    /// </summary>
    public static class Excavation
    {
        /// <summary>Slack for comparing sums of bulked floats.</summary>
        const float Epsilon = 1e-4f;

        /// <summary>
        /// Digs up to <paramref name="depthPerCell"/> metres (in place) from each cell within
        /// <paramref name="radius"/> of the centre, nearest cells first, into the inventory.
        /// The grid holds metres of thickness; the inventory and the report hold m³, which is
        /// thickness times <see cref="TerrainGrid.CellArea"/>.
        /// Cells whose loose output would not fit in what is left are skipped, not part-dug.
        /// </summary>
        public static DigReport Dig(TerrainGrid grid, MaterialInventory inventory, int centreX, int centreZ, int radius, float depthPerCell)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            if (radius < 0)
                throw new ArgumentOutOfRangeException(nameof(radius));

            Span<Layer> preview = stackalloc Layer[TerrainGrid.MaxLayersPerCell];
            Span<MaterialVolume> removed = stackalloc MaterialVolume[TerrainGrid.MaxLayersPerCell];
            var report = new DigReport(grid.Materials.MaxId + 1);
            var area = grid.CellArea;
            foreach (var (dx, dz) in DiscNearestFirst(radius))
            {
                var x = centreX + dx;
                var z = centreZ + dz;
                if (!grid.InBounds(x, z))
                    continue;

                var layers = grid.PeekRemove(x, z, depthPerCell, preview);
                if (layers == 0)
                {
                    report.CellsAtBedrock++;
                    continue;
                }

                var loose = 0f;
                for (var i = 0; i < layers; i++)
                    loose += preview[i].Thickness * area * grid.Materials.Get(preview[i].Material).BulkingFactor;
                if (!inventory.CanFit(loose))
                {
                    report.CellsThatDidNotFit++;
                    report.SmallestMisfit = Math.Min(report.SmallestMisfit, loose);
                    continue;
                }

                for (var i = 0; i < layers; i++)
                {
                    report.InPlaceBySource[preview[i].Material.Value] += preview[i].Thickness * area;
                    report.InPlace += preview[i].Thickness * area;
                }

                var pieces = grid.Remove(x, z, depthPerCell, removed);
                for (var i = 0; i < pieces; i++)
                {
                    // The preview said it fits; tiny float differences are absorbed by TryAdd's slack.
                    var volume = removed[i].Volume * area;
                    inventory.Add(removed[i].Material, volume);
                    report.Loose += volume;
                }

                report.CellsDug++;
            }

            return report;
        }

        /// <summary>
        /// Tips as many whole height steps of the load as it holds, up to
        /// <paramref name="maxVolume"/> m³, onto cell (x, z) in one go. One step on the cell is
        /// <see cref="TerrainGrid.HeightStep"/> times <see cref="TerrainGrid.CellArea"/> m³. The steps are made up from the
        /// top of the load downwards, and one step may mix several materials: a load of small
        /// interleaved pieces still tips. Within one tip the load is mixed into one layer per landed
        /// material, newest material lowest, so a tip adds only as many layers as it has distinct
        /// materials. Whatever is left stays in the load, oldest material. Nothing is tipped if the
        /// cell's layer stack has no room.
        /// </summary>
        /// <summary>
        /// Moves up to <paramref name="volume"/> m³ from the top of <paramref name="from"/> into
        /// <paramref name="to"/>, stopping when the receiver is full: a digger emptying its scoop
        /// into a hauler. Volume is conserved, and the material keeps its identity.
        /// Returns how much moved.
        /// </summary>
        public static float Transfer(MaterialInventory from, MaterialInventory to, float volume)
        {
            if (from == null)
                throw new ArgumentNullException(nameof(from));
            if (to == null)
                throw new ArgumentNullException(nameof(to));
            if (!(volume > 0f))
                return 0f;

            const float epsilon = 1e-4f;
            var moved = 0f;
            while (moved + epsilon < volume && to.Remaining > epsilon && from.TryPeekTop(out var top))
            {
                var take = Math.Min(Math.Min(volume - moved, top.Volume), to.Remaining);
                var taken = from.RemoveFromTop(take);
                if (taken <= epsilon)
                    break;
                to.Add(top.Material, taken);
                moved += taken;
            }

            return moved;
        }

        public static TipReport Tip(TerrainGrid grid, MaterialInventory inventory, int x, int z, float maxVolume = float.PositiveInfinity)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            var report = new TipReport(grid.Materials.MaxId + 1);
            var step = grid.HeightStep * grid.CellArea;
            var area = grid.CellArea;
            var total = inventory.Total;
            var wanted = Math.Min(total, maxVolume);
            var amount = step > 0f ? (float)Math.Floor((wanted + Epsilon) / step) * step : wanted;
            if (amount <= Epsilon)
            {
                report.HeldBack = total;
                return report;
            }

            // What comes off the load, top first.
            var stack = inventory.Stack;
            var taken = new MaterialVolume[stack.Count];
            var takenCount = 0;
            var remaining = amount;
            for (var i = stack.Count - 1; i >= 0 && remaining > Epsilon; i--)
            {
                var take = Math.Min(remaining, stack[i].Volume);
                taken[takenCount++] = new MaterialVolume(stack[i].Material, take);
                remaining -= take;
            }

            // One dump mixes: group by the material each piece lands as, in order of first
            // appearance, so the newest material still goes down first. Otherwise a load of
            // interleaved pieces (dirt, loose rock, dirt, loose rock...) would need a layer per
            // piece and overflow the column's layer cap.
            var landed = new MaterialVolume[takenCount];
            var landedCount = 0;
            for (var i = 0; i < takenCount; i++)
            {
                var material = grid.Materials.GetDisturbed(taken[i].Material);
                var at = 0;
                while (at < landedCount && landed[at].Material != material)
                    at++;
                if (at == landedCount)
                    landed[landedCount++] = new MaterialVolume(material, taken[i].Volume);
                else
                    landed[at] = new MaterialVolume(material, landed[at].Volume + taken[i].Volume);
            }

            // The grid takes metres of thickness.
            var thicknesses = new MaterialVolume[landedCount];
            for (var i = 0; i < landedCount; i++)
                thicknesses[i] = new MaterialVolume(landed[i].Material, landed[i].Volume / area);
            if (!grid.AddStack(x, z, thicknesses))
            {
                report.StackFull = true;
                report.HeldBack = total;
                return report;
            }

            for (var i = 0; i < takenCount; i++)
                inventory.RemoveFromTop(taken[i].Volume);
            for (var i = 0; i < landedCount; i++)
            {
                report.TippedByMaterial[landed[i].Material.Value] += landed[i].Volume;
                report.Tipped += landed[i].Volume;
            }

            report.HeldBack = inventory.Total;
            return report;
        }

        /// <summary>Brush offsets within the disc, centre first, then outwards ring by ring.</summary>
        static List<(int dx, int dz)> DiscNearestFirst(int radius)
        {
            var offsets = new List<(int dx, int dz)>();
            for (var dz = -radius; dz <= radius; dz++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dz * dz <= radius * radius)
                        offsets.Add((dx, dz));
            // Stable order for equal distances keeps digging deterministic.
            offsets.Sort((a, b) =>
            {
                var byDistance = (a.dx * a.dx + a.dz * a.dz).CompareTo(b.dx * b.dx + b.dz * b.dz);
                if (byDistance != 0)
                    return byDistance;
                var byZ = a.dz.CompareTo(b.dz);
                return byZ != 0 ? byZ : a.dx.CompareTo(b.dx);
            });
            return offsets;
        }
    }

    /// <summary>What one <see cref="Excavation.Dig"/> did.</summary>
    public sealed class DigReport
    {
        public DigReport(int materialSlots)
        {
            InPlaceBySource = new float[materialSlots];
        }

        /// <summary>m³ the ground dropped by (solid, in place).</summary>
        public float InPlace;

        /// <summary>m³ that went into the load (loose, bulked).</summary>
        public float Loose;

        /// <summary>In-place m³ dug, by the material it was in the ground, indexed by material id.</summary>
        public readonly float[] InPlaceBySource;

        public int CellsDug;

        /// <summary>Cells skipped because their loose output would have overflowed the load.</summary>
        public int CellsThatDidNotFit;

        /// <summary>Loose m³ the smallest skipped cell would have needed; infinity if none were skipped.</summary>
        public float SmallestMisfit = float.PositiveInfinity;

        /// <summary>Cells with nothing diggable left above bedrock.</summary>
        public int CellsAtBedrock;

        /// <summary>The load could not take even one cell's worth, so nothing was dug.</summary>
        public bool WasFull => CellsDug == 0 && CellsThatDidNotFit > 0;
    }

    /// <summary>What one <see cref="Excavation.Tip"/> did.</summary>
    public sealed class TipReport
    {
        public TipReport(int materialSlots)
        {
            TippedByMaterial = new float[materialSlots];
        }

        /// <summary>Loose m³ placed on the terrain.</summary>
        public float Tipped;

        /// <summary>Placed m³ per material id, as it landed (disturbed form).</summary>
        public readonly float[] TippedByMaterial;

        /// <summary>Loose m³ still in the load afterwards.</summary>
        public float HeldBack;

        /// <summary>The cell's layer stack had no room for the new layers, so nothing was tipped.</summary>
        public bool StackFull;
    }
}
