using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The water: one sheet over the sea, clipped to the disc, and a ribbon down the river the
    /// generator cut. Both are built here rather than bought.
    ///
    /// What the shader needs to know about the land (depth, distance to the shore, which way the
    /// shore lies) is baked by <see cref="WaterField"/> into the mesh when the sheet is built. The
    /// sheets are rebuilt when the land changes, which is what <see cref="Rebuild"/> is for:
    /// reclaiming the shallows should push the shoreline, and the breaking waves, back.
    ///
    /// The swell is a <see cref="WaveSet"/> made from <see cref="WaterSettings"/> and handed to the
    /// shader as globals; everything else in the settings goes onto the material.
    /// </summary>
    public sealed class WaterView : MonoBehaviour
    {
        static readonly int WaveCountId = Shader.PropertyToID("_TDWaveCount");
        static readonly int WaveAId = Shader.PropertyToID("_TDWaveA");
        static readonly int WaveBId = Shader.PropertyToID("_TDWaveB");

        [SerializeField] TerrainView _terrain;
        [SerializeField] WaterSettings _settings;

        [Tooltip("Cells between vertices of the sea sheet. The swell needs a vertex every couple of " +
            "metres or its shortest waves turn into spikes.")]
        [SerializeField, Min(1)] int _seaResolution = 2;

        [Tooltip("Cells the sea runs past the edge of the land, so it meets the plinth wall.")]
        [SerializeField, Min(0f)] float _seaOverhang = 5f;

        [Tooltip("Metres the river surface sits above its channel floor.")]
        [SerializeField, Min(0f)] float _riverDepth = 1.2f;

        [Tooltip("Rebuild the sheets this many seconds after the land last changed.")]
        [SerializeField, Min(0f)] float _rebuildDelay = 0.5f;

        Material _material;
        Texture2D _detail;
        Wave[] _waves;
        readonly Vector4[] _waveA = new Vector4[8];
        readonly Vector4[] _waveB = new Vector4[8];
        GameObject _sea;
        GameObject _river;
        bool _visible = true;

        /// <summary>
        /// Whether the static sea and river draw. Off while PromptWaffle Dynamic Water draws the
        /// water instead (DynamicWaterBridge), so the two surfaces never fight.
        /// </summary>
        public bool Visible
        {
            get => _visible;
            set
            {
                _visible = value;
                ApplyVisible();
            }
        }

        void ApplyVisible()
        {
            if (_sea != null)
                _sea.GetComponent<MeshRenderer>().enabled = _visible;
            if (_river != null)
                _river.GetComponent<MeshRenderer>().enabled = _visible;
        }
        Mesh _seaMesh;
        Mesh _riverMesh;
        float _dirtyAt = -1f;

        /// <summary>Triangles across both sheets, for the perf report.</summary>
        public int TriangleCount { get; private set; }

        /// <summary>The field the sheets were last built from.</summary>
        public WaterField Field { get; private set; }

        /// <summary>The swell the shader is running, for anything that wants to float on it.</summary>
        public IReadOnlyList<Wave> Waves => _waves;

        public WaterSettings Settings => _settings;

        void OnValidate()
        {
            if (_material != null)
                Apply();
        }

        void Start()
        {
            if (_terrain == null || _terrain.Grid == null)
                return;
            if (_settings == null)
                _settings = ScriptableObject.CreateInstance<WaterSettings>();

            var shader = Shader.Find("TinyDiggers/Water");
            if (shader == null)
            {
                Debug.LogError("WaterView: the water shader is missing.");
                enabled = false;
                return;
            }

            _material = new Material(shader) { name = "Water", hideFlags = HideFlags.DontSave };
            _detail = WaterDetailTexture.Create();
            Apply();
            _sea = NewSheet("Sea");
            _river = NewSheet("River");
            ApplyVisible();
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
            Destroy(_detail);
            Destroy(_seaMesh);
            Destroy(_riverMesh);
        }

        /// <summary>Puts the settings onto the material and the swell into the shader's globals.</summary>
        public void Apply()
        {
            if (_material == null || _settings == null)
                return;

            var s = _settings;
            _material.SetTexture("_Detail", _detail);
            _material.SetColor("_ScatterShallow", s.ScatterShallow);
            _material.SetColor("_ScatterDeep", s.ScatterDeep);
            _material.SetVector("_Absorption", s.Absorption);
            _material.SetFloat("_ScatterDensity", s.ScatterDensity);
            _material.SetFloat("_Refraction", s.Refraction);

            var wind = s.WindDegrees * Mathf.Deg2Rad;
            _material.SetVector("_Wind", new Vector4(Mathf.Cos(wind), Mathf.Sin(wind), 0f, 0f));
            _material.SetFloat("_GustSize", s.GustSize);
            _material.SetFloat("_GustCalm", s.GustCalm);
            _material.SetFloat("_DampDepth", s.DampDepth);
            _material.SetFloat("_DampDistance", s.DampDistance);
            _material.SetFloat("_Whitecaps", s.Whitecaps);
            _material.SetFloat("_ShoreReach", s.ShoreReach);
            _material.SetFloat("_ShoreSpacing", s.ShoreSpacing);
            _material.SetFloat("_ShoreSpeed", s.ShoreSpeed);
            _material.SetFloat("_ShoreLift", s.ShoreLift);
            _material.SetFloat("_ShoreFoam", s.ShoreFoam);
            _material.SetFloat("_RippleSize", s.RippleSize);
            _material.SetFloat("_RippleDetailSize", s.RippleDetailSize);
            _material.SetFloat("_RippleStrength", s.RippleStrength);
            _material.SetFloat("_RippleSpeed", s.RippleSpeed);
            _material.SetFloat("_RippleFade", s.RippleFade);
            _material.SetColor("_SkyHorizon", s.SkyHorizon);
            _material.SetColor("_SkyZenith", s.SkyZenith);
            _material.SetFloat("_Reflection", s.Reflection);
            _material.SetFloat("_FresnelPower", s.FresnelPower);
            _material.SetFloat("_SunGlint", s.SunGlint);
            _material.SetFloat("_SunSharpness", s.SunSharpness);
            _material.SetFloat("_Caustics", s.Caustics);
            _material.SetFloat("_CausticSize", s.CausticSize);
            _material.SetColor("_Foam", s.Foam);
            _material.SetFloat("_ContactFoam", s.ContactFoam);

            _waves = WaveSet.Generate(s);
            var tallest = 0f;
            for (var i = 0; i < _waveA.Length; i++)
            {
                if (i < _waves.Length)
                {
                    var wave = _waves[i];
                    _waveA[i] = new Vector4(wave.Direction.x, wave.Direction.y, wave.Amplitude, wave.Number);
                    _waveB[i] = new Vector4(wave.Steepness, wave.Speed, wave.Phase, 0f);
                    tallest += wave.Amplitude;
                }
                else
                {
                    _waveA[i] = Vector4.zero;
                    _waveB[i] = Vector4.zero;
                }
            }

            _material.SetFloat("_SwellHeight", Mathf.Max(0.05f, tallest));
            Shader.SetGlobalInt(WaveCountId, _waves.Length);
            Shader.SetGlobalVectorArray(WaveAId, _waveA);
            Shader.SetGlobalVectorArray(WaveBId, _waveB);
        }

        /// <summary>
        /// A cell changed. The sheets only need rebuilding if that cell could move the waterline —
        /// if it is water, touches water, or is low enough to become water.
        ///
        /// It used to mark them dirty wherever the change was, and a rebuild bakes the water field
        /// over every one of the island's 9.6 million cells and remeshes both sheets: profiling a
        /// stutter found `WaterView.Update` taking **1,396 ms of a 1,407 ms frame** (2026-09-23).
        /// The crew spend nearly all their time digging ground that is nowhere near the sea, and
        /// none of that can change where the water is.
        /// </summary>
        void OnCellChanged(int x, int z)
        {
            if (_dirtyAt >= 0f)
                return;   // already waiting to rebuild; no need to look
            if (!CouldMoveTheWaterline(x, z))
                return;
            _dirtyAt = Time.time;
        }

        /// <summary>Whether a change at (x, z) could put water somewhere it was not, or take it away.</summary>
        bool CouldMoveTheWaterline(int x, int z)
        {
            var grid = _terrain.Grid;
            if (grid == null)
                return true;

            // Near enough the water's level to matter, counting the rim the field blurs over.
            const float Margin = 2f;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    var nz = z + dz;
                    if (!grid.InBounds(nx, nz))
                        continue;
                    if (grid.IsWater(nx, nz))
                        return true;
                    if (grid.GetSurfaceHeight(nx, nz) <= World.SeaLevel + Margin)
                        return true;
                }
            }

            return false;
        }

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

            Field = WaterField.Bake(_terrain.Grid);
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
            // Built in cells, like the grid it follows, and scaled to metres at the end.
            var grid = _terrain.Grid;
            var cellSize = grid.CellSize;
            var step = Mathf.Max(1, Mathf.RoundToInt(_seaResolution / cellSize));
            var centre = _terrain.DiscCentre / cellSize;
            var radius = (_terrain.DiscRadius + _seaOverhang) / cellSize;

            var columns = grid.Width / step + 1;
            var rows = grid.Height / step + 1;
            var vertices = new List<Vector3>(columns * rows);
            var depths = new List<Vector2>(columns * rows);
            var shores = new List<Vector2>(columns * rows);
            var directions = new List<Vector2>(columns * rows);
            var indices = new List<int>(columns * rows * 6);
            var lookup = new int[columns * rows];
            for (var i = 0; i < lookup.Length; i++)
                lookup[i] = -1;

            var field = Field;

            // Dry land under a corner, read from the land itself rather than the smoothed depth,
            // so the sheet stops where the water does.
            float RawDepthAt(int x, int z)
            {
                x = Mathf.Clamp(x, 0, grid.Width - 1);
                z = Mathf.Clamp(z, 0, grid.Height - 1);
                if (!grid.IsGround(x, z))
                    return 12f; // Past the rim: open water.
                return World.SeaLevel - grid.GetSurfaceHeight(x, z);
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
                depths.Add(new Vector2(field.DepthAt(x, z), 0f));
                shores.Add(new Vector2(field.ShoreDistanceAt(x, z), 0f));
                directions.Add(field.ShoreDirectionAt(x, z));
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

            return Fill(mesh, "Sea", vertices, depths, shores, directions, indices, cellSize);
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
            var flows = new List<Vector2>();
            var directions = new List<Vector2>();
            var indices = new List<int>();
            if (island == null || island.River.Count < 2)
                return Fill(mesh, "River", vertices, depths, flows, directions, indices);
            var flowSpeed = _settings != null ? _settings.FlowSpeed : 1.2f;

            // The river's points are in cells; the ribbon is built in cells and scaled to metres at
            // the end. The settings' RiverWidth is metres.
            var cellSize = _terrain.Grid.CellSize;
            var settings = _terrain.Settings;
            var halfWidth = Mathf.Max(1f, (settings != null ? settings.RiverWidth : 4) * 0.5f) / cellSize;
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
                    travelled += Vector3.Distance(new Vector3(previous.x, 0f, previous.z), new Vector3(point.x, 0f, point.z)) * cellSize;

                // The surface stands above the floor, but never above the sea: where the channel
                // reaches the coast the river simply becomes the sea.
                var surface = Mathf.Min(point.y + _riverDepth, World.SeaLevel + _riverDepth);
                var left = new Vector3(point.x, surface, point.z) - across;
                var right = new Vector3(point.x, surface, point.z) + across;

                vertices.Add(left);
                vertices.Add(right);
                var depth = surface - point.y;
                // The banks are shallow, the middle is not: that is what foams the edges.
                depths.Add(new Vector2(depth, travelled));
                depths.Add(new Vector2(depth, travelled));

                // Steeper reaches run faster: a metre of drop in ten metres doubles the speed.
                var run = Mathf.Max(0.5f, new Vector2(next.x - previous.x, next.z - previous.z).magnitude * cellSize);
                var drop = Mathf.Max(0f, previous.y - next.y) / run;
                var speed = flowSpeed * (1f + drop * 10f);
                // Past the reach of the shore waves, so none break on a river.
                flows.Add(new Vector2(WaterField.Open, speed));
                flows.Add(new Vector2(WaterField.Open, speed));
                var flowDirection = new Vector2(along.x, along.z);
                directions.Add(flowDirection);
                directions.Add(flowDirection);

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

            return Fill(mesh, "River", vertices, depths, flows, directions, indices, cellSize);
        }

        /// <summary>Uploads a sheet built in cells, scaling x and z by <paramref name="cellSize"/> into metres.</summary>
        static Mesh Fill(Mesh mesh, string name, List<Vector3> vertices, List<Vector2> depths,
            List<Vector2> shores, List<Vector2> directions, List<int> indices, float cellSize = 1f)
        {
            if (!Mathf.Approximately(cellSize, 1f))
                for (var i = 0; i < vertices.Count; i++)
                    vertices[i] = new Vector3(vertices[i].x * cellSize, vertices[i].y, vertices[i].z * cellSize);

            if (mesh == null)
                mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.Clear();
            if (vertices.Count == 0)
                return mesh;

            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetUVs(1, depths);
            mesh.SetUVs(2, shores);
            mesh.SetUVs(3, directions);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // The swell moves the surface in the shader; keep the sheet from being culled for it.
            var bounds = mesh.bounds;
            bounds.Expand(new Vector3(2f, 4f, 2f));
            mesh.bounds = bounds;
            return mesh;
        }
    }
}
