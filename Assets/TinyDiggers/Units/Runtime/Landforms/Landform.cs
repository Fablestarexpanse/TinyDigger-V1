using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    public enum LandformKind
    {
        /// <summary>A closed outline levelled to a height, or ramped between two.</summary>
        Area,

        /// <summary>An open chain with a width, like a road that is never paved.</summary>
        Ribbon,

        /// <summary>A closed outline dug down in benches to a floor.</summary>
        Pit,

        /// <summary>A closed outline spoil is tipped into, up to a crown.</summary>
        Heap,

        /// <summary>A freehand stroke over the plan surface.</summary>
        Brush,
    }

    /// <summary>What a landform does where it meets one drawn before it.</summary>
    public enum LandformBlend
    {
        /// <summary>Its height wins. The only rule that behaves when you cut a pad into a heap.</summary>
        Replace,

        /// <summary>The lower of the two wins: dig only, never fill.</summary>
        Lower,

        /// <summary>The higher of the two wins: fill only, never dig.</summary>
        Raise,
    }

    /// <summary>
    /// One shape in the plan: an outline, and what height the ground under it should end up at.
    ///
    /// A landform is a *statement of intent*, not an edit. Nothing is dug when one is drawn; the
    /// plan rasterises to a target height per cell, which becomes Dig and Fill designations on
    /// commit, which the crew then work through like any other job (Ronan, 2026-09-23: the crew does
    /// the work). That is also why it can be re-edited at any time: the shape is the record, and the
    /// designations are recomputed from it against the ground as it now stands.
    ///
    /// The outline is a list of <see cref="RoadNode"/> — the road tool's own node, unchanged — so an
    /// outline gets Hermite curves, draggable handles, ground-locking and 45° snapping without a
    /// line of new curve code.
    /// </summary>
    [Serializable]
    public sealed class Landform
    {
        /// <summary>Its place in the plan, and the site units are posted to.</summary>
        public int Id;

        public LandformKind Kind = LandformKind.Area;

        /// <summary>What it is called in the list. Empty means the tool names it.</summary>
        public string Name = "";

        /// <summary>
        /// The outline, in cells. A closed shape does *not* repeat its first node at the end.
        /// </summary>
        public List<RoadNode> Nodes = new List<RoadNode>();

        /// <summary>Cells across, for a <see cref="LandformKind.Ribbon"/>.</summary>
        public int Width = 3;

        /// <summary>The level, the pit floor, or the heap crown, in metres.</summary>
        public float Height;

        /// <summary>The far end of a ramped area.</summary>
        public float HeightB;

        /// <summary>Whether the area ramps from <see cref="Height"/> to <see cref="HeightB"/>.</summary>
        public bool Ramped;

        /// <summary>Where the ramp starts and ends, in cells. Only the direction between them matters.</summary>
        public Vector2 RampFrom;

        public Vector2 RampTo;

        /// <summary>Metres a pit bench drops, and cells it runs before the next one.</summary>
        public float BenchHeight = 2f;

        public float BenchWidth = 6f;

        /// <summary>
        /// Whether the outline curves between its nodes. Off by default, and deliberately: a road
        /// through four nodes should sweep, but a pad drawn through four corners should be a
        /// rectangle, and a Catmull-Rom curve through those corners bows out past every edge — a
        /// ten-cell square came out 12 by 11 (2026-09-23). Corners are what you want for a pad, a
        /// terrace or a quarry outline; a dragged handle or this switch is how you ask for a curve.
        /// </summary>
        public bool Curved;

        public LandformBlend Blend = LandformBlend.Replace;

        /// <summary>
        /// What the batter and the heap angle are worked out from. Which material is actually dug or
        /// tipped is a separate question, and a later one.
        /// </summary>
        public MaterialId Spoil = MaterialTable.DirtLoose;

        /// <summary>Changes on every edit. What the host watches to know when to replan.</summary>
        public int Version;

        public bool Closed => Kind != LandformKind.Ribbon && Kind != LandformKind.Brush;

        /// <summary>Whether it has enough nodes to mean anything.</summary>
        public bool IsDrawn => Closed ? Nodes.Count >= 3 : Nodes.Count >= 2;

        public void Touch() => Version++;

        /// <summary>
        /// The target height at a point inside the shape, in cells: the level, or the ramp's height
        /// where the point falls along it. Rounded to the grid's height step, so a gentle ramp comes
        /// out as even treads rather than a surface no machine can build.
        /// </summary>
        public float TargetAt(Vector2 at, float heightStep)
        {
            var height = Height;
            if (Ramped)
            {
                var along = RampTo - RampFrom;
                var length = along.sqrMagnitude;
                var t = length < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(at - RampFrom, along) / length);
                height = Mathf.Lerp(Height, HeightB, t);
            }

            return heightStep > 0f ? Mathf.Round(height / heightStep) * heightStep : height;
        }
    }
}
