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
    /// them, ticks them and draws a placeholder body (a box about two cells long) and the path of
    /// each. Clicking a body selects that unit, which the readout then describes; Escape clears
    /// the selection. No logic of its own.
    /// </summary>
    public sealed class CrewView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] DesignationsView _designations;
        [SerializeField] Material _pathMaterial;
        [SerializeField] Vector2Int _spawnCell = new Vector2Int(256, 256);

        [Tooltip("How many diggers to spawn, side by side across the spawn cell.")]
        [SerializeField, Min(0)] int _diggerCount = 2;

        [Tooltip("How many haulers to spawn.")]
        [SerializeField, Min(0)] int _haulerCount = 2;

        [Tooltip("Metres per second.")]
        [SerializeField, Min(0.1f)] float _speed = 3f;

        [Tooltip("Loose m³ a digger's scoop holds.")]
        [SerializeField, Min(0.1f)] float _capacity = MaterialInventory.DefaultCapacity;

        [Tooltip("Loose m³ a hauler's bed holds.")]
        [SerializeField, Min(0.1f)] float _haulerCapacity = 20f;

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

        readonly List<CrewUnit> _units = new List<CrewUnit>();
        readonly List<Transform> _bodies = new List<Transform>();
        readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        readonly List<LineRenderer> _lines = new List<LineRenderer>();
        readonly Vector3[] _pathPoints = new Vector3[512];

        GridPathfinder _pathfinder;
        int _selected = -1;

        public JobDispatcher Dispatcher { get; private set; }

        public IReadOnlyList<CrewUnit> Units => _units;

        /// <summary>The unit the player has clicked, or null.</summary>
        public CrewUnit Selected => _selected >= 0 && _selected < _units.Count ? _units[_selected] : null;

        /// <summary>The first unit, for readouts that want something to show with nothing selected.</summary>
        public CrewUnit Unit => _units.Count > 0 ? _units[0] : null;

        void Start()
        {
            var grid = _terrain.Grid;
            _pathfinder = new GridPathfinder(grid) { MaxStepHeight = maxStepHeight };
            Dispatcher = new JobDispatcher(grid, _designations.Map, _pathfinder);

            var spawnX = Mathf.Clamp(_spawnCell.x, 0, grid.Width - 1);
            var spawnZ = Mathf.Clamp(_spawnCell.y, 0, grid.Height - 1);
            var total = _diggerCount + _haulerCount;
            for (var i = 0; i < total; i++)
            {
                // Side by side, so no two start on the same cell.
                var x = Mathf.Clamp(spawnX + i % 2 * 2 - 1, 0, grid.Width - 1);
                var z = Mathf.Clamp(spawnZ + i / 2 * 2 - 1, 0, grid.Height - 1);
                var role = i < _diggerCount ? UnitRole.Digger : UnitRole.Hauler;
                _units.Add(new CrewUnit(Dispatcher, x, z, role, role == UnitRole.Digger ? _capacity : _haulerCapacity));

                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = $"{role} {i} Body";
                body.hideFlags = HideFlags.DontSave;
                body.transform.SetParent(transform, false);
                // A unit is a one-cell machine to the crew logic, so its body is sized to the cell.
                body.transform.localScale = (role == UnitRole.Digger ? _bodySize : _haulerBodySize) * grid.CellSize;
                var bodyRenderer = body.GetComponent<MeshRenderer>();
                bodyRenderer.material.color = role == UnitRole.Digger ? _bodyColor : _haulerColor;
                _bodies.Add(body.transform);
                _renderers.Add(bodyRenderer);

                var line = new GameObject($"{role} {i} Path") { hideFlags = HideFlags.DontSave };
                line.transform.SetParent(transform, false);
                var pathLine = line.AddComponent<LineRenderer>();
                pathLine.sharedMaterial = _pathMaterial;
                pathLine.widthMultiplier = 0.2f;
                pathLine.startColor = pathLine.endColor = new Color(1f, 1f, 1f, 0.85f);
                pathLine.useWorldSpace = true;
                pathLine.positionCount = 0;
                _lines.Add(pathLine);
            }
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
                unit.Speed = _speed;
                unit.WorkInterval = _workInterval;
                unit.Tick(Time.deltaTime);
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                _selected = -1;
        }

        /// <summary>
        /// Selects the unit under the ray, if any; returns whether it took the click, so the
        /// designation tool can leave it alone.
        /// </summary>
        public bool TrySelectAt(Ray ray)
        {
            if (!Physics.Raycast(ray, out var hit, 10000f))
                return false;
            var index = _bodies.IndexOf(hit.transform);
            if (index < 0)
                return false;
            _selected = index;
            return true;
        }

        public void Deselect() => _selected = -1;

        void LateUpdate()
        {
            var terrainTransform = _terrain.transform;
            var grid = _terrain.Grid;
            var cellSize = grid.CellSize;
            for (var i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                // Unit positions are in cells; the terrain's local space is metres.
                var position = unit.Position * cellSize;
                var size = (unit.Role == UnitRole.Digger ? _bodySize : _haulerBodySize) * cellSize;
                _bodies[i].SetPositionAndRotation(
                    terrainTransform.TransformPoint(new Vector3(position.x, unit.Height + size.y * 0.5f, position.y)),
                    terrainTransform.rotation * Quaternion.Euler(0f, unit.Heading, 0f));
                _renderers[i].material.color = i == _selected ? _selectedColor
                    : unit.Role == UnitRole.Digger ? _bodyColor : _haulerColor;


                // The path still ahead: from the body to each remaining waypoint's centre.
                var path = unit.Path;
                var count = 0;
                if ((unit.State == CrewUnitState.Moving || unit.State == CrewUnitState.Waiting) && unit.PathIndex < path.Count)
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
