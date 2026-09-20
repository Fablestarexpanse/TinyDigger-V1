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

            // The top of the plinth sits just under the rim of the land, or the two surfaces fight
            // for the same depth and the flats come out striped.
            //
            // It is a ring, not a solid cylinder: a solid one's top is a disc right under the
            // land, and anything dug deeper than three quarters of a metre comes out through it —
            // a pit floor drawn in plinth colour. The ring only fills the collar between the
            // ragged edge of the land and the clean circle of the plinth, so however deep a pit
            // goes, what is under it is still terrain.
            var top = rim - 0.75f;
            AddRing("Plinth", centre, top, _depth, radius, _terrain.DiscRadius - 2f, capBottom: true, _plinthMaterial);
            AddRing("Plinth Band", centre, top, _bandHeight, radius + 0.025f, radius - 0.025f, capBottom: false, _bandMaterial);

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

        /// <summary>
        /// A ring standing on its end: an outer wall from <paramref name="top"/> down by
        /// <paramref name="height"/>, a collar across the top from <paramref name="innerRadius"/>
        /// out to <paramref name="outerRadius"/>, and optionally a floor. No disc across the top,
        /// so nothing of the plinth ever appears inside the land.
        /// </summary>
        void AddRing(string name, Vector2 centre, float top, float height, float outerRadius, float innerRadius, bool capBottom, Material material)
        {
            const int Segments = 96;
            var bottom = top - height;
            innerRadius = Mathf.Clamp(innerRadius, 0f, outerRadius);

            var vertices = new Vector3[Segments * 4 + (capBottom ? Segments + 1 : 0)];
            var normals = new Vector3[vertices.Length];
            var triangles = new int[Segments * 12 + (capBottom ? Segments * 3 : 0)];
            var vertex = 0;
            var triangle = 0;

            for (var i = 0; i < Segments; i++)
            {
                var angle = i * Mathf.PI * 2f / Segments;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices[vertex] = direction * outerRadius + Vector3.up * top;
                vertices[vertex + 1] = direction * outerRadius + Vector3.up * bottom;
                vertices[vertex + 2] = direction * innerRadius + Vector3.up * top;
                normals[vertex] = direction;
                normals[vertex + 1] = direction;
                normals[vertex + 2] = Vector3.up;
                normals[vertex + 3] = Vector3.up;
                vertex += 4;
            }

            for (var i = 0; i < Segments; i++)
            {
                var a = i * 4;
                var b = (i + 1) % Segments * 4;

                // Outer wall.
                triangles[triangle++] = a;
                triangles[triangle++] = a + 1;
                triangles[triangle++] = b + 1;
                triangles[triangle++] = a;
                triangles[triangle++] = b + 1;
                triangles[triangle++] = b;

                // Collar across the top, between the land and the plinth's edge.
                triangles[triangle++] = a;
                triangles[triangle++] = b;
                triangles[triangle++] = b + 2;
                triangles[triangle++] = a;
                triangles[triangle++] = b + 2;
                triangles[triangle++] = a + 2;
            }

            if (capBottom)
            {
                var centreVertex = vertex;
                vertices[centreVertex] = Vector3.up * bottom;
                normals[centreVertex] = Vector3.down;
                vertex++;
                for (var i = 0; i < Segments; i++)
                {
                    var angle = i * Mathf.PI * 2f / Segments;
                    vertices[vertex + i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * outerRadius + Vector3.up * bottom;
                    normals[vertex + i] = Vector3.down;
                }

                for (var i = 0; i < Segments; i++)
                {
                    triangles[triangle++] = centreVertex;
                    triangles[triangle++] = vertex + (i + 1) % Segments;
                    triangles[triangle++] = vertex + i;
                }
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();

            var piece = new GameObject(name) { hideFlags = HideFlags.DontSave };
            piece.transform.SetParent(_terrain.transform, false);
            piece.transform.localPosition = new Vector3(centre.x, 0f, centre.y);
            piece.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = piece.AddComponent<MeshRenderer>();
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
