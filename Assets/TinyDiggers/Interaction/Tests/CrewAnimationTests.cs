using NUnit.Framework;
using TinyDiggers.Units;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>Which clip a crew unit plays for what it is doing.</summary>
    public class CrewAnimationTests
    {
        [TestCase(CrewUnitState.Digging)]
        [TestCase(CrewUnitState.Tipping)]
        [TestCase(CrewUnitState.Transferring)]
        public void WorkingIsWorkLoadedOrNot(CrewUnitState state)
        {
            Assert.That(CrewAnimation.StateFor(state, false), Is.EqualTo(CrewAnimation.Work));
            Assert.That(CrewAnimation.StateFor(state, true), Is.EqualTo(CrewAnimation.Work));
        }

        [Test]
        public void AnEmptyUnitMovesAndALoadedOneCarries()
        {
            Assert.That(CrewAnimation.StateFor(CrewUnitState.Moving, false), Is.EqualTo(CrewAnimation.Move));
            Assert.That(CrewAnimation.StateFor(CrewUnitState.Moving, true), Is.EqualTo(CrewAnimation.Carry));
        }

        [TestCase(CrewUnitState.Idle)]
        [TestCase(CrewUnitState.Parked)]
        [TestCase(CrewUnitState.Waiting)]
        [TestCase(CrewUnitState.Unreachable)]
        public void AnythingStandingStillIsIdleLoadedOrNot(CrewUnitState state)
        {
            Assert.That(CrewAnimation.StateFor(state, false), Is.EqualTo(CrewAnimation.Idle));
            // Standing still is Idle whether or not it is loaded: a machine's Carry is its walk
            // cycle, so a loaded one standing on Carry walked on the spot (2026-09-23).
            Assert.That(CrewAnimation.StateFor(state, true), Is.EqualTo(CrewAnimation.Idle));
        }
    }
}
