using System.Collections.Generic;
using TinyDiggers.Presentation;
using PromptWaffle.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The forge's working craft on the map (Ronan, 2026-09-25, slice A: afloat first): a dredge,
    /// a gold dredge, a scow and a tug moored on open water near the landing craft, each clicked to
    /// select and sent by right-clicking water. Each craft is a <see cref="Vessel"/>, plain C#; this
    /// draws it. It sets the hull's place and heading; the model's WaterFloater puts it on the
    /// water as drawn and its WaterFoamEmitter makes the foam round it.
    /// </summary>
    public sealed class FleetHost : MonoBehaviour
    {
        /// <summary>The order they are moored in: the big ones first, while there is room.</summary>
        static readonly VesselKind[] Kinds = { VesselKind.GoldDredge, VesselKind.Dredge, VesselKind.Scow, VesselKind.Tug };

        /// <summary>Seconds into the game before they are put on the water: the water settles first.</summary>
        const float WaterSettleSeconds = 10f;

        /// <summary>How long to wait for the landing craft to moor, to moor near it, before mooring near the crew.</summary>
        const float WaitForFerrySeconds = 25f;

        /// <summary>How far out to look for open water, cells.</summary>
        const int SearchCells = 700;

        /// <summary>Floating cells a mooring has to be joined to: open water, not a pool.</summary>
        const int OpenWaterCells = 2000;

        /// <summary>Metres kept clear between hulls when they are moored.</summary>
        const float MooringGap = 4f;

        sealed class Craft
        {
            public Vessel Vessel;
            public Transform Body;
        }

        readonly List<Craft> _fleet = new List<Craft>();
        PlayerTools _tools;
        TerrainView _terrain;
        FleetCatalog _catalog;
        LineRenderer _routeLine;
        LineRenderer _selectedOutline;
        Craft _selected;
        bool _launched;

        /// <summary>The craft on the water, in the order they were moored.</summary>
        public IEnumerable<Vessel> Vessels
        {
            get
            {
                foreach (var craft in _fleet)
                    yield return craft.Vessel;
            }
        }

        /// <summary>The selected craft, or null.</summary>
        public Vessel Selected => _selected?.Vessel;

        public void Init(PlayerTools tools, TerrainView terrain)
        {
            _tools = tools;
            _terrain = terrain;
            _catalog = Resources.Load<FleetCatalog>(FleetCatalog.ResourcePath);
            if (_catalog == null)
                Debug.LogWarning("FleetHost: no Resources/FleetCatalog; run TinyDiggers/Build Forge Machines");
            var material = new Material(Shader.Find("Sprites/Default"));
            _routeLine = Line("Fleet Route", material, new Color(0.4f, 0.85f, 1f, 0.8f), 0.15f, loop: false);
            _selectedOutline = Line("Fleet Selected", material, new Color(1f, 0.95f, 0.5f, 0.9f), 0.12f, loop: true);
        }

        LineRenderer Line(string name, Material material, Color colour, float width, bool loop)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.widthMultiplier = width;
            line.startColor = line.endColor = colour;
            line.loop = loop;
            line.positionCount = 0;
            return line;
        }

        void Update()
        {
            if (_terrain == null || _terrain.Grid == null || _catalog == null)
                return;
            if (!_launched)
            {
                var ferryReady = _tools.Ferries != null && _tools.Ferries.Craft != null;
                if (Time.timeSinceLevelLoad >= WaterSettleSeconds && (ferryReady || Time.timeSinceLevelLoad >= WaitForFerrySeconds))
                {
                    _launched = true;
                    Launch();
                }

                return;
            }

            var dt = Mathf.Min(Time.deltaTime, 0.1f);
            foreach (var craft in _fleet)
            {
                craft.Vessel.Tick(dt);
                var position = World(craft.Vessel.Position, craft.Body.position.y);
                // Across the water and round: ours. Up and down, pitch and roll: the floater's.
                craft.Body.SetPositionAndRotation(position, Quaternion.Euler(0f, craft.Vessel.Heading, 0f));
            }

            DrawSelection();
        }

        /// <summary>Moors each craft on open water near the landing craft, or near the crew if there is none.</summary>
        void Launch()
        {
            var grid = _terrain.Grid;
            var ferry = _tools.Ferries != null ? _tools.Ferries.Craft : null;
            var crew = FindAnyObjectByType<CrewView>();
            var from = ferry != null ? new Vector2Int(Mathf.FloorToInt(ferry.Position.x), Mathf.FloorToInt(ferry.Position.y))
                : crew != null && crew.Units.Count > 0 ? crew.Units[0].Cell
                : new Vector2Int(grid.Width / 2, grid.Height / 2);
            // The landing craft counts as a hull to keep clear of.
            var taken = new List<(Vector2 at, float halfLength)>();
            if (ferry != null)
                taken.Add((ferry.Position, Ferry.HalfLength));

            foreach (var kind in Kinds)
            {
                var prefab = _catalog.For(kind);
                if (prefab == null)
                    continue;
                var spec = VesselSpecs.For(kind);
                var nav = new WaterNav(grid, spec.Draft, spec.HalfBeam);
                nav.Refresh();
                if (!TryFindMooring(nav, from, spec, taken, out var cell))
                {
                    Debug.LogWarning($"FleetHost: no open water for the {spec.Name} near ({from.x}, {from.y})");
                    continue;
                }

                var at = new Vector2(cell.x + 0.5f, cell.y + 0.5f);
                taken.Add((at, spec.HalfLength));
                var vessel = new Vessel(kind, grid, at, 0f, nav);
                _fleet.Add(new Craft { Vessel = vessel, Body = Place(prefab, vessel) });
                Debug.Log($"FleetHost: {spec.Name} moored at ({cell.x}, {cell.y})");
            }
        }

        bool TryFindMooring(WaterNav nav, Vector2Int from, VesselSpec spec, List<(Vector2 at, float halfLength)> taken, out Vector2Int found)
        {
            var cellSize = _terrain.Grid.CellSize;
            for (var ring = 8; ring <= SearchCells; ring += 4)
                for (var side = 0; side < 4 * ring; side += 4)
                {
                    var t = side / (float)(4 * ring) * Mathf.PI * 2f;
                    var cell = from + new Vector2Int(Mathf.RoundToInt(Mathf.Cos(t) * ring), Mathf.RoundToInt(Mathf.Sin(t) * ring));
                    if (!nav.Floats(cell.x, cell.y))
                        continue;
                    var clear = true;
                    foreach (var (at, halfLength) in taken)
                        if (Vector2.Distance(at, cell) * cellSize < halfLength + spec.HalfLength + MooringGap)
                        {
                            clear = false;
                            break;
                        }

                    if (clear && FloatsAlong(nav, cell, spec.HalfLength / cellSize) && nav.OpenWater(cell, OpenWaterCells) >= OpenWaterCells)
                    {
                        found = cell;
                        return true;
                    }
                }

            found = default;
            return false;
        }

        /// <summary>
        /// Whether the whole length of a hull moored bow north floats, not just its middle: the
        /// nav's reach is half the beam, and the 13.6 m scow moored with its stern on the sand.
        /// </summary>
        static bool FloatsAlong(WaterNav nav, Vector2Int cell, float halfLengthCells)
        {
            var steps = Mathf.CeilToInt(halfLengthCells / 2f);
            for (var i = -steps; i <= steps; i++)
            {
                var z = cell.y + Mathf.RoundToInt(halfLengthCells * i / Mathf.Max(1, steps));
                if (!nav.Floats(cell.x, z))
                    return false;
            }

            return true;
        }

        Transform Place(GameObject prefab, Vessel vessel)
        {
            var body = Instantiate(prefab, transform, false);
            body.name = char.ToUpperInvariant(vessel.Spec.Name[0]) + vessel.Spec.Name.Substring(1);
            body.hideFlags = HideFlags.DontSave;
            body.transform.SetPositionAndRotation(World(vessel.Position, WaterLevel(vessel.Position)), Quaternion.identity);

            // Something to click: a box round the hull, measured square to it before it turns.
            var renderers = body.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            var box = body.AddComponent<BoxCollider>();
            box.center = body.transform.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
            body.transform.rotation = Quaternion.Euler(0f, vessel.Heading, 0f);
            return body.transform;
        }

        void DrawSelection()
        {
            if (_selected == null)
            {
                _selectedOutline.positionCount = 0;
                _routeLine.positionCount = 0;
                return;
            }

            var vessel = _selected.Vessel;
            var cell = _terrain.Grid.CellSize;
            var radians = vessel.Heading * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
            var side = new Vector2(dir.y, -dir.x);
            var along = dir * (vessel.Spec.HalfLength / cell);
            var across = side * (vessel.Spec.HalfBeam / cell);
            var y = WaterLevel(vessel.Position) + 0.35f;
            _selectedOutline.positionCount = 4;
            _selectedOutline.SetPosition(0, World(vessel.Position + along + across, y));
            _selectedOutline.SetPosition(1, World(vessel.Position + along - across, y));
            _selectedOutline.SetPosition(2, World(vessel.Position - along - across, y));
            _selectedOutline.SetPosition(3, World(vessel.Position - along + across, y));

            var route = vessel.Route;
            _routeLine.positionCount = route.Count;
            for (var i = 0; i < route.Count; i++)
            {
                var at = new Vector2(route[i].x + 0.5f, route[i].y + 0.5f);
                _routeLine.SetPosition(i, World(at, WaterLevel(at) + 0.3f));
            }
        }

        /// <summary>Selects the craft the ray hits, if any; true when it hit one.</summary>
        public bool TrySelectAt(Ray ray)
        {
            _selected = null;
            if (!Physics.Raycast(ray, out var hit, 10000f))
                return false;
            foreach (var craft in _fleet)
                if (hit.transform == craft.Body)
                {
                    _selected = craft;
                    // The water has moved and been dug since it was last looked at.
                    craft.Vessel.Nav.Refresh();
                    _tools.Say($"{NameOf(craft.Vessel)}: right-click water to send it there");
                    return true;
                }

            return false;
        }

        public void Deselect() => _selected = null;

        /// <summary>Sends the selected craft to the water near the cell; the status line says how it went.</summary>
        public bool OrderSailTo(int x, int z)
        {
            if (_selected == null)
                return false;
            var vessel = _selected.Vessel;
            // Live water moves and the crew digs: where it floats is worked out afresh for each order.
            vessel.Nav.Refresh();
            var sailing = vessel.SailTo(new Vector2Int(x, z), out var why);
            _tools.Say(sailing ? $"{NameOf(vessel)}: under way" : $"{NameOf(vessel)} can't go there: {why}");
            return sailing;
        }

        static string NameOf(Vessel vessel) => char.ToUpperInvariant(vessel.Spec.Name[0]) + vessel.Spec.Name.Substring(1);

        float WaterLevel(Vector2 cells)
        {
            var grid = _terrain.Grid;
            int x = Mathf.Clamp(Mathf.FloorToInt(cells.x), 0, grid.Width - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt(cells.y), 0, grid.Height - 1);
            return grid.GetSurfaceHeight(x, z) + grid.WaterDepth(x, z);
        }

        Vector3 World(Vector2 cells, float y)
        {
            var grid = _terrain.Grid;
            var world = _terrain.transform.TransformPoint(new Vector3(cells.x * grid.CellSize, 0f, cells.y * grid.CellSize));
            world.y = y;
            return world;
        }
    }
}
