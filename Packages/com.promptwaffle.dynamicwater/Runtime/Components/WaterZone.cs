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

        readonly List<WaterEffector> _effectors = new List<WaterEffector>();
        WaterEffector[] _effectorArray = new WaterEffector[0];

        public WaterSimulation Simulation { get; private set; }

        /// <summary>World rectangle (x, z) the zone covers.</summary>
        public Rect Bounds
        {
            get
            {
                var p = transform.position;
                return new Rect(p.x - _size.x * 0.5f, p.z - _size.y * 0.5f, _size.x, _size.y);
            }
        }

        public bool TrySample(Vector3 world, out WaterSample sample)
        {
            sample = default;
            return Simulation != null && Simulation.Query.TrySample(world, out sample);
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

        void OnEnable()
        {
            Active.Add(this);
            if (_ground == null)
                _ground = GetComponent<WaterGroundProvider>();
            if (_ground == null)
            {
                Debug.LogError($"{name}: a WaterZone needs a WaterGroundProvider.", this);
                enabled = false;
                return;
            }

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
                return;
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
