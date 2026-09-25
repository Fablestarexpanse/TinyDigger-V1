using System;
using System.Collections.Generic;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// What a cell of a planned shape asks for: the height it should end at, and whether that
    /// means cutting or filling from where the ground is now.
    /// </summary>
    public readonly struct PlannedCell
    {
        public PlannedCell(int x, int z, float height, float cut, float fill)
        {
            X = x;
            Z = z;
            Height = height;
            Cut = cut;
            Fill = fill;
        }

        public int X { get; }

        public int Z { get; }

        /// <summary>The height the cell should finish at.</summary>
        public float Height { get; }

        /// <summary>Metres of ground to take off this cell, or zero.</summary>
        public float Cut { get; }

        /// <summary>Metres of material to put on this cell, or zero.</summary>
        public float Fill { get; }

        public bool IsDig => Cut > 0f;

        public bool IsFill => Fill > 0f;
    }

    /// <summary>
    /// Shapes the player draws that turn into designations: levelling a pad, and laying a road.
    /// Plain C#, so the tools can show a costed ghost before anything is committed, and so the
    /// arithmetic can be tested without a scene.
    ///
    /// Both work the same way: they work out a target height per cell, and each cell becomes a
    /// dig or a fill depending on which side of that height the ground is. Cells already at the
    /// target, and cells off the map, are left alone.
    /// </summary>
    public static class Blueprints
    {
        /// <summary>Heights within this of the target need no work.</summary>
        public const float Tolerance = 1e-3f;

        /// <summary>
        /// Every cell in the rectangle, levelled to <paramref name="height"/>.
        /// </summary>
        public static void PlanLevel(TerrainGrid grid, RectInt area, float height, List<PlannedCell> into, bool includeSettled = false)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            for (var z = area.yMin; z < area.yMax; z++)
                for (var x = area.xMin; x < area.xMax; x++)
                    AddCell(grid, x, z, height, into, includeSettled);
        }

        /// <summary>
        /// A ramp over the rectangle (Slice 17 Part B): <paramref name="heightA"/> along the edge
        /// nearest <paramref name="from"/> (where the drag started), <paramref name="heightB"/>
        /// along the far edge, even between, running along the rectangle's longer side. Heights are
        /// rounded to the grid's height step, so a gentle ramp comes out as even treads.
        /// </summary>
        public static void PlanRamp(TerrainGrid grid, RectInt area, Vector2Int from, float heightA, float heightB,
            List<PlannedCell> into, bool includeSettled = false)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            var alongX = area.width >= area.height;
            var span = (alongX ? area.width : area.height) - 1;
            var startsLow = alongX ? from.x <= area.xMin : from.y <= area.yMin;
            for (var z = area.yMin; z < area.yMax; z++)
            {
                for (var x = area.xMin; x < area.xMax; x++)
                {
                    var along = alongX ? x - area.xMin : z - area.yMin;
                    var t = span > 0 ? along / (float)span : 0f;
                    if (!startsLow)
                        t = 1f - t;
                    var height = Mathf.Lerp(heightA, heightB, t);
                    if (grid.HeightStep > 0f)
                        height = Mathf.Round(height / grid.HeightStep) * grid.HeightStep;
                    AddCell(grid, x, z, height, into, includeSettled);
                }
            }
        }

        /// <summary>
        /// A road of <paramref name="width"/> cells through the control points, its height
        /// interpolated along each leg between the heights of the points at either end, so the
        /// grade is even between them.
        ///
        /// **Superseded, and kept only for its tests.** Roads in the game are straight legs no
        /// longer: they run on a Hermite spline through <see cref="RoadNode"/>s with draggable
        /// handles, and are stamped by <see cref="RoadPlanner.Footprint"/> from
        /// <see cref="RoadSpline.Sample"/>. Landform ribbons go the same way. Nothing outside this
        /// class's own tests calls this, or <see cref="SteepestGrade"/>, <see cref="LegGrade"/> or
        /// <see cref="RoadPoint"/> — start from <see cref="RoadPlanner"/> instead.
        /// </summary>
        public static void PlanRoad(TerrainGrid grid, IReadOnlyList<RoadPoint> points, int width, List<PlannedCell> into, bool includeSettled = false)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            if (points == null || points.Count < 2)
                return;

            var radius = width * 0.5f;
            var seen = new HashSet<int>();
            for (var leg = 0; leg + 1 < points.Count; leg++)
            {
                var from = points[leg];
                var to = points[leg + 1];
                var start = new Vector2(from.Cell.x + 0.5f, from.Cell.y + 0.5f);
                var end = new Vector2(to.Cell.x + 0.5f, to.Cell.y + 0.5f);
                var along = end - start;
                var length = along.magnitude;
                if (length < 1e-4f)
                    continue;

                // Every cell the leg could touch: its bounding box, grown by the half width.
                var minX = Mathf.FloorToInt(Math.Min(start.x, end.x) - radius - 1f);
                var maxX = Mathf.CeilToInt(Math.Max(start.x, end.x) + radius + 1f);
                var minZ = Mathf.FloorToInt(Math.Min(start.y, end.y) - radius - 1f);
                var maxZ = Mathf.CeilToInt(Math.Max(start.y, end.y) + radius + 1f);
                for (var z = minZ; z <= maxZ; z++)
                {
                    for (var x = minX; x <= maxX; x++)
                    {
                        if (!grid.IsGround(x, z) || seen.Contains(z * grid.Width + x))
                            continue;

                        // Distance from the centre line, and how far along it the nearest point is.
                        var centre = new Vector2(x + 0.5f, z + 0.5f);
                        var t = Mathf.Clamp01(Vector2.Dot(centre - start, along) / (length * length));
                        var onLine = start + along * t;
                        if ((centre - onLine).sqrMagnitude > radius * radius)
                            continue;

                        var height = Mathf.Lerp(from.Height, to.Height, t);
                        if (grid.HeightStep > 0f)
                            height = Mathf.Round(height / grid.HeightStep) * grid.HeightStep;
                        seen.Add(z * grid.Width + x);
                        AddCell(grid, x, z, height, into, includeSettled);
                    }
                }
            }
        }

        /// <summary>
        /// The steepest leg of a road, in metres of rise per metre travelled, on cells
        /// <paramref name="cellSize"/> metres across. A road is refused when this is over the
        /// machines' limit.
        /// </summary>
        public static float SteepestGrade(IReadOnlyList<RoadPoint> points, out int steepestLeg, float cellSize = 1f)
        {
            steepestLeg = -1;
            var worst = 0f;
            if (points == null)
                return 0f;
            for (var leg = 0; leg + 1 < points.Count; leg++)
            {
                var grade = LegGrade(points[leg], points[leg + 1], cellSize);
                if (grade <= worst)
                    continue;
                worst = grade;
                steepestLeg = leg;
            }

            return worst;
        }

        /// <summary>Metres of rise per metre travelled along one leg, on cells <paramref name="cellSize"/> across.</summary>
        public static float LegGrade(RoadPoint from, RoadPoint to, float cellSize = 1f)
        {
            var run = Vector2.Distance(from.Cell, to.Cell) * cellSize;
            return run < 1e-4f ? 0f : Math.Abs(to.Height - from.Height) / run;
        }

        /// <summary>Turns planned cells into Dig and Fill designations. Returns how many were made.</summary>
        public static int Apply(DesignationMap map, IReadOnlyList<PlannedCell> cells)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            if (cells == null)
                return 0;

            var made = 0;
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var kind = cell.IsDig ? DesignationKind.Dig : DesignationKind.Fill;
                if (map.Designate(cell.X, cell.Z, kind, cell.Height))
                    made++;
            }

            return made;
        }

        /// <summary>
        /// Total cut and fill across a plan in m³: each cell's metres of cut or fill times
        /// <paramref name="cellArea"/> (the grid's <see cref="TerrainGrid.CellArea"/>).
        /// </summary>
        public static void Volumes(IReadOnlyList<PlannedCell> cells, out float cut, out float fill, float cellArea = 1f)
        {
            cut = 0f;
            fill = 0f;
            if (cells == null)
                return;
            for (var i = 0; i < cells.Count; i++)
            {
                cut += cells[i].Cut;
                fill += cells[i].Fill;
            }

            cut *= cellArea;
            fill *= cellArea;
        }

        /// <summary>
        /// Adds the cell to the plan. Cells already at the target are left out unless
        /// <paramref name="includeSettled"/> is set, which the ghost does so the shape draws as
        /// one piece rather than with holes where no work is needed.
        /// </summary>
        public static void AddCell(TerrainGrid grid, int x, int z, float height, List<PlannedCell> into, bool includeSettled)
        {
            if (!grid.IsGround(x, z))
                return;
            var surface = grid.GetSurfaceHeight(x, z);
            var difference = surface - height;
            if (Math.Abs(difference) <= Tolerance)
            {
                if (includeSettled)
                    into.Add(new PlannedCell(x, z, height, 0f, 0f));
                return;
            }
            into.Add(difference > 0f
                ? new PlannedCell(x, z, height, difference, 0f)
                : new PlannedCell(x, z, height, 0f, -difference));
        }
    }

    /// <summary>A control point of a road: a cell, and the height the road passes through it at.</summary>
    public readonly struct RoadPoint
    {
        public RoadPoint(Vector2Int cell, float height)
        {
            Cell = cell;
            Height = height;
        }

        public Vector2Int Cell { get; }

        public float Height { get; }
    }
}
