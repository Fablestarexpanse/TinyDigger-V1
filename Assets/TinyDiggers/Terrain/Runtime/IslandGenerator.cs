using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// What a generated island came out as, besides the grid itself: where its peak is, and the
    /// line its river runs along, so a river mesh can be built to match the channel that was cut.
    /// </summary>
    public sealed class IslandMap
    {
        /// <summary>The cell the river starts from: the highest point of the mountain region.</summary>
        public Vector2Int Peak;

        /// <summary>
        /// The river, in grid space: x and z are cell centres, y is the floor of the channel. It
        /// descends the whole way and ends below sea level. <see cref="TerrainGrid"/> knows nothing
        /// of transforms, so the view turns these into world points.
        /// </summary>
        public readonly List<Vector3> River = new List<Vector3>();

        /// <summary>Cells the river's channel covers, for the sim to treat as water.</summary>
        public readonly List<Vector2Int> RiverCells = new List<Vector2Int>();
    }

    /// <summary>
    /// The island: a disc of land standing out of the sea, with a mountain, a valley or two, a
    /// river running from the peak to the coast, and a shelf falling away to a channel at the rim.
    ///
    /// Stages, in order, because each one reads what the last wrote:
    /// 1. Base — domain-warped fBm at <see cref="TerrainGenSettings.FeatureSize"/>, plus a medium
    ///    octave for texture between terraces.
    /// 2. Regions — one mountain and up to a few valleys, placed by seed.
    /// 3. Island mask — a radial falloff, perturbed by noise so the coastline is irregular, taking
    ///    the outer cells below sea level to a shelf and then to a deeper channel at the rim.
    /// 4. Quantise to the height step, then relax until no two neighbouring land cells differ by
    ///    more than one step. Cliffs are for later; this keeps every slope walkable.
    /// 5. River — from the peak, downhill to the sea with a little wander, cutting a flat-floored
    ///    channel whose banks step up one metre a cell, so the relaxed slope survives the cut.
    /// 6. Strata — bedrock base, granite in the mountain core, rock, clay in the valleys, dirt,
    ///    sand around sea level, topsoil above it except on sand and steep rock.
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

        public static IslandMap Generate(TerrainGrid grid, TerrainGenSettings settings)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var random = new System.Random(settings.Seed);
            var width = grid.Width;
            var depth = grid.Height;
            var cells = width * depth;

            // Kept modest: Mathf.PerlinNoise loses precision far from the origin.
            var baseOffset = new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);
            var mediumOffset = new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);
            var warpOffset = new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);
            var coastOffset = new Vector2((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);

            var radius = TerrainGenerator.DiscRadius(grid);
            var centre = new Vector2(width * 0.5f, depth * 0.5f);

            var mountain = PlaceRegion(random, centre, radius * 0.45f);
            var valleys = new Vector2[Mathf.Max(0, settings.Valleys)];
            for (var i = 0; i < valleys.Length; i++)
                valleys[i] = PlaceRegion(random, centre, radius * 0.6f);

            var heights = new float[cells];
            var isLand = new bool[cells];
            var mountainStrength = new float[cells];
            var valleyStrength = new float[cells];

            // --- 1-3: the height field -------------------------------------------------------
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    var toCentre = Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), centre);
                    if (toCentre > radius)
                    {
                        grid.SetVoid(x, z, true);
                        heights[cell] = settings.ChannelDepth;
                        continue;
                    }

                    var warped = Warp(x, z, warpOffset, settings);
                    var height = settings.BaseHeight + Fbm(warped, baseOffset, settings.FeatureSize, 4) * settings.BaseRelief;
                    height += (Noise(warped, mediumOffset, settings.MediumSize) - 0.5f) * settings.MediumRelief;

                    var toMountain = Vector2.Distance(warped, mountain) / Mathf.Max(1f, settings.MountainRadius);
                    var mountainAmount = Falloff(toMountain);
                    height += mountainAmount * settings.MountainHeight;
                    mountainStrength[cell] = mountainAmount;

                    var valleyAmount = 0f;
                    foreach (var valley in valleys)
                        valleyAmount = Mathf.Max(valleyAmount, Falloff(Vector2.Distance(warped, valley) / Mathf.Max(1f, settings.ValleyRadius)));
                    height -= valleyAmount * settings.ValleyDepth;
                    valleyStrength[cell] = valleyAmount;

                    heights[cell] = MaskToSea(height, toCentre, radius, x, z, coastOffset, settings);
                }
            }

            // --- 4: quantise and relax -------------------------------------------------------
            var step = grid.HeightStep > 0f ? grid.HeightStep : 1f;
            for (var cell = 0; cell < cells; cell++)
            {
                heights[cell] = Mathf.Round(heights[cell] / step) * step;
                isLand[cell] = heights[cell] >= World.SeaLevel;
            }

            var sweeps = Relax(heights, isLand, width, depth, step);

            // --- 5: the river ----------------------------------------------------------------
            var map = new IslandMap();
            var path = CarveRiver(map, heights, width, depth, grid, random, settings, step);

            // Carving only ever lowers ground, so relaxing again can only lower it further: this
            // takes out the steps the channel's own banks leave where the river bends and one cut
            // reaches a cell its neighbour's did not.
            for (var cell = 0; cell < cells; cell++)
                isLand[cell] = heights[cell] >= World.SeaLevel;
            sweeps = Mathf.Max(sweeps, Relax(heights, isLand, width, depth, step));

            // The polyline is read back from the ground as it finally stands, not from what the
            // carve asked for, and is held monotonically descending so a river laid on it never
            // runs uphill.
            var floor = float.MaxValue;
            foreach (var point in path)
            {
                floor = Mathf.Min(floor, heights[point.y * width + point.x]);
                map.River.Add(new Vector3(point.x + 0.5f, floor, point.y + 0.5f));
                if (floor < World.SeaLevel - 1f)
                    break;
            }

            // --- 6: strata -------------------------------------------------------------------
            Span<Layer> column = stackalloc Layer[8];
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (grid.IsVoid(x, z))
                        continue;
                    var cell = z * width + x;
                    var count = BuildColumn(column, heights[cell], Steepness(heights, width, depth, x, z, step),
                        mountainStrength[cell], valleyStrength[cell], grid.Datum, settings);
                    grid.SetColumn(x, z, column.Slice(0, count));
                }
            }

            if (sweeps >= MaxRelaxSweeps)
                Debug.LogWarning($"Island: the land was still stepping by more than {step} m after {sweeps} sweeps.");
            return map;
        }

        // --- the height field ----------------------------------------------------------------

        static Vector2 PlaceRegion(System.Random random, Vector2 centre, float spread)
        {
            var angle = (float)random.NextDouble() * Mathf.PI * 2f;
            var distance = Mathf.Sqrt((float)random.NextDouble()) * spread;
            return centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
        }

        static Vector2 Warp(int x, int z, Vector2 offset, TerrainGenSettings settings)
        {
            var frequency = 1f / Mathf.Max(1f, settings.WarpSize);
            var dx = (Mathf.PerlinNoise(offset.x + x * frequency, offset.y + z * frequency) - 0.5f) * 2f * settings.WarpStrength;
            var dz = (Mathf.PerlinNoise(offset.y + x * frequency, offset.x + z * frequency) - 0.5f) * 2f * settings.WarpStrength;
            return new Vector2(x + dx, z + dz);
        }

        /// <summary>1 at the middle of a region, easing to 0 at its edge.</summary>
        static float Falloff(float normalisedDistance)
        {
            var t = Mathf.Clamp01(1f - normalisedDistance);
            return t * t * (3f - 2f * t);
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
        /// Takes the outer ring of the disc down to a shelf and then to a channel at the rim. The
        /// coastline is the radius at which this starts, pushed in and out by noise, so it is an
        /// irregular shore rather than a circle.
        /// </summary>
        static float MaskToSea(float height, float toCentre, float radius, int x, int z, Vector2 coastOffset, TerrainGenSettings settings)
        {
            var wander = (Mathf.PerlinNoise(
                coastOffset.x + x / Mathf.Max(1f, settings.CoastNoiseSize),
                coastOffset.y + z / Mathf.Max(1f, settings.CoastNoiseSize)) - 0.5f) * 2f * settings.CoastNoiseCells;

            var shelfStart = radius - settings.ShelfCells + wander;
            if (toCentre <= shelfStart)
                return height;

            var channelStart = radius - settings.ChannelCells;
            if (toCentre >= channelStart)
            {
                var deep = Mathf.InverseLerp(channelStart, radius, toCentre);
                return Mathf.Lerp(settings.ShelfFarDepth, settings.ChannelDepth, deep);
            }

            // From the shore out to the channel: the land dives under, then the shelf falls away.
            var t = Mathf.InverseLerp(shelfStart, channelStart, toCentre);
            var smooth = t * t * (3f - 2f * t);
            var shelf = Mathf.Lerp(settings.ShelfNearDepth, settings.ShelfFarDepth, smooth);
            return Mathf.Lerp(height, shelf, smooth);
        }

        /// <summary>
        /// Pulls down any land cell standing more than one step above a neighbour, sweeping until
        /// nothing moves. Water is left alone: a shelf may fall away as steeply as it likes, it is
        /// only what the crew walks on that has to stay climbable.
        /// </summary>
        static int Relax(float[] heights, bool[] isLand, int width, int depth, float step)
        {
            // Four directional passes a sweep, the way a chamfer distance transform works: a low
            // cell's influence travels the whole width of the map in one pass rather than one cell
            // a sweep, so a 46 m mountain settles in a handful of sweeps instead of hundreds.
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

        /// <summary>The biggest step to a neighbour, in metres. Used to decide what a face is made of.</summary>
        static float Steepness(float[] heights, int width, int depth, int x, int z, float step)
        {
            var cell = z * width + x;
            var height = heights[cell];
            var worst = 0f;
            if (x > 0) worst = Mathf.Max(worst, Mathf.Abs(height - heights[cell - 1]));
            if (x < width - 1) worst = Mathf.Max(worst, Mathf.Abs(height - heights[cell + 1]));
            if (z > 0) worst = Mathf.Max(worst, Mathf.Abs(height - heights[cell - width]));
            if (z < depth - 1) worst = Mathf.Max(worst, Mathf.Abs(height - heights[cell + width]));
            return worst;
        }

        // --- the river -----------------------------------------------------------------------

        /// <summary>
        /// Walks from the highest cell of the mountain down to the sea, taking the lowest neighbour
        /// each step with a little sideways wander, then cuts the channel it ran through. The floor
        /// only ever descends, so a river mesh laid on the polyline never runs uphill.
        /// </summary>
        static List<Vector2Int> CarveRiver(IslandMap map, float[] heights, int width, int depth, TerrainGrid grid,
            System.Random random, TerrainGenSettings settings, float step)
        {
            var peak = -1;
            for (var cell = 0; cell < heights.Length; cell++)
            {
                var x = cell % width;
                var z = cell / width;
                if (!grid.IsGround(x, z))
                    continue;
                if (peak < 0 || heights[cell] > heights[peak])
                    peak = cell;
            }

            if (peak < 0)
                return new List<Vector2Int>();

            map.Peak = new Vector2Int(peak % width, peak / width);
            var path = new List<Vector2Int>();
            var visited = new HashSet<int>();
            var at = map.Peak;
            var guard = width * depth;

            while (guard-- > 0)
            {
                var cell = at.y * width + at.x;
                if (!visited.Add(cell))
                    break;
                path.Add(at);
                if (heights[cell] < World.SeaLevel - 1f)
                    break;

                var best = at;
                var bestScore = float.MaxValue;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        var nx = at.x + dx;
                        var nz = at.y + dz;
                        if (!grid.InBounds(nx, nz) || visited.Contains(nz * width + nx))
                            continue;
                        // The wander is part of the score, not a separate coin flip, so the river
                        // leans off the steepest line without ever turning back uphill.
                        var score = heights[nz * width + nx] + (float)random.NextDouble() * settings.RiverWander;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(nx, nz);
                    }
                }

                if (best == at)
                    break;
                at = best;
            }

            if (path.Count < 2)
                return path;

            // Cut the channel, then read the floor back out, so the polyline is what is actually
            // there rather than what was asked for.
            var halfWidth = Mathf.Max(1, settings.RiverWidth) * 0.5f;
            var reach = Mathf.CeilToInt(halfWidth + settings.RiverDepth / step) + 1;
            var floor = float.MaxValue;
            foreach (var point in path)
            {
                var here = heights[point.y * width + point.x];
                var target = Mathf.Min(floor, here - settings.RiverDepth);
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
                        // Flat floor across the channel, then one step up a cell on the banks, so
                        // the cut keeps the neighbour-step guarantee it was relaxed to.
                        var bank = Mathf.Max(0f, distance - halfWidth) * step;
                        var cut = target + bank;
                        var cell = nz * width + nx;
                        if (cut < heights[cell])
                            heights[cell] = Mathf.Round(cut / step) * step;
                        if (distance <= halfWidth)
                            map.RiverCells.Add(new Vector2Int(nx, nz));
                    }
                }

                if (target < World.SeaLevel - 1f)
                    break;
            }

            return path;
        }

        // --- strata --------------------------------------------------------------------------

        /// <summary>
        /// Builds one column bottom-up for a surface at <paramref name="surface"/>: bedrock from
        /// the datum, granite where a mountain stands over it, rock, clay in the valleys, then the
        /// cap — sand near sea level and under water, bare rock where it is too steep to hold soil,
        /// dirt and topsoil everywhere else.
        /// </summary>
        static int BuildColumn(Span<Layer> column, float surface, float steepness, float mountain, float valley,
            float datum, TerrainGenSettings settings)
        {
            var total = surface - datum;
            if (total <= MinLayerThickness)
            {
                column[0] = new Layer(MaterialTable.Bedrock, Mathf.Max(MinLayerThickness, total));
                return 1;
            }

            var underwater = surface < World.SeaLevel;
            var coastal = Mathf.Abs(surface - World.SeaLevel) <= settings.SandBand;
            // A face that steps a whole metre and a half to a neighbour will not hold topsoil.
            var steep = steepness > 1.5f;

            var topsoil = underwater || coastal || steep ? 0f : settings.TopsoilThickness;
            var sand = underwater || coastal ? settings.SandThickness : 0f;
            var dirt = underwater ? 0.4f : steep ? settings.DirtOnSlopes : settings.DirtOnPlains;
            if (coastal && !underwater)
                dirt *= 0.5f;
            var clay = valley > 0.35f && !underwater ? settings.ClayInValleys * valley : 0f;
            var granite = mountain > 0.05f ? mountain * settings.MountainHeight * settings.GraniteShare : 0f;

            // Everything above the bedrock, trimmed to what the column can actually hold.
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
                // No room for a bedrock base: give it what is left and thin the cap instead.
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
            // layers picked up goes into the thickest band below the cap.
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
    }
}
