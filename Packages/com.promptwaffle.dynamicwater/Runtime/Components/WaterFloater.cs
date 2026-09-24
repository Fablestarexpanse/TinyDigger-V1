using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// Makes a hull ride the water as it is drawn: the simulated surface plus the swell, sampled
    /// through <see cref="WaterZone.TrySampleAny"/>, which evaluates the waves on the CPU exactly as
    /// the surface shader does. It is not a physics body. Each frame it samples the water under a
    /// few float points, puts the waterline on the mean of them, and tilts the hull to the plane
    /// through them, eased so a hull does not snap onto each crest.
    ///
    /// Whatever moves the hull owns its position across the water and its heading: set them before
    /// this runs (it runs late) and it only changes the height and the pitch and roll. Where none of
    /// the points is over wet water — hauled out, or beached high — it leaves the hull alone and
    /// <see cref="Floating"/> is false.
    /// </summary>
    [AddComponentMenu("PromptWaffle/Dynamic Water/Water Floater")]
    [DefaultExecutionOrder(100)]
    public sealed class WaterFloater : MonoBehaviour
    {
        [Tooltip("Where the hull meets the water, in this object's local space, around its middle. Four at the corners is the usual set.")]
        [SerializeField] Vector3[] _floatPoints =
        {
            new Vector3(0.5f, 0f, 1f), new Vector3(-0.5f, 0f, 1f),
            new Vector3(0.5f, 0f, -1f), new Vector3(-0.5f, 0f, -1f),
        };

        [Tooltip("Height of the waterline above this object's origin, in its local units, with the load it is carrying.")]
        [SerializeField] float _waterline = 0.1f;

        [Tooltip("Seconds the hull takes to follow the water; 0 rides every ripple rigidly. Keep it short: "
            + "the float points spread over the hull already smooth out waves shorter than it, and a slow "
            + "response lags the swell. At 0.3 s a 6 s swell left the keel hanging half a metre over the trough.")]
        [SerializeField, Min(0f)] float _response = 0.06f;

        [Tooltip("The most it pitches or rolls, degrees.")]
        [SerializeField, Range(0f, 45f)] float _maxTilt = 12f;

        bool _logging;
        float _nextLog;

        float _heightRate;
        Vector2 _tilt;
        Vector2 _tiltRate;
        bool _placed;

        /// <summary>Whether any float point was over wet water on the last update.</summary>
        public bool Floating { get; private set; }

        /// <summary>The mean water surface under the float points on the last update, world metres.</summary>
        public float Surface { get; private set; }

        /// <summary>
        /// Logs, once a second, the surface under the hull, the still water under it without the
        /// swell, and where the hull sits: for finding out why a hull rides high or low. Off by
        /// default; callable by name (SendMessage) from code that cannot see this assembly.
        /// </summary>
        public void SetLogging(bool on) => _logging = on;

        /// <summary>Sets the float points and the waterline from code (a prefab builder, say).</summary>
        public void Configure(Vector3[] floatPoints, float waterline)
        {
            _floatPoints = floatPoints;
            _waterline = waterline;
        }

        /// <summary>Sets how far the waterline sits above the origin, as the load changes the draft.</summary>
        public float Waterline
        {
            get => _waterline;
            set => _waterline = value;
        }

        void LateUpdate()
        {
            if (_floatPoints == null || _floatPoints.Length == 0)
                return;

            // Sampled at the points as they sit with the hull level: the heading is the owner's,
            // and sampling through the tilt this frame would feed the tilt back into itself.
            var scale = transform.lossyScale;
            var yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            var origin = transform.position;
            float sum = 0f, sumX = 0f, sumZ = 0f, sumXX = 0f, sumZZ = 0f, sumXH = 0f, sumZH = 0f;
            var wet = 0;
            foreach (var point in _floatPoints)
            {
                var local = new Vector3(point.x * scale.x, 0f, point.z * scale.z);
                var world = origin + yaw * local;
                if (!WaterZone.TrySampleAny(world, out var sample) || !sample.IsWet)
                    continue;
                wet++;
                sum += sample.Surface;
                sumX += local.x;
                sumZ += local.z;
                sumXX += local.x * local.x;
                sumZZ += local.z * local.z;
                sumXH += local.x * sample.Surface;
                sumZH += local.z * sample.Surface;
            }

            Floating = wet > 0;
            if (!Floating)
                return;

            var mean = sum / wet;
            Surface = mean;

            // The plane through the points: its slope along the hull is the pitch, across it the
            // roll. Least squares, so three points or five work as well as four; fewer than three
            // wet (half aground) keeps the hull level rather than guessing.
            var pitch = 0f;
            var roll = 0f;
            if (wet >= 3)
            {
                var meanX = sumX / wet;
                var meanZ = sumZ / wet;
                var varX = sumXX / wet - meanX * meanX;
                var varZ = sumZZ / wet - meanZ * meanZ;
                var slopeX = varX > 1e-5f ? (sumXH / wet - meanX * mean) / varX : 0f;
                var slopeZ = varZ > 1e-5f ? (sumZH / wet - meanZ * mean) / varZ : 0f;
                pitch = Mathf.Clamp(-Mathf.Atan(slopeZ) * Mathf.Rad2Deg, -_maxTilt, _maxTilt);
                roll = Mathf.Clamp(Mathf.Atan(slopeX) * Mathf.Rad2Deg, -_maxTilt, _maxTilt);
            }

            var height = mean - _waterline * scale.y;
            var position = transform.position;
            if (!_placed || _response <= 0f)
            {
                position.y = height;
                _tilt = new Vector2(pitch, roll);
                _placed = true;
            }
            else
            {
                position.y = Mathf.SmoothDamp(position.y, height, ref _heightRate, _response);
                _tilt.x = Mathf.SmoothDamp(_tilt.x, pitch, ref _tiltRate.x, _response);
                _tilt.y = Mathf.SmoothDamp(_tilt.y, roll, ref _tiltRate.y, _response);
            }

            transform.SetPositionAndRotation(position, yaw * Quaternion.Euler(_tilt.x, 0f, _tilt.y));

            if (_logging && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 1f;
                var still = float.NaN;
                foreach (var zone in WaterZone.All)
                    if (zone.Simulation != null && zone.Simulation.Query.TrySample(origin, out var calm))
                    {
                        still = calm.Surface;
                        break;
                    }

                Debug.Log($"WaterFloater {name}: t {Time.timeSinceLevelLoad:0.00}, surface {mean:0.000} over {wet} points, "
                          + $"still water {still:0.000}, swell {mean - still:0.000}, hull at {position.y:0.000} "
                          + $"(wants {height:0.000}), pitch {_tilt.x:0.0}, roll {_tilt.y:0.0}");
            }
        }
    }
}
