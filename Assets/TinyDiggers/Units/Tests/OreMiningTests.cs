using NUnit.Framework;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units.Tests
{
    /// <summary>Dug ore stays ore: in the scoop, in the ledger, and in the heap it is tipped into.</summary>
    public class OreMiningTests
    {
        const int Size = 9;

        static TerrainGrid IronField()
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.IronOre, 4f) });
            return grid;
        }

        [Test]
        public void DiggingIronOreFillsTheScoopWithLooseIronOreNotRock()
        {
            var grid = IronField();
            var scoop = new MaterialInventory(capacity: 10f);

            Excavation.Dig(grid, scoop, 4, 4, 0, 1f);

            Assert.That(scoop.GetVolume(MaterialTable.IronOreLoose), Is.EqualTo(1.5f).Within(1e-3f), "1 m³ of ore bulks by 1.5");
            Assert.That(scoop.GetVolume(MaterialTable.RockLoose), Is.Zero);
        }

        [Test]
        public void TheLedgerCountsWhatLeftTheGroundBeforeItBulked()
        {
            var grid = IronField();
            var scoop = new MaterialInventory(capacity: 20f);
            var ledger = new MiningLedger();

            ledger.Record(Excavation.Dig(grid, scoop, 4, 4, 0, 1f).InPlaceBySource);
            ledger.Record(Excavation.Dig(grid, scoop, 3, 4, 0, 1f).InPlaceBySource);

            Assert.That(ledger.Dug(MaterialTable.IronOre), Is.EqualTo(2f).Within(1e-3f));
            Assert.That(ledger.OreSummary(grid.Materials), Is.EqualTo("Dug: Iron ore 2 m³"));
        }

        [Test]
        public void TippedOreSlumpsLikeRubble()
        {
            var table = MaterialTable.CreateDefault();
            Assert.That(table.GetDisturbed(MaterialTable.IronOre), Is.EqualTo(MaterialTable.IronOreLoose));
            Assert.That(table.Get(MaterialTable.IronOreLoose).IsLoose, Is.True);
            Assert.That(table.Get(MaterialTable.IronOreLoose).AngleOfRepose, Is.LessThan(45f));
            Assert.That(MaterialTable.IsStone(MaterialTable.IronOre), Is.True, "ore in a face stands like rock");
            Assert.That(MaterialTable.IsStone(MaterialTable.IronOreLoose), Is.False);
        }
    }
}
