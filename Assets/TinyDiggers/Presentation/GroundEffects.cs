using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The dust and chunks that say a machine moved the ground rather than the ground changing by
    /// itself (Ronan, 2026-09-23). Three effects: a bite at the bucket, a tip off the bed, and a
    /// bloom where a slump lets go.
    ///
    /// **One system per effect, not one object per event.** The brief asked for a prefab each,
    /// pooled; a single world-space <see cref="ParticleSystem"/> per effect, emitted into with
    /// <see cref="ParticleSystem.EmitParams"/>, is the same thing without the pool: every bite on
    /// the island is one burst into one system, at whatever position and colour it wants, and
    /// Unity recycles the particles itself. Nothing is instantiated while the crew work.
    ///
    /// Colours come from <see cref="MaterialTable"/>, so spoil off a clay bank throws clay and a
    /// cut in rock throws grey.
    /// </summary>
    public sealed class GroundEffects : MonoBehaviour
    {
        /// <summary>Seconds a chunk or a puff lives. The brief's ceiling is 0.6 s.</summary>
        [SerializeField, Min(0.05f)] float _life = 0.55f;

        /// <summary>Chunks thrown by one bite, and by one tipped load.</summary>
        [SerializeField, Min(0)] int _biteChunks = 8;

        [SerializeField, Min(0)] int _tipChunks = 24;

        /// <summary>Cubic metres that have to leave one cell in one move before it reads as a collapse.</summary>
        [SerializeField, Min(0.1f)] float _collapseVolume = 2f;

        /// <summary>Off, for measuring what the effects cost.</summary>
        public bool Enabled = true;

        ParticleSystem _chunks;
        ParticleSystem _dust;
        TerrainGrid _grid;
        Transform _terrain;
        float _cellSize;

        /// <summary>Bursts asked for since the last reset, for the capture and the budget check.</summary>
        public int Bursts { get; private set; }

        public void Init(TerrainGrid grid, Transform terrain)
        {
            _grid = grid;
            _terrain = terrain;
            _cellSize = grid.CellSize;
            _chunks = MakeChunks();
            _dust = MakeDust();
        }

        /// <summary>Dust and a few chunks where a bucket bit, coloured by what came off.</summary>
        public void Bite(int x, int z, MaterialId material, float cubicMetres)
        {
            if (!Ready())
                return;
            var at = Surface(x, z);
            var colour = Colour(material);
            Throw(_chunks, at, colour, _biteChunks, 1.6f, 0.05f);
            Throw(_dust, at, colour, 3, 0.5f, 0.35f);
            Bursts++;
        }

        /// <summary>
        /// Chunks arcing off a bed and a bloom of dust where they land. More material means more
        /// of it, so a dumper's bed reads heavier than a barrow.
        /// </summary>
        public void Tip(int x, int z, MaterialId material, float cubicMetres)
        {
            if (!Ready())
                return;
            var at = Surface(x, z);
            var colour = Colour(material);
            var many = Mathf.Clamp(Mathf.RoundToInt(_tipChunks * Mathf.Clamp01(cubicMetres / 2.25f)), 6, _tipChunks);
            Throw(_chunks, at + Vector3.up * 0.4f, colour, many, 1.9f, 0.06f);
            Throw(_dust, at, colour, 5, 0.6f, 0.4f);
            Bursts++;
        }

        /// <summary>A bloom where ground let go, so a landslide reads as one.</summary>
        public void Collapse(int x, int z, MaterialId material)
        {
            if (!Ready())
                return;
            Throw(_dust, Surface(x, z), Colour(material), 8, 0.8f, 0.5f);
            Bursts++;
        }

        /// <summary>Whether a height change is big enough to read as ground letting go.</summary>
        public bool IsCollapse(float metres) => Mathf.Abs(metres) * _grid.CellArea >= _collapseVolume;

        public void ResetCount() => Bursts = 0;

        bool Ready() => Enabled && _chunks != null && _grid != null;

        Color Colour(MaterialId material) =>
            material.Value == 0 ? new Color(0.6f, 0.5f, 0.4f) : (Color)_grid.Materials.Get(material).Color;

        Vector3 Surface(int x, int z) =>
            _terrain.TransformPoint(new Vector3((x + 0.5f) * _cellSize,
                _grid.GetSurfaceHeight(x, z), (z + 0.5f) * _cellSize));

        /// <summary>One burst into a shared system: position, colour and count are all per-burst.</summary>
        static void Throw(ParticleSystem system, Vector3 at, Color colour, int count, float speed, float size)
        {
            if (system == null || count <= 0)
                return;
            var emit = new ParticleSystem.EmitParams
            {
                position = at,
                startColor = colour,
                startSize = size,
                applyShapeToPosition = true,
            };
            system.Emit(emit, count);
        }

        ParticleSystem MakeChunks()
        {
            var system = NewSystem("Ground Chunks", _life, 0.06f, 1.6f);
            var main = system.main;
            main.gravityModifier = 2.2f;
            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 42f;
            shape.radius = 0.12f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            // Chunks stop when they hit the ground rather than sinking through it.
            var collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.lifetimeLoss = 1f;
            collision.quality = ParticleSystemCollisionQuality.Medium;
            return system;
        }

        ParticleSystem MakeDust()
        {
            var system = NewSystem("Ground Dust", _life, 0.35f, 0.5f);
            var main = system.main;
            main.gravityModifier = -0.05f;   // it hangs and lifts a little
            main.startColor = new ParticleSystem.MinMaxGradient(Color.white);
            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.2f;
            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 2.2f));
            var colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(Fade());
            return system;
        }

        ParticleSystem NewSystem(string name, float life, float size, float speed)
        {
            var holder = new GameObject(name) { hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(transform, false);
            var system = holder.AddComponent<ParticleSystem>();

            var main = system.main;
            // Looping with nothing to emit: the system idles for ever and costs nothing, but it is
            // *running*, and a stopped system does not simulate the particles handed to it by
            // Emit — 83 bursts landed and none of them lived (2026-09-23).
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = life;
            main.startSize = size;
            main.startSpeed = speed;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 600;
            // Unscaled, like the height lag and the slump budget: a puff should read the same at
            // one times and at twenty.
            main.useUnscaledTime = true;

            var emission = system.emission;
            emission.enabled = false;   // everything is emitted by hand, in bursts

            var renderer = system.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.material = DustMaterial();
            renderer.trailMaterial = null;
            system.Play();
            return system;
        }

        static Gradient Fade()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0.35f, 0.4f), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        static Material _dustMaterial;

        /// <summary>One unlit, vertex-coloured material for every effect, so they all batch together.</summary>
        static Material DustMaterial()
        {
            if (_dustMaterial != null)
                return _dustMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Sprites/Default");
            _dustMaterial = new Material(shader) { name = "Ground Effect" };
            if (_dustMaterial.HasProperty("_Surface"))
                _dustMaterial.SetFloat("_Surface", 1f);   // transparent
            if (_dustMaterial.HasProperty("_Blend"))
                _dustMaterial.SetFloat("_Blend", 0f);     // alpha
            _dustMaterial.renderQueue = 3000;
            return _dustMaterial;
        }
    }
}
