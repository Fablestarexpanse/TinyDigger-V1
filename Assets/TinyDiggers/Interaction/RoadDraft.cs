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
                // With a grade held, the node leaves the ground and keeps the ramp's slope
                // instead, climbing or falling whichever way the ground goes.
                if (GradeLock.HasValue && Nodes.Count > 0)
                {
                    var last = Nodes[Nodes.Count - 1];
                    var run = Vector2.Distance(last.Position, at) * CellSize;
                    var rise = GradeLock.Value * run;
                    node.Height = last.Height + (groundAt(at) < last.Height ? -rise : rise);
                    node.LockToGround = false;
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
                if (Mathf.Abs(height - Nodes[i].Height) > 1e-4f)
                    moved++;
                Nodes[i].Height = height;
                Nodes[i].LockToGround = false;
            }

            Version++;
            return moved;
        }

        /// <summary>
        /// Rounds the bend at node <paramref name="index"/> out towards <paramref name="radius"/>
        /// metres by pulling its handle along the way the road already runs. The turn cannot always
        /// be had — two nodes close together cannot round a wide arc — so it returns the radius it
        /// actually reached, which is never tighter than the one it started from.
        /// </summary>
        public float SmoothTo(int index, float radius)
        {
            if (index <= 0 || index >= Nodes.Count - 1)
                return float.PositiveInfinity;

            var node = Nodes[index];
            var was = node.Handle;
            var direction = RoadSpline.Tangent(Nodes, index);
            if (node.HasHandle)
                direction = node.Handle;
            if (direction.sqrMagnitude < 1e-8f)
                direction = Nodes[index + 1].Position - Nodes[index - 1].Position;
            if (direction.sqrMagnitude < 1e-8f)
                return float.PositiveInfinity;
            direction = direction.normalized;

            // A longer handle sweeps the bend wider, up to about the shorter of the two chords;
            // past that the curve starts to overshoot and tightens again, so that is the ceiling.
            var reach = Mathf.Min(
                Vector2.Distance(node.Position, Nodes[index - 1].Position),
                Vector2.Distance(node.Position, Nodes[index + 1].Position));
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

            float At(Vector2 handle)
            {
                Nodes[index].Handle = handle;
                return Mathf.Min(
                    RoadSpline.TurnRadius(Nodes, index - 1, CellSize),
                    RoadSpline.TurnRadius(Nodes, index, CellSize));
            }
        }

        /// <summary>Rounds every bend in the road out towards <paramref name="radius"/> metres.</summary>
        public void SmoothAll(float radius)
        {
            for (var i = 1; i + 1 < Nodes.Count; i++)
                SmoothTo(i, radius);
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
