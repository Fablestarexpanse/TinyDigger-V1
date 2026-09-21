using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The water: one sheet over the sea, clipped to the disc, and a ribbon down the river the
    /// generator cut. Both are built here rather than bought, because all they have to be is a
    /// flat sheet that reads as water from an RTS camera.
    ///
    /// How deep the water is over each vertex is read from the terrain when the sheet is built and
    /// baked into the mesh, so the shader can shade the shallows and foam the shoreline without
    /// reading scene depth. That also means the sheets are rebuilt when the land changes, which is
    /// what <see cref="Rebuild"/> is for: reclaiming the shallows should push the shoreline back.
    /// </summary>
    public sealed class WaterView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;

        [Tooltip("Cells between vertices of the sea sheet. Coarser is cheaper; the sea is flat.")]
        [SerializeField, Min(1)] int _seaResolution = 4;

        [Tooltip("Cells the sea runs past the edge of the land, so it meets the plinth wall.")]
        [SerializeField, Min(0f)] float _seaOverhang = 5f;

        [Tooltip("Metres the river surface sits above its channel floor.")]
        [SerializeField, Min(0f)] float _riverDepth = 1.2f;

        [Tooltip("Rebuild the sheets this many seconds after the land last changed.")]
        [SerializeField, Min(0f)] float _rebuildDelay = 0.5f;

        Material _material;
        GameObject _sea;
        GameObject _river;
        Mesh _seaMesh;
        Mesh _riverMesh;
        float _dirtyAt = -1f;

        /// <summary>Triangles across both sheets, for the perf report.</summary>
        public int TriangleCount { get; private set; }

        void Start()
        {
            if (_terrain == null || _terrain.Grid == null)
                return;

            var shader = Shader.Find("TinyDiggers/Water");
            if (shader == null)
            {
                Debug.LogError("WaterView: the water shader is missing.");
                enabled = false;
                return;
            }

            _material = new Material(shader) { name = "Water", hideFlags = HideFlags.DontSave };
            _sea = NewSheet("Sea");
            _river = NewSheet("River");
            Rebuild();

            _terrain.Grid.CellChanged += OnCellChanged;
            _terrain.Regenerated += Rebuild;
        }

        void OnDestroy()
        {
            if (_terrain != null)
            {
                if (_terrain.Grid != null)
                    _terrain.Grid.CellChanged -= OnCellChanged;
                _terrain.Regenerated -= Rebuild;
            }

            Destroy(_material);
            Destroy(_seaMesh);
            Destroy(_riverMesh);
        }

        void OnCellChanged(int x, int z) => _dirtyAt = Time.time;

        void Update()
        {
            // Edits come in bursts, and rebuilding a sheet is not free, so the shoreline follows a
            // moment after the digging stops rather than on every cell.
            if (_dirtyAt < 0f || Time.time - _dirtyAt < _rebuildDelay)
                return;
            _dirtyAt = -1f;
            Rebuild();
        }

        GameObject NewSheet(string name)
        {
            var sheet = new GameObject(name) { hideFlags = HideFlags.DontSave };
            sheet.transform.SetParent(_terrain.transform, false);
            sheet.AddComponent<MeshFilter>();
            var renderer = sheet.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return sheet;
        }

        /// <summary>Rebuilds both sheets from the land as it now stands.</summary>
        public void Rebuild()
        {
            if (_terrain == null || _terrain.Grid == null || _sea == null)
                return;

            _seaMesh = BuildSea(_seaMesh);
            _riverMesh = BuildRiver(_riverMesh);
            _sea.GetComponent<MeshFilter>().sharedMesh = _seaMesh;
            _river.GetComponent<MeshFilter>().sharedMesh = _riverMesh;
            TriangleCount = (_seaMesh == null ? 0 : _seaMesh.triangles.Length / 3)
                + (_riverMesh == null ? 0 : _riverMesh.triangles.Length / 3);
        }

        /// <summary>
        /// The sea: a grid of quads over the disc at sea level, keeping only the quads that have
        /// water under at least one corner, so the sheet stops at the shoreline instead of being
        /// drawn over the whole island and hidden by it.
        /// </summary>
        Mesh BuildSea(Mesh mesh)
        {
            var grid = _terrain.Grid;
            var step = Mathf.Max(1, _seaResolution);
            var centre = _terrain.DiscCentre;
            var radius = _terrain.DiscRadius + _seaOverhang;

            var columns = grid.Width / step + 1;
            var rows = grid.Height / step + 1;
            var vertices = new List<Vector3>(columns * rows);
            var depths = new List<Vector2>(columns * rows);
            var indices = new List<int>(columns * rows * 6);
            var lookup = new int[columns * rows];
            for (var i = 0; i < lookup.Length; i++)
                lookup[i] = -1;

            float RawDepthAt(int x, int z)
            {
                x = Mathf.Clamp(x, 0, grid.Width - 1);
                z = Mathf.Clamp(z, 0, grid.Height - 1);
                if (!grid.IsGround(x, z))
                    return 12f; // Past the rim: as deep as the channel, so the edge stays dark.
                return World.SeaLevel - grid.GetSurfaceHeight(x, z);
            }

            // Averaged over a couple of cells either way. The seabed is quantised to whole metres,
            // so reading it a cell at a time gives the shallows a staircase of colour bands; a
            // small blur turns that into the gradient the eye expects of shallow water.
            float DepthAt(int x, int z)
            {
                const int Blur = 2;
                var sum = 0f;
                var count = 0;
                for (var dz = -Blur; dz <= Blur; dz++)
                {
                    for (var dx = -Blur; dx <= Blur; dx++)
                    {
                        sum += RawDepthAt(x + dx, z + dz);
                        count++;
                    }
                }

                return sum / count;
            }

            int VertexAt(int column, int row)
            {
                var slot = row * columns + column;
                if (lookup[slot] >= 0)
                    return lookup[slot];

                var x = column * step;
                var z = row * step;
                var position = new Vector3(Mathf.Min(x, grid.Width), World.SeaLevel, Mathf.Min(z, grid.Height));
                // Pull the outermost ring onto the circle, so the sea ends in a clean edge under
                // the plinth wall rather than in a staircase of quads.
                var flat = new Vector2(position.x - centre.x, position.z - centre.y);
                if (flat.magnitude > radius)
                {
                    flat = flat.normalized * radius;
                    position = new Vector3(centre.x + flat.x, World.SeaLevel, centre.y + flat.y);
                }

                lookup[slot] = vertices.Count;
                vertices.Add(position);
                depths.Add(new Vector2(DepthAt(x, z), 0f));
                return lookup[slot];
            }

            for (var row = 0; row < rows - 1; row++)
            {
                for (var column = 0; column < columns - 1; column++)
                {
                    var x = column * step;
                    var z = row * step;
                    // Keep a quad when any corner has water over it, plus a cell of slack so the
                    // sheet runs under the shore rather than stopping short of it.
                    var wet = RawDepthAt(x, z) > -1f || RawDepthAt(x + step, z) > -1f
                        || RawDepthAt(x, z + step) > -1f || RawDepthAt(x + step, z + step) > -1f;
                    if (!wet)
                        continue;

                    var a = VertexAt(column, row);
                    var b = VertexAt(column + 1, row);
                    var c = VertexAt(column, row + 1);
                    var d = VertexAt(column + 1, row + 1);
                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(b); indices.Add(c); indices.Add(d);
                }
            }

            return Fill(mesh, "Sea", vertices, depths, indices);
        }

        /// <summary>
        /// The river: a ribbon down the polyline the generator carved, its surface a little above
        /// the channel floor, carrying distance travelled so the shader can run the surface
        /// downstream.
        /// </summary>
        Mesh BuildRiver(Mesh mesh)
        {
            var island = _terrain.Island;
            var vertices = new List<Vector3>();
            var depths = new List<Vector2>();
            var indices = new List<int>();
            if (island == null || island.River.Count < 2)
                return Fill(mesh, "River", vertices, depths, indices);

            var settings = _terrain.Settings;
            var halfWidth = Mathf.Max(1f, (settings != null ? settings.RiverWidth : 4) * 0.5f);
            var travelled = 0f;

            for (var i = 0; i < island.River.Count; i++)
            {
                var point = island.River[i];
                var previous = island.River[Mathf.Max(0, i - 1)];
                var next = island.River[Mathf.Min(island.River.Count - 1, i + 1)];
                var along = next - previous;
                along.y = 0f;
                if (along.sqrMagnitude < 1e-4f)
                    along = Vector3.forward;
                along.Normalize();
                var across = new Vector3(-along.z, 0f, along.x) * halfWidth;

                if (i > 0)
                    travelled += Vector3.Distance(new Vector3(previous.x, 0f, previous.z), new Vector3(point.x, 0f, point.z));

                // The surface stands above the floor, but never above the sea: where the channel
                // reaches the coast the river simply becomes the sea.
                var surface = Mathf.Min(point.y + _riverDepth, World.SeaLevel + _riverDepth);
                var left = new Vector3(point.x, surface, point.z) - across;
                var right = new Vector3(point.x, surface, point.z) + across;

                vertices.Add(left);
                vertices.Add(right);
                var depth = surface - point.y;
                // The banks are shallow, the middle is not: that is what foams the edges.
                depths.Add(new Vector2(depth * 0.25f, travelled));
                depths.Add(new Vector2(depth * 0.25f, travelled));

                if (i == 0)
                    continue;
                var b = (i - 1) * 2;
                indices.Add(b); indices.Add(b + 2); indices.Add(b + 1);
                indices.Add(b + 1); indices.Add(b + 2); indices.Add(b + 3);
            }

            // A wide middle strip, so the channel reads as water rather than as two banks.
            for (var i = 0; i < vertices.Count; i += 2)
            {
                var middle = (vertices[i] + vertices[i + 1]) * 0.5f;
                vertices[i] = Vector3.Lerp(middle, vertices[i], 1.05f);
                vertices[i + 1] = Vector3.Lerp(middle, vertices[i + 1], 1.05f);
            }

            return Fill(mesh, "River", vertices, depths, indices);
        }

        static Mesh Fill(Mesh mesh, string name, List<Vector3> vertices, List<Vector2> depths, List<int> indices)
        {
            if (mesh == null)
                mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.Clear();
            if (vertices.Count == 0)
                return mesh;

            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetUVs(1, depths);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
