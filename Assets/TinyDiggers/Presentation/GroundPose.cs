using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The ground a vehicle's wheels stand on, in cells: half its length fore and aft, half its
    /// width left and right of its centre. Taken from the body as it is drawn rather than from the
    /// unit's collision radius, because a machine should sit on its own visible tracks.
    /// </summary>
    public readonly struct Footprint
    {
        public Footprint(float halfLength, float halfWidth)
        {
            HalfLength = Mathf.Max(1e-3f, halfLength);
            HalfWidth = Mathf.Max(1e-3f, halfWidth);
        }

        /// <summary>Half the wheelbase, in cells, along the direction the unit faces.</summary>
        public float HalfLength { get; }

        /// <summary>Half the track width, in cells, across it.</summary>
        public float HalfWidth { get; }
    }

    /// <summary>How a body lies on the ground: where its centre sits, and how far it is tipped.</summary>
    public readonly struct GroundFit
    {
        public GroundFit(float height, float pitch, float roll)
        {
            Height = height;
            Pitch = pitch;
            Roll = roll;
        }

        /// <summary>The fitted plane's height at the unit's centre, in metres.</summary>
        public float Height { get; }

        /// <summary>Degrees the nose is raised: positive when the ground rises ahead of the unit.</summary>
        public float Pitch { get; }

        /// <summary>Degrees the right-hand side is raised: positive when the ground falls away to the left.</summary>
        public float Roll { get; }
    }

    /// <summary>
    /// Standing a vehicle on the ground it is driving over. The simulation stays two-dimensional —
    /// a unit has a cell, a height and a heading, and none of that changes here. Only what is drawn
    /// tips: pitch and roll come from the land under the four wheels, yaw from the simulation.
    ///
    /// The ground is sampled through <see cref="TerrainSurface.SampleHeight"/>, which is bilinear
    /// over the smoothed corner field, so the pose follows the surface the player sees rather than
    /// the column steps underneath it — and roads come free, because a built road feeds the same
    /// <see cref="TerrainGrid.DrawnHeight"/> hook that corner heights read.
    ///
    /// Plain C# with no scene behind it, so the arithmetic is testable.
    /// </summary>
    public static class GroundPose
    {
        /// <summary>Past this, the fit is wrong rather than the vehicle: no machine stands on a 40° side slope.</summary>
        public const float DefaultMaxTiltDegrees = 35f;

        /// <summary>
        /// The four ground-contact points, in cells: front-left, front-right, rear-left, rear-right,
        /// turned to face <paramref name="headingDegrees"/> (0 = +z, as <c>CrewUnit.Heading</c> is)
        /// and placed at <paramref name="at"/>.
        /// </summary>
        public static void Contacts(Vector2 at, float headingDegrees, Footprint print, Vector2[] into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            if (into.Length < 4)
                throw new ArgumentException("Needs room for four contact points.", nameof(into));

            var radians = headingDegrees * Mathf.Deg2Rad;
            var sin = Mathf.Sin(radians);
            var cos = Mathf.Cos(radians);
            var forward = new Vector2(sin, cos) * print.HalfLength;
            var right = new Vector2(cos, -sin) * print.HalfWidth;

            into[0] = at + forward - right;
            into[1] = at + forward + right;
            into[2] = at - forward - right;
            into[3] = at - forward + right;
        }

        /// <summary>
        /// The plane through the ground under those four points, as a height at the centre and a
        /// pitch and roll in degrees, clamped to <paramref name="maxTiltDegrees"/>.
        ///
        /// The four points are symmetric about the centre, which collapses the least-squares plane
        /// to two differences: the height is their mean, the fore-and-aft slope is front minus rear
        /// over the wheelbase, and the side slope is right minus left over the track. No solver.
        /// </summary>
        public static GroundFit Fit(TerrainGrid grid, Vector2 at, float headingDegrees, Footprint print,
            float maxTiltDegrees = DefaultMaxTiltDegrees)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            var points = new Vector2[4];
            Contacts(at, headingDegrees, print, points);
            var frontLeft = TerrainSurface.SampleHeight(grid, points[0].x, points[0].y);
            var frontRight = TerrainSurface.SampleHeight(grid, points[1].x, points[1].y);
            var rearLeft = TerrainSurface.SampleHeight(grid, points[2].x, points[2].y);
            var rearRight = TerrainSurface.SampleHeight(grid, points[3].x, points[3].y);

            // Heights are metres and the footprint is cells, so the run has to be converted before
            // it can be divided into a rise — otherwise every slope reads twice as steep on a
            // half-metre grid.
            var alongMetres = 2f * print.HalfLength * grid.CellSize;
            var acrossMetres = 2f * print.HalfWidth * grid.CellSize;
            var rise = (frontLeft + frontRight - rearLeft - rearRight) * 0.5f / alongMetres;
            var tilt = (frontRight + rearRight - frontLeft - rearLeft) * 0.5f / acrossMetres;

            var limit = Mathf.Max(0f, maxTiltDegrees);
            return new GroundFit(
                (frontLeft + frontRight + rearLeft + rearRight) * 0.25f,
                Mathf.Clamp(Mathf.Atan(rise) * Mathf.Rad2Deg, -limit, limit),
                Mathf.Clamp(Mathf.Atan(tilt) * Mathf.Rad2Deg, -limit, limit));
        }

        /// <summary>
        /// The body's rotation: yaw about the world's up, then pitch and roll about the body's own
        /// axes, so a machine tipped onto a slope still points where the simulation says it does.
        /// </summary>
        public static Quaternion Rotation(float headingDegrees, float pitch, float roll) =>
            Quaternion.Euler(0f, headingDegrees, 0f) * Quaternion.Euler(-pitch, 0f, roll);
    }
}
