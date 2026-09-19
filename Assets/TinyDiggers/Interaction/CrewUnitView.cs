using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Scene side of the one <see cref="CrewUnit"/>: creates it, ticks it, and draws a
    /// placeholder body (a box about two cells long) and its current path. No logic of its own.
    /// </summary>
    public sealed class CrewUnitView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] DesignationsView _designations;
        [SerializeField] Material _pathMaterial;
        [SerializeField] Vector2Int _spawnCell = new Vector2Int(256, 256);

        [Tooltip("Cells per second.")]
        [SerializeField, Min(0.1f)] float _speed = 3f;

        [Tooltip("Loose m³ the unit can carry.")]
        [SerializeField, Min(0.1f)] float _capacity = MaterialInventory.DefaultCapacity;

        [Tooltip("Seconds per height step dug, or per tip.")]
        [SerializeField, Min(0.01f)] float _workInterval = 0.4f;

        /// <summary>How many height steps above or below its own cell the unit can dig or fill. Read every frame.</summary>
        [Min(0)] public int digReachLevels = 2;

        /// <summary>Largest height change the unit can drive across between neighbouring cells, in metres.</summary>
        [Min(0f)] public float maxStepHeight = 1f;

        /// <summary>Let the unit bench dig areas and cut its own ramps to work it cannot reach. Read every frame.</summary>
        public bool autoRamp = true;

        [SerializeField] Vector3 _bodySize = new Vector3(1f, 0.8f, 2f);
        [SerializeField] Color _bodyColor = new Color(1f, 0.78f, 0.1f);

        GridPathfinder _pathfinder;
        Transform _body;
        LineRenderer _pathLine;
        readonly Vector3[] _pathPoints = new Vector3[512];

        public CrewUnit Unit { get; private set; }

        void Start()
        {
            var grid = _terrain.Grid;
            _pathfinder = new GridPathfinder(grid) { MaxStepHeight = maxStepHeight };
            var spawnX = Mathf.Clamp(_spawnCell.x, 0, grid.Width - 1);
            var spawnZ = Mathf.Clamp(_spawnCell.y, 0, grid.Height - 1);
            Unit = new CrewUnit(grid, _designations.Map, _pathfinder, spawnX, spawnZ, _capacity);

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Crew Unit Body";
            body.hideFlags = HideFlags.DontSave;
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(transform, false);
            body.transform.localScale = _bodySize;
            var bodyRenderer = body.GetComponent<MeshRenderer>();
            bodyRenderer.material.color = _bodyColor;
            _body = body.transform;

            var line = new GameObject("Crew Unit Path") { hideFlags = HideFlags.DontSave };
            line.transform.SetParent(transform, false);
            _pathLine = line.AddComponent<LineRenderer>();
            _pathLine.sharedMaterial = _pathMaterial;
            _pathLine.widthMultiplier = 0.2f;
            _pathLine.startColor = _pathLine.endColor = new Color(1f, 1f, 1f, 0.85f);
            _pathLine.useWorldSpace = true;
            _pathLine.positionCount = 0;
        }

        void Update()
        {
            Unit.DigReachLevels = digReachLevels;
            Unit.AutoRamp = autoRamp;
            Unit.Speed = _speed;
            Unit.WorkInterval = _workInterval;
            _pathfinder.MaxStepHeight = maxStepHeight;
            Unit.Tick(Time.deltaTime);
        }

        void LateUpdate()
        {
            var terrainTransform = _terrain.transform;
            var position = Unit.Position;
            _body.SetPositionAndRotation(
                terrainTransform.TransformPoint(new Vector3(position.x, Unit.Height + _bodySize.y * 0.5f, position.y)),
                terrainTransform.rotation * Quaternion.Euler(0f, Unit.Heading, 0f));

            // The path still ahead: from the body to each remaining waypoint's centre.
            var grid = _terrain.Grid;
            var path = Unit.Path;
            var count = 0;
            if (Unit.State == CrewUnitState.Moving && Unit.PathIndex < path.Count)
            {
                _pathPoints[count++] = terrainTransform.TransformPoint(new Vector3(position.x, Unit.Height + 0.3f, position.y));
                for (var i = Unit.PathIndex; i < path.Count && count < _pathPoints.Length; i++)
                {
                    var x = path[i].x + 0.5f;
                    var z = path[i].y + 0.5f;
                    _pathPoints[count++] = terrainTransform.TransformPoint(new Vector3(x, TerrainSurface.SampleHeight(grid, x, z) + 0.3f, z));
                }
            }

            _pathLine.positionCount = count;
            _pathLine.SetPositions(_pathPoints);
        }

        void OnDestroy()
        {
            Unit?.Dispose();
        }
    }
}
