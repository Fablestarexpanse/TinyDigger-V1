using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The cursor for the height tools (Slice 17): a ring on the ground, following the terrain,
    /// round what the brush covers (red dig, blue fill, purple level, green dump), and a
    /// translucent disc at the target height H, so the player sees what the cut or fill would
    /// leave before clicking. Level and Dump Zone are dragged as rectangles; for them the ring
    /// marks the cell under the cursor.
    /// </summary>
    public sealed class BrushCursorView : MonoBehaviour
    {
        const int Segments = 72;
        const float RingWidth = 0.14f;
        const float Lift = 0.05f;

        PlayerTools _tools;
        TerrainView _terrain;
        Mesh _mesh;
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _triangles = new List<int>();

        public void Init(PlayerTools tools, TerrainView terrain, Material material)
        {
            _tools = tools;
            _terrain = terrain;
            _mesh = new Mesh { name = "Brush Cursor" };
            _mesh.MarkDynamic();
            var holder = new GameObject("Brush Cursor") { hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(terrain.transform, false);
            holder.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = holder.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        void LateUpdate()
        {
            if (_mesh == null)
                return;
            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();

            var grid = _terrain.Grid;
            var mode = _tools.Mode;
            var heightTool = mode == ToolMode.Dig || mode == ToolMode.Fill || mode == ToolMode.Level || mode == ToolMode.DumpZone;
            if (grid != null && _tools.HasHover && heightTool)
            {
                var cell = grid.CellSize;
                var centre = new Vector2((_tools.HoverX + 0.5f) * cell, (_tools.HoverZ + 0.5f) * cell);
                var brush = mode == ToolMode.Dig || mode == ToolMode.Fill;
                var radius = (brush ? _tools.BrushRadius + 0.5f : 0.75f) * cell;
                var height = _tools.IsPainting ? _tools.StrokeHeight : _tools.TargetHeight;
                if (brush)
                    Disc(centre, radius, height, PlayerTools.ToolColor(mode, 70));
                Ring(grid, centre, radius, PlayerTools.ToolColor(mode, 230));
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, true);
        }

        /// <summary>A flat disc at <paramref name="height"/>: the ground as the brush would leave it.</summary>
        void Disc(Vector2 centre, float radius, float height, Color32 color)
        {
            var start = _vertices.Count;
            _vertices.Add(new Vector3(centre.x, height + 0.02f, centre.y));
            _colors.Add(color);
            for (var i = 0; i <= Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                _vertices.Add(new Vector3(centre.x + Mathf.Cos(a) * radius, height + 0.02f, centre.y + Mathf.Sin(a) * radius));
                _colors.Add(color);
                if (i == 0)
                    continue;
                // Both faces, so it reads from below a cliff as well as from above.
                _triangles.Add(start);
                _triangles.Add(start + i + 1);
                _triangles.Add(start + i);
                _triangles.Add(start);
                _triangles.Add(start + i);
                _triangles.Add(start + i + 1);
            }
        }

        /// <summary>A band round the brush lying on the ground, a little above it.</summary>
        void Ring(TerrainGrid grid, Vector2 centre, float radius, Color32 color)
        {
            var start = _vertices.Count;
            var inner = Mathf.Max(0.05f, radius - RingWidth * 0.5f);
            var outer = radius + RingWidth * 0.5f;
            for (var i = 0; i <= Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                var direction = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var ground = GroundAt(grid, centre + direction * radius) + Lift;
                _vertices.Add(new Vector3(centre.x + direction.x * inner, ground, centre.y + direction.y * inner));
                _vertices.Add(new Vector3(centre.x + direction.x * outer, ground, centre.y + direction.y * outer));
                _colors.Add(color);
                _colors.Add(color);
                if (i == 0)
                    continue;
                var b = start + (i - 1) * 2;
                _triangles.Add(b);
                _triangles.Add(b + 2);
                _triangles.Add(b + 1);
                _triangles.Add(b + 1);
                _triangles.Add(b + 2);
                _triangles.Add(b + 3);
            }
        }

        /// <summary>The ground under a local point, averaged with its neighbours so the ring does not stair-step.</summary>
        static float GroundAt(TerrainGrid grid, Vector2 local)
        {
            var fx = local.x / grid.CellSize - 0.5f;
            var fz = local.y / grid.CellSize - 0.5f;
            var x0 = Mathf.FloorToInt(fx);
            var z0 = Mathf.FloorToInt(fz);
            var tx = fx - x0;
            var tz = fz - z0;
            float H(int x, int z)
            {
                x = Mathf.Clamp(x, 0, grid.Width - 1);
                z = Mathf.Clamp(z, 0, grid.Height - 1);
                return grid.IsGround(x, z) ? grid.GetSurfaceHeight(x, z) : World.SeaLevel;
            }

            var low = Mathf.Lerp(H(x0, z0), H(x0 + 1, z0), tx);
            var high = Mathf.Lerp(H(x0, z0 + 1), H(x0 + 1, z0 + 1), tx);
            // The top of the step, not its middle, or the ring sinks into every terrace edge.
            return Mathf.Max(Mathf.Lerp(low, high, tz), H(Mathf.RoundToInt(fx), Mathf.RoundToInt(fz)));
        }
    }
}
