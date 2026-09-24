using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// A place the player has set the crew to work (2026-09-24), after Captain of Industry's mining
    /// tower: a building put down at the site, and a work area round it. Vehicles are assigned to
    /// it, and an assigned vehicle only takes work inside the area.
    ///
    /// The area is a closed outline through <see cref="Nodes"/> — a spline through them when
    /// <see cref="Curved"/>, straight edges between them when not — so it can be any shape and any
    /// size the job needs (Ronan, the same day: "you should be able to set work area with splines to
    /// get any size shape needed"). A rectangle is four straight nodes. The cells whose centres lie
    /// inside are worked out whenever the outline changes, so asking about a cell is one lookup.
    /// </summary>
    public sealed class Worksite
    {
        public int Id;

        /// <summary>The cell the building stands on.</summary>
        public Vector2Int Building;

        /// <summary>The outline's nodes in cells, going round; the last joins the first.</summary>
        public readonly List<Vector2> Nodes = new List<Vector2>();

        /// <summary>A spline through the nodes (true) or straight edges between them.</summary>
        public bool Curved;

        /// <summary>The outline as traced, a closed polygon in cells: what is drawn and what is filled.</summary>
        public readonly List<Vector2> Outline = new List<Vector2>();

        /// <summary>The smallest rectangle of cells holding every cell of the area.</summary>
        public RectInt Area { get; private set; }

        /// <summary>How many cells the area covers.</summary>
        public int CellCount => _cells.Count;

        readonly HashSet<long> _cells = new HashSet<long>();

        public string Name => $"Worksite {Id}";

        public bool Contains(int x, int z) => _cells.Contains(Key(x, z));

        static long Key(int x, int z) => ((long)z << 32) | (uint)x;

        /// <summary>Traces the outline and fills it: every cell whose centre is inside, by even-odd.</summary>
        internal void Rebuild()
        {
            Outline.Clear();
            _cells.Clear();
            if (Nodes.Count < 3)
            {
                Area = default;
                return;
            }

            if (Curved)
            {
                var loop = new List<RoadNode>(Nodes.Count);
                foreach (var node in Nodes)
                    loop.Add(new RoadNode { Position = node, LockToGround = false });
                var samples = new List<RoadSample>();
                LandformSpline.SampleLoop(loop, 0.25f, samples);
                LandformSpline.Outline(samples, Outline);
            }
            else
            {
                Outline.AddRange(Nodes);
            }

            var minX = float.MaxValue;
            var minZ = float.MaxValue;
            var maxX = float.MinValue;
            var maxZ = float.MinValue;
            foreach (var point in Outline)
            {
                minX = Math.Min(minX, point.x);
                minZ = Math.Min(minZ, point.y);
                maxX = Math.Max(maxX, point.x);
                maxZ = Math.Max(maxZ, point.y);
            }

            var crossings = new List<float>();
            int lowX = int.MaxValue, lowZ = int.MaxValue, highX = int.MinValue, highZ = int.MinValue;
            for (var z = (int)Math.Floor(minZ); z <= (int)Math.Ceiling(maxZ); z++)
            {
                // Where a line through this row's cell centres crosses the outline.
                var row = z + 0.5f;
                crossings.Clear();
                for (var i = 0; i < Outline.Count; i++)
                {
                    var a = Outline[i];
                    var b = Outline[(i + 1) % Outline.Count];
                    if (a.y <= row == b.y <= row)
                        continue;
                    crossings.Add(a.x + (row - a.y) / (b.y - a.y) * (b.x - a.x));
                }

                crossings.Sort();
                for (var i = 0; i + 1 < crossings.Count; i += 2)
                {
                    // Cells whose centre x + 0.5 lies between this pair of crossings.
                    var first = (int)Math.Ceiling(crossings[i] - 0.5f);
                    var last = (int)Math.Ceiling(crossings[i + 1] - 0.5f) - 1;
                    for (var x = first; x <= last; x++)
                    {
                        _cells.Add(Key(x, z));
                        lowX = Math.Min(lowX, x);
                        highX = Math.Max(highX, x);
                        lowZ = Math.Min(lowZ, z);
                        highZ = Math.Max(highZ, z);
                    }
                }
            }

            Area = _cells.Count == 0 ? default : new RectInt(lowX, lowZ, highX - lowX + 1, highZ - lowZ + 1);
        }
    }

    /// <summary>
    /// Every worksite on the island (2026-09-24). Plain C#, held by the <see cref="JobDispatcher"/>,
    /// so the crew logic asks it directly whether a cell is in a unit's area.
    ///
    /// Ronan: *"a building that you put at your worksite and then you assign vehicles to it; that
    /// building can designate a large area to represent the work area, instead of them just trying
    /// to find anything on the map to do."*
    /// </summary>
    public sealed class Worksites
    {
        /// <summary>
        /// The shortest side of a rectangular area, in cells. There is no longest: an area is
        /// whatever shape and size the job needs.
        /// </summary>
        public const int MinSide = 4;

        readonly List<Worksite> _sites = new List<Worksite>();
        int _nextId = 1;

        /// <summary>Raised with a worksite's id whenever one is added, moved, resized or removed.</summary>
        public event Action<int> Changed;

        /// <summary>Raised with a worksite's id when it is removed, so its vehicles can be let go.</summary>
        public event Action<int> Removed;

        public IReadOnlyList<Worksite> All => _sites;

        public int Count => _sites.Count;

        public Worksite Get(int id)
        {
            foreach (var site in _sites)
                if (site.Id == id)
                    return site;
            return null;
        }

        /// <summary>Puts a worksite's building down at <paramref name="building"/> with a rectangular work area.</summary>
        public Worksite Add(Vector2Int building, RectInt area)
        {
            var site = new Worksite { Id = _nextId++, Building = building };
            SetRectangle(site, Clamp(area));
            _sites.Add(site);
            Changed?.Invoke(site.Id);
            return site;
        }

        /// <summary>
        /// Puts a worksite's building down with the work area the outline <paramref name="nodes"/>
        /// draws (cells, going round, the last joining the first): a spline through them if
        /// <paramref name="curved"/>, straight edges if not. Null if the outline has fewer than three
        /// nodes or holds no cell.
        /// </summary>
        public Worksite Add(Vector2Int building, IReadOnlyList<Vector2> nodes, bool curved)
        {
            var site = new Worksite { Id = _nextId, Building = building, Curved = curved };
            site.Nodes.AddRange(nodes);
            site.Rebuild();
            if (site.CellCount == 0)
                return null;
            _nextId++;
            _sites.Add(site);
            Changed?.Invoke(site.Id);
            return site;
        }

        /// <summary>A worksite with a square area <paramref name="side"/> cells across, centred on the building.</summary>
        public Worksite Add(Vector2Int building, int side) =>
            Add(building, new RectInt(building.x - side / 2, building.y - side / 2, side, side));

        public bool SetArea(int id, RectInt area)
        {
            var site = Get(id);
            if (site == null)
                return false;
            site.Curved = false;
            SetRectangle(site, Clamp(area));
            Changed?.Invoke(id);
            return true;
        }

        /// <summary>
        /// Redraws a worksite's area through new outline nodes. False, and nothing changes, if the
        /// outline would have fewer than three nodes or hold no cell.
        /// </summary>
        public bool SetOutline(int id, IReadOnlyList<Vector2> nodes, bool curved)
        {
            var site = Get(id);
            if (site == null || nodes == null || nodes.Count < 3)
                return false;
            var was = new List<Vector2>(site.Nodes);
            var wasCurved = site.Curved;
            site.Nodes.Clear();
            site.Nodes.AddRange(nodes);
            site.Curved = curved;
            site.Rebuild();
            if (site.CellCount == 0)
            {
                site.Nodes.Clear();
                site.Nodes.AddRange(was);
                site.Curved = wasCurved;
                site.Rebuild();
                return false;
            }

            Changed?.Invoke(id);
            return true;
        }

        static void SetRectangle(Worksite site, RectInt area)
        {
            site.Nodes.Clear();
            site.Nodes.Add(new Vector2(area.x, area.y));
            site.Nodes.Add(new Vector2(area.x + area.width, area.y));
            site.Nodes.Add(new Vector2(area.x + area.width, area.y + area.height));
            site.Nodes.Add(new Vector2(area.x, area.y + area.height));
            site.Rebuild();
        }

        /// <summary>Moves the building, and the area with it.</summary>
        public bool Move(int id, Vector2Int building)
        {
            var site = Get(id);
            if (site == null)
                return false;
            var by = building - site.Building;
            site.Building = building;
            for (var i = 0; i < site.Nodes.Count; i++)
                site.Nodes[i] += new Vector2(by.x, by.y);
            site.Rebuild();
            Changed?.Invoke(id);
            return true;
        }

        /// <summary>
        /// Puts a worksite's building on <paramref name="building"/>, or on the cell of its area
        /// nearest that when it is outside the area, without moving the area.
        /// </summary>
        public bool PlaceBuilding(int id, Vector2Int building)
        {
            var site = Get(id);
            if (site == null || site.CellCount == 0)
                return false;
            if (!site.Contains(building.x, building.y))
            {
                var best = building;
                var bestDistance = long.MaxValue;
                var area = site.Area;
                for (var z = area.y; z < area.y + area.height; z++)
                {
                    for (var x = area.x; x < area.x + area.width; x++)
                    {
                        if (!site.Contains(x, z))
                            continue;
                        long dx = x - building.x;
                        long dz = z - building.y;
                        if (dx * dx + dz * dz >= bestDistance)
                            continue;
                        bestDistance = dx * dx + dz * dz;
                        best = new Vector2Int(x, z);
                    }
                }

                building = best;
            }

            site.Building = building;
            Changed?.Invoke(id);
            return true;
        }

        public bool Remove(int id)
        {
            var site = Get(id);
            if (site == null)
                return false;
            _sites.Remove(site);
            Removed?.Invoke(id);
            Changed?.Invoke(id);
            return true;
        }

        public void Clear()
        {
            var ids = new List<int>();
            foreach (var site in _sites)
                ids.Add(site.Id);
            foreach (var id in ids)
                Remove(id);
        }

        /// <summary>Whether cell (x, z) is inside worksite <paramref name="id"/>'s work area.</summary>
        public bool Contains(int id, int x, int z)
        {
            var site = Get(id);
            return site != null && site.Contains(x, z);
        }

        /// <summary>The worksite whose building is within <paramref name="radius"/> cells of a point, nearest, or null.</summary>
        public Worksite BuildingNear(Vector2 at, float radius)
        {
            Worksite best = null;
            var bestDistance = radius;
            foreach (var site in _sites)
            {
                var distance = Vector2.Distance(at, new Vector2(site.Building.x + 0.5f, site.Building.y + 0.5f));
                if (distance > bestDistance)
                    continue;
                bestDistance = distance;
                best = site;
            }

            return best;
        }

        /// <summary>
        /// The worksite whose area covers cell (x, z), or null. Where areas overlap, the smallest —
        /// the one drawn round this particular job — wins.
        /// </summary>
        public Worksite At(int x, int z)
        {
            Worksite best = null;
            foreach (var site in _sites)
                if (site.Contains(x, z) && (best == null || site.CellCount < best.CellCount))
                    best = site;
            return best;
        }

        static RectInt Clamp(RectInt area)
        {
            var width = Math.Max(Mathf.Abs(area.width), MinSide);
            var height = Math.Max(Mathf.Abs(area.height), MinSide);
            var x = area.width < 0 ? area.x + area.width : area.x;
            var z = area.height < 0 ? area.y + area.height : area.y;
            return new RectInt(x, z, width, height);
        }
    }
}
