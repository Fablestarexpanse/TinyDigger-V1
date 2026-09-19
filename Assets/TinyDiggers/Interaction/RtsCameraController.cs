using TinyDiggers.Presentation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// WASD or arrows to pan, hold the middle mouse button and drag sideways to rotate around the
    /// pivot, scroll to zoom. (Q/E belong to the designation tool's height.) A middle press that
    /// has not yet moved <see cref="DesignationTool.ClickTravelPixels"/> does not rotate, so a
    /// middle click stays a click. Reads input and hands it to an <see cref="RtsCameraRig"/>,
    /// which does the maths.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class RtsCameraController : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;

        [Tooltip("A narrow field of view keeps the look close to orthographic while keeping depth cues.")]
        [SerializeField, Range(10f, 60f)] float _fieldOfView = 30f;

        [SerializeField, Range(20f, 89f)] float _pitch = 55f;
        [SerializeField] float _minDistance = 15f;
        [SerializeField] float _maxDistance = 700f;
        [SerializeField] float _startDistance = 350f;
        [SerializeField] float _panSpeed = 0.8f;
        [SerializeField] float _rotateSpeed = 90f;

        [Tooltip("Degrees of rotation per pixel of middle-button drag.")]
        [SerializeField] float _dragDegreesPerPixel = 0.3f;

        [SerializeField, Range(0.01f, 0.5f)] float _zoomStep = 0.12f;

        readonly RtsCameraRig _rig = new RtsCameraRig();
        Vector2 _dragStart;
        Vector2 _dragLast;
        bool _dragging;

        void Start()
        {
            var grid = _terrain.Grid;
            _rig.Pitch = _pitch;
            _rig.MinDistance = _minDistance;
            _rig.MaxDistance = _maxDistance;
            _rig.Distance = Mathf.Clamp(_startDistance, _minDistance, _maxDistance);
            _rig.PanSpeed = _panSpeed;
            _rig.RotateSpeed = _rotateSpeed;
            _rig.ZoomStep = _zoomStep;
            _rig.BoundsMin = Vector2.zero;
            _rig.BoundsMax = new Vector2(grid.Width, grid.Height);
            _rig.Pivot = new Vector3(grid.Width * 0.5f, 0f, grid.Height * 0.5f);
            _rig.Pivot.y = GroundHeightUnderPivot();

            GetComponent<Camera>().fieldOfView = _fieldOfView;
            Apply();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            var deltaTime = Time.unscaledDeltaTime;
            if (keyboard != null)
            {
                var pan = Vector2.zero;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) pan.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) pan.y -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) pan.x += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) pan.x -= 1f;
                _rig.Pan(pan, deltaTime);

            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                var pointer = mouse.position.ReadValue();
                if (mouse.middleButton.wasPressedThisFrame)
                {
                    _dragStart = _dragLast = pointer;
                    _dragging = false;
                }
                else if (mouse.middleButton.isPressed)
                {
                    if (!_dragging && (pointer - _dragStart).magnitude >= DesignationTool.ClickTravelPixels)
                        _dragging = true;
                    if (_dragging)
                        _rig.RotateBy((pointer.x - _dragLast.x) * _dragDegreesPerPixel);
                    _dragLast = pointer;
                }


                // Scroll deltas differ by platform and Input System version (±1 or ±120 a notch),
                // so take one zoom step per frame of scrolling rather than trusting the size.
                var scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                    _rig.Zoom(Mathf.Sign(scroll));
            }

            _rig.FollowGround(GroundHeightUnderPivot(), deltaTime);
            Apply();
        }

        float GroundHeightUnderPivot()
        {
            var grid = _terrain.Grid;
            var x = Mathf.Clamp(Mathf.FloorToInt(_rig.Pivot.x), 0, grid.Width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(_rig.Pivot.z), 0, grid.Height - 1);
            return grid.GetSurfaceHeight(x, z);
        }

        void Apply()
        {
            // The rig works in terrain space; the terrain may not sit at the world origin.
            var terrainTransform = _terrain.transform;
            transform.SetPositionAndRotation(
                terrainTransform.TransformPoint(_rig.Position),
                terrainTransform.rotation * _rig.Rotation);
        }
    }
}
