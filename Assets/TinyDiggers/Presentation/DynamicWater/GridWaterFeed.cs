using PromptWaffle.DynamicWater;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Hands the simulated water to the game: every time the zone's readback brings a fresh
    /// snapshot, the water surface over each cell goes into the grid
    /// (<see cref="TerrainGrid.SetWaterSurfaces"/>), so digging, the crew's paths and the hover
    /// readout see the water that is really there rather than the sea-level rule. Disabled, the
    /// grid goes back to the sea-level rule.
    ///
    /// Needs the zone laid over the grid one cell to one cell, as <see cref="DynamicWaterBridge"/>
    /// lays it.
    /// </summary>
    [RequireComponent(typeof(WaterZone))]
    public sealed class GridWaterFeed : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;

        [Tooltip("Metres of simulated water under which a cell counts as dry.")]
        [SerializeField, Min(0f)] float _wetDepth = 0.02f;

        WaterZone _zone;
        TerrainGrid _fed;
        float _fedSnapshot = float.NaN;
        float[] _surfaces;
        bool _misaligned;

        /// <summary>Cells that turned water, or stopped being water, on the last update.</summary>
        public int LastChanged { get; private set; }

        /// <summary>Milliseconds the last update took.</summary>
        public float LastMilliseconds { get; private set; }

        /// <summary>Updates handed to the grid so far.</summary>
        public int Updates { get; private set; }

        void Awake() => _zone = GetComponent<WaterZone>();

        void Update()
        {
            var grid = _terrain != null ? _terrain.Grid : null;
            if (!ReferenceEquals(grid, _fed))
                Release();

            var simulation = _zone.Simulation;
            if (grid == null || simulation == null || _misaligned || !simulation.Query.HasData)
                return;
            if (simulation.Query.SnapshotTime == _fedSnapshot && ReferenceEquals(grid, _fed))
                return;
            if (!Aligned(simulation.Desc, grid))
            {
                _misaligned = true;
                Debug.LogError("GridWaterFeed: the water zone is not laid one cell to one cell over the grid; the game keeps the sea-level rule.", this);
                return;
            }

            var started = Time.realtimeSinceStartup;
            var snapshot = simulation.Query.Snapshot;
            if (_surfaces == null || _surfaces.Length != snapshot.Length)
                _surfaces = new float[snapshot.Length];
            // The simulation's heights are world metres; the grid's are the terrain's own.
            var lift = _terrain.transform.position.y;
            for (var i = 0; i < _surfaces.Length; i++)
            {
                var state = snapshot[i];
                _surfaces[i] = state.y > _wetDepth ? state.x - lift : float.NegativeInfinity;
            }

            LastChanged = grid.SetWaterSurfaces(_surfaces);
            _fed = grid;
            _fedSnapshot = simulation.Query.SnapshotTime;
            Updates++;
            LastMilliseconds = (Time.realtimeSinceStartup - started) * 1000f;
        }

        bool Aligned(in WaterSimulationDesc desc, TerrainGrid grid)
        {
            var origin = _terrain.transform.position;
            return desc.Width == grid.Width && desc.Height == grid.Height
                && Mathf.Abs(desc.CellSize - grid.CellSize) < 1e-4f
                && Mathf.Abs(desc.Origin.x - origin.x) < 1e-3f && Mathf.Abs(desc.Origin.y - origin.z) < 1e-3f
                && _terrain.transform.rotation == Quaternion.identity;
        }

        void Release()
        {
            _fed?.ClearWaterSurfaces();
            _fed = null;
            _fedSnapshot = float.NaN;
        }

        void OnDisable() => Release();
    }
}
