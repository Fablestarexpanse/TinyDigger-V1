using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The player's only way to change the terrain: marking what should be done, for the unit to
    /// do. Across <see cref="TerrainView.BrushRadius"/>:
    /// - left click or drag: dig to height H;
    /// - right click or drag: fill to height H;
    /// - Shift + right click or drag: mark a Dump Zone, where the unit tips spoil;
    /// - middle click (without dragging; a middle drag rotates the camera): clear designations,
    ///   the unit's Auto ramp steps and Dump Zones.
    ///
    /// H follows the hovered cell's height until Q or E moves it by one height step, which locks
    /// it; R unlocks it again. A drag stroke keeps the H it started with, so dragging across a
    /// hill designates one level, not the hill's own shape. The brush footprint is previewed as a
    /// flat sheet at H.
    /// </summary>
    public sealed class DesignationTool : MonoBehaviour
    {
        /// <summary>A middle press that travels less than this many pixels is a click, not a camera drag.</summary>
        public const float ClickTravelPixels = 6f;

        /// <summary>Metres above H that spoil may be heaped on a Dump Zone marked now. Q/E move H, so they move the cap.</summary>
        [Min(0f)] public float zoneCapAbove = 3f;

        [SerializeField] TerrainView _terrain;
        [SerializeField] Camera _camera;
        [SerializeField] DesignationsView _designations;
        [SerializeField] CrewView _crew;
        [SerializeField] Material _overlayMaterial;
        [SerializeField] Color32 _previewColor = new Color32(255, 240, 160, 90);

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();

        Mesh _preview;
        DesignationKind _stroke;
        bool _strokeIsZone;
        float _strokeHeight;
        readonly HashSet<int> _strokeCells = new HashSet<int>();
        Vector2 _middlePressedAt;
        GUIStyle _labelStyle;
        int _previewX = int.MinValue;
        int _previewZ;
        float _previewHeight;
        int _previewRadius = -1;

        public bool HasHover { get; private set; }

        public int HoverX { get; private set; }

        public int HoverZ { get; private set; }

        /// <summary>The height designations are set to.</summary>
        public float TargetHeight { get; private set; }

        /// <summary>False while H follows the hovered cell; true once Q/E has set it.</summary>
        public bool HeightLocked { get; private set; }

        /// <summary>What the last stroke or clear did, for the readout.</summary>
        public string LastAction { get; private set; } = "";

        void Start()
        {
            _preview = new Mesh { name = "Designation Preview" };
            _preview.MarkDynamic();
            var preview = new GameObject("Designation Preview") { hideFlags = HideFlags.DontSave };
            preview.transform.SetParent(_terrain.transform, false);
            preview.AddComponent<MeshFilter>().sharedMesh = _preview;
            var meshRenderer = preview.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _overlayMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        void Update()
        {
            var grid = _terrain.Grid;
            var step = grid.HeightStep > 0f ? grid.HeightStep : 1f;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.qKey.wasPressedThisFrame)
                    NudgeHeight(-step);
                if (keyboard.eKey.wasPressedThisFrame)
                    NudgeHeight(step);
                if (keyboard.rKey.wasPressedThisFrame)
                    HeightLocked = false;
            }

            var mouse = Mouse.current;
            HasHover = false;
            if (mouse != null)
                Pick(grid, mouse.position.ReadValue());

            if (!HeightLocked && _stroke == DesignationKind.None && HasHover)
                TargetHeight = grid.GetSurfaceHeight(HoverX, HoverZ);

            if (mouse != null)
                HandleButtons(mouse);

            UpdatePreview(grid);
        }

        void NudgeHeight(float delta)
        {
            if (!HeightLocked && HasHover)
                TargetHeight = _terrain.Grid.GetSurfaceHeight(HoverX, HoverZ);
            TargetHeight += delta;
            HeightLocked = true;
        }

        void Pick(TerrainGrid grid, Vector2 screen)
        {
            var ray = _camera.ScreenPointToRay(screen);
            var terrainTransform = _terrain.transform;
            HasHover = TerrainPicker.TryPick(
                grid,
                terrainTransform.InverseTransformPoint(ray.origin),
                terrainTransform.InverseTransformDirection(ray.direction),
                out var x,
                out var z,
                out _);
            if (!HasHover)
                return;
            HoverX = x;
            HoverZ = z;
        }

        void HandleButtons(Mouse mouse)
        {
            // Strokes: the kind and H are fixed when the button goes down.
            // A left click on a unit selects it instead of designating.
            if (mouse.leftButton.wasPressedThisFrame && _crew != null
                && _crew.TrySelectAt(_camera.ScreenPointToRay(mouse.position.ReadValue())))
            {
                LastAction = "Selected a unit";
                return;
            }

            if (mouse.leftButton.wasPressedThisFrame)
                BeginStroke(DesignationKind.Dig, false);
            else if (mouse.rightButton.wasPressedThisFrame)
                BeginStroke(DesignationKind.Fill, Keyboard.current != null && Keyboard.current.shiftKey.isPressed);

            var held = _stroke == DesignationKind.Dig ? mouse.leftButton.isPressed
                : _stroke == DesignationKind.Fill && mouse.rightButton.isPressed;
            if (_stroke != DesignationKind.None)
            {
                if (held && HasHover)
                {
                    var width = _terrain.Grid.Width;
                    ApplyBrush(HoverX, HoverZ, cell =>
                    {
                        if (_strokeIsZone)
                        {
                            _designations.Map.SetDumpZone(cell.x, cell.y, true, _strokeHeight + zoneCapAbove);
                            _strokeCells.Add(cell.y * width + cell.x);
                            return true;
                        }

                        if (!_designations.Map.Designate(cell.x, cell.y, _stroke, _strokeHeight))
                            return false;
                        _strokeCells.Add(cell.y * width + cell.x);
                        return true;
                    });
                }

                if (!held)
                    EndStroke();
            }

            if (mouse.middleButton.wasPressedThisFrame)
                _middlePressedAt = mouse.position.ReadValue();
            if (mouse.middleButton.wasReleasedThisFrame && HasHover
                && (mouse.position.ReadValue() - _middlePressedAt).magnitude < ClickTravelPixels)
            {
                var cleared = 0;
                ApplyBrush(HoverX, HoverZ, cell =>
                {
                    if (!_designations.Map.Cancel(cell.x, cell.y))
                        return false;
                    cleared++;
                    return true;
                });
                LastAction = $"Cleared {cleared} designation{(cleared == 1 ? "" : "s")}";
            }
        }

        void BeginStroke(DesignationKind kind, bool zone)
        {
            _stroke = kind;
            _strokeIsZone = zone;
            _strokeHeight = TargetHeight;
            _strokeCells.Clear();
        }

        void EndStroke()
        {
            var verb = _stroke == DesignationKind.Dig ? "dig" : "fill";
            var count = _strokeCells.Count;
            if (_strokeIsZone)
            {
                LastAction = $"Marked {count} cell{(count == 1 ? "" : "s")} as Dump Zone, capped at {_strokeHeight + zoneCapAbove:0.#} m";
                _stroke = DesignationKind.None;
                _strokeIsZone = false;
                return;
            }

            LastAction = count > 0

                ? $"Designated {count} cell{(count == 1 ? "" : "s")}: {verb} to {_strokeHeight:0.#} m"
                : $"Nothing to {verb}: those cells are already at {_strokeHeight:0.#} m";
            _stroke = DesignationKind.None;
        }

        /// <summary>Runs <paramref name="apply"/> on every in-bounds cell of the brush; returns how many it returned true for.</summary>
        int ApplyBrush(int centreX, int centreZ, System.Func<Vector2Int, bool> apply)
        {
            var grid = _terrain.Grid;
            var radius = _terrain.BrushRadius;
            var applied = 0;
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dz * dz > radius * radius || !grid.InBounds(centreX + dx, centreZ + dz))
                        continue;
                    if (apply(new Vector2Int(centreX + dx, centreZ + dz)))
                        applied++;
                }
            }

            return applied;
        }

        void UpdatePreview(TerrainGrid grid)
        {
            var height = _stroke != DesignationKind.None ? _strokeHeight : TargetHeight;
            var x = HasHover ? HoverX : int.MinValue;
            if (x == _previewX && HoverZ == _previewZ && height == _previewHeight && _terrain.BrushRadius == _previewRadius)
                return;
            _previewX = x;
            _previewZ = HoverZ;
            _previewHeight = height;
            _previewRadius = _terrain.BrushRadius;

            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            if (HasHover)
            {
                // A flat sheet at H over the brush: where it sits below the ground the cells
                // would be dug, above it they would be filled.
                ApplyBrush(HoverX, HoverZ, cell =>
                {
                    DesignationsView.AddTile(cell.x, cell.y, height, height, height, height, _previewColor, _vertices, _colors, _triangles);
                    return true;
                });
            }

            _preview.Clear();
            _preview.SetVertices(_vertices);
            _preview.SetColors(_colors);
            _preview.SetTriangles(_triangles, 0, true);
        }

        void OnGUI()
        {
            var mouse = Mouse.current;
            if (!HasHover || mouse == null)
                return;
            _labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = Color.white } };
            var position = mouse.position.ReadValue();
            var height = _stroke != DesignationKind.None ? _strokeHeight : TargetHeight;
            var zone = _strokeIsZone || (Keyboard.current != null && Keyboard.current.shiftKey.isPressed && _stroke == DesignationKind.None);
            var text = $"H {height:0.#} m" + (HeightLocked ? " (locked)" : "")
                + (zone ? $"   dump zone cap {height + zoneCapAbove:0.#} m" : "");

            GUI.Label(new Rect(position.x + 18f, Screen.height - position.y + 4f, 200f, 24f), text, _labelStyle);
        }
    }
}
