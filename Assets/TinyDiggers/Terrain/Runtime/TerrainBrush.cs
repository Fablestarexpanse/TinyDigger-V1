using System;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// Applies a dig or a fill to every cell in a disc. Radius 0 is the single centre cell;
    /// radius r covers the cells whose centres lie within r of the centre cell's.
    /// Cells off the edge of the grid are skipped, so a brush can overhang the border.
    /// </summary>
    public static class TerrainBrush
    {
        /// <summary>
        /// Digs up to <paramref name="volumePerCell"/> (in place) from each cell in the disc. Adds
        /// the loose volume that came out into <paramref name="removedByMaterial"/>, indexed by
        /// <see cref="MaterialId.Value"/> and sized at least <see cref="MaterialTable.MaxId"/> + 1,
        /// and returns the loose total. Less comes out than asked wherever a column bottoms out
        /// on bedrock.
        /// </summary>
        public static float Dig(
            TerrainGrid grid,
            int centreX,
            int centreZ,
            int radius,
            float volumePerCell,
            Span<float> removedByMaterial)
        {
            return Dig(grid, centreX, centreZ, radius, volumePerCell, removedByMaterial, out _);
        }

        /// <summary>
        /// As <see cref="Dig(TerrainGrid,int,int,int,float,Span{float})"/>, also reporting the
        /// in-place volume removed, i.e. how much the ground dropped, before bulking.
        /// </summary>
        public static float Dig(
            TerrainGrid grid,
            int centreX,
            int centreZ,
            int radius,
            float volumePerCell,
            Span<float> removedByMaterial,
            out float inPlaceVolume)
        {
            inPlaceVolume = 0f;
            Validate(grid, radius);
            if (removedByMaterial.Length <= grid.Materials.MaxId)
                throw new ArgumentException(
                    $"Needs room for material ids up to {grid.Materials.MaxId}.", nameof(removedByMaterial));

            Span<MaterialVolume> removed = stackalloc MaterialVolume[TerrainGrid.MaxLayersPerCell];
            var total = 0f;
            var radiusSquared = radius * radius;
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dz * dz > radiusSquared || !grid.InBounds(centreX + dx, centreZ + dz))
                        continue;

                    var before = grid.GetSurfaceHeight(centreX + dx, centreZ + dz);
                    var count = grid.Remove(centreX + dx, centreZ + dz, volumePerCell, removed);
                    inPlaceVolume += before - grid.GetSurfaceHeight(centreX + dx, centreZ + dz);
                    for (var i = 0; i < count; i++)
                    {
                        removedByMaterial[removed[i].Material.Value] += removed[i].Volume;
                        total += removed[i].Volume;
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// Tips <paramref name="volumePerCell"/> of <paramref name="material"/> onto each cell in
        /// the disc and returns the total actually placed; full stacks take nothing.
        /// </summary>
        public static float Fill(
            TerrainGrid grid,
            int centreX,
            int centreZ,
            int radius,
            MaterialId material,
            float volumePerCell)
        {
            Validate(grid, radius);

            var total = 0f;
            var radiusSquared = radius * radius;
            for (var dz = -radius; dz <= radius; dz++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dz * dz <= radiusSquared && grid.InBounds(centreX + dx, centreZ + dz))
                        total += grid.Add(centreX + dx, centreZ + dz, material, volumePerCell);

            return total;
        }

        static void Validate(TerrainGrid grid, int radius)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (radius < 0)
                throw new ArgumentOutOfRangeException(nameof(radius));
        }
    }
}
