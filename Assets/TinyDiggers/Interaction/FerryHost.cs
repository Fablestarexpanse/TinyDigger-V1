using System.Collections.Generic;
using PromptWaffle.DynamicWater;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;

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
        LineRenderer _ghost;
        LineRenderer _rampMark;
        LineRenderer _selectedOutline;
        Vector2Int _lastHover = new Vector2Int(int.MinValue, int.MinValue);
        Landing _preview;
        bool _hintedBoard;
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

            // The ghost of where it will beach, the ramp foot, and a ring round it when selected.
            _ghost = Line("Landing Craft Ghost", _routeLine.sharedMaterial, new Color(0.45f, 1f, 0.55f, 0.9f), 0.12f);
            _rampMark = Line("Landing Craft Ramp Foot", _routeLine.sharedMaterial, new Color(1f, 0.9f, 0.3f, 0.95f), 0.1f);
            _selectedOutline = Line("Landing Craft Selected", _routeLine.sharedMaterial, new Color(1f, 0.95f, 0.5f, 0.9f), 0.1f);
        }

        LineRenderer Line(string name, Material material, Color colour, float width)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.widthMultiplier = width;
            line.startColor = line.endColor = colour;
            line.loop = true;
            line.positionCount = 0;
            return line;
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
            Preview();
            HintBoarding();
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
                    // A beach the crew can drive to: the first take moored it on a lake the crew
                    // could not reach, and every order to board was refused (2026-09-24).
                    var craft = Ferry.BeachedAt(grid, nav, landing);
                    var lineUp = craft.LineUpCell(CrewUnit.ReverseCells);
                    if (crew != null && crew.Dispatcher != null
                        && !crew.Dispatcher.Regions.CanReach(from.x, from.y, lineUp.x, lineUp.y))
                        continue;
                    Craft = craft;
                    // Moored with the ramp down, ready for machines to come aboard.
                    Craft.LowerRamp();
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

            // Foam round the hull: lapping at rest, a bow wave and a wake under way (Ronan,
            // 2026-09-24: "water effects surrounding" the boat). Sized from the hull in its own
            // frame, a little inside the drawn outline, which includes the ramp and the rails.
            var foam = body.AddComponent<WaterFoamEmitterComponent>();
            var local = LocalFootprint(renderers);
            foam.HullSize = new Vector2(local.size.z * 0.9f, local.size.x * 0.85f);
            foam.HullOffset = new Vector2(local.center.x, local.center.z);
            _drawnState = Craft.State;
        }

        /// <summary>
        /// The hull's extent in the body's own frame, from each renderer's own bounds: the world
        /// bounds are boxes square to the world, and at a slant heading they overstate the hull.
        /// </summary>
        Bounds LocalFootprint(Renderer[] renderers)
        {
            var local = new Bounds(_body.InverseTransformPoint(renderers[0].bounds.center), Vector3.zero);
            foreach (var r in renderers)
            {
                var b = r.localBounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    local.Encapsulate(_body.InverseTransformPoint(r.transform.TransformPoint(corner)));
                }
            }

            return local;
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

        /// <summary>
        /// With the craft selected: where it would beach for the ground under the cursor, drawn as a
        /// ghost of its hull and ramp foot, or the reason it cannot, on the status line. Worked out
        /// only when the cursor moves to another cell, from the water as last refreshed.
        /// </summary>
        void Preview()
        {
            DrawHull(_selectedOutline, Selected ? Craft.Position : (Vector2?)null, Craft.Heading, 0.35f);
            if (!Selected || !_tools.HasHover)
            {
                _ghost.positionCount = 0;
                _rampMark.positionCount = 0;
                _lastHover = new Vector2Int(int.MinValue, int.MinValue);
                return;
            }

            var hover = new Vector2Int(_tools.HoverX, _tools.HoverZ);
            if (hover != _lastHover)
            {
                _lastHover = hover;
                _preview = Ferry.FindLanding(_terrain.Grid, Craft.Nav, hover);
                _tools.Say(Craft.Busy ? "Landing craft: waiting for machines to finish going aboard or ashore"
                    : _preview.Found ? "Landing craft: right-click to beach here"
                    : "Landing craft can't beach here: " + _preview.Refusal);
            }

            DrawHull(_ghost, _preview.Found ? _preview.Hull : (Vector2?)null, _preview.Heading, 0.15f);
            if (_preview.Found)
            {
                var foot = _preview.RampFoot;
                var centre = new Vector2(foot.x + 0.5f, foot.y + 0.5f);
                var y = _terrain.transform.TransformPoint(new Vector3(0f, _terrain.Grid.GetSurfaceHeight(foot.x, foot.y) + 0.08f, 0f)).y;
                _rampMark.positionCount = 4;
                _rampMark.SetPosition(0, World(centre + new Vector2(-0.45f, -0.45f), y));
                _rampMark.SetPosition(1, World(centre + new Vector2(0.45f, -0.45f), y));
                _rampMark.SetPosition(2, World(centre + new Vector2(0.45f, 0.45f), y));
                _rampMark.SetPosition(3, World(centre + new Vector2(-0.45f, 0.45f), y));
            }
            else
            {
                _rampMark.positionCount = 0;
            }
        }

        /// <summary>The hull's outline at <paramref name="hull"/> (cells), just above the water; hidden when null.</summary>
        void DrawHull(LineRenderer line, Vector2? hull, float heading, float above)
        {
            if (!hull.HasValue)
            {
                line.positionCount = 0;
                return;
            }

            var cell = _terrain.Grid.CellSize;
            var radians = heading * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
            var side = new Vector2(dir.y, -dir.x);
            var along = dir * (Ferry.HalfLength / cell);
            var across = side * (Ferry.HalfBeam / cell);
            var y = WaterLevel(hull.Value) + above;
            line.positionCount = 4;
            line.SetPosition(0, World(hull.Value + along + across, y));
            line.SetPosition(1, World(hull.Value + along - across, y));
            line.SetPosition(2, World(hull.Value - along - across, y));
            line.SetPosition(3, World(hull.Value - along + across, y));
        }

        /// <summary>
        /// With machines selected and the cursor on the craft: says that a right-click sends them
        /// aboard, or why it would not.
        /// </summary>
        void HintBoarding()
        {
            var crew = _tools.Crew;
            var mouse = Mouse.current;
            var over = crew != null && crew.SelectedCount > 0 && mouse != null && _tools.Camera != null
                       && Hits(_tools.Camera.ScreenPointToRay(mouse.position.ReadValue()));
            if (over && !_hintedBoard)
            {
                var free = 0;
                foreach (var lane in Craft.Lanes)
                    if (lane < 0)
                        free++;
                _tools.Say(Craft.State != FerryState.RampDown ? "Landing craft: its ramp is not down"
                    : free == 0 ? "Landing craft: full"
                    : "Right-click to send them aboard the landing craft (" + free + (free == 1 ? " lane free)" : " lanes free)"));
            }

            _hintedBoard = over;
        }

        /// <summary>
        /// Metres above the keel the machines ride: the deck of the well (forge 0.235 m, x1.41). It
        /// was 0.22, flush with the hull box, and the two tops z-fought (2026-09-25).
        /// </summary>
        const float DeckHeight = 0.33f;

        /// <summary>
        /// Where a unit on the craft is drawn: blended from the ramp foot on the beach to its lane on
        /// the deck as drawn, bobbing and all, by how far up the ramp it is. False for a unit that is
        /// not on this craft.
        /// </summary>
        public bool TryDeckPose(CrewUnit unit, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (_body == null || Craft == null || unit.Ferry != Craft || unit.FerryLane < 0)
                return false;
            var grid = _terrain.Grid;
            var lane = new Vector3((unit.FerryLane == 0 ? -Ferry.LaneOffset : Ferry.LaneOffset), DeckHeight, Ferry.LaneForward);
            var deck = _body.TransformPoint(lane);
            var footCell = Craft.Landing.RampFoot;
            var foot = World(new Vector2(footCell.x + 0.5f, footCell.y + 0.5f),
                _terrain.transform.TransformPoint(new Vector3(0f, grid.GetSurfaceHeight(footCell.x, footCell.y), 0f)).y);
            position = Vector3.Lerp(foot, deck, unit.RampProgress);
            rotation = unit.RampProgress >= 1f ? _body.rotation : Quaternion.Euler(0f, unit.Heading, 0f);
            return true;
        }

        /// <summary>Whether the ray hits the craft.</summary>
        public bool Hits(Ray ray) => _body != null && Physics.Raycast(ray, out var hit, 10000f) && hit.transform == _body;

        /// <summary>Selects the craft if the ray hits it; true when it did.</summary>
        public bool TrySelectAt(Ray ray)
        {
            var was = Selected;
            Selected = _body != null && Physics.Raycast(ray, out var hit, 10000f) && hit.transform == _body;
            // The water has moved and been dug since it was last looked at: the ghost reads it fresh.
            if (Selected && !was && Craft != null)
                Craft.Nav.Refresh();
            return Selected;
        }

        public void Deselect() => Selected = false;

        /// <summary>Selects the craft, as clicking it does (the units menu).</summary>
        public void Select()
        {
            if (Craft == null)
                return;
            if (!Selected)
                Craft.Nav.Refresh();
            Selected = true;
            _tools.Crew?.Deselect();
            _tools.Say("Landing craft: right-click a shore to send it there");
        }

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
