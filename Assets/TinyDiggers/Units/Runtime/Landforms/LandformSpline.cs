using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// The curve a closed outline follows, which is <see cref="RoadSpline"/> with its ends joined.
    ///
    /// A road has two ends and <see cref="RoadSpline.Tangent"/> treats them as ends: the first and
    /// last node take a doubled one-sided tangent, because there is nothing beyond them. A loop has
    /// no ends, so sampling a chain whose last node happens to sit on its first puts a kink in the
    /// curve exactly where the player cannot see a reason for one.
    ///
    /// The fix is to give the seam its neighbours rather than to write another spline: the chain is
    /// wrapped — the last node before the first, the first two nodes after the last — so every node
    /// of the loop is an *interior* node with a proper Catmull-Rom tangent, and the samples over the
    /// padding are dropped. Every rule the road curve has (handles, the height clamp within a
    /// segment, <see cref="RoadSpline.Grade"/>, <see cref="RoadSpline.TurnRadius"/>) still holds.
    /// </summary>
    public static class LandformSpline
    {
        /// <summary>
        /// Samples the closed outline through <paramref name="loop"/> — which does *not* repeat its
        /// first node at the end — every <paramref name="spacing"/> cells, all the way round.
        /// Sample distances start at zero at the first node, and each sample's
        /// <see cref="RoadSample.Segment"/> is the outline's own segment: 0 from node 0 to node 1,
        /// and the last is the closing one, from the last node back to the first.
        /// </summary>
        public static void SampleLoop(IReadOnlyList<RoadNode> loop, float spacing, List<RoadSample> into)
        {
            if (loop == null)
                throw new ArgumentNullException(nameof(loop));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            var count = loop.Count;
            if (count < 3 || spacing <= 0f)
                return;

            // [last, first … last, first, second]: the loop's nodes are indices 1 … count, and its
            // own segments are 1 … count, so both the seam and every node either side of it have a
            // node before and a node after.
            var wrapped = new List<RoadNode>(count + 3) { loop[count - 1] };
            for (var i = 0; i < count; i++)
                wrapped.Add(loop[i]);
            wrapped.Add(loop[0]);
            wrapped.Add(loop[1]);

            var all = new List<RoadSample>();
            RoadSpline.Sample(wrapped, spacing, all);

            // Only the loop's own segments, with distance measured from the first node rather than
            // from the padding, and the segment numbers put back the way the outline counts them.
            var start = float.NaN;
            foreach (var sample in all)
            {
                if (sample.Segment < 1 || sample.Segment > count)
                    continue;
                if (float.IsNaN(start))
                    start = sample.Distance;
                into.Add(new RoadSample(sample.Position, sample.Direction, sample.Distance - start,
                    sample.Segment - 1));
            }
        }

        /// <summary>
        /// The outline as a flat polygon in cells, for the fill test: the sampled points with the
        /// height thrown away. The loop is implicitly closed — the last point joins the first.
        /// </summary>
        public static void Outline(IReadOnlyList<RoadSample> samples, List<Vector2> into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            into.Clear();
            if (samples == null)
                return;
            foreach (var sample in samples)
                into.Add(new Vector2(sample.Position.x, sample.Position.z));
        }

        /// <summary>
        /// The polygon a shape's outline traces: its nodes joined by curves if it is
        /// <see cref="Landform.Curved"/>, and by straight edges if it is not — which is the default,
        /// because a pad drawn through four corners should be a rectangle rather than the bowed
        /// shape a curve through those corners makes.
        /// </summary>
        public static void Polygon(Landform form, float spacing, List<RoadSample> samples, List<Vector2> into)
        {
            if (form == null)
                throw new ArgumentNullException(nameof(form));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            if (form.Curved)
            {
                SampleLoop(form.Nodes, spacing, samples);
                Outline(samples, into);
                return;
            }

            samples?.Clear();
            into.Clear();
            foreach (var node in form.Nodes)
                into.Add(node.Position);
        }
    }
}
