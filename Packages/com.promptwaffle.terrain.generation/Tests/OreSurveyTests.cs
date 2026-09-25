using PromptWaffle.Terrain;
using System.Collections.Generic;
using NUnit.Framework;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>The ore view's data: the nearest ore within reach, gone once it is dug out.</summary>
    public class OreSurveyTests
    {
        static TerrainGrid Column(params Layer[] layers)
        {
            var grid = new TerrainGrid(3, 3, TestOres.CreateTable(), 1f);
            for (var z = 0; z < 3; z++)
                for (var x = 0; x < 3; x++)
                    grid.SetColumn(x, z, layers);
            return grid;
        }

        [Test]
        public void FindsTheNearestOreAndHowDeepItIs()
        {
            var grid = Column(
                new Layer(MaterialTable.Bedrock, 10f),
                new Layer(TestOres.Seam, 2f),
                new Layer(MaterialTable.Rock, 3f),
                new Layer(TestOres.Lens, 1.5f),
                new Layer(MaterialTable.Rock, 4f),
                new Layer(MaterialTable.Topsoil, 1f));
            using var survey = new OreSurvey(grid, depth: 20f);

            var find = survey.At(1, 1);
            Assert.That(find.Ore, Is.EqualTo(TestOres.Lens));
            Assert.That(find.DepthToTop, Is.EqualTo(5f).Within(1e-3f));
            Assert.That(find.Thickness, Is.EqualTo(1.5f).Within(1e-3f));
        }

        [Test]
        public void OreDeeperThanTheSurveyIsNotShown()
        {
            var grid = Column(new Layer(TestOres.Lens, 2f), new Layer(MaterialTable.Rock, 30f));
            using var survey = new OreSurvey(grid, depth: 20f);

            Assert.That(survey.At(1, 1).IsNone, Is.True);
        }

        [Test]
        public void DiggingTheOreOutClearsItsMark()
        {
            var grid = Column(new Layer(MaterialTable.Rock, 5f), new Layer(TestOres.Lowland, 1f));
            using var survey = new OreSurvey(grid);
            var changed = 0;
            survey.Changed += _ => changed++;

            grid.Remove(1, 1, 1f, new List<MaterialVolume>());

            Assert.That(survey.At(1, 1).IsNone, Is.True);
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(survey.At(0, 0).Ore, Is.EqualTo(TestOres.Lowland), "a cell nobody dug still shows it");
        }
    }
}
