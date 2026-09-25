using System;

namespace PromptWaffle.Terrain
{
    /// <summary>
    /// The terrain's drawn surface as a continuous height: heights at cell corners are the
    /// average of the up to four cells touching them (as the smoothed renderer draws them), and
    /// points in between are bilinear. Things that sit on the ground use this so they ride the
    /// surface the player sees, not the stepped column tops.
    /// </summary>
    public static class TerrainSurface
    {
        /// <summary>Average drawn height (<see cref="TerrainGrid.GetDrawnHeight"/>) of the up to four cells that touch grid corner (cornerX, cornerZ).</summary>
        public static float CornerHeight(TerrainGrid grid, int cornerX, int cornerZ)
        {
            var sum = 0f;
            var count = 0;
            for (var z = cornerZ - 1; z <= cornerZ; z++)
            {
                for (var x = cornerX - 1; x <= cornerX; x++)
                {
                    // Void cells are off the map: the rim corner takes the height of the ground
                    // beside it, so the disc ends in a cut edge rather than a ramp down to zero.
                    if (!grid.IsGround(x, z))
                        continue;
                    sum += grid.GetDrawnHeight(x, z);
                    count++;
                }
            }

            return count == 0 ? 0f : sum / count;
        }

        /// <summary>
        /// Surface height at (x, z) in cell units: cell (i, j) spans i..i+1, j..j+1, so its
        /// centre is (i + 0.5, j + 0.5). Positions off the map are clamped to its edge.
        /// </summary>
        public static float SampleHeight(TerrainGrid grid, float x, float z)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            x = Math.Min(Math.Max(x, 0f), grid.Width);
            z = Math.Min(Math.Max(z, 0f), grid.Height);
            var cornerX = Math.Min((int)x, grid.Width - 1);
            var cornerZ = Math.Min((int)z, grid.Height - 1);
            var fx = x - cornerX;
            var fz = z - cornerZ;

            var h00 = CornerHeight(grid, cornerX, cornerZ);
            var h10 = CornerHeight(grid, cornerX + 1, cornerZ);
            var h01 = CornerHeight(grid, cornerX, cornerZ + 1);
            var h11 = CornerHeight(grid, cornerX + 1, cornerZ + 1);
            var south = h00 + (h10 - h00) * fx;
            var north = h01 + (h11 - h01) * fx;
            return south + (north - south) * fz;
        }
    }
}
