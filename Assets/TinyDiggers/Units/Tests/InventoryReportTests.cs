using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    public class InventoryReportTests
    {
        [Test]
        public void ShowsContentsTopFirstWithRemainingCapacity()
        {
            var inventory = new MaterialInventory(5f);
            inventory.Add(MaterialTable.DirtLoose, 1f);
            inventory.Add(MaterialTable.RockLoose, 1.5f);

            var text = new InventoryReport().Describe(inventory, TinyDiggersMaterials.CreateTable());

            Assert.That(text, Does.Contain("Crew load 2.5 / 5.0 m³ (2.5 m³ free)"));
            Assert.That(text.IndexOf("Loose rock"), Is.LessThan(text.IndexOf("Loose dirt")), "top of the stack first");
            Assert.That(text, Does.Contain("tips first"));
        }

        [Test]
        public void SaysFullAndEmpty()
        {
            var materials = TinyDiggersMaterials.CreateTable();
            var inventory = new MaterialInventory(3f);
            var report = new InventoryReport();

            Assert.That(report.Describe(inventory, materials), Is.EqualTo("Crew load empty (3.0 m³ free)"));

            inventory.Add(MaterialTable.RockLoose, 3f);
            Assert.That(report.Describe(inventory, materials), Does.Contain("- Full"));
        }
    }
}
