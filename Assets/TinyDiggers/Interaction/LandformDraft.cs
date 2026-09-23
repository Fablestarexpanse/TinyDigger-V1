using System;
using System.Collections.Generic;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The shape being drawn, before it is part of the plan. <see cref="RoadDraft"/>'s sibling, and
    /// deliberately the same shape of thing: plain C#, a list of nodes, a version that ticks on
    /// every edit, and nothing that knows about a scene — so what the tool does to an outline can be
    /// tested without one.
    /// </summary>
    public sealed class LandformDraft
    {
        /// <summary>Cells a click has to land within to take hold of a node instead of adding one.</summary>
        public const float GrabRadius = 1.5f;

        public Landform Form { get; private set; } = new Landform();

        /// <summary>Which shape in the plan is being re-drawn, or 0 for a new one.</summary>
        public int EditingId;

        /// <summary>Metres a cell is across, for the readouts.</summary>
        public float CellSize = 1f;

        /// <summary>Changes on every edit, so the host knows when to replan.</summary>
        public int Version { get; private set; }

        public bool Any => Form.Nodes.Count > 0;

        public bool CanCommit => Form.IsDrawn;

        /// <summary>Starts a fresh shape of the given kind at the given height.</summary>
        public void Begin(LandformKind kind, float height)
        {
            Form = new Landform { Kind = kind, Height = height };
            EditingId = 0;
            Touch();
        }

        /// <summary>Takes a committed shape back into the draft to be re-drawn.</summary>
        public void Load(LandformPlan plan, int id)
        {
            var form = plan?.Get(id);
            if (form == null)
                return;
            Form = new Landform
            {
                Id = form.Id,
                Kind = form.Kind,
                Name = form.Name,
                Nodes = new List<RoadNode>(form.Nodes),
                Width = form.Width,
                Height = form.Height,
                HeightB = form.HeightB,
                Ramped = form.Ramped,
                RampFrom = form.RampFrom,
                RampTo = form.RampTo,
                BenchHeight = form.BenchHeight,
                BenchWidth = form.BenchWidth,
                Curved = form.Curved,
                Blend = form.Blend,
                Spoil = form.Spoil,
            };
            EditingId = id;
            Touch();
        }

        public void Clear()
        {
            Form = new Landform { Kind = Form.Kind, Height = Form.Height, Curved = Form.Curved };
            EditingId = 0;
            Touch();
        }

        /// <summary>
        /// Adds a corner. The node's own height follows the ground, which is only what the outline
        /// is *drawn* at — an area's target is <see cref="Landform.Height"/>, one number for the
        /// whole shape, so that dragging a corner over a bank does not tilt the pad.
        /// </summary>
        public RoadNode Place(Vector2 at, Func<Vector2, float> groundAt)
        {
            var node = new RoadNode { Position = at, Height = groundAt?.Invoke(at) ?? 0f };
            Form.Nodes.Add(node);
            Touch();
            return node;
        }

        /// <summary>Which corner is within <paramref name="radius"/> cells of the point, or -1.</summary>
        public int NodeNear(Vector2 at, float radius)
        {
            var best = -1;
            var nearest = radius * radius;
            for (var i = 0; i < Form.Nodes.Count; i++)
            {
                var away = (Form.Nodes[i].Position - at).sqrMagnitude;
                if (away > nearest)
                    continue;
                nearest = away;
                best = i;
            }

            return best;
        }

        public void Move(int index, Vector2 at, Func<Vector2, float> groundAt)
        {
            if (index < 0 || index >= Form.Nodes.Count)
                return;
            Form.Nodes[index].Position = at;
            if (groundAt != null)
                Form.Nodes[index].Height = groundAt(at);
            Touch();
        }

        public void RemoveLast()
        {
            if (Form.Nodes.Count == 0)
                return;
            Form.Nodes.RemoveAt(Form.Nodes.Count - 1);
            Touch();
        }

        public void SetHeight(float height)
        {
            if (Mathf.Approximately(Form.Height, height))
                return;
            Form.Height = height;
            Touch();
        }

        /// <summary>
        /// What the shape is for. The outline is kept, so a pad you have already drawn can become a
        /// pit without drawing it again.
        /// </summary>
        public void SetKind(LandformKind kind)
        {
            if (Form.Kind == kind)
                return;
            Form.Kind = kind;
            Touch();
        }

        public void SetCurved(bool curved)
        {
            if (Form.Curved == curved)
                return;
            Form.Curved = curved;
            Touch();
        }

        /// <summary>
        /// Puts the shape into the plan — as a new one, or over the one it was loaded from. Returns
        /// the shape in the plan, or null if there is not enough of it to mean anything.
        /// </summary>
        public Landform Commit(LandformPlan plan)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (!CanCommit)
                return null;

            if (EditingId != 0)
            {
                var already = plan.Get(EditingId);
                if (already != null)
                {
                    already.Nodes = new List<RoadNode>(Form.Nodes);
                    already.Height = Form.Height;
                    already.HeightB = Form.HeightB;
                    already.Ramped = Form.Ramped;
                    already.RampFrom = Form.RampFrom;
                    already.RampTo = Form.RampTo;
                    already.Curved = Form.Curved;
                    already.Blend = Form.Blend;
                    already.Touch();
                    Clear();
                    return already;
                }
            }

            var added = plan.Add(new Landform
            {
                Kind = Form.Kind,
                Nodes = new List<RoadNode>(Form.Nodes),
                Width = Form.Width,
                Height = Form.Height,
                HeightB = Form.HeightB,
                Ramped = Form.Ramped,
                RampFrom = Form.RampFrom,
                RampTo = Form.RampTo,
                BenchHeight = Form.BenchHeight,
                BenchWidth = Form.BenchWidth,
                Curved = Form.Curved,
                Blend = Form.Blend,
                Spoil = Form.Spoil,
            });
            Clear();
            return added;
        }

        void Touch()
        {
            Version++;
            Form.Touch();
        }
    }
}
