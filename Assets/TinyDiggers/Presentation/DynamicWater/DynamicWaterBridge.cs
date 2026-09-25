using PromptWaffle.DynamicWater;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Puts a PromptWaffle Dynamic Water zone over the TinyDiggers disc: sized to the grid,
    /// aligned to its cells, filled to sea level, and refilled whenever the island is regenerated.
    /// While it runs, the old static sea (WaterView) can be hidden so the two surfaces do not
    /// fight (<see cref="_hideStaticSea"/>).
    /// </summary>
    [RequireComponent(typeof(WaterZone), typeof(TerrainGridWaterGround))]
    public sealed class DynamicWaterBridge : MonoBehaviour
    {
        [SerializeField] WaterView _staticWater;

        [Tooltip("Hide the old static sea and river while the dynamic water runs.")]
        [SerializeField] bool _hideStaticSea = true;

        WaterZone _zone;
        TerrainView _terrain;

        void Start()
        {
            _zone = GetComponent<WaterZone>();
            _terrain = GetComponent<TerrainGridWaterGround>().Terrain;
            if (_terrain == null || _terrain.Grid == null)
            {
                Debug.LogError("DynamicWaterBridge: the ground has no TerrainView with a grid.", this);
                enabled = false;
                return;
            }

            // One zone cell per grid cell, covering the whole grid, centred on it.
            var grid = _terrain.Grid;
            var size = new Vector2(grid.Width * grid.CellSize, grid.Height * grid.CellSize);
            var centre = _terrain.transform.TransformPoint(new Vector3(size.x * 0.5f, 0f, size.y * 0.5f));
            transform.position = new Vector3(centre.x, World.SeaLevel, centre.z);
            _zone.Configure(size, grid.CellSize, World.SeaLevel);
            _zone.Rebuild();
            _terrain.Regenerated += OnRegenerated;

            if (_hideStaticSea && _staticWater != null)
                _staticWater.Visible = false;
        }

        void OnRegenerated() => _zone.Rebuild();

        void OnDestroy()
        {
            if (_staticWater != null)
                _staticWater.Visible = true;
            if (_terrain != null)
                _terrain.Regenerated -= OnRegenerated;
        }
    }
}
