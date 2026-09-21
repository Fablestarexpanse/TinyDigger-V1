using UnityEngine;
#if PW_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PromptWaffle.DynamicWater.Samples
{
    /// <summary>
    /// The Basic Zone sample's controls and readout:
    /// - hold the left mouse button to pour water where you point (a Source effector that follows
    ///   the cursor);
    /// - click the right button to dig a crater, and the water finds its way in;
    /// - R refills the zone from its start level.
    /// The readout shows the water in the zone and the water under the cursor, both read from the
    /// zone's query snapshot, so nothing waits on the GPU.
    /// </summary>
    public sealed class BasicZoneDemo : MonoBehaviour
    {
        [SerializeField] WaterZone _zone;
        [SerializeField] ProceduralBasinGround _ground;
        [SerializeField] Camera _camera;

        [Tooltip("Cubic metres a second poured while the left button is held.")]
        [SerializeField, Min(0f)] float _pourRate = 20f;

        [SerializeField, Min(0.1f)] float _pourRadius = 1.5f;
        [SerializeField, Min(0.1f)] float _digRadius = 3f;
        [SerializeField, Min(0f)] float _digDepth = 1.5f;

        WaterEffectorComponent _hose;
        string _underCursor = "";
        float _volume;
        float _nextVolume;

        void Awake()
        {
            if (_camera == null)
                _camera = Camera.main;
            var hose = new GameObject("Hose");
            hose.transform.SetParent(transform, false);
            _hose = hose.AddComponent<WaterEffectorComponent>();
            _hose.Kind = WaterEffectorKind.Source;
            _hose.Rate = _pourRate;
            _hose.Radius = _pourRadius;
            hose.SetActive(false);
        }

        void Update()
        {
            var hit = PointerHit(out var point);
            _hose.gameObject.SetActive(hit && PourHeld());
            if (hit)
                _hose.transform.position = point;
            if (hit && DigPressed())
                _ground.Dig(point, _digRadius, _digDepth);
            if (ResetPressed())
                _zone.Rebuild();

            _underCursor = hit && _zone.TrySample(point, out var water) && water.IsWet
                ? $"Under the cursor: {water.Depth:0.00} m deep, flowing {water.Velocity.magnitude:0.00} m/s"
                : "Under the cursor: dry";
            if (Time.unscaledTime >= _nextVolume)
            {
                _nextVolume = Time.unscaledTime + 0.5f;
                _volume = Volume();
            }
        }

        bool PointerHit(out Vector3 point)
        {
            point = default;
            if (_camera == null || !PointerPosition(out var screen))
                return false;
            if (!Physics.Raycast(_camera.ScreenPointToRay(screen), out var hit, 1000f))
                return false;
            point = hit.point;
            return true;
        }

        /// <summary>Cubic metres of water in the zone, summed from the latest snapshot.</summary>
        float Volume()
        {
            var simulation = _zone != null ? _zone.Simulation : null;
            if (simulation == null || !simulation.Query.HasData)
                return 0f;
            var snapshot = simulation.Query.Snapshot;
            var sum = 0.0;
            for (var i = 0; i < snapshot.Length; i++)
                sum += snapshot[i].y;
            return (float)(sum * simulation.Desc.CellArea);
        }

        void OnGUI()
        {
            GUI.Box(new Rect(12, 12, 360, 96),
                "PromptWaffle Dynamic Water: Basic Zone\n" +
                "Hold left mouse: pour water    Right click: dig\n" +
                "R: refill from the start level\n" +
                $"Water in the zone: {_volume:0} m³\n" +
                _underCursor);
        }

#if PW_INPUT_SYSTEM
        static bool PointerPosition(out Vector2 screen)
        {
            screen = Mouse.current != null ? Mouse.current.position.ReadValue() : default;
            return Mouse.current != null;
        }

        static bool PourHeld() => Mouse.current != null && Mouse.current.leftButton.isPressed;
        static bool DigPressed() => Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
        static bool ResetPressed() => Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
        static bool PointerPosition(out Vector2 screen)
        {
            screen = Input.mousePosition;
            return true;
        }

        static bool PourHeld() => Input.GetMouseButton(0);
        static bool DigPressed() => Input.GetMouseButtonDown(1);
        static bool ResetPressed() => Input.GetKeyDown(KeyCode.R);
#endif
    }
}
