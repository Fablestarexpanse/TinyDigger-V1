using PromptWaffle.DynamicWater;
using Unity.Collections;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Hands the simulated water to the game: the water surface over each cell goes into the grid
    /// (<see cref="TerrainGrid.SetWaterSurfaces"/>), so digging, the crew's paths and the hover
    /// readout see the water that is really there rather than the sea-level rule. Disabled, the
    /// grid goes back to the sea-level rule.
    ///
    /// The first snapshot goes in whole. After that the map is fed a band of rows a frame
    /// (<see cref="TerrainGrid.SetWaterRows"/>), sweeping from the first row to the last and
    /// starting again while snapshots keep coming, so no frame pays for the whole map: at 1024
    /// rows and 128 a frame, a sweep takes eight frames, well inside the readback interval.
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

        [Tooltip("Rows of the map fed to the grid each frame, after the first whole snapshot.")]
        [SerializeField, Min(1)] int _rowsPerFrame = 128;

        WaterZone _zone;
        TerrainGrid _fed;
        float[] _surfaces;
        bool _misaligned;

        // The sweep: the next row to feed, and the snapshot the last finished sweep ended on.
        int _nextRow;
        float _sweepSnapshot = float.NaN;
        int _changedThisSweep;

        /// <summary>Cells that turned water, or stopped being water, in the last finished sweep.</summary>
        public int LastChanged { get; private set; }

        /// <summary>Milliseconds the last frame's feeding took.</summary>
        public float LastMilliseconds { get; private set; }

        /// <summary>The most any one frame's band has taken.</summary>
        public float WorstBandMilliseconds { get; private set; }

        /// <summary>Milliseconds the first whole snapshot took.</summary>
        public float FirstMilliseconds { get; private set; }

        /// <summary>Whole sweeps of the map handed to the grid so far, counting the first.</summary>
        public int Sweeps { get; private set; }

        /// <summary>Frames that fed a band.</summary>
        public int Bands { get; private set; }

        void Awake() => _zone = GetComponent<WaterZone>();

        void Update()
        {
            var grid = _terrain != null ? _terrain.Grid : null;
            if (!ReferenceEquals(grid, _fed))
                Release();

            var simulation = _zone.Simulation;
            if (grid == null || simulation == null || _misaligned || !simulation.Query.HasData)
                return;
            if (_fed == null && !Aligned(simulation.Desc, grid))
            {
                _misaligned = true;
                Debug.LogError("GridWaterFeed: the water zone is not laid one cell to one cell over the grid; the game keeps the sea-level rule.", this);
                return;
            }

            var query = simulation.Query;
            // Between sweeps, wait for water newer than the last sweep saw.
            if (_fed != null && _nextRow == 0 && query.SnapshotTime == _sweepSnapshot)
                return;

            var started = Time.realtimeSinceStartup;
            var snapshot = query.Snapshot;
            var width = grid.Width;
            if (_fed == null)
            {
                // The first goes in whole: a half-fed map would read its unfed half as dry.
                Fill(snapshot, 0, grid.Height, width);
                LastChanged = grid.SetWaterSurfaces(_surfaces);
                _fed = grid;
                _sweepSnapshot = query.SnapshotTime;
                _nextRow = 0;
                Sweeps++;
                FirstMilliseconds = LastMilliseconds = (Time.realtimeSinceStartup - started) * 1000f;
                return;
            }

            if (_nextRow == 0)
                _changedThisSweep = 0;
            var rows = Mathf.Min(_rowsPerFrame, grid.Height - _nextRow);
            Fill(snapshot, _nextRow, rows, width);
            _changedThisSweep += grid.SetWaterRows(_nextRow, new System.ReadOnlySpan<float>(_surfaces, 0, rows * width));
            _nextRow += rows;
            if (_nextRow >= grid.Height)
            {
                // The sweep read whichever snapshot was newest as it went; one newer than the
                // snapshot it ends on gets another sweep.
                _nextRow = 0;
                _sweepSnapshot = query.SnapshotTime;
                LastChanged = _changedThisSweep;
                Sweeps++;
            }

            Bands++;
            LastMilliseconds = (Time.realtimeSinceStartup - started) * 1000f;
            WorstBandMilliseconds = Mathf.Max(WorstBandMilliseconds, LastMilliseconds);
        }

        /// <summary>Water surfaces for <paramref name="rows"/> rows from <paramref name="firstRow"/>, in the terrain's own heights.</summary>
        void Fill(NativeArray<Vector4>.ReadOnly snapshot, int firstRow, int rows, int width)
        {
            var count = rows * width;
            if (_surfaces == null || _surfaces.Length < count)
                _surfaces = new float[count];
            // The simulation's heights are world metres; the grid's are the terrain's own.
            var lift = _terrain.transform.position.y;
            var start = firstRow * width;
            for (var i = 0; i < count; i++)
            {
                var state = snapshot[start + i];
                _surfaces[i] = state.y > _wetDepth ? state.x - lift : float.NegativeInfinity;
            }
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
            _nextRow = 0;
            _sweepSnapshot = float.NaN;
        }

        void OnDisable() => Release();
    }
}
