using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Turns a road into ground work (Slice 17 Part B). The road's target surface is the spline's
    /// height across its full width, then a half-cell shoulder blending back into the ground.
    /// Those cells become Dig or Fill designations to that height; the cut faces and embankments
    /// beyond them are left to the slump rules, as with any dig or fill. <see cref="Settle"/> works
    /// out what the slump will leave, at the materials' angles of repose, so the ghost can show the
    /// whole footprint and its true cost before anything is committed.
    /// </summary>
    public static class RoadPlanner
    {
        /// <summary>Cells of shoulder either side of the road, blended into the ground.</summary>
        public const float Shoulder = 0.5f;

        /// <summary>Default steepest grade a road may climb: 12%. Over it the ghost warns; over twice it, the road is refused.</summary>
        public const float DefaultMaxGrade = 0.12f;

        /// <summary>Cells apart the centre line is sampled at when planning.</summary>
        public const float SampleSpacing = 0.25f;

        /// <summary>
        /// The road's footprint: every cell within half its width of the centre line (road bed, at
        /// the line's height there) and the shoulder beyond (blended back to the ground). Heights
        /// are rounded to the grid's height step. Road-bed cells also go into
        /// <paramref name="bed"/> if given: those are the cells that become Road once built.
        /// </summary>
        public static void Footprint(TerrainGrid grid, IReadOnlyList<RoadSample> samples, int width, List<PlannedCell> into,
            List<Vector2Int> bed = null, bool includeSettled = false)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            into.Clear();
            bed?.Clear();
            if (samples == null || samples.Count < 2)
                return;

            var half = width * 0.5f;
            var reach = half + Shoulder;
            var box = Mathf.CeilToInt(reach) + 1;
            // The nearest sample to each cell, found by stamping each sample over the cells round it.
            var nearest = new Dictionary<int, (float distance, float height)>();
            foreach (var sample in samples)
            {
                var cx = Mathf.FloorToInt(sample.Position.x);
                var cz = Mathf.FloorToInt(sample.Position.z);
                for (var dz = -box; dz <= box; dz++)
                {
                    for (var dx = -box; dx <= box; dx++)
                    {
                        var x = cx + dx;
                        var z = cz + dz;
                        if (!grid.IsGround(x, z))
                            continue;
                        var distance = Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), new Vector2(sample.Position.x, sample.Position.z));
                        if (distance > reach)
                            continue;
                        var cell = z * grid.Width + x;
                        if (!nearest.TryGetValue(cell, out var best) || distance < best.distance)
                            nearest[cell] = (distance, sample.Position.y);
                    }
                }
            }

            var step = grid.HeightStep > 0f ? grid.HeightStep : 0f;
            foreach (var pair in nearest)
            {
                var x = pair.Key % grid.Width;
                var z = pair.Key / grid.Width;
                var (distance, height) = pair.Value;
                var ground = grid.GetSurfaceHeight(x, z);
                var target = distance <= half
                    ? height
                    : Mathf.Lerp(height, ground, (distance - half) / Shoulder);
                if (step > 0f)
                    target = Mathf.Round(target / step) * step;
                if (distance <= half)
                    bed?.Add(new Vector2Int(x, z));
                Add(grid, x, z, target, into, includeSettled);
            }

            // In a fixed order, so the same road always plans the same way.
            into.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
            bed?.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        }

        /// <summary>
        /// What the ground round a footprint will settle to once it is built: a cut face stands no
        /// steeper than the ground's own angle of repose above the road, and an embankment of
        /// tipped spoil (<paramref name="spoil"/>) runs out no steeper than its angle below it.
        /// Adds the cells that would move, beyond the footprint, to <paramref name="faces"/>, with
        /// the height each settles at, out to <paramref name="reach"/> cells.
        /// </summary>
        public static void Settle(TerrainGrid grid, IReadOnlyList<PlannedCell> footprint, MaterialId spoil, int reach, List<PlannedCell> faces)
        {
            faces.Clear();
            if (footprint.Count == 0)
                return;
            var planned = new Dictionary<int, float>();
            foreach (var cell in footprint)
                planned[cell.Z * grid.Width + cell.X] = cell.Height;

            var materials = grid.Materials;
            var fillSlope = Mathf.Tan(materials.Get(spoil).AngleOfRepose * Mathf.Deg2Rad) * grid.CellSize;
            var settled = new Dictionary<int, float>();
            foreach (var cell in footprint)
            {
                for (var dz = -reach; dz <= reach; dz++)
                {
                    for (var dx = -reach; dx <= reach; dx++)
                    {
                        var x = cell.X + dx;
                        var z = cell.Z + dz;
                        var index = z * grid.Width + x;
                        if (!grid.IsGround(x, z) || planned.ContainsKey(index))
                            continue;
                        var run = Mathf.Sqrt(dx * dx + dz * dz);
                        var ground = settled.TryGetValue(index, out var already) ? already : grid.GetSurfaceHeight(x, z);
                        var cutSlope = Mathf.Tan(materials.Get(grid.GetTopMaterial(x, z)).AngleOfRepose * Mathf.Deg2Rad) * grid.CellSize;
                        // Ground above the road is cut back to the steepest face it holds; ground
                        // below it is built up to the steepest bank the spoil holds.
                        var top = cell.Height + run * cutSlope;
                        var bottom = cell.Height - run * fillSlope;
                        var height = Mathf.Clamp(ground, bottom, top);
                        if (Math.Abs(height - ground) > Blueprints.Tolerance)
                            settled[index] = height;
                    }
                }
            }

            foreach (var pair in settled)
            {
                var x = pair.Key % grid.Width;
                var z = pair.Key / grid.Width;
                Add(grid, x, z, pair.Value, faces, false);
            }

            faces.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        }

        /// <summary>Each segment's steepest grade, as a fraction, and the worst of them.</summary>
        public static float Grades(IReadOnlyList<RoadNode> chain, float cellSize, List<float> into)
        {
            into.Clear();
            var worst = 0f;
            for (var i = 0; i + 1 < chain.Count; i++)
            {
                var grade = RoadSpline.Grade(chain, i, cellSize);
                into.Add(grade);
                worst = Mathf.Max(worst, grade);
            }

            return worst;
        }

        /// <summary>How a grade reads against the limit: fine, too steep to recommend (orange), or refused (red, over twice the limit).</summary>
        /// <summary>
        /// The tightest turn the crew are taken to drive without slowing to a crawl, in metres.
        /// Four metres is a little over five of the half-metre cells, which is the tightest bend a
        /// 0.7 m dumper rounds without reversing.
        /// </summary>
        public const float DefaultMinTurnRadius = 4f;

        /// <summary>Each segment's tightest turn in metres, and the tightest of them all.</summary>
        public static float Turns(IReadOnlyList<RoadNode> chain, float cellSize, List<float> into)
        {
            into.Clear();
            var tightest = float.PositiveInfinity;
            for (var i = 0; i + 1 < chain.Count; i++)
            {
                var radius = RoadSpline.TurnRadius(chain, i, cellSize);
                into.Add(radius);
                tightest = Mathf.Min(tightest, radius);
            }

            return tightest;
        }

        /// <summary>
        /// How a turn of <paramref name="radius"/> metres is judged against the minimum: gentler is
        /// fine, and half the minimum or tighter is refused, as the grades are.
        /// </summary>
        public static RoadGradeState JudgeTurn(float radius, float minRadius) =>
            radius < minRadius * 0.5f - 1e-5f ? RoadGradeState.Refused
            : radius < minRadius - 1e-5f ? RoadGradeState.Steep
            : RoadGradeState.Fine;

        public static RoadGradeState Judge(float grade, float maxGrade) =>
            grade > maxGrade * 2f + 1e-5f ? RoadGradeState.Refused
            : grade > maxGrade + 1e-5f ? RoadGradeState.Steep
            : RoadGradeState.Fine;

        static void Add(TerrainGrid grid, int x, int z, float height, List<PlannedCell> into, bool includeSettled)
        {
            var difference = grid.GetSurfaceHeight(x, z) - height;
            if (Math.Abs(difference) <= Blueprints.Tolerance)
            {
                if (includeSettled)
                    into.Add(new PlannedCell(x, z, height, 0f, 0f));
                return;
            }

            into.Add(difference > 0f
                ? new PlannedCell(x, z, height, difference, 0f)
                : new PlannedCell(x, z, height, 0f, -difference));
        }
    }

    public enum RoadGradeState
    {
        Fine,
        Steep,
        Refused,
    }
}
