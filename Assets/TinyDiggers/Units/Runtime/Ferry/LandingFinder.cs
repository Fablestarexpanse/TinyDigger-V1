using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Where the landing craft beaches (FERRY_PROPOSAL.md, slice A): its stern floats, it lies
    /// bow on to the shore, the ramp at the bow reaches dry ground a machine can stand on, and
    /// behind the ramp foot there is a straight run of ground for a machine to line up on and
    /// reverse aboard, the way the dump truck backs in to tip.
    /// </summary>
    public readonly struct Landing
    {
        /// <summary>Where the craft's middle sits, cells.</summary>
        public readonly Vector2 Hull;

        /// <summary>Degrees, bow toward the shore (0 = +z, 90 = +x, as <see cref="CrewUnit.Heading"/>).</summary>
        public readonly float Heading;

        /// <summary>The land cell the ramp rests on, and the first cell a machine drives onto.</summary>
        public readonly Vector2Int RampFoot;

        /// <summary>Why there is no landing here, or null when there is one.</summary>
        public readonly string Refusal;

        public bool Found => Refusal == null;

        public Landing(Vector2 hull, float heading, Vector2Int rampFoot)
        {
            Hull = hull;
            Heading = heading;
            RampFoot = rampFoot;
            Refusal = null;
        }

        Landing(string refusal)
        {
            Hull = default;
            Heading = 0f;
            RampFoot = default;
            Refusal = refusal;
        }

        public static Landing Refused(string why) => new Landing(why);
    }

    public static class LandingFinder
    {
        /// <summary>Why a place was turned down, most telling first; the first seen wins a tie.</summary>
        public const string TooShallow = "too shallow to float here";
        public const string TooSteep = "bank too steep for the ramp";
        public const string NoRoom = "no room behind the ramp to line up";
        public const string NoShore = "no shore in reach";

        static readonly Vector2Int[] Headings =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(1, -1),
            new Vector2Int(0, -1), new Vector2Int(-1, -1), new Vector2Int(-1, 0), new Vector2Int(-1, 1),
        };

        /// <summary>
        /// The best landing within <paramref name="searchRadius"/> cells of <paramref name="near"/>:
        /// the nearest good one. Refused with the reason that stopped the most candidates when
        /// there is none.
        /// </summary>
        /// <param name="halfLength">Half the craft's length, metres, bow to middle.</param>
        /// <param name="rampReach">How far past the bow the ramp's foot lands, metres.</param>
        /// <param name="rampRise">The most the ramp foot may stand above or below the water, metres.</param>
        /// <param name="lineUpCells">Cells of straight ground a machine needs behind the ramp foot.</param>
        public static Landing Find(TerrainGrid grid, WaterNav nav, Vector2Int near, int searchRadius,
            float halfLength, float rampReach, float rampRise, int lineUpCells, float maxStep)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (nav == null)
                throw new ArgumentNullException(nameof(nav));

            var cell = Math.Max(0.01f, grid.CellSize);
            var toBow = halfLength / cell;
            var toFoot = toBow + rampReach / cell;
            int shallow = 0, steep = 0, cramped = 0;
            var best = Landing.Refused(NoShore);
            var bestScore = float.MaxValue;

            for (var hz = near.y - searchRadius; hz <= near.y + searchRadius; hz++)
                for (var hx = near.x - searchRadius; hx <= near.x + searchRadius; hx++)
                {
                    if (!grid.InBounds(hx, hz))
                        continue;
                    foreach (var heading in Headings)
                    {
                        var dir = new Vector2(heading.x, heading.y).normalized;
                        var hull = new Vector2(hx + 0.5f, hz + 0.5f);
                        var foot = Cell(hull + dir * toFoot);
                        if (!grid.InBounds(foot.x, foot.y))
                            continue;
                        // Only a hull in the water with its bow at land is a landing at all; a hull
                        // on dry ground says nothing about why the water here will not do.
                        if (!grid.IsPassableGround(foot.x, foot.y) || grid.WaterDepth(foot.x, foot.y) > 0.05f
                            || grid.WaterDepth(hx, hz) <= 0f)
                            continue;
                        // Beached, the bow is on the sand and the stern afloat: the stern floats
                        // with its whole beam clear, and the middle has at least the draft under it.
                        // Asking the middle to float clear only let it land against a cliff.
                        var stern = Cell(hull - dir * (toBow * 0.6f));
                        if (!nav.Floats(stern.x, stern.y) || grid.WaterDepth(hx, hz) < nav.Draft)
                        {
                            shallow++;
                            continue;
                        }

                        // The bow half may run up into the shallows and onto sand at the water's
                        // edge, as a landing craft's does, but not up a bank above the water.
                        var water = grid.GetSurfaceHeight(hx, hz) + grid.WaterDepth(hx, hz);
                        var bowOnLand = false;
                        for (var k = 1f; k < toBow && !bowOnLand; k += 0.5f)
                        {
                            var c = Cell(hull + dir * k);
                            bowOnLand = !grid.IsGround(c.x, c.y) || grid.GetSurfaceHeight(c.x, c.y) > water + 0.1f;
                        }

                        // The whole hull behind the bow, stern end and both sides, lies over water.
                        // Checking one point near the stern let a craft moor across a river with its
                        // stern on the far bank: a bridge, which it is not (Ronan, 2026-09-24).
                        var halfBeam = nav.HalfBeamCells;
                        var side = new Vector2(dir.y, -dir.x);
                        for (var k = -toBow; k <= toBow * 0.5f && !bowOnLand; k += 0.5f)
                            for (var w = -halfBeam; w <= halfBeam + 1e-3f && !bowOnLand; w += halfBeam)
                            {
                                var c = Cell(hull + dir * k + side * w);
                                bowOnLand = !grid.IsGround(c.x, c.y) || grid.GetSurfaceHeight(c.x, c.y) > water + 0.1f;
                            }

                        var footHeight = grid.GetSurfaceHeight(foot.x, foot.y);
                        if (bowOnLand || Math.Abs(footHeight - water) > rampRise)
                        {
                            steep++;
                            continue;
                        }

                        // A straight run inland for a machine to line up and reverse aboard.
                        var room = true;
                        var previous = footHeight;
                        for (var k = 1; k <= lineUpCells && room; k++)
                        {
                            var c = foot + heading * k;
                            if (!grid.IsPassableGround(c.x, c.y) || grid.WaterDepth(c.x, c.y) > 0.05f)
                            {
                                room = false;
                                break;
                            }

                            var height = grid.GetSurfaceHeight(c.x, c.y);
                            room = Math.Abs(height - previous) <= maxStep;
                            previous = height;
                        }

                        if (!room)
                        {
                            cramped++;
                            continue;
                        }

                        var score = (hull - new Vector2(near.x + 0.5f, near.y + 0.5f)).sqrMagnitude;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        var degrees = (float)(Math.Atan2(heading.x, heading.y) * 180.0 / Math.PI);
                        best = new Landing(hull, degrees, foot);
                    }
                }

            if (best.Found)
                return best;
            if (shallow == 0 && steep == 0 && cramped == 0)
                return Landing.Refused(NoShore);
            // The reason that stopped the most bows pointed at land.
            if (steep >= shallow && steep >= cramped)
                return Landing.Refused(TooSteep);
            return Landing.Refused(cramped >= shallow ? NoRoom : TooShallow);
        }

        static Vector2Int Cell(Vector2 at) => new Vector2Int(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y));
    }
}
