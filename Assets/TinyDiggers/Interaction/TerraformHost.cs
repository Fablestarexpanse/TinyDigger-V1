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
        static readonly Color32 HeapColor = new Color32(80, 205, 95, 90);
        static readonly Color32 PitColor = new Color32(220, 160, 70, 90);
        static readonly Color32 CutFaceColor = new Color32(215, 150, 95, 95);
        static readonly Color32 FillFaceColor = new Color32(110, 165, 235, 95);
        static readonly Color32 OutlineColor = new Color32(255, 255, 255, 230);
        static readonly Color32 ActiveNodeColor = new Color32(255, 220, 60, 240);
        static readonly Color32 BuiltColor = new Color32(250, 240, 200, 150);
        static readonly Color32 GodColor = new Color32(255, 200, 40, 255);

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
        readonly Dictionary<int, float> _caps = new Dictionary<int, float>();
        readonly Dictionary<int, float> _floors = new Dictionary<int, float>();
        readonly Dictionary<int, int> _owners = new Dictionary<int, int>();
        readonly List<InsideCell> _inside = new List<InsideCell>();
        readonly Dictionary<int, float> _profile = new Dictionary<int, float>();

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

        /// <summary>A heap's m³ before it reaches the shape drawn, and a pit's m³ above its floor.</summary>
        public float Capacity { get; private set; }

        public float Reserves { get; private set; }

        /// <summary>How many benches deep a drafted pit goes.</summary>
        public int Benches { get; private set; }

        /// <summary>What the freehand brush is doing, and how far one press moves the plan.</summary>
        public BrushMode BrushMode { get; private set; } = BrushMode.Raise;

        public float BrushAmount = 0.5f;

        public bool IsDrawing => Draft.Any && Draft.Form.Kind != LandformKind.Stamp;

        /// <summary>
        /// God mode (Ronan, 2026-09-24): a placed stamp reshapes the ground on the spot instead of
        /// becoming orders for the crew. G toggles it while a stamp is in hand; Ctrl+Z takes back the
        /// last god stamp.
        /// </summary>
        public bool GodMode;

        readonly List<GroundStamp.Edit> _godEdits = new List<GroundStamp.Edit>();
        const int GodUndoDepth = 20;

        /// <summary>The stamps the tool offers, in library order; empty before any are imported.</summary>
        public List<HeightStamp> Stamps { get; } = new List<HeightStamp>();

        /// <summary>
        /// Takes up a kind of shape — the toolbar's Terraform and Stamp entries both land here. A stamp
        /// with none chosen yet starts on the first in the library.
        /// </summary>
        public void Pick(LandformKind kind)
        {
            Draft.SetKind(kind);
            if (kind == LandformKind.Stamp && Draft.Form.ResolveStamp() == null)
                NextStamp(0);
        }

        /// <summary>Moves <paramref name="by"/> along the library from the stamp in hand.</summary>
        public void NextStamp(int by)
        {
            LoadStamps();
            if (Stamps.Count == 0)
            {
                _tools.Say("No stamps yet: run TinyDiggers/Import Stamps");
                return;
            }

            var at = Stamps.IndexOf(Draft.Form.ResolveStamp());
            var next = Stamps[(int)Mathf.Repeat(at < 0 ? 0 : at + by, Stamps.Count)];
            Draft.SetStamp(next);
            _tools.Say($"Stamp — {next.DisplayName}");
        }

        /// <summary>Chooses one stamp outright, from the panel.</summary>
        public void ChooseStamp(HeightStamp stamp)
        {
            Draft.SetKind(LandformKind.Stamp);
            Draft.SetStamp(stamp);
        }

        void LoadStamps()
        {
            if (Stamps.Count > 0)
                return;
            var library = StampLibrary.Load();
            if (library != null)
                Stamps.AddRange(library.For(StampUse.Game));
        }

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
            _godEdits.Clear();
            Plan = new LandformPlan();
            Builder = null;
            _builtFor = null;
            Draft.Clear();
        }

        TerrainGrid Grid => _terrain == null ? null : _terrain.Grid;

        float GroundAt(Vector2 cells) => TerrainSpace.GroundAt(Grid, cells);

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

            // A stamp rides on the cursor, and a click puts it down and hands it to the crew.
            if (Draft.Form.Kind == LandformKind.Stamp)
            {
                if (hasHover)
                    Draft.MoveStamp(at, GroundAt);
                if (mouse.leftButton.wasPressedThisFrame && hasHover && Draft.CanCommit)
                    Commit();
                return;
            }

            // The freehand brush paints while the button is held, rather than dropping corners.
            if (Draft.Form.Kind == LandformKind.Brush)
            {
                if (mouse.leftButton.wasPressedThisFrame && hasHover)
                {
                    if (!Draft.Any)
                        Draft.Begin(LandformKind.Brush, _tools.TargetHeight);
                    Draft.Form.Height = _tools.TargetHeight;   // what Flatten pulls towards
                }

                if (mouse.leftButton.isPressed && hasHover)
                    Draft.Dab(at, _tools.BrushRadius, BrushMode, BrushAmount);
                return;
            }

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

            // T takes up the stamp, and again steps through the library (Shift back).
            if (keyboard.tKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
            {
                if (Draft.Form.Kind != LandformKind.Stamp)
                    Pick(LandformKind.Stamp);
                else
                    NextStamp(keyboard.shiftKey.isPressed ? -1 : 1);
                return true;
            }

            if (Draft.Form.Kind == LandformKind.Stamp && StampKeys(keyboard))
                return true;

            // Ctrl+Z after a god stamp takes the ground back; with none to take back it falls through
            // to the designation undo as ever.
            if (keyboard.zKey.wasPressedThisFrame && keyboard.ctrlKey.isPressed && !keyboard.shiftKey.isPressed
                && Draft.Form.Kind == LandformKind.Stamp && _godEdits.Count > 0)
            {
                var last = _godEdits[_godEdits.Count - 1];
                _godEdits.RemoveAt(_godEdits.Count - 1);
                last.Undo(Grid);
                _plannedVersion = -1;
                _tools.Say($"God stamp taken back: {last.Cells} cells as they were");
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

            // What the shape is for. A drawn outline keeps its corners when the kind changes, so you
            // can draw a pad and decide afterwards that it is a pit.
            if (keyboard.aKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
                return SetKind(LandformKind.Area, "Area — levelled to H");
            if (keyboard.hKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
                return SetKind(LandformKind.Heap, "Heap — spoil goes here, crowned at H");
            if (keyboard.pKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
                return SetKind(LandformKind.Pit, "Pit — dug in benches down to H");
            if (keyboard.rKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
                return SetKind(LandformKind.Ribbon, "Ribbon — a route cut at H; change H between clicks to grade it");

            // B picks the brush, and picks again through what it does. One key rather than four,
            // because the readout says which it is on and the panel has no room for a mode switch.
            if (keyboard.bKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
            {
                if (Draft.Form.Kind != LandformKind.Brush)
                    return SetKind(LandformKind.Brush, "Brush — raise. Hold the button and paint; B again for the next");
                BrushMode = BrushMode switch
                {
                    BrushMode.Raise => BrushMode.Lower,
                    BrushMode.Lower => BrushMode.Smooth,
                    BrushMode.Smooth => BrushMode.Flatten,
                    _ => BrushMode.Raise,
                };
                _tools.Say($"Brush — {BrushMode.ToString().ToLowerInvariant()}"
                           + (BrushMode == BrushMode.Flatten ? ", towards H" : ""));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Sizing a stamp in hand: [ and ] scale it, comma and full stop turn it 15°, PageUp and
        /// PageDown make it taller or shorter by a height step, I turns it upside down. The same keys
        /// as the brush and H elsewhere, so a hand that knows those knows these.
        /// </summary>
        bool StampKeys(Keyboard keyboard)
        {
            var step = Grid != null ? Mathf.Max(Grid.HeightStep, 0.25f) : 0.5f;
            if (keyboard.leftBracketKey.wasPressedThisFrame)
                Draft.ScaleStamp(1f / 1.15f);
            else if (keyboard.rightBracketKey.wasPressedThisFrame)
                Draft.ScaleStamp(1.15f);
            else if (keyboard.commaKey.wasPressedThisFrame)
                Draft.TurnStamp(-15f);
            else if (keyboard.periodKey.wasPressedThisFrame)
                Draft.TurnStamp(15f);
            else if (keyboard.pageUpKey.wasPressedThisFrame)
                Draft.RaiseStamp(step * 2f);
            else if (keyboard.pageDownKey.wasPressedThisFrame)
                Draft.RaiseStamp(-step * 2f);
            else if (keyboard.gKey.wasPressedThisFrame && !keyboard.ctrlKey.isPressed)
            {
                GodMode = !GodMode;
                _tools.Say(GodMode ? "God mode: stamps shape the ground at once, no crew" : "God mode off: stamps are orders for the crew");
            }
            else if (keyboard.iKey.wasPressedThisFrame)
            {
                Draft.FlipStamp();
                _tools.Say(Draft.Form.Placement.Invert ? "Stamp upside down: a hollow" : "Stamp the right way up");
            }
            else
                return false;
            return true;
        }

        bool SetKind(LandformKind kind, string says)
        {
            Draft.SetKind(kind);
            _tools.Say(says);
            return true;
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

            if (GodMode && Draft.Form.Kind == LandformKind.Stamp)
            {
                GodStamp();
                return;
            }
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
            // Surface first: it clears the owner map and fills in the areas, then the zones add the
            // heaps and pits on top. Between them every cell the plan claims knows which shape
            // claimed it, which is what a posted crew reads.
            Plan.Surface(Grid, _surface, _owners);
            Plan.Zones(Grid, _caps, _floors, _owners);
            Builder.CommitZones(_caps, _floors);
            Builder.CommitShapes(_owners);
            _tools.History?.Commit();

            Blueprints.Volumes(_committedCells, out var cut, out var fill, Grid.CellArea);
            _tools.Say(form.Kind switch
            {
                LandformKind.Heap => $"Heap committed: holds {LandformProfile.Capacity(Grid, _caps):0.#} m³",
                LandformKind.Pit => $"Pit committed: {LandformProfile.Reserves(Grid, _floors):0.#} m³ in reserve",
                _ => $"Shape committed: {made} cells of work, cut {cut:0.#} m³, fill {fill:0.#} m³",
            });
        }

        /// <summary>
        /// The stamp in hand, straight into the ground. It stays in hand for the next one, and the
        /// ghost is worked out again against the ground as it now is.
        /// </summary>
        void GodStamp()
        {
            var edit = GroundStamp.Apply(Grid, Draft.Form.ResolveStamp(), Draft.Form.Placement);
            if (edit.Cells == 0)
            {
                _tools.Say("God stamp: nothing to change here");
                return;
            }

            // Land raised out of a sea pushes the sea aside rather than lifting it.
            WaterDisplacement.KeepSurface(_terrain, edit.Changes);
            _godEdits.Add(edit);
            if (_godEdits.Count > GodUndoDepth)
                _godEdits.RemoveAt(0);
            _plannedVersion = -1;
            _tools.Say($"God stamp: {edit.Cells} cells, raised {edit.Raised:0.#} m³, lowered {edit.Lowered:0.#} m³   (Ctrl+Z takes it back)");
        }

        /// <summary>Which shape covers the cell under the cursor, or 0 for bare ground.</summary>
        public int ShapeAt(Vector2 at)
        {
            var grid = Grid;
            if (grid == null)
                return 0;
            var x = Mathf.FloorToInt(at.x);
            var z = Mathf.FloorToInt(at.y);
            return grid.InBounds(x, z) && _tools.Map != null ? _tools.Map.ShapeAt(x, z) : 0;
        }

        /// <summary>Takes the shape under the cursor out of the plan, and its orders with it.</summary>
        public bool RemoveAt(Vector2 at)
        {
            var grid = Grid;
            if (grid == null)
                return false;
            var id = ShapeAt(at);
            if (id == 0)
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
            _profile.Clear();
            Cut = Fill = 0f;
            Capacity = Reserves = 0f;
            Benches = 0;
            Cells = 0;
            if (!Draft.CanCommit)
                return;

            var one = new LandformPlan();
            one.Add(Draft.Form);

            // A heap and a pit are shapes for spoil and for material, not work to be done: they have
            // a profile and a number, not a cut and a fill.
            if (Draft.Form.Kind == LandformKind.Heap || Draft.Form.Kind == LandformKind.Pit)
            {
                LandformSpline.Polygon(Draft.Form, LandformPlan.SampleSpacing, _samples, _outline);
                LandformRaster.Fill(Grid, _outline, _rim);
                LandformRaster.Inside(Grid, _rim, _inside);
                if (Draft.Form.Kind == LandformKind.Heap)
                {
                    LandformProfile.Heap(Grid, Draft.Form, _inside, _profile);
                    Capacity = LandformProfile.Capacity(Grid, _profile);
                }
                else
                {
                    LandformProfile.Pit(Grid, Draft.Form, _inside, _profile);
                    Reserves = LandformProfile.Reserves(Grid, _profile);
                    Benches = LandformProfile.Benches(Grid, Draft.Form, _profile);
                }

                Cells = _profile.Count;
                return;
            }

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

                // A heap's crown or a pit's benches, drawn at the height the profile asks for — the
                // cone or the steps, not a flat sheet at one number.
                var heap = Draft.Form.Kind == LandformKind.Heap;
                foreach (var pair in _profile)
                {
                    var x = pair.Key % Grid.Width;
                    var z = pair.Key / Grid.Width;
                    var top = pair.Value + 0.06f;
                    DesignationsView.AddTile(x, z, top, top, top, top,
                        heap ? HeapColor : PitColor, _vertices, _colors, _triangles, cell);
                }

                // A stamp's reach, a ring on the ground, so a flat stretch of it still shows its edge.
                if (Draft.Form.Kind == LandformKind.Stamp && Draft.CanCommit)
                    StampRing(Draft.Form.Placement, GodMode ? GodColor : OutlineColor, cell);

                // The outline itself, on the ground it is drawn round, and its corners. A stroke has
                // neither: the tiles above are the whole of what it is.
                if (Draft.Any && Draft.Form.Kind != LandformKind.Brush && Draft.Form.Kind != LandformKind.Stamp)
                {
                    Edge(Draft.Form, Draft.Form.Height + 0.15f, 0.12f, OutlineColor, cell);
                    var nodes = Draft.Form.Nodes;
                    for (var i = 0; i < nodes.Count; i++)
                        GhostMesh.Disc(nodes[i].Position, GroundAt(nodes[i].Position) + 0.2f, 0.55f,
                            i == nodes.Count - 1 ? ActiveNodeColor : OutlineColor,
                            _vertices, _colors, _triangles, cell);
                }

                // Shapes already in the plan, so they can be found again.
                foreach (var form in Plan.Forms)
                {
                    if (!form.IsDrawn || form.Kind == LandformKind.Brush)
                        continue;
                    if (form.Kind == LandformKind.Stamp)
                    {
                        StampRing(form.Placement, BuiltColor, cell);
                        continue;
                    }

                    Edge(form, form.Height + 0.1f, 0.08f, BuiltColor, cell);
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        /// <summary>
        /// A shape's edge: the loop round a closed one, or the centre line along a ribbon. A ribbon
        /// drawn as a loop would close itself back to the start, which is not a route.
        /// </summary>
        void Edge(Landform form, float height, float half, Color32 colour, float cell)
        {
            if (form.Kind == LandformKind.Ribbon)
            {
                RoadSpline.Sample(form.Nodes, RoadPlanner.SampleSpacing, _samples);
                GhostMesh.Ribbon(_samples, form.Width * 0.5f, 0.08f, _ => colour,
                    _vertices, _colors, _triangles, cell);
                return;
            }

            LandformSpline.Polygon(form, LandformPlan.SampleSpacing, _samples, _outline);
            // On the ground it is drawn round, not at the shape's own height: a pad across a slope
            // would otherwise have its outline hanging in the air on one side and buried on the
            // other, and stop reading as the edge of the thing it belongs to.
            GhostMesh.LoopOnGround(_outline, at => GroundAt(at) + 0.15f, half, colour,
                _vertices, _colors, _triangles, cell);
        }

        /// <summary>The circle a stamp reaches, on the ground, with a tick showing which way it faces.</summary>
        void StampRing(StampPlacement placement, Color32 colour, float cell)
        {
            const int Segments = 48;
            var radius = placement.Size * 0.5f / cell;
            _outline.Clear();
            for (var i = 0; i < Segments; i++)
            {
                var angle = i * Mathf.PI * 2f / Segments;
                _outline.Add(placement.Centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }

            GhostMesh.LoopOnGround(_outline, at => GroundAt(at) + 0.15f, 0.1f, colour, _vertices, _colors, _triangles, cell);
            // The stamp's own east, turned clockwise as the placement is.
            var radians = placement.Rotation * Mathf.Deg2Rad;
            var facing = placement.Centre + new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians)) * radius;
            GhostMesh.Disc(facing, GroundAt(facing) + 0.2f, 0.6f, ActiveNodeColor, _vertices, _colors, _triangles, cell);
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
            var says = Draft.Form.Kind switch
            {
                // A heap's number is what it holds before it starts spreading, not a limit: the
                // crown is what you drew, and spoil past it piles on and runs out at its own angle.
                LandformKind.Heap => $"heap holds {Capacity:0.#} m³ before it spreads   area {area:0.#} m²",
                LandformKind.Pit => $"pit holds {Reserves:0.#} m³   {Benches} bench{(Benches == 1 ? "" : "es")}"
                                    + $"   area {area:0.#} m²",
                _ => $"cut {Cut:0.#} m³   fill {Fill:0.#} m³   "
                     + (Mathf.Abs(balance) < 0.05f ? "balanced"
                         : balance > 0f ? $"spoil {balance:0.#} m³ to tip"
                         : $"needs {-balance:0.#} m³")
                     + $"   area {area:0.#} m²",
            };
            if (Draft.Form.Kind == LandformKind.Stamp)
            {
                var placement = Draft.Form.Placement;
                var stamp = Draft.Form.ResolveStamp();
                says = $"{(stamp != null ? stamp.DisplayName : "?")}  {placement.Size:0} m across, {placement.Height:0.#} m"
                       + (placement.Invert ? " deep" : " tall") + $", turned {placement.Rotation:0}°   "
                       + (GodMode ? $"raises about {Fill:0.#} m³, lowers {Cut:0.#} m³   area {area:0.#} m²" : says);
            }

            GUI.Label(new Rect(12f, Screen.height - 76f, 900f, 26f), says, _label);
            GUI.Label(new Rect(12f, Screen.height - 48f, 900f, 26f),
                Draft.Form.Kind == LandformKind.Stamp
                    ? (GodMode ? "GOD MODE: click shapes the ground now   •   Ctrl+Z takes it back   •   G for crew orders"
                                : "Click to place   •   G god mode")
                      + "   •   T next stamp   •   [ ] size   •   , . turn   •   PgUp/PgDn height   •   I upside down"
                    : Draft.Form.Kind == LandformKind.Brush
                    ? $"Hold the button and paint   •   B changes what it does ({BrushMode.ToString().ToLowerInvariant()})"
                      + "   •   [ and ] resize it   •   Enter commits"
                    : Draft.CanCommit
                        ? "Enter commits   •   A area, H heap, P pit, R ribbon, B brush   •   C curves it   •   Backspace undoes a corner"
                        : "Click to drop corners — three make a shape, two make a ribbon   •   A area, H heap, P pit, R ribbon, B brush",
                _label);
        }
    }
}
