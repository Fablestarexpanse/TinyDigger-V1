using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>A point along a road's centre line.</summary>
    public readonly struct RoadSample
    {
        public RoadSample(Vector3 position, Vector2 direction, float distance, int segment)
        {
            Position = position;
            Direction = direction;
            Distance = distance;
            Segment = segment;
        }

        /// <summary>x and z in cells, y the road's height in metres.</summary>
        public Vector3 Position { get; }

        /// <summary>The way the road runs here, flat and of unit length.</summary>
        public Vector2 Direction { get; }

        /// <summary>Cells along the road from its first node, measured on the flat.</summary>
        public float Distance { get; }

        /// <summary>Which segment of the chain the sample is on (0 for the first).</summary>
        public int Segment { get; }
    }

    /// <summary>
    /// The curve a road follows through its nodes (Slice 17 Part B): a cubic Hermite spline, the
    /// same curve as a cubic Bézier with its handles a third of the tangent out. Each node's
    /// tangent is automatic (Catmull-Rom: the direction from the node before to the node after)
    /// unless the player has dragged its handle. The spline is 3D: the height runs along it
    /// smoothly too, but never beyond the heights at either end of a segment, so a road never
    /// humps or dips between two nodes. Because the height eases in and out at the nodes, a
    /// segment is steepest in its middle: <see cref="Grade"/> is that steepest grade, not the
    /// average, since the steepest stretch is what the crew has to climb.
    /// </summary>
    public static class RoadSpline
    {
        /// <summary>
        /// Samples the road <paramref name="spacing"/> cells apart along its length (measured on the
        /// flat), from the first node to the last, both included. Distances only ever grow.
        /// </summary>
        public static void Sample(IReadOnlyList<RoadNode> chain, float spacing, List<RoadSample> into)
        {
            if (chain == null)
                throw new ArgumentNullException(nameof(chain));
            into.Clear();
            if (chain.Count < 2 || spacing <= 0f)
                return;

            // A fine polyline first, with its running length; then even steps along it.
            var fine = new List<(Vector3 point, int segment)>();
            for (var i = 0; i + 1 < chain.Count; i++)
            {
                var chord = Vector2.Distance(chain[i].Position, chain[i + 1].Position);
                var steps = Mathf.Max(8, Mathf.CeilToInt(chord * 6f));
                for (var s = i == 0 ? 0 : 1; s <= steps; s++)
                    fine.Add((Evaluate(chain, i, s / (float)steps), i));
            }

            var travelled = 0f;
            var next = 0f;
            for (var k = 0; k < fine.Count; k++)
            {
                var (point, segment) = fine[k];
                if (k > 0)
                {
                    var (previous, _) = fine[k - 1];
                    var step = Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(point.x, point.z));
                    // Lay down every even step that falls inside this piece of the polyline.
                    while (next <= travelled + step && step > 1e-6f)
                    {
                        var t = (next - travelled) / step;
                        var at = Vector3.Lerp(previous, point, t);
                        into.Add(new RoadSample(at, Flat(point - previous), next, segment));
                        next += spacing;
                    }

                    travelled += step;
                }
                else
                {
                    into.Add(new RoadSample(point, Flat(Evaluate(chain, 0, 0.05f) - point), 0f, 0));
                    next = spacing;
                }
            }

            var (last, lastSegment) = fine[fine.Count - 1];
            if (into.Count == 0 || travelled - into[into.Count - 1].Distance > 1e-4f)
                into.Add(new RoadSample(last, into.Count > 0 ? into[into.Count - 1].Direction : Vector2.right, travelled, lastSegment));
        }

        /// <summary>The point <paramref name="t"/> (0–1) of the way along segment <paramref name="i"/>.</summary>
        public static Vector3 Evaluate(IReadOnlyList<RoadNode> chain, int i, float t)
        {
            var a = chain[i];
            var b = chain[i + 1];
            var ta = Tangent(chain, i);
            var tb = Tangent(chain, i + 1);
            var t2 = t * t;
            var t3 = t2 * t;
            var h00 = 2f * t3 - 3f * t2 + 1f;
            var h10 = t3 - 2f * t2 + t;
            var h01 = -2f * t3 + 3f * t2;
            var h11 = t3 - t2;
            var flat = h00 * a.Position + h10 * ta + h01 * b.Position + h11 * tb;

            // Height: the same curve through the node heights, held within the segment's two ends.
            var ha = HeightTangent(chain, i);
            var hb = HeightTangent(chain, i + 1);
            var height = h00 * a.Height + h10 * ha + h01 * b.Height + h11 * hb;
            height = Mathf.Clamp(height, Mathf.Min(a.Height, b.Height), Mathf.Max(a.Height, b.Height));
            return new Vector3(flat.x, height, flat.y);
        }

        /// <summary>The flat tangent at node <paramref name="i"/>: its handle (times three, as a Bézier's), or Catmull-Rom.</summary>
        public static Vector2 Tangent(IReadOnlyList<RoadNode> chain, int i)
        {
            var node = chain[i];
            if (node.HasHandle)
                return node.Handle * 3f;
            var before = chain[Mathf.Max(0, i - 1)].Position;
            var after = chain[Mathf.Min(chain.Count - 1, i + 1)].Position;
            var ends = i == 0 || i == chain.Count - 1;
            return ends ? after - before : (after - before) * 0.5f;
        }

        static float HeightTangent(IReadOnlyList<RoadNode> chain, int i)
        {
            var before = chain[Mathf.Max(0, i - 1)].Height;
            var after = chain[Mathf.Min(chain.Count - 1, i + 1)].Height;
            var ends = i == 0 || i == chain.Count - 1;
            return ends ? after - before : (after - before) * 0.5f;
        }

        /// <summary>Cells along segment <paramref name="i"/>, on the flat.</summary>
        public static float SegmentLength(IReadOnlyList<RoadNode> chain, int i)
        {
            var length = 0f;
            var previous = Evaluate(chain, i, 0f);
            const int steps = 64;
            for (var s = 1; s <= steps; s++)
            {
                var point = Evaluate(chain, i, s / (float)steps);
                length += Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(point.x, point.z));
                previous = point;
            }

            return length;
        }

        /// <summary>
        /// Segment <paramref name="i"/>'s grade: the steepest rise over run anywhere along it, as a
        /// fraction (0.12 is 12%), on cells <paramref name="cellSize"/> metres across.
        /// </summary>
        public static float Grade(IReadOnlyList<RoadNode> chain, int i, float cellSize)
        {
            const int steps = 64;
            var steepest = 0f;
            var previous = Evaluate(chain, i, 0f);
            for (var s = 1; s <= steps; s++)
            {
                var point = Evaluate(chain, i, s / (float)steps);
                var run = Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(point.x, point.z)) * cellSize;
                if (run > 1e-5f)
                    steepest = Mathf.Max(steepest, Mathf.Abs(point.y - previous.y) / run);
                previous = point;
            }

            return steepest;
        }

        /// <summary>
        /// The tightest turn anywhere along segment <paramref name="i"/>, as the radius of the
        /// circle the road bends around, in metres. A straight segment turns around nothing at
        /// all, so it returns <see cref="float.PositiveInfinity"/>: bigger is always gentler, and
        /// a minimum turn radius is a floor to compare against.
        /// </summary>
        public static float TurnRadius(IReadOnlyList<RoadNode> chain, int i, float cellSize) =>
            TurnRadius(chain, i, cellSize, 0f, 1f);

        /// <summary>
        /// The tightest turn along the stretch of segment <paramref name="i"/> between
        /// <paramref name="from"/> and <paramref name="to"/> (0–1 along it). Shaping one node's
        /// handle bends the curve near that node and straightens it further off, so a tool that
        /// judged the whole segment would read the far end's bend and think it had done nothing.
        /// </summary>
        public static float TurnRadius(IReadOnlyList<RoadNode> chain, int i, float cellSize, float from, float to)
        {
            const int steps = 48;
            var tightest = float.PositiveInfinity;
            float At(int s) => Mathf.Lerp(from, to, s / (float)steps);
            var origin = chain[i].Position;
            var a = FlatNear(chain, i, At(0), origin);
            var b = FlatNear(chain, i, At(1), origin);
            for (var s = 2; s <= steps; s++)
            {
                var c = FlatNear(chain, i, At(s), origin);
                var radius = Circumradius(a, b, c) * cellSize;
                if (radius < tightest)
                    tightest = radius;
                a = b;
                b = c;
            }

            return tightest;
        }

        /// <summary>The tightest turn on the whole chain, in metres, or infinity if it never bends.</summary>
        public static float TightestTurn(IReadOnlyList<RoadNode> chain, float cellSize)
        {
            var tightest = float.PositiveInfinity;
            for (var i = 0; i + 1 < chain.Count; i++)
                tightest = Mathf.Min(tightest, TurnRadius(chain, i, cellSize));
            return tightest;
        }

        /// <summary>
        /// The radius of the circle through three points, in the points' own units. Three points
        /// in a line have no circle through them, so that returns infinity rather than dividing
        /// by a zero area.
        /// </summary>
        public static float Circumradius(Vector2 a, Vector2 b, Vector2 c)
        {
            // Rebased on the middle point and worked in double. The three points are a fraction of
            // a cell apart but sit wherever the road does — fifteen hundred cells out on this
            // island — and taking the area from the raw coordinates threw away most of the digits
            // that matter: a four-metre arc read as four metres at the origin and 3.7 m out in the
            // world, which made the tool warn about bends it had just built properly (2026-09-22).
            double ux = (double)a.x - b.x, uy = (double)a.y - b.y;
            double wx = (double)c.x - b.x, wy = (double)c.y - b.y;
            var twiceArea = System.Math.Abs(ux * wy - uy * wx);
            if (twiceArea < 1e-12)
                return float.PositiveInfinity;
            var u = System.Math.Sqrt(ux * ux + uy * uy);
            var w = System.Math.Sqrt(wx * wx + wy * wy);
            var uw = System.Math.Sqrt((ux - wx) * (ux - wx) + (uy - wy) * (uy - wy));
            return (float)(u * w * uw / (2.0 * twiceArea));
        }

        static Vector2 Flat3(Vector3 v) => new Vector2(v.x, v.z);

        /// <summary>
        /// The flat point <paramref name="t"/> along segment <paramref name="i"/>, measured from
        /// <paramref name="origin"/> rather than from the map's corner. A bend is read from points
        /// a tenth of a cell apart, and a road drawn fifteen hundred cells out has already lost
        /// those digits by the time <see cref="Evaluate"/> has added its terms together: the same
        /// four-metre arc read 4 m at the origin and 3.7 m out in the world (2026-09-22). Taking
        /// the node positions off first keeps every term small and the digits with them.
        /// </summary>
        static Vector2 FlatNear(IReadOnlyList<RoadNode> chain, int i, float t, Vector2 origin)
        {
            var a = chain[i].Position - origin;
            var b = chain[i + 1].Position - origin;
            var ta = Tangent(chain, i);
            var tb = Tangent(chain, i + 1);
            var t2 = t * t;
            var t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * a
                   + (t3 - 2f * t2 + t) * ta
                   + (-2f * t3 + 3f * t2) * b
                   + (t3 - t2) * tb;
        }

        /// <summary>Segment <paramref name="i"/>'s rise over its whole length on the flat: its average grade.</summary>
        public static float AverageGrade(IReadOnlyList<RoadNode> chain, int i, float cellSize)
        {
            var run = SegmentLength(chain, i) * cellSize;
            return run < 1e-4f ? 0f : Mathf.Abs(chain[i + 1].Height - chain[i].Height) / run;
        }

        static Vector2 Flat(Vector3 v)
        {
            var flat = new Vector2(v.x, v.z);
            return flat.sqrMagnitude > 1e-12f ? flat.normalized : Vector2.right;
        }
    }
}
