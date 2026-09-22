using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// A node of the road network (Slice 17 Part B): a point roads pass through or end at, in grid
    /// cells (x, z continuous), with the height the road passes it at.
    /// </summary>
    [Serializable]
    public sealed class RoadNode
    {
        public int Id;

        /// <summary>Where the node is, in cells: x and z, continuous (cell centres at .5).</summary>
        public Vector2 Position;

        /// <summary>Metres: the height the road passes through the node at.</summary>
        public float Height;

        /// <summary>Whether the height follows the ground under the node when the node is moved.</summary>
        public bool LockToGround = true;

        /// <summary>
        /// A dragged tangent handle, in cells from the node: the direction and pull of the curve
        /// through it. Zero means automatic (Catmull-Rom from the neighbouring nodes).
        /// </summary>
        public Vector2 Handle;

        public bool HasHandle => Handle.sqrMagnitude > 1e-6f;
    }

    /// <summary>A stretch of road between two nodes, part of one road the player drew.</summary>
    [Serializable]
    public sealed class RoadSegment
    {
        public int Id;

        /// <summary>The road this segment was drawn as part of: selecting a road selects all of them.</summary>
        public int Road;

        public int From;
        public int To;

        /// <summary>Cells across: 3, 5 or 7.</summary>
        public int Width = 3;
    }

    /// <summary>
    /// Every road on the island (Slice 17 Part B), as the player drew them: nodes and the segments
    /// between them. Plain C# and serialisable (JsonUtility). A road is the chain of segments
    /// drawn in one go; roads meet by sharing a node, which is how a junction is made.
    ///
    /// The network is the plan. <see cref="RoadPlanner"/> turns a road into the designations the
    /// crew works to, and cells become road surface once they are built.
    /// </summary>
    [Serializable]
    public sealed class RoadNetwork
    {
        public List<RoadNode> Nodes = new List<RoadNode>();
        public List<RoadSegment> Segments = new List<RoadSegment>();
        public int NextId = 1;

        /// <summary>Raised whenever a road is added, removed or reshaped, with the road's id.</summary>
        [field: NonSerialized]
        public event Action<int> Changed;

        public RoadNode Node(int id) => Nodes.Find(n => n.Id == id);

        /// <summary>The node within <paramref name="radius"/> cells of a point, nearest first, or null.</summary>
        public RoadNode NodeNear(Vector2 position, float radius)
        {
            RoadNode best = null;
            var bestDistance = radius;
            foreach (var node in Nodes)
            {
                var distance = Vector2.Distance(node.Position, position);
                if (distance > bestDistance)
                    continue;
                bestDistance = distance;
                best = node;
            }

            return best;
        }

        /// <summary>
        /// Adds a road through the points (cells, with heights), <paramref name="width"/> cells
        /// wide. Its ends join any node already within <paramref name="snap"/> cells, so drawing a
        /// road from the end of another continues it and drawing one onto another's node makes a
        /// junction; the joined node keeps its own position and height. Returns the road's id, or
        /// 0 if there were fewer than two distinct points.
        /// </summary>
        public int AddRoad(IReadOnlyList<Vector3> points, int width, float snap = 1.5f)
        {
            if (points == null || points.Count < 2)
                return 0;

            var road = NextId++;
            var chain = new List<int>();
            for (var i = 0; i < points.Count; i++)
            {
                var at = new Vector2(points[i].x, points[i].z);
                RoadNode node = null;
                if (i == 0 || i == points.Count - 1)
                    node = NodeNear(at, snap);
                if (node == null)
                {
                    node = new RoadNode { Id = NextId++, Position = at, Height = points[i].y };
                    Nodes.Add(node);
                }

                if (chain.Count == 0 || chain[chain.Count - 1] != node.Id)
                    chain.Add(node.Id);
            }

            if (chain.Count < 2)
            {
                RemoveOrphans();
                return 0;
            }

            for (var i = 0; i + 1 < chain.Count; i++)
                Segments.Add(new RoadSegment { Id = NextId++, Road = road, From = chain[i], To = chain[i + 1], Width = width });
            Changed?.Invoke(road);
            return road;
        }

        /// <summary>The road's nodes in order, first to last.</summary>
        public List<RoadNode> Chain(int road)
        {
            var chain = new List<RoadNode>();
            foreach (var segment in Segments)
            {
                if (segment.Road != road)
                    continue;
                if (chain.Count == 0)
                    chain.Add(Node(segment.From));
                chain.Add(Node(segment.To));
            }

            return chain;
        }

        /// <summary>The ids of every road, in the order they were drawn.</summary>
        public List<int> Roads()
        {
            var roads = new List<int>();
            foreach (var segment in Segments)
                if (!roads.Contains(segment.Road))
                    roads.Add(segment.Road);
            return roads;
        }

        public int WidthOf(int road)
        {
            foreach (var segment in Segments)
                if (segment.Road == road)
                    return segment.Width;
            return 0;
        }

        /// <summary>How many segments meet at the node: 1 at an end, 2 along a road, 3 or more at a junction.</summary>
        public int Degree(int node)
        {
            var degree = 0;
            foreach (var segment in Segments)
                if (segment.From == node || segment.To == node)
                    degree++;
            return degree;
        }

        /// <summary>Moves a node; the roads through it are reshaped.</summary>
        public void MoveNode(int id, Vector2 position, float? height = null)
        {
            var node = Node(id);
            if (node == null)
                return;
            node.Position = position;
            if (height.HasValue)
                node.Height = height.Value;
            RaiseFor(id);
        }

        public void SetHeight(int id, float height)
        {
            var node = Node(id);
            if (node == null)
                return;
            node.Height = height;
            node.LockToGround = false;
            RaiseFor(id);
        }

        public void SetHandle(int id, Vector2 handle)
        {
            var node = Node(id);
            if (node == null)
                return;
            node.Handle = handle;
            RaiseFor(id);
        }

        /// <summary>Takes a road out of the network, and any node no other road still uses.</summary>
        public bool RemoveRoad(int road)
        {
            var removed = Segments.RemoveAll(s => s.Road == road);
            if (removed == 0)
                return false;
            RemoveOrphans();
            Changed?.Invoke(road);
            return true;
        }

        void RemoveOrphans() => Nodes.RemoveAll(n => Degree(n.Id) == 0);

        void RaiseFor(int node)
        {
            var raised = new HashSet<int>();
            foreach (var segment in Segments)
                if ((segment.From == node || segment.To == node) && raised.Add(segment.Road))
                    Changed?.Invoke(segment.Road);
        }

        public string ToJson() => JsonUtility.ToJson(this);

        public static RoadNetwork FromJson(string json) => JsonUtility.FromJson<RoadNetwork>(json) ?? new RoadNetwork();
    }
}
