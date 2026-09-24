using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    public enum ChannelKind
    {
        /// <summary>Wide and deep: the crew cannot wade it.</summary>
        River,

        /// <summary>Narrow and shallow: the crew wades it.</summary>
        Creek,
    }

    /// <summary>One river or creek the generator cut, head to mouth.</summary>
    public sealed class Channel
    {
        public ChannelKind Kind;

        /// <summary>Grid space, head to mouth: x and z in cells, y the bed's floor in metres.</summary>
        public readonly List<Vector3> Path = new List<Vector3>();

        /// <summary>The head: where the spring goes.</summary>
        public Vector2Int Spring;

        /// <summary>Square metres of land draining to the head, which scales the spring.</summary>
        public float Catchment;

        /// <summary>
        /// Metres across the bed and metres the bed sits below the ground beside it, as drawn for
        /// the channel. The bed is not this width all the way along: see <see cref="Widths"/>.
        /// </summary>
        public float Width;
        public float Depth;

        /// <summary>
        /// Metres across the bed at each point of <see cref="Path"/>. A channel narrows at its
        /// spring, widens downstream, narrows on steep reaches and flares at its end.
        /// </summary>
        public readonly List<float> Widths = new List<float>();

        /// <summary>Metres along the channel, head to mouth.</summary>
        public float Length;

        /// <summary>A creek that ends in a river rather than in the sea or a lake.</summary>
        public bool EndsInRiver;
    }

    /// <summary>
    /// Rivers and creeks (Ronan, 2026-09-21): one to three rivers and four to ten creeks per seed,
    /// cut as smooth winding channels for the simulated water to run down. This replaces the D8
    /// walk up the wettest cells, which only turns in eight directions and cut dead-straight
    /// grooves.
    ///
    /// For each channel:
    /// 1. A head: high ground with water running into it. Rivers start from the upper ground,
    ///    creeks from the middle.
    /// 2. A route from the head to water, down the valley: the way water leaves each cell, as
    ///    the pit fill found it.
    /// 3. The route is smoothed, and a meander is added that swings wider as the land flattens.
    ///    Routes follow the way water leaves each cell, from the pit fill, so they run down the
    ///    valleys water would really take.
    /// 4. A bed with a rounded floor and gentle banks is cut along it. The floor never rises
    ///    towards the mouth.
    ///
    /// Rivers are cut first; a creek may end where it meets one. Everything works in cells, like
    /// the rest of the generator, on the settings as <see cref="TerrainGenSettings.ScaledForCells"/>
    /// left them. Heights are metres.
    /// </summary>
    public static class RiverChannels
    {
        /// <summary>Metres of bank cut, at most, either side of a bed before the cut stops.</summary>
        const float MaxBank = 3f;

        /// <summary>Metres a bed may sit under sea level while it still runs over land.</summary>
        const float UnderSea = 0.5f;

        static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] StepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>
        /// Draws the counts from the seed, picks heads, routes and cuts every channel into
        /// <paramref name="heights"/>, and records them on <paramref name="map"/>.
        /// <paramref name="accumulation"/> is cells of land draining through each cell.
        /// <paramref name="downstream"/> is the way water leaves each cell, from
        /// <see cref="TerrainErosion.FillDepressions"/>. <paramref name="frame"/> is where the
        /// island's own frame starts on the grid: heads are picked on the frame's cells, so the
        /// same island on a bigger disc gets the same channels.
        /// </summary>
        public static void Carve(IslandMap map, float[] heights, bool[] inDisc, float[] accumulation, int[] downstream,
            int width, int depth, TerrainGenSettings s, float step, Vector2Int frame)
        {
            // Their own stream: drawing from the island's would move everything drawn after it,
            // the ores included, on every existing seed.
            var random = new System.Random(unchecked(s.Seed * 7919 + 1013));
            map.RiversWanted = Draw(random, s.RiverCountMin, s.RiverCountMax);
            map.CreeksWanted = Draw(random, s.CreekCountMin, s.CreekCountMax);

            var cells = width * depth;
            var cellSize = s.GenerationCellSize > 0f ? s.GenerationCellSize : 1f;
            var water = new bool[cells];
            var peak = World.SeaLevel;
            for (var cell = 0; cell < cells; cell++)
            {
                water[cell] = inDisc[cell] && heights[cell] < World.SeaLevel;
                if (inDisc[cell] && !water[cell])
                    peak = Mathf.Max(peak, heights[cell]);
            }

            if (peak <= World.SeaLevel + step)
                return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var toWater = TerrainErosion.EuclideanDistance(water, width, depth);
            var stats = new Stats();
            var channelMask = new byte[cells]; // 1 river bed, 2 creek bed
            var heads = new List<Vector2Int>();

            // Rivers from the upper ground.
            var riverCandidates = Candidates(heights, inDisc, water, accumulation, toWater, width, depth, frame, peak,
                0.15f, 1f, s.MinRiverLength, 12f);
            Place(map, ChannelKind.River, map.RiversWanted, riverCandidates, s.RiverHeadSpacing, heads, random,
                heights, inDisc, water, toWater, accumulation, channelMask, downstream, stats, width, depth, s, step, cellSize, frame);

            // Creeks from the middle ground, clear of the rivers already cut.
            var onRiver = new bool[cells];
            for (var cell = 0; cell < cells; cell++)
                onRiver[cell] = channelMask[cell] == 1;
            var toRiver = TerrainErosion.EuclideanDistance(onRiver, width, depth);
            var creekCandidates = Candidates(heights, inDisc, water, accumulation, toWater, width, depth, frame, peak,
                0.1f, 0.65f, s.MinCreekLength, 6f);
            creekCandidates.RemoveAll(cell => toRiver[cell] < s.CreekHeadSpacing * 0.5f);
            Place(map, ChannelKind.Creek, map.CreeksWanted, creekCandidates, s.CreekHeadSpacing, heads, random,
                heights, inDisc, water, toWater, accumulation, channelMask, downstream, stats, width, depth, s, step, cellSize, frame);

            for (var cell = 0; cell < cells; cell++)
                if (channelMask[cell] == 1)
                    map.RiverCells.Add(new Vector2Int(cell % width, cell / width));
            if (map.Channels.Count > 0)
                map.ChannelBeds = channelMask;

            map.ChannelStats = $"{stats.Routes} routes followed ({stats.Short} too short, {stats.Dropped} dropped), " +
                $"{clock.Elapsed.TotalMilliseconds:0} ms";
        }

        /// <summary>
        /// Reads every channel's floor back out of the land as it finally stands, held never
        /// rising towards the mouth, so what is laid on the line sits in the bed that is really
        /// there. A river's last point goes under the sea, because that is where it ends.
        /// </summary>
        public static void ReadFloors(IslandMap map, float[] heights, int width)
        {
            foreach (var channel in map.Channels)
            {
                var path = channel.Path;
                var floor = float.MaxValue;
                for (var i = 0; i < path.Count; i++)
                {
                    var point = path[i];
                    floor = Mathf.Min(floor, heights[Mathf.FloorToInt(point.z) * width + Mathf.FloorToInt(point.x)]);
                    var mouth = i == path.Count - 1 && channel.Kind == ChannelKind.River;
                    path[i] = new Vector3(point.x, mouth ? Mathf.Min(floor, World.SeaLevel - UnderSea) : floor, point.z);
                }
            }

            map.Rivers.Sort((a, b) => b.Count.CompareTo(a.Count));
        }

        static int Mod(int value, int by) => (value % by + by) % by;

        static int Draw(System.Random random, int min, int max) =>
            max <= min ? Mathf.Max(0, min) : random.Next(Mathf.Max(0, min), max + 1);

        /// <summary>
        /// Land cells that could be a head, best first: between <paramref name="lowShare"/> and
        /// <paramref name="highShare"/> of the way from the sea to the peak, far enough from water
        /// to make a channel, and with at least <paramref name="minFlow"/> cells draining into
        /// them, so a head sits in a valley rather than on a crest. Every fourth cell each way is
        /// enough to choose from.
        /// </summary>
        static List<int> Candidates(float[] heights, bool[] inDisc, bool[] water, float[] accumulation, float[] toWater,
            int width, int depth, Vector2Int frame, float peak, float lowShare, float highShare, float minLength, float minFlow)
        {
            var scored = new List<(int cell, float score)>();
            var range = peak - World.SeaLevel;
            // Every fourth cell of the island's frame, not of the grid.
            for (var z = 2 + Mod(frame.y, 4); z < depth - 2; z += 4)
            {
                for (var x = 2 + Mod(frame.x, 4); x < width - 2; x += 4)
                {
                    var cell = z * width + x;
                    // A quarter of the length is still let in, for the second pass on land too small
                    // for the first.
                    if (!inDisc[cell] || water[cell] || toWater[cell] < minLength * 0.25f || accumulation[cell] < minFlow)
                        continue;
                    var share = (heights[cell] - World.SeaLevel) / range;
                    if (share < lowShare || share > highShare)
                        continue;
                    scored.Add((cell, share * Mathf.Log(1f + accumulation[cell])));
                }
            }

            scored.Sort((a, b) => b.score.CompareTo(a.score));
            var ordered = new List<int>(scored.Count);
            foreach (var (cell, _) in scored)
                ordered.Add(cell);
            return ordered;
        }

        /// <summary>
        /// Cuts up to <paramref name="wanted"/> channels from the candidates, each head at least
        /// <paramref name="spacing"/> cells from every head so far. If the land cannot fit that
        /// many that far apart and that far from water, the rest are tried again at half the
        /// spacing and a quarter of the length.
        /// </summary>
        static void Place(IslandMap map, ChannelKind kind, int wanted, List<int> candidates, float spacing,
            List<Vector2Int> heads, System.Random random, float[] heights, bool[] inDisc, bool[] water, float[] toWater,
            float[] accumulation, byte[] channelMask, int[] downstream, Stats stats, int width, int depth, TerrainGenSettings s,
            float step, float cellSize, Vector2Int frame)
        {
            var made = 0;
            var tried = new HashSet<int>();
            for (var pass = 0; pass < 2 && made < wanted; pass++)
            {
                var apart = pass == 0 ? spacing : spacing * 0.5f;
                foreach (var cell in candidates)
                {
                    if (made >= wanted)
                        break;
                    if (tried.Contains(cell) || pass == 0 && toWater[cell] < (kind == ChannelKind.River ? s.MinRiverLength : s.MinCreekLength))
                        continue;
                    var head = new Vector2Int(cell % width, cell / width);
                    var clear = true;
                    foreach (var other in heads)
                        if ((other - head).sqrMagnitude < apart * apart)
                            clear = false;
                    if (!clear)
                        continue;
                    tried.Add(cell);

                    var route = Follow(cell, downstream, water, channelMask, kind == ChannelKind.Creek);
                    stats.Routes++;
                    var minLength = (kind == ChannelKind.River ? s.MinRiverLength : s.MinCreekLength) * (pass == 0 ? 0.8f : 0.2f);
                    if (route == null || route.Count < minLength)
                    {
                        stats.Short++;
                        continue;
                    }

                    var channel = new Channel
                    {
                        Kind = kind,
                        Spring = head,
                        Catchment = accumulation[cell] * cellSize * cellSize,
                        Width = kind == ChannelKind.River
                            ? Mathf.Lerp(s.RiverWidthMin, s.RiverWidthMax, (float)random.NextDouble())
                            : Mathf.Lerp(s.CreekWidthMin, s.CreekWidthMax, (float)random.NextDouble()),
                        // Never shallower than the ground can hold. A creek is asked for at
                        // 0.5 m, and the bed is put on the height step like everything else, so on
                        // a 1 m step it rounded away to nothing: 84% of creek bed points came out
                        // level with their banks, and on the game's 0.5 m step 57% did. It read as
                        // a river bug and was a quantising one (2026-09-24).
                        Depth = Mathf.Max(step, kind == ChannelKind.River
                            ? Mathf.Lerp(s.RiverDepthMin, s.RiverDepthMax, (float)random.NextDouble())
                            : s.CreekDepth),
                    };

                    var line = Meander(Smooth(route, width), heights, width, depth, inDisc, channel.Width, s,
                        (float)random.NextDouble() * Mathf.PI * 2f, cellSize);
                    if (!Cut(channel, line, heights, inDisc, water, channelMask, accumulation, width, depth, s, step, cellSize, frame))
                    {
                        stats.Dropped++;
                        continue;
                    }

                    // Width is carried in cells until here; the record is in metres.
                    channel.Width *= cellSize;
                    map.Channels.Add(channel);
                    if (kind == ChannelKind.River)
                        map.Rivers.Add(channel.Path);
                    heads.Add(head);
                    made++;
                }
            }
        }

        // --- route --------------------------------------------------------------------------------

        sealed class Stats
        {
            public int Routes, Short, Dropped;
        }

        /// <summary>
        /// The cells from a head down to the first water cell (or, for a creek, the first river
        /// bed cell), following the way water leaves each cell. The pit fill works that out for
        /// every cell as it floods up from the water, so the route is the valley the water would
        /// really take, and it costs only its own length. Null if it never reaches water.
        /// </summary>
        static List<int> Follow(int start, int[] downstream, bool[] water, byte[] channelMask, bool joinRivers)
        {
            var route = new List<int>();
            for (var at = start; at >= 0 && route.Count < downstream.Length; at = downstream[at])
            {
                route.Add(at);
                if (water[at] || joinRivers && channelMask[at] == 1)
                    return route;
            }

            return null;
        }

        // --- line ---------------------------------------------------------------------------------

        /// <summary>
        /// The route's cell centres, thinned to every third cell and rounded off twice by Chaikin's
        /// corner cutting, so it no longer turns only in eighths. The ends stay where they were.
        /// </summary>
        static List<Vector2> Smooth(List<int> route, int width)
        {
            var points = new List<Vector2>();
            for (var i = 0; i < route.Count; i += 3)
                points.Add(new Vector2(route[i] % width + 0.5f, route[i] / width + 0.5f));
            var end = new Vector2(route[route.Count - 1] % width + 0.5f, route[route.Count - 1] / width + 0.5f);
            if (points[points.Count - 1] != end)
                points.Add(end);
            for (var pass = 0; pass < 2; pass++)
                points = Chaikin(points);

            // Then averaged over six cells either way, three times. A route over terraced land
            // hugs the terrace edges in little zigzags, and a meander laid on a zigzag folds
            // back on itself into combs.
            return Average(Resample(points, 1f), 6, 3);
        }

        /// <summary>
        /// Each point moved to the mean of the points <paramref name="reach"/> either side of it,
        /// <paramref name="passes"/> times. Narrower near the ends, so the head and the mouth stay
        /// where they were.
        /// </summary>
        static List<Vector2> Average(List<Vector2> points, int reach, int passes)
        {
            for (var pass = 0; pass < passes; pass++)
            {
                var averaged = new List<Vector2>(points.Count);
                for (var i = 0; i < points.Count; i++)
                {
                    var span = Mathf.Min(reach, Mathf.Min(i, points.Count - 1 - i));
                    var sum = Vector2.zero;
                    for (var j = i - span; j <= i + span; j++)
                        sum += points[j];
                    averaged.Add(sum / (2 * span + 1));
                }

                points = averaged;
            }

            return points;
        }

        static List<Vector2> Chaikin(List<Vector2> points)
        {
            if (points.Count < 3)
                return points;
            var smooth = new List<Vector2>(points.Count * 2) { points[0] };
            for (var i = 0; i < points.Count - 1; i++)
            {
                smooth.Add(Vector2.Lerp(points[i], points[i + 1], 0.25f));
                smooth.Add(Vector2.Lerp(points[i], points[i + 1], 0.75f));
            }

            smooth.Add(points[points.Count - 1]);
            return smooth;
        }

        /// <summary>Points every <paramref name="spacing"/> cells along the line.</summary>
        static List<Vector2> Resample(List<Vector2> points, float spacing)
        {
            var even = new List<Vector2> { points[0] };
            var carried = 0f;
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                var length = Vector2.Distance(a, b);
                var t = spacing - carried;
                while (t <= length)
                {
                    even.Add(Vector2.Lerp(a, b, t / length));
                    t += spacing;
                }

                carried = length - (t - spacing);
            }

            if (even[even.Count - 1] != points[points.Count - 1])
                even.Add(points[points.Count - 1]);
            return even;
        }

        /// <summary>
        /// Swings the line side to side, a sine along its length, as far as
        /// <see cref="TerrainGenSettings.MeanderStrength"/> bed widths on flat ground and not at
        /// all on a slope of one in ten or steeper. It is nothing at the head and the mouth, so
        /// the channel still starts at its spring and still reaches the water.
        /// </summary>
        static List<Vector2> Meander(List<Vector2> line, float[] heights, int width, int depth, bool[] inDisc,
            float bedCells, TerrainGenSettings s, float phase, float cellSize)
        {
            var even = line;
            if (even.Count < 8 || s.MeanderStrength <= 0f)
                return Resample(even, 0.5f);

            // At least four swings' worth of room either way, or a narrow creek zigzags.
            var wavelength = Mathf.Max(Mathf.Max(8f, s.MeanderMinSwing * 5f), s.MeanderWavelength * bedCells);
            var total = even.Count - 1f;
            var swung = new List<Vector2>(even.Count);
            const int window = 8;
            for (var i = 0; i < even.Count; i++)
            {
                var behind = even[Mathf.Max(0, i - window)];
                var ahead = even[Mathf.Min(even.Count - 1, i + window)];
                var along = ahead - behind;
                if (along.sqrMagnitude < 1e-6f)
                {
                    swung.Add(even[i]);
                    continue;
                }

                var run = along.magnitude * cellSize;
                var fall = Mathf.Abs(HeightAt(heights, width, depth, behind) - HeightAt(heights, width, depth, ahead));
                // The wide swing gives out by MeanderSlopeLimit. It used to give out at a slope of
                // one in ten, which is nearly every channel on the island: they measured 1.05
                // sinuosity, as good as straight (2026-09-24).
                var flatness = Mathf.Clamp01(1f - fall / run / Mathf.Max(0.02f, s.MeanderSlopeLimit));
                var ends = Mathf.Clamp01(Mathf.Min(i, total - i) / (wavelength * 0.5f));
                var taper = ends * ends * (3f - 2f * ends);
                var normal = new Vector2(-along.y, along.x).normalized;
                var swing = Mathf.Max(s.MeanderStrength * bedCells, s.MeanderMinSwing);
                var u = i / wavelength * Mathf.PI * 2f;
                // Two swings, not one: a second, faster one at an unrelated phase breaks the sine's
                // regularity, so no two bends are the same size.
                var wide = (Mathf.Sin(phase + u) + 0.35f * Mathf.Sin(phase * 1.7f + 1f + u * 2.3f)) / 1.35f;
                // Where the wide swing is off, a steep stream still wanders, tighter and shorter.
                // At least four tenths of the least meander, or a one-cell creek wandered by one
                // cell and still measured 1.06 sinuosity.
                var steepSwing = Mathf.Max(s.MeanderMinSwing * 0.4f, s.SteepWiggle * bedCells);
                var steep = Mathf.Sin(phase * 2.9f + 2f + u * 2f) * steepSwing;
                // The wander has its own, shorter fade at the ends: faded over the wide swing's
                // half-wavelength, a creek of thirty cells was faded along nearly all of it.
                var steepEnds = Mathf.Clamp01(Mathf.Min(i, total - i) / (wavelength * 0.15f));
                var steepTaper = steepEnds * steepEnds * (3f - 2f * steepEnds);
                var offset = wide * swing * flatness * taper + steep * (1f - flatness) * steepTaper;
                var point = even[i] + normal * offset;
                var x = Mathf.FloorToInt(point.x);
                var z = Mathf.FloorToInt(point.y);
                swung.Add(x >= 1 && z >= 1 && x < width - 1 && z < depth - 1 && inDisc[z * width + x] ? point : even[i]);
            }

            // Averaged again over a bed and a half either way: a swing laid round a bend can kink
            // it tighter than the bed is wide, and the inside of the kink is left with no bank.
            var rounded = Average(CutOffLoops(swung, bedCells), Mathf.CeilToInt(bedCells * 1.5f), 2);
            return Resample(rounded, 0.5f);
        }

        /// <summary>
        /// Where the line comes back within a bed's width of where it was a while before, the
        /// loop between is cut out, as a river cuts through the neck of a meander. A meander
        /// swung out from the inside of a tight bend folds back on itself, and cutting both
        /// reaches leaves a channel beside its own downstream, with no bank between them.
        /// </summary>
        static List<Vector2> CutOffLoops(List<Vector2> points, float bedCells)
        {
            var neck = Mathf.Max(1f, bedCells);
            var lookAhead = Mathf.CeilToInt(neck * 12f);
            var kept = new List<Vector2>(points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                kept.Add(points[i]);
                // Far enough along the line that it is another reach, not the next few points.
                var skipTo = -1;
                var last = Mathf.Min(points.Count - 1, i + lookAhead);
                for (var j = i + Mathf.CeilToInt(neck * 1.5f) + 1; j <= last; j++)
                    if (Vector2.Distance(points[i], points[j]) < neck)
                        skipTo = j;
                if (skipTo > 0)
                    i = skipTo - 1;
            }

            return kept;
        }

        static float HeightAt(float[] heights, int width, int depth, Vector2 at)
        {
            var x = Mathf.Clamp(Mathf.FloorToInt(at.x), 0, width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(at.y), 0, depth - 1);
            return heights[z * width + x];
        }

        // --- bed ----------------------------------------------------------------------------------

        /// <summary>The lowest dry ground within <paramref name="radius"/> cells of a point.</summary>
        static float LowestBeside(float[] heights, bool[] inDisc, bool[] water, int width, int depth, Vector2 at, float radius)
        {
            var lowest = float.MaxValue;
            var box = Mathf.CeilToInt(radius);
            var cx = Mathf.FloorToInt(at.x);
            var cz = Mathf.FloorToInt(at.y);
            for (var dz = -box; dz <= box; dz++)
            {
                var z = cz + dz;
                if (z < 0 || z >= depth)
                    continue;
                for (var dx = -box; dx <= box; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= width || dx * dx + dz * dz > radius * radius)
                        continue;
                    var cell = z * width + x;
                    if (inDisc[cell] && !water[cell])
                        lowest = Mathf.Min(lowest, heights[cell]);
                }
            }

            var own = Mathf.Clamp(cz, 0, depth - 1) * width + Mathf.Clamp(cx, 0, width - 1);
            return lowest < float.MaxValue ? lowest : heights[own];
        }

        /// <summary>
        /// Cuts the bed along the line, and says whether it was kept. The shape of the bed changes
        /// along its length (Ronan, 2026-09-24: *"realistic looking and varied streams and river
        /// channels"*); until then every channel was one width and one depth from spring to sea,
        /// with the same bank on both sides. Now, point by point:
        ///
        /// - **it grows downstream** with the water gathered above it, as the square root, from
        ///   <see cref="TerrainGenSettings.ChannelHeadShare"/> of its drawn width at the spring;
        /// - **its shape follows the slope**: narrow and deep on a steep reach, wide and shallow
        ///   on a flat one;
        /// - **its width wanders** a little, so no two reaches are alike;
        /// - **it flares** where a creek meets a river and where a river meets the water;
        /// - **it has pools**, on the outside of bends and in a slower rhythm along straight
        ///   reaches. A pool is a local hollow; the floor it sits in still never rises towards
        ///   the mouth;
        /// - **its banks differ at bends**: the outside is a steep cut bank, the inside a gentle
        ///   point bar, and the deepest water leans towards the outside.
        /// </summary>
        static bool Cut(Channel channel, List<Vector2> line, float[] heights, bool[] inDisc, bool[] water,
            byte[] channelMask, float[] accumulation, int width, int depth, TerrainGenSettings s, float step, float cellSize,
            Vector2Int frame)
        {
            var creek = channel.Kind == ChannelKind.Creek;
            var baseHalf = Mathf.Max(0.5f, channel.Width * 0.5f);
            var bank = baseHalf + Mathf.Max(2f, 1f / cellSize);

            // --- where it ends, and the ground it is cut into --------------------------------
            var grounds = new List<float>(line.Count);
            var gathered = new List<float>(line.Count);
            var end = line.Count - 1;
            for (var i = 0; i < line.Count; i++)
            {
                var x = Mathf.Clamp(Mathf.FloorToInt(line[i].x), 0, width - 1);
                var z = Mathf.Clamp(Mathf.FloorToInt(line[i].y), 0, depth - 1);
                var cell = z * width + x;
                // The lowest ground just outside the bed, not the ground under it: across a hillside
                // the downhill bank is lower, and a bed cut from the middle would leave the water
                // nothing to hold it on that side. Cut from the lowest, the uphill bank is cut
                // deeper instead, which is what a river does to a hillside.
                grounds.Add(LowestBeside(heights, inDisc, water, width, depth, line[i], bank));
                gathered.Add(accumulation != null ? accumulation[cell] : 1f);
                if (water[cell] || creek && channelMask[cell] == 1)
                {
                    end = i;
                    channel.EndsInRiver = creek && !water[cell];
                    break;
                }
            }

            if (end < 4)
                return false;
            var count = end + 1;

            // --- the shape, point by point -------------------------------------------------
            var head = Mathf.Max(1f, gathered[0]);
            // From the spring's place in the island's own frame, not the grid's: the same seed makes
            // the same island on a bigger disc, and a spring keyed to grid cells moved every
            // channel's pools and width as the island moved (LandRadiusTests, 2026-09-24).
            var fromFrame = channel.Spring - frame;
            var noiseAt = new Vector2((fromFrame.x * 0.6180339f) % 97f, (fromFrame.y * 0.4142135f) % 89f);
            var widthShare = new float[count];
            var depthShare = new float[count];
            var bendOf = new float[count];
            for (var i = 0; i < count; i++)
            {
                // Hydraulic geometry: width runs as the square root of the water carried, depth
                // rather more slowly.
                var grown = Mathf.Sqrt(Mathf.Max(head, gathered[i]) / head);
                var w = Mathf.Clamp(s.ChannelHeadShare * grown, s.ChannelHeadShare, s.ChannelMouthShare);
                var d = Mathf.Clamp(0.75f * Mathf.Pow(grown, 0.35f), 0.75f, 1.25f);

                // Slope over three cells either way.
                var behind = line[Mathf.Max(0, i - 6)];
                var ahead = line[Mathf.Min(count - 1, i + 6)];
                var run = Mathf.Max(0.5f, Vector2.Distance(behind, ahead)) * cellSize;
                var fall = Mathf.Abs(HeightAt(heights, width, depth, behind) - HeightAt(heights, width, depth, ahead));
                var steep = Mathf.Clamp01(fall / run / 0.3f);
                w *= Mathf.Lerp(s.ChannelFlatWidening, s.ChannelSteepNarrowing, steep);
                d *= Mathf.Lerp(0.85f, 1.35f, steep);

                // Wandering width, on a scale of a few bed widths.
                var wander = Mathf.PerlinNoise(noiseAt.x + i * 0.5f / Mathf.Max(2f, baseHalf * 8f), noiseAt.y) * 2f - 1f;
                w *= 1f + s.ChannelWidthNoise * wander;

                widthShare[i] = w;
                depthShare[i] = d;

                // How hard it bends here, signed: positive turns left.
                var a = line[Mathf.Max(0, i - 4)];
                var b = line[i];
                var c = line[Mathf.Min(count - 1, i + 4)];
                var inward = b - a;
                var onward = c - b;
                if (inward.sqrMagnitude > 1e-6f && onward.sqrMagnitude > 1e-6f)
                    bendOf[i] = Mathf.Asin(Mathf.Clamp((inward.x * onward.y - inward.y * onward.x)
                        / (inward.magnitude * onward.magnitude), -1f, 1f));
            }

            // Flares at the end: a creek into a river, a river into the water.
            var flareLength = Mathf.Min(count, creek ? 8 : 16);
            var flare = creek ? (channel.EndsInRiver ? s.ConfluenceFlare : 1f) : s.MouthFlare;
            for (var i = count - flareLength; i < count; i++)
            {
                var t = (i - (count - flareLength)) / (float)flareLength;
                widthShare[i] *= Mathf.Lerp(1f, flare, t * t);
                depthShare[i] *= Mathf.Lerp(1f, 0.8f, t);
            }

            // Smoothed, so the bed changes over a few cells rather than from one point to the next.
            widthShare = Smoothed(widthShare, 6);
            depthShare = Smoothed(depthShare, 6);
            bendOf = Smoothed(bendOf, 6);

            var halfAt = new float[count];
            var depthAt = new float[count];
            var poolAt = new float[count];
            var floors = new float[count];
            var floor = float.MaxValue;
            var travelled = 0f;
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                    travelled += Vector2.Distance(line[i - 1], line[i]);
                halfAt[i] = Mathf.Max(0.5f, baseHalf * widthShare[i]);
                depthAt[i] = Mathf.Max(step, channel.Depth * depthShare[i]);

                // The floor the water runs on never rises towards the mouth.
                floor = Mathf.Min(floor, grounds[i] - depthAt[i]);
                var x = Mathf.Clamp(Mathf.FloorToInt(line[i].x), 0, width - 1);
                var z = Mathf.Clamp(Mathf.FloorToInt(line[i].y), 0, depth - 1);
                if (!water[z * width + x])
                    floor = Mathf.Max(floor, World.SeaLevel - UnderSea);
                floors[i] = floor;

                // Pools: hard on the outside of bends, and a gentler rhythm down the straights.
                var bend = Mathf.Clamp01(Mathf.Abs(bendOf[i]) / 0.35f);
                var spacing = Mathf.Max(4f, s.ChannelPoolSpacing * halfAt[i] * 2f);
                var rhythm = 0.5f + 0.5f * Mathf.Sin(travelled / spacing * Mathf.PI * 2f + noiseAt.x);
                poolAt[i] = depthAt[i] * s.ChannelPoolDepth * Mathf.Max(bend, 0.4f * rhythm * rhythm);
            }

            // --- the cut -------------------------------------------------------------------
            var bankPerCell = s.ChannelBankSlope * cellSize;
            var mark = (byte)(creek ? 2 : 1);
            // Every second point: they are half a cell apart, and a cell apart is close enough for
            // a bed at least a cell across.
            for (var i = 0; i <= end; i = i < end && i + 2 > end ? end : i + 2)
            {
                var at = line[i];
                var half = halfAt[i];
                var top = floors[i] + depthAt[i];
                var bed = floors[i] - poolAt[i];
                var bend = Mathf.Clamp01(Mathf.Abs(bendOf[i]) / 0.35f);
                // Turning left puts the outside of the bend on the right.
                var outsideSign = bendOf[i] > 0f ? -1f : 1f;
                var forward = line[Mathf.Min(end, i + 1)] - line[Mathf.Max(0, i - 1)];
                forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector2.right;
                // The deepest water leans towards the outside of a bend.
                var lean = 0.35f * half * bend * outsideSign;

                var softest = Mathf.Min(1f, Mathf.Lerp(1f, s.PointBarSoften, bend));
                var reach = half + MaxBank / (bankPerCell * softest);
                var box = Mathf.CeilToInt(reach + Mathf.Abs(lean));
                var cx = Mathf.FloorToInt(at.x);
                var cz = Mathf.FloorToInt(at.y);
                for (var dz = -box; dz <= box; dz++)
                {
                    var z = cz + dz;
                    if (z < 0 || z >= depth)
                        continue;
                    for (var dx = -box; dx <= box; dx++)
                    {
                        var x = cx + dx;
                        if (x < 0 || x >= width)
                            continue;
                        var cell = z * width + x;
                        if (!inDisc[cell])
                            continue;
                        var offset = new Vector2(x + 0.5f, z + 0.5f) - at;
                        var distance = offset.magnitude;
                        if (distance > reach + Mathf.Abs(lean))
                            continue;
                        // Which side of the line: left is +1. (It was the other way round the
                        // first time, which put every cut bank on the inside of its bend and
                        // measured 0.98 outside over inside.)
                        var side = forward.x * offset.y - forward.y * offset.x > 0f ? 1f : -1f;
                        var sideways = distance * side;
                        var fromDeepest = Mathf.Abs(sideways - lean);
                        float cut;
                        if (fromDeepest < half)
                        {
                            var across = fromDeepest / half;
                            cut = bed + (top - bed) * across * across;
                        }
                        else
                        {
                            var steepen = side == outsideSign
                                ? Mathf.Lerp(1f, s.CutBankSteepen, bend)
                                : Mathf.Lerp(1f, s.PointBarSoften, bend);
                            cut = top + (fromDeepest - half) * bankPerCell * steepen;
                        }

                        // The bed goes down to the step below, the banks to the nearest. Rounding
                        // the bed to the nearest step let a shallow channel round back up level
                        // with its own banks, which is a channel with no water in it.
                        cut = fromDeepest < half
                            ? Mathf.Floor(cut / step) * step
                            : Mathf.Round(cut / step) * step;
                        if (cut < heights[cell])
                            heights[cell] = cut;
                        // At least three quarters of a cell either side: a bed a cell or two wide
                        // would otherwise miss cells the line runs between points over.
                        if (fromDeepest < Mathf.Max(half, 0.75f) && channelMask[cell] == 0)
                            channelMask[cell] = mark;
                    }
                }
            }

            var length = 0f;
            for (var i = 0; i <= end; i++)
            {
                if (i > 0)
                    length += Vector2.Distance(line[i - 1], line[i]);
                if (i % 4 == 0 || i == end)
                {
                    channel.Path.Add(new Vector3(line[i].x, floors[i], line[i].y));
                    channel.Widths.Add(halfAt[i] * 2f * cellSize);
                }
            }

            channel.Length = length * cellSize;
            return true;
        }

        /// <summary>Each value averaged with <paramref name="reach"/> either side, narrowing at the ends.</summary>
        static float[] Smoothed(float[] values, int reach)
        {
            var smoothed = new float[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var span = Mathf.Min(reach, Mathf.Min(i, values.Length - 1 - i));
                var sum = 0f;
                for (var j = i - span; j <= i + span; j++)
                    sum += values[j];
                smoothed[i] = sum / (2 * span + 1);
            }

            return smoothed;
        }
    }
}

