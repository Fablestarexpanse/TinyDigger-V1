using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The dam that rings the world (the disc floats in space, and this is what holds it
    /// together). Lays the ring out with <see cref="DamLayout"/>, bends each kit piece onto the
    /// circle with <see cref="DamBend"/>, and merges them into a few sector meshes, one submesh
    /// per surface, so the whole ring is a handful of draw calls. The spillway water is its own
    /// mesh per sector, because it is drawn transparent.
    ///
    /// Built from the kit at runtime, so there is nothing to keep in the scene file.
    /// </summary>
    public sealed class DamView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] DamSettings _settings;
        [SerializeField] DamKit _kit;

        [Tooltip("Sector meshes the ring is split into, so the camera can cull what is behind it.")]
        [SerializeField, Range(1, 64)] int _sectors = 16;

        [SerializeField] int _seed = 7;

        static readonly int DamCentreId = Shader.PropertyToID("_DamCentre");
        static readonly int SpillFlowId = Shader.PropertyToID("_DamSpillFlow");

        /// <summary>Spillways the water shader can tell apart; must match _DamSpillFlow in Spillwater.shader.</summary>
        public const int MaxSpillways = 32;

        readonly float[] _spillFlow = new float[MaxSpillways];

        readonly List<GameObject> _built = new List<GameObject>();

        /// <summary>The ring as laid out. Empty until built.</summary>
        public IReadOnlyList<DamSlot> Slots { get; private set; } = new List<DamSlot>();

        /// <summary>Metres from the middle of the disc to the dam's inner face.</summary>
        public float InnerRadius { get; private set; }

        void Start()
        {
            if (_terrain == null || _terrain.Grid == null || _settings == null || _kit == null)
            {
                Debug.LogError("DamView: needs a TerrainView, DamSettings and a DamKit.");
                return;
            }

            Build();
        }

        public void Build()
        {
            Clear();
            InnerRadius = _terrain.DiscRadius + _settings.InnerOffset;
            var slots = DamLayout.Build(_settings, InnerRadius, _seed);
            Slots = slots;

            var sectors = new SectorBuilder[_sectors];
            for (var i = 0; i < sectors.Length; i++)
                sectors[i] = new SectorBuilder();
            var circumference = 2f * Mathf.PI * InnerRadius;

            var tones = new System.Random(_seed);
            var spillway = 0;
            foreach (var slot in slots)
            {
                var angle = slot.Centre / InnerRadius;
                var sector = sectors[Mathf.Clamp(Mathf.FloorToInt(slot.Centre / circumference * _sectors), 0, _sectors - 1)];
                // Each piece a slightly different pour: its tone rides in uv0.w.
                var tone = (float)tones.NextDouble();
                sector.Add(Piece(slot.Kind), angle, InnerRadius, slot.Stretch, tone);
                if (slot.Kind == DamPieceKind.Spillway)
                    sector.Add(_kit.SpillwayWater, angle, InnerRadius, slot.Stretch, tone, Mathf.Min(spillway++, MaxSpillways - 1));
                if (slot.Kind == DamPieceKind.Tower)
                    sector.Add(_kit.Tower, angle, InnerRadius, slot.Stretch, (float)tones.NextDouble());
            }

            var centre = _terrain.DiscCentre;
            var origin = _terrain.transform.TransformPoint(new Vector3(centre.x, 0f, centre.y));
            Shader.SetGlobalVector(DamCentreId, origin);
            // Every spillway pours at full flow until something reports the real flow.
            for (var i = 0; i < _spillFlow.Length; i++)
                _spillFlow[i] = 1f;
            Shader.SetGlobalFloatArray(SpillFlowId, _spillFlow);
            for (var i = 0; i < sectors.Length; i++)
            {
                var solid = sectors[i].Solid(_kit, $"Dam Sector {i}");
                if (solid != null)
                    _built.Add(Spawn(solid.Value.mesh, solid.Value.materials, origin, ShadowCastingMode.On));
                var water = sectors[i].Water($"Dam Water {i}");
                if (water != null)
                    _built.Add(Spawn(water, new[] { _kit.SpillWater }, origin, ShadowCastingMode.Off));
            }

            // The floor: the disc floats in space, and without the old plinth's capped bottom the
            // seabed's edge showed from underneath as a comb of spikes below the dam.
            _built.Add(Spawn(Floor(InnerRadius + 3f, _settings.FloorDepth), new[] { _kit.Concrete }, origin, ShadowCastingMode.Off));

            Debug.Log($"DamView: {slots.Count} pieces round {circumference:0} m at radius {InnerRadius:0} m " +
                $"({Count(slots, DamPieceKind.Terminal)} terminals, {Count(slots, DamPieceKind.Spillway)} spillways, " +
                $"{Count(slots, DamPieceKind.Pad)} pads, {Count(slots, DamPieceKind.Tower)} towers), {_built.Count} meshes.");
        }

        /// <summary>
        /// How full each spillway's falling water is drawn, 0 (dry) to 1 (full sheet), in the
        /// order the spillways appear in <see cref="Slots"/>. Spillways past the end keep theirs.
        /// </summary>
        public void SetSpillwayFlow(IReadOnlyList<float> fullness)
        {
            for (var i = 0; i < fullness.Count && i < MaxSpillways; i++)
                _spillFlow[i] = Mathf.Clamp01(fullness[i]);
            Shader.SetGlobalFloatArray(SpillFlowId, _spillFlow);
        }

        /// <summary>A disc facing down at <paramref name="depth"/>, closing the world from below.</summary>
        static Mesh Floor(float radius, float depth)
        {
            const int Segments = 256;
            var vertices = new Vector3[Segments + 1];
            var triangles = new int[Segments * 3];
            vertices[0] = new Vector3(0f, depth, 0f);
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                vertices[i + 1] = new Vector3(Mathf.Sin(a) * radius, depth, Mathf.Cos(a) * radius);
                // The rim runs clockwise seen from above; taking it backwards makes the
                // triangles clockwise seen from below, so the floor faces down.
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = (i + 1) % Segments + 1;
                triangles[i * 3 + 2] = i + 1;
            }

            var mesh = new Mesh { name = "Dam Floor", hideFlags = HideFlags.DontSave, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static int Count(List<DamSlot> slots, DamPieceKind kind)
        {
            var n = 0;
            foreach (var s in slots)
                if (s.Kind == kind)
                    n++;
            return n;
        }

        GameObject Piece(DamPieceKind kind) => kind switch
        {
            DamPieceKind.Spillway => _kit.Spillway,
            DamPieceKind.Pad => _kit.Pad,
            DamPieceKind.Terminal => _kit.Terminal,
            _ => _kit.Bay,
        };

        GameObject Spawn(Mesh mesh, Material[] materials, Vector3 origin, ShadowCastingMode shadows)
        {
            var go = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(origin, _terrain.transform.rotation);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = shadows;
            return go;
        }

        void Clear()
        {
            foreach (var go in _built)
            {
                if (go == null)
                    continue;
                var filter = go.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                    Destroy(filter.sharedMesh);
                Destroy(go);
            }

            _built.Clear();
        }

        void OnDestroy() => Clear();

        /// <summary>Gathers bent pieces for one sector: vertices, colours and triangles per surface.</summary>
        sealed class SectorBuilder
        {
            readonly List<Vector3> _vertices = new List<Vector3>();
            readonly List<Color> _colours = new List<Color>();
            readonly List<Vector4> _wall = new List<Vector4>();
            readonly List<Vector4> _waterWall = new List<Vector4>();
            readonly List<Vector2> _waterSpillway = new List<Vector2>();
            readonly List<int>[] _triangles = new List<int>[DamKit.SurfaceCount];
            readonly List<Vector3> _waterVertices = new List<Vector3>();
            readonly List<Color> _waterColours = new List<Color>();
            readonly List<int> _waterTriangles = new List<int>();

            public SectorBuilder()
            {
                for (var i = 0; i < _triangles.Length; i++)
                    _triangles[i] = new List<int>();
            }

            /// <summary>
            /// Bends a piece into the sector. uv0 carries where each vertex sits on the wall, for
            /// the concrete shader's formwork: x metres along the ring (measured at the inner face),
            /// y height, z metres out from the inner face, w the piece's tone. Spillway water also
            /// carries its spillway's index in uv1.x, so each sheet follows its own flow.
            /// </summary>
            public void Add(GameObject piece, float angle, float radius, float stretch, float tone, int spillway = 0)
            {
                if (piece == null)
                    return;
                var filter = piece.GetComponentInChildren<MeshFilter>();
                var renderer = piece.GetComponentInChildren<MeshRenderer>();
                if (filter == null || filter.sharedMesh == null)
                    return;
                var mesh = filter.sharedMesh;
                var source = mesh.vertices;
                var colours = mesh.colors;
                var materials = renderer != null ? renderer.sharedMaterials : new Material[0];

                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var surface = sub < materials.Length && materials[sub] != null
                        ? DamKit.SurfaceOf(materials[sub].name)
                        : DamSurface.Concrete;
                    var water = surface == DamSurface.SpillWater;
                    var vertices = water ? _waterVertices : _vertices;
                    var vertexColours = water ? _waterColours : _colours;
                    var wall = water ? _waterWall : _wall;
                    var triangles = water ? _waterTriangles : _triangles[(int)surface];

                    // Each submesh copies the vertices it uses, so a piece's vertices are never
                    // shared between surfaces.
                    var remap = new Dictionary<int, int>();
                    foreach (var index in mesh.GetTriangles(sub))
                    {
                        if (!remap.TryGetValue(index, out var mapped))
                        {
                            mapped = vertices.Count;
                            remap[index] = mapped;
                            vertices.Add(DamBend.Point(source[index], angle, radius, stretch));
                            vertexColours.Add(index < colours.Length ? colours[index] : Color.white);
                            var kit = source[index];
                            wall.Add(new Vector4(angle * radius - kit.x * stretch, kit.y, -kit.z, tone));
                            if (water)
                                _waterSpillway.Add(new Vector2(spillway, 0f));
                        }

                        triangles.Add(mapped);
                    }
                }
            }

            public (Mesh mesh, Material[] materials)? Solid(DamKit kit, string name)
            {
                if (_vertices.Count == 0)
                    return null;
                var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(_vertices);
                mesh.SetColors(_colours);
                mesh.SetUVs(0, _wall);
                var used = new List<Material>();
                var subs = new List<List<int>>();
                for (var s = 0; s < _triangles.Length; s++)
                {
                    if (_triangles[s].Count == 0 || s == (int)DamSurface.SpillWater)
                        continue;
                    subs.Add(_triangles[s]);
                    used.Add(kit.MaterialFor((DamSurface)s));
                }

                mesh.subMeshCount = subs.Count;
                for (var s = 0; s < subs.Count; s++)
                    mesh.SetTriangles(subs[s], s, false);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return (mesh, used.ToArray());
            }

            public Mesh Water(string name)
            {
                if (_waterVertices.Count == 0)
                    return null;
                var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(_waterVertices);
                mesh.SetColors(_waterColours);
                mesh.SetUVs(0, _waterWall);
                mesh.SetUVs(1, _waterSpillway);
                mesh.SetTriangles(_waterTriangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
