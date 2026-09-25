using System;
using System.Collections.Generic;
using PromptWaffle.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The shapes a tool's ghost is drawn from: a band along a curve, a disc at a node, a line out
    /// to a handle. Vertex-coloured triangles built into whatever lists the caller hands over, so
    /// one mesh carries a whole preview.
    ///
    /// Everything here is double-sided on purpose. A ghost is drawn at the height the ground is
    /// being asked to become, which is often *inside* a hill, and a preview you can only see from
    /// above is no use when you are looking into a cutting from below.
    ///
    /// These were the road tool's private helpers until the terraform tool wanted the same three
    /// shapes; they sit beside <see cref="DesignationsView.AddTile"/>, which every overlay in the
    /// game already shares.
    /// </summary>
    public static class GhostMesh
    {
        /// <summary>
        /// A band <paramref name="half"/> cells either side of the samples, <paramref name="lift"/>
        /// metres above the height the curve carries — not above the ground. A road cut through a
        /// rise is at its own level, and a ribbon lifted to the ground would show it climbing a hill
        /// it is being dug through (Ronan, 2026-09-23).
        /// </summary>
        public static void Ribbon(IReadOnlyList<RoadSample> samples, float half, float lift,
            Func<int, Color32> color, List<Vector3> vertices, List<Color32> colors, List<int> triangles,
            float cell = 1f)
        {
            if (samples == null || samples.Count < 2)
                return;

            var start = vertices.Count;
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                var across = new Vector2(-s.Direction.y, s.Direction.x) * half;
                var height = s.Position.y + lift;
                vertices.Add(new Vector3((s.Position.x - across.x) * cell, height, (s.Position.z - across.y) * cell));
                vertices.Add(new Vector3((s.Position.x + across.x) * cell, height, (s.Position.z + across.y) * cell));
                var c = color(s.Segment);
                colors.Add(c);
                colors.Add(c);
                if (i == 0)
                    continue;
                var b = start + (i - 1) * 2;
                triangles.Add(b); triangles.Add(b + 2); triangles.Add(b + 1);
                triangles.Add(b + 1); triangles.Add(b + 2); triangles.Add(b + 3);
                triangles.Add(b); triangles.Add(b + 1); triangles.Add(b + 2);
                triangles.Add(b + 1); triangles.Add(b + 3); triangles.Add(b + 2);
            }
        }

        /// <summary>
        /// The ground as it will be once the work is done: every cell in <paramref name="heights"/>
        /// (keyed z × width + x, in metres) drawn at its finished height, as one continuous,
        /// shaded surface rather than a scatter of flat tiles. Each corner takes the average of the
        /// finished heights of the cells round it — cells not in the plan count at the ground's
        /// height today — so the surface meets the untouched land at its edge instead of standing
        /// off it. Light is baked in from one fixed sun, because a cutting's faces only read as
        /// faces when they are darker than the road between them.
        /// </summary>
        public static void Surface(TerrainGrid grid, IReadOnlyDictionary<int, float> heights, Func<int, Color32> colorOf,
            float lift, List<Vector3> vertices, List<Color32> colors, List<int> triangles)
        {
            if (grid == null || heights == null || heights.Count == 0)
                return;
            var width = grid.Width;
            var cell = grid.CellSize;

            float Finished(int x, int z)
            {
                if (!grid.IsGround(x, z))
                    return float.NaN;
                return heights.TryGetValue(z * width + x, out var height) ? height : grid.GetSurfaceHeight(x, z);
            }

            // A corner is shared by the four cells round it; void cells (sea, off the map) do not count.
            float Corner(int cx, int cz)
            {
                var sum = 0f;
                var count = 0;
                for (var dz = -1; dz <= 0; dz++)
                {
                    for (var dx = -1; dx <= 0; dx++)
                    {
                        var height = Finished(cx + dx, cz + dz);
                        if (float.IsNaN(height))
                            continue;
                        sum += height;
                        count++;
                    }
                }

                return count > 0 ? sum / count : 0f;
            }

            foreach (var pair in heights)
            {
                var x = pair.Key % width;
                var z = pair.Key / width;
                var sw = Corner(x, z);
                var nw = Corner(x, z + 1);
                var ne = Corner(x + 1, z + 1);
                var se = Corner(x + 1, z);
                var slopeX = (se + ne - sw - nw) * 0.5f / cell;
                var slopeZ = (nw + ne - sw - se) * 0.5f / cell;
                var normal = new Vector3(-slopeX, 1f, -slopeZ).normalized;
                var light = Mathf.Lerp(0.5f, 1.05f, Mathf.Clamp01(Vector3.Dot(normal, Sun)));
                var color = colorOf(pair.Key);
                color.r = (byte)Mathf.Min(255f, color.r * light);
                color.g = (byte)Mathf.Min(255f, color.g * light);
                color.b = (byte)Mathf.Min(255f, color.b * light);
                DesignationsView.AddTile(x, z, sw + lift, nw + lift, ne + lift, se + lift, color,
                    vertices, colors, triangles, cell);
            }
        }

        /// <summary>The light the finished surface is shaded by: high, from the south-west.</summary>
        static readonly Vector3 Sun = new Vector3(-0.35f, 0.85f, -0.4f).normalized;

        public static void Disc(Vector2 at, float height, float radius, Color32 color,
            List<Vector3> vertices, List<Color32> colors, List<int> triangles, float cell = 1f)
        {
            const int Segments = 20;
            var start = vertices.Count;
            vertices.Add(new Vector3(at.x * cell, height, at.y * cell));
            colors.Add(color);
            for (var i = 0; i <= Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                vertices.Add(new Vector3((at.x + Mathf.Cos(a) * radius) * cell, height,
                    (at.y + Mathf.Sin(a) * radius) * cell));
                colors.Add(color);
                if (i == 0)
                    continue;
                triangles.Add(start); triangles.Add(start + i + 1); triangles.Add(start + i);
                triangles.Add(start); triangles.Add(start + i); triangles.Add(start + i + 1);
            }
        }

        public static void Line(Vector2 from, Vector2 to, float height, float half, Color32 color,
            List<Vector3> vertices, List<Color32> colors, List<int> triangles, float cell = 1f) =>
            Line(from, to, height, height, half, color, vertices, colors, triangles, cell);

        /// <summary>
        /// A line whose two ends are at different heights, so a run of them can follow the ground
        /// without breaking into dashes wherever it crosses a step.
        /// </summary>
        public static void Line(Vector2 from, Vector2 to, float fromHeight, float toHeight, float half,
            Color32 color, List<Vector3> vertices, List<Color32> colors, List<int> triangles, float cell = 1f)
        {
            var along = to - from;
            if (along.sqrMagnitude < 1e-6f)
                return;
            var across = new Vector2(-along.y, along.x).normalized * half;
            var start = vertices.Count;
            vertices.Add(new Vector3((from.x - across.x) * cell, fromHeight, (from.y - across.y) * cell));
            vertices.Add(new Vector3((from.x + across.x) * cell, fromHeight, (from.y + across.y) * cell));
            vertices.Add(new Vector3((to.x - across.x) * cell, toHeight, (to.y - across.y) * cell));
            vertices.Add(new Vector3((to.x + across.x) * cell, toHeight, (to.y + across.y) * cell));
            for (var i = 0; i < 4; i++)
                colors.Add(color);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
            triangles.Add(start + 1); triangles.Add(start + 2); triangles.Add(start + 3);
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start + 1); triangles.Add(start + 3); triangles.Add(start + 2);
        }

        /// <summary>
        /// A closed outline as a band of the given width, walked all the way round. Takes points
        /// rather than samples, so a shape with straight edges draws its corners as corners.
        /// </summary>
        public static void Loop(IReadOnlyList<Vector2> outline, float height, float half, Color32 color,
            List<Vector3> vertices, List<Color32> colors, List<int> triangles, float cell = 1f)
        {
            if (outline == null || outline.Count < 2)
                return;
            for (var i = 0; i < outline.Count; i++)
                Line(outline[i], outline[(i + 1) % outline.Count], height, half, color,
                    vertices, colors, triangles, cell);
        }

        /// <summary>
        /// The same loop, but lying on the ground rather than at one height: each edge is walked in
        /// short pieces and each piece takes the height <paramref name="heightAt"/> gives it.
        ///
        /// An outline marks a boundary *on the land*, and a shape drawn across a slope with its
        /// outline pinned at a single height leaves the line hanging in the air on one side and
        /// buried on the other — it stops looking like the edge of the thing it belongs to.
        /// </summary>
        public static void LoopOnGround(IReadOnlyList<Vector2> outline, Func<Vector2, float> heightAt, float half,
            Color32 color, List<Vector3> vertices, List<Color32> colors, List<int> triangles, float cell = 1f,
            float step = 2f)
        {
            if (outline == null || outline.Count < 2 || heightAt == null)
                return;

            for (var i = 0; i < outline.Count; i++)
            {
                var from = outline[i];
                var to = outline[(i + 1) % outline.Count];
                var pieces = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / Mathf.Max(0.5f, step)));
                for (var piece = 0; piece < pieces; piece++)
                {
                    var a = Vector2.Lerp(from, to, piece / (float)pieces);
                    var b = Vector2.Lerp(from, to, (piece + 1) / (float)pieces);
                    // Each piece takes its own two end heights, so consecutive pieces meet instead
                    // of leaving a gap wherever the ground steps — flat pieces at their midpoints
                    // drew the outline as an accidental dashed line (2026-09-23).
                    Line(a, b, heightAt(a), heightAt(b), half, color, vertices, colors, triangles, cell);
                }
            }
        }
    }
}
