using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        /// <summary>The mix of plains, hills and mountains this island was drawn with (phase 2).</summary>
        public LandMix Mix;

        /// <summary>Metres the mountains' crest was drawn to add at most (phase 4).</summary>
        public float MountainCrest;

        /// <summary>Share of the coast drawn to be cliff, and how high those cliffs stand (phase 4).</summary>
        public float CliffCoastShare;
        public float CliffCoastHeight;

        /// <summary>Land cells raised to fill hollows (phase 3).</summary>
        public int PitsFilled;

        /// <summary>Metres the erosion moved the land on average, over the land it ran on (phase 3).</summary>
        public float ErosionMeanChange;

        /// <summary>The highest cell of the land.</summary>
        public Vector2Int Peak;

        /// <summary>Every river, longest first, in grid space: x and z cell centres, y the channel floor.</summary>
        public readonly List<List<Vector3>> Rivers = new List<List<Vector3>>();

        /// <summary>The main river. Empty when the land made none.</summary>
        public List<Vector3> River => Rivers.Count > 0 ? Rivers[0] : NoRiver;

        /// <summary>Cells the rivers' beds cover, for the sim to treat as water.</summary>
        public readonly List<Vector2Int> RiverCells = new List<Vector2Int>();

        /// <summary>Every river and creek, in the order they were cut: rivers first (see <see cref="RiverChannels"/>).</summary>
        public readonly List<Channel> Channels = new List<Channel>();

        /// <summary>Rivers and creeks the seed drew. Fewer are cut when the land has no room for them.</summary>
        public int RiversWanted;
        public int CreeksWanted;

        /// <summary>
        /// Which cells are channel bed, one per grid cell: 0 none, 1 river, 2 creek. Null when
        /// the island has no channels. Beds are gravel on top (<see cref="MaterialTable.RockLoose"/>).
        /// </summary>
        public byte[] ChannelBeds;

        /// <summary>Where the channels' time went, for perf reporting.</summary>
        public string ChannelStats = "";

        /// <summary>Milliseconds the land took to generate.</summary>
        public double Milliseconds;

        /// <summary>Milliseconds per stage, in order, for perf reporting.</summary>
        public readonly List<KeyValuePair<string, double>> Stages = new List<KeyValuePair<string, double>>();

        /// <summary>The stages as one line: "mask 120, heights 300, ...".</summary>
        public string StageSummary()
        {
            var text = new System.Text.StringBuilder();
            foreach (var stage in Stages)
                text.Append(text.Length > 0 ? ", " : "").Append(stage.Key).Append(' ').Append(stage.Value.ToString("0"));
            return text.ToString();
        }

        internal void Mark(string stage, System.Diagnostics.Stopwatch clock, ref double last)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            Stages.Add(new KeyValuePair<string, double>(stage, now - last));
            last = now;
        }
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
    /// 8. Rivers and creeks — routed from heads on high ground down to water and cut as winding
    ///    channels (see <see cref="RiverChannels"/>).
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

            // The generator works in cells; the settings are in metres. At one-metre cells they
            // are the same thing, so the asset is used as it is.
            if (Mathf.Approximately(grid.CellSize, 1f))
                return GenerateInCells(grid, settings);
            var scaled = settings.ScaledForCells(grid.CellSize);
            try
            {
                return GenerateInCells(grid, scaled);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scaled);
            }
        }

        static IslandMap GenerateInCells(TerrainGrid grid, TerrainGenSettings settings)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastMark = 0d;
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
            var ridgeWarp = Offset(random);
            var materialOffset = Offset(random);

            var radius = TerrainGenerator.DiscRadius(grid);
            var centre = new Vector2(width * 0.5f, depth * 0.5f);

            // The island's own frame. Everything that places the island (the land noise, the
            // ridge, the benches, the height and material noise, the ore) works in coordinates
            // of the frame, not of the grid: a square LandRadius + 2 cells either side of the
            // island's middle, set in the middle of the disc. Only distances are taken in grid
            // coordinates, and they do not care where the frame sits. So the same seed gives the
            // same island on any size of disc, and past LandRadius it is open sea. With
            // LandRadius 0 the frame is the whole disc, and nothing moves.
            var landRadius = settings.LandRadius > 0f ? Mathf.Min(settings.LandRadius, radius) : radius;
            var islandHalf = Mathf.RoundToInt(landRadius) + 2;
            var shift = new Vector2Int(width / 2 - islandHalf, depth / 2 - islandHalf);
            var islandCentre = centre - (Vector2)shift;
            var map = new IslandMap
            {
                Shape = settings.Shape == LandShape.Any ? PickShape(random) : settings.Shape,
            };

            // --- 1: where the land is --------------------------------------------------------
            // The per-cell passes run a row per task: each cell reads only the settings and the
            // noise, and writes only its own slot, so the result is the same as in series.
            // Anything that touches the grid (and so raises its events) stays on this thread.
            var land = new bool[cells];
            var inDisc = new bool[cells];
            var shape = map.Shape;
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), centre) > radius)
                        continue;

                    inDisc[cell] = true;
                    var here = new Vector2(x + 0.5f - shift.x, z + 0.5f - shift.y);
                    var toCentre = Vector2.Distance(here, islandCentre);
                    land[cell] = toCentre <= landRadius
                        && IsLand(here, toCentre, landRadius, islandCentre, landOffset, landWarp, landWarpFine, settings, shape);
                }
            });
            for (var cell = 0; cell < cells; cell++)
                if (!inDisc[cell])
                    grid.SetVoid(cell % width, cell / width, true);

            RemoveSmallBlobs(land, inDisc, width, depth, settings, map.Shape);
            map.Mark("mask", stopwatch, ref lastMark);

            // --- 2-4: height over the land ---------------------------------------------------
            var ridge = RidgeLine(random, islandCentre, landRadius);
            var benches = Benches(random, ridge, islandCentre, landRadius, settings);
            var heights = new float[cells];
            var highGround = new float[cells];
            float[] upland = null;
            float[] cliffCoast = null;
            float[] cliffBands = null;
            if (settings.UseLandTypes)
            {
                upland = BuildTypedHeights(random, map, heights, highGround, land, inDisc, width, depth, shift, ridge, benches,
                    baseOffset, mediumOffset, warpOffset, ridgeOffset, ridgeWarp, settings, out cliffCoast, out cliffBands);
            }
            else Parallel.For(0, depth, z =>
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

                    var warped = Warp(x - shift.x, z - shift.y, warpOffset, settings.WarpSize, settings.WarpStrength);
                    var height = settings.BaseHeight + Fbm(warped, baseOffset, settings.FeatureSize, 4) * settings.BaseRelief;
                    height += (Noise(warped, mediumOffset, settings.MediumSize) - 0.5f) * settings.MediumRelief;
                    // A fine octave over everything: at five to twelve metres it is too small to
                    // read as a hill and too big to be noise, which is exactly what stops a slope
                    // coming out as one flat plane.
                    height += (Fbm(warped, mediumOffset + new Vector2(313f, 77f), settings.DetailSize, 2) * 2f) * settings.DetailRelief;

                    var alongRidge = 1f - DistanceToCurve(warped, ridge) / Mathf.Max(4f, settings.RidgeWidth);
                    if (alongRidge > 0f)
                    {
                        // Ridged multifractal: the noise is folded about its middle so the field
                        // has creases rather than lumps, which is what makes a ridge read as one.
                        //
                        // Warped again before it is sampled, at about 25 m: without that the
                        // creases run in the few directions the lattice allows and the mountain
                        // comes out as a set of big diagonal facets. A medium octave over the
                        // flanks breaks up what is left of them.
                        var crestAt = warped + Direction(warped, ridgeWarp, settings.RidgeWarpSize) * settings.RidgeWarpStrength;
                        var crest = RidgedFbm(crestAt, ridgeOffset, settings.FeatureSize * 0.55f, 4);
                        crest += (Noise(crestAt, ridgeOffset + new Vector2(211f, 97f), settings.RidgeWarpSize * 1.6f) - 0.5f) * 0.22f;
                        highGround[cell] = Smooth(alongRidge) * Mathf.Clamp01(crest);
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
            });

            map.Mark("heights", stopwatch, ref lastMark);

            // --- 5: valleys where the water would run ---------------------------------------
            var accumulation = FlowAccumulation(heights, land, width, depth);
            // With erosion on, the rain carves the valleys. The old cut lowered every cell by the
            // water through it along D8 flow lines, which only run in eight directions, so it
            // scored the land with dead-straight one-cell grooves (and starbursts into ponds).
            // The accumulation is still kept: it decides where valley clay lies.
            if (!settings.Erosion)
                CutValleys(heights, land, accumulation, settings, upland);
            if (settings.Erosion)
                map.ErosionMeanChange = ErodeIsland(heights, land, highGround, width, depth, settings, random.Next(), step);

            map.Mark("valleys", stopwatch, ref lastMark);

            // --- 6: the coast and the shelf --------------------------------------------------
            var toLandForSea = ShapeCoast(heights, land, inDisc, width, depth, radius, centre, settings, cliffCoast);
            var reef = settings.OpenSeaFloor
                ? SeabedShaper.Shape(heights, land, inDisc, toLandForSea, width, depth, radius, centre, settings)
                : null;

            map.Mark("coast", stopwatch, ref lastMark);

            // --- 7: quantise and relax -------------------------------------------------------
            // Settle first, at full resolution and in all eight directions, to just under the step
            // the relaxation enforces. Otherwise the relaxation does the settling itself, a row or
            // a column at a time, and a steep hillside comes out combed into straight grooves.
            if (cliffCoast != null)
                ShapeCliffFaces(heights, land, inDisc, cliffCoast, width, depth, settings.MaxCliffStep, settings.ShelfNearDepth * 0.7f);
            if (settings.Erosion && settings.SettleIterations > 0)
            {
                // A rise per cell, from the angle: the settling works in metres across one cell,
                // and a talus angle means nothing until it knows how wide a cell is.
                var rockRise = Mathf.Tan(Mathf.Clamp(settings.TalusRock, 10f, 85f) * Mathf.Deg2Rad)
                    * Mathf.Max(0.05f, settings.GenerationCellSize);
                SettleSlopes(heights, land, upland, cliffBands, width, depth, step, rockRise,
                    settings.MaxCliffStep, settings.SettleIterations);
                if (upland != null && settings.CreaseSoftenPasses > 0)
                    SoftenCreases(heights, land, upland, width, depth, step, settings.CreaseSoftenPasses, settings.CreaseSoften);
            }

            var isLand = new bool[cells];
            for (var cell = 0; cell < cells; cell++)
            {
                heights[cell] = Mathf.Round(heights[cell] / step) * step;
                isLand[cell] = land[cell];
            }

            // Where the land wants to stand up, it is allowed to: a cliff mask from the slope of
            // the field before it is relaxed, so the relaxation knows which faces are rock before
            // there are any materials to ask.
            var cliff = CliffMask(heights, inDisc, width, depth, settings, step, cliffBands);
            if (cliffCoast != null)
                MarkCliffCoast(cliff, cliffCoast, land, width, depth, Mathf.CeilToInt(4f / Mathf.Max(0.1f, settings.GenerationCellSize)));
            var sweeps = Relax(heights, isLand, cliff, width, depth, step, settings.MaxCliffStep);

            map.Mark("relax", stopwatch, ref lastMark);

            // --- 8: shores ------------------------------------------------------------------
            for (var cell = 0; cell < cells; cell++)
                isLand[cell] = land[cell] && heights[cell] >= World.SeaLevel;
            sweeps = Mathf.Max(sweeps, Relax(heights, isLand, cliff, width, depth, step, settings.MaxCliffStep));

            // The mask's cleanup was about where land was meant to be; this one is about where it
            // ended up, because relaxing, beaching and carving all move cells across the waterline.
            CleanUpShores(heights, inDisc, width, depth, settings, map.Shape, step, fillPockets: true);
            for (var cell = 0; cell < cells; cell++)
                isLand[cell] = heights[cell] >= World.SeaLevel;
            sweeps = Mathf.Max(sweeps, Relax(heights, isLand, cliff, width, depth, step, settings.MaxCliffStep));
            // Once more, dropping specks only: relaxing lowers cells, which can cut a corner of
            // land off from the rest, and a one-cell island is not somewhere to play.
            CleanUpShores(heights, inDisc, width, depth, settings, map.Shape, step, fillPockets: false);
            var downstream = new int[cells];
            var fillLandCopy = settings.FillPits ? null : new bool[cells];
            var outletsCopy = settings.FillPits ? null : new bool[cells];
            if (settings.FillPits)
            {
                // Every hollow on land is filled to where it spills, working up from the water
                // (the sea and any lake) and the edge of the disc.
                var fillLand = new bool[cells];
                var outlets = new bool[cells];
                for (var cell = 0; cell < cells; cell++)
                {
                    fillLand[cell] = inDisc[cell] && heights[cell] >= World.SeaLevel;
                    outlets[cell] = inDisc[cell] && heights[cell] < World.SeaLevel;
                }

                map.PitsFilled = TerrainErosion.FillDepressions(heights, width, depth, fillLand, outlets, step, downstream);
            }
            else
            {
                // The channels still need the way water leaves each cell: flood a copy.
                var copy = (float[])heights.Clone();
                for (var cell = 0; cell < cells; cell++)
                {
                    fillLandCopy[cell] = inDisc[cell] && copy[cell] >= World.SeaLevel;
                    outletsCopy[cell] = inDisc[cell] && copy[cell] < World.SeaLevel;
                }

                TerrainErosion.FillDepressions(copy, width, depth, fillLandCopy, outletsCopy, step, downstream);
            }

            map.Mark("shores", stopwatch, ref lastMark);

            // --- 8b: rivers and creeks -------------------------------------------------------
            // Cut into land that already drains: every hollow is filled, so a route from a head
            // runs downhill all the way, down the flow the fill worked out. A channel falls all the
            // way to its mouth, so cutting it makes no hollow of its own, bar a pool at its head,
            // which is where the spring goes.
            RiverChannels.Carve(map, heights, inDisc, accumulation, downstream, width, depth, settings, step, shift);
            for (var cell = 0; cell < cells; cell++)
                isLand[cell] = heights[cell] >= World.SeaLevel;
            sweeps = Mathf.Max(sweeps, Relax(heights, isLand, cliff, width, depth, step, settings.MaxCliffStep));
            // A mouth cut through a narrow neck can leave a scrap of coast on its own.
            CleanUpShores(heights, inDisc, width, depth, settings, map.Shape, step, fillPockets: false);
            RiverChannels.ReadFloors(map, heights, width);

            map.Mark("channels", stopwatch, ref lastMark);

            // --- 9: surface materials --------------------------------------------------------
            // Its own pass over the finished heights, so what a hillside is made of is decided by
            // the hillside rather than cell by cell as columns are built. See SurfaceMaterials.
            var wet = new bool[cells];
            for (var cell = 0; cell < cells; cell++)
                wet[cell] = inDisc[cell] && heights[cell] < World.SeaLevel + step;
            var toWater = Distance(wet, inDisc, width, depth, from: true);
            var surfaceMaterials = SurfaceMaterials.Assign(heights, inDisc, toWater, width, depth, settings, materialOffset, shift, reef);

            // Both sides of a cliff are rock by definition — the relaxation only lets a step stand
            // where the land was already steep — so the faces are promoted to rock rather than the
            // land being flattened to suit the materials. One material pass, not two: assigning
            // them is the expensive part of generating a map and the budget is half a second.
            SurfaceMaterials.PromoteCliffFaces(surfaceMaterials, heights, inDisc, width, depth, step);
            SurfaceMaterials.Tidy(surfaceMaterials, heights, inDisc, toWater, width, depth, settings);

            // Channel beds are gravel (Ronan, 2026-09-22): grass under clear running water read as
            // bright green water. Last, so no tidying folds a narrow creek bed back into the grass
            // either side of it. Not over stone: where a channel runs down through a rock step,
            // the step is a cliff face and stays rock, or loose gravel would stand as a cliff.
            if (map.ChannelBeds != null)
                for (var cell = 0; cell < cells; cell++)
                    if (map.ChannelBeds[cell] != 0 && inDisc[cell] && !IsStone(surfaceMaterials[cell]))
                        surfaceMaterials[cell] = MaterialTable.RockLoose;

            map.Mark("materials", stopwatch, ref lastMark);

            // --- 10: strata ------------------------------------------------------------------
            // Ore offsets come last off the island's random stream, so turning ores on changed
            // nothing about the land any existing seed already made.
            var oreFields = OreDeposits.Fields.Draw(random);
            // Columns are built a band of rows at a time in parallel, then written to the grid
            // in series, because writing a cell raises the grid's events.
            var peak = -1;
            const int bandRows = 64;
            var perCell = TerrainGrid.MaxLayersPerCell;
            var bandLayers = new Layer[bandRows * width * perCell];
            var bandCounts = new byte[bandRows * width];
            var datum = grid.Datum;
            for (var bandStart = 0; bandStart < depth; bandStart += bandRows)
            {
                var rows = Math.Min(bandRows, depth - bandStart);
                var start = bandStart;
                Parallel.For(0, rows, row =>
                {
                    var z = start + row;
                    Span<Layer> column = stackalloc Layer[TerrainGrid.MaxLayersPerCell];
                    for (var x = 0; x < width; x++)
                    {
                        var cell = z * width + x;
                        var slot = row * width + x;
                        if (!inDisc[cell])
                        {
                            bandCounts[slot] = 0;
                            continue;
                        }

                        var count = BuildColumn(column, heights[cell], surfaceMaterials[cell],
                            highGround[cell], ValleyStrength(accumulation[cell]), datum, settings);
                        OreDeposits.Apply(column, ref count, x - shift.x, z - shift.y, heights[cell], datum, highGround[cell], oreFields, settings);
                        column.Slice(0, count).CopyTo(new Span<Layer>(bandLayers, slot * perCell, perCell));
                        bandCounts[slot] = (byte)count;
                    }
                });

                for (var row = 0; row < rows; row++)
                {
                    var z = start + row;
                    for (var x = 0; x < width; x++)
                    {
                        var cell = z * width + x;
                        if (!inDisc[cell])
                            continue;
                        if (peak < 0 || heights[cell] > heights[peak])
                            peak = cell;
                        var slot = row * width + x;
                        grid.SetColumn(x, z, new ReadOnlySpan<Layer>(bandLayers, slot * perCell, bandCounts[slot]));
                    }
                }
            }

            // Rivers always block the crew, however shallow they run (Ronan, 2026-09-22).
            if (map.ChannelBeds != null)
            {
                var riverBeds = new bool[cells];
                for (var cell = 0; cell < cells; cell++)
                    riverBeds[cell] = map.ChannelBeds[cell] == 1;
                grid.SetRiverBeds(riverBeds);
            }
            else
            {
                // The same grid regenerated: the last island's rivers are not this one's.
                grid.SetRiverBeds(ReadOnlySpan<bool>.Empty);
            }

            map.Mark("strata+ore", stopwatch, ref lastMark);

            if (peak >= 0)
                map.Peak = new Vector2Int(peak % width, peak / width);
            if (sweeps >= MaxRelaxSweeps)
                Debug.LogWarning($"Island: the land was still stepping by more than {step} m after {sweeps} sweeps.");

            map.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return map;
        }

        /// <summary>
        /// Natural terrain, phase 2: heights from land types. A slowly varying type field, pulled
        /// toward mountains along the ridge and toward lowland near the sea, is cut at the seed's
        /// mix into plains, hills and mountains. Each has its own relief:
        /// - plains: the shore height and a gentle rise inland, with swells of a metre or two;
        /// - hills: the plain with broad rolling mounds on it;
        /// - mountains: the hills with the ridged crest, and the medium and fine octaves.
        /// The three blend over the borders, so a plain climbs into foothills rather than hitting
        /// a wall. Benches still pull the land onto their floors. Returns how much each cell is
        /// hills or mountains (1 minus its plains weight), which the valley cut scales by.
        /// </summary>
        static float[] BuildTypedHeights(System.Random random, IslandMap map, float[] heights, float[] highGround,
            bool[] land, bool[] inDisc, int width, int depth, Vector2Int shift, Vector2[] ridge, Vector3[] benches,
            Vector2 baseOffset, Vector2 mediumOffset, Vector2 warpOffset, Vector2 ridgeOffset, Vector2 ridgeWarp,
            TerrainGenSettings settings, out float[] cliffCoast, out float[] cliffBands)
        {
            var cells = width * depth;
            var cliffWeights = new float[cells];
            cliffCoast = cliffWeights;
            var typeOffset = Offset(random);
            var hillsOffset = Offset(random);
            var cliffOffset = Offset(random);
            map.Mix = LandTypes.Draw(random, settings);
            map.CliffCoastShare = Mathf.Lerp(settings.CliffCoastShareMin, settings.CliffCoastShareMax, (float)random.NextDouble());
            map.CliffCoastHeight = Mathf.Lerp(settings.CliffCoastHeightMin, settings.CliffCoastHeightMax, (float)random.NextDouble());
            map.MountainCrest = Mathf.Lerp(settings.MountainCrestMin, settings.MountainCrestMax, (float)random.NextDouble());
            var toSea = Distance(land, inDisc, width, depth, from: false);

            // The type field over the land.
            var type = new float[cells];
            var upland = new float[cells];
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell])
                        continue;
                    var warped = Warp(x - shift.x, z - shift.y, warpOffset, settings.WarpSize, settings.WarpStrength);
                    var value = Fbm(warped, typeOffset, settings.TypeFeatureSize, 3) + 0.5f;
                    var alongRidge = Mathf.Clamp01(1f - DistanceToCurve(warped, ridge) / Mathf.Max(4f, settings.RidgeWidth));
                    value += settings.RidgeMountainBias * Smooth(alongRidge);
                    value -= settings.CoastLowlandBias * Mathf.Exp(-toSea[cell] / Mathf.Max(1f, settings.CoastLowlandDistance));
                    type[cell] = value;
                }
            });

            // Cut points from a sample of the land, so the shares come out as drawn.
            var sample = new float[Math.Min(cells, 200000)];
            var count = 0;
            var stride = Math.Max(1, cells / sample.Length);
            for (var cell = 0; cell < cells && count < sample.Length; cell += stride)
                if (land[cell])
                    sample[count++] = type[cell];
            var (plainsBelow, mountainsAbove) = LandTypes.Thresholds(sample, count, map.Mix);

            // Cliff coasts (phase 4): a slow field along the coast, pulled up where hills and
            // mountains are, cut so the drawn share of the shore is cliff. Only the open sea's
            // coast: a cliff round a lake stood up as a rampart round it.
            var toOcean = Distance(OpenSea(land, inDisc, width, depth), inDisc, width, depth, from: true);
            var cliffField = new float[cells];
            var reachInland = settings.CliffCoastInland * 3f;
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell] || toOcean[cell] > reachInland)
                        continue;
                    var at = new Vector2(x - shift.x, z - shift.y);
                    var (plainsHere, _, _) = LandTypes.Weights(type[cell], plainsBelow, mountainsAbove, settings.TypeBlend);
                    cliffField[cell] = Fbm(at, cliffOffset, settings.CliffCoastSize, 2) + 0.5f + settings.CliffUplandBias * (1f - plainsHere);
                }
            });
            var shoreCount = 0;
            for (var cell = 0; cell < cells && shoreCount < sample.Length; cell += Math.Max(1, stride / 8))
                if (land[cell] && toOcean[cell] <= 2f)
                    sample[shoreCount++] = cliffField[cell];
            var (_, cliffAbove) = LandTypes.Thresholds(sample, shoreCount, new LandMix(0f, 1f - map.CliffCoastShare, map.CliffCoastShare));

            Parallel.For(0, depth, z =>
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

                    var (plains, hills, mountains) = LandTypes.Weights(type[cell], plainsBelow, mountainsAbove, settings.TypeBlend);
                    var warped = Warp(x - shift.x, z - shift.y, warpOffset, settings.WarpSize, settings.WarpStrength);

                    var rise = settings.InlandRise * (1f - Mathf.Exp(-toSea[cell] / Mathf.Max(1f, settings.InlandRiseDistance)));
                    var plain = settings.ShoreHeight + rise + Fbm(warped, baseOffset, settings.PlainsFeatureSize, 2) * 2f * settings.PlainsRelief;

                    // Only what has weight here is worked out: most land is plain, where the hill
                    // and mountain noise counted for nothing but cost most of the stage.
                    var hill = plain;
                    if (plains < 1f)
                    {
                        var mound = Smooth(Fbm(warped, hillsOffset, settings.HillsFeatureSize, 3) * 1.6f + 0.5f);
                        hill = plain + mound * settings.HillsRelief;
                    }

                    // Mountains: the ridged crest, stronger on the ridge line, and the finer octaves.
                    var mountain = hill;
                    var peak = 0f;
                    if (mountains > 0f)
                    {
                        var crestAt = warped + Direction(warped, ridgeWarp, settings.RidgeWarpSize) * settings.RidgeWarpStrength;
                        var crest = RidgedFbm(crestAt, ridgeOffset, settings.FeatureSize * 0.55f, 4);
                        crest += (Noise(crestAt, ridgeOffset + new Vector2(211f, 97f), settings.RidgeWarpSize * 1.6f) - 0.5f) * 0.22f;
                        var alongRidge = Mathf.Clamp01(1f - DistanceToCurve(warped, ridge) / Mathf.Max(4f, settings.RidgeWidth));
                        peak = Mathf.Clamp01(crest) * (0.55f + 0.45f * Smooth(alongRidge));
                        var roughness = (Noise(warped, mediumOffset, settings.MediumSize) - 0.5f) * settings.MediumRelief
                            + Fbm(warped, mediumOffset + new Vector2(313f, 77f), settings.DetailSize, 2) * 2f * settings.DetailRelief;
                        mountain = hill + peak * map.MountainCrest + roughness;
                    }

                    var height = plains * plain + hills * hill + mountains * mountain;
                    highGround[cell] = mountains * peak;

                    // A cliff coast: the land stands at the cliff's height right to the sea, and
                    // eases back to what is behind it over as far again inland. The top is rock,
                    // so erosion and settling leave the face standing.
                    if (toOcean[cell] <= reachInland)
                    {
                        // Blended wide: a narrow blend switched a stretch of cliff on within a few
                        // metres along the coast and left a wall running inland from its end.
                        var cliff = Smooth((cliffField[cell] - cliffAbove) / settings.CliffCoastBlend + 0.5f);
                        cliffWeights[cell] = cliff;
                        if (cliff > 0f)
                        {
                            var inland = toOcean[cell] / Mathf.Max(1f, settings.CliffCoastInland);
                            var top = map.CliffCoastHeight * (1f - Smooth(inland - 1f)) + settings.ShoreHeight;
                            var lifted = Mathf.Max(height, top);
                            height = Mathf.Lerp(height, lifted, cliff);
                            highGround[cell] = Mathf.Max(highGround[cell], 0.5f * cliff * (1f - Smooth(inland - 0.5f)));
                        }
                    }
                    upland[cell] = 1f - plains;

                    foreach (var bench in benches)
                    {
                        var t = 1f - Vector2.Distance(warped, new Vector2(bench.x, bench.y)) / Mathf.Max(4f, settings.PlateauRadius);
                        if (t <= 0f)
                            continue;
                        // Only where the land is already that high: a bench is a shelf in the
                        // hills, not a table lifted out of a plain.
                        if (height < bench.z * 0.6f)
                            continue;
                        height = Mathf.Lerp(height, bench.z, Smooth(t));
                    }

                    heights[cell] = height;
                }
            });

            // Cliff bands: where the mountains are allowed to stand up. A slow field over the high
            // ground, cut so that the drawn share of it is band and the rest settles to rock's own
            // talus — a flank with rock bands in it rather than one wall (Ronan, 2026-09-24).
            // The offset is derived rather than drawn, so adding this does not shift every other
            // feature's noise along the random stream.
            var bands = new float[cells];
            cliffBands = bands;
            var bandOffset = cliffOffset + new Vector2(517f, 233f);
            var bandField = new float[cells];
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell] || upland[cell] <= 0f)
                        continue;
                    bandField[cell] = Fbm(new Vector2(x - shift.x, z - shift.y), bandOffset, settings.CliffBandSize, 2) + 0.5f;
                }
            });

            var bandCount = 0;
            for (var cell = 0; cell < cells && bandCount < sample.Length; cell += Math.Max(1, stride / 4))
                if (land[cell] && upland[cell] > 0.5f)
                    sample[bandCount++] = bandField[cell];
            var share = Mathf.Clamp01(settings.CliffBandShare);
            var (_, bandAbove) = LandTypes.Thresholds(sample, bandCount, new LandMix(0f, 1f - share, share));
            var blend = Mathf.Max(0.01f, settings.CliffBandBlend);
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell] || upland[cell] <= 0f)
                        continue;
                    // Fades out with the mountain, so a band cannot run on into the lowlands.
                    bands[cell] = Smooth((bandField[cell] - bandAbove) / blend + 0.5f)
                        * Mathf.Clamp01(upland[cell] * 2f);
                }
            });

            return upland;
        }

        /// <summary>
        /// Natural terrain, phase 3: weathers the land on a coarser grid of
        /// <see cref="TerrainGenSettings.ErosionCellSize"/> and adds the change back. Rain first,
        /// then the slopes settle, soil to <see cref="TerrainGenSettings.TalusSoil"/> and the
        /// mountains to <see cref="TerrainGenSettings.TalusRock"/>. Only the change is scaled back
        /// up, so the fine shape of the land underneath is kept. The sea does not move, and land
        /// is never eroded below one height step above the sea, where land starts, so it stays land
        /// whatever the step (a fixed 0.3 m rounded to the sea with 1 m steps and drowned the lowlands). Returns the mean change in metres over the
        /// land it ran on.
        /// </summary>
        static float ErodeIsland(float[] heights, bool[] land, float[] highGround, int width, int depth,
            TerrainGenSettings settings, int seed, float step)
        {
            var f = Mathf.Max(1, Mathf.RoundToInt(settings.ErosionCellSize));
            int minX = width, minZ = depth, maxX = -1, maxZ = -1;
            for (var z = 0; z < depth; z++)
                for (var x = 0; x < width; x++)
                    if (land[z * width + x])
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                    }

            if (maxX < 0)
                return 0f;
            minX = Math.Max(0, minX - 2 * f); minZ = Math.Max(0, minZ - 2 * f);
            maxX = Math.Min(width - 1, maxX + 2 * f); maxZ = Math.Min(depth - 1, maxZ + 2 * f);
            var cw = (maxX - minX) / f + 1;
            var cd = (maxZ - minZ) / f + 1;
            var coarse = new float[cw * cd];
            var fixedCells = new bool[cw * cd];
            var rock = new float[cw * cd];
            var landCoarse = 0;
            Parallel.For(0, cd, cz =>
            {
                for (var cx = 0; cx < cw; cx++)
                {
                    float sum = 0f, high = 0f;
                    int count = 0, landCount = 0;
                    for (var dz = 0; dz < f; dz++)
                        for (var dx = 0; dx < f; dx++)
                        {
                            var x = minX + cx * f + dx;
                            var z = minZ + cz * f + dz;
                            if (x > maxX || z > maxZ)
                                continue;
                            var cell = z * width + x;
                            sum += heights[cell];
                            count++;
                            if (land[cell])
                            {
                                landCount++;
                                high += highGround[cell];
                            }
                        }

                    var c = cz * cw + cx;
                    coarse[c] = count > 0 ? sum / count : World.SeaLevel - 1f;
                    // Mostly sea is sea: fixed, so the coastline is not eroded away.
                    fixedCells[c] = landCount * 2 < count || cx == 0 || cz == 0 || cx == cw - 1 || cz == cd - 1;
                    rock[c] = landCount > 0 ? high / landCount : 0f;
                }
            });
            for (var c = 0; c < coarse.Length; c++)
                if (!fixedCells[c])
                    landCoarse++;

            var before = (float[])coarse.Clone();
            var metres = settings.ErosionCellSize * settings.GenerationCellSize;
            var km2 = landCoarse * metres * metres / 1e6f;
            var drops = Mathf.RoundToInt(settings.ErosionDropletsPerKm2 * km2);
            // The droplet maths wants heights per erosion cell, not per metre: scale in and out.
            for (var c = 0; c < coarse.Length; c++)
                coarse[c] /= metres;
            TerrainErosion.Droplets(coarse, cw, cd, fixedCells, drops, seed, settings.Droplets);
            for (var c = 0; c < coarse.Length; c++)
                coarse[c] *= metres;

            var talus = new float[coarse.Length];
            var soil = Mathf.Tan(settings.TalusSoil * Mathf.Deg2Rad) * metres;
            var stone = Mathf.Tan(settings.TalusRock * Mathf.Deg2Rad) * metres;
            for (var c = 0; c < coarse.Length; c++)
                talus[c] = Mathf.Lerp(soil, stone, Mathf.Clamp01(rock[c] * 3f));
            TerrainErosion.Thermal(coarse, cw, cd, fixedCells, talus, settings.ThermalIterations);

            var change = new float[coarse.Length];
            var moved = 0.0;
            for (var c = 0; c < coarse.Length; c++)
            {
                change[c] = coarse[c] - before[c];
                if (!fixedCells[c])
                    moved += Math.Abs(change[c]);
            }


            // Back up to the grid: the change, sampled bilinearly at each cell's centre.
            Parallel.For(minZ, maxZ + 1, z =>
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell])
                        continue;
                    var gx = Mathf.Clamp((x - minX + 0.5f) / f - 0.5f, 0f, cw - 1.001f);
                    var gz = Mathf.Clamp((z - minZ + 0.5f) / f - 0.5f, 0f, cd - 1.001f);
                    var ix = (int)gx;
                    var iz = (int)gz;
                    var tx = gx - ix;
                    var tz = gz - iz;
                    var ix1 = Math.Min(cw - 1, ix + 1);
                    var iz1 = Math.Min(cd - 1, iz + 1);
                    var d = change[iz * cw + ix] * (1 - tx) * (1 - tz) + change[iz * cw + ix1] * tx * (1 - tz)
                          + change[iz1 * cw + ix] * (1 - tx) * tz + change[iz1 * cw + ix1] * tx * tz;
                    heights[cell] = Mathf.Max(World.SeaLevel + step, heights[cell] + d);
                }
            });
            return landCoarse > 0 ? (float)(moved / landCoarse) : 0f;
        }

        /// <summary>
        /// Settles the land's slopes, at full resolution, to 0.9 of a height step a cell (soil) or
        /// of a cliff step (the mountains), on a window round the land. The sea is fixed.
        /// </summary>
        static void SettleSlopes(float[] heights, bool[] land, float[] upland, float[] cliffBands, int width, int depth,
            float step, float rockRise, float cliffStep, int iterations)
        {
            int minX = width, minZ = depth, maxX = -1, maxZ = -1;
            for (var z = 0; z < depth; z++)
                for (var x = 0; x < width; x++)
                    if (land[z * width + x])
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                    }

            if (maxX < 0)
                return;
            minX = Math.Max(0, minX - 1); minZ = Math.Max(0, minZ - 1);
            maxX = Math.Min(width - 1, maxX + 1); maxZ = Math.Min(depth - 1, maxZ + 1);
            var w = maxX - minX + 1;
            var d = maxZ - minZ + 1;
            var window = new float[w * d];
            var fixedCells = new bool[w * d];
            var talus = new float[w * d];
            Parallel.For(0, d, z =>
            {
                for (var x = 0; x < w; x++)
                {
                    var cell = (minZ + z) * width + minX + x;
                    var i = z * w + x;
                    window[i] = heights[cell];
                    fixedCells[i] = !land[cell];
                    // What a cell is allowed to stand at, in metres of rise per cell. Soil keeps
                    // the height step; mountain rock stands at its own talus; only a cell inside a
                    // cliff band gets the cliff angle.
                    //
                    // This used to read `Lerp(step, cliffStep, highGround * 3)`, so every cell
                    // where the crest noise reached a third of its height stood at the cliff
                    // angle — which is the whole massif, not a band in it, and is why mountains
                    // had no footslope at all (2026-09-24).
                    var rock = upland == null ? 0f : Mathf.Clamp01(upland[cell]);
                    var band = cliffBands == null ? 0f : Mathf.Clamp01(cliffBands[cell]);
                    talus[i] = 0.9f * Mathf.Max(Mathf.Lerp(step, rockRise, rock), Mathf.Lerp(step, cliffStep, band));
                }
            });
            TerrainErosion.Thermal(window, w, d, fixedCells, talus, iterations);
            Parallel.For(0, d, z =>
            {
                for (var x = 0; x < w; x++)
                {
                    var cell = (minZ + z) * width + minX + x;
                    if (land[cell])
                        heights[cell] = Mathf.Max(World.SeaLevel + step, window[z * w + x]);
                }
            });
        }

        /// <summary>
        /// Scree at the foot of a face, and a top on a knife-edge crest, on the high ground (Ronan,
        /// 2026-09-24: "fix the spiky ridge teeth"). A cell sharply below the mean of its eight
        /// neighbours is at the foot of something steep: it is filled a share of the way up,
        /// beyond one height step, which over a few passes builds an apron. A cell more than a step
        /// above both of its neighbours across any line is a knife: it is taken down to one step
        /// over the higher of them. A plane, however steep, is its own neighbours' mean and is left
        /// alone, so a cliff face stays a cliff.
        ///
        /// It works with the crest rule in <see cref="CliffMask"/>, not instead of it: the mask
        /// decides what the relaxation may build, this takes the edge off what the settling left.
        /// </summary>
        static void SoftenCreases(float[] heights, bool[] land, float[] upland, int width, int depth,
            float step, int passes, float strength)
        {
            var current = heights;
            var next = new float[heights.Length];
            for (var pass = 0; pass < passes; pass++)
            {
                var from = current;
                var to = next;
                Parallel.For(0, depth, z =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var cell = z * width + x;
                        var h = from[cell];
                        to[cell] = h;
                        if (!land[cell] || x == 0 || z == 0 || x == width - 1 || z == depth - 1)
                            continue;
                        var weight = Mathf.Clamp01(upland[cell] * 2f);
                        if (weight <= 0f)
                            continue;
                        var sum = 0f;
                        var count = 0;
                        for (var dz = -1; dz <= 1; dz++)
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0)
                                    continue;
                                var other = (z + dz) * width + x + dx;
                                if (!land[other])
                                    continue;
                                sum += from[other];
                                count++;
                            }

                        if (count < 8)
                            continue;
                        var below = sum / count - h;
                        if (below > step)
                        {
                            to[cell] = h + strength * weight * (below - step);
                            continue;
                        }

                        // A knife: higher than both of its neighbours across some line by more than
                        // a step each (diagonals by a step and a half, being further away).
                        var knife = 0f;
                        for (var axis = 0; axis < 4; axis++)
                        {
                            var ax = axis == 1 ? 0 : 1;
                            var az = axis == 0 ? 0 : axis == 3 ? -1 : 1;
                            var reach = axis < 2 ? step : step * 1.41421356f;
                            var over = h - Mathf.Max(from[(z + az) * width + x + ax], from[(z - az) * width + x - ax]) - reach;
                            if (over > knife)
                                knife = over;
                        }

                        if (knife > 0f)
                            to[cell] = h - weight * knife;
                    }
                });
                current = to;
                next = from;
            }

            if (!ReferenceEquals(current, heights))
                Array.Copy(current, heights, heights.Length);
        }

        /// <summary>
        /// Lets a cliff coast stand: its land and the sea in front of it, up to
        /// <paramref name="reach"/> cells out, count as cliff for the relaxation. It then steps the
        /// face down a cliff's worth a cell into the sea, instead of a soil step. A soil step
        /// dragged the coast down to the shelf and left a bank no higher than a beach.
        /// </summary>
        static void MarkCliffCoast(bool[] cliff, float[] cliffCoast, bool[] land, int width, int depth, int reach)
        {
            var zone = new bool[cliff.Length];
            for (var z = 0; z < depth; z++)
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell] || cliffCoast[cell] <= 0.5f)
                        continue;
                    zone[cell] = true;
                    // Only the land at the shore spreads out to sea.
                    var atShore = false;
                    for (var n = 0; n < 4 && !atShore; n++)
                    {
                        var nx = x + StepX[n];
                        var nz = z + StepZ[n];
                        atShore = nx >= 0 && nz >= 0 && nx < width && nz < depth && !land[nz * width + nx];
                    }

                    if (!atShore)
                        continue;
                    for (var dz = -reach; dz <= reach; dz++)
                        for (var dx = -reach; dx <= reach; dx++)
                        {
                            var ax = x + dx;
                            var az = z + dz;
                            if (ax < 0 || az < 0 || ax >= width || az >= depth || dx * dx + dz * dz > reach * reach)
                                continue;
                            if (!land[az * width + ax])
                                zone[az * width + ax] = true;
                        }
                }

            for (var cell = 0; cell < cliff.Length; cell++)
                cliff[cell] |= zone[cell];
        }

        /// <summary>
        /// Gives a cliff coast its face: land may stand no higher than the shelf at its foot plus
        /// 0.9 of a cliff step for every cell (in a straight line) from the open sea. The face
        /// then comes down evenly in every direction. Left to the relaxation, a ten-metre drop
        /// straight onto the shelf was cut back a row or a column at a time and came out combed
        /// into teeth.
        /// </summary>
        static void ShapeCliffFaces(float[] heights, bool[] land, bool[] inDisc, float[] cliffCoast, int width, int depth,
            float cliffStep, float shelfTop)
        {
            var toOcean = Distance(OpenSea(land, inDisc, width, depth), inDisc, width, depth, from: true, reach: 64f);
            var rise = 0.9f * cliffStep;
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!land[cell] || cliffCoast[cell] <= 0f || toOcean[cell] >= float.MaxValue)
                        continue;
                    var face = shelfTop + toOcean[cell] * rise;
                    if (heights[cell] > face)
                        heights[cell] = Mathf.Lerp(heights[cell], face, Mathf.Clamp01(cliffCoast[cell] * 2f));
                }
            });
        }

        /// <summary>
        /// Water joined to the rim of the disc, as a land flag array for <see cref="Distance"/>:
        /// true is open sea. A lake inside the land is not.
        /// </summary>
        static bool[] OpenSea(bool[] land, bool[] inDisc, int width, int depth)
        {
            var sea = new bool[land.Length];
            var queue = new Queue<int>();
            for (var cell = 0; cell < land.Length; cell++)
            {
                if (!inDisc[cell] || land[cell])
                    continue;
                var x = cell % width;
                var z = cell / width;
                var edge = false;
                for (var n = 0; n < 4 && !edge; n++)
                {
                    var nx = x + StepX[n];
                    var nz = z + StepZ[n];
                    edge = nx < 0 || nz < 0 || nx >= width || nz >= depth || !inDisc[nz * width + nx];
                }

                if (edge)
                {
                    sea[cell] = true;
                    queue.Enqueue(cell);
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
                    if (sea[next] || !inDisc[next] || land[next])
                        continue;
                    sea[next] = true;
                    queue.Enqueue(next);
                }
            }

            return sea;
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
                land[cell] = heights[cell] >= World.SeaLevel + step;

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
            var landCount = 0;
            for (var cell = 0; cell < cells; cell++)
                if (land[cell])
                    landCount++;

            // Highest first: sorted on a key array (the negated heights), which is several times
            // faster than a comparison delegate over a million cells.
            var order = new int[landCount];
            var keys = new float[landCount];
            var filled = 0;
            for (var cell = 0; cell < cells; cell++)
            {
                if (!land[cell])
                    continue;
                accumulation[cell] = 1f;
                order[filled] = cell;
                keys[filled] = -heights[cell];
                filled++;
            }

            Array.Sort(keys, order);
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

        /// <summary>Cuts each cell down by how much water runs through it, less on plains.</summary>
        static void CutValleys(float[] heights, bool[] land, float[] accumulation, TerrainGenSettings settings, float[] upland = null)
        {
            if (settings.ValleyCut <= 0f)
                return;
            for (var cell = 0; cell < heights.Length; cell++)
            {
                if (!land[cell])
                    continue;
                var strength = ValleyStrength(accumulation[cell]);
                // On plains a river runs in a shallow bed, not a gorge.
                if (upland != null)
                    strength *= 0.2f + 0.8f * upland[cell];
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
        static float[] ShapeCoast(float[] heights, bool[] land, bool[] inDisc, int width, int depth,
            float radius, Vector2 centre, TerrainGenSettings settings, float[] cliffCoast = null)
        {
            // Each flood only as far as anything reads it: the beach, and the shelf plus the sea
            // floor's fade from the shore. Past that a cell reads as "far" either way.
            var toWater = Distance(land, inDisc, width, depth, from: false, reach: Mathf.Max(1, settings.BeachCells) + 1f);
            var seaReach = Mathf.Max(3f * Mathf.Max(1, settings.ShallowCells), settings.OpenSeaFloor ? 2f * settings.SeabedShoreGap : 0f) + 2f;
            var toLand = Distance(land, inDisc, width, depth, from: true, reach: seaReach);

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
                        // is pulled down onto a beach that rises from the waterline. A cliff coast
                        // keeps its height too, though its top is flat: pulled onto a beach, it was.
                        if (Steepness(heights, width, depth, x, z) > settings.BeachMaxSlope)
                            continue;
                        if (cliffCoast != null && cliffCoast[cell] > 0.5f)
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

            return toLand;
        }

        /// <summary>
        /// Cells, in a straight line, to the nearest cell whose land flag is <paramref name="from"/>.
        /// Cells further than <paramref name="reach"/> get float.MaxValue, which every caller reads
        /// as "far".
        /// </summary>
        static float[] Distance(bool[] land, bool[] inDisc, int width, int depth, bool from, float reach = float.MaxValue)
        {
            // Straight-line distance (phase 3). This was a four-way flood, which measures
            // city-block distance: its equal-distance lines are diamonds, and the beaches, the
            // shelf and the inland rise built on it came out in straight lines and chevrons.
            var seed = new bool[land.Length];
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    seed[cell] = inDisc[cell] && land[cell] == from;
                }
            });
            return TerrainErosion.EuclideanDistance(seed, width, depth, reach);
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
        /// <summary>Rock, granite and bedrock stand up; everything else slumps.</summary>
        public static bool IsStone(MaterialId material) => MaterialTable.IsStone(material);

        /// <summary>
        /// Which cells are allowed to stand in a cliff: the ones the land is already steep at,
        /// measured on a smoothed field so the answer is about the hillside rather than about one
        /// cell of a staircase. These are the cells the material pass will call rock.
        /// </summary>
        static bool[] CliffMask(float[] heights, bool[] inDisc, int width, int depth, TerrainGenSettings settings, float step,
            float[] cliffBands)
        {
            var smoothed = SurfaceMaterials.Smooth(heights, inDisc, width, depth, Mathf.Max(1, settings.SlopeSmoothing));
            var cliff = new bool[heights.Length];
            // A crest is never a cliff: a cell standing well above the ring of cells round it is
            // on a summit, however steep the ground is there. The relaxation after this builds
            // every mountain up from its foot at whatever this mask allows, and where the noise
            // asks for more than that (it asks for 80 degrees by a ridge) the summit it makes is
            // entirely its own: diamond pyramids at the full cliff step with a crest one cell
            // wide, and a crest not square to the grid steps sideways every few cells into a row
            // of teeth — measured in the game at 1.5 m a cell for twelve cells up to a knife
            // (Ronan, 2026-09-24: "fix the spiky ridge teeth"). At the ordinary step a crest's
            // stair is half a metre, too small to read; the faces below it keep their cliffs.
            var ring = Mathf.Max(1, Mathf.RoundToInt(settings.CliffCrestReach / Mathf.Max(0.05f, settings.GenerationCellSize)));
            var proud = ring * step;
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    // Only a cliff band may stand as a cliff (Ronan, 2026-09-24: real talus, cliffs
                    // as bands). This mask used to be steepness alone, and the noise is steep all
                    // along every ridge, so every ridge was built as a cliff and the bands the
                    // settling had picked counted for nothing here, where the final shape is made.
                    if (!inDisc[cell] || cliffBands != null && cliffBands[cell] < 0.5f
                        || SurfaceMaterials.SlopeDegrees(smoothed, inDisc, width, depth, x, z, settings.GenerationCellSize) < settings.CliffSlope)
                        continue;
                    var sum = 0f;
                    var count = 0;
                    for (var dz = -ring; dz <= ring; dz++)
                        for (var dx = -ring; dx <= ring; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                                continue;
                            var ax = x + dx;
                            var az = z + dz;
                            if (ax < 0 || az < 0 || ax >= width || az >= depth || !inDisc[az * width + ax])
                                continue;
                            sum += heights[az * width + ax];
                            count++;
                        }

                    cliff[cell] = count == 0 || heights[cell] - sum / count <= proud;
                }
            });
            return cliff;
        }

        static int Relax(float[] heights, bool[] isLand, bool[] cliff, int width, int depth, float step, float cliffStep)
        {
            // Only land moves, so each row is swept only across the span its land covers, in the
            // same order as before: the cells skipped were no-ops. On the 3104² disc most of every
            // row is sea, and the full sweeps cost over a second.
            var first = new int[depth];
            var last = new int[depth];
            Parallel.For(0, depth, z =>
            {
                first[z] = width;
                last[z] = -1;
                for (var x = 0; x < width; x++)
                {
                    if (!isLand[z * width + x])
                        continue;
                    first[z] = Math.Min(first[z], x);
                    last[z] = x;
                }
            });

            for (var sweep = 0; sweep < MaxRelaxSweeps; sweep++)
            {
                var moved = false;

                for (var z = 0; z < depth; z++)
                    for (var x = Math.Max(1, first[z]); x <= last[z]; x++)
                        moved |= Pull(heights, isLand, cliff, z * width + x, z * width + x - 1, step, cliffStep);

                for (var z = 0; z < depth; z++)
                    for (var x = Math.Min(width - 2, last[z]); x >= first[z]; x--)
                        moved |= Pull(heights, isLand, cliff, z * width + x, z * width + x + 1, step, cliffStep);

                for (var z = 1; z < depth; z++)
                    for (var x = first[z]; x <= last[z]; x++)
                        moved |= Pull(heights, isLand, cliff, z * width + x, (z - 1) * width + x, step, cliffStep);

                for (var z = depth - 2; z >= 0; z--)
                    for (var x = first[z]; x <= last[z]; x++)
                        moved |= Pull(heights, isLand, cliff, z * width + x, (z + 1) * width + x, step, cliffStep);

                if (!moved)
                    return sweep;
            }

            return MaxRelaxSweeps;
        }

        /// <summary>
        /// Lowers <paramref name="cell"/> until it stands no more than one step above
        /// <paramref name="from"/> — or, where both cells are rock, no more than a cliff's worth.
        ///
        /// That exception is the whole point of it: clamping every steep face to one metre a cell
        /// is what planes a mountain into flat forty-five degree facets. Rock stands up; soil does
        /// not, and keeps the one-metre rule, which is also the rule the slump simulator enforces.
        /// </summary>
        static bool Pull(float[] heights, bool[] isLand, bool[] cliff, int cell, int from, float step, float cliffStep)
        {
            if (!isLand[cell])
                return false;
            var allowed = cliff != null && cliff[cell] && cliff[from] ? cliffStep : step;
            var limit = heights[from] + allowed;
            if (heights[cell] <= limit + 1e-4f)
                return false;
            heights[cell] = Mathf.Round(limit / step) * step;
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

        // --- 9: strata -------------------------------------------------------------------------

        /// <summary>
        /// Builds one column bottom-up for a surface at <paramref name="surface"/>: bedrock from
        /// the datum, granite under the high ground, rock, clay in the valleys, then the cap —
        /// sand on beaches and under water, gravel in channel beds, bare rock where it is too steep
        /// to hold soil, dirt and topsoil everywhere else.
        /// </summary>
        static int BuildColumn(Span<Layer> column, float surface, MaterialId top, float high, float valley,
            float datum, TerrainGenSettings settings)
        {
            var total = surface - datum;
            if (total <= MinLayerThickness)
            {
                column[0] = new Layer(MaterialTable.Bedrock, Mathf.Max(MinLayerThickness, total));
                return 1;
            }

            var underwater = surface < World.SeaLevel;

            // The cap is whatever the surface pass chose, so a hillside is made of what it looks
            // like it is made of, and one decision covers both.
            var topsoil = top == MaterialTable.Topsoil ? settings.TopsoilThickness : 0f;
            var sand = top == MaterialTable.Sand ? settings.SandThickness : 0f;
            var gravel = top == MaterialTable.RockLoose ? settings.ChannelGravelThickness : 0f;
            float dirt;
            if (top == MaterialTable.Topsoil)
                dirt = settings.DirtOnPlains;
            else if (top == MaterialTable.Dirt)
                dirt = settings.DirtOnSlopes * 2f;
            else if (top == MaterialTable.Sand || top == MaterialTable.RockLoose)
                dirt = settings.DirtOnSlopes;
            else
                dirt = 0f; // Bare rock: nothing lying over it.
            if (underwater)
                dirt = Mathf.Min(dirt, 0.4f);

            // Clay stays where it belongs: a band inside a valley, under the soil. On bare rock
            // there is no soil to be under, so there is no clay either — otherwise a clay band
            // surfaces on a rocky slope in the middle of a valley, which is both wrong and the
            // source of single-cell material islands.
            var clay = valley > 0.35f && !underwater && topsoil + sand + gravel + dirt >= MinLayerThickness
                ? settings.ClayInValleys * valley
                : 0f;
            var granite = high > 0.05f ? high * settings.RidgeHeight * settings.GraniteShare : 0f;

            var cap = topsoil + sand + gravel + dirt + clay;
            var maxCap = Mathf.Max(0f, total - MinLayerThickness);
            if (cap > maxCap)
            {
                var scale = maxCap / Mathf.Max(cap, 1e-4f);
                topsoil *= scale;
                sand *= scale;
                gravel *= scale;
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
                gravel *= scale;
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
            count = Add(column, count, MaterialTable.RockLoose, gravel);
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
