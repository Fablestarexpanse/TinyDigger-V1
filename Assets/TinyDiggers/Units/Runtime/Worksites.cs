using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// A place the player has set the crew to work (2026-09-24), after Captain of Industry's mining
    /// tower: a building put down at the site, and a work area round it. Vehicles are assigned to
    /// it, and an assigned vehicle only takes work inside the area.
    /// </summary>
    public sealed class Worksite
    {
        public int Id;

        /// <summary>The cell the building stands on.</summary>
        public Vector2Int Building;

        /// <summary>The work area, in cells: x, y are the south-west cell; width and height in cells.</summary>
        public RectInt Area;

        public string Name => $"Worksite {Id}";

        public bool Contains(int x, int z) =>
            x >= Area.x && z >= Area.y && x < Area.x + Area.width && z < Area.y + Area.height;
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
        /// <summary>The longest side a work area may have, in cells, as a mining tower has a reach.</summary>
        public int MaxSide = 160;

        /// <summary>The shortest side, in cells: an area has to hold at least the building's own ground.</summary>
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

        /// <summary>Puts a worksite's building down at <paramref name="building"/> with the work area given, clamped to size.</summary>
        public Worksite Add(Vector2Int building, RectInt area)
        {
            var site = new Worksite { Id = _nextId++, Building = building, Area = Clamp(area) };
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
            site.Area = Clamp(area);
            Changed?.Invoke(id);
            return true;
        }

        /// <summary>Moves the building, and the area with it.</summary>
        public bool Move(int id, Vector2Int building)
        {
            var site = Get(id);
            if (site == null)
                return false;
            var by = building - site.Building;
            site.Building = building;
            site.Area = new RectInt(site.Area.x + by.x, site.Area.y + by.y, site.Area.width, site.Area.height);
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
                if (site.Contains(x, z) && (best == null || site.Area.width * site.Area.height < best.Area.width * best.Area.height))
                    best = site;
            return best;
        }

        RectInt Clamp(RectInt area)
        {
            var width = Mathf.Clamp(Mathf.Abs(area.width), MinSide, Math.Max(MinSide, MaxSide));
            var height = Mathf.Clamp(Mathf.Abs(area.height), MinSide, Math.Max(MinSide, MaxSide));
            var x = area.width < 0 ? area.x + area.width : area.x;
            var z = area.height < 0 ? area.y + area.height : area.y;
            return new RectInt(x, z, width, height);
        }
    }
}
