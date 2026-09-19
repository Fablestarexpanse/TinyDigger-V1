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
                PanSpeed = 1f,
                BoundsMin = Vector2.zero,
                BoundsMax = new Vector2(200f, 200f),
            };
        }

        [Test]
        public void PanningForwardAtYawZeroMovesAlongPlusZ()
        {
            var rig = NewRig();

            rig.Pan(new Vector2(0f, 1f), 0.1f);

            // Speed is PanSpeed x distance per second: 1 x 100 x 0.1s = 10.
            Assert.That(rig.Pivot.z, Is.EqualTo(110f).Within(Tolerance));
            Assert.That(rig.Pivot.x, Is.EqualTo(100f).Within(Tolerance));
        }

        [Test]
        public void PanningFollowsTheYaw()
        {
            var rig = NewRig();
            rig.Yaw = 90f;

            rig.Pan(new Vector2(0f, 1f), 0.1f);

            Assert.That(rig.Pivot.x, Is.EqualTo(110f).Within(Tolerance));
            Assert.That(rig.Pivot.z, Is.EqualTo(100f).Within(Tolerance));
        }

        [Test]
        public void DiagonalPanningIsNoFasterThanStraight()
        {
            var rig = NewRig();

            rig.Pan(new Vector2(1f, 1f), 0.1f);

            var moved = new Vector2(rig.Pivot.x - 100f, rig.Pivot.z - 100f).magnitude;
            Assert.That(moved, Is.EqualTo(10f).Within(Tolerance));
        }

        [Test]
        public void PanningIsFasterWhenZoomedOut()
        {
            var near = NewRig();
            var far = NewRig();
            far.Distance = 400f;

            near.Pan(new Vector2(0f, 0.1f), 0.1f);
            far.Pan(new Vector2(0f, 0.1f), 0.1f);

            Assert.That(far.Pivot.z - 100f, Is.EqualTo((near.Pivot.z - 100f) * 4f).Within(Tolerance));
        }

        [Test]
        public void PanningStopsAtTheBounds()
        {
            var rig = NewRig();

            rig.Pan(new Vector2(1f, 0f), 100f);

            Assert.That(rig.Pivot.x, Is.EqualTo(200f));
        }

        [Test]
        public void ZoomingInShortensTheDistanceAndIsClamped()
        {
            var rig = NewRig();
            rig.ZoomStep = 0.5f;
            rig.MinDistance = 30f;
            rig.MaxDistance = 300f;

            rig.Zoom(1f);
            Assert.That(rig.Distance, Is.EqualTo(50f).Within(Tolerance));

            rig.Zoom(10f);
            Assert.That(rig.Distance, Is.EqualTo(30f));

            rig.Zoom(-100f);
            Assert.That(rig.Distance, Is.EqualTo(300f));
        }

        [Test]
        public void RotationWrapsAround()
        {
            var rig = NewRig();
            rig.RotateSpeed = 90f;

            rig.Rotate(-1f, 1f);

            Assert.That(rig.Yaw, Is.EqualTo(270f).Within(Tolerance));
        }

        [Test]
        public void TheCameraSitsDistanceAwayLookingAtThePivot()
        {
            var rig = NewRig();
            rig.Pitch = 55f;
            rig.Yaw = 30f;

            var toPivot = rig.Pivot - rig.Position;

            Assert.That(toPivot.magnitude, Is.EqualTo(100f).Within(Tolerance));
            Assert.That(Vector3.Angle(rig.Rotation * Vector3.forward, toPivot), Is.LessThan(0.01f));
            Assert.That(rig.Position.y, Is.GreaterThan(rig.Pivot.y));
        }

        [Test]
        public void FollowingTheGroundEasesTowardsItWithoutOvershooting()
        {
            var rig = NewRig();

            rig.FollowGround(10f, 0.05f);
            var partWay = rig.Pivot.y;
            for (var i = 0; i < 200; i++)
                rig.FollowGround(10f, 0.05f);

            Assert.That(partWay, Is.GreaterThan(0f).And.LessThan(10f));
            Assert.That(rig.Pivot.y, Is.EqualTo(10f).Within(Tolerance));
        }
    }
}
