using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>The pieces the interface is built from, and the one thing it cannot work without.</summary>
    public class UiKitTests
    {
        GameObject _holder;
        EventSystem _madeHere;

        [SetUp]
        public void SetUp()
        {
            _holder = new GameObject("UiKit test root");
            _madeHere = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_madeHere != null)
                Object.DestroyImmediate(_madeHere.gameObject);
            Object.DestroyImmediate(_holder);
        }

        /// <summary>
        /// The event system living in the scene, whatever its hide flags: EventSystem.current is a
        /// play-mode notion and is not set by simply adding the component in an edit-mode test.
        /// </summary>
        static EventSystem InScene()
        {
            foreach (var one in Resources.FindObjectsOfTypeAll<EventSystem>())
                if (one.gameObject.scene.IsValid())
                    return one;
            return null;
        }

        [Test]
        public void ACanvasComesWithAnEventSystemOrNothingIsClickable()
        {
            // Without an EventSystem no uGUI raycast happens at all, so every button, toggle and
            // typed field in the game is dead — and worse, PlayerTools asks EventSystem.current
            // whether the pointer is over the interface, gets null, and digs the ground behind the
            // panel instead. The scene carried none and nothing made one, so no tool panel had
            // ever taken a click (Ronan, 2026-09-23: "I can't use my mouse on any of the tools").
            var already = InScene();

            var canvas = UiKit.NewCanvas(_holder.transform, "Test Canvas", 0);

            Assert.That(canvas.GetComponent<GraphicRaycaster>(), Is.Not.Null,
                "a canvas needs a raycaster to know what was clicked");
            Assert.That(InScene(), Is.Not.Null,
                "and an event system to do the clicking, which the canvas has to bring with it");

            if (already == null)
                _madeHere = InScene();
        }

        [Test]
        public void ASecondCanvasDoesNotBringASecondEventSystem()
        {
            // Two of them fight over the input and Unity warns about it every frame.
            var already = InScene();
            UiKit.NewCanvas(_holder.transform, "First", 0);
            if (already == null)
                _madeHere = InScene();
            UiKit.NewCanvas(_holder.transform, "Second", 1);

            var live = 0;
            foreach (var one in Resources.FindObjectsOfTypeAll<EventSystem>())
                if (one.gameObject.scene.IsValid())
                    live++;

            Assert.That(live, Is.EqualTo(1), "one event system, however many canvases there are");
        }
    }
}
