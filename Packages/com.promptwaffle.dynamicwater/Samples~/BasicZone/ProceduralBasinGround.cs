using UnityEngine;

namespace PromptWaffle.DynamicWater.Samples
{
    /// <summary>
    /// Ground for the Basic Zone sample, built in code so the sample needs no terrain asset:
    /// - an upper and a lower pool, split by a sill with a notch in it;
    /// - an island in the lower pool;
    /// - a spillway notch in the rim.
    /// It is both the zone's ground (a <see cref="WaterGroundProvider"/>) and a mesh with a
    /// collider, so the demo can click on it. <see cref="Dig"/> shows how a provider reports a
    /// change: edit the heights, then <see cref="WaterGroundProvider.RaiseChanged"/> with the
    /// world rectangle.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class ProceduralBasinGround : WaterGroundProvider
    {
        [Tooltip("Metres across, x and z, centred on this transform.")]
        [SerializeField] Vector2 _size = new Vector2(64f, 64f);

        [Tooltip("Metres between the heightfield's points.")]
        [SerializeField, Min(0.1f)] float _spacing = 0.5f;

        [SerializeField] int _seed = 3;

        float[] _heights;
        int _countX;
        int _countZ;
        Mesh _mesh;

        /// <summary>Height of the lip of the notch in the rim, where the sample's overflow sits.</summary>
        public const float SpillwayCrest = 0.4f;

        /// <summary>Height of the notch in the sill between the two pools.</summary>
        public const float SillNotch = 0.1f;

        public Vector2 Size => _size;

        public override bool IsReady => _heights != null;

        void Awake() => Build();

        /// <summary>Builds the heightfield and its mesh afresh, undoing any digging.</summary>
        public void Build()
        {
            _countX = Mathf.RoundToInt(_size.x / _spacing) + 1;
            _countZ = Mathf.RoundToInt(_size.y / _spacing) + 1;
            _heights = new float[_countX * _countZ];
            var offset = new Vector2(_seed * 17.3f, _seed * 31.7f);
            for (var j = 0; j < _countZ; j++)
                for (var i = 0; i < _countX; i++)
                    _heights[j * _countX + i] = Shape(Local(i, j), offset);
            BuildMesh();
            RaiseChanged(WorldRect());
        }

        /// <summary>Height of the designed basin at a local point (x, z), metres.</summary>
        float Shape(Vector2 p, Vector2 noiseOffset)
        {
            var halfX = _size.x * 0.5f;
            var halfZ = _size.y * 0.5f;

            // Pools: the lower one (south) 2.5 m down, the upper one (north) 1.2 m down.
            var floor = p.y > 4f ? -1.2f : -2.5f;

            // The sill across the middle, 0.4 m high, with a notch at x -3..-1 down to SillNotch.
            var height = Mathf.Lerp(floor, 0.4f, Mathf.Clamp01(1f - Mathf.Abs(p.y - 4f) / 1.5f));
            if (Mathf.Abs(p.y - 4f) < 1.5f && p.x > -3.5f && p.x < -0.5f)
                height = Mathf.Min(height, Mathf.Max(floor, Mathf.Lerp(SillNotch, height, Mathf.Clamp01((Mathf.Abs(p.x + 2f) - 1f) / 0.5f))));

            // An island in the lower pool.
            var island = 1f - (p - new Vector2(-10f, -12f)).magnitude / 6f;
            if (island > 0f)
                height = Mathf.Max(height, Mathf.Lerp(floor, 2f, Mathf.SmoothStep(0f, 1f, island * 1.4f)));

            // The rim: rises to 3 m over the last 6 m, except the spillway notch on the east side.
            var edge = Mathf.Max(Mathf.Abs(p.x) - (halfX - 7f), Mathf.Abs(p.y) - (halfZ - 7f));
            var rim = Mathf.Lerp(height, 3f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edge / 5f)));
            var inNotch = p.x > 0f && p.y > -11f && p.y < -5f;
            if (inNotch)
                rim = Mathf.Min(rim, Mathf.Max(height, SpillwayCrest));
            height = Mathf.Max(height, rim);

            // A little unevenness, so the water has something to find.
            height += (Mathf.PerlinNoise((p.x + noiseOffset.x) * 0.15f, (p.y + noiseOffset.y) * 0.15f) - 0.5f) * 0.3f
                * (inNotch ? 0f : 1f);
            return height;
        }

        /// <summary>Ground height at a world point, bilinear between the heightfield's points.</summary>
        public float HeightAt(Vector3 world)
        {
            var local = transform.InverseTransformPoint(world);
            var fx = Mathf.Clamp((local.x + _size.x * 0.5f) / _spacing, 0f, _countX - 1.001f);
            var fz = Mathf.Clamp((local.z + _size.y * 0.5f) / _spacing, 0f, _countZ - 1.001f);
            int i = (int)fx, j = (int)fz;
            float tx = fx - i, tz = fz - j;
            var a = Mathf.Lerp(_heights[j * _countX + i], _heights[j * _countX + i + 1], tx);
            var b = Mathf.Lerp(_heights[(j + 1) * _countX + i], _heights[(j + 1) * _countX + i + 1], tx);
            return Mathf.Lerp(a, b, tz) + transform.position.y;
        }

        /// <summary>
        /// Digs a smooth crater <paramref name="depth"/> metres deep, <paramref name="radius"/>
        /// across, and tells the zone which part of the ground changed.
        /// </summary>
        public void Dig(Vector3 world, float radius, float depth)
        {
            var local = transform.InverseTransformPoint(world);
            var ci = (local.x + _size.x * 0.5f) / _spacing;
            var cj = (local.z + _size.y * 0.5f) / _spacing;
            var reach = Mathf.CeilToInt(radius / _spacing);
            for (var j = Mathf.Max(0, (int)cj - reach); j <= Mathf.Min(_countZ - 1, (int)cj + reach + 1); j++)
                for (var i = Mathf.Max(0, (int)ci - reach); i <= Mathf.Min(_countX - 1, (int)ci + reach + 1); i++)
                {
                    var d = new Vector2(i - ci, j - cj).magnitude * _spacing / radius;
                    if (d < 1f)
                        _heights[j * _countX + i] -= depth * (1f - d * d) * (1f - d * d);
                }

            BuildMesh();
            RaiseChanged(new Rect(world.x - radius - _spacing, world.z - radius - _spacing, 2f * (radius + _spacing), 2f * (radius + _spacing)));
        }

        public override void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into)
        {
            var rect = WorldRect();
            var i = 0;
            for (var z = region.yMin; z < region.yMax; z++)
                for (var x = region.xMin; x < region.xMax; x++)
                {
                    var centre = desc.CellCentre(x, z);
                    into[i++] = rect.Contains(centre) && _heights != null
                        ? HeightAt(new Vector3(centre.x, 0f, centre.y))
                        : WaterGround.Wall;
                }
        }

        Vector2 Local(int i, int j) => new Vector2(i * _spacing - _size.x * 0.5f, j * _spacing - _size.y * 0.5f);

        Rect WorldRect()
        {
            var p = transform.position;
            return new Rect(p.x - _size.x * 0.5f, p.z - _size.y * 0.5f, _size.x, _size.y);
        }

        void BuildMesh()
        {
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "Procedural Basin", hideFlags = HideFlags.DontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                GetComponent<MeshFilter>().sharedMesh = _mesh;
            }

            var vertices = new Vector3[_countX * _countZ];
            for (var j = 0; j < _countZ; j++)
                for (var i = 0; i < _countX; i++)
                {
                    var p = Local(i, j);
                    vertices[j * _countX + i] = new Vector3(p.x, _heights[j * _countX + i], p.y);
                }

            if (_mesh.vertexCount != vertices.Length)
            {
                var triangles = new int[(_countX - 1) * (_countZ - 1) * 6];
                var t = 0;
                for (var j = 0; j < _countZ - 1; j++)
                    for (var i = 0; i < _countX - 1; i++)
                    {
                        var a = j * _countX + i;
                        triangles[t++] = a;
                        triangles[t++] = a + _countX;
                        triangles[t++] = a + 1;
                        triangles[t++] = a + 1;
                        triangles[t++] = a + _countX;
                        triangles[t++] = a + _countX + 1;
                    }

                _mesh.Clear();
                _mesh.vertices = vertices;
                _mesh.triangles = triangles;
            }
            else
            {
                _mesh.vertices = vertices;
            }

            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            var collider = GetComponent<MeshCollider>();
            collider.sharedMesh = null;
            collider.sharedMesh = _mesh;
        }

        void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
