using UnityEngine;

namespace PromptWaffle.DynamicWater.Samples
{
    /// <summary>
    /// A buoy that rides the water: each frame it asks <see cref="WaterZone.TrySampleAny"/> for
    /// the surface under it (the simulated water plus the swell, as drawn), floats at that height
    /// and drifts with the current. Stranded on dry ground, it stays where it is.
    /// </summary>
    public sealed class SampleFloater : MonoBehaviour
    {
        [Tooltip("Metres the buoy sits below the surface.")]
        [SerializeField] float _draft = 0.25f;

        [Tooltip("How much of the water's speed the buoy takes on.")]
        [SerializeField, Range(0f, 1f)] float _drift = 0.8f;

        [Tooltip("How quickly it follows the surface up and down, per second.")]
        [SerializeField, Min(0f)] float _bob = 6f;

        Vector3 _velocity;

        void Update()
        {
            var position = transform.position;
            if (!WaterZone.TrySampleAny(position, out var water) || !water.IsWet)
            {
                _velocity = Vector3.zero;
                return;
            }

            var dt = Time.deltaTime;
            var current = new Vector3(water.Velocity.x, 0f, water.Velocity.y) * _drift;
            _velocity = Vector3.Lerp(_velocity, current, 1f - Mathf.Exp(-2f * dt));
            position += _velocity * dt;
            position.y = Mathf.Lerp(position.y, water.Surface - _draft, 1f - Mathf.Exp(-_bob * dt));
            transform.position = position;
        }
    }
}
