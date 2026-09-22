using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Scene side of the crew: it owns the <see cref="JobDispatcher"/> the units share, spawns
    /// them, ticks them and draws a body and the path of each. The body is the crew robot prefab,
    /// playing the clip <see cref="CrewAnimation"/> picks, or a placeholder box when no prefab is
    /// set. It holds the selection the RTS controls work on: click or box to select, then send
    /// the selection somewhere with <see cref="OrderSelectedTo"/>. Rings mark the selected units
    /// and, briefly, where they were sent; only selected units show their paths. Escape clears
    /// the selection. No logic of its own.
    /// </summary>
    public sealed class CrewView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] DesignationsView _designations;
        [SerializeField] Material _pathMaterial;
        [Tooltip("Metres from the middle of the disc the crew would like to start. It starts on the nearest level, dry ground to that.")]
        [SerializeField] Vector2 _spawnOffset;

        [Tooltip("How many worker robots to spawn: the first unit, digging and hauling barrow loads itself.")]
        [SerializeField, Min(0)] int _workerCount = 4;

        [Tooltip("Loose m³ a worker's barrow holds. One scoop of any ground is up to 0.19 m³ loose.")]
        [SerializeField, Min(0.01f)] float _workerCapacity = UnitLoads.Barrow;

        [Tooltip("A worker's speed, metres per second.")]
        [SerializeField, Min(0.1f)] float _workerSpeed = 1.5f;

        [Tooltip("How many diggers to spawn, side by side across the spawn cell.")]
        [SerializeField, Min(0)] int _diggerCount;

        [Tooltip("How many haulers to spawn.")]
        [SerializeField, Min(0)] int _haulerCount;

        [Tooltip("Metres per second.")]
        [SerializeField, Min(0.1f)] float _speed = 3f;

        [Tooltip("Loose m³ a digger's scoop holds.")]
        [SerializeField, Min(0.01f)] float _capacity = UnitLoads.Scoop;

        [Tooltip("Loose m³ a hauler's bed holds.")]
        [SerializeField, Min(0.01f)] float _haulerCapacity = UnitLoads.Bed;

        [Tooltip("Seconds per height step dug, or per tip.")]
        [SerializeField, Min(0.01f)] float _workInterval = 0.4f;

        /// <summary>How far above or below its own cell a unit can dig or fill, in metres. Read every frame.</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("digReachLevels")]
        [Min(0f)] public float digReach = 2f;

        /// <summary>How far up a rock face a unit can work from its foot, in metres. Read every frame.</summary>
        [Min(0f)] public float cliffReach = 6f;

        /// <summary>Largest height change a unit can drive across between neighbouring cells, in metres.</summary>
        [Min(0f)] public float maxStepHeight = 1f;

        /// <summary>Take designated ground down in drivable layers, so upper cells stay workable.</summary>
        public bool benching = true;

        /// <summary>Let the crew cut ramps to work it cannot otherwise reach.</summary>
        public bool autoRamp = true;

        // Bodies fit inside one cell: units keep a cell between their centres, so anything
        // longer than that overlaps its neighbour when they work side by side.
        [SerializeField] Vector3 _bodySize = new Vector3(0.7f, 0.55f, 0.95f);
        [SerializeField] Vector3 _haulerBodySize = new Vector3(0.85f, 0.7f, 0.95f);
        [SerializeField] Color _bodyColor = new Color(1f, 0.78f, 0.1f);
        [SerializeField] Color _haulerColor = new Color(0.35f, 0.55f, 0.9f);
        [SerializeField] Color _selectedColor = new Color(1f, 1f, 1f);

        [Tooltip("The crew robot (crew_unit.prefab), with an Animator holding Idle, Move, Work and Carry. Empty: placeholder boxes.")]
        [SerializeField] GameObject _bodyPrefab;

        [Tooltip("The digger machine (digger.prefab). Empty: the crew robot's body, or a box.")]
        [SerializeField] GameObject _diggerPrefab;

        [Tooltip("The dumper machine (dumper.prefab), for haulers. Empty: the crew robot's body, or a box.")]
        [SerializeField] GameObject _haulerPrefab;

        [Tooltip("Tint on the selected robot.")]
        [SerializeField] Color _selectedTint = new Color(1f, 0.95f, 0.55f);

        [Tooltip("Flat colour for the selection rings and the move marker (URP Unlit, transparent). Empty: no rings.")]
        [SerializeField] Material _markerMaterial;

        [Tooltip("Seconds the ring where units were sent stays up.")]
        [SerializeField, Min(0.1f)] float _orderMarkerSeconds = 0.8f;

        [Tooltip("Seconds to blend from one clip to the next.")]
        [SerializeField, Min(0f)] float _clipBlend = 0.15f;

        /// <summary>
        /// Size of the robot body; 1 is as modelled, human scale: a ball 0.285 m across floating
        /// 0.125 m up. Read every frame.
        /// </summary>
        [Min(0.05f)] public float bodyScale = 1f;

        /// <summary>
        /// Cells between machines where they start, from the room a machine actually takes
        /// (<see cref="CrewUnit.DiggerRadius"/>). The crew logic gives every unit one cell, but a
        /// machine is over a metre long on half-metre cells, so two of them a cell apart start
        /// inside one another.
        /// </summary>
        static int MachineSpacing => Mathf.CeilToInt(2f * CrewUnit.DiggerRadius) + 1;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        readonly List<CrewUnit> _units = new List<CrewUnit>();
        readonly List<Transform> _bodies = new List<Transform>();
        readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        readonly List<Renderer[]> _robotRenderers = new List<Renderer[]>();
        readonly List<Animator> _animators = new List<Animator>();
        readonly List<string> _clips = new List<string>();
        readonly List<bool> _tinted = new List<bool>();
        MaterialPropertyBlock _tint;
        readonly List<int> _selection = new List<int>();
        readonly List<GameObject> _rings = new List<GameObject>();
        Transform _orderMarker;
        Renderer _orderMarkerRenderer;
        float _orderMarkerAt = float.NegativeInfinity;
        Mesh _ringMesh;
        MaterialPropertyBlock _markerBlock;
        readonly List<LineRenderer> _lines = new List<LineRenderer>();
        readonly Vector3[] _pathPoints = new Vector3[512];

        GridPathfinder _pathfinder;

        public JobDispatcher Dispatcher { get; private set; }

        public IReadOnlyList<CrewUnit> Units => _units;

        /// <summary>The first selected unit, for the readout, or null.</summary>
        public CrewUnit Selected => _selection.Count > 0 ? _units[_selection[0]] : null;

        /// <summary>How many units are selected.</summary>
        public int SelectedCount => _selection.Count;

        /// <summary>The first unit, for readouts that want something to show with nothing selected.</summary>
        public CrewUnit Unit => _units.Count > 0 ? _units[0] : null;

        void Start()
        {
            var grid = _terrain.Grid;
            _pathfinder = new GridPathfinder(grid) { MaxStepHeight = maxStepHeight };
            Dispatcher = new JobDispatcher(grid, _designations.Map, _pathfinder);

            var wish = (_terrain.DiscCentre + _spawnOffset) / grid.CellSize;
            var wishX = Mathf.Clamp(Mathf.FloorToInt(wish.x), 0, grid.Width - 1);
            var wishZ = Mathf.Clamp(Mathf.FloorToInt(wish.y), 0, grid.Height - 1);
            if (!CrewSpawn.TryFind(grid, wishX, wishZ, Mathf.Max(grid.Width, grid.Height) / 2, out var spawn))
            {
                Debug.LogWarning($"CrewView: no level, dry ground anywhere near ({wishX}, {wishZ}); the crew starts there anyway.", this);
                spawn = new Vector2Int(wishX, wishZ);
            }

            var spawnX = spawn.x;
            var spawnZ = spawn.y;
            var total = _workerCount + _diggerCount + _haulerCount;
            for (var i = 0; i < total; i++)
            {
                var role = i < _workerCount ? UnitRole.Worker
                    : i < _workerCount + _diggerCount ? UnitRole.Digger : UnitRole.Hauler;
                // Side by side, so no two start on the same cell — and the machines further out
                // than that, because a machine is over a metre long and two of them a metre apart
                // stand inside each other.
                var apart = role == UnitRole.Worker ? 2 : MachineSpacing;
                var x = Mathf.Clamp(spawnX + i % 2 * apart - apart / 2, 0, grid.Width - 1);
                var z = Mathf.Clamp(spawnZ + i / 2 * apart - apart / 2, 0, grid.Height - 1);
                var capacity = role == UnitRole.Worker ? _workerCapacity : role == UnitRole.Digger ? _capacity : _haulerCapacity;
                var unit = new CrewUnit(Dispatcher, x, z, role, capacity);
                // In the game a digger or a hauler is a machine, with a machine's room and, for
                // the digger, a rock cutter. The crew logic itself makes no such assumption.
                if (role != UnitRole.Worker)
                    unit.AsMachine();
                _units.Add(unit);

                var prefab = BodyFor(role);
                if (prefab != null)
                    AddRobot(prefab, role, i);
                else
                    AddBox(role, i, grid.CellSize);

                var line = new GameObject($"{role} {i} Path") { hideFlags = HideFlags.DontSave };
                line.transform.SetParent(transform, false);
                var pathLine = line.AddComponent<LineRenderer>();
                pathLine.sharedMaterial = _pathMaterial;
                pathLine.widthMultiplier = 0.06f;
                pathLine.startColor = pathLine.endColor = new Color(1f, 1f, 1f, 0.85f);
                pathLine.useWorldSpace = true;
                pathLine.positionCount = 0;
                _lines.Add(pathLine);
            }
        }

        void AddBox(UnitRole role, int i, float cellSize)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = $"{role} {i} Body";
            body.hideFlags = HideFlags.DontSave;
            body.transform.SetParent(transform, false);
            // A unit is a one-cell machine to the crew logic, so its body is sized to the cell.
            body.transform.localScale = (role == UnitRole.Digger ? _bodySize : _haulerBodySize) * cellSize;
            var bodyRenderer = body.GetComponent<MeshRenderer>();
            bodyRenderer.material.color = role == UnitRole.Digger ? _bodyColor : _haulerColor;
            _bodies.Add(body.transform);
            _renderers.Add(bodyRenderer);
            _robotRenderers.Add(null);
            _rings.Add(null);
            _animators.Add(null);
            _clips.Add(null);
            _tinted.Add(false);
        }

        /// <summary>
        /// The model for a role: each machine has its own now that the digger and the dumper are
        /// built (the crew robot stands in for anything not yet modelled, and a box for that).
        /// </summary>
        GameObject BodyFor(UnitRole role)
        {
            switch (role)
            {
                case UnitRole.Digger:
                    return _diggerPrefab != null ? _diggerPrefab : _bodyPrefab;
                case UnitRole.Hauler:
                    return _haulerPrefab != null ? _haulerPrefab : _bodyPrefab;
                default:
                    return _bodyPrefab;
            }
        }

        void AddRobot(GameObject prefab, UnitRole role, int i)
        {
            var body = Instantiate(prefab, transform, false);
            body.name = $"{role} {i} Body";
            body.hideFlags = HideFlags.DontSave;
            var renderers = body.GetComponentsInChildren<Renderer>();

            // A sphere round the model, for clicking; it scales with the body.
            var bounds = new Bounds(body.transform.position, Vector3.zero);
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            var hit = body.AddComponent<SphereCollider>();
            hit.center = body.transform.InverseTransformPoint(bounds.center);
            hit.radius = bounds.extents.magnitude * 0.6f;

            _bodies.Add(body.transform);
            _renderers.Add(null);
            _robotRenderers.Add(renderers);
            _animators.Add(body.GetComponent<Animator>());
            _clips.Add(null);
            _tinted.Add(false);
            _rings.Add(MakeRing($"{role} {i} Ring", body.transform, 0.22f, 0.26f));
        }

        /// <summary>A flat ring on the ground, hidden until wanted; null without a marker material.</summary>
        GameObject MakeRing(string name, Transform parent, float inner, float outer)
        {
            if (_markerMaterial == null)
                return null;
            _ringMesh ??= RingMesh(48);
            var ring = new GameObject(name) { hideFlags = HideFlags.DontSave };
            ring.transform.SetParent(parent, false);
            ring.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            ring.transform.localScale = new Vector3(outer, 1f, outer);
            ring.AddComponent<MeshFilter>().sharedMesh = _ringMesh;
            var ringRenderer = ring.AddComponent<MeshRenderer>();
            ringRenderer.sharedMaterial = _markerMaterial;
            ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.SetActive(false);
            return ring;
        }

        /// <summary>A unit-radius ring 20% thick, facing up.</summary>
        static Mesh RingMesh(int segments)
        {
            const float inner = 0.8f;
            var vertices = new Vector3[segments * 2];
            var triangles = new int[segments * 6];
            for (var s = 0; s < segments; s++)
            {
                var angle = 2f * Mathf.PI * s / segments;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices[s * 2] = direction * inner;
                vertices[s * 2 + 1] = direction;
                var next = (s + 1) % segments;
                var t = s * 6;
                triangles[t] = s * 2;
                triangles[t + 1] = next * 2;
                triangles[t + 2] = s * 2 + 1;
                triangles[t + 3] = s * 2 + 1;
                triangles[t + 4] = next * 2;
                triangles[t + 5] = next * 2 + 1;
            }

            var mesh = new Mesh { name = "Crew Ring", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void Update()
        {
            Dispatcher.Benching = benching;
            Dispatcher.AutoRamp = autoRamp;
            _pathfinder.MaxStepHeight = maxStepHeight;
            Dispatcher.Tick(Time.deltaTime);
            foreach (var unit in _units)
            {
                unit.DigReachLevels = Levels(digReach);
                unit.CliffReachLevels = Levels(cliffReach);
                unit.Speed = unit.Role == UnitRole.Worker ? _workerSpeed : _speed;
                unit.WorkInterval = _workInterval;
                unit.Tick(Time.deltaTime);
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                Deselect();
        }

        /// <summary>
        /// Selects the unit under the ray, if any: on its own, or added to the selection with
        /// <paramref name="add"/> (clicking a selected one then drops it). Returns whether a unit
        /// took the click, so the tools can leave it alone.
        /// </summary>
        public bool TrySelectAt(Ray ray, bool add = false)
        {
            if (!Physics.Raycast(ray, out var hit, 10000f))
                return false;
            var index = _bodies.IndexOf(hit.transform);
            if (index < 0)
                return false;
            if (!add)
                _selection.Clear();
            if (add && _selection.Contains(index))
                _selection.Remove(index);
            else
                _selection.Add(index);
            return true;
        }

        /// <summary>
        /// Selects every unit whose body is inside the screen rectangle (Input System pixels,
        /// origin bottom left); <paramref name="add"/> keeps the ones already selected. Returns how
        /// many are selected after.
        /// </summary>
        public int SelectInScreenRect(Rect box, Camera camera, bool add)
        {
            if (!add)
                _selection.Clear();
            for (var i = 0; i < _bodies.Count; i++)
            {
                // Aim at the ball, not the ground under it.
                var centre = _bodies[i].position + _bodies[i].up * (0.27f * bodyScale);
                if (SelectionBox.Contains(box, camera.WorldToScreenPoint(centre)) && !_selection.Contains(i))
                    _selection.Add(i);
            }

            return _selection.Count;
        }

        public void Deselect() => _selection.Clear();

        /// <summary>Selects unit <paramref name="index"/> of <see cref="Units"/>, on its own or added (Slice 17: the crew panel).</summary>
        public void Select(int index, bool add = false)
        {
            if (index < 0 || index >= _units.Count)
                return;
            if (!add)
                _selection.Clear();
            if (!_selection.Contains(index))
                _selection.Add(index);
        }

        public bool IsSelected(int index) => _selection.Contains(index);

        /// <summary>Where unit <paramref name="index"/>'s body is in the world, for the camera to go to.</summary>
        public Vector3 BodyPosition(int index) =>
            index >= 0 && index < _bodies.Count ? _bodies[index].position : Vector3.zero;

        /// <summary>
        /// Sends the selected units to the cell, spread over the nearest cells they can stand on,
        /// one each. Onto a dig, fill or dump designation they go back to work once there; onto
        /// anything else they hold. Returns how many took the order.
        /// </summary>
        public int OrderSelectedTo(int x, int z)
        {
            if (_selection.Count == 0)
                return 0;
            var grid = _terrain.Grid;
            var map = _designations.Map;
            var work = map.GetKind(x, z) != DesignationKind.None || map.IsDumpZone(x, z);
            var goal = new Vector2Int(x, z);
            var targets = CrewFormation.Targets(grid, goal, _selection.Count, grid.IsPassableGround);
            var starts = new List<Vector2>(_selection.Count);
            foreach (var index in _selection)
                starts.Add(_units[index].Position);
            var picks = CrewFormation.Assign(starts, targets, new Vector2(x + 0.5f, z + 0.5f));

            var ordered = 0;
            for (var s = 0; s < _selection.Count; s++)
                if (picks[s] >= 0 && _units[_selection[s]].OrderMoveTo(targets[picks[s]].x, targets[picks[s]].y, work))
                    ordered++;

            if (ordered > 0)
                ShowOrderMarker(grid, x, z);
            return ordered;
        }

        void ShowOrderMarker(TerrainGrid grid, int x, int z)
        {
            if (_orderMarker == null)
            {
                var marker = MakeRing("Order Marker", transform, 0.3f, 0.45f);
                if (marker == null)
                    return;
                _orderMarker = marker.transform;
                _orderMarkerRenderer = marker.GetComponent<Renderer>();
            }

            _orderMarker.gameObject.SetActive(true);
            _orderMarker.position = _terrain.transform.TransformPoint(
                TerrainSpace.CellCentre(grid, x, z, grid.GetSurfaceHeight(x, z) + 0.03f));
            _orderMarkerAt = Time.time;
        }

        void UpdateOrderMarker()
        {
            if (_orderMarker == null || !_orderMarker.gameObject.activeSelf)
                return;
            var t = (Time.time - _orderMarkerAt) / _orderMarkerSeconds;
            if (t >= 1f)
            {
                _orderMarker.gameObject.SetActive(false);
                return;
            }

            var radius = Mathf.Lerp(0.45f, 0.25f, t);
            _orderMarker.localScale = new Vector3(radius, 1f, radius);
            _markerBlock ??= new MaterialPropertyBlock();
            var colour = _markerMaterial.GetColor(BaseColorId);
            colour.a *= 1f - t;
            _markerBlock.SetColor(BaseColorId, colour);
            _orderMarkerRenderer.SetPropertyBlock(_markerBlock);
        }

        void LateUpdate()
        {
            UpdateOrderMarker();
            var terrainTransform = _terrain.transform;
            var grid = _terrain.Grid;
            var cellSize = grid.CellSize;
            for (var i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                // Unit positions are in cells; the terrain's local space is metres.
                var position = unit.Position * cellSize;
                var rotation = terrainTransform.rotation * Quaternion.Euler(0f, unit.Heading, 0f);
                if (_renderers[i] != null)
                {
                    var size = (unit.Role == UnitRole.Digger ? _bodySize : _haulerBodySize) * cellSize;
                    _bodies[i].SetPositionAndRotation(
                        terrainTransform.TransformPoint(new Vector3(position.x, unit.Height + size.y * 0.5f, position.y)), rotation);
                    _renderers[i].material.color = _selection.Contains(i) ? _selectedColor
                        : unit.Role == UnitRole.Digger ? _bodyColor : _haulerColor;
                }
                else
                {
                    // The robot's origin is the ground under it; it hovers by itself.
                    _bodies[i].SetPositionAndRotation(
                        terrainTransform.TransformPoint(new Vector3(position.x, unit.Height, position.y)), rotation);
                    _bodies[i].localScale = Vector3.one * bodyScale;
                    PlayClip(i, CrewAnimation.StateFor(unit.State, !unit.Inventory.IsEmpty));
                    Tint(i, _selection.Contains(i));
                    if (_rings[i] != null)
                        _rings[i].SetActive(_selection.Contains(i));
                }


                // The path still ahead: from the body to each remaining waypoint's centre.
                var path = unit.Path;
                var count = 0;
                if (_selection.Contains(i) && (unit.State == CrewUnitState.Moving || unit.State == CrewUnitState.Waiting)
                    && unit.PathIndex < path.Count)
                {
                    _pathPoints[count++] = terrainTransform.TransformPoint(new Vector3(position.x, unit.Height + 0.3f, position.y));
                    for (var p = unit.PathIndex; p < path.Count && count < _pathPoints.Length; p++)
                    {
                        var x = path[p].x + 0.5f;
                        var z = path[p].y + 0.5f;
                        _pathPoints[count++] = terrainTransform.TransformPoint(new Vector3(x * cellSize, TerrainSurface.SampleHeight(grid, x, z) + 0.3f, z * cellSize));
                    }
                }

                _lines[i].positionCount = count;
                _lines[i].SetPositions(_pathPoints);
            }
        }

        void PlayClip(int i, string clip)
        {
            var animator = _animators[i];
            if (animator == null || _clips[i] == clip)
                return;
            _clips[i] = clip;
            animator.CrossFadeInFixedTime(clip, _clipBlend);
        }

        /// <summary>
        /// Tints the selected robot, each material by its own colour times the tint, and clears it
        /// again when deselected. Only on a change, so unselected robots keep no property blocks
        /// and stay batched.
        /// </summary>
        void Tint(int i, bool selected)
        {
            if (_tinted[i] == selected)
                return;
            _tinted[i] = selected;
            _tint ??= new MaterialPropertyBlock();
            foreach (var r in _robotRenderers[i])
            {
                if (r.name == "CrewGlow")
                    continue;
                var materials = r.sharedMaterials;
                for (var m = 0; m < materials.Length; m++)
                {
                    if (!selected || materials[m] == null || !materials[m].HasProperty(BaseColorId))
                    {
                        r.SetPropertyBlock(null, m);
                        continue;
                    }

                    _tint.Clear();
                    _tint.SetColor(BaseColorId, materials[m].GetColor(BaseColorId) * _selectedTint);
                    r.SetPropertyBlock(_tint, m);
                }
            }
        }

        /// <summary>Metres of reach as whole height steps, never less than one.</summary>
        int Levels(float metres)
        {
            var step = _terrain.Grid.HeightStep > 0f ? _terrain.Grid.HeightStep : 1f;
            return Mathf.Max(1, Mathf.RoundToInt(metres / step));
        }

        void OnDestroy()
        {
            foreach (var unit in _units)
                unit.Dispose();
            _units.Clear();
            Dispatcher?.Dispose();
        }
    }
}
