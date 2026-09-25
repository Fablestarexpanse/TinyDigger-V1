using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Rivers and creeks, step 4 (Ronan, 2026-09-22: "not at start … a section of load that waits
    /// for the streams to run to normal then any surface water gets zapped").
    ///
    /// A fresh island puts every spring on at once over beds that were only roughly pre-filled, so
    /// the first minute of play is a sheet of water spreading over the flats. Loading now settles
    /// the water first: the simulation is stepped until the streams stop changing, then everything
    /// standing outside the sea and the channel beds is taken off in one go.
    ///
    /// After that the water is the player's business, with one standing rule: off the beds and out
    /// of the sea, a film shallower than <see cref="DefaultSoakDepth"/> soaks away at
    /// <see cref="DefaultSoakRate"/> (the package's soak mask). A spring that spills a little can
    /// never build a permanent sheet again, while anything the player floods is deeper than a film
    /// and stays.
    ///
    /// The arithmetic here is pure so it is tested without a GPU; <see cref="ChannelSprings"/>
    /// drives it.
    /// </summary>
    public static class WaterSettle
    {
        /// <summary>Films shallower than this soak away where the mask allows it.</summary>
        public const float DefaultSoakDepth = 0.15f;

        /// <summary>Metres a second a film soaks away at: a full film is gone in about 3 s.</summary>
        public const float DefaultSoakRate = 0.05f;

        /// <summary>Seconds of load the settle may take.</summary>
        public const float DefaultBudgetSeconds = 1.0f;

        /// <summary>Simulation steps run between two checks for steadiness.</summary>
        public const int DefaultBatch = 60;

        /// <summary>
        /// Cells either side of a bed that are kept with it. A stream wanders a little outside the
        /// band the island marked, and with no margin the soak took that water: the creeks of the
        /// 2026-09-22 play run bled out sideways and ran dry while the rivers, wide enough to stay
        /// inside their own band, were fine.
        /// </summary>
        public const int DefaultBedMargin = 1;

        /// <summary>
        /// Cells the stray water may still change by, between two batches, and count as settled.
        ///
        /// Total volume is the wrong thing to watch: the springs feed the sea for as long as the
        /// island exists, so the volume climbs for ever (a load of 2026-09-22 ran its whole budget
        /// without ever steadying). What does settle is the water standing where it does not
        /// belong — off the beds and out of the sea — which spreads at first and then stops.
        /// </summary>
        public const int DefaultTolerance = 32;

        /// <summary>
        /// Fills <paramref name="keep"/> (one per cell, row by row) with the cells whose water is
        /// kept: the sea's basin, every cell whose ground stands at or below
        /// <paramref name="seaLevel"/>, and every channel bed cell the island was cut with.
        /// Returns how many cells are kept.
        /// </summary>
        public static int BuildKeepMask(bool[] keep, TerrainGrid grid, IslandMap island, float seaLevel) =>
            BuildKeepMask(keep, grid, island, seaLevel, DefaultBedMargin);

        /// <summary>
        /// As above, with the bed band widened by <paramref name="bedMargin"/> cells either side.
        /// </summary>
        public static int BuildKeepMask(bool[] keep, TerrainGrid grid, IslandMap island, float seaLevel, int bedMargin)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (island == null)
                throw new ArgumentNullException(nameof(island));
            var cells = grid.Width * grid.Height;
            if (keep == null || keep.Length != cells)
                throw new ArgumentException("One flag per cell.", nameof(keep));

            var beds = island.ChannelBeds;
            var margin = Mathf.Max(0, bedMargin);
            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var cell = z * grid.Width + x;
                    keep[cell] = grid.GetSurfaceHeight(x, z) <= seaLevel;
                }
            }

            if (beds != null)
            {
                for (var z = 0; z < grid.Height; z++)
                {
                    for (var x = 0; x < grid.Width; x++)
                    {
                        var cell = z * grid.Width + x;
                        if (cell >= beds.Length || beds[cell] == 0)
                            continue;
                        for (var dz = -margin; dz <= margin; dz++)
                        {
                            var nz = z + dz;
                            if (nz < 0 || nz >= grid.Height)
                                continue;
                            for (var dx = -margin; dx <= margin; dx++)
                            {
                                var nx = x + dx;
                                if (nx < 0 || nx >= grid.Width)
                                    continue;
                                keep[nz * grid.Width + nx] = true;
                            }
                        }
                    }
                }
            }

            var kept = 0;
            for (var cell = 0; cell < keep.Length; cell++)
                if (keep[cell])
                    kept++;
            return kept;
        }

        /// <summary>
        /// Takes the water off every cell <paramref name="keep"/> does not hold, and returns how
        /// many cells were cleared. <paramref name="cleared"/> is the depth taken off in metres,
        /// which times the cell's area is the volume.
        /// </summary>
        public static int Zap(float[] depths, bool[] keep, out float cleared)
        {
            if (depths == null)
                throw new ArgumentNullException(nameof(depths));
            if (keep == null || keep.Length != depths.Length)
                throw new ArgumentException("One flag per cell.", nameof(keep));

            var count = 0;
            cleared = 0f;
            for (var cell = 0; cell < depths.Length; cell++)
            {
                if (keep[cell] || depths[cell] <= 0f)
                    continue;
                cleared += depths[cell];
                depths[cell] = 0f;
                count++;
            }

            return count;
        }

        /// <summary>Where a film may soak away: everywhere the water is not kept.</summary>
        public static void SoakMask(bool[] keep, bool[] soaks)
        {
            if (keep == null)
                throw new ArgumentNullException(nameof(keep));
            if (soaks == null || soaks.Length != keep.Length)
                throw new ArgumentException("One flag per cell.", nameof(soaks));
            for (var cell = 0; cell < keep.Length; cell++)
                soaks[cell] = !keep[cell];
        }

        /// <summary>
        /// Cells holding more than a film of water where none is kept: what the settle waits on.
        /// </summary>
        public static int CountStray(float[] depths, bool[] keep, float filmDepth)
        {
            if (depths == null)
                throw new ArgumentNullException(nameof(depths));
            if (keep == null || keep.Length != depths.Length)
                throw new ArgumentException("One flag per cell.", nameof(keep));
            var stray = 0;
            for (var cell = 0; cell < depths.Length; cell++)
                if (!keep[cell] && depths[cell] > filmDepth)
                    stray++;
            return stray;
        }

        /// <summary>
        /// Whether the stray water has stopped spreading: two batches whose counts differ by no
        /// more than <paramref name="tolerance"/> cells. The first batch has nothing to compare
        /// with, so it is never steady.
        /// </summary>
        public static bool Steady(int stray, int previous, int tolerance) =>
            previous >= 0 && Math.Abs(stray - previous) <= Math.Max(0, tolerance);

        /// <summary>Batches that fit in a budget, at least one.</summary>
        public static int Batches(float budgetSeconds, float secondsPerBatch) =>
            Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(0f, budgetSeconds) / Mathf.Max(1e-4f, secondsPerBatch)));
    }
}
