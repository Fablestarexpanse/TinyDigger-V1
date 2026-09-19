using System.Diagnostics;
using TinyDiggers.Terrain;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Scene entry point for the terrain: builds the grid, fills it with test terrain and hands it
    /// to a renderer. Holds no terrain logic of its own.
    /// </summary>
    public sealed class TerrainView : MonoBehaviour
    {
        [SerializeField, Min(1)] int _width = 512;
        [SerializeField, Min(1)] int _height = 512;
        [SerializeField, Range(1, ChunkedMeshTerrainRenderer.MaxChunkSize)] int _chunkSize = ChunkedMeshTerrainRenderer.DefaultChunkSize;
        [SerializeField] int _seed = 1;
        [SerializeField] Material _material;

        ChunkedMeshTerrainRenderer _renderer;

        public TerrainGrid Grid { get; private set; }

        public ITerrainRenderer Renderer => _renderer;

        void Awake()
        {
            var stopwatch = Stopwatch.StartNew();
            Grid = new TerrainGrid(_width, _height, MaterialTable.CreateDefault());
            TerrainGenerator.Generate(Grid, _seed);
            var generated = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            _renderer = new ChunkedMeshTerrainRenderer(Grid, transform, _material, _chunkSize);
            var built = stopwatch.Elapsed.TotalMilliseconds;

            Debug.Log(
                $"TerrainView: {_width}x{_height} cells, {_renderer.ChunkCountX * _renderer.ChunkCountZ} chunks. " +
                $"Generated in {generated:0} ms, meshed in {built:0} ms.");
        }

        void LateUpdate()
        {
            _renderer.Rebuild();
        }

        void OnDestroy()
        {
            _renderer?.Dispose();
        }
    }
}
