using PromptWaffle.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Short turf generated on the GPU (Ronan, 2026-09-24: "really nice grass with effects and small
    /// for our world that doesn't bog the system down", after Advanced Terrain Grass; 10–25 cm).
    ///
    /// Each camera, just before it renders: a compute pass (Resources/GrassSpawn) walks a jittered grid
    /// of spots round the eye, keeps the ones on grass-topped ground that are in view, thins them with
    /// distance, colours them from the ground, and appends them to a buffer; then one indirect draw of
    /// a small clump mesh (<see cref="GrassClumpMesh"/>) draws them all with TinyDiggers/Grass Blades.
    /// Nothing is kept on the CPU per blade, so the cost is the GPU's and does not grow with the island.
    ///
    /// Where grass grows is read from the terrain's own cell map, so a dig or a tip clears it the
    /// moment the ground changes; the heights come from <see cref="TerrainHeightTexture"/>, so it
    /// stands on the drawn surface.
    /// </summary>
    public sealed class GpuGrass : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;

        [Header("Reach")]
        [Tooltip("Metres from the camera within which the grass is at full density.")]
        [SerializeField, Min(1f)] float _fullDensityTo = 35f;

        [Tooltip("Metres from the camera beyond which there is none; it thins to nothing between.")]
        [SerializeField, Min(2f)] float _noneBeyond = 90f;

        [Tooltip("Metres between clumps at full density.")]
        [SerializeField, Range(0.1f, 1f)] float _spacing = 0.2f;

        [Header("Look")]
        [Tooltip("Clump height range in metres (Ronan: short turf, 10–25 cm).")]
        [SerializeField] Vector2 _height = new Vector2(0.10f, 0.25f);

        [SerializeField, Range(5f, 60f)] float _steepestSlope = 38f;
        [SerializeField, Range(1, 12)] int _bladesPerClump = 7;
        [SerializeField] Color _meadow = new Color(0.50f, 0.60f, 0.13f);
        [SerializeField] Color _lush = new Color(0.28f, 0.44f, 0.10f);
        [SerializeField] Color _dry = new Color(0.64f, 0.68f, 0.15f);

        [Header("Shadows")]
        [SerializeField] bool _castShadows;

        static readonly int InstancesId = Shader.PropertyToID("_Instances");

        ComputeShader _compute;
        int _kernel;
        Material _material;
        Mesh _mesh;
        GraphicsBuffer _instances;
        GraphicsBuffer _args;
        TerrainHeightTexture _heights;
        TerrainGrid _builtFor;
        int _builtSide;
        MaterialPropertyBlock _properties;
        readonly Plane[] _planes = new Plane[6];
        readonly Vector4[] _planeVectors = new Vector4[6];

        /// <summary>Whether the last frame drew; the count itself stays on the GPU.</summary>
        public bool DrewLastFrame { get; private set; }

        void OnEnable() => RenderPipelineManager.beginCameraRendering += Draw;

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= Draw;
            Release();
        }

        void Build()
        {
            Release();
            var grid = _terrain != null ? _terrain.Grid : null;
            if (grid == null || _terrain.Detail == null)
                return;
            _compute = Resources.Load<ComputeShader>("GrassSpawn");
            var shader = Shader.Find("TinyDiggers/Grass Blades");
            if (_compute == null || shader == null)
            {
                Debug.LogWarning("GpuGrass: GrassSpawn compute or Grass Blades shader missing", this);
                return;
            }

            _kernel = _compute.FindKernel("Spawn");
            _material = new Material(shader) { name = "Grass Blades", hideFlags = HideFlags.DontSave };
            _mesh = GrassClumpMesh.Build(_bladesPerClump, spread: 0.6f, width: 0.12f);
            // 32 bytes a clump: position, rotation, colour, scale. Room for every spot of the grid:
            // an append past the end of the buffer is not checked on the GPU.
            var side = Mathf.CeilToInt(2f * _noneBeyond / _spacing);
            _instances = new GraphicsBuffer(GraphicsBuffer.Target.Append | GraphicsBuffer.Target.Structured, side * side, 32);
            _builtSide = side;
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _args.SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs { indexCountPerInstance = _mesh.GetIndexCount(0) } });
            _heights = new TerrainHeightTexture(grid);
            _properties = new MaterialPropertyBlock();
            _builtFor = grid;
        }

        void Release()
        {
            _instances?.Release();
            _instances = null;
            _args?.Release();
            _args = null;
            _heights?.Dispose();
            _heights = null;
            if (_material != null)
                DestroyImmediate(_material);
            if (_mesh != null)
                DestroyImmediate(_mesh);
            _builtFor = null;
        }

        void Draw(ScriptableRenderContext context, Camera camera)
        {
            DrewLastFrame = false;
            if (camera.cameraType != CameraType.Game)
                return;
            if (_terrain == null)
                _terrain = FindAnyObjectByType<TerrainView>();
            if (_terrain == null || _terrain.Grid == null || _terrain.Detail == null)
                return;
            if (!ReferenceEquals(_builtFor, _terrain.Grid) || _builtSide != Mathf.CeilToInt(2f * _noneBeyond / _spacing))
                Build();
            if (_instances == null)
                return;

            var grid = _terrain.Grid;
            var eye = camera.transform.position;
            var origin = _terrain.transform.position;
            // Too high up for any blade to be within reach: the painted ground does the work.
            if (eye.y - origin.y > _noneBeyond + 60f)
                return;

            _heights.Flush();
            var side = Mathf.CeilToInt(2f * _noneBeyond / _spacing);
            var gridOrigin = new Vector2(Mathf.Floor((eye.x - _noneBeyond) / _spacing) * _spacing, Mathf.Floor((eye.z - _noneBeyond) / _spacing) * _spacing);

            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (var i = 0; i < 6; i++)
                _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);

            var detail = _terrain.Detail;
            _instances.SetCounterValue(0);
            _compute.SetBuffer(_kernel, "_Instances", _instances);
            _compute.SetTexture(_kernel, "_CellMap", detail.CellMap.Texture);
            _compute.SetTexture(_kernel, "_Heights", _heights.Texture);
            _compute.SetTexture(_kernel, "_HollowMap", detail.Hollows.Texture);
            _compute.SetVector("_MapSize", new Vector4(grid.Width, grid.Height, 0f, 0f));
            _compute.SetVector("_TerrainOrigin", new Vector4(origin.x, origin.z, 1f / grid.CellSize, origin.y));
            _compute.SetVector("_Eye", eye);
            _compute.SetVector("_Range", new Vector4(_fullDensityTo, _noneBeyond, _spacing, side));
            _compute.SetVector("_GridOrigin", gridOrigin);
            _compute.SetVectorArray("_Planes", _planeVectors);
            _compute.SetVector("_Grass", new Vector4(MaterialTable.Topsoil.Value, Mathf.Cos(_steepestSlope * Mathf.Deg2Rad), _height.x, _height.y));
            _compute.SetVector("_Meadow", _meadow);
            _compute.SetVector("_Lush", _lush);
            _compute.SetVector("_Dry", _dry);
            _compute.SetVector("_Variants", new Vector4(_terrain.VariantScale, _terrain.VariantAmount, _terrain.DryHeight.x, _terrain.DryHeight.y));
            _compute.SetVector("_Hollows", new Vector4(_terrain.HollowStrength, 0f, 0f, 0f));
            _compute.SetVector("_HollowColour", _terrain.HollowColour);
            var groups = Mathf.CeilToInt(side / 8f);
            _compute.Dispatch(_kernel, groups, groups, 1);
            GraphicsBuffer.CopyCount(_instances, _args, sizeof(uint));

            _properties.SetBuffer(InstancesId, _instances);
            var parameters = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = new Bounds(eye, Vector3.one * (_noneBeyond * 2f + 100f)),
                shadowCastingMode = _castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = true,
                matProps = _properties,
                layer = gameObject.layer,
            };
            Graphics.RenderMeshIndirect(parameters, _mesh, _args);
            DrewLastFrame = true;
        }
    }
}
