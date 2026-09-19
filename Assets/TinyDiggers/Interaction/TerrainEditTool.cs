using System.Text;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Finds the cell under the mouse each frame. Left click digs into the crew's load across
    /// <see cref="TerrainView.BrushRadius"/>; right click tips the load onto the clicked cell.
    /// Picking lives in <see cref="TerrainPicker"/>, digging and tipping in <see cref="Crew"/>.
    /// </summary>
    public sealed class TerrainEditTool : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] Camera _camera;
        [SerializeField] CrewView _crew;

        [Tooltip("In-place volume dug from each cell in the brush per click. Cells are 1x1 m, so this is metres.")]
        [SerializeField, Min(0.01f)] float _volumePerCell = 1f;

        readonly StringBuilder _summary = new StringBuilder(128);

        public bool HasHover { get; private set; }

        public int HoverX { get; private set; }

        public int HoverZ { get; private set; }

        public float VolumePerCell => _volumePerCell;

        /// <summary>What the last click did, for the readout. Rebuilt only on clicks.</summary>
        public string LastAction { get; private set; } = "";

        void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                HasHover = false;
                return;
            }

            var grid = _terrain.Grid;
            var ray = _camera.ScreenPointToRay(mouse.position.ReadValue());
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

            if (mouse.leftButton.wasPressedThisFrame)
                Dig(grid, x, z);
            else if (mouse.rightButton.wasPressedThisFrame)
                Tip(grid, x, z);
        }

        void Dig(TerrainGrid grid, int x, int z)
        {
            var crew = _crew.Crew;
            var report = crew.Dig(grid, x, z, _terrain.BrushRadius, _volumePerCell);

            _summary.Clear();
            if (report.WasFull)
            {
                _summary.Append("Full: ").Append(crew.Inventory.Remaining.ToString("0.0"))
                    .Append(" m³ free, next dig needs ").Append(report.SmallestMisfit.ToString("0.0")).Append(" m³");
            }
            else if (report.CellsDug == 0)
            {
                _summary.Append("Dug nothing: bedrock");
            }
            else
            {
                // In-place volume is the hole; loose is what it swelled to in the load.
                _summary.Append("Dug ").Append(report.InPlace.ToString("0.0"))
                    .Append(" m³ solid → ").Append(report.Loose.ToString("0.0")).Append(" m³ loose (");
                AppendMaterials(grid, report.InPlaceBySource);
                _summary.Append(')');
                if (report.CellsThatDidNotFit > 0)
                    _summary.Append("; ").Append(report.CellsThatDidNotFit).Append(" cells did not fit");
            }

            LastAction = _summary.ToString();
        }

        void Tip(TerrainGrid grid, int x, int z)
        {
            var report = _crew.Crew.Tip(grid, x, z);

            _summary.Clear();
            if (report.StackFull)
            {
                _summary.Append("Tipped nothing: that cell's layer stack is full");
            }
            else if (report.Tipped <= 0f && report.HeldBack <= 0f)
            {
                _summary.Append("Nothing to tip");
            }
            else
            {
                _summary.Append("Tipped ").Append(report.Tipped.ToString("0.0")).Append(" m³");
                if (report.Tipped > 0f)
                {
                    _summary.Append(" (");
                    AppendMaterials(grid, report.TippedByMaterial);
                    _summary.Append(')');
                }

                if (report.HeldBack > 0f)
                {
                    _summary.Append("; ").Append(report.HeldBack.ToString("0.00"))
                        .Append(" m³ kept (under one ").Append(grid.HeightStep.ToString("0.#")).Append(" m step)");
                }
            }

            LastAction = _summary.ToString();
        }

        void AppendMaterials(TerrainGrid grid, float[] byMaterial)
        {
            var first = true;
            for (var id = 0; id < byMaterial.Length; id++)
            {
                if (byMaterial[id] <= 0f)
                    continue;
                if (!first)
                    _summary.Append(", ");
                _summary.Append(grid.Materials.Get(new MaterialId((byte)id)).DisplayName);
                first = false;
            }
        }
    }
}
