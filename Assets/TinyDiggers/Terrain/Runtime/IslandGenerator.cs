using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// What a generated island came out as, besides the grid itself: the shape it was asked for,
    /// where its high point is, and the lines its rivers run along, so river meshes can be built to
    /// match the channels that were cut.
    /// </summary>
    public sealed class IslandMap
    {
        static readonly List<Vector3> NoRiver = new List<Vector3>();

        /// <summary>The shape this seed actually made, once <see cref="LandShape.Any"/> has chosen.</summary>
        public LandShape Shape;

        /// <summary>The highest cell of the land.</summary>
        public Vector2Int Peak;

        /// <summary>Every river, longest first, in grid space: x and z cell centres, y the channel floor.</summary>
        public readonly List<List<Vector3>> Rivers = new List<List<Vector3>>();

        /// <summary>The main river. Empty when the land made none.</summary>
        public List<Vector3> River => Rivers.Count > 0 ? Rivers[0] : NoRiver;

        /// <summary>Cells the rivers' channels cover, for the sim to treat as water.</summary>
        public readonly List<Vector2Int> RiverCells = new List<Vector2Int>();

        /// <summary>Milliseconds the land took to generate.</summary>
        public double Milliseconds;
    }

    /// <summary>
    /// The island: land where noise says land, shaped by an archetype, with a ridge along it,
    /// valleys cut by where water would run, rivers in the biggest of those valleys, a beach or a
    /// rocky shore depending on how steeply the land meets the sea, and a shelf falling away
    /// offshore.
    ///
    /// Stages, in order, because each reads what the last wrote:
    /// 1. Land mask — twice-warped fBm above a threshold, shaped by the archetype, with the outer
    ///    ring of the disc forced to sea. Small islands and small ponds are then cleaned up.
    /// 2. Base height — fBm over the land, plus a medium octave for texture.
    /// 3. Ridge — a curve of two to four control points placed by seed, with ridged multifractal
    ///    noise standing along it.
    /// 4. Benches — flat ground pulled into the lee of the ridge.
    /// 5. Valleys — one D8 flow accumulation pass; the more water a cell would gather, the deeper
    ///    it is cut, so valleys converge the way real ones do.
    /// 6. Coast — a beach where the land meets the sea gently, bare ground where it does not, and
    ///    a shallow shelf out from the shore before the sea drops away.
    /// 7. Quantise to the height step and relax until no two neighbouring land cells differ by more
    ///    than one step. Cliffs are for later.
    /// 8. Rivers — from the outlet with the most land draining through it, carved as a channel.
    /// 9. Strata — bedrock base, granite under the high ground, rock, clay in the valleys, dirt,
    ///    sand on beaches and under water, topsoil above them.
    ///
    /// Deterministic: the same seed and settings give the same island, cell for cell.
    /// <see cref="TerrainGenerator"/> is still here and still used by its own tests; this one is
    /// what the game generates.
    /// </summary>
    public static class IslandGenerator
    {
        /// <summary>Layers thinner than this are folded into the one above rather than kept.</summary>
        const float MinLayerThickness = 0.05f;

        /// <summary>Sweeps of the neighbour-step relaxation before it gives up and reports.</summary>
        const int MaxRelaxSweeps = 200;

        /// <summary>Cells that must drain through one before it counts as a valley floor.</summary>
        const float ValleyThreshold = 12f;

        static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] StepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public static IslandMap Generate(TerrainGrid grid, TerrainGenSettings settings)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var random = new System.Random(settings.Seed);
            var width = grid.Width;
            var depth = grid.Height;
            var cells = width * depth;
            var step = grid.HeightStep > 0f ? grid.HeightStep : 1f;

            // Kept modest: Mathf.PerlinNoise loses precision far from the origin.
            var landOffset = Offset(random);
            var landWarp = Offset(random);
            var landWarpFine = Offset(random);
            var baseOffset = Offset(random);
            var mediumOffset = Offset(random);
            var warpOffset = Offset(random);
            var ridgeOffset = Offset(random);

            var radius = TerrainGenerator.DiscRadius(grid);
            var centre = new Vector2(width * 0.5f, depth * 0.5f);
            var map = new IslandMap
            {
                Shape = settings.Shape == LandShape.Any ? PickShape(random) : settings.Shape,
            };

            // --- 1: where the land is --------------------------------------------------------
            var land = new bool[cells];
            var inDisc = new bool[cells];
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    var here = new Vector2(x + 0.5f, z + 0.5f);
                    var toCentre = Vector2.Distance(here, centre);
                    if (toCentre > radius)
                    {
                        grid.SetVoid(x, z, true);
                        continue;
                    }

                    inDisc[cell] = true;
                    land[cell] = IsLand(here, toCentre, radius, centre, landOffset, landWarp, landWarpFine, settings, map.Shape);
                }
            }

            RemoveSmallBlobs(land, inDisc, width, depth, settings, map.Shape);

            // --- 2-4: height over the land ---------------------------------------------------
            var ridge = RidgeLine(random, centre, radius);
            var benches = Benches(random, ridge, centre, radius, settings);
            var heights = new float[cells];
            var highGround = new float[cells];
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell])
                    {
                        heights[cell] = settings.ChannelDepth;
                        continue;
                    }

                    if (!land[cell])
                    {
                        heights[cell] = World.SeaLevel - 1f; // Shaped properly in stage 6.
                        continue;
                    }

                    var warped = Warp(x, z, warpOffset, settings.WarpSize, settings.WarpStrength);
                    var height = settings.BaseHeight + Fbm(warped, baseOffset, settings.FeatureSize, 4) * settings.BaseRelief;
                    height += (Noise(warped, mediumOffset, settings.MediumSize) - 0.5f) * settings.MediumRelief;

                    var alongRidge = 1f - DistanceToCurve(warped, ridge) / Mathf.Max(4f, settings.RidgeWidth);
                    if (alongRidge > 0f)
                    {
                        // Ridged multifractal: the noise is folded about its middle so the field
                        // has creases rather than lumps, which is what makes a ridge read as one.
                        var crest = RidgedFbm(warped, ridgeOffset, settings.FeatureSize * 0.55f, 4);
                        highGround[cell] = Smooth(alongRidge) * crest;
                        height += highGround[cell] * settings.RidgeHeight;
                    }

                    foreach (var bench in benches)
                    {
                        var t = 1f - Vector2.Distance(warped, new Vector2(bench.x, bench.y)) / Mathf.Max(4f, settings.PlateauRadius);
                        if (t <= 0f)
                            continue;
                        // A bench is a floor the land is pulled onto, not a block dropped on it.
                        height = Mathf.Lerp(height, bench.z, Smooth(t));
                    }

                    heights[cell] = height;
                }
            }

            // --- 5: valleys where the water would run ---------------------------------------
            var accumulation = FlowAccumulation(heights, land, width, depth);
            CutValleys(heights, land, accumulation, settings);

            // --- 6: the coast and the shelf --------------------------------------------------
            ShapeCoast(heights, land, inDisc, width, depth, radius, centre, settings);

            // --- 7: quantise and relax -------------------------------------------------------
            var isLand = new bool[cells];
            for (var cell = 0; cell < cells; cell++)
            {
                heights[cell] = Mathf.Round(heights[cell] / step) * step;
                isLand[cell] = land[cell];
            }

            var sweeps = Relax(heights, isLand, width, depth, step);

            // --- 8: rivers -------------------------------------------------------------------
            CarveRivers(map, heights, land, accumulation, width, depth, grid, settings, step);

            for (var cell = 0; cell < cells; cell++)
                isLand[cell] = land[cell] && heights[cell] >= World.SeaLevel;
            sweeps = Mathf.Max(sweeps, Relax(heights, isLand, width, depth, step));

            // The mask's cleanup was about where land was meant to be; this one is about where it
            // ended up, because relaxing, beaching and carving all move cells across the waterline.
            CleanUpShores(heights, inDisc, width, depth, settings, map.Shape, step, fillPockets: true);
            for (var cell = 0; cell < cells; cell++)
                isLand[cell] = heights[cell] >= World.SeaLevel;
            sweeps = Mathf.Max(sweeps, Relax(heights, isLand, width, depth, step));
            // Once more, dropping specks only: relaxing lowers cells, which can cut a corner of
            // land off from the rest, and a one-cell island is not somewhere to play.
            CleanUpShores(heights, inDisc, width, depth, settings, map.Shape, step, fillPockets: false);
            ReadRiverFloors(map, heights, width);

            // --- 9: strata -------------------------------------------------------------------
            var peak = -1;
            Span<Layer> column = stackalloc Layer[8];
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell])
                        continue;
                    if (peak < 0 || heights[cell] > heights[peak])
                        peak = cell;
                    var count = BuildColumn(column, heights[cell], Steepness(heights, width, depth, x, z),
                        highGround[cell], ValleyStrength(accumulation[cell]), grid.Datum, settings);
                    grid.SetColumn(x, z, column.Slice(0, count));
                }
            }

            if (peak >= 0)
                map.Peak = new Vector2Int(peak % width, peak / width);
            if (sweeps >= MaxRelaxSweeps)
                Debug.LogWarning($"Island: the land was still stepping by more than {step} m after {sweeps} sweeps.");

            map.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return map;
        }

        static Vector2 Offset(System.Random random) =>
            new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);

        static LandShape PickShape(System.Random random)
        {
            // Continent twice, so the common case stays common.
            var shapes = new[]
            {
                LandShape.Continent, LandShape.Continent, LandShape.Crescent,
                LandShape.Twin, LandShape.Archipelago, LandShape.Lagoon,
            };
            return shapes[random.Next(shapes.Length)];
        }

        // --- 1: the land mask ------------------------------------------------------------------

        /// <summary>
        /// Whether this cell is land: twice-warped fBm above a threshold, pushed up or down by the
        /// archetype, and forced under the sea in the outer ring of the disc so the map always ends
        /// in water rather than in a cut edge.
        /// </summary>
        static bool IsLand(Vector2 here, float toCentre, float radius, Vector2 centre,
            Vector2 landOffset, Vector2 warp, Vector2 warpFine, TerrainGenSettings settings, LandShape shape)
        {
            var size = Mathf.Max(20f, settings.LandFeatureSize);
            // Two warps: the first bends the coast, the second frays it, which is where bays and
            // headlands come from. One warp alone gives smooth ovals.
            var first = here + Direction(here, warp, size * 0.8f) * settings.LandWarpStrength;
            var sample = first + Direction(first, warpFine, size * 0.35f) * (settings.LandWarpStrength * 0.5f);

            var field = Fbm(sample, landOffset, size, 4) * 2f;
            field += ShapeBias(here, toCentre, radius, centre, shape);

            // The rim: everything past it is sea, and the last stretch before it is pushed under.
            var water = Mathf.Max(8, settings.RimWaterCells);
            var fromRim = radius - toCentre;
            if (fromRim < water)
                field -= Mathf.Lerp(1.6f, 0f, Mathf.Clamp01(fromRim / water));

            return field > settings.LandThreshold;
        }

        /// <summary>How much the archetype adds to or takes from the land field at a point.</summary>
        static float ShapeBias(Vector2 here, float toCentre, float radius, Vector2 centre, LandShape shape)
        {
            var normalised = toCentre / Mathf.Max(1f, radius);
            switch (shape)
            {
                case LandShape.Crescent:
                {
                    // A bay bitten out of one side: a soft disc of "not land" set off centre.
                    var bay = centre + new Vector2(0.42f, 0.18f) * radius;
                    var t = 1f - Vector2.Distance(here, bay) / (radius * 0.5f);
                    return 0.55f - 1.9f * Smooth(t) - normalised * 0.5f;
                }

                case LandShape.Twin:
                {
                    // Two masses either side of a strait: a trough along one axis.
                    var strait = 1f - Mathf.Abs(here.x - centre.x) / (radius * 0.2f);
                    return 0.6f - 2f * Smooth(strait) - normalised * 0.55f;
                }

                case LandShape.Archipelago:
                    // Lower and closer to the threshold: the same field breaks into pieces.
                    return 0.24f - normalised * 0.7f;

                case LandShape.Lagoon:
                {
                    // A ring of land: high where the distance from the middle is near the ring, low
                    // inside it, with one gap cut through so the lagoon opens to the sea.
                    var ring = 1f - Mathf.Abs(normalised - 0.62f) / 0.24f;
                    var angle = Mathf.Atan2(here.y - centre.y, here.x - centre.x) * Mathf.Rad2Deg;
                    var gap = 1f - Mathf.Abs(Mathf.DeltaAngle(angle, 35f)) / 20f;
                    return 1.2f * Smooth(ring) - 0.55f - 2f * Smooth(gap);
                }

                default:
                    // Continent: one mass, rugged coast, thinning toward the rim.
                    return 0.6f - normalised * 0.95f;
            }
        }

        static Vector2 Direction(Vector2 at, Vector2 offset, float size)
        {
            var frequency = 1f / Mathf.Max(1f, size);
            return new Vector2(
                Mathf.PerlinNoise(offset.x + at.x * frequency, offset.y + at.y * frequency) - 0.5f,
                Mathf.PerlinNoise(offset.y + at.x * frequency, offset.x + at.y * frequency) - 0.5f) * 2f;
        }

        /// <summary>
        /// Drops land too small to play on into the sea, and fills ponds too small to be worth
        /// swimming. An archipelago keeps its islets: that is what it is for.
        /// </summary>
        static void RemoveSmallBlobs(bool[] land, bool[] inDisc, int width, int depth, TerrainGenSettings settings, LandShape shape)
        {
            if (shape != LandShape.Archipelago)
                Cull(land, inDisc, width, depth, wanted: true, minimum: settings.MinLandBlob);
            // Ponds are filled whatever the shape: a puddle of sea in the middle of a field is not
            // a lagoon, it is a hole the crew cannot cross.
            Cull(land, inDisc, width, depth, wanted: false, minimum: settings.MinWaterPocket);
        }

        /// <summary>
        /// The same cleanup again, on the land as it finally stands: specks of land left standing
        /// by the relaxation are pushed under, and puddles too small to matter are filled to a step
        /// above the sea. Heights, not flags, because by now the heights are what decide.
        /// </summary>
        static void CleanUpShores(float[] heights, bool[] inDisc, int width, int depth,
            TerrainGenSettings settings, LandShape shape, float step, bool fillPockets)
        {
            // The sim's own rule for land, not just "above the waterline": a cell level with the
            // sea is water, so a speck that was beached to sea level is not an island, it is a
            // shoal, and it should not be counted as somewhere to stand.
            var land = new bool[heights.Length];
            for (var cell = 0; cell < heights.Length; cell++)
                land[cell] = heights[cell] >= World.SeaLevel + step - 1e-3f;

            if (shape != LandShape.Archipelago)
            {
                foreach (var patch in Patches(land, inDisc, width, depth, wanted: true, minimum: settings.MinLandBlob))
                    foreach (var cell in patch)
                        heights[cell] = World.SeaLevel - Mathf.Max(step, 1f);
            }

            if (!fillPockets)
                return;
            foreach (var patch in Patches(land, inDisc, width, depth, wanted: false, minimum: settings.MinWaterPocket))
                foreach (var cell in patch)
                    heights[cell] = World.SeaLevel + step;
        }

        /// <summary>Every connected patch of <paramref name="wanted"/> smaller than <paramref name="minimum"/>.</summary>
        static List<List<int>> Patches(bool[] land, bool[] inDisc, int width, int depth, bool wanted, int minimum)
        {
            var small = new List<List<int>>();
            var seen = new bool[land.Length];
            var stack = new Stack<int>();
            for (var start = 0; start < land.Length; start++)
            {
                if (seen[start] || !inDisc[start] || land[start] != wanted)
                    continue;

                var patch = new List<int>();
                stack.Push(start);
                seen[start] = true;
                var touchesRim = false;
                while (stack.Count > 0)
                {
                    var cell = stack.Pop();
                    patch.Add(cell);
                    var x = cell % width;
                    var z = cell / width;
                    for (var n = 0; n < 4; n++)
                    {
                        var nx = x + StepX[n];
                        var nz = z + StepZ[n];
                        if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                            continue;
                        var next = nz * width + nx;
                        if (!inDisc[next])
                        {
                            touchesRim = true;
                            continue;
                        }

                        if (seen[next] || land[next] != wanted)
                            continue;
                        seen[next] = true;
                        stack.Push(next);
                    }
                }

                if (!wanted && touchesRim)
                    continue;
                if (patch.Count < minimum)
                    small.Add(patch);
            }

            return small;
        }

        /// <summary>Flips every connected patch of <paramref name="wanted"/> smaller than <paramref name="minimum"/>.</summary>
        static void Cull(bool[] land, bool[] inDisc, int width, int depth, bool wanted, int minimum)
        {
            var seen = new bool[land.Length];
            var stack = new Stack<int>();
            var patch = new List<int>();
            for (var start = 0; start < land.Length; start++)
            {
                if (seen[start] || !inDisc[start] || land[start] != wanted)
                    continue;

                patch.Clear();
                stack.Push(start);
                seen[start] = true;
                var touchesRim = false;
                while (stack.Count > 0)
                {
                    var cell = stack.Pop();
                    patch.Add(cell);
                    var x = cell % width;
                    var z = cell / width;
                    for (var n = 0; n < 4; n++)
                    {
                        var nx = x + StepX[n];
                        var nz = z + StepZ[n];
                        if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                            continue;
                        var next = nz * width + nx;
                        if (!inDisc[next])
                        {
                            // Water that reaches the edge of the disc is the sea, never a pond.
                            touchesRim = true;
                            continue;
                        }

                        if (seen[next] || land[next] != wanted)
                            continue;
                        seen[next] = true;
                        stack.Push(next);
                    }
                }

                if (!wanted && touchesRim)
                    continue;
                if (patch.Count >= minimum)
                    continue;
                foreach (var cell in patch)
                    land[cell] = !wanted;
            }
        }

        // --- 3-4: ridge and benches -------------------------------------------------------------

        /// <summary>Two to four control points across the interior, as a curve the ridge runs along.</summary>
        static Vector2[] RidgeLine(System.Random random, Vector2 centre, float radius)
        {
            var count = 2 + random.Next(3);
            var points = new Vector2[count];
            var heading = (float)random.NextDouble() * Mathf.PI * 2f;
            var along = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
            var side = new Vector2(-along.y, along.x);
            var start = centre + along * radius * 0.5f;
            var end = centre - along * radius * 0.4f;
            for (var i = 0; i < count; i++)
            {
                var t = count == 1 ? 0.5f : i / (float)(count - 1);
                // Each point is pushed off the straight line, so the ridge bends.
                points[i] = Vector2.Lerp(start, end, t) + side * ((float)random.NextDouble() - 0.5f) * radius * 0.45f;
            }

            return points;
        }

        /// <summary>Benches as (x, z, height): flat ground off to one side of the ridge.</summary>
        static Vector3[] Benches(System.Random random, Vector2[] ridge, Vector2 centre, float radius, TerrainGenSettings settings)
        {
            var count = Mathf.Clamp(settings.Plateaus, 0, 2);
            var benches = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var anchor = ridge[random.Next(ridge.Length)];
                var away = (anchor - centre).sqrMagnitude > 1f ? (anchor - centre).normalized : Vector2.right;
                var side = new Vector2(-away.y, away.x) * (random.Next(2) == 0 ? 1f : -1f);
                var at = anchor + side * radius * Mathf.Lerp(0.18f, 0.35f, (float)random.NextDouble());
                var height = Mathf.Lerp(10f, 25f, (float)random.NextDouble());
                benches[i] = new Vector3(at.x, at.y, height);
            }

            return benches;
        }

        /// <summary>Distance from a point to the polyline, in cells.</summary>
        static float DistanceToCurve(Vector2 at, Vector2[] curve)
        {
            var best = float.MaxValue;
            for (var i = 1; i < curve.Length; i++)
            {
                var a = curve[i - 1];
                var ab = curve[i] - a;
                var length = ab.sqrMagnitude;
                var t = length < 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(at - a, ab) / length);
                best = Mathf.Min(best, Vector2.Distance(at, a + ab * t));
            }

            return best;
        }

        // --- 5: flow and valleys ---------------------------------------------------------------

        /// <summary>
        /// One D8 pass: every land cell starts holding one cell's worth of water and hands it to
        /// its steepest downhill neighbour, worked from the highest cell down so a cell always has
        /// everything above it before it passes anything on. What comes out is how much water would
        /// run through each cell, which is where a valley belongs.
        /// </summary>
        static float[] FlowAccumulation(float[] heights, bool[] land, int width, int depth)
        {
            var cells = heights.Length;
            var accumulation = new float[cells];
            var order = new List<int>(cells);
            for (var cell = 0; cell < cells; cell++)
            {
                if (!land[cell])
                    continue;
                accumulation[cell] = 1f;
                order.Add(cell);
            }

            order.Sort((a, b) => heights[b].CompareTo(heights[a]));
            foreach (var cell in order)
            {
                var x = cell % width;
                var z = cell / width;
                var height = heights[cell];
                var lowest = -1;
                var drop = 0f;
                for (var n = 0; n < 8; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                        continue;
                    var next = nz * width + nx;
                    // Diagonals are longer, so their fall counts for less.
                    var run = n < 4 ? 1f : 1.414f;
                    var slope = (height - heights[next]) / run;
                    if (slope <= drop)
                        continue;
                    drop = slope;
                    lowest = next;
                }

                if (lowest >= 0)
                    accumulation[lowest] += accumulation[cell];
            }

            return accumulation;
        }

        /// <summary>0 where nothing drains through, rising on a log scale where a lot does.</summary>
        static float ValleyStrength(float accumulation) =>
            accumulation <= ValleyThreshold ? 0f : Mathf.Clamp01(Mathf.Log(accumulation / ValleyThreshold) / 6f);

        /// <summary>Cuts each cell down by how much water runs through it.</summary>
        static void CutValleys(float[] heights, bool[] land, float[] accumulation, TerrainGenSettings settings)
        {
            if (settings.ValleyCut <= 0f)
                return;
            for (var cell = 0; cell < heights.Length; cell++)
            {
                if (!land[cell])
                    continue;
                var strength = ValleyStrength(accumulation[cell]);
                if (strength > 0f)
                    heights[cell] -= settings.ValleyCut * strength;
            }
        }

        // --- 6: the coast ----------------------------------------------------------------------

        /// <summary>
        /// Gives the shore its profile: a beach where the land comes down to the sea gently, bare
        /// ground where it does not, and out at sea a shallow shelf that falls away to the deep.
        ///
        /// Both are driven by how far a cell is from the waterline, which is one flood each way.
        /// </summary>
        static void ShapeCoast(float[] heights, bool[] land, bool[] inDisc, int width, int depth,
            float radius, Vector2 centre, TerrainGenSettings settings)
        {
            var toWater = Distance(land, inDisc, width, depth, from: false);
            var toLand = Distance(land, inDisc, width, depth, from: true);

            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell])
                        continue;

                    if (land[cell])
                    {
                        var beach = Mathf.Max(1, settings.BeachCells);
                        var from = toWater[cell];
                        if (from > beach)
                            continue;

                        // Steep ground keeps its height and meets the water as rock; gentle ground
                        // is pulled down onto a beach that rises from the waterline.
                        if (Steepness(heights, width, depth, x, z) > settings.BeachMaxSlope)
                            continue;
                        var t = Mathf.Clamp01((from - 1f) / beach);
                        heights[cell] = Mathf.Min(heights[cell], Mathf.Lerp(0.2f, settings.BeachHeight, t));
                        continue;
                    }

                    var offshore = toLand[cell];
                    var shallow = Mathf.Max(1, settings.ShallowCells);
                    var toCentre = Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), centre);
                    var rim = Mathf.Clamp01((toCentre - (radius - settings.ChannelCells)) / Mathf.Max(1f, settings.ChannelCells));
                    float sea;
                    if (offshore <= shallow)
                    {
                        // The shelf: a gentle slope from the waterline out.
                        sea = Mathf.Lerp(settings.ShelfNearDepth * 0.7f, settings.ShelfFarDepth * 0.75f, Mathf.Clamp01(offshore / shallow));
                    }
                    else
                    {
                        var beyond = Mathf.Clamp01((offshore - shallow) / (shallow * 2f));
                        sea = Mathf.Lerp(settings.ShelfFarDepth * 0.75f, settings.ChannelDepth, beyond);
                    }

                    heights[cell] = Mathf.Lerp(sea, settings.ChannelDepth, rim);
                }
            }
        }

        /// <summary>Cells to the nearest cell whose land flag is <paramref name="from"/>, by flood.</summary>
        static float[] Distance(bool[] land, bool[] inDisc, int width, int depth, bool from)
        {
            var distance = new float[land.Length];
            var queue = new Queue<int>();
            for (var cell = 0; cell < land.Length; cell++)
            {
                if (inDisc[cell] && land[cell] == from)
                {
                    distance[cell] = 0f;
                    queue.Enqueue(cell);
                }
                else
                {
                    distance[cell] = float.MaxValue;
                }
            }

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var x = cell % width;
                var z = cell / width;
                for (var n = 0; n < 4; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                        continue;
                    var next = nz * width + nx;
                    if (distance[next] <= distance[cell] + 1f)
                        continue;
                    distance[next] = distance[cell] + 1f;
                    queue.Enqueue(next);
                }
            }

            return distance;
        }

        // --- 7: relaxation ---------------------------------------------------------------------

        /// <summary>
        /// Pulls down any land cell standing more than one step above a neighbour, sweeping until
        /// nothing moves. Four directional passes a sweep, the way a chamfer distance transform
        /// works: a low cell's influence travels the whole width of the map in one pass rather than
        /// one cell a sweep.
        ///
        /// Water is left alone: a shelf may fall away as steeply as it likes, it is only what the
        /// crew walks on that has to stay climbable.
        /// </summary>
        static int Relax(float[] heights, bool[] isLand, int width, int depth, float step)
        {
            for (var sweep = 0; sweep < MaxRelaxSweeps; sweep++)
            {
                var moved = false;

                for (var z = 0; z < depth; z++)
                    for (var x = 1; x < width; x++)
                        moved |= Pull(heights, isLand, z * width + x, z * width + x - 1, step);

                for (var z = 0; z < depth; z++)
                    for (var x = width - 2; x >= 0; x--)
                        moved |= Pull(heights, isLand, z * width + x, z * width + x + 1, step);

                for (var z = 1; z < depth; z++)
                    for (var x = 0; x < width; x++)
                        moved |= Pull(heights, isLand, z * width + x, (z - 1) * width + x, step);

                for (var z = depth - 2; z >= 0; z--)
                    for (var x = 0; x < width; x++)
                        moved |= Pull(heights, isLand, z * width + x, (z + 1) * width + x, step);

                if (!moved)
                    return sweep;
            }

            return MaxRelaxSweeps;
        }

        /// <summary>Lowers <paramref name="cell"/> to one step above <paramref name="from"/> if it stands higher than that.</summary>
        static bool Pull(float[] heights, bool[] isLand, int cell, int from, float step)
        {
            if (!isLand[cell])
                return false;
            var limit = heights[from] + step;
            if (heights[cell] <= limit + 1e-4f)
                return false;
            heights[cell] = limit;
            return true;
        }

        /// <summary>
        /// How steep the ground is here, in metres a cell, averaged over a three by three window.
        ///
        /// Averaged rather than the worst single step on purpose: on quantised land a gentle slope
        /// is a row of one-metre steps with flats between them, so the worst step alternates cell
        /// by cell and anything keyed to it comes out as a checkerboard of materials.
        /// </summary>
        static float Steepness(float[] heights, int width, int depth, int x, int z)
        {
            var sum = 0f;
            var count = 0;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var ax = x + dx;
                    var az = z + dz;
                    if (ax < 0 || az < 0 || ax >= width || az >= depth)
                        continue;
                    if (ax + 1 < width)
                    {
                        sum += Mathf.Abs(heights[az * width + ax] - heights[az * width + ax + 1]);
                        count++;
                    }

                    if (az + 1 < depth)
                    {
                        sum += Mathf.Abs(heights[az * width + ax] - heights[(az + 1) * width + ax]);
                        count++;
                    }
                }
            }

            return count == 0 ? 0f : sum / count;
        }

        // --- 8: rivers -------------------------------------------------------------------------

        /// <summary>
        /// Rivers run where the water already does: the outlet with the most land draining through
        /// it becomes the main river, and the best outlet well away from it, if there is one,
        /// becomes a second. Each is walked back upstream along the wettest cells and cut.
        /// </summary>
        static void CarveRivers(IslandMap map, float[] heights, bool[] land, float[] accumulation,
            int width, int depth, TerrainGrid grid, TerrainGenSettings settings, float step)
        {
            var outlets = new List<int>();
            for (var z = 1; z < depth - 1; z++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell] || accumulation[cell] < ValleyThreshold * 4f)
                        continue;
                    var meetsSea = false;
                    for (var n = 0; n < 4 && !meetsSea; n++)
                        meetsSea = !land[(z + StepZ[n]) * width + x + StepX[n]];
                    if (meetsSea)
                        outlets.Add(cell);
                }
            }

            if (outlets.Count == 0)
                return;
            outlets.Sort((a, b) => accumulation[b].CompareTo(accumulation[a]));

            var chosen = new List<int> { outlets[0] };
            foreach (var outlet in outlets)
            {
                if (chosen.Count >= 2)
                    break;
                var far = true;
                foreach (var taken in chosen)
                {
                    var dx = outlet % width - taken % width;
                    var dz = outlet / width - taken / width;
                    if (dx * dx + dz * dz < 60 * 60)
                        far = false;
                }

                // A second river only if it drains a decent share of the island: two trickles side
                // by side read as a mistake rather than as a second river.
                if (far && accumulation[outlet] > accumulation[outlets[0]] * 0.35f)
                    chosen.Add(outlet);
            }

            foreach (var outlet in chosen)
            {
                var path = Upstream(outlet, accumulation, land, width, depth);
                if (path.Count < 6)
                    continue;
                Carve(map, path, heights, width, grid, settings, step);
            }
        }

        /// <summary>Walks from an outlet back up the wettest neighbours to the head of the valley.</summary>
        static List<Vector2Int> Upstream(int outlet, float[] accumulation, bool[] land, int width, int depth)
        {
            var path = new List<int> { outlet };
            var seen = new HashSet<int> { outlet };
            var at = outlet;
            while (true)
            {
                var x = at % width;
                var z = at / width;
                var best = -1;
                // Low enough to follow a river up to its headwaters: a stream carrying four cells
                // is still a stream, and stopping at a dozen leaves rivers that are stubs.
                var bestFlow = 4f;
                for (var n = 0; n < 8; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    if (nx < 1 || nz < 1 || nx >= width - 1 || nz >= depth - 1)
                        continue;
                    var next = nz * width + nx;
                    if (!land[next] || seen.Contains(next) || accumulation[next] <= bestFlow)
                        continue;
                    // Upstream means less water than here: this is the biggest feeder, not the sea.
                    if (accumulation[next] >= accumulation[at])
                        continue;
                    bestFlow = accumulation[next];
                    best = next;
                }

                if (best < 0)
                    break;
                seen.Add(best);
                path.Add(best);
                at = best;
            }

            path.Reverse();
            var points = new List<Vector2Int>(path.Count);
            foreach (var cell in path)
                points.Add(new Vector2Int(cell % width, cell / width));
            return points;
        }

        /// <summary>
        /// Cuts a flat-floored channel along the path, its banks stepping up a metre a cell so the
        /// cut keeps the neighbour-step guarantee, with the floor held monotonically descending and
        /// stopped at the waterline while there is still land around it.
        /// </summary>
        static void Carve(IslandMap map, List<Vector2Int> path, float[] heights, int width,
            TerrainGrid grid, TerrainGenSettings settings, float step)
        {
            var halfWidth = Mathf.Max(1, settings.RiverWidth) * 0.5f;
            var reach = Mathf.CeilToInt(halfWidth + settings.RiverDepth / step) + 1;
            var floor = float.MaxValue;
            var river = new List<Vector3>();

            foreach (var point in path)
            {
                var here = heights[point.y * width + point.x];
                var target = Mathf.Min(floor, here - settings.RiverDepth);
                // While there is still land around it, the channel floor stops at the waterline:
                // otherwise a river coming down a hillside cuts itself a trench below sea level
                // halfway across the island, and what should be a river becomes an inlet.
                if (here > World.SeaLevel)
                    target = Mathf.Max(target, World.SeaLevel - 0.5f);
                target = Mathf.Round(target / step) * step;
                floor = target;

                for (var dz = -reach; dz <= reach; dz++)
                {
                    for (var dx = -reach; dx <= reach; dx++)
                    {
                        var nx = point.x + dx;
                        var nz = point.y + dz;
                        if (!grid.InBounds(nx, nz) || grid.IsVoid(nx, nz))
                            continue;
                        var distance = Mathf.Sqrt(dx * dx + dz * dz);
                        var bank = Mathf.Max(0f, distance - halfWidth) * step;
                        var cut = target + bank;
                        var cell = nz * width + nx;
                        if (cut < heights[cell])
                            heights[cell] = Mathf.Round(cut / step) * step;
                        if (distance <= halfWidth)
                            map.RiverCells.Add(new Vector2Int(nx, nz));
                    }
                }

                river.Add(new Vector3(point.x + 0.5f, target, point.y + 0.5f));
                // The river ends where the land does.
                if (here <= World.SeaLevel)
                    break;
            }

            if (river.Count >= 2)
                map.Rivers.Add(river);
        }

        /// <summary>
        /// Reads every river's floor back out of the land as it finally stands, held monotonically
        /// descending, so a river mesh laid on the line sits in the channel that is really there and
        /// never runs uphill. The last point goes under the surface, because that is where a river
        /// ends.
        /// </summary>
        static void ReadRiverFloors(IslandMap map, float[] heights, int width)
        {
            foreach (var river in map.Rivers)
            {
                var floor = float.MaxValue;
                for (var i = 0; i < river.Count; i++)
                {
                    var point = river[i];
                    floor = Mathf.Min(floor, heights[Mathf.FloorToInt(point.z) * width + Mathf.FloorToInt(point.x)]);
                    var last = i == river.Count - 1;
                    river[i] = new Vector3(point.x, last ? Mathf.Min(floor, World.SeaLevel - 0.5f) : floor, point.z);
                }
            }

            map.Rivers.Sort((a, b) => b.Count.CompareTo(a.Count));
        }

        // --- 9: strata -------------------------------------------------------------------------

        /// <summary>
        /// Builds one column bottom-up for a surface at <paramref name="surface"/>: bedrock from
        /// the datum, granite under the high ground, rock, clay in the valleys, then the cap —
        /// sand on beaches and under water, bare rock where it is too steep to hold soil, dirt and
        /// topsoil everywhere else.
        /// </summary>
        static int BuildColumn(Span<Layer> column, float surface, float steepness, float high, float valley,
            float datum, TerrainGenSettings settings)
        {
            var total = surface - datum;
            if (total <= MinLayerThickness)
            {
                column[0] = new Layer(MaterialTable.Bedrock, Mathf.Max(MinLayerThickness, total));
                return 1;
            }

            var underwater = surface < World.SeaLevel;
            // Ground that averages better than two thirds of a metre a cell is too steep to hold
            // soil: that is a slope of roughly thirty-five degrees.
            var steep = steepness > 0.67f;
            // A beach is low ground that is not steep; a steep shore is rock to the waterline.
            var coastal = !steep && Mathf.Abs(surface - World.SeaLevel) <= settings.SandBand;

            var topsoil = underwater || coastal || steep ? 0f : settings.TopsoilThickness;
            var sand = underwater || coastal ? settings.SandThickness : 0f;
            if (underwater && steep)
                sand *= 0.3f;
            var dirt = underwater ? 0.4f : steep ? settings.DirtOnSlopes : settings.DirtOnPlains;
            if (coastal && !underwater)
                dirt *= 0.5f;
            var clay = valley > 0.35f && !underwater ? settings.ClayInValleys * valley : 0f;
            var granite = high > 0.05f ? high * settings.RidgeHeight * settings.GraniteShare : 0f;

            var cap = topsoil + sand + dirt + clay;
            var maxCap = Mathf.Max(0f, total - MinLayerThickness);
            if (cap > maxCap)
            {
                var scale = maxCap / Mathf.Max(cap, 1e-4f);
                topsoil *= scale;
                sand *= scale;
                dirt *= scale;
                clay *= scale;
                cap = maxCap;
            }

            var rockRoom = total - cap;
            granite = Mathf.Clamp(granite, 0f, Mathf.Max(0f, rockRoom - settings.RockCover));
            var rock = Mathf.Min(rockRoom - granite, settings.RockCover + granite * 0.2f);
            if (rock < MinLayerThickness)
                rock = 0f;
            var bedrock = total - cap - granite - rock;
            if (bedrock < MinLayerThickness)
            {
                bedrock = Mathf.Max(MinLayerThickness, total * 0.25f);
                var remainder = total - bedrock;
                var wanted = granite + rock + cap;
                var scale = wanted > 1e-4f ? remainder / wanted : 0f;
                granite *= scale;
                rock *= scale;
                topsoil *= scale;
                sand *= scale;
                dirt *= scale;
                clay *= scale;
            }

            var count = 0;
            column[count++] = new Layer(MaterialTable.Bedrock, bedrock);
            count = Add(column, count, MaterialTable.Granite, granite);
            count = Add(column, count, MaterialTable.Rock, rock);
            count = Add(column, count, MaterialTable.Clay, clay);
            count = Add(column, count, MaterialTable.Dirt, dirt);
            count = Add(column, count, MaterialTable.Sand, sand);
            count = Add(column, count, MaterialTable.Topsoil, topsoil);

            // The surface must land exactly where the height field says: whatever rounding the
            // layers picked up goes into the bedrock below them.
            var built = 0f;
            for (var i = 0; i < count; i++)
                built += column[i].Thickness;
            var drift = total - built;
            if (Mathf.Abs(drift) > 1e-5f)
                column[0] = new Layer(MaterialTable.Bedrock, Mathf.Max(MinLayerThickness, column[0].Thickness + drift));
            return count;
        }

        static int Add(Span<Layer> column, int count, MaterialId material, float thickness)
        {
            if (thickness < MinLayerThickness)
                return count;
            column[count] = new Layer(material, thickness);
            return count + 1;
        }

        // --- noise -----------------------------------------------------------------------------

        static Vector2 Warp(int x, int z, Vector2 offset, float size, float strength)
        {
            var frequency = 1f / Mathf.Max(1f, size);
            var dx = (Mathf.PerlinNoise(offset.x + x * frequency, offset.y + z * frequency) - 0.5f) * 2f * strength;
            var dz = (Mathf.PerlinNoise(offset.y + x * frequency, offset.x + z * frequency) - 0.5f) * 2f * strength;
            return new Vector2(x + dx, z + dz);
        }

        static float Noise(Vector2 at, Vector2 offset, float featureSize)
        {
            var frequency = 1f / Mathf.Max(1f, featureSize);
            return Mathf.PerlinNoise(offset.x + at.x * frequency, offset.y + at.y * frequency);
        }

        /// <summary>Several octaves of Perlin, centred on zero, each twice as fine and half as strong.</summary>
        static float Fbm(Vector2 at, Vector2 offset, float featureSize, int octaves)
        {
            var sum = 0f;
            var weight = 0f;
            var amplitude = 1f;
            var size = featureSize;
            for (var i = 0; i < octaves; i++)
            {
                sum += (Noise(at, offset + new Vector2(i * 37f, i * 91f), size) - 0.5f) * amplitude;
                weight += amplitude;
                amplitude *= 0.5f;
                size *= 0.5f;
            }

            return sum / weight;
        }

        /// <summary>
        /// Ridged multifractal: each octave is folded about its middle, so the field has creases
        /// instead of lumps, and finer octaves are weighted by how high the coarser ones already
        /// are, which keeps the detail on the crests and off the flats.
        /// </summary>
        static float RidgedFbm(Vector2 at, Vector2 offset, float featureSize, int octaves)
        {
            var sum = 0f;
            var total = 0f;
            var weight = 1f;
            var amplitude = 1f;
            var size = featureSize;
            for (var i = 0; i < octaves; i++)
            {
                var signal = 1f - Mathf.Abs(Noise(at, offset + new Vector2(i * 53f, i * 17f), size) * 2f - 1f);
                signal *= signal * weight;
                weight = Mathf.Clamp01(signal * 2f);
                sum += signal * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                size *= 0.5f;
            }

            return total < 1e-4f ? 0f : sum / total;
        }

        static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
