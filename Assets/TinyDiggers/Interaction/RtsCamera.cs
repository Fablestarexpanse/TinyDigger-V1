using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The RTS camera: it orbits a pivot on the ground, and everything the player does moves that
    /// pivot or the way the camera hangs off it. The arithmetic lives in <see cref="RtsCameraRig"/>;
    /// this reads the input and eases the camera toward what the rig asks for.
    ///
    /// - WASD and the arrow keys pan along the ground in screen-relative directions, faster the
    ///   further out the camera is.
    /// - Q and E turn, as does dragging with the middle mouse button. The turn is about the
    ///   pivot, so from far out it reads as turning the table the map sits on.
    /// - The wheel zooms toward whatever is under the cursor, in even steps. Shift and the wheel
    ///   changes the lens instead: narrow and far back is what reads as a model on a table.
    /// - The tilt is the player's. R and F tilt while held, and dragging the middle button up and
    ///   down tilts as well as turning, anywhere from 10° to 89°. The old zoom-driven pitch curve
    ///   is still there behind <see cref="RtsCameraRig.PitchFollowsZoom"/>, off by default.
    /// - F5 saves the way the camera is looking to the preset asset, F6 loads it back, and Home
    ///   re-centres on the map using it — so the preset is also what the game starts with.
    /// - Edge panning is off unless <see cref="EdgePan"/> is set.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class RtsCamera : MonoBehaviour
    {
        /// <summary>A middle press that travels less than this many pixels is a click, not a turn.</summary>
        public const float ClickTravelPixels = 6f;

        [SerializeField] TerrainView _terrain;

        [Tooltip("The camera the game starts with, and what F5 saves to, F6 loads and Home uses.")]
        [SerializeField] CameraPreset _preset;

        [Header("Pan")]
        [Tooltip("Cells a second at the closest zoom; scaled up with distance.")]
        public float PanSpeed = 14f;

        [Tooltip("How many times faster panning is at the furthest zoom.")]
        public float PanSpeedAtFar = 7f;

        [Tooltip("Pan when the mouse is at the edge of the screen. Off by default.")]
        public bool EdgePan;

        [Tooltip("Pixels from the edge that count as the edge.")]
        public float EdgePanMargin = 12f;

        [Header("Zoom")]
        [Tooltip("Metres from the pivot when fully zoomed in: about three cells across the screen.")]
        public float MinDistance = 3f;

        [Tooltip("Metres from the pivot when fully zoomed out. Clamped so the whole disc fits with margin.")]
        public float MaxDistance = 700f;

        [Tooltip("Fraction of the distance one scroll notch covers.")]
        public float ZoomStep = 0.15f;

        [Tooltip("Degrees of field of view one scroll notch covers, held with Shift.")]
        public float FovStep = 2f;

        [Header("Turn and pitch")]
        public float TurnSpeed = 90f;

        public float DragTurnSpeed = 0.25f;

        [Tooltip("Degrees a second R and F tilt while held.")]
        public float TiltSpeed = 45f;

        [Tooltip("Degrees of tilt per pixel of vertical middle-drag. Dragging up looks further down.")]
        public float DragTiltSpeed = 0.2f;

        [Header("Pitch follows zoom (off by default)")]
        [Range(RtsCameraRig.MinPitch, RtsCameraRig.MaxPitch)] public float ClosePitch = 35f;

        [Range(RtsCameraRig.MinPitch, RtsCameraRig.MaxPitch)] public float FarPitch = 60f;

        [Header("Easing")]
        public float PanSmoothing = 0.12f;

        public float ZoomSmoothing = 0.15f;

        public float TurnSmoothing = 0.1f;

        readonly RtsCameraRig _rig = new RtsCameraRig();

        Camera _camera;
        Vector3 _pivot;
        Vector3 _pivotVelocity;
        float _distance;
        float _distanceVelocity;
        float _yaw;
        float _yawVelocity;
        float _pitch;
        float _pitchVelocity;
        float _fov;
        float _fovVelocity;
        Vector2 _middlePressedAt;
        bool _turning;

        /// <summary>What the camera is easing toward.</summary>
        public RtsCameraRig Rig => _rig;

        /// <summary>The ground point the camera orbits, in cells.</summary>
        public Vector3 Pivot => _pivot;

        public float Distance => _distance;

        /// <summary>Degrees below the horizon, as the camera is drawn this frame.</summary>
        public float Pitch => _pitch;

        /// <summary>Degrees clockwise from +z, as the camera is drawn this frame.</summary>
        public float Yaw => _yaw;

        /// <summary>Vertical field of view, as the camera is drawn this frame.</summary>
        public float Fov => _fov;

        /// <summary>The preset F5 writes to and F6 reads back. May be unset.</summary>
        public CameraPreset Preset => _preset;

        /// <summary>Whether a middle-button press has turned into a turn rather than a click.</summary>
        public bool IsTurning => _turning;

        void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        void Start()
        {
            ApplySettings();
            GoHome();
            _pivot = _rig.Pivot;
            _distance = _rig.Distance;
            _yaw = _rig.Yaw;
            _pitch = _rig.Pitch;
            _fov = _rig.Fov;
            Place();
        }

        /// <summary>Puts the camera back over the middle of the map, looking the way the preset says.</summary>
        public void GoHome()
        {
            ApplySettings();
            _rig.GoHome(_preset);
            _rig.Pivot = new Vector3(_rig.Pivot.x, GroundHeightAt(_rig.Pivot), _rig.Pivot.z);
        }

        /// <summary>
        /// Writes how the camera is looking now to the preset asset. Only the editor can write an
        /// asset, so in a build this reports rather than pretending to have saved.
        /// </summary>
        public void SavePreset()
        {
            if (_preset == null)
            {
                Debug.LogWarning("Camera: no preset asset is assigned, so F5 has nothing to save to.");
                return;
            }

            _preset.CaptureFrom(_rig);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(_preset);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(_preset);
            Debug.Log($"Camera saved to {UnityEditor.AssetDatabase.GetAssetPath(_preset)}: " +
                $"pitch {_preset.Pitch:0.#}°, FOV {_preset.Fov:0.#}°, {_preset.Distance:0} m" +
                (_preset.PitchFollowsZoom ? ", pitch follows zoom." : "."));
#else
            Debug.Log("Camera preset held for this session; saving it to the asset needs the editor.");
#endif
        }

        /// <summary>Puts the camera back the way the preset says, without moving the pivot.</summary>
        public void LoadPreset()
        {
            if (_preset == null)
            {
                Debug.LogWarning("Camera: no preset asset is assigned, so F6 has nothing to load.");
                return;
            }

            ApplySettings();
            _preset.ApplyTo(_rig);
        }

        void ApplySettings()
        {
            var grid = _terrain != null ? _terrain.Grid : null;
            _rig.PanSpeed = PanSpeed;
            _rig.PanSpeedAtFar = PanSpeedAtFar;
            _rig.MinDistance = MinDistance;
            _rig.ZoomStep = ZoomStep;
            _rig.ClosePitch = ClosePitch;
            _rig.FarPitch = FarPitch;
            if (grid == null)
                return;
            _rig.DiscCentre = _terrain.DiscCentre;
            _rig.DiscRadius = _terrain.DiscRadius;
            // Far enough out that the whole disc fits on screen with a margin.
            _rig.MaxDistance = Mathf.Min(MaxDistance, _terrain.DiscRadius * 2.6f + 40f);
        }

        void Update()
        {
            ApplySettings();
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var deltaTime = Time.unscaledDeltaTime;

            ReadTurn(keyboard, mouse, deltaTime);
            ReadZoom(keyboard, mouse);
            ReadPan(keyboard, mouse, deltaTime);
            if (keyboard != null)
            {
                if (keyboard.homeKey.wasPressedThisFrame)
                    GoHome();
                if (keyboard.f5Key.wasPressedThisFrame)
                    SavePreset();
                if (keyboard.f6Key.wasPressedThisFrame)
                    LoadPreset();
            }

            _rig.Pivot = new Vector3(_rig.Pivot.x, GroundHeightAt(_rig.Pivot), _rig.Pivot.z);
            _pivot = Vector3.SmoothDamp(_pivot, _rig.Pivot, ref _pivotVelocity, PanSmoothing, Mathf.Infinity, deltaTime);
            _distance = Mathf.SmoothDamp(_distance, _rig.Distance, ref _distanceVelocity, ZoomSmoothing, Mathf.Infinity, deltaTime);
            _yaw = Mathf.SmoothDampAngle(_yaw, _rig.Yaw, ref _yawVelocity, TurnSmoothing, Mathf.Infinity, deltaTime);
            _pitch = Mathf.SmoothDamp(_pitch, _rig.Pitch, ref _pitchVelocity, TurnSmoothing, Mathf.Infinity, deltaTime);
            _fov = Mathf.SmoothDamp(_fov, _rig.Fov, ref _fovVelocity, ZoomSmoothing, Mathf.Infinity, deltaTime);
            Place();
        }

        void ReadTurn(Keyboard keyboard, Mouse mouse, float deltaTime)
        {
            if (keyboard != null)
            {
                if (keyboard.qKey.isPressed)
                    _rig.Turn(-TurnSpeed * deltaTime);
                if (keyboard.eKey.isPressed)
                    _rig.Turn(TurnSpeed * deltaTime);
                // Held, not tapped: the tilt is continuous now.
                if (keyboard.rKey.isPressed)
                    _rig.Tilt(TiltSpeed * deltaTime);
                if (keyboard.fKey.isPressed)
                    _rig.Tilt(-TiltSpeed * deltaTime);
            }

            if (mouse == null)
                return;
            if (mouse.middleButton.wasPressedThisFrame)
            {
                _middlePressedAt = mouse.position.ReadValue();
                _turning = false;
            }

            if (mouse.middleButton.isPressed)
            {
                if ((mouse.position.ReadValue() - _middlePressedAt).magnitude >= ClickTravelPixels)
                    _turning = true;
                if (_turning)
                {
                    var drag = mouse.delta.ReadValue();
                    _rig.Turn(drag.x * DragTurnSpeed);
                    // Dragging up pulls the camera over the map; dragging down lowers it toward
                    // the horizon.
                    _rig.Tilt(drag.y * DragTiltSpeed);
                }
            }

            if (mouse.middleButton.wasReleasedThisFrame)
                _turning = false;
        }

        void ReadZoom(Keyboard keyboard, Mouse mouse)
        {
            if (mouse == null)
                return;
            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f)
                return;

            var notches = Mathf.Clamp(scroll / 120f, -3f, 3f);
            if (Mathf.Abs(notches) < 0.01f)
                notches = Mathf.Sign(scroll);

            // Shift and the wheel is the lens rather than the distance: a narrow lens from further
            // back flattens the map out, which is where the tilt-shift look comes from.
            if (keyboard != null && keyboard.shiftKey.isPressed)
            {
                _rig.ChangeFov(-notches * FovStep);
                return;
            }

            _rig.Zoom(notches, TryGroundPointUnder(mouse.position.ReadValue(), out var under) ? under : (Vector3?)null);
        }

        void ReadPan(Keyboard keyboard, Mouse mouse, float deltaTime)
        {
            var input = Vector2.zero;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                    input.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                    input.y -= 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    input.x -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                    input.x += 1f;
            }

            if (EdgePan && mouse != null)
            {
                var position = mouse.position.ReadValue();
                if (position.x <= EdgePanMargin)
                    input.x -= 1f;
                if (position.x >= Screen.width - EdgePanMargin)
                    input.x += 1f;
                if (position.y <= EdgePanMargin)
                    input.y -= 1f;
                if (position.y >= Screen.height - EdgePanMargin)
                    input.y += 1f;
            }

            _rig.Pan(input, deltaTime);
        }

        void Place()
        {
            if (_camera != null && !_camera.orthographic)
                _camera.fieldOfView = _fov;
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var position = _pivot - rotation * Vector3.forward * _distance;
            var terrain = _terrain != null ? _terrain.transform : null;
            if (terrain != null)
                transform.SetPositionAndRotation(terrain.TransformPoint(position), terrain.rotation * rotation);
            else
                transform.SetPositionAndRotation(position, rotation);
        }

        float GroundHeightAt(Vector3 point)
        {
            var grid = _terrain != null ? _terrain.Grid : null;
            return grid == null ? point.y : TerrainSurface.SampleHeight(grid, point.x, point.z);
        }

        /// <summary>The ground point under a screen position, in cells.</summary>
        bool TryGroundPointUnder(Vector2 screen, out Vector3 point)
        {
            point = default;
            var grid = _terrain != null ? _terrain.Grid : null;
            if (grid == null || _camera == null)
                return false;

            var ray = _camera.ScreenPointToRay(screen);
            var terrainTransform = _terrain.transform;
            var origin = terrainTransform.InverseTransformPoint(ray.origin);
            var direction = terrainTransform.InverseTransformDirection(ray.direction);
            if (TerrainPicker.TryPick(grid, origin, direction, out var x, out var z, out _) && grid.IsGround(x, z))
            {
                point = new Vector3(x + 0.5f, grid.GetSurfaceHeight(x, z), z + 0.5f);
                return true;
            }

            // Nothing under the cursor — the void, or past the rim — so aim at the plane the
            // pivot is on, and the zoom still goes where the player is looking.
            if (Mathf.Abs(direction.y) < 1e-4f)
                return false;
            var t = (_pivot.y - origin.y) / direction.y;
            if (t <= 0f)
                return false;
            point = origin + direction * t;
            return true;
        }
    }
}
