using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Placeholder crew for Vertical Slice 1: one <see cref="MaterialInventory"/> and the two
    /// things it can do to the terrain. It does not move yet; it works wherever it is told to.
    ///
    /// Digging moves material out of the ground into the inventory. Dug material comes out
    /// bulked, so the load fills faster than the hole grows. Nothing is dug that would not fit:
    /// each cell is previewed first, and skipped if its loose output would overflow the load.
    ///
    /// Tipping empties the load onto one cell, newest material first, in whole height steps.
    /// Anything less than a step stays in the load until the next dig tops it up.
    /// </summary>
    public sealed class Crew
    {
        /// <summary>Slack for comparing sums of bulked floats.</summary>
        const float Epsilon = 1e-4f;

        readonly Layer[] _preview = new Layer[TerrainGrid.MaxLayersPerCell];
        readonly MaterialVolume[] _removed = new MaterialVolume[TerrainGrid.MaxLayersPerCell];

        public Crew(float capacity = MaterialInventory.DefaultCapacity)
        {
            Inventory = new MaterialInventory(capacity);
        }

        public MaterialInventory Inventory { get; }

        /// <summary>
        /// Digs up to <paramref name="volumePerCell"/> (in place) from each cell within
        /// <paramref name="radius"/> of the centre, nearest cells first, into the inventory.
        /// Cells whose loose output would not fit in what is left are skipped, not part-dug.
        /// </summary>
        public DigReport Dig(TerrainGrid grid, int centreX, int centreZ, int radius, float volumePerCell)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (radius < 0)
                throw new ArgumentOutOfRangeException(nameof(radius));

            var report = new DigReport(grid.Materials.MaxId + 1);
            foreach (var (dx, dz) in DiscNearestFirst(radius))
            {
                var x = centreX + dx;
                var z = centreZ + dz;
                if (!grid.InBounds(x, z))
                    continue;

                var layers = grid.PeekRemove(x, z, volumePerCell, _preview);
                if (layers == 0)
                {
                    report.CellsAtBedrock++;
                    continue;
                }

                var loose = 0f;
                for (var i = 0; i < layers; i++)
                    loose += _preview[i].Thickness * grid.Materials.Get(_preview[i].Material).BulkingFactor;
                if (!Inventory.CanFit(loose))
                {
                    report.CellsThatDidNotFit++;
                    report.SmallestMisfit = Math.Min(report.SmallestMisfit, loose);
                    continue;
                }

                for (var i = 0; i < layers; i++)
                {
                    report.InPlaceBySource[_preview[i].Material.Value] += _preview[i].Thickness;
                    report.InPlace += _preview[i].Thickness;
                }

                var pieces = grid.Remove(x, z, volumePerCell, _removed);
                for (var i = 0; i < pieces; i++)
                {
                    // The preview said it fits; tiny float differences are absorbed by TryAdd's slack.
                    Inventory.Add(_removed[i].Material, _removed[i].Volume);
                    report.Loose += _removed[i].Volume;
                }

                report.CellsDug++;
            }

            return report;
        }

        /// <summary>
        /// Tips as many whole height steps of the load as it holds onto cell (x, z), in one go.
        /// The steps are made up from the top of the load downwards, and one step may mix several
        /// materials: a load of small interleaved pieces still tips. Within one tip the load is
        /// mixed into one layer per landed material, newest material lowest, so a tip adds only as
        /// many layers as it has distinct materials. Whatever is left under one step stays in the
        /// load, and it is the oldest material. Nothing is tipped if the cell's layer stack has no
        /// room.
        /// </summary>
        public TipReport Tip(TerrainGrid grid, int x, int z)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            var report = new TipReport(grid.Materials.MaxId + 1);
            var step = grid.HeightStep;
            var total = Inventory.Total;
            var amount = step > 0f ? (float)Math.Floor((total + Epsilon) / step) * step : total;
            if (amount <= Epsilon)
            {
                report.HeldBack = total;
                return report;
            }

            // What comes off the load, top first.
            var stack = Inventory.Stack;
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

            if (!grid.AddStack(x, z, new ReadOnlySpan<MaterialVolume>(landed, 0, landedCount)))
            {
                report.StackFull = true;
                report.HeldBack = total;
                return report;
            }

            for (var i = 0; i < takenCount; i++)
                Inventory.RemoveFromTop(taken[i].Volume);
            for (var i = 0; i < landedCount; i++)
            {
                report.TippedByMaterial[landed[i].Material.Value] += landed[i].Volume;
                report.Tipped += landed[i].Volume;
            }

            report.HeldBack = Inventory.Total;
            return report;
        }

        /// <summary>Brush offsets within the disc, centre first, then outwards ring by ring.</summary>
        static IEnumerable<(int dx, int dz)> DiscNearestFirst(int radius)
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

    /// <summary>What one <see cref="Crew.Dig"/> did.</summary>
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

    /// <summary>What one <see cref="Crew.Tip"/> did.</summary>
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

        /// <summary>Loose m³ still in the load afterwards: under one height step, or everything if the stack was full.</summary>
        public float HeldBack;

        /// <summary>The cell's layer stack had no room for the new layers, so nothing was tipped.</summary>
        public bool StackFull;
    }
}
