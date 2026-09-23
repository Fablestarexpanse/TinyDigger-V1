using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The terraform tool: draw the shape you want the ground to be, see what it costs, and hand it
    /// to the crew.
    ///
    /// It is the road tool's sibling in every way that matters (<see cref="RoadsHost"/>): a draft of
    /// nodes, a ghost rebuilt only when something changes, pixel-space picking, and a commit that
    /// turns the shape into designations. What is different is that a landform is an *area*, and
    /// that the plan holds every shape at once, so committing is a diff of the whole plan rather
    /// than one road at a time (<see cref="LandformBuilder"/>).
    ///
    /// Click to drop corners; Enter or a double click closes the shape and commits it. Nothing is
    /// dug by drawing: the crew work the designations off, exactly as they do a road.
    /// </summary>
    public sealed class TerraformHost : MonoBehaviour
    {
        const float NodePickPixels = 22f;
        const float NodePickRadius = 0.9f;
        const float DoubleClickSeconds = 0.35f;

        static readonly Color32 DigColor = new Color32(235, 70, 60, 90);
        static readonly Color32 FillColor = new Color32(70, 140, 245, 90);
        static readonly Color32 FlatColor = new Color32(200, 205, 210, 55);
        static readonly Color32 CutFaceColor = new Color32(215, 150, 95, 95);
        static readonly Color32 FillFaceColor = new Color32(110, 165, 235, 95);
        static readonly Color32 OutlineColor = new Color32(255, 255, 255, 230);
        static readonly Color32 ActiveNodeColor = new Color32(255, 220, 60, 240);
        static readonly Color32 BuiltColor = new Color32(250, 240, 200, 150);

        PlayerTools _tools;
        TerrainView _terrain;
        Mesh _mesh;
        Material _ghostMaterial;
        DesignationMap _builtFor;

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();
        readonly List<PlannedCell> _preview = new List<PlannedCell>();
        readonly List<PlannedCell> _faces = new List<PlannedCell>();
        readonly List<int> _rim = new List<int>();
        readonly List<PlannedCell> _committedCells = new List<PlannedCell>();
        readonly List<Vector2> _outline = new List<Vector2>();
        readonly List<RoadSample> _samples = new List<RoadSample>();
        readonly Dictionary<int, float> _surface = new Dictionary<int, float>();

        int _plannedVersion = -1;
        int _dragNode = -1;
        float _lastClickTime;
        Vector2 _lastClickAt;

        public LandformPlan Plan { get; private set; } = new LandformPlan();

        public LandformDraft Draft { get; } = new LandformDraft();

        public LandformBuilder Builder { get; private set; }

        /// <summary>The draft's cut and fill in m³, before anything is committed.</summary>
        public float Cut { get; private set; }

        public float Fill { get; private set; }

        /// <summary>Cells the draft covers, for the readout.</summary>
        public int Cells { get; private set; }

        public bool IsDrawing => Draft.Any;

        public void Init(PlayerTools tools, TerrainView terrain, Material material)
        {
            _tools = tools;
            _terrain = terrain;
            _mesh = new Mesh { name = "Terraform Ghost" };
            _mesh.MarkDynamic();
            var holder = new GameObject("Terraform Ghost") { hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(terrain.transform, false);
            holder.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = holder.AddComponent<MeshRenderer>();
            // Its own copy of the overlay material, drawn without a depth test: a pit's ghost is
            // inside the hill, and a preview you cannot see is no use for deciding how deep to cut.
            _ghostMaterial = new Material(material) { name = "Terraform Ghost" };
            if (_ghostMaterial.HasProperty("_ZTest"))
                _ghostMaterial.SetFloat("_ZTest", (float)CompareFunction.Always);
            meshRenderer.sharedMaterial = _ghostMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            terrain.Regenerated += Forget;
        }

        void OnDestroy()
        {
            if (_terrain != null)
                _terrain.Regenerated -= Forget;
            if (_ghostMaterial != null)
                Destroy(_ghostMaterial);
        }

        /// <summary>A new island: the old plan means nothing on it.</summary>
        void Forget()
        {
            Plan = new LandformPlan();
            Builder = null;
            _builtFor = null;
            Draft.Clear();
        }

        TerrainGrid Grid => _terrain == null ? null : _terrain.Grid;

        float GroundAt(Vector2 cells)
        {
            var grid = Grid;
            if (grid == null)
                return 0f;
            var x = Mathf.Clamp(Mathf.FloorToInt(cells.x), 0, grid.Width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(cells.y), 0, grid.Height - 1);
            return grid.IsGround(x, z) ? grid.GetSurfaceHeight(x, z) : World.SeaLevel;
        }

        void EnsureBuilder()
        {
            var map = _tools?.Map;
            if (map == null || ReferenceEquals(map, _builtFor))
                return;
            _builtFor = map;
            Builder = new LandformBuilder(Grid, map);
        }

        // --- input, from PlayerTools while the terraform tool is in hand ------------------------

        /// <summary>Handles the mouse over the ground; <paramref name="at"/> is the cursor in cells.</summary>
        public void HandleMouse(Mouse mouse, bool hasHover, Vector2 at)
        {
            if (Grid == null)
                return;

            if (mouse.leftButton.wasPressedThisFrame && hasHover)
                Press(at);

            if (mouse.leftButton.isPressed && _dragNode >= 0 && hasHover)
                Draft.Move(_dragNode, at, GroundAt);

            if (mouse.leftButton.wasReleasedThisFrame)
                _dragNode = -1;
        }

        void Press(Vector2 at)
        {
            var now = Time.unscaledTime;
            var quick = now - _lastClickTime < DoubleClickSeconds && Vector2.Distance(at, _lastClickAt) < 1.5f;
            _lastClickTime = now;
            _lastClickAt = at;

            // A second click in the same place closes the shape rather than stacking a corner on a
            // corner, which is how everyone expects to finish an outline.
            if (quick && Draft.CanCommit)
            {
                Commit();
                return;
            }

            var node = PickNode(at);
            if (node >= 0)
            {
                _dragNode = node;
                return;
            }

            if (!Draft.Any)
                Draft.Begin(Draft.Form.Kind, _tools.TargetHeight);
            Draft.Place(at, GroundAt);
        }

        /// <summary>Returns true when the key was ours, so PlayerTools leaves it alone.</summary>
        public bool HandleKeys(Keyboard keyboard)
        {
            if (keyboard == null)
                return false;

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                Commit();
                return true;
            }

            if (keyboard.backspaceKey.wasPressedThisFrame && Draft.Any)
            {
                Draft.RemoveLast();
                return true;
            }

            if (keyboard.cKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
            {
                Draft.SetCurved(!Draft.Form.Curved);
                _tools.Say(Draft.Form.Curved ? "Outline curves between corners" : "Outline corners");
                return true;
            }

            return false;
        }

        public void Cancel()
        {
            Draft.Clear();
            _dragNode = -1;
        }

        /// <summary>Puts the drawn shape into the plan and the plan to the crew.</summary>
        public void Commit()
        {
            if (!Draft.CanCommit || Grid == null)
                return;
            EnsureBuilder();
            if (Builder == null)
                return;

            var form = Draft.Commit(Plan);
            if (form == null)
                return;

            // Only the shape is designated. The batters are not work the crew are given: they are
            // what the ground will do to itself once the shape is cut, and the slump does them. They
            // are shown, and counted in the cost, because you are paying for them either way —
            // which is the same bargain the road tool strikes.
            _tools.History?.Begin("terraform");
            Plan.Rasterise(Grid, _committedCells);
            var made = Builder.Commit(_committedCells);
            _tools.History?.Commit();

            Blueprints.Volumes(_committedCells, out var cut, out var fill, Grid.CellArea);
            _tools.Say($"Shape committed: {made} cells of work, cut {cut:0.#} m³, fill {fill:0.#} m³");
        }

        /// <summary>Takes the shape under the cursor out of the plan, and its orders with it.</summary>
        public bool RemoveAt(Vector2 at)
        {
            var grid = Grid;
            if (grid == null)
                return false;
            var owners = new Dictionary<int, int>();
            Plan.Surface(grid, _surface, owners);
            var cell = Mathf.FloorToInt(at.y) * grid.Width + Mathf.FloorToInt(at.x);
            if (!owners.TryGetValue(cell, out var id))
                return false;

            Plan.Remove(id);
            EnsureBuilder();
            _tools.History?.Begin("remove shape");
            Plan.Rasterise(Grid, _committedCells);
            Builder?.Commit(_committedCells);
            _tools.History?.Commit();
            _tools.Say("Shape removed");
            return true;
        }

        /// <summary>
        /// The draft node under the cursor, measured on screen so it is as easy to grab close up as
        /// far away — the road tool learned this the hard way — or -1.
        /// </summary>
        int PickNode(Vector2 at)
        {
            var camera = _tools?.Camera;
            if (camera == null)
                return Draft.NodeNear(at, NodePickRadius);

            var best = -1;
            var bestPixels = NodePickPixels;
            var cursor = camera.WorldToScreenPoint(WorldOf(at, GroundAt(at)));
            var nodes = Draft.Form.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                var screen = camera.WorldToScreenPoint(WorldOf(nodes[i].Position, nodes[i].Height));
                if (screen.z <= 0f)
                    continue;
                var pixels = Vector2.Distance(new Vector2(screen.x, screen.y), new Vector2(cursor.x, cursor.y));
                if (pixels > bestPixels)
                    continue;
                bestPixels = pixels;
                best = i;
            }

            return best;
        }

        Vector3 WorldOf(Vector2 cells, float height) =>
            _terrain.transform.TransformPoint(new Vector3(cells.x * Grid.CellSize, height, cells.y * Grid.CellSize));

        // --- the ghost --------------------------------------------------------------------------

        void Update()
        {
            if (Grid == null || _tools == null)
                return;
            EnsureBuilder();
            PlanDraft();
            DrawGhost();
        }

        /// <summary>
        /// Works out what the draft would do, and only when something has changed. A shape can cover
        /// tens of thousands of cells, so this is the difference between a tool and a stutter.
        /// </summary>
        void PlanDraft()
        {
            var stamp = Draft.Version * 31 + Plan.Stamp();
            if (stamp == _plannedVersion)
                return;
            _plannedVersion = stamp;

            Draft.CellSize = Grid.CellSize;
            _preview.Clear();
            _faces.Clear();
            Cut = Fill = 0f;
            Cells = 0;
            if (!Draft.CanCommit)
                return;

            var one = new LandformPlan();
            one.Add(Draft.Form);
            one.Rasterise(Grid, _preview, includeSettled: true);

            // What the ground round the shape does once it is built. Only the rim can batter
            // anything, which is what keeps this in microseconds on a shape of thousands of cells.
            LandformPlanner.Batters(Grid, _preview, Draft.Form.Spoil, _faces, _rim);

            Blueprints.Volumes(_preview, out var cut, out var fill, Grid.CellArea);
            Blueprints.Volumes(_faces, out var faceCut, out var faceFill, Grid.CellArea);
            Cut = cut + faceCut;
            Fill = fill + faceFill;
            Cells = _preview.Count;
        }

        void DrawGhost()
        {
            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();

            if (_tools.Mode == ToolMode.Terraform)
            {
                var cell = Grid.CellSize;

                // The batters first, under the shape itself. A cut face is drawn on the ground it
                // takes away rather than at the height it settles to, or it is buried in the hill.
                foreach (var face in _faces)
                {
                    var top = Mathf.Max(face.Height, Grid.GetSurfaceHeight(face.X, face.Z)) + 0.05f;
                    DesignationsView.AddTile(face.X, face.Z, top, top, top, top,
                        face.IsDig ? CutFaceColor : FillFaceColor, _vertices, _colors, _triangles, cell);
                }

                // What the draft would do, cell by cell: red where the ground comes off, blue where
                // it goes on, and a neutral tile where it is already right — so the shape reads as
                // one piece rather than a scatter of work.
                foreach (var planned in _preview)
                {
                    var top = Mathf.Max(planned.Height, Grid.GetSurfaceHeight(planned.X, planned.Z)) + 0.06f;
                    var colour = planned.IsDig ? DigColor : planned.IsFill ? FillColor : FlatColor;
                    DesignationsView.AddTile(planned.X, planned.Z, top, top, top, top,
                        colour, _vertices, _colors, _triangles, cell);
                }

                // The outline itself, at the height it is asking for, and its corners.
                if (Draft.Any)
                {
                    LandformSpline.Polygon(Draft.Form, LandformPlan.SampleSpacing, _samples, _outline);
                    GhostMesh.Loop(_outline, Draft.Form.Height + 0.15f, 0.12f, OutlineColor,
                        _vertices, _colors, _triangles, cell);
                    var nodes = Draft.Form.Nodes;
                    for (var i = 0; i < nodes.Count; i++)
                        GhostMesh.Disc(nodes[i].Position, Draft.Form.Height + 0.2f, 0.55f,
                            i == nodes.Count - 1 ? ActiveNodeColor : OutlineColor,
                            _vertices, _colors, _triangles, cell);
                }

                // Shapes already in the plan, so they can be found again.
                foreach (var form in Plan.Forms)
                {
                    if (!form.IsDrawn)
                        continue;
                    LandformSpline.Polygon(form, LandformPlan.SampleSpacing, _samples, _outline);
                    GhostMesh.Loop(_outline, form.Height + 0.1f, 0.08f, BuiltColor,
                        _vertices, _colors, _triangles, cell);
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        GUIStyle _label;

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _tools == null || _tools.Mode != ToolMode.Terraform)
                return;
            if (!Draft.Any)
                return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
                _label.normal.textColor = Color.white;
            }

            // The balance is the number worth watching: a site that feeds itself needs nothing
            // hauled in and has nowhere to put anything, and one that does not needs a quarry or a
            // heap somewhere.
            var area = Cells * Grid.CellArea;
            var balance = Cut - Fill;
            var says = $"cut {Cut:0.#} m³   fill {Fill:0.#} m³   "
                       + (Mathf.Abs(balance) < 0.05f ? "balanced"
                           : balance > 0f ? $"spoil {balance:0.#} m³ to tip"
                           : $"needs {-balance:0.#} m³")
                       + $"   area {area:0.#} m²";
            GUI.Label(new Rect(12f, Screen.height - 76f, 620f, 26f), says, _label);
            GUI.Label(new Rect(12f, Screen.height - 48f, 620f, 26f),
                Draft.CanCommit ? "Enter or double-click to commit   •   C curves the outline   •   Backspace undoes a corner"
                    : "Click to drop corners — three make a shape", _label);
        }
    }
}
