using System.Collections.Generic;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// A source, drain, level or force placed in the scene. Every enabled one inside a zone's
    /// bounds acts on it; move, resize or retune it at runtime and the zone picks it up next frame.
    /// </summary>
    [AddComponentMenu("PromptWaffle/Dynamic Water/Water Effector")]
    public sealed class WaterEffectorComponent : MonoBehaviour
    {
        static readonly List<WaterEffectorComponent> Active = new List<WaterEffectorComponent>();

        /// <summary>Every enabled effector in loaded scenes.</summary>
        public static IReadOnlyList<WaterEffectorComponent> All => Active;

        public WaterEffectorKind Kind = WaterEffectorKind.Source;

        [Tooltip("Metres.")]
        [Min(0.01f)] public float Radius = 2f;

        [Tooltip("Source and drain: m³/s. Level: 0..1 of the gap closed per second. Force: m/s².")]
        public float Rate = 5f;

        [Tooltip("Level only: the world height the water surface is pulled toward.")]
        public float Level;

        void OnEnable() => Active.Add(this);

        void OnDisable() => Active.Remove(this);

        /// <summary>The effector as the simulation sees it. Force pushes along the transform's forward.</summary>
        public WaterEffector ToEffector()
        {
            var p = transform.position;
            var forward = transform.forward;
            return new WaterEffector
            {
                Kind = (int)Kind,
                Position = new Vector2(p.x, p.z),
                Radius = Radius,
                Rate = Rate,
                Level = Level,
                Direction = new Vector2(forward.x, forward.z).normalized,
            };
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Kind switch
            {
                WaterEffectorKind.Source => new Color(0.3f, 0.7f, 1f),
                WaterEffectorKind.Drain => new Color(1f, 0.5f, 0.2f),
                WaterEffectorKind.Level => new Color(0.4f, 1f, 0.6f),
                _ => new Color(1f, 1f, 0.3f),
            };
            Gizmos.DrawWireSphere(transform.position, Radius);
            if (Kind == WaterEffectorKind.Force)
                Gizmos.DrawRay(transform.position, transform.forward * Radius * 1.5f);
        }
    }
}
