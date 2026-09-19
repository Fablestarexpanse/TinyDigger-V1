using System;
using System.Text;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Finds the cell under the mouse each frame; left click digs, right click tips Dirt, both
    /// across <see cref="TerrainView.BrushRadius"/>. The picking and brush logic live in
    /// <see cref="TerrainPicker"/> and <see cref="TerrainBrush"/>.
    /// </summary>
    public sealed class TerrainEditTool : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] Camera _camera;

        [Tooltip("Volume dug from, or tipped onto, each cell in the brush per click. Cells are 1x1 m, so this is metres.")]
        [SerializeField, Min(0.01f)] float _volumePerCell = 1f;

        readonly StringBuilder _summary = new StringBuilder(128);
        float[] _removedByMaterial;

        public bool HasHover { get; private set; }

        public int HoverX { get; private set; }

        public int HoverZ { get; private set; }

        public float VolumePerCell => _volumePerCell;

        /// <summary>What the last click did, for the readout. Rebuilt only on clicks.</summary>
        public string LastAction { get; private set; } = "";

        void Start()
        {
            _removedByMaterial = new float[_terrain.Grid.Materials.MaxId + 1];
        }

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
                Fill(grid, x, z);
        }

        void Dig(TerrainGrid grid, int x, int z)
        {
            Array.Clear(_removedByMaterial, 0, _removedByMaterial.Length);
            var total = TerrainBrush.Dig(grid, x, z, _terrain.BrushRadius, _volumePerCell, _removedByMaterial);

            _summary.Clear();
            if (total <= 0f)
            {
                _summary.Append("Dug nothing: bedrock");
            }
            else
            {
                _summary.Append("Dug ").Append(total.ToString("0.0")).Append(" m3:");
                for (var id = 0; id < _removedByMaterial.Length; id++)
                {
                    if (_removedByMaterial[id] <= 0f)
                        continue;
                    _summary.Append(' ')
                        .Append(grid.Materials.Get(new MaterialId((byte)id)).DisplayName)
                        .Append(' ')
                        .Append(_removedByMaterial[id].ToString("0.0"));
                }
            }

            LastAction = _summary.ToString();
        }

        void Fill(TerrainGrid grid, int x, int z)
        {
            var added = TerrainBrush.Fill(grid, x, z, _terrain.BrushRadius, MaterialTable.Dirt, _volumePerCell);
            LastAction = added > 0f
                ? $"Tipped {added:0.0} m3 of Dirt"
                : "Tipped nothing: layer stacks full";
        }
    }
}
