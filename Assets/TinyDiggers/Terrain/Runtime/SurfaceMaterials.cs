using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// What the ground is made of at the surface, decided as its own pass over the finished
    /// heights rather than cell by cell while columns are built.
    ///
    /// The rule that matters: **slope is measured on a smoothed heightfield**. On quantised land a
    /// uniform hillside is a staircase — a row of one-metre steps with flats between them — so the
    /// per-cell slope alternates from row to row. Anything keyed to it paints the hillside in
    /// stripes that follow the contours, which is the "contour banding" this pass exists to remove.
    /// Smoothing first means a uniform hillside is one slope, gets one material, and the variation
    /// in it comes from noise instead.
    ///
    /// Then:
    /// - Boundaries between materials are pushed about by a noise field a few tens of metres
    ///   across, so they wander instead of tracing a slope threshold exactly.
    /// - Sand is coastal only: low ground near water. It is never a mountain material.
    /// - Every patch smaller than <see cref="TerrainGenSettings.MinMaterialPatch"/> is absorbed
    ///   into the material that surrounds it most, twice, which takes out single-cell speckle.
    /// </summary>
    public static class SurfaceMaterials
    {
        static readonly int[] StepX = { 1, -1, 0, 0 };
        static readonly int[] StepZ = { 0, 0, 1, -1 };

        /// <summary>
        /// Decides the surface material of every cell of the map.
        /// </summary>
        /// <param name="heights">Finished surface heights, metres above sea level.</param>
        /// <param name="inDisc">Which cells are on the map at all.</param>
        /// <param name="toWater">Cells to the nearest water, as a flood distance.</param>
        public static MaterialId[] Assign(float[] heights, bool[] inDisc, float[] toWater,
            int width, int depth, TerrainGenSettings settings, Vector2 noiseOffset)
        {
            var smoothed = Smooth(heights, inDisc, width, depth, Mathf.Max(1, settings.SlopeSmoothing));
            var materials = new MaterialId[heights.Length];

            // A row per task: each cell reads the finished fields and writes only its own slot.
            Parallel.For(0, depth, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell])
                        continue;

                    var height = heights[cell];
                    if (height < World.SeaLevel)
                    {
                        // The seabed: sand in the shallows, silt and rock further out.
                        materials[cell] = height > settings.ShelfFarDepth ? MaterialTable.Sand : MaterialTable.Rock;
                        continue;
                    }

                    var slope = SlopeDegrees(smoothed, inDisc, width, depth, x, z, settings.GenerationCellSize);
                    // The boundaries wander: a few degrees of give either way, from a noise field
                    // a couple of dozen metres across, so no material edge traces a threshold.
                    var wander = (Noise(x, z, noiseOffset, settings.MaterialNoiseSize) - 0.5f) * 2f * settings.MaterialNoiseDegrees;
                    materials[cell] = Pick(height, slope + wander, toWater[cell], settings);
                }
            });

            // Twice: absorbing one speck can leave its neighbour below the minimum in turn.
            Despeckle(materials, inDisc, width, depth, settings.MinMaterialPatch);
            Despeckle(materials, inDisc, width, depth, settings.MinMaterialPatch);

            // The cleanup absorbs a patch into whatever surrounds it, which can carry sand up a
            // hillside if a speck of something else sat in a beach. Sand is coastal by definition,
            // so the rule is enforced after every cleanup, and last of all — a patch of hillside
            // that ends up a little small is a smaller sin than a dune at four metres.
            EnforceCoastalSand(materials, heights, smoothed, inDisc, toWater, width, depth, settings);
            Despeckle(materials, inDisc, width, depth, settings.MinMaterialPatch);
            EnforceCoastalSand(materials, heights, smoothed, inDisc, toWater, width, depth, settings);

            // That last pass can leave a cell alone in the middle of a beach it has just been
            // taken out of. A cell with no neighbour of its own material is speckle whatever put
            // it there, so it joins whichever of its neighbours it is allowed to.
            // Twice: joining one cell to its neighbours can leave the cell it left behind alone.
            RepairLonelyCells(materials, heights, inDisc, toWater, width, depth, settings);
            RepairLonelyCells(materials, heights, inDisc, toWater, width, depth, settings);
            return materials;
        }

        /// <summary>Turns any sand that is not low and coastal back into what the slope says it is.</summary>
        static void EnforceCoastalSand(MaterialId[] materials, float[] heights, float[] smoothed, bool[] inDisc,
            float[] toWater, int width, int depth, TerrainGenSettings settings)
        {
            for (var cell = 0; cell < materials.Length; cell++)
            {
                if (!inDisc[cell] || materials[cell] != MaterialTable.Sand)
                    continue;
                if (heights[cell] < World.SeaLevel)
                    continue; // The seabed is allowed its sand.
                if (heights[cell] <= settings.SandMaxHeight && toWater[cell] <= settings.SandMaxDistance)
                    continue;
                var slope = SlopeDegrees(smoothed, inDisc, width, depth, cell % width, cell / width, settings.GenerationCellSize);
                materials[cell] = slope < settings.SlopeGrass ? MaterialTable.Topsoil
                    : slope < settings.SlopeBare ? MaterialTable.Dirt : MaterialTable.Rock;
            }
        }

        /// <summary>
        /// Makes both sides of every step taller than one metre rock, because that is what a cliff
        /// is. The relaxation lets rock stand in such a step; this is the other half of the same
        /// rule, and it promotes the ground rather than flattening it — the face of a cliff is
        /// stone, whatever the slope thresholds made of it before the land settled.
        /// </summary>
        public static void PromoteCliffFaces(MaterialId[] materials, float[] heights, bool[] inDisc,
            int width, int depth, float step)
        {
            var limit = step + 1e-3f;
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell] || heights[cell] < World.SeaLevel)
                        continue;
                    for (var n = 0; n < 4; n++)
                    {
                        var nx = x + StepX[n];
                        var nz = z + StepZ[n];
                        if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                            continue;
                        var next = nz * width + nx;
                        if (!inDisc[next] || heights[next] < World.SeaLevel)
                            continue;
                        if (Mathf.Abs(heights[cell] - heights[next]) <= limit)
                            continue;
                        materials[cell] = MaterialTable.Rock;
                        materials[next] = MaterialTable.Rock;
                    }
                }
            }
        }

        /// <summary>Joins whatever a later pass left on its own to its neighbours.</summary>
        public static void Tidy(MaterialId[] materials, float[] heights, bool[] inDisc, float[] toWater,
            int width, int depth, TerrainGenSettings settings)
        {
            RepairLonelyCells(materials, heights, inDisc, toWater, width, depth, settings);
            RepairLonelyCells(materials, heights, inDisc, toWater, width, depth, settings);
        }

        /// <summary>
        /// The material for one cell: sand only low and near water, then grass, grass-and-rock,
        /// dirt-and-rock and bare rock as the ground gets steeper.
        /// </summary>
        public static MaterialId Pick(float height, float slope, float toWater, TerrainGenSettings settings)
        {
            // Sand is a coastal material and nothing else: low ground near water. Above the beach,
            // or inland of it, steep ground is dirt over rock — never a dune halfway up a
            // mountain. A cliff that happens to be low and coastal is still a cliff.
            if (height <= settings.SandMaxHeight && toWater <= settings.SandMaxDistance && slope < settings.SlopeBare)
                return MaterialTable.Sand;

            if (slope < settings.SlopeGrass)
                return MaterialTable.Topsoil;
            if (slope < settings.SlopeMixed)
                // Grass with rock showing through: the noise above decides which cells.
                return slope < settings.SlopeMixed - 5f ? MaterialTable.Topsoil : MaterialTable.Rock;
            if (slope < settings.SlopeBare)
                return slope < settings.SlopeBare - 6f ? MaterialTable.Dirt : MaterialTable.Rock;
            return MaterialTable.Rock;
        }

        /// <summary>
        /// Joins every cell that has no neighbour of its own material to the material most of its
        /// neighbours are, skipping sand where sand is not allowed.
        /// </summary>
        static void RepairLonelyCells(MaterialId[] materials, float[] heights, bool[] inDisc, float[] toWater,
            int width, int depth, TerrainGenSettings settings)
        {
            var counts = new Dictionary<MaterialId, int>();
            for (var z = 0; z < depth; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = z * width + x;
                    if (!inDisc[cell])
                        continue;

                    var material = materials[cell];
                    var mayBeSand = heights[cell] < World.SeaLevel
                        || (heights[cell] <= settings.SandMaxHeight && toWater[cell] <= settings.SandMaxDistance);

                    counts.Clear();
                    var same = 0;
                    for (var n = 0; n < 4; n++)
                    {
                        var nx = x + StepX[n];
                        var nz = z + StepZ[n];
                        if (nx < 0 || nz < 0 || nx >= width || nz >= depth)
                            continue;
                        var next = nz * width + nx;
                        if (!inDisc[next])
                            continue;
                        if (materials[next] == material)
                        {
                            same++;
                            continue;
                        }

                        if (materials[next] == MaterialTable.Sand && !mayBeSand)
                            continue;
                        counts.TryGetValue(materials[next], out var count);
                        counts[materials[next]] = count + 1;
                    }

                    if (same > 0 || counts.Count == 0)
                        continue;

                    var winner = material;
                    var best = 0;
                    foreach (var pair in counts)
                    {
                        if (pair.Value <= best)
                            continue;
                        best = pair.Value;
                        winner = pair.Key;
                    }

                    materials[cell] = winner;
                }
            }
        }

        /// <summary>A box blur of the heights, run twice so the falloff is smooth rather than square.</summary>
        public static float[] Smooth(float[] heights, bool[] inDisc, int width, int depth, int radius)
        {
            var source = heights;
            var output = new float[heights.Length];
            var pass = new float[heights.Length];

            for (var repeat = 0; repeat < 2; repeat++)
            {
                // Each row reads the previous pass and writes only itself: a row per task.
                var from = source;
                Parallel.For(0, depth, z =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var sum = 0f;
                        var count = 0;
                        for (var d = -radius; d <= radius; d++)
                        {
                            var nx = Mathf.Clamp(x + d, 0, width - 1);
                            var cell = z * width + nx;
                            if (!inDisc[cell])
                                continue;
                            sum += from[cell];
                            count++;
                        }

                        pass[z * width + x] = count == 0 ? from[z * width + x] : sum / count;
                    }
                });

                Parallel.For(0, depth, z =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var sum = 0f;
                        var count = 0;
                        for (var d = -radius; d <= radius; d++)
                        {
                            var nz = Mathf.Clamp(z + d, 0, depth - 1);
                            var cell = nz * width + x;
                            if (!inDisc[cell])
                                continue;
                            sum += pass[cell];
                            count++;
                        }

                        output[z * width + x] = count == 0 ? pass[z * width + x] : sum / count;
                    }
                });

                source = output;
            }

            return output;
        }

        /// <summary>The slope of the smoothed field at a cell, in degrees.</summary>
        public static float SlopeDegrees(float[] smoothed, bool[] inDisc, int width, int depth, int x, int z, float cellSize = 1f)
        {
            float At(int ax, int az)
            {
                ax = Mathf.Clamp(ax, 0, width - 1);
                az = Mathf.Clamp(az, 0, depth - 1);
                var cell = az * width + ax;
                return inDisc[cell] ? smoothed[cell] : smoothed[Mathf.Clamp(z, 0, depth - 1) * width + Mathf.Clamp(x, 0, width - 1)];
            }

            // Central differences over two cells, so one cell of noise cannot swing the answer.
            // Heights are metres and cells are cellSize metres: the gradient is metres per metre.
            var dx = (At(x + 1, z) - At(x - 1, z)) * 0.5f / cellSize;
            var dz = (At(x, z + 1) - At(x, z - 1)) * 0.5f / cellSize;
            return Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Absorbs every patch smaller than <paramref name="minimum"/> into whichever material
        /// touches it most. This is the coastline's blob cleanup, run over materials instead of
        /// over land, and it is what takes out single-cell speckle.
        /// </summary>
        public static void Despeckle(MaterialId[] materials, bool[] inDisc, int width, int depth, int minimum)
        {
            var seen = new bool[materials.Length];
            var stack = new Stack<int>();
            var patch = new List<int>();
            var touching = new Dictionary<MaterialId, int>();

            for (var start = 0; start < materials.Length; start++)
            {
                if (seen[start] || !inDisc[start])
                    continue;

                var material = materials[start];
                patch.Clear();
                touching.Clear();
                stack.Push(start);
                seen[start] = true;

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
                            continue;
                        if (materials[next] != material)
                        {
                            touching.TryGetValue(materials[next], out var count);
                            touching[materials[next]] = count + 1;
                            continue;
                        }

                        if (seen[next])
                            continue;
                        seen[next] = true;
                        stack.Push(next);
                    }
                }

                if (patch.Count >= minimum || touching.Count == 0)
                    continue;

                var winner = material;
                var best = 0;
                foreach (var pair in touching)
                {
                    if (pair.Value <= best)
                        continue;
                    best = pair.Value;
                    winner = pair.Key;
                }

                foreach (var cell in patch)
                    materials[cell] = winner;
            }
        }

        static float Noise(int x, int z, Vector2 offset, float featureSize)
        {
            var frequency = 1f / Mathf.Max(1f, featureSize);
            return Mathf.PerlinNoise(offset.x + x * frequency, offset.y + z * frequency);
        }
    }
}
