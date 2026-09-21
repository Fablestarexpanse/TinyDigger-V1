using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Draws the <see cref="GrassField"/>: one instanced draw per chunk that is in view and within
    /// <see cref="_drawDistance"/> of the camera. The tuft mesh and material come from the art
    /// pipeline (Art/Props/grass_a), and the material's TinyDiggers/Foliage shader sways them in
    /// the <see cref="WindView"/> wind.
    ///
    /// Thin on purpose: where grass stands is GrassField's business. This only follows the land
    /// (a dug cell takes its grass with it, a regenerated island regrows the meadow) and draws.
    /// </summary>
    public sealed class GrassView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] Mesh _mesh;
        [SerializeField] Material _material;

        [Tooltip("Tufts per grass cell, on average.")]
        [SerializeField, Min(0f)] float _density = 1.5f;

        [Tooltip("Metres from the camera beyond which no grass is drawn. At RTS distance a tuft is " +
            "a pixel or two, and the meadow colour of the ground does the work.")]
        [SerializeField, Min(10f)] float _drawDistance = 220f;

        [SerializeField] bool _castShadows = true;

        GrassField _field;
        readonly HashSet<int> _dirty = new HashSet<int>();
        readonly Plane[] _planes = new Plane[6];

        public GrassField Field => _field;

        /// <summary>Instances drawn last frame, for the perf report.</summary>
        public int DrawnLastFrame { get; private set; }

        void OnEnable() => RenderPipelineManager.beginCameraRendering += Draw;

        void OnDisable() => RenderPipelineManager.beginCameraRendering -= Draw;

        void Start()
        {
            if (_terrain == null)
                _terrain = FindAnyObjectByType<TerrainView>();
            Build();
            if (_terrain != null)
                _terrain.Regenerated += Build;
        }

        void OnDestroy()
        {
            if (_terrain != null)
            {
                _terrain.Regenerated -= Build;
                if (_terrain.Grid != null)
                    _terrain.Grid.CellChanged -= OnCellChanged;
            }
        }

        void Build()
        {
            if (_terrain == null || _terrain.Grid == null)
                return;
            _terrain.Grid.CellChanged -= OnCellChanged;
            _field = new GrassField(_terrain.Grid, _density);
            _terrain.Grid.CellChanged += OnCellChanged;
            _dirty.Clear();
        }

        void OnCellChanged(int x, int z)
        {
            if (_field == null)
                return;
            _dirty.Add(z / _field.ChunkSize * _field.ChunksX + x / _field.ChunkSize);
        }

        void LateUpdate()
        {
            if (_field == null)
                return;

            // Edits come in bursts; rebuild each touched chunk once, after the frame's digging.
            foreach (var chunk in _dirty)
                _field.OnCellChanged(chunk % _field.ChunksX * _field.ChunkSize, chunk / _field.ChunksX * _field.ChunkSize);
            _dirty.Clear();
        }

        /// <summary>
        /// Draws for each camera as it starts rendering, not once in LateUpdate: draws queued in
        /// LateUpdate only reach the frame's own render, so a camera rendered by hand (the
        /// capture tools, a minimap) drew no grass at all.
        /// </summary>
        void Draw(ScriptableRenderContext context, Camera camera)
        {
            if (_field == null || _mesh == null || _material == null)
                return;
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return;

            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            var eye = camera.transform.position;
            var parameters = new RenderParams(_material)
            {
                camera = camera,
                shadowCastingMode = _castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = true,
                layer = gameObject.layer,
            };

            var drawn = 0;
            for (var cz = 0; cz < _field.ChunksZ; cz++)
            {
                for (var cx = 0; cx < _field.ChunksX; cx++)
                {
                    var tufts = _field.Chunk(cx, cz);
                    if (tufts.Count == 0)
                        continue;
                    var bounds = _field.ChunkBounds(cx, cz);
                    if (bounds.SqrDistance(eye) > _drawDistance * _drawDistance)
                        continue;
                    if (!GeometryUtility.TestPlanesAABB(_planes, bounds))
                        continue;
                    parameters.worldBounds = bounds;
                    Graphics.RenderMeshInstanced(parameters, _mesh, 0, (List<Matrix4x4>)tufts);
                    drawn += tufts.Count;
                }
            }

            DrawnLastFrame = drawn;
        }
    }
}
