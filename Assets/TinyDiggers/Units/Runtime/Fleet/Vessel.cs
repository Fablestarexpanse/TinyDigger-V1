using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    public enum VesselState
    {
        /// <summary>At anchor on open water.</summary>
        Moored,

        Sailing,
    }

    /// <summary>
    /// One of the forge's working craft afloat (Ronan, 2026-09-25, slice A): sent across the water
    /// to anchor on open water, turning before it goes like the landing craft, over water deep and
    /// wide enough for its own hull only. No beaching and no ramp; the dredging work comes in later
    /// slices. Plain C#: a host draws it.
    /// </summary>
    public sealed class Vessel
    {
        /// <summary>Cells round an ordered spot searched for water the hull floats on.</summary>
        public const int SearchCells = 6;

        readonly TerrainGrid _grid;
        readonly List<Vector2Int> _route = new List<Vector2Int>();
        readonly List<Vector2Int> _routeScratch = new List<Vector2Int>();
        int _routeIndex;
        Vector2 _anchor;

        public Vessel(VesselKind kind, TerrainGrid grid, Vector2 position, float heading, WaterNav nav = null)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Kind = kind;
            Spec = VesselSpecs.For(kind);
            Nav = nav ?? new WaterNav(grid, Spec.Draft, Spec.HalfBeam);
            Position = position;
            Heading = heading;
            Status = "Moored";
        }

        public VesselKind Kind { get; }
        public VesselSpec Spec { get; }

        /// <summary>Where this hull floats. Refresh it when the water or the ground changes.</summary>
        public WaterNav Nav { get; }

        /// <summary>Hull centre, in cells (x, z).</summary>
        public Vector2 Position { get; private set; }

        /// <summary>Degrees from +z toward +x.</summary>
        public float Heading { get; private set; }

        public VesselState State { get; private set; }
        public string Status { get; private set; }
        public IReadOnlyList<Vector2Int> Route => _route;

        public bool UnderWay => State == VesselState.Sailing;

        /// <summary>
        /// Sends it to anchor on the water nearest <paramref name="water"/> that floats it. False,
        /// and nothing about its course changes, when no water near there floats this hull or
        /// the water does not join; <paramref name="why"/> says which.
        /// </summary>
        public bool SailTo(Vector2Int water, out string why)
        {
            var to = NearestFloating(water, SearchCells);
            if (to.x < 0)
            {
                why = $"too shallow or too narrow there for the {Spec.Name}";
                return false;
            }

            var from = NearestFloating(Cell(Position), SearchCells);
            // Found into a scratch list, so a refusal leaves the course it has (the landing
            // craft's lesson, 2026-09-25).
            if (from.x < 0 || !Nav.TryFindRoute(from, to, _routeScratch))
            {
                why = "no way over the water from here";
                return false;
            }

            _route.Clear();
            _route.AddRange(_routeScratch);
            _routeIndex = 0;
            _anchor = new Vector2(to.x + 0.5f, to.y + 0.5f);
            State = VesselState.Sailing;
            Status = "Sailing";
            why = null;
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (State != VesselState.Sailing)
                return;
            var cells = Spec.Speed / CellSize * deltaTime;
            while (_routeIndex < _route.Count && cells > 0f)
            {
                var at = _route[_routeIndex];
                var before = Position;
                if (!MoveTowards(new Vector2(at.x + 0.5f, at.y + 0.5f), cells, deltaTime))
                    break;
                cells -= Vector2.Distance(before, Position);
                _routeIndex++;
            }

            if (_routeIndex >= _route.Count && MoveTowards(_anchor, cells, deltaTime))
            {
                _route.Clear();
                State = VesselState.Moored;
                Status = "Moored";
            }
        }

        /// <summary>
        /// Steers for <paramref name="target"/> and moves up to <paramref name="cells"/>, slowing
        /// while it is well off its course, so it swings like a boat rather than sliding sideways.
        /// True on arrival.
        /// </summary>
        bool MoveTowards(Vector2 target, float cells, float deltaTime)
        {
            var offset = target - Position;
            var distance = offset.magnitude;
            if (distance <= 1e-3f)
            {
                Position = target;
                return true;
            }

            var along = offset / distance;
            var wanted = (float)(Math.Atan2(along.x, along.y) * 180.0 / Math.PI);
            Heading = Mathf.MoveTowardsAngle(Heading, wanted, Spec.TurnRate * deltaTime);
            var off = Mathf.Abs(Mathf.DeltaAngle(Heading, wanted));
            cells *= Mathf.Clamp01(Mathf.Cos(off * Mathf.Deg2Rad));
            if (distance <= cells)
            {
                Position = target;
                return true;
            }

            Position += along * cells;
            return false;
        }

        Vector2Int NearestFloating(Vector2Int around, int radius)
        {
            for (var ring = 0; ring <= radius; ring++)
                for (var dz = -ring; dz <= ring; dz++)
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                            continue;
                        if (Nav.Floats(around.x + dx, around.y + dz))
                            return new Vector2Int(around.x + dx, around.y + dz);
                    }

            return new Vector2Int(-1, -1);
        }

        float CellSize => Math.Max(0.01f, _grid.CellSize);

        static Vector2Int Cell(Vector2 at) => new Vector2Int(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));
    }
}
