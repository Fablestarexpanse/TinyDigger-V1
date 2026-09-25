using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
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

        public bool Any => Form.Nodes.Count > 0 || Form.Kind == LandformKind.Stamp && Form.IsDrawn;

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
                Dabs = new List<BrushDab>(form.Dabs),
                StampName = form.StampName,
                StampAsset = form.StampAsset,
                Placement = form.Placement,
            };
            EditingId = id;
            Touch();
        }

        public void Clear()
        {
            // A stamp's choice, size, turn and height carry over to the next one: placing a row of
            // the same mound should not mean setting it up again each time.
            Form = new Landform
            {
                Kind = Form.Kind, Height = Form.Height, Curved = Form.Curved,
                StampName = Form.StampName, StampAsset = Form.StampAsset, Placement = Form.Placement,
            };
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
            // A ribbon's nodes carry the height it is cut at, because the spline runs the height
            // along the chain: place two at different H and the ribbon grades between them, which is
            // how a haul route or a terrace edge is drawn. A ribbon on the ground would ask for
            // nothing, which is no use at all.
            //
            // An area's nodes only draw the outline; its target is Landform.Height, one number for
            // the whole shape, so dragging a corner over a bank does not tilt the pad.
            var height = Form.Kind == LandformKind.Ribbon ? Form.Height : groundAt?.Invoke(at) ?? 0f;
            var node = new RoadNode { Position = at, Height = height, LockToGround = false };
            Form.Nodes.Add(node);
            Touch();
            return node;
        }

        /// <summary>
        /// Lays one press of the freehand brush, unless the last one was too close to be worth
        /// having. The spacing is in cells rather than in frames, so a stroke comes out the same
        /// whether the game is running at sixty frames a second or six.
        /// </summary>
        public bool Dab(Vector2 at, int radius, BrushMode mode, float amount, float spacing = 0.5f)
        {
            if (Form.Kind != LandformKind.Brush)
                return false;
            if (Form.Dabs.Count > 0 && Vector2.Distance(Form.Dabs[Form.Dabs.Count - 1].At, at) < spacing)
                return false;

            Form.Dabs.Add(new BrushDab { At = at, Radius = radius, Mode = mode, Amount = amount });
            Touch();
            return true;
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

        // --- a stamp: which one, and where and how it sits ----------------------------------------

        /// <summary>Picks the stamp, at its own size and height; the turn and flip are kept.</summary>
        public void SetStamp(HeightStamp stamp)
        {
            if (stamp == null)
                return;
            Form.StampAsset = stamp;
            Form.StampName = stamp.name;
            Form.Placement.Size = stamp.NativeSize;
            Form.Placement.Height = stamp.NativeHeight;
            Touch();
        }

        /// <summary>
        /// Puts the stamp's middle at <paramref name="centre"/> (cells), standing on the ground
        /// there. Snapped to the cell middle, so the plan changes only when the cursor crosses a cell.
        /// </summary>
        public void MoveStamp(Vector2 centre, Func<Vector2, float> groundAt)
        {
            var snapped = new Vector2(Mathf.Floor(centre.x) + 0.5f, Mathf.Floor(centre.y) + 0.5f);
            if (snapped == Form.Placement.Centre)
                return;
            Form.Placement.Centre = snapped;
            Form.Placement.Base = groundAt(snapped);
            Touch();
        }

        /// <summary>Scales it by <paramref name="factor"/>, between 4 m and 400 m across.</summary>
        public void ScaleStamp(float factor)
        {
            Form.Placement.Size = Mathf.Clamp(Form.Placement.Size * factor, 4f, 400f);
            Touch();
        }

        public void TurnStamp(float degrees)
        {
            Form.Placement.Rotation = Mathf.Repeat(Form.Placement.Rotation + degrees, 360f);
            Touch();
        }

        /// <summary>Makes it taller or shorter by <paramref name="metres"/>, never under half a metre.</summary>
        public void RaiseStamp(float metres)
        {
            Form.Placement.Height = Mathf.Max(0.5f, Form.Placement.Height + metres);
            Touch();
        }

        public void FlipStamp()
        {
            Form.Placement.Invert = !Form.Placement.Invert;
            Touch();
        }

        /// <summary>Metres across, typed in; kept between 4 m and 400 m like the scaling keys.</summary>
        public void SetStampSize(float metres)
        {
            Form.Placement.Size = Mathf.Clamp(metres, 4f, 400f);
            Touch();
        }

        /// <summary>Degrees clockwise, typed in; any number, brought round into 0–360.</summary>
        public void SetStampTurn(float degrees)
        {
            Form.Placement.Rotation = Mathf.Repeat(degrees, 360f);
            Touch();
        }

        /// <summary>Metres high (or deep, upside down), typed in; never under half a metre.</summary>
        public void SetStampHeight(float metres)
        {
            Form.Placement.Height = Mathf.Max(0.5f, metres);
            Touch();
        }

        /// <summary>Back to the stamp as it comes: its own size and height, unturned, right way up.</summary>
        public void ResetStamp()
        {
            var stamp = Form.ResolveStamp();
            if (stamp == null)
                return;
            Form.Placement.Size = stamp.NativeSize;
            Form.Placement.Height = stamp.NativeHeight;
            Form.Placement.Rotation = 0f;
            Form.Placement.Invert = false;
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
                    already.Dabs = new List<BrushDab>(Form.Dabs);
                    already.Height = Form.Height;
                    already.HeightB = Form.HeightB;
                    already.Ramped = Form.Ramped;
                    already.RampFrom = Form.RampFrom;
                    already.RampTo = Form.RampTo;
                    already.Curved = Form.Curved;
                    already.Blend = Form.Blend;
                    already.StampName = Form.StampName;
                    already.StampAsset = Form.StampAsset;
                    already.Placement = Form.Placement;
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
                Dabs = new List<BrushDab>(Form.Dabs),
                StampName = Form.StampName,
                StampAsset = Form.StampAsset,
                Placement = Form.Placement,
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
