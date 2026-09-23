using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// The landscape the player has asked for: an ordered list of shapes, and the target height per
    /// cell they add up to.
    ///
    /// Nothing here touches the ground. Rasterising the plan gives <see cref="PlannedCell"/>s — the
    /// same struct the road tool and <see cref="Blueprints"/> already work in, carrying the cut or
    /// fill each cell needs — so the ghost, the volume tally and the commit all come out of one
    /// pass, and the plan stays a thing you can edit rather than an edit you have made.
    ///
    /// Where two shapes overlap, **the later one wins**, unless it says <see cref="LandformBlend"/>
    /// Lower or Raise, which take the lower or the higher of the two. Order is the rule that behaves
    /// sensibly when you cut a pad into a heap; min and max are there for "dig only" and "fill only".
    /// </summary>
    public sealed class LandformPlan
    {
        readonly List<Landform> _forms = new List<Landform>();
        readonly List<RoadSample> _samples = new List<RoadSample>();
        readonly List<Vector2> _outline = new List<Vector2>();
        readonly List<int> _cells = new List<int>();
        readonly List<PlannedCell> _footprint = new List<PlannedCell>();

        /// <summary>How finely an outline is walked before its cells are worked out, in cells.</summary>
        public const float SampleSpacing = RoadPlanner.SampleSpacing;

        public IReadOnlyList<Landform> Forms => _forms;

        /// <summary>Changes whenever a shape is added, removed, or reports an edit of its own.</summary>
        public int Version { get; private set; }

        int _nextId = 1;

        public Landform Add(Landform form)
        {
            if (form == null)
                throw new ArgumentNullException(nameof(form));
            if (form.Id == 0)
                form.Id = _nextId++;
            else
                _nextId = Mathf.Max(_nextId, form.Id + 1);
            _forms.Add(form);
            Version++;
            return form;
        }

        public Landform Get(int id)
        {
            foreach (var form in _forms)
                if (form.Id == id)
                    return form;
            return null;
        }

        public bool Remove(int id)
        {
            for (var i = 0; i < _forms.Count; i++)
            {
                if (_forms[i].Id != id)
                    continue;
                _forms.RemoveAt(i);
                Version++;
                return true;
            }

            return false;
        }

        public void Clear()
        {
            if (_forms.Count == 0)
                return;
            _forms.Clear();
            Version++;
        }

        /// <summary>The plan's version plus every shape's, so any edit anywhere shows as a change.</summary>
        public int Stamp()
        {
            var stamp = Version;
            foreach (var form in _forms)
                stamp = stamp * 31 + form.Version * 7 + form.Id;
            return stamp;
        }

        /// <summary>
        /// The target height each cell should end up at, from every shape in the plan, in order.
        /// Cells no shape covers are not in the map: the plan says nothing about them, which is not
        /// the same as saying they should stay as they are.
        /// </summary>
        /// <param name="owners">
        /// Filled, if given, with which shape decided each cell — the site a unit is posted to when
        /// it is sent to work there, and how the tool knows which shape the pointer is over.
        /// </param>
        public void Surface(TerrainGrid grid, Dictionary<int, float> into, Dictionary<int, int> owners = null)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (into == null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            owners?.Clear();
            foreach (var form in _forms)
            {
                if (!form.IsDrawn)
                    continue;
                if (form.Kind == LandformKind.Ribbon)
                {
                    Ribbon(grid, form, into, owners);
                    continue;
                }

                if (form.Kind != LandformKind.Area)
                    continue;

                LandformSpline.Polygon(form, SampleSpacing, _samples, _outline);
                LandformRaster.Fill(grid, _outline, _cells);

                foreach (var cell in _cells)
                {
                    var at = new Vector2(cell % grid.Width + 0.5f, cell / grid.Width + 0.5f);
                    var height = form.TargetAt(at, grid.HeightStep);
                    if (into.TryGetValue(cell, out var already))
                        height = form.Blend switch
                        {
                            LandformBlend.Lower => Mathf.Min(already, height),
                            LandformBlend.Raise => Mathf.Max(already, height),
                            _ => height,
                        };

                    into[cell] = height;
                    if (owners != null)
                        owners[cell] = form.Id;
                }
            }
        }

        /// <summary>
        /// A ribbon: an open chain with a width, which is a road that is never paved. It is the road
        /// tool's own footprint — same curve, same nearest-sample stamping, same half-cell shoulder
        /// easing back to the ground — so a terrace edge or a haul route cut by this tool comes out
        /// exactly as the road tool would cut it, without a second implementation to drift.
        ///
        /// The one thing it does not take from a road is the refusal: a road over twice the grade
        /// limit is rejected, and a ribbon is only ever a shape somebody asked for.
        /// </summary>
        void Ribbon(TerrainGrid grid, Landform form, Dictionary<int, float> into, Dictionary<int, int> owners)
        {
            RoadSpline.Sample(form.Nodes, RoadPlanner.SampleSpacing, _samples);
            if (_samples.Count < 2)
                return;

            _footprint.Clear();
            RoadPlanner.Footprint(grid, _samples, Mathf.Max(1, form.Width), _footprint, null, includeSettled: true);
            foreach (var cell in _footprint)
            {
                var key = cell.Z * grid.Width + cell.X;
                var height = cell.Height;
                if (into.TryGetValue(key, out var already))
                    height = form.Blend switch
                    {
                        LandformBlend.Lower => Mathf.Min(already, height),
                        LandformBlend.Raise => Mathf.Max(already, height),
                        _ => height,
                    };

                into[key] = height;
                if (owners != null)
                    owners[key] = form.Id;
            }
        }

        /// <summary>
        /// The heaps' caps and the pits' floors, per cell. Separate channels from the surface,
        /// because a cap and a dig target on the same cell are different statements: one says where
        /// spoil may go, the other says what the ground should become.
        /// </summary>
        public void Zones(TerrainGrid grid, Dictionary<int, float> caps, Dictionary<int, float> floors,
            Dictionary<int, int> owners = null)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            caps?.Clear();
            floors?.Clear();
            var inside = new List<InsideCell>();
            var one = new Dictionary<int, float>();
            foreach (var form in _forms)
            {
                var heap = form.Kind == LandformKind.Heap;
                var pit = form.Kind == LandformKind.Pit;
                if (!heap && !pit || !form.IsDrawn)
                    continue;
                var into = heap ? caps : floors;
                if (into == null)
                    continue;

                LandformSpline.Polygon(form, SampleSpacing, _samples, _outline);
                LandformRaster.Fill(grid, _outline, _cells);
                LandformRaster.Inside(grid, _cells, inside);
                if (heap)
                    LandformProfile.Heap(grid, form, inside, one);
                else
                    LandformProfile.Pit(grid, form, inside, one);

                foreach (var pair in one)
                {
                    // Later wins here too, and for the same reason: a heap drawn over a pit is you
                    // changing your mind about that ground, not asking for both.
                    into[pair.Key] = pair.Value;
                    if (owners != null)
                        owners[pair.Key] = form.Id;
                }
            }
        }

        /// <summary>
        /// The whole plan as cells to dig and fill. <paramref name="includeSettled"/> keeps cells
        /// that already sit at their target, which the ghost wants so a shape draws as one piece
        /// rather than with holes wherever no work happens to be needed.
        /// </summary>
        public void Rasterise(TerrainGrid grid, List<PlannedCell> into, bool includeSettled = false)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            into.Clear();

            var surface = new Dictionary<int, float>();
            Surface(grid, surface);
            Emit(grid, surface, into, includeSettled);
        }

        /// <summary>
        /// Turns a target-height map into planned cells, leaving out the work the crew cannot do:
        /// a cut under water is refused by <see cref="DesignationMap.Designate"/>, so counting it
        /// would have the ghost promise a hole nobody can dig. A fill in the shallows is fine —
        /// reclaiming them is the point of a fill.
        /// </summary>
        public static void Emit(TerrainGrid grid, Dictionary<int, float> surface, List<PlannedCell> into,
            bool includeSettled)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            foreach (var pair in surface)
            {
                var x = pair.Key % grid.Width;
                var z = pair.Key / grid.Width;
                if (!grid.IsGround(x, z))
                    continue;
                if (grid.IsWater(x, z) && pair.Value < grid.GetSurfaceHeight(x, z))
                    continue;
                Blueprints.AddCell(grid, x, z, pair.Value, into, includeSettled);
            }

            into.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        }
    }
}
