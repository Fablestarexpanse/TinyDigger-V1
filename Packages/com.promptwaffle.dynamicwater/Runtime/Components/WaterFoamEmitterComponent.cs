using System.Collections.Generic;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// Makes foam round a hull in the water: lapping at the waterline, a bow wave and a wake as it
    /// moves (<see cref="WaterFoamMap"/>). Put it on the boat; its speed is taken from how the
    /// transform moves, so whatever drives the boat needs to know nothing about it.
    /// </summary>
    [AddComponentMenu("PromptWaffle/Dynamic Water/Water Foam Emitter")]
    public sealed class WaterFoamEmitterComponent : MonoBehaviour
    {
        static readonly List<WaterFoamEmitterComponent> Active = new List<WaterFoamEmitterComponent>();

        /// <summary>Every enabled emitter in loaded scenes.</summary>
        public static IReadOnlyList<WaterFoamEmitterComponent> All => Active;

        [Tooltip("Hull length (along the transform's forward) and width at the waterline, metres.")]
        public Vector2 HullSize = new Vector2(6f, 3f);

        [Tooltip("Hull centre relative to the transform, metres, in its own x and z.")]
        public Vector2 HullOffset;

        [Range(0f, 1f)] public float Ring = 0.45f;
        [Range(0f, 1f)] public float Bow = 1f;
        [Range(0f, 1f)] public float Wake = 0.9f;

        [Tooltip("Seconds over which the measured speed is smoothed.")]
        [Min(0.01f)] public float SpeedSmoothing = 0.25f;

        Vector3 _lastPosition;
        Vector2 _velocity;
        bool _hasLast;

        /// <summary>World x, z metres a second, smoothed.</summary>
        public Vector2 Velocity => _velocity;

        void OnEnable()
        {
            Active.Add(this);
            _hasLast = false;
        }

        void OnDisable() => Active.Remove(this);

        void LateUpdate()
        {
            var position = transform.position;
            var dt = Time.deltaTime;
            if (_hasLast && dt > 0f)
            {
                var measured = new Vector2(position.x - _lastPosition.x, position.z - _lastPosition.z) / dt;
                _velocity = Vector2.Lerp(_velocity, measured, 1f - Mathf.Exp(-dt / SpeedSmoothing));
            }

            _lastPosition = position;
            _hasLast = true;
        }

        /// <summary>The hull as the foam map sees it.</summary>
        public WaterFoamEmitter ToEmitter()
        {
            var forward = transform.forward;
            var flat = new Vector2(forward.x, forward.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector2.up;
            var centre = transform.TransformPoint(new Vector3(HullOffset.x, 0f, HullOffset.y));
            return new WaterFoamEmitter
            {
                Position = new Vector2(centre.x, centre.z),
                Forward = flat,
                HalfSize = new Vector2(Mathf.Max(0.1f, HullSize.x * 0.5f), Mathf.Max(0.1f, HullSize.y * 0.5f)),
                Velocity = _velocity,
                Ring = Ring,
                Bow = Bow,
                Wake = Wake,
            };
        }
    }
}
