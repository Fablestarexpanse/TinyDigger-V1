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
        public bool Snap45 = true;

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
                node = new RoadNode { Position = at, Height = groundAt(at), LockToGround = true };
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
                node.Height = groundAt(at);
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
                node.Height = groundAt(node.Position);
            Version++;
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
            Handle = node.Handle,
        };
    }
}
