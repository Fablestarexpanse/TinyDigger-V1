using System;
using System.Collections.Generic;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The road being drawn, or a built road being edited (Slice 17 Part B), before it goes into
    /// the <see cref="RoadNetwork"/>. Plain C#, so the authoring rules are tested without a scene:
    /// - clicks place nodes, snapped onto any road node within <see cref="JoinRadius"/> (to join
    ///   it) or else, with <see cref="Snap45"/>, turned onto the nearest 45° from the last node;
    /// - a node takes the ground's height where it lands, and follows the ground when it is moved,
    ///   until its height is set by hand (<see cref="Raise"/>), which unlocks it;
    /// - nodes and their tangent handles can be dragged, and the last node taken back;
    /// - every segment's grade is judged against <see cref="MaxGrade"/>, and a road with any
    ///   segment over twice it cannot be committed.
    /// Nodes keep the id of the network node they came from (0 for new ones), so committing an
    /// edit moves the real nodes, junctions included.
    /// </summary>
    public sealed class RoadDraft
    {
        /// <summary>Cells within which a placed node lands on an existing road node, joining it.</summary>
        public const float JoinRadius = 1.5f;

        /// <summary>
        /// How much of a segment next to a node belongs to that node's neighbour when a bend is
        /// being shaped, as a fraction of the segment.
        /// </summary>
        const float Near = 0.15f;

        public readonly List<RoadNode> Nodes = new List<RoadNode>();

        /// <summary>The road being edited, or 0 for a new one.</summary>
        public int EditingRoad { get; private set; }

        public int Width = 3;
        public float MaxGrade = RoadPlanner.DefaultMaxGrade;
        public float MinTurnRadius = RoadPlanner.DefaultMinTurnRadius;
        public bool Snap45 = true;

        /// <summary>
        /// Metres across a cell, so the draft can talk about grades and turns in metres without
        /// being handed the grid at every call. The host sets it from the terrain.
        /// </summary>
        public float CellSize = 0.5f;

        /// <summary>
        /// A grade held while drawing, as a fraction (0.04 is 4%): each node placed takes the last
        /// node's height plus that slope over the run, rather than the ground's height, so a long
        /// ramp keeps one slope the whole way. Null while nodes simply sit on the ground.
        /// </summary>
        public float? GradeLock;

        /// <summary>
        /// Metres held above the ground by nodes that follow it: 0 sits the road on the land, and
        /// a positive offset carries it over the land as a causeway. Applied to nodes as they are
        /// placed, moved or re-locked.
        /// </summary>
        public float GroundOffset;

        /// <summary>
        /// Whether a node placed takes the height of the ground under it. Off, it takes the height
        /// of the node before it instead, so a road run at a level keeps that level: into a rise it
        /// becomes a cutting to be dug out, over a dip an embankment to be filled.
        ///
        /// Ronan, 2026-09-23: *"I start on a flat grade, run my road into the hill, and I need a
        /// way to tell it I don't want it to go up — I want it to go through."* Following the
        /// ground is what a road does across open country and is still the default; holding the
        /// level is what it does at a hill.
        /// </summary>
        public bool FollowGround = true;

        /// <summary>Changes on every edit, so views can tell when to re-plan.</summary>
        public int Version { get; private set; }

        public bool IsEmpty => Nodes.Count == 0;

        public void Clear()
        {
            Nodes.Clear();
            EditingRoad = 0;
            Version++;
        }

        /// <summary>Takes a built road out of the network into the draft for editing.</summary>
        public void Load(RoadNetwork network, int road)
        {
            Nodes.Clear();
            foreach (var node in network.Chain(road))
                Nodes.Add(Copy(node));
            EditingRoad = road;
            Width = network.WidthOf(road);
            Version++;
        }

        /// <summary>
        /// Adds a node at <paramref name="at"/> (cells). It joins a network node within
        /// <see cref="JoinRadius"/>, taking its place and height; otherwise it is turned onto the
        /// nearest 45° from the last node if <see cref="Snap45"/>, and sits on the ground there.
        /// </summary>
        public RoadNode Place(Vector2 at, Func<Vector2, float> groundAt, RoadNetwork network = null)
        {
            var join = network?.NodeNear(at, JoinRadius);
            RoadNode node;
            if (join != null)
            {
                node = Copy(join);
            }
            else
            {
                if (Snap45 && Nodes.Count > 0)
                    at = SnapAngle(Nodes[Nodes.Count - 1].Position, at);
                node = new RoadNode
                {
                    Position = at,
                    Height = groundAt(at) + GroundOffset,
                    LockToGround = true,
                    GroundOffset = GroundOffset,
                };
                if (Nodes.Count > 0)
                {
                    var last = Nodes[Nodes.Count - 1];
                    if (GradeLock.HasValue)
                    {
                        // With a grade held, the node leaves the ground and keeps the ramp's slope
                        // instead, climbing or falling whichever way the ground goes.
                        var run = Vector2.Distance(last.Position, at) * CellSize;
                        var rise = GradeLock.Value * run;
                        node.Height = last.Height + (groundAt(at) < last.Height ? -rise : rise);
                        node.LockToGround = false;
                    }
                    else if (!FollowGround)
                    {
                        // Hold the level: the road goes through the hill rather than over it.
                        node.Height = last.Height;
                        node.LockToGround = false;
                    }
                }
            }

            Nodes.Add(node);
            Version++;
            return node;
        }

        /// <summary>Turns <paramref name="to"/> onto the nearest multiple of 45° from <paramref name="from"/>, keeping its distance.</summary>
        public static Vector2 SnapAngle(Vector2 from, Vector2 to)
        {
            var offset = to - from;
            var length = offset.magnitude;
            if (length < 1e-4f)
                return to;
            var angle = Mathf.Round(Mathf.Atan2(offset.y, offset.x) / (Mathf.PI / 4f)) * (Mathf.PI / 4f);
            return from + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * length;
        }

        /// <summary>The draft node within <paramref name="radius"/> cells of a point, nearest, or -1.</summary>
        public int NodeNear(Vector2 at, float radius)
        {
            var best = -1;
            var bestDistance = radius;
            for (var i = 0; i < Nodes.Count; i++)
            {
                var distance = Vector2.Distance(Nodes[i].Position, at);
                if (distance > bestDistance)
                    continue;
                bestDistance = distance;
                best = i;
            }

            return best;
        }

        /// <summary>Where node <paramref name="index"/>'s handle is drawn: its dragged handle, or its automatic tangent's third.</summary>
        public Vector2 HandleTip(int index)
        {
            var node = Nodes[index];
            var tangent = Nodes.Count >= 2 ? RoadSpline.Tangent(Nodes, index) / 3f : Vector2.zero;
            return node.Position + (node.HasHandle ? node.Handle : tangent);
        }

        /// <summary>The node whose handle tip is within <paramref name="radius"/> cells of a point, or -1.</summary>
        public int HandleNear(Vector2 at, float radius)
        {
            for (var i = 0; i < Nodes.Count; i++)
                if (Vector2.Distance(HandleTip(i), at) <= radius)
                    return i;
            return -1;
        }

        /// <summary>Moves a node; one locked to the ground takes the ground's height where it goes.</summary>
        public void Move(int index, Vector2 at, Func<Vector2, float> groundAt)
        {
            var node = Nodes[index];
            node.Position = at;
            if (node.LockToGround)
                node.Height = groundAt(at) + node.GroundOffset;
            Version++;
        }

        public void DragHandle(int index, Vector2 tip)
        {
            Nodes[index].Handle = tip - Nodes[index].Position;
            Version++;
        }

        /// <summary>Raises (or lowers) a node by hand, which unlocks it from the ground.</summary>
        public void Raise(int index, float metres)
        {
            Nodes[index].Height += metres;
            Nodes[index].LockToGround = false;
            Version++;
        }

        /// <summary>Locks a node to the ground (taking the ground's height) or unlocks it.</summary>
        public void SetLocked(int index, bool locked, Func<Vector2, float> groundAt)
        {
            var node = Nodes[index];
            node.LockToGround = locked;
            if (locked)
                node.Height = groundAt(node.Position) + node.GroundOffset;
            Version++;
        }

        /// <summary>
        /// Sets a node's height outright, in metres, which unlocks it from the ground. This is what
        /// a typed height does, where <see cref="Raise"/> is what a scroll of the wheel does.
        /// </summary>
        public void SetHeight(int index, float metres)
        {
            Nodes[index].Height = metres;
            Nodes[index].LockToGround = false;
            Version++;
        }

        /// <summary>
        /// Holds a node <paramref name="metres"/> above the ground, following it: the causeway
        /// mode. A node that was set by hand goes back to following the ground.
        /// </summary>
        public void SetGroundOffset(int index, float metres, Func<Vector2, float> groundAt)
        {
            var node = Nodes[index];
            node.GroundOffset = metres;
            node.LockToGround = true;
            node.Height = groundAt(node.Position) + metres;
            Version++;
        }

        /// <summary>
        /// Re-cuts the heights of every node after the first so the road holds one grade the whole
        /// way, each stretch keeping the direction it already ran in (up stays up). The first node
        /// does not move, and every node it touches leaves the ground. Returns how many moved.
        /// </summary>
        public int ApplyGrade(float grade)
        {
            if (Nodes.Count < 2)
                return 0;
            var was = new float[Nodes.Count];
            for (var i = 0; i < Nodes.Count; i++)
                was[i] = Nodes[i].Height;

            var moved = Lay(grade, was);

            // The height eases in and out at each node, so a segment is steeper in its middle than
            // end to end: laying nodes at exactly the grade asked for reads back as over it. Lay
            // them again a shade shallower, by however much the first pass overshot.
            var steepest = new List<float>();
            var worst = RoadPlanner.Grades(Nodes, CellSize, steepest);
            if (worst > grade + 1e-4f)
                moved = Lay(grade * grade / worst, was);

            Version++;
            return moved;
        }

        /// <summary>
        /// Lays every node after the first at <paramref name="grade"/> from the one before, each
        /// stretch keeping the direction it ran in when the heights were <paramref name="was"/>.
        /// </summary>
        int Lay(float grade, float[] was)
        {
            var moved = 0;
            for (var i = 1; i < Nodes.Count; i++)
            {
                var run = Vector2.Distance(Nodes[i - 1].Position, Nodes[i].Position) * CellSize;
                var rise = grade * run;
                // Flat stretches stay flat; the rest keep the way they already ran.
                if (Mathf.Abs(was[i] - was[i - 1]) < 1e-4f)
                    rise = 0f;
                else if (was[i] < was[i - 1])
                    rise = -rise;
                var height = Nodes[i - 1].Height + rise;
                if (Mathf.Abs(height - was[i]) > 1e-4f)
                    moved++;
                Nodes[i].Height = height;
                Nodes[i].LockToGround = false;
            }

            return moved;
        }

        /// <summary>
        /// Rounds the bend at node <paramref name="index"/> out towards <paramref name="radius"/>
        /// metres by pulling its handle along the way the road already runs. The turn cannot always
        /// be had — two nodes close together cannot round a wide arc — so it returns the radius it
        /// actually reached, which is never tighter than the one it started from. That reading is
        /// this bend alone, not the whole road's worst: the neighbouring nodes have their own.
        /// </summary>
        public float SmoothTo(int index, float radius)
        {
            if (Nodes.Count < 2 || index < 0 || index >= Nodes.Count)
                return float.PositiveInfinity;

            var first = index == 0;
            var last = index == Nodes.Count - 1;
            var node = Nodes[index];
            var was = node.Handle;
            var direction = RoadSpline.Tangent(Nodes, index);
            if (node.HasHandle)
                direction = node.Handle;
            if (direction.sqrMagnitude < 1e-8f)
                direction = Nodes[Mathf.Min(Nodes.Count - 1, index + 1)].Position
                            - Nodes[Mathf.Max(0, index - 1)].Position;
            if (direction.sqrMagnitude < 1e-8f)
                return float.PositiveInfinity;
            direction = direction.normalized;

            // A longer handle sweeps the bend wider, up to about the shorter of the two chords;
            // past that the curve starts to overshoot and tightens again, so that is the ceiling.
            // At the two ends of the road there is only one chord to go by.
            var reach = float.MaxValue;
            if (!first)
                reach = Mathf.Min(reach, Vector2.Distance(node.Position, Nodes[index - 1].Position));
            if (!last)
                reach = Mathf.Min(reach, Vector2.Distance(node.Position, Nodes[index + 1].Position));
            // Widen by steps and stop at the first handle that makes the turn asked for; if none
            // does, keep the widest turn found, which is never tighter than the one we started at.
            var bestHandle = was;
            var best = At(was);
            const int tries = 24;
            for (var t = 1; t <= tries; t++)
            {
                var handle = direction * (reach * t / tries);
                var got = At(handle);
                if (got > best)
                {
                    best = got;
                    bestHandle = handle;
                }

                if (got >= radius)
                    break;
            }

            Nodes[index].Handle = bestHandle;
            Version++;
            return best;

            // The road either side of the node, short of the neighbouring nodes themselves: those
            // hold bends of their own, and reading them made the tool think a long handle had
            // changed nothing, so it left the handle short (2.2 m stayed 2.5 m, 2026-09-22).
            float At(Vector2 handle)
            {
                Nodes[index].Handle = handle;
                var tightest = float.PositiveInfinity;
                if (!first)
                    tightest = Mathf.Min(tightest, RoadSpline.TurnRadius(Nodes, index - 1, CellSize, Near, 1f));
                if (!last)
                    tightest = Mathf.Min(tightest, RoadSpline.TurnRadius(Nodes, index, CellSize, 0f, 1f - Near));
                return tightest;
            }
        }

        /// <summary>
        /// Rounds every bend in the road out towards <paramref name="radius"/> metres, the two end
        /// nodes included: an end's automatic tangent is a whole chord long, twice what an inside
        /// node gets, and the swing that puts into the first and last stretches was the tightest
        /// bend left on a road whose corners had all been rounded (2026-09-22). Neighbouring bends
        /// pull on one another through the segment they share, so it sweeps twice.
        /// </summary>
        public void SmoothAll(float radius)
        {
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < Nodes.Count; i++)
                    SmoothTo(i, radius);
        }

        /// <summary>
        /// Replaces the corner at node <paramref name="index"/> with a true arc of
        /// <paramref name="radius"/> metres: the corner node goes, and two nodes take its place
        /// where the arc leaves each leg, handled so the curve between them is that circle. This
        /// is the one shaping a handle cannot do — pulling a handle can only widen a bend so far
        /// before the nodes themselves are in the way — so it is what <see cref="Round"/> falls
        /// back on. The arc has to fit: it is cut down to whatever the shorter leg allows, and the
        /// radius actually laid is returned.
        /// </summary>
        public float Fillet(int index, float radius)
        {
            if (index <= 0 || index >= Nodes.Count - 1 || radius <= 0f || CellSize <= 0f)
                return float.PositiveInfinity;

            var corner = Nodes[index];
            var before = Nodes[index - 1];
            var after = Nodes[index + 1];
            var into = corner.Position - before.Position;
            var away = after.Position - corner.Position;
            if (into.sqrMagnitude < 1e-8f || away.sqrMagnitude < 1e-8f)
                return float.PositiveInfinity;
            var inLeg = into.magnitude;
            var outLeg = away.magnitude;
            into /= inLeg;
            away /= outLeg;

            // How far the road turns at the corner. A straight run has no corner to cut.
            var turn = Vector2.Angle(into, away) * Mathf.Deg2Rad;
            if (turn < 1e-3f)
                return float.PositiveInfinity;

            // The arc leaves each leg a tangent distance back from the corner, and that distance
            // has to stay short of the neighbouring nodes or the legs would cross each other.
            var cells = radius / CellSize;
            var reach = Mathf.Min(inLeg, outLeg) * 0.45f;
            var tangent = cells * Mathf.Tan(turn * 0.5f);
            if (tangent > reach)
            {
                tangent = reach;
                cells = tangent / Mathf.Tan(turn * 0.5f);
            }

            // A cubic through both ends of an arc, with its handles this long along the tangents,
            // is the standard circle approximation and is within a fraction of a percent here.
            var pull = 4f / 3f * Mathf.Tan(turn * 0.25f) * cells;
            var start = new RoadNode
            {
                Position = corner.Position - into * tangent,
                Height = Mathf.Lerp(before.Height, corner.Height, 1f - tangent / inLeg),
                LockToGround = false,
                GroundOffset = corner.GroundOffset,
                Handle = into * pull,
            };
            var end = new RoadNode
            {
                Position = corner.Position + away * tangent,
                Height = Mathf.Lerp(corner.Height, after.Height, tangent / outLeg),
                LockToGround = false,
                GroundOffset = corner.GroundOffset,
                Handle = away * pull,
            };

            Nodes.RemoveAt(index);
            Nodes.Insert(index, end);
            Nodes.Insert(index, start);
            Version++;
            return RoadSpline.TurnRadius(Nodes, index, CellSize);
        }

        /// <summary>
        /// Rounds the bend at node <paramref name="index"/> to <paramref name="radius"/> metres,
        /// by pulling its handle if that reaches and by cutting a proper arc into the corner if it
        /// does not. Returns the radius laid. The node count changes when it falls back, so a
        /// caller walking the chain walks it backwards.
        /// </summary>
        public float Round(int index, float radius)
        {
            var pulled = SmoothTo(index, radius);
            if (pulled >= radius || index <= 0 || index >= Nodes.Count - 1)
                return pulled;
            var cut = Fillet(index, radius);
            return float.IsInfinity(cut) ? pulled : cut;
        }

        /// <summary>
        /// Rounds every bend in the road to <paramref name="radius"/> metres, cutting arcs into the
        /// corners that will not come round by their handles alone. Backwards along the chain,
        /// because a corner that is cut becomes two nodes and would shift everything after it.
        /// </summary>
        public void RoundAll(float radius)
        {
            SmoothAll(radius);
            for (var i = Nodes.Count - 2; i > 0; i--)
                if (RoadSpline.TurnRadius(Nodes, i - 1, CellSize) < radius
                    || RoadSpline.TurnRadius(Nodes, i, CellSize) < radius)
                    Fillet(i, radius);

            // No sweep afterwards: a cut corner's two nodes already carry the handles that are the
            // arc, and there is nothing left for the smoother to widen.
        }

        /// <summary>
        /// Makes node <paramref name="index"/> a hard corner again: a handle short enough that the
        /// road barely curves through it, which is what undoes a smoothed bend.
        /// </summary>
        public void Corner(int index)
        {
            var direction = RoadSpline.Tangent(Nodes, index);
            if (direction.sqrMagnitude < 1e-8f)
                direction = Vector2.right;
            Nodes[index].Handle = direction.normalized * 0.05f;
            Version++;
        }

        /// <summary>Clears a node's handle, putting its curve back to automatic.</summary>
        public void AutoHandle(int index)
        {
            Nodes[index].Handle = Vector2.zero;
            Version++;
        }

        /// <summary>Each segment's tightest turn in metres, and how the tightest of them is judged.</summary>
        public RoadGradeState Turns(List<float> into)
        {
            if (Nodes.Count < 2)
            {
                into.Clear();
                return RoadGradeState.Fine;
            }

            return RoadPlanner.JudgeTurn(RoadPlanner.Turns(Nodes, CellSize, into), MinTurnRadius);
        }

        public void RemoveLast()
        {
            if (Nodes.Count == 0)
                return;
            Nodes.RemoveAt(Nodes.Count - 1);
            Version++;
        }

        /// <summary>Each segment's steepest grade, and how the worst of them is judged.</summary>
        public RoadGradeState Grades(float cellSize, List<float> into)
        {
            var worst = Nodes.Count >= 2 ? RoadPlanner.Grades(Nodes, cellSize, into) : 0f;
            if (Nodes.Count < 2)
                into.Clear();
            return RoadPlanner.Judge(worst, MaxGrade);
        }

        /// <summary>Whether the road can go into the network: two nodes or more, none over twice the max grade.</summary>
        public bool CanCommit(float cellSize)
        {
            if (Nodes.Count < 2)
                return false;
            var grades = new List<float>();
            return Grades(cellSize, grades) != RoadGradeState.Refused;
        }

        /// <summary>
        /// Puts the draft into the network: a new road, or the edited one redrawn. Returns the
        /// road's id, or 0 if it could not be committed.
        /// </summary>
        public int Commit(RoadNetwork network, float cellSize)
        {
            if (!CanCommit(cellSize))
                return 0;
            int road;
            if (EditingRoad != 0)
            {
                road = network.ReplaceRoad(EditingRoad, Nodes, Width, JoinRadius) ? EditingRoad : 0;
            }
            else
            {
                var points = new List<Vector3>();
                foreach (var node in Nodes)
                    points.Add(new Vector3(node.Position.x, node.Height, node.Position.y));
                road = network.AddRoad(points, Width, JoinRadius);
                // Carry over what AddRoad does not take: handles and the ground locks.
                if (road != 0)
                {
                    var chain = network.Chain(road);
                    for (var i = 0; i < chain.Count && i < Nodes.Count; i++)
                    {
                        if (Nodes[i].Id != 0 && Nodes[i].Id == chain[i].Id)
                            continue;
                        chain[i].Handle = Nodes[i].Handle;
                        chain[i].LockToGround = Nodes[i].LockToGround;
                        chain[i].GroundOffset = Nodes[i].GroundOffset;
                    }
                }
            }

            if (road != 0)
                Clear();
            return road;
        }

        static RoadNode Copy(RoadNode node) => new RoadNode
        {
            Id = node.Id,
            Position = node.Position,
            Height = node.Height,
            LockToGround = node.LockToGround,
            GroundOffset = node.GroundOffset,
            Handle = node.Handle,
        };
    }
}
