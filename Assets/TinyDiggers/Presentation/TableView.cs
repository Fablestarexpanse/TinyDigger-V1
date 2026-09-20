using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The table the map sits on: a plinth under the disc of land, a darker band where the two
    /// meet, and a flat gradient behind everything instead of a sky. Together they make the world
    /// read as a model on a surface rather than a landscape seen from a plane.
    ///
    /// Built from primitives at runtime, so there is nothing to keep in the scene file.
    /// </summary>
    public sealed class TableView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;

        [Tooltip("Cells the plinth stands proud of the disc of land, so its ragged edge reads as a clean circle.")]
        [SerializeField, Min(0f)] float _overhang = 6f;

        [Tooltip("Metres of plinth below the rim of the land.")]
        [SerializeField, Min(1f)] float _depth = 26f;

        [Tooltip("Metres of darker band just under the rim.")]
        [SerializeField, Min(0f)] float _bandHeight = 2f;

        [SerializeField] Color _plinthColor = new Color(0.72f, 0.69f, 0.64f);
        [SerializeField] Color _bandColor = new Color(0.42f, 0.40f, 0.37f);
        [SerializeField] Color _skyTop = new Color(0.88f, 0.87f, 0.85f);
        [SerializeField] Color _skyBottom = new Color(0.55f, 0.54f, 0.52f);

        [Tooltip("Blur the far rim when fully zoomed out, for a tilt-shift look. Needs a volume in the scene.")]
        public bool TiltShift;

        Material _plinthMaterial;
        Material _bandMaterial;
        Material _skyMaterial;
        Material _previousSkybox;

        void Start()
        {
            var grid = _terrain != null ? _terrain.Grid : null;
            if (grid == null)
                return;

            var radius = _terrain.DiscRadius + _overhang;
            var centre = _terrain.DiscCentre;
            var rim = _terrain.RimHeight;

            _plinthMaterial = NewMaterial(_plinthColor);
            _bandMaterial = NewMaterial(_bandColor);
            // Unity's cylinder is 2 units tall and 1 across, so a scale of (d, h/2, d) gives a
            // plinth of diameter d and height h.
            // The top of the plinth sits just under the rim of the land, or the two surfaces
            // fight for the same depth and the flats come out striped.
            var top = rim - 0.75f;
            AddCylinder("Plinth", centre, top - _depth * 0.5f, radius * 2f, _depth, _plinthMaterial);
            AddCylinder("Plinth Band", centre, top - _bandHeight * 0.5f, radius * 2f + 0.05f, _bandHeight, _bandMaterial);

            var shader = Shader.Find("TinyDiggers/Gradient Sky");
            if (shader != null)
            {
                _skyMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
                _skyMaterial.SetColor("_Top", _skyTop);
                _skyMaterial.SetColor("_Bottom", _skyBottom);
                _previousSkybox = RenderSettings.skybox;
                RenderSettings.skybox = _skyMaterial;
                DynamicGI.UpdateEnvironment();
            }
        }

        void AddCylinder(string name, Vector2 centre, float centreHeight, float diameter, float height, Material material)
        {
            var cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylinder.name = name;
            cylinder.hideFlags = HideFlags.DontSave;
            Destroy(cylinder.GetComponent<Collider>());
            cylinder.transform.SetParent(_terrain.transform, false);
            cylinder.transform.localPosition = new Vector3(centre.x, centreHeight, centre.y);
            cylinder.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            var renderer = cylinder.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Material NewMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { hideFlags = HideFlags.DontSave };
            material.color = color;
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0.05f);
            return material;
        }

        void OnDestroy()
        {
            if (_skyMaterial != null && ReferenceEquals(RenderSettings.skybox, _skyMaterial))
                RenderSettings.skybox = _previousSkybox;
            if (_plinthMaterial != null)
                Destroy(_plinthMaterial);
            if (_bandMaterial != null)
                Destroy(_bandMaterial);
            if (_skyMaterial != null)
                Destroy(_skyMaterial);
        }
    }
}
