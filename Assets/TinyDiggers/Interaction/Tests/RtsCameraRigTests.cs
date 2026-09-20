using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    public class RtsCameraRigTests
    {
        const float Tolerance = 1e-3f;

        static RtsCameraRig NewRig()
        {
            return new RtsCameraRig
            {
                Pivot = new Vector3(100f, 0f, 100f),
                Distance = 100f,
                MinDistance = 4f,
                MaxDistance = 500f,
                PanSpeed = 10f,
                PanSpeedAtFar = 5f,
                DiscCentre = new Vector2(100f, 100f),
                DiscRadius = 90f,
            };
        }

        [Test]
        public void PanningForwardAtYawZeroMovesAlongPlusZ()
        {
            var rig = NewRig();

            rig.Pan(new Vector2(0f, 1f), 0.1f);

            // 10 cells a second at the closest zoom, scaled by how far out this rig is.
            var expected = 100f + 10f * Mathf.Lerp(1f, 5f, rig.ZoomFraction) * 0.1f;
            Assert.That(rig.Pivot.z, Is.EqualTo(expected).Within(Tolerance));
            Assert.That(rig.Pivot.x, Is.EqualTo(100f).Within(Tolerance));
        }

        [Test]
        public void PanningFollowsTheHeading()
        {
            var rig = NewRig();
            rig.Yaw = 90f;

            rig.Pan(new Vector2(0f, 1f), 0.1f);

            Assert.That(rig.Pivot.x, Is.GreaterThan(100f), "forward is +x once turned a quarter turn");
            Assert.That(rig.Pivot.z, Is.EqualTo(100f).Within(Tolerance));
        }

        [Test]
        public void PanningIsFasterTheFurtherOutItIs()
        {
            var near = NewRig();
            near.Distance = near.MinDistance;
            var far = NewRig();
            far.Distance = far.MaxDistance;

            near.Pan(new Vector2(0f, 1f), 0.1f);
            far.Pan(new Vector2(0f, 1f), 0.1f);

            var nearMoved = near.Pivot.z - 100f;
            var farMoved = far.Pivot.z - 100f;
            Assert.That(farMoved, Is.EqualTo(nearMoved * 5f).Within(0.01f));
        }

        [Test]
        public void ThePivotStaysOnTheDisc()
        {
            var rig = NewRig();

            for (var i = 0; i < 200; i++)
                rig.Pan(new Vector2(1f, 0f), 0.1f);

            var fromCentre = new Vector2(rig.Pivot.x - rig.DiscCentre.x, rig.Pivot.z - rig.DiscCentre.y).magnitude;
            Assert.That(fromCentre, Is.LessThanOrEqualTo(rig.DiscRadius + Tolerance));
        }

        [Test]
        public void ZoomingStepsByAFractionAndStopsAtTheLimits()
        {
            var rig = NewRig();

            rig.Zoom(1f);
            Assert.That(rig.Distance, Is.EqualTo(100f * (1f - rig.ZoomStep)).Within(Tolerance));

            for (var i = 0; i < 200; i++)
                rig.Zoom(1f);
            Assert.That(rig.Distance, Is.EqualTo(rig.MinDistance).Within(Tolerance));

            for (var i = 0; i < 400; i++)
                rig.Zoom(-1f);
            Assert.That(rig.Distance, Is.EqualTo(rig.MaxDistance).Within(Tolerance));
        }

        [Test]
        public void ZoomingInMovesThePivotTowardThePointUnderTheCursor()
        {
            var rig = NewRig();
            var cursor = new Vector3(140f, 0f, 100f);

            rig.Zoom(1f, cursor);

            Assert.That(rig.Pivot.x, Is.GreaterThan(100f), "it moved toward the cursor");
            Assert.That(rig.Pivot.x, Is.LessThan(cursor.x), "but not all the way in one notch");

            // Zooming out leaves the pivot where it is.
            var before = rig.Pivot;
            rig.Zoom(-1f, cursor);
            Assert.That(rig.Pivot.x, Is.EqualTo(before.x).Within(Tolerance));
        }

        [Test]
        public void PitchFollowsTheZoomAndTheNudgesWhenItIsToldTo()
        {
            var rig = NewRig();
            rig.PitchFollowsZoom = true;
            rig.ClosePitch = 35f;
            rig.FarPitch = 60f;

            rig.Distance = rig.MinDistance;
            Assert.That(rig.Pitch, Is.EqualTo(35f).Within(0.01f));

            rig.Distance = rig.MaxDistance;
            Assert.That(rig.Pitch, Is.EqualTo(60f).Within(0.01f));

            rig.Tilt(-10f);
            Assert.That(rig.Pitch, Is.EqualTo(50f).Within(0.01f));
        }

        [Test]
        public void TheZoomLeavesThePitchAloneByDefault()
        {
            var rig = NewRig();
            rig.ClosePitch = 35f;
            rig.FarPitch = 60f;
            rig.SetPitch(42f);

            rig.Distance = rig.MinDistance;
            Assert.That(rig.Pitch, Is.EqualTo(42f).Within(0.01f));
            rig.Distance = rig.MaxDistance;
            Assert.That(rig.Pitch, Is.EqualTo(42f).Within(0.01f));
        }

        [Test]
        public void TiltIsFreeBetweenTenAndEightyNineDegrees()
        {
            var rig = NewRig();
            rig.SetPitch(45f);

            rig.Tilt(12f);
            Assert.That(rig.Pitch, Is.EqualTo(57f).Within(0.01f));

            rig.Tilt(1000f);
            Assert.That(rig.Pitch, Is.EqualTo(RtsCameraRig.MaxPitch).Within(0.01f));

            rig.Tilt(-1000f);
            Assert.That(rig.Pitch, Is.EqualTo(RtsCameraRig.MinPitch).Within(0.01f));
        }

        [Test]
        public void TheLensIsClampedToFifteenAndSixtyDegrees()
        {
            var rig = NewRig();
            rig.Fov = 40f;

            rig.ChangeFov(-5f);
            Assert.That(rig.Fov, Is.EqualTo(35f).Within(0.01f));

            rig.ChangeFov(-100f);
            Assert.That(rig.Fov, Is.EqualTo(RtsCameraRig.MinFov).Within(0.01f));

            rig.ChangeFov(100f);
            Assert.That(rig.Fov, Is.EqualTo(RtsCameraRig.MaxFov).Within(0.01f));
        }

        [Test]
        public void APresetCarriesTheWayTheCameraLooksButNotWhereItIs()
        {
            var rig = NewRig();
            rig.SetPitch(63f);
            rig.Fov = 22f;
            rig.Distance = 210f;
            rig.PitchFollowsZoom = false;

            var preset = ScriptableObject.CreateInstance<CameraPreset>();
            try
            {
                preset.CaptureFrom(rig);

                var other = NewRig();
                other.Pivot = new Vector3(7f, 0f, 9f);
                other.Yaw = 123f;
                preset.ApplyTo(other);

                Assert.That(other.Pitch, Is.EqualTo(63f).Within(0.01f));
                Assert.That(other.Fov, Is.EqualTo(22f).Within(0.01f));
                Assert.That(other.Distance, Is.EqualTo(210f).Within(0.01f));
                Assert.That(other.PitchFollowsZoom, Is.False);
                Assert.That(other.Pivot, Is.EqualTo(new Vector3(7f, 0f, 9f)), "a preset is a way of looking, not a place");
                Assert.That(other.Yaw, Is.EqualTo(123f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(preset);
            }
        }

        [Test]
        public void HomeTakesThePresetWhenThereIsOne()
        {
            var rig = NewRig();
            rig.Pivot = new Vector3(10f, 0f, 10f);
            rig.Distance = 20f;

            var preset = ScriptableObject.CreateInstance<CameraPreset>();
            try
            {
                preset.Pitch = 55f;
                preset.Fov = 25f;
                preset.Distance = 180f;

                rig.GoHome(preset);

                Assert.That(rig.Pivot.x, Is.EqualTo(rig.DiscCentre.x).Within(Tolerance));
                Assert.That(rig.Distance, Is.EqualTo(180f).Within(Tolerance));
                Assert.That(rig.Pitch, Is.EqualTo(55f).Within(0.01f));
                Assert.That(rig.Fov, Is.EqualTo(25f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(preset);
            }
        }

        [Test]
        public void TheCameraSitsBackFromThePivotAndLooksAtIt()
        {
            var rig = NewRig();
            rig.Yaw = 30f;

            var toPivot = rig.Pivot - rig.CameraPosition;

            Assert.That(toPivot.magnitude, Is.EqualTo(rig.Distance).Within(Tolerance));
            Assert.That(Vector3.Angle(rig.Rotation * Vector3.forward, toPivot), Is.LessThan(0.01f));
            Assert.That(rig.CameraPosition.y, Is.GreaterThan(rig.Pivot.y), "above the ground it looks at");
        }

        [Test]
        public void HomeCentresTheMapFullyZoomedOut()
        {
            var rig = NewRig();
            rig.Pivot = new Vector3(10f, 0f, 10f);
            rig.Distance = 20f;

            rig.GoHome();

            Assert.That(rig.Pivot.x, Is.EqualTo(rig.DiscCentre.x).Within(Tolerance));
            Assert.That(rig.Pivot.z, Is.EqualTo(rig.DiscCentre.y).Within(Tolerance));
            Assert.That(rig.Distance, Is.EqualTo(rig.MaxDistance).Within(Tolerance));
        }
    }
}
