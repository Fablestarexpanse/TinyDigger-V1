using System;
using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    public class MaterialInventoryTests
    {
        const float Tolerance = 1e-4f;

        [Test]
        public void StartsEmptyWithTheDefaultCapacity()
        {
            var inventory = new MaterialInventory();

            Assert.That(inventory.Capacity, Is.EqualTo(5f));
            Assert.That(inventory.IsEmpty, Is.True);
            Assert.That(inventory.Total, Is.EqualTo(0f));
            Assert.That(inventory.Remaining, Is.EqualTo(5f));
        }

        [Test]
        public void AddTracksTotalsPerMaterial()
        {
            var inventory = new MaterialInventory();

            inventory.Add(MaterialTable.RockLoose, 1.5f);
            inventory.Add(MaterialTable.DirtLoose, 1.25f);
            inventory.Add(MaterialTable.RockLoose, 0.5f);

            Assert.That(inventory.Total, Is.EqualTo(3.25f).Within(Tolerance));
            Assert.That(inventory.Remaining, Is.EqualTo(1.75f).Within(Tolerance));
            Assert.That(inventory.GetVolume(MaterialTable.RockLoose), Is.EqualTo(2f).Within(Tolerance));
            Assert.That(inventory.GetVolume(MaterialTable.DirtLoose), Is.EqualTo(1.25f).Within(Tolerance));
            Assert.That(inventory.GetVolume(MaterialTable.Sand), Is.EqualTo(0f));
        }

        [Test]
        public void TheStackKeepsDigOrderAndMergesARepeatOfTheTop()
        {
            var inventory = new MaterialInventory();

            inventory.Add(MaterialTable.DirtLoose, 1f);
            inventory.Add(MaterialTable.RockLoose, 1f);
            inventory.Add(MaterialTable.RockLoose, 1f);

            Assert.That(inventory.Stack.Count, Is.EqualTo(2));
            Assert.That(inventory.Stack[0].Material, Is.EqualTo(MaterialTable.DirtLoose), "oldest at the bottom");
            Assert.That(inventory.Stack[1].Material, Is.EqualTo(MaterialTable.RockLoose));
            Assert.That(inventory.Stack[1].Volume, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void TheTopIsTheMostRecentlyAddedMaterial()
        {
            var inventory = new MaterialInventory();
            inventory.Add(MaterialTable.DirtLoose, 1f);
            inventory.Add(MaterialTable.RockLoose, 2f);

            Assert.That(inventory.TryPeekTop(out var top), Is.True);
            Assert.That(top.Material, Is.EqualTo(MaterialTable.RockLoose));

            Assert.That(inventory.RemoveFromTop(5f), Is.EqualTo(2f).Within(Tolerance), "only the top entry");
            Assert.That(inventory.TryPeekTop(out top), Is.True);
            Assert.That(top.Material, Is.EqualTo(MaterialTable.DirtLoose));
        }

        [Test]
        public void TryAddRefusesWhatDoesNotFitAndChangesNothing()
        {
            var inventory = new MaterialInventory(5f);
            inventory.Add(MaterialTable.RockLoose, 4.5f);
            var version = inventory.Version;

            Assert.That(inventory.TryAdd(MaterialTable.RockLoose, 1.5f), Is.False);
            Assert.That(inventory.Total, Is.EqualTo(4.5f).Within(Tolerance));
            Assert.That(inventory.Version, Is.EqualTo(version));
            Assert.That(inventory.TryAdd(MaterialTable.RockLoose, 0.5f), Is.True, "exactly filling it is fine");
            Assert.That(inventory.Remaining, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void AddThrowsWhenOverCapacity()
        {
            var inventory = new MaterialInventory(1f);

            Assert.Throws<InvalidOperationException>(() => inventory.Add(MaterialTable.RockLoose, 1.5f));
        }

        [Test]
        public void RemoveTakesAMaterialNewestFirstFromAnywhereInTheStack()
        {
            var inventory = new MaterialInventory();
            inventory.Add(MaterialTable.RockLoose, 1f);
            inventory.Add(MaterialTable.DirtLoose, 1f);
            inventory.Add(MaterialTable.RockLoose, 1f);

            var removed = inventory.Remove(MaterialTable.DirtLoose, 5f);

            Assert.That(removed, Is.EqualTo(1f).Within(Tolerance), "only what was there");
            Assert.That(inventory.Stack.Count, Is.EqualTo(1), "the two rock entries meet and merge");
            Assert.That(inventory.Stack[0].Volume, Is.EqualTo(2f).Within(Tolerance));
            Assert.That(inventory.Total, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void EmptyingItResetsTotals()
        {
            var inventory = new MaterialInventory();
            inventory.Add(MaterialTable.RockLoose, 1.5f);
            inventory.Add(MaterialTable.DirtLoose, 1.25f);

            inventory.RemoveFromTop(10f);
            inventory.RemoveFromTop(10f);

            Assert.That(inventory.IsEmpty, Is.True);
            Assert.That(inventory.Total, Is.EqualTo(0f));
            Assert.That(inventory.GetVolume(MaterialTable.RockLoose), Is.EqualTo(0f));
            Assert.That(inventory.TryPeekTop(out _), Is.False);
            Assert.That(inventory.RemoveFromTop(1f), Is.EqualTo(0f));
        }

        [Test]
        public void InvalidInputIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MaterialInventory(0f));
            var inventory = new MaterialInventory();
            Assert.Throws<ArgumentException>(() => inventory.TryAdd(MaterialId.None, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryAdd(MaterialTable.RockLoose, -1f));
        }
    }
}
