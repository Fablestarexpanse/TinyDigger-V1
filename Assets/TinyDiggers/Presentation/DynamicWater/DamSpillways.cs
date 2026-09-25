using System.Collections.Generic;
using PromptWaffle.DynamicWater;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Makes the dam's spillways real outlets for the dynamic water: one Overflow effector per
    /// spillway, on the sea just inside the dam's inner face, with its crest at sea level and a
    /// weir as wide as the spillway. A safety valve: water at or under sea level is left alone,
    /// and anything that lifts the sea above it (sources, pumps, rain) runs out over the dam.
    ///
    /// Follows the dam's layout, so it rebuilds whenever the ring does. Each frame it reads the
    /// head over each crest from the zone and tells the dam how full to draw that spillway's
    /// falling water, so the sheets pour only while the outlets really run.
    /// </summary>
    public sealed class DamSpillways : MonoBehaviour
    {
        [SerializeField] DamView _dam;
        [SerializeField] TerrainView _terrain;
        [SerializeField] WaterZone _zone;

        [Tooltip("Metres. How far into the sea each spillway draws water from.")]
        [SerializeField, Min(0.5f)] float _reach = 8f;

        [Tooltip("Metres above sea level of the spillway crests.")]
        [SerializeField] float _crestAboveSea;

        [Tooltip("Metres of water over the crest at which a spillway's sheet is drawn full.")]
        [SerializeField, Min(0.01f)] float _fullHead = 0.4f;

        [Tooltip("Seconds for a sheet to follow a change in flow.")]
        [SerializeField, Min(0f)] float _response = 1f;

        /// <summary>Metres over the crest below which a spillway counts as dry (simulation noise).</summary>
        public const float MinHead = 0.005f;

        readonly List<WaterEffectorComponent> _outlets = new List<WaterEffectorComponent>();
        readonly List<float> _fullness = new List<float>();
        IReadOnlyList<DamSlot> _builtFor;
        float _builtRadius;

        /// <summary>The spillway outlets, one per spillway in the ring.</summary>
        public IReadOnlyList<WaterEffectorComponent> Outlets => _outlets;

        /// <summary>How full each spillway's sheet is drawn right now, 0..1, in outlet order.</summary>
        public IReadOnlyList<float> Fullness => _fullness;

        /// <summary>
        /// How full to draw a spillway with <paramref name="head"/> metres over its crest: none
        /// when dry, full at <paramref name="fullHead"/>. Grows as the square root of the weir
        /// flow (head^0.75), so a trickle still shows as a thin rope.
        /// </summary>
        public static float SheetFullness(float head, float fullHead)
        {
            if (head < MinHead)
                return 0f;
            return Mathf.Clamp01(Mathf.Pow(head / Mathf.Max(fullHead, 0.01f), 0.75f));
        }

        void Awake()
        {
            if (_zone == null)
                _zone = GetComponent<WaterZone>();
        }

        void Update()
        {
            if (_dam == null || _terrain == null || _terrain.Grid == null)
                return;
            if (!ReferenceEquals(_dam.Slots, _builtFor) || !Mathf.Approximately(_terrain.DiscRadius, _builtRadius))
                Build();
            Follow();
        }

        /// <summary>Eases each sheet toward the flow its outlet is carrying, and hands them to the dam.</summary>
        void Follow()
        {
            var query = _zone != null && _zone.Simulation != null ? _zone.Simulation.Query : null;
            if (query == null || !query.HasData)
                return;
            var ease = _response > 0f ? 1f - Mathf.Exp(-Time.deltaTime / _response) : 1f;
            for (var i = 0; i < _outlets.Count; i++)
            {
                var outlet = _outlets[i];
                var head = query.TrySample(outlet.transform.position, out var sample) && sample.IsWet
                    ? sample.Surface - outlet.Level
                    : 0f;
                _fullness[i] = Mathf.Lerp(_fullness[i], SheetFullness(head, _fullHead), ease);
            }

            _dam.SetSpillwayFlow(_fullness);
        }

        void Build()
        {
            Clear();
            _builtFor = _dam.Slots;
            _builtRadius = _terrain.DiscRadius;
            if (_dam.InnerRadius <= 0f)
                return;

            var centre = _terrain.DiscCentre;
            var origin = _terrain.transform.TransformPoint(new Vector3(centre.x, 0f, centre.y));
            // Wholly inside the disc, so none of the outlet's reach is wasted on the void.
            var radius = Mathf.Max(_reach, 0.5f);
            var distance = _terrain.DiscRadius - radius;
            foreach (var slot in _dam.Slots)
            {
                if (slot.Kind != DamPieceKind.Spillway)
                    continue;
                var angle = slot.Centre / _dam.InnerRadius;
                var outlet = new GameObject($"Spillway Outlet {_outlets.Count}").AddComponent<WaterEffectorComponent>();
                outlet.transform.SetParent(transform, false);
                outlet.transform.position = origin + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * distance;
                outlet.Kind = WaterEffectorKind.Overflow;
                outlet.Radius = radius;
                outlet.Rate = slot.Width;
                outlet.Level = World.SeaLevel + _crestAboveSea;
                _outlets.Add(outlet);
                _fullness.Add(0f);
            }
        }

        void Clear()
        {
            foreach (var outlet in _outlets)
                if (outlet != null)
                    Destroy(outlet.gameObject);
            _outlets.Clear();
            _fullness.Clear();
        }

        void OnDestroy() => Clear();
    }
}
