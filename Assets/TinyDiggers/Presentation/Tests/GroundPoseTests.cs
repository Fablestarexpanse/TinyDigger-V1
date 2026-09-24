using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// Standing a vehicle on the ground (Ronan, 2026-09-24, after Captain of Industry: pitch and
    /// roll come from the land under the wheels, yaw from the simulation). The simulation is still
    /// flat — these tests only ask what the body is drawn at.
    /// </summary>
    public class GroundPoseTests
    {
        const int Size = 40;
        const float CellSize = 0.5f;
        const float Base = 10f;

        // A dumper's footprint, roughly: 1.7 m long, 1.3 m wide, in cells.
        static readonly Footprint Dumper = new Footprint(1.7f, 1.3f);
        static readonly Vector2 Middle = new Vector2(20f, 20f);

        static TerrainGrid Flat(float height = Base)
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f, cellSize: CellSize);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f),
                    });
            return grid;
        }

        /// <summary>Ground rising towards +z at <paramref name="grade"/> metres per metre.</summary>
        static TerrainGrid Slope(float grade)
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), heightStep: 0.5f, cellSize: CellSize);
            for (var z = 0; z < Size; z++)
            {
                var height = Base + grade * (z + 0.5f) * CellSize;
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, height - 2f),
                    });
            }

            return grid;
        }

        [Test]
        public void FlatGroundLeavesTheBodyLevel()
        {
            var fit = GroundPose.Fit(Flat(), Middle, headingDegrees: 37f, Dumper);

            Assert.That(fit.Pitch, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(fit.Roll, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(fit.Height, Is.EqualTo(Base).Within(1e-3f), "and sitting on the ground, not above it");
        }

        [Test]
        public void FacingStraightUpASlopeGivesTheSlopesAngle()
        {
            const float Degrees = 20f;
            var fit = GroundPose.Fit(Slope(Mathf.Tan(Degrees * Mathf.Deg2Rad)), Middle, headingDegrees: 0f, Dumper);

            Assert.That(fit.Pitch, Is.EqualTo(Degrees).Within(0.05f), "nose up by the grade it is climbing");
            Assert.That(fit.Roll, Is.EqualTo(0f).Within(1e-3f), "and not leaning, because the hill does not");
        }

        [Test]
        public void DrivingDownTheSameSlopePutsTheNoseDown()
        {
            const float Degrees = 20f;
            var fit = GroundPose.Fit(Slope(Mathf.Tan(Degrees * Mathf.Deg2Rad)), Middle, headingDegrees: 180f, Dumper);

            Assert.That(fit.Pitch, Is.EqualTo(-Degrees).Within(0.05f));
        }

        [Test]
        public void CrossingASlopeDiagonallyGivesBothPitchAndRoll()
        {
            const float Degrees = 20f;
            var grade = Mathf.Tan(Degrees * Mathf.Deg2Rad);
            // Quartering up the hill: the grade splits evenly between climbing it and leaning across
            // it, and the lean is to the left because the high ground is off the left shoulder.
            var expected = Mathf.Atan(grade * Mathf.Cos(45f * Mathf.Deg2Rad)) * Mathf.Rad2Deg;
            var fit = GroundPose.Fit(Slope(grade), Middle, headingDegrees: 45f, Dumper);

            Assert.That(fit.Pitch, Is.EqualTo(expected).Within(0.05f));
            Assert.That(fit.Roll, Is.EqualTo(-expected).Within(0.05f));
        }

        [Test]
        public void ARoadUnderTheWheelsIsWhatTheBodyRidesOn()
        {
            // A road is built in whole height steps and drawn at its true grade; the pose has to
            // follow what is drawn, or a machine on a graded ramp rides the steps underneath it.
            var grid = Flat();
            const float Degrees = 15f;
            var grade = Mathf.Tan(Degrees * Mathf.Deg2Rad);
            grid.DrawnHeight = (cell, height) => height + grade * (cell / Size + 0.5f) * CellSize;

            var fit = GroundPose.Fit(grid, Middle, headingDegrees: 0f, Dumper);

            Assert.That(fit.Pitch, Is.EqualTo(Degrees).Within(0.05f), "it climbs the road, not the flat ground under it");
            Assert.That(fit.Height, Is.GreaterThan(Base), "and sits on the road's surface");
        }

        [Test]
        public void GroundSteeperThanAnyMachineCouldStandOnIsClamped()
        {
            // Past the clamp the fit is wrong rather than the vehicle — a cliff edge under one
            // wheel should not stand a dumper on its nose.
            var fit = GroundPose.Fit(Slope(4f), Middle, headingDegrees: 0f, Dumper);

            Assert.That(fit.Pitch, Is.EqualTo(GroundPose.DefaultMaxTiltDegrees).Within(1e-3f));
        }

        [Test]
        public void TheFourContactPointsTurnWithTheUnit()
        {
            var points = new Vector2[4];
            GroundPose.Contacts(Middle, headingDegrees: 90f, Dumper, points);

            // Facing +x: the front pair is to the east, and the right-hand pair to the south.
            Assert.That(points[0].x, Is.EqualTo(Middle.x + Dumper.HalfLength).Within(1e-3f));
            Assert.That(points[1].x, Is.EqualTo(Middle.x + Dumper.HalfLength).Within(1e-3f));
            Assert.That(points[2].x, Is.EqualTo(Middle.x - Dumper.HalfLength).Within(1e-3f));
            Assert.That(points[1].y, Is.EqualTo(Middle.y - Dumper.HalfWidth).Within(1e-3f));
            Assert.That(points[0].y, Is.EqualTo(Middle.y + Dumper.HalfWidth).Within(1e-3f));
        }

        [Test]
        public void TheRotationKeepsTheHeadingItWasGiven()
        {
            var rotation = GroundPose.Rotation(headingDegrees: 90f, pitch: 20f, roll: -10f);
            var forward = rotation * Vector3.forward;

            Assert.That(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, Is.EqualTo(90f).Within(0.5f),
                "tipping the body does not turn it");
            Assert.That(forward.y, Is.GreaterThan(0f), "and a nose-up pitch points the nose up");
        }
    }
}
