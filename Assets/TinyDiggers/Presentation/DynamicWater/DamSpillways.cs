using System.Collections.Generic;
using PromptWaffle.DynamicWater;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Makes the dam's spillways real outlets for the dynamic water: one Overflow effector per
    /// spillway, on the sea just inside the dam's inner face, with its crest at sea level and a
    /// weir as wide as the spillway. A safety valve: water at or under sea level is left alone,
    /// and anything that lifts the sea above it (sources, pumps, rain) runs out over the dam.
    ///
    /// Follows the dam's layout, so it rebuilds whenever the ring does.
    /// </summary>
    public sealed class DamSpillways : MonoBehaviour
    {
        [SerializeField] DamView _dam;
        [SerializeField] TerrainView _terrain;

        [Tooltip("Metres. How far into the sea each spillway draws water from.")]
        [SerializeField, Min(0.5f)] float _reach = 8f;

        [Tooltip("Metres above sea level of the spillway crests.")]
        [SerializeField] float _crestAboveSea;

        readonly List<WaterEffectorComponent> _outlets = new List<WaterEffectorComponent>();
        IReadOnlyList<DamSlot> _builtFor;
        float _builtRadius;

        /// <summary>The spillway outlets, one per spillway in the ring.</summary>
        public IReadOnlyList<WaterEffectorComponent> Outlets => _outlets;

        void Update()
        {
            if (_dam == null || _terrain == null || _terrain.Grid == null)
                return;
            if (ReferenceEquals(_dam.Slots, _builtFor) && Mathf.Approximately(_terrain.DiscRadius, _builtRadius))
                return;
            Build();
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
            }
        }

        void Clear()
        {
            foreach (var outlet in _outlets)
                if (outlet != null)
                    Destroy(outlet.gameObject);
            _outlets.Clear();
        }

        void OnDestroy() => Clear();
    }
}
