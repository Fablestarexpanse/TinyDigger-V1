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
    /// The Road tool's hands and the island's roads (Slice 17 Part B). Holds the
    /// <see cref="RoadNetwork"/>, the <see cref="RoadBuilder"/> that turns roads into designations
    /// and paves them once built, and the <see cref="RoadDraft"/> being drawn or edited. Draws the
    /// ghost: the ribbon coloured by grade (green, orange over the limit, red over twice it), its
    /// centre line, the cut faces and embankments the ground will settle into, the nodes, their
    /// handles and each segment's grade.
    ///
    /// Mouse, with the Road tool: click places a node (onto a road's node to join it); drag a node
    /// or its handle to reshape; scroll over a node raises or lowers it a metre (Shift: 0.25 m);
    /// double-click or Enter lays the road; Backspace takes the last node back; Escape drops the
    /// draft. With no draft, clicking a road picks it up to edit, and Delete removes it.
    /// </summary>
    public sealed class RoadsHost : MonoBehaviour
    {
        const float NodePickRadius = 0.9f;
        const float HandlePickRadius = 0.6f;
        const float DoubleClickSeconds = 0.35f;
        const int FaceReach = 14;

        static readonly Color32 FineColor = new Color32(120, 230, 160, 120);
        static readonly Color32 SteepColor = new Color32(255, 150, 40, 150);
        static readonly Color32 RefusedColor = new Color32(235, 60, 50, 170);
        static readonly Color32 CutFaceColor = new Color32(215, 150, 95, 95);
        static readonly Color32 FillFaceColor = new Color32(110, 165, 235, 95);
        static readonly Color32 NodeColor = new Color32(255, 255, 255, 230);
        static readonly Color32 ActiveNodeColor = new Color32(255, 220, 60, 240);
        static readonly Color32 BuiltColor = new Color32(250, 240, 200, 150);

        PlayerTools _tools;
        TerrainView _terrain;
        Mesh _mesh;
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();

        readonly List<RoadSample> _samples = new List<RoadSample>();
        readonly List<PlannedCell> _footprint = new List<PlannedCell>();
        readonly List<PlannedCell> _faces = new List<PlannedCell>();
        readonly List<Vector2Int> _bed = new List<Vector2Int>();
        readonly List<float> _grades = new List<float>();
        int _plannedVersion = -1;

        int _dragNode = -1;
        int _dragHandle = -1;
        float _lastClickTime;
        Vector2 _lastClickAt;
        DesignationMap _builtFor;

        public RoadNetwork Network { get; private set; } = new RoadNetwork();

        public RoadBuilder Builder { get; private set; }

        public RoadDraft Draft { get; } = new RoadDraft();

        /// <summary>The draft node last placed, dragged or scrolled: what the panel's lock toggle acts on.</summary>
        public int ActiveNode { get; private set; } = -1;

        /// <summary>The draft's cut and fill in m³, the road itself and the faces the slump will leave.</summary>
        public float Cut { get; private set; }

        public float Fill { get; private set; }

        /// <summary>Each segment's steepest grade, as a fraction, and how the draft is judged.</summary>
        public IReadOnlyList<float> Grades => _grades;

        public RoadGradeState State { get; private set; }

        public bool IsDrawing => !Draft.IsEmpty;

        public void Init(PlayerTools tools, TerrainView terrain, Material material)
        {
            _tools = tools;
            _terrain = terrain;
            _mesh = new Mesh { name = "Road Ghost" };
            _mesh.MarkDynamic();
            var holder = new GameObject("Road Ghost") { hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(terrain.transform, false);
            holder.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = holder.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            Network.Changed += Replan;
            terrain.Regenerated += Forget;
        }

        void OnDestroy()
        {
            if (_terrain != null)
                _terrain.Regenerated -= Forget;
        }

        /// <summary>A new island: no roads.</summary>
        void Forget()
        {
            Network.Changed -= Replan;
            Network = new RoadNetwork();
            Network.Changed += Replan;
            Builder?.Clear();
            Draft.Clear();
        }

        TerrainGrid Grid => _terrain.Grid;

        float GroundAt(Vector2 cells)
        {
            var grid = Grid;
            var x = Mathf.Clamp(Mathf.FloorToInt(cells.x), 0, grid.Width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(cells.y), 0, grid.Height - 1);
            return grid.IsGround(x, z) ? grid.GetSurfaceHeight(x, z) : World.SeaLevel;
        }

        // --- input, from PlayerTools while the Road tool is in hand ---------------------------------

        /// <summary>Handles the mouse over the ground; <paramref name="at"/> is the cursor in cells.</summary>
        public void HandleMouse(Mouse mouse, bool hasHover, Vector2 at)
        {
            var keyboard = Keyboard.current;
            var shift = keyboard != null && keyboard.shiftKey.isPressed;

            // Scroll over a node raises or lowers it, and the camera leaves the wheel alone.
            var hovered = hasHover ? Draft.NodeNear(at, NodePickRadius) : -1;
            if (hovered >= 0)
            {
                RtsCamera.ScrollCaptured = true;
                var scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    Draft.Raise(hovered, Mathf.Sign(scroll) * (shift ? 0.25f : 1f));
                    ActiveNode = hovered;
                    _tools.Say($"Node {hovered + 1} at {Draft.Nodes[hovered].Height:0.##} m");
                }
            }

            if (mouse.leftButton.wasPressedThisFrame && hasHover)
                Press(at);
            if (mouse.leftButton.isPressed && hasHover)
            {
                if (_dragNode >= 0)
                    Draft.Move(_dragNode, at, GroundAt);
                else if (_dragHandle >= 0)
                    Draft.DragHandle(_dragHandle, at);
            }

            if (mouse.leftButton.wasReleasedThisFrame)
            {
                _dragNode = -1;
                _dragHandle = -1;
            }
        }

        void Press(Vector2 at)
        {
            var doubleClick = Time.unscaledTime - _lastClickTime < DoubleClickSeconds && Vector2.Distance(at, _lastClickAt) < 1.5f;
            _lastClickTime = Time.unscaledTime;
            _lastClickAt = at;

            if (!Draft.IsEmpty)
            {
                // The first click of a double-click placed the last node; the second lays the road.
                if (doubleClick && Draft.Nodes.Count >= 2)
                {
                    Commit();
                    return;
                }

                var handle = Draft.HandleNear(at, HandlePickRadius);
                var node = Draft.NodeNear(at, NodePickRadius);
                if (handle >= 0 && (node < 0 || Vector2.Distance(Draft.HandleTip(handle), at) < Vector2.Distance(Draft.Nodes[node].Position, at)))
                {
                    _dragHandle = handle;
                    ActiveNode = handle;
                    return;
                }

                if (node >= 0)
                {
                    _dragNode = node;
                    ActiveNode = node;
                    return;
                }
            }
            else
            {
                // No draft: a click on a road picks it up to edit.
                var road = RoadAt(at);
                if (road != 0)
                {
                    Draft.Load(Network, road);
                    ActiveNode = -1;
                    _tools.Say($"Editing road {road}: drag its nodes, Enter to rebuild, Delete to remove it");
                    return;
                }
            }

            Draft.Place(at, GroundAt, Network);
            ActiveNode = Draft.Nodes.Count - 1;
            _tools.Say($"Road: {Draft.Nodes.Count} node{(Draft.Nodes.Count == 1 ? "" : "s")}; double-click or Enter to lay it");
        }

        /// <summary>The road whose centre line passes within its half width (plus a cell) of a point, or 0.</summary>
        int RoadAt(Vector2 at)
        {
            var samples = new List<RoadSample>();
            foreach (var road in Network.Roads())
            {
                RoadSpline.Sample(Network.Chain(road), 0.5f, samples);
                var reach = Network.WidthOf(road) * 0.5f + 1f;
                foreach (var sample in samples)
                    if (Vector2.Distance(new Vector2(sample.Position.x, sample.Position.z), at) <= reach)
                        return road;
            }

            return 0;
        }

        /// <summary>Keys for the Road tool. Returns true for any it used, so PlayerTools does not also act on them.</summary>
        public bool HandleKeys(Keyboard keyboard)
        {
            if (keyboard == null)
                return false;
            var shift = keyboard.shiftKey.isPressed;
            var step = shift ? 0.25f : 1f;
            var target = ActiveNode >= 0 && ActiveNode < Draft.Nodes.Count ? ActiveNode : Draft.Nodes.Count - 1;

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                Commit();
                return true;
            }

            if (keyboard.backspaceKey.wasPressedThisFrame && !Draft.IsEmpty)
            {
                Draft.RemoveLast();
                ActiveNode = Draft.Nodes.Count - 1;
                return true;
            }

            if (keyboard.deleteKey.wasPressedThisFrame && Draft.EditingRoad != 0)
            {
                var road = Draft.EditingRoad;
                Draft.Clear();
                Builder?.RemoveRoad(road);
                Network.RemoveRoad(road);
                _tools.Say($"Removed road {road}");
                return true;
            }

            if (target >= 0 && keyboard.pageUpKey.wasPressedThisFrame)
            {
                Draft.Raise(target, step);
                return true;
            }

            if (target >= 0 && keyboard.pageDownKey.wasPressedThisFrame)
            {
                Draft.Raise(target, -step);
                return true;
            }

            return false;
        }

        /// <summary>Drops the draft; an edit leaves the road as it was built.</summary>
        public void Cancel()
        {
            Draft.Clear();
            ActiveNode = -1;
            _dragNode = _dragHandle = -1;
        }

        /// <summary>Locks the active node to the ground, or frees it (the panel's toggle).</summary>
        public void SetActiveLocked(bool locked)
        {
            if (ActiveNode >= 0 && ActiveNode < Draft.Nodes.Count)
                Draft.SetLocked(ActiveNode, locked, GroundAt);
        }

        public bool ActiveLocked => ActiveNode >= 0 && ActiveNode < Draft.Nodes.Count && Draft.Nodes[ActiveNode].LockToGround;

        /// <summary>Lays the draft as a road, or refuses it if a segment is over twice the max grade.</summary>
        public bool Commit()
        {
            if (Draft.Nodes.Count < 2)
                return false;
            if (!Draft.CanCommit(Grid.CellSize))
            {
                _tools.Say($"Too steep to build: a segment is over {Draft.MaxGrade * 200f:0}% (twice the {Draft.MaxGrade * 100f:0}% limit)");
                return false;
            }

            var editing = Draft.EditingRoad;
            var road = Draft.Commit(Network, Grid.CellSize);
            if (road == 0)
                return false;
            var spoil = Cut - Fill;
            var map = _tools.Map;
            _tools.Say((editing != 0 ? $"Road {road} rebuilt" : $"Road {road} laid")
                + (spoil > 1f && map != null && map.DumpZoneCount == 0
                    ? $": it makes {spoil:0} m³ of spoil and there is no Dump Zone to tip it in"
                    : ""));
            ActiveNode = -1;
            return true;
        }

        // --- planning and building ---------------------------------------------------------------

        void EnsureBuilder()
        {
            var map = _tools.Map;
            if (map == null || ReferenceEquals(map, _builtFor))
                return;
            _builtFor = map;
            Builder = new RoadBuilder(Grid, map);
        }

        /// <summary>Turns a road in the network into designations, or takes them away if it is gone.</summary>
        void Replan(int road)
        {
            EnsureBuilder();
            if (Builder == null)
                return;
            var chain = Network.Chain(road);
            if (chain.Count < 2)
            {
                Builder.RemoveRoad(road);
                return;
            }

            var samples = new List<RoadSample>();
            RoadSpline.Sample(chain, RoadPlanner.SampleSpacing, samples);
            var footprint = new List<PlannedCell>();
            var bed = new List<Vector2Int>();
            RoadPlanner.Footprint(Grid, samples, Network.WidthOf(road), footprint, bed);
            Builder.PlanRoad(road, footprint, bed);
        }

        void Update()
        {
            if (Grid == null || _tools == null)
                return;
            EnsureBuilder();
            Builder?.Tick();
            PlanDraft();
            DrawGhost();
        }

        void PlanDraft()
        {
            if (Draft.Version == _plannedVersion)
                return;
            _plannedVersion = Draft.Version;
            _footprint.Clear();
            _faces.Clear();
            Cut = Fill = 0f;
            State = Draft.Grades(Grid.CellSize, _grades);
            if (Draft.Nodes.Count < 2)
                return;

            RoadSpline.Sample(Draft.Nodes, RoadPlanner.SampleSpacing, _samples);
            RoadPlanner.Footprint(Grid, _samples, Draft.Width, _footprint, _bed, includeSettled: true);
            RoadPlanner.Settle(Grid, _footprint, MaterialTable.DirtLoose, FaceReach, _faces);
            Blueprints.Volumes(_footprint, out var cut, out var fill, Grid.CellArea);
            Blueprints.Volumes(_faces, out var faceCut, out var faceFill, Grid.CellArea);
            Cut = cut + faceCut;
            Fill = fill + faceFill;
        }

        // --- the ghost ---------------------------------------------------------------------------

        void DrawGhost()
        {
            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            var cell = Grid.CellSize;
            var roadTool = _tools.Mode == ToolMode.Road;

            if (roadTool)
            {
                // Built roads' centre lines, so they can be found and picked up.
                var samples = new List<RoadSample>();
                foreach (var road in Network.Roads())
                {
                    if (road == Draft.EditingRoad)
                        continue;
                    RoadSpline.Sample(Network.Chain(road), 0.5f, samples);
                    Ribbon(samples, 0.25f, 0.12f, _ => BuiltColor, cell);
                }
            }

            if (Draft.Nodes.Count >= 2)
            {
                // Faces first, under the ribbon.
                // A cut face is drawn on the ground it will take away, an embankment where it will
                // stand; drawn at a cut's settled height it would be buried in the hill.
                foreach (var face in _faces)
                {
                    var top = Mathf.Max(face.Height, Grid.GetSurfaceHeight(face.X, face.Z)) + 0.05f;
                    DesignationsView.AddTile(face.X, face.Z, top, top, top, top,
                        face.IsDig ? CutFaceColor : FillFaceColor, _vertices, _colors, _triangles, cell);
                }
                Ribbon(_samples, Draft.Width * 0.5f, 0.08f, SegmentColor, cell);
                Ribbon(_samples, 0.12f, 0.1f, _ => NodeColor, cell);
            }

            for (var i = 0; i < Draft.Nodes.Count; i++)
            {
                var node = Draft.Nodes[i];
                var active = i == ActiveNode;
                Disc(node.Position, node.Height + 0.15f, 0.55f, active ? ActiveNodeColor : NodeColor, cell);
                if (Draft.Nodes.Count >= 2)
                {
                    var tip = Draft.HandleTip(i);
                    Line(node.Position, tip, node.Height + 0.14f, 0.06f, NodeColor, cell);
                    Disc(tip, node.Height + 0.15f, 0.3f, node.HasHandle ? ActiveNodeColor : NodeColor, cell);
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        Color32 SegmentColor(int segment)
        {
            if (segment < 0 || segment >= _grades.Count)
                return FineColor;
            return RoadPlanner.Judge(_grades[segment], Draft.MaxGrade) switch
            {
                RoadGradeState.Refused => RefusedColor,
                RoadGradeState.Steep => SteepColor,
                _ => FineColor,
            };
        }

        /// <summary>A band <paramref name="half"/> cells either side of the samples, <paramref name="lift"/> m above the road's height.</summary>
        void Ribbon(List<RoadSample> samples, float half, float lift, System.Func<int, Color32> color, float cell)
        {
            if (samples.Count < 2)
                return;
            var start = _vertices.Count;
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                var across = new Vector2(-s.Direction.y, s.Direction.x) * half;
                // On the road bed, or on the ground where the road is cut into it, so the ribbon
                // reads as one band instead of vanishing into the hill it will cut.
                var ground = GroundAt(new Vector2(s.Position.x, s.Position.z));
                var height = Mathf.Max(s.Position.y, ground) + lift;
                _vertices.Add(new Vector3((s.Position.x - across.x) * cell, height, (s.Position.z - across.y) * cell));
                _vertices.Add(new Vector3((s.Position.x + across.x) * cell, height, (s.Position.z + across.y) * cell));
                var c = color(s.Segment);
                _colors.Add(c);
                _colors.Add(c);
                if (i == 0)
                    continue;
                var b = start + (i - 1) * 2;
                // Both faces: seen from below a cut, the ribbon still shows.
                _triangles.Add(b); _triangles.Add(b + 2); _triangles.Add(b + 1);
                _triangles.Add(b + 1); _triangles.Add(b + 2); _triangles.Add(b + 3);
                _triangles.Add(b); _triangles.Add(b + 1); _triangles.Add(b + 2);
                _triangles.Add(b + 1); _triangles.Add(b + 3); _triangles.Add(b + 2);
            }
        }

        void Disc(Vector2 at, float height, float radius, Color32 color, float cell)
        {
            const int segments = 20;
            var start = _vertices.Count;
            _vertices.Add(new Vector3(at.x * cell, height, at.y * cell));
            _colors.Add(color);
            for (var i = 0; i <= segments; i++)
            {
                var a = i * Mathf.PI * 2f / segments;
                _vertices.Add(new Vector3((at.x + Mathf.Cos(a) * radius) * cell, height, (at.y + Mathf.Sin(a) * radius) * cell));
                _colors.Add(color);
                if (i == 0)
                    continue;
                _triangles.Add(start); _triangles.Add(start + i + 1); _triangles.Add(start + i);
                _triangles.Add(start); _triangles.Add(start + i); _triangles.Add(start + i + 1);
            }
        }

        void Line(Vector2 from, Vector2 to, float height, float half, Color32 color, float cell)
        {
            var along = to - from;
            if (along.sqrMagnitude < 1e-6f)
                return;
            var across = new Vector2(-along.y, along.x).normalized * half;
            var start = _vertices.Count;
            _vertices.Add(new Vector3((from.x - across.x) * cell, height, (from.y - across.y) * cell));
            _vertices.Add(new Vector3((from.x + across.x) * cell, height, (from.y + across.y) * cell));
            _vertices.Add(new Vector3((to.x - across.x) * cell, height, (to.y - across.y) * cell));
            _vertices.Add(new Vector3((to.x + across.x) * cell, height, (to.y + across.y) * cell));
            for (var i = 0; i < 4; i++)
                _colors.Add(color);
            _triangles.Add(start); _triangles.Add(start + 2); _triangles.Add(start + 1);
            _triangles.Add(start + 1); _triangles.Add(start + 2); _triangles.Add(start + 3);
            _triangles.Add(start); _triangles.Add(start + 1); _triangles.Add(start + 2);
            _triangles.Add(start + 1); _triangles.Add(start + 3); _triangles.Add(start + 2);
        }

        // --- grade labels ---------------------------------------------------------------------

        GUIStyle _label;

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || Draft.Nodes.Count < 2 || _tools.Mode != ToolMode.Road || _samples.Count < 2)
                return;
            var camera = _tools.Camera;
            if (camera == null)
                return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
                _label.normal.textColor = Color.white;
            }

            var cell = Grid.CellSize;
            var terrain = _terrain.transform;
            for (var segment = 0; segment < _grades.Count; segment++)
            {
                // The label sits on the middle of its segment.
                var first = _samples.FindIndex(s => s.Segment == segment);
                var last = _samples.FindLastIndex(s => s.Segment == segment);
                if (first < 0)
                    continue;
                var middle = _samples[(first + last) / 2].Position;
                var world = terrain.TransformPoint(new Vector3(middle.x * cell, middle.y + 1.2f, middle.z * cell));
                var screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                    continue;
                var state = RoadPlanner.Judge(_grades[segment], Draft.MaxGrade);
                var old = GUI.color;
                GUI.color = state == RoadGradeState.Refused ? new Color(1f, 0.45f, 0.4f)
                    : state == RoadGradeState.Steep ? new Color(1f, 0.75f, 0.35f) : Color.white;
                GUI.Box(new Rect(screen.x - 34f, Screen.height - screen.y - 13f, 68f, 26f), $"{_grades[segment] * 100f:0.#}%", _label);
                GUI.color = old;
            }
        }
    }
}
