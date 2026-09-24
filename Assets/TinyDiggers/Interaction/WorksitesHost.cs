using System.Collections.Generic;
using System.Text;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The Worksite tool and the worksites on the map (2026-09-24), after Captain of Industry's
    /// mining tower: a building put down at a job, a work area round it, and vehicles assigned to
    /// it that work that area and nothing else.
    ///
    /// Mouse, with the Worksite tool: drag on open ground to put a building down where the drag
    /// starts and draw its area; a click without a drag gives a square area centred on it. Click
    /// inside an area to select that worksite; drag its building to move it, area and all; drag a
    /// corner of the selected area to resize it. Right-click a worksite to remove it — its vehicles
    /// park. With units selected, right-clicking a worksite in the Select tool assigns them to it.
    ///
    /// The worksites themselves live on the crew's <see cref="JobDispatcher"/>, where the crew
    /// logic reads them; this draws them and edits them.
    /// </summary>
    public sealed class WorksitesHost : MonoBehaviour
    {
        /// <summary>Side of the area a click without a drag gives, in cells: 20 m at half-metre cells.</summary>
        const int DefaultSide = 40;

        /// <summary>How near the cursor has to be to grab a building or a corner, in screen pixels.</summary>
        const float PickPixels = 24f;

        /// <summary>Drags shorter than this, in cells, are clicks.</summary>
        const float ClickCells = 1.5f;

        static readonly Color32 AreaColor = new Color32(255, 200, 70, 200);
        static readonly Color32 SelectedAreaColor = new Color32(255, 235, 120, 240);
        static readonly Color32 DrawingColor = new Color32(255, 255, 255, 170);
        static readonly Color32 CornerColor = new Color32(255, 235, 120, 240);
        static readonly Color BuildingColor = new Color(0.85f, 0.62f, 0.2f);
        static readonly Color SelectedBuildingColor = new Color(1f, 0.85f, 0.4f);

        PlayerTools _tools;
        TerrainView _terrain;
        Mesh _mesh;
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();
        readonly Dictionary<int, Transform> _buildings = new Dictionary<int, Transform>();
        readonly List<int> _gone = new List<int>();

        enum Drag { None, New, Move, Corner }

        Drag _drag;
        Vector2Int _dragFrom;
        Vector2Int _dragTo;
        int _dragSite;

        /// <summary>The corner of the area held still while another is dragged.</summary>
        Vector2Int _fixedCorner;

        /// <summary>The worksite the panel shows and whose corners can be dragged, or 0.</summary>
        public int Selected { get; private set; }

        /// <summary>The crew's worksites, or null before the crew is up.</summary>
        public Worksites Sites => _tools != null && _tools.Crew != null && _tools.Crew.Dispatcher != null
            ? _tools.Crew.Dispatcher.Worksites
            : null;

        public bool IsDrawing => _drag == Drag.New;

        TerrainGrid Grid => _terrain != null ? _terrain.Grid : null;

        public void Init(PlayerTools tools, TerrainView terrain, Material material)
        {
            _tools = tools;
            _terrain = terrain;
            _mesh = new Mesh { name = "Worksite Areas" };
            _mesh.MarkDynamic();
            var holder = new GameObject("Worksite Areas") { hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(terrain.transform, false);
            holder.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = holder.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            terrain.Regenerated += Forget;
        }

        void OnDestroy()
        {
            if (_terrain != null)
                _terrain.Regenerated -= Forget;
        }

        /// <summary>A new island: the worksites were on the old one.</summary>
        void Forget()
        {
            Sites?.Clear();
            Selected = 0;
            _drag = Drag.None;
        }

        public void Cancel()
        {
            _drag = Drag.None;
        }

        // --- input, from PlayerTools while the Worksite tool is in hand -------------------------------

        public void HandleMouse(Mouse mouse, bool hasHover, Vector2 at)
        {
            var sites = Sites;
            if (sites == null)
                return;
            var cell = new Vector2Int(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));

            if (mouse.leftButton.wasPressedThisFrame && hasHover)
                Press(sites, at, cell);

            if (mouse.leftButton.isPressed && hasHover && _drag != Drag.None)
            {
                _dragTo = cell;
                switch (_drag)
                {
                    case Drag.Move:
                        sites.Move(_dragSite, cell);
                        break;
                    case Drag.Corner:
                        sites.SetArea(_dragSite, Between(_fixedCorner, cell));
                        break;
                }
            }

            if (mouse.leftButton.wasReleasedThisFrame && _drag != Drag.None)
            {
                if (_drag == Drag.New)
                {
                    var site = Vector2.Distance(_dragFrom, _dragTo) < ClickCells
                        ? sites.Add(_dragFrom, DefaultSide)
                        : sites.Add(_dragFrom, Between(_dragFrom, _dragTo));
                    Selected = site.Id;
                    _tools.Say($"{site.Name} set up: {Describe(site)}. Select units and right-click it to assign them");
                }

                _drag = Drag.None;
            }
        }

        void Press(Worksites sites, Vector2 at, Vector2Int cell)
        {
            // A corner of the selected area first: it is the smallest target.
            var selected = sites.Get(Selected);
            if (selected != null)
            {
                var corners = Corners(selected.Area);
                for (var i = 0; i < 4; i++)
                {
                    if (!Near(corners[i], at))
                        continue;
                    _drag = Drag.Corner;
                    _dragSite = selected.Id;
                    // The corner diagonally opposite stays where it is. Corners are cell edges, so
                    // the cell kept is the one just inside that corner.
                    var opposite = corners[(i + 2) % 4];
                    _fixedCorner = new Vector2Int(
                        opposite.x > corners[i].x ? Mathf.RoundToInt(opposite.x) - 1 : Mathf.RoundToInt(opposite.x),
                        opposite.y > corners[i].y ? Mathf.RoundToInt(opposite.y) - 1 : Mathf.RoundToInt(opposite.y));
                    return;
                }
            }

            foreach (var site in sites.All)
            {
                if (!Near(new Vector2(site.Building.x + 0.5f, site.Building.y + 0.5f), at))
                    continue;
                Selected = site.Id;
                _drag = Drag.Move;
                _dragSite = site.Id;
                return;
            }

            var under = sites.At(cell.x, cell.y);
            if (under != null)
            {
                Selected = under.Id;
                _tools.Say($"{under.Name}: {Describe(under)}");
                return;
            }

            _drag = Drag.New;
            _dragFrom = _dragTo = cell;
        }

        /// <summary>Right-click with the Worksite tool: takes away the worksite under the cursor. Its vehicles park.</summary>
        public bool RemoveAt(Vector2 at)
        {
            var sites = Sites;
            var site = SiteAt(at);
            if (sites == null || site == null)
                return false;
            sites.Remove(site.Id);
            if (Selected == site.Id)
                Selected = 0;
            _tools.Say($"{site.Name} removed; its vehicles park until they are assigned again");
            return true;
        }

        /// <summary>The worksite under a point: its building, or else the smallest area covering it.</summary>
        public Worksite SiteAt(Vector2 at)
        {
            var sites = Sites;
            if (sites == null)
                return null;
            foreach (var site in sites.All)
                if (Near(new Vector2(site.Building.x + 0.5f, site.Building.y + 0.5f), at))
                    return site;
            return sites.At(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));
        }

        public void Select(int id) => Selected = id;

        /// <summary>Takes every vehicle off the selected worksite: they park.</summary>
        public int ReleaseSelected()
        {
            var crew = _tools.Crew;
            if (crew == null || Selected == 0)
                return 0;
            var released = 0;
            foreach (var unit in crew.Units)
            {
                if (unit.Site != Selected)
                    continue;
                unit.Site = 0;
                unit.RequestRethink();
                released++;
            }

            return released;
        }

        public bool RemoveSelected()
        {
            var sites = Sites;
            if (sites == null || !sites.Remove(Selected))
                return false;
            Selected = 0;
            return true;
        }

        /// <summary>How many vehicles of each kind a worksite has, as words: "2 diggers, 1 dumper".</summary>
        public string Crew(Worksite site)
        {
            var crew = _tools.Crew;
            if (crew == null)
                return "no vehicles";
            var counts = new Dictionary<UnitRole, int>();
            foreach (var unit in crew.Units)
            {
                if (unit.Site != site.Id)
                    continue;
                counts.TryGetValue(unit.Role, out var n);
                counts[unit.Role] = n + 1;
            }

            if (counts.Count == 0)
                return "no vehicles";
            var text = new StringBuilder();
            foreach (var pair in counts)
            {
                if (text.Length > 0)
                    text.Append(", ");
                text.Append(pair.Value).Append(' ').Append(UnitNames.Short(pair.Key).ToLowerInvariant());
                if (pair.Value > 1)
                    text.Append('s');
            }

            return text.ToString();
        }

        string Describe(Worksite site)
        {
            var cell = Grid != null ? Grid.CellSize : 1f;
            return $"{site.Area.width * cell:0.#} × {site.Area.height * cell:0.#} m, {Crew(site)}";
        }

        /// <summary>The area between two cells, both included.</summary>
        static RectInt Between(Vector2Int a, Vector2Int b) =>
            new RectInt(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Abs(a.x - b.x) + 1, Mathf.Abs(a.y - b.y) + 1);

        /// <summary>An area's four corners in cells, going round: south-west, south-east, north-east, north-west.</summary>
        static Vector2[] Corners(RectInt area) => new[]
        {
            new Vector2(area.x, area.y),
            new Vector2(area.x + area.width, area.y),
            new Vector2(area.x + area.width, area.y + area.height),
            new Vector2(area.x, area.y + area.height),
        };

        /// <summary>Whether a point in cells is within <see cref="PickPixels"/> of the cursor on screen (or two cells without a camera).</summary>
        bool Near(Vector2 point, Vector2 cursor)
        {
            var camera = _tools.Camera;
            if (camera == null || Grid == null)
                return Vector2.Distance(point, cursor) <= 2f;
            var a = camera.WorldToScreenPoint(WorldOf(point));
            var b = camera.WorldToScreenPoint(WorldOf(cursor));
            return a.z > 0f && Vector2.Distance(new Vector2(a.x, a.y), new Vector2(b.x, b.y)) <= PickPixels;
        }

        Vector3 WorldOf(Vector2 cells) =>
            _terrain.transform.TransformPoint(new Vector3(cells.x * Grid.CellSize, TerrainSpace.GroundAt(Grid, cells), cells.y * Grid.CellSize));

        // --- drawing -----------------------------------------------------------------------------

        void Update()
        {
            var grid = Grid;
            var sites = Sites;
            if (grid == null || sites == null)
                return;
            if (Selected != 0 && sites.Get(Selected) == null)
                Selected = 0;

            SyncBuildings(sites, grid);

            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            var cell = grid.CellSize;
            float Ground(Vector2 at) => TerrainSpace.GroundAt(grid, at) + 0.12f;
            // The areas are always shown: which machine is working where is the whole point of them.
            foreach (var site in sites.All)
            {
                var selected = site.Id == Selected;
                GhostMesh.LoopOnGround(Corners(site.Area), Ground, selected ? 0.22f : 0.14f,
                    selected ? SelectedAreaColor : AreaColor, _vertices, _colors, _triangles, cell);
                if (selected && _tools.Mode == ToolMode.Worksite)
                    foreach (var corner in Corners(site.Area))
                        GhostMesh.Disc(corner, Ground(corner) + 0.02f, 0.9f, CornerColor, _vertices, _colors, _triangles, cell);
            }

            if (_drag == Drag.New)
                GhostMesh.LoopOnGround(Corners(Between(_dragFrom, _dragTo)), Ground, 0.18f, DrawingColor,
                    _vertices, _colors, _triangles, cell);

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        /// <summary>
        /// One stand-in building per worksite, standing on the ground at its cell: a squat block with
        /// a mast, so it reads as a site office from any angle until it has a model of its own.
        /// </summary>
        void SyncBuildings(Worksites sites, TerrainGrid grid)
        {
            _gone.Clear();
            foreach (var id in _buildings.Keys)
                if (sites.Get(id) == null)
                    _gone.Add(id);
            foreach (var id in _gone)
            {
                if (_buildings[id] != null)
                    Destroy(_buildings[id].gameObject);
                _buildings.Remove(id);
            }

            var terrain = _terrain.transform;
            foreach (var site in sites.All)
            {
                if (!_buildings.TryGetValue(site.Id, out var building) || building == null)
                {
                    building = MakeBuilding(site.Id);
                    _buildings[site.Id] = building;
                }

                var x = Mathf.Clamp(site.Building.x, 0, grid.Width - 1);
                var z = Mathf.Clamp(site.Building.y, 0, grid.Height - 1);
                var ground = grid.IsGround(x, z) ? TerrainSurface.SampleHeight(grid, x + 0.5f, z + 0.5f) : 0f;
                building.position = terrain.TransformPoint(new Vector3((x + 0.5f) * grid.CellSize, ground, (z + 0.5f) * grid.CellSize));
                building.rotation = terrain.rotation;
                var color = site.Id == Selected ? SelectedBuildingColor : BuildingColor;
                foreach (var r in building.GetComponentsInChildren<MeshRenderer>())
                    if (r.name == "Office")
                        r.material.color = color;
            }
        }

        Transform MakeBuilding(int id)
        {
            var root = new GameObject($"Worksite {id}") { hideFlags = HideFlags.DontSave }.transform;
            root.SetParent(transform, false);
            Part(root, "Office", PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(1.6f, 1.2f, 1.6f), BuildingColor);
            Part(root, "Roof", PrimitiveType.Cube, new Vector3(0f, 1.25f, 0f), new Vector3(1.8f, 0.1f, 1.8f), new Color(0.25f, 0.25f, 0.28f));
            Part(root, "Mast", PrimitiveType.Cylinder, new Vector3(0.55f, 2f, 0.55f), new Vector3(0.08f, 0.75f, 0.08f), new Color(0.8f, 0.8f, 0.82f));
            return root;
        }

        static void Part(Transform root, string name, PrimitiveType shape, Vector3 at, Vector3 scale, Color color)
        {
            var part = GameObject.CreatePrimitive(shape);
            part.name = name;
            part.hideFlags = HideFlags.DontSave;
            // Units are selected with the physics raycast; a building must not swallow those clicks.
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(root, false);
            part.transform.localPosition = at;
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().material.color = color;
        }

        // --- labels ------------------------------------------------------------------------------

        GUIStyle _label;

        void OnGUI()
        {
            var sites = Sites;
            var camera = _tools != null ? _tools.Camera : null;
            if (Event.current.type != EventType.Repaint || sites == null || camera == null || Grid == null)
                return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.box) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
                _label.normal.textColor = Color.white;
            }

            foreach (var site in sites.All)
            {
                var at = WorldOf(new Vector2(site.Building.x + 0.5f, site.Building.y + 0.5f)) + Vector3.up * 3.2f;
                var screen = camera.WorldToScreenPoint(at);
                if (screen.z <= 0f)
                    continue;
                var says = $"{site.Name}\n{Crew(site)}";
                var old = GUI.color;
                GUI.color = site.Id == Selected ? new Color(1f, 0.92f, 0.6f) : Color.white;
                GUI.Box(new Rect(screen.x - 75f, Screen.height - screen.y - 20f, 150f, 40f), says, _label);
                GUI.color = old;
            }
        }
    }
}
