using System.Collections.Generic;
using TinyDiggers.Presentation;
using PromptWaffle.Terrain;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The ore view (F7), after Captain of Industry's resource view: a translucent tile on every
    /// cell with ore within the survey depth, coloured by the ore and fading with how deep it is,
    /// so shallow deposits read strong and deep ones faint. What lies where is
    /// <see cref="OreSurvey"/>'s business; this only draws it, and rebuilds the mesh at most once a
    /// frame when a find changes (digging ore out clears its mark).
    /// </summary>
    public sealed class OreOverlayView : MonoBehaviour
    {
        const float Lift = 0.08f;

        [SerializeField] TerrainView _terrain;
        [SerializeField] Material _overlayMaterial;
        [SerializeField, Min(1f)] float _surveyDepth = 20f;
        [SerializeField] bool _visible;

        [SerializeField] Color32 _coal = new Color32(20, 20, 24, 200);
        [SerializeField] Color32 _iron = new Color32(200, 70, 40, 200);
        [SerializeField] Color32 _copper = new Color32(40, 200, 170, 200);
        [SerializeField] Color32 _limestone = new Color32(245, 240, 220, 200);

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();

        OreSurvey _survey;
        Mesh _mesh;
        GameObject _overlay;
        bool _dirty = true;

        public OreSurvey Survey => _survey;

        public bool Visible
        {
            get => _visible;
            set
            {
                _visible = value;
                if (_overlay != null)
                    _overlay.SetActive(value);
            }
        }

        void Start()
        {
            if (_terrain == null)
                _terrain = FindAnyObjectByType<TerrainView>();
            _mesh = new Mesh { name = "Ore Overlay", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _overlay = new GameObject("Ore Overlay") { hideFlags = HideFlags.DontSave };
            _overlay.transform.SetParent(_terrain.transform, false);
            _overlay.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = _overlay.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _overlayMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _overlay.SetActive(_visible);
            Build();
            _terrain.Regenerated += Build;
        }

        void OnDestroy()
        {
            if (_terrain != null)
                _terrain.Regenerated -= Build;
            _survey?.Dispose();
            if (_mesh != null)
                Destroy(_mesh);
        }

        void Build()
        {
            _survey?.Dispose();
            _survey = new OreSurvey(_terrain.Grid, _surveyDepth);
            _survey.Changed += _ => _dirty = true;
            _dirty = true;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f7Key.wasPressedThisFrame)
                Visible = !Visible;
        }

        void LateUpdate()
        {
            if (!_dirty || _mesh == null || _survey == null)
                return;
            _dirty = false;

            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            var grid = _terrain.Grid;
            for (var z = 0; z < grid.Height; z++)
            {
                for (var x = 0; x < grid.Width; x++)
                {
                    var find = _survey.At(x, z);
                    if (find.IsNone)
                        continue;
                    var color = ColourOf(find.Ore);
                    // Shallow reads strong, the bottom of the survey faint.
                    var nearness = 1f - Mathf.Clamp01(find.DepthToTop / _survey.Depth);
                    color.a = (byte)(color.a * (0.25f + 0.75f * nearness));
                    DesignationsView.AddSurfaceTile(grid, x, z, color, _vertices, _colors, _triangles, Lift);
                }
            }

            _mesh.Clear();
            _mesh.indexFormat = _vertices.Count > ushort.MaxValue ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        Color32 ColourOf(MaterialId ore)
        {
            if (ore == MaterialTable.Coal || ore == MaterialTable.CoalLoose) return _coal;
            if (ore == MaterialTable.IronOre || ore == MaterialTable.IronOreLoose) return _iron;
            if (ore == MaterialTable.CopperOre || ore == MaterialTable.CopperOreLoose) return _copper;
            return _limestone;
        }
    }
}
