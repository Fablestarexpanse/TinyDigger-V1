using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>Box selection's screen maths.</summary>
    public class SelectionBoxTests
    {
        [Test]
        public void AShortWiggleIsAClickAndALongerPullIsABox()
        {
            Assert.That(SelectionBox.IsDrag(new Vector2(100, 100), new Vector2(103, 102)), Is.False);
            Assert.That(SelectionBox.IsDrag(new Vector2(100, 100), new Vector2(110, 100)), Is.True);
        }

        [Test]
        public void TheBoxIsTheSameWhicheverWayItIsDragged()
        {
            var a = SelectionBox.FromCorners(new Vector2(10, 50), new Vector2(40, 20));
            var b = SelectionBox.FromCorners(new Vector2(40, 20), new Vector2(10, 50));

            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.xMin, Is.EqualTo(10f));
            Assert.That(a.yMin, Is.EqualTo(20f));
            Assert.That(a.width, Is.EqualTo(30f));
            Assert.That(a.height, Is.EqualTo(30f));
        }

        [Test]
        public void UnitsInsideCountAndOnesBehindTheCameraNever()
        {
            var box = SelectionBox.FromCorners(new Vector2(0, 0), new Vector2(100, 100));

            Assert.That(SelectionBox.Contains(box, new Vector3(50, 50, 20)), Is.True);
            Assert.That(SelectionBox.Contains(box, new Vector3(150, 50, 20)), Is.False);
            Assert.That(SelectionBox.Contains(box, new Vector3(50, 50, -5)), Is.False, "behind the camera");
        }

        [Test]
        public void TheGuiRectangleIsFlippedToCountFromTheTop()
        {
            var box = SelectionBox.FromCorners(new Vector2(10, 20), new Vector2(60, 100));

            var gui = SelectionBox.ToGui(box, 1080f);

            Assert.That(gui.xMin, Is.EqualTo(10f));
            Assert.That(gui.yMin, Is.EqualTo(980f));
            Assert.That(gui.height, Is.EqualTo(80f));
        }
    }
}
