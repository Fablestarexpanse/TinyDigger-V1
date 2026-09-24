using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The landing craft on the map (FERRY_PROPOSAL.md, slice B): one craft moored off the beach
    /// nearest the crew's spawn, sailed from beach to beach by clicking it and right-clicking a
    /// shore. The craft itself is a <see cref="Ferry"/>, plain C#; this draws it. It sets the hull's
    /// place across the water and its heading, and the model's WaterFloater puts it on the water
    /// as drawn, swell and all; it plays the ramp and the props from the craft's state.
    /// </summary>
    public sealed class FerryHost : MonoBehaviour
    {
        /// <summary>Seconds into the game before the craft is put on the water: the water settles first.</summary>
        const float WaterSettleSeconds = 8f;

        /// <summary>How far out from the crew's spawn to look for a beach, cells.</summary>
        const int ShoreSearchCells = 600;

        /// <summary>
        /// Floating cells a mooring has to be joined to: open water (about 500 m² at half-metre
        /// cells), not a pool or a stretch of river it could go nowhere from.
        /// </summary>
        const int OpenWaterCells = 2000;

        PlayerTools _tools;
        TerrainView _terrain;
        GameObject _prefab;
        Transform _body;
        Animator _animator;
        LineRenderer _routeLine;
        FerryState _drawnState;
        bool _gaveUp;

        /// <summary>The craft, or null before it is on the water.</summary>
        public Ferry Craft { get; private set; }

        public bool Selected { get; private set; }

        public void Init(PlayerTools tools, TerrainView terrain, GameObject prefab, Material lineMaterial)
        {
            _tools = tools;
            _terrain = terrain;
            _prefab = prefab;
            var line = new GameObject("Landing Craft Route") { hideFlags = HideFlags.DontSave };
            line.transform.SetParent(transform, false);
            _routeLine = line.AddComponent<LineRenderer>();
            _routeLine.sharedMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
            _routeLine.widthMultiplier = 0.15f;
            _routeLine.startColor = _routeLine.endColor = new Color(0.4f, 0.85f, 1f, 0.8f);
            _routeLine.positionCount = 0;
        }

        void Update()
        {
            if (_terrain == null || _terrain.Grid == null || _prefab == null)
                return;
            if (Craft == null)
            {
                if (!_gaveUp && Time.timeSinceLevelLoad >= WaterSettleSeconds)
                    Launch();
                return;
            }

            Craft.Tick(Mathf.Min(Time.deltaTime, 0.1f));
            Draw();
        }

        /// <summary>Moors the craft on the beach nearest the crew's spawn.</summary>
        void Launch()
        {
            var grid = _terrain.Grid;
            var nav = new WaterNav(grid, Ferry.Draft, Ferry.HalfBeam);
            nav.Refresh();
            var crew = FindAnyObjectByType<CrewView>();
            var from = crew != null && crew.Units.Count > 0 ? crew.Units[0].Cell : new Vector2Int(grid.Width / 2, grid.Height / 2);

            // Rings outward from the spawn: the first stretch of deep water with a beach on it.
            for (var ring = 8; ring <= ShoreSearchCells; ring += 8)
                for (var side = 0; side < 4 * ring; side += 4)
                {
                    var t = side / (float)(4 * ring) * Mathf.PI * 2f;
                    var cell = from + new Vector2Int(Mathf.RoundToInt(Mathf.Cos(t) * ring), Mathf.RoundToInt(Mathf.Sin(t) * ring));
                    if (!nav.Floats(cell.x, cell.y) || nav.OpenWater(cell, OpenWaterCells) < OpenWaterCells)
                        continue;
                    var landing = Ferry.FindLanding(grid, nav, cell, 30);
                    if (!landing.Found)
                        continue;
                    Craft = Ferry.BeachedAt(grid, nav, landing);
                    Place(landing);
                    Debug.Log($"FerryHost: landing craft moored at ({landing.Hull.x:0}, {landing.Hull.y:0}), {ring * grid.CellSize:0} m from the crew");
                    return;
                }

            _gaveUp = true;
            Debug.LogWarning("FerryHost: no beach for the landing craft near the crew's spawn");
        }

        void Place(Landing landing)
        {
            var body = Instantiate(_prefab, transform, false);
            body.name = "Landing Craft";
            body.hideFlags = HideFlags.DontSave;
            _body = body.transform;
            _body.position = World(landing.Hull, WaterLevel(landing.Hull));
            _body.rotation = Quaternion.Euler(0f, landing.Heading, 0f);
            _animator = body.GetComponentInChildren<Animator>();

            // Something to click: a box round the hull.
            var renderers = body.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            var box = body.AddComponent<BoxCollider>();
            box.center = _body.InverseTransformPoint(bounds.center);
            box.size = new Vector3(bounds.size.x, bounds.size.y, bounds.size.z);
            _drawnState = Craft.State;
        }

        void Draw()
        {
            // Across the water and round: ours. Up and down, pitch and roll: the floater's, after this.
            var position = World(Craft.Position, _body.position.y);
            _body.SetPositionAndRotation(position, Quaternion.Euler(0f, Craft.Heading, 0f));

            if (_animator != null)
            {
                if (Craft.State != _drawnState)
                {
                    if (Craft.State == FerryState.LoweringRamp)
                        _animator.CrossFadeInFixedTime("RampDown", 0.1f, 2);
                    else if (Craft.State == FerryState.RaisingRamp)
                        _animator.CrossFadeInFixedTime("RampUp", 0.1f, 2);
                    _drawnState = Craft.State;
                }

                _animator.SetLayerWeight(1, Craft.UnderWay ? 1f : 0f);
            }

            var route = Craft.Route;
            _routeLine.positionCount = Selected ? route.Count : 0;
            if (Selected)
                for (var i = 0; i < route.Count; i++)
                {
                    var at = new Vector2(route[i].x + 0.5f, route[i].y + 0.5f);
                    _routeLine.SetPosition(i, World(at, WaterLevel(at) + 0.3f));
                }
        }

        /// <summary>Selects the craft if the ray hits it; true when it did.</summary>
        public bool TrySelectAt(Ray ray)
        {
            Selected = _body != null && Physics.Raycast(ray, out var hit, 10000f) && hit.transform == _body;
            return Selected;
        }

        public void Deselect() => Selected = false;

        /// <summary>Sends the selected craft to beach near the cell; the status line says how it went.</summary>
        public bool OrderSailTo(int x, int z)
        {
            if (Craft == null)
                return false;
            // Live water moves and the crew digs: where it floats is worked out afresh for each order.
            Craft.Nav.Refresh();
            var sailing = Craft.SailTo(new Vector2Int(x, z), out var why);
            _tools.Say(sailing
                ? $"Landing craft: heading for the beach near ({x}, {z})"
                : $"Landing craft can't beach there: {why}");
            return sailing;
        }

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
            var local = new Vector3(cells.x * grid.CellSize, 0f, cells.y * grid.CellSize);
            var world = _terrain.transform.TransformPoint(local);
            world.y = y;
            return world;
        }
    }
}
