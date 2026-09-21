using System.Collections.Generic;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// A shallow-water simulation zone in the scene: a rectangle of the world, centred on this
    /// transform, where water flows over the ground a <see cref="WaterGroundProvider"/> gives it.
    /// Thin: it builds a <see cref="WaterSimulation"/>, feeds it the effectors inside its bounds each
    /// frame, steps it, and refreshes its query. Renderers read <see cref="Simulation"/>.
    /// </summary>
    [AddComponentMenu("PromptWaffle/Dynamic Water/Water Zone")]
    [DefaultExecutionOrder(-50)]
    public sealed class WaterZone : MonoBehaviour
    {
        static readonly List<WaterZone> Active = new List<WaterZone>();

        public static IReadOnlyList<WaterZone> All => Active;

        [Tooltip("What the water flows over. Defaults to a provider on this GameObject.")]
        [SerializeField] WaterGroundProvider _ground;

        [Tooltip("Metres across, x and z. Cells = size / cell size.")]
        [SerializeField] Vector2 _size = new Vector2(64f, 64f);

        [SerializeField, Min(0.05f)] float _cellSize = 0.5f;

        [Tooltip("Fill every open cell below this height when the zone starts.")]
        [SerializeField] bool _fillOnStart = true;

        [SerializeField] float _startLevel;

        [SerializeField] WaterSimulationDesc _tuning = WaterSimulationDesc.Default(2, 2, 1f, Vector2.zero);

        [Tooltip("Optional swell drawn and sampled on top of the simulated water.")]
        [SerializeField] WaterWaveSettings _waves;

        GerstnerWave[] _waveSet = new GerstnerWave[0];
        WaterWaveSettings _waveSetFrom;
        int _waveSetVersion = -1;

        readonly List<WaterEffector> _effectors = new List<WaterEffector>();
        WaterEffector[] _effectorArray = new WaterEffector[0];

        public WaterSimulation Simulation { get; private set; }

        public WaterWaveSettings WaveSettings
        {
            get => _waves;
            set => _waves = value;
        }

        /// <summary>The swell as generated from <see cref="WaveSettings"/>; empty without settings. Regenerated when they change.</summary>
        public IReadOnlyList<GerstnerWave> Waves
        {
            get
            {
                var version = _waves != null ? _waves.Version : 0;
                if (_waves != _waveSetFrom || version != _waveSetVersion)
                {
                    _waveSet = _waves != null ? WaterWaves.Generate(_waves) : new GerstnerWave[0];
                    _waveSetFrom = _waves;
                    _waveSetVersion = version;
                }

                return _waveSet;
            }
        }

        /// <summary>World rectangle (x, z) the zone covers.</summary>
        public Rect Bounds
        {
            get
            {
                var p = transform.position;
                return new Rect(p.x - _size.x * 0.5f, p.z - _size.y * 0.5f, _size.x, _size.y);
            }
        }

        /// <summary>
        /// The water at a world point: the simulated surface plus the swell, as drawn. The swell's
        /// sideways drift is ignored (it moves the sample point by at most a few centimetres).
        /// </summary>
        public bool TrySample(Vector3 world, out WaterSample sample)
        {
            sample = default;
            if (Simulation == null || !Simulation.Query.TrySample(world, out sample))
                return false;
            if (_waves != null && sample.IsWet)
            {
                var damping = WaterWaves.Damping(_waves, sample.Depth, world.x, world.z);
                sample.Surface += WaterWaves.Displacement(Waves, world.x, world.z, Time.timeSinceLevelLoad, damping).y;
            }

            return true;
        }

        /// <summary>Samples whichever active zone covers the point.</summary>
        public static bool TrySampleAny(Vector3 world, out WaterSample sample)
        {
            foreach (var zone in Active)
                if (zone.Bounds.Contains(new Vector2(world.x, world.z)) && zone.TrySample(world, out sample))
                    return true;
            sample = default;
            return false;
        }

        /// <summary>
        /// Sets the zone's size, cell size and starting level from code; takes effect on the next
        /// <see cref="Rebuild"/> (or when the simulation is first created).
        /// </summary>
        public void Configure(Vector2 size, float cellSize, float startLevel)
        {
            _size = size;
            _cellSize = cellSize;
            _startLevel = startLevel;
        }

        /// <summary>Throws the simulation away; a new one is built from the ground next frame.</summary>
        public void Rebuild()
        {
            Simulation?.Dispose();
            Simulation = null;
        }

        void OnEnable()
        {
            Active.Add(this);
            if (_ground == null)
                _ground = GetComponent<WaterGroundProvider>();
            if (_ground == null)
            {
                Debug.LogError($"{name}: a WaterZone needs a WaterGroundProvider.", this);
                enabled = false;
            }
        }

        /// <summary>
        /// Built lazily, once the ground is ready: OnEnable can run before the objects that build
        /// the ground have woken up.
        /// </summary>
        void CreateSimulation()
        {
            var bounds = Bounds;
            var desc = _tuning;
            desc.CellSize = _cellSize;
            desc.Width = Mathf.Max(2, Mathf.RoundToInt(_size.x / _cellSize));
            desc.Height = Mathf.Max(2, Mathf.RoundToInt(_size.y / _cellSize));
            desc.Origin = new Vector2(bounds.xMin, bounds.yMin);
            if (desc.FixedStep <= 0f)
                desc = WaterSimulationDesc.Default(desc.Width, desc.Height, desc.CellSize, desc.Origin);
            desc.FixedStep = Mathf.Min(desc.FixedStep, desc.StableStep * 0.9f);
            Simulation = new WaterSimulation(desc, _ground);
            if (_fillOnStart)
                Simulation.FillTo(_startLevel);
        }

        void OnDisable()
        {
            Active.Remove(this);
            Simulation?.Dispose();
            Simulation = null;
        }

        void Update()
        {
            if (Simulation == null)
            {
                if (_ground == null || !_ground.IsReady)
                    return;
                CreateSimulation();
            }

            GatherEffectors();
            Simulation.Step(Time.deltaTime);
            Simulation.Query.Update();
        }

        void GatherEffectors()
        {
            _effectors.Clear();
            var bounds = Bounds;
            foreach (var component in WaterEffectorComponent.All)
            {
                var p = component.transform.position;
                var r = component.Radius;
                if (p.x + r < bounds.xMin || p.x - r > bounds.xMax || p.z + r < bounds.yMin || p.z - r > bounds.yMax)
                    continue;
                _effectors.Add(component.ToEffector());
            }

            if (_effectorArray.Length != _effectors.Count)
                _effectorArray = new WaterEffector[_effectors.Count];
            _effectors.CopyTo(_effectorArray);
            Simulation.SetEffectors(_effectorArray);
        }

        void OnDrawGizmos()
        {
            var b = Bounds;
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.6f);
            Gizmos.DrawWireCube(new Vector3(b.center.x, transform.position.y, b.center.y), new Vector3(b.width, 0.1f, b.height));
        }
    }
}
