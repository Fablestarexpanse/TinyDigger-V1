using NUnit.Framework;

namespace PromptWaffle.Terrain.Tests
{
    public class TerrainCellReportTests
    {
        [Test]
        public void ListsCoordinatesHeightAndLayersTopFirst()
        {
            var grid = new TerrainGrid(4, 4, MaterialTable.CreateBasic());
            grid.SetColumn(2, 3, new[]
            {
                new Layer(MaterialTable.Bedrock, 10f),
                new Layer(MaterialTable.Rock, 2.5f),
                new Layer(MaterialTable.Topsoil, 0.3f),
            });

            var text = new TerrainCellReport().Describe(grid, 2, 3);

            Assert.That(text, Does.Contain("Cell (2, 3)"));
            Assert.That(text, Does.Contain("Surface 12.80 m"));
            var topsoil = text.IndexOf("Topsoil");
            var rock = text.IndexOf("Rock");
            var bedrock = text.IndexOf("Bedrock");
            Assert.That(topsoil, Is.GreaterThan(0));
            Assert.That(topsoil, Is.LessThan(rock));
            Assert.That(rock, Is.LessThan(bedrock));
            Assert.That(text, Does.Contain("2.50 m"));
        }

        [Test]
        public void SaysSoWhenThereIsNoCell()
        {
            var grid = new TerrainGrid(4, 4, MaterialTable.CreateBasic());

            Assert.That(new TerrainCellReport().Describe(grid, -1, -1), Is.EqualTo("No cell under the cursor"));
        }

        [Test]
        public void ReusingTheReportDoesNotCarryOverThePreviousCell()
        {
            var grid = new TerrainGrid(4, 4, MaterialTable.CreateBasic());
            grid.Add(0, 0, MaterialTable.Sand, 1f);
            grid.Add(1, 0, MaterialTable.Clay, 1f);
            var report = new TerrainCellReport();

            report.Describe(grid, 0, 0);
            var second = report.Describe(grid, 1, 0);

            Assert.That(second, Does.Contain("Clay"));
            Assert.That(second, Does.Not.Contain("Sand"));
        }
    }
}
