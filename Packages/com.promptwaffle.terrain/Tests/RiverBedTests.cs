using NUnit.Framework;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// Rivers always block the crew, however shallow they run (Ronan, 2026-09-22); creeks, and
    /// everything else, go by depth.
    /// </summary>
    public class RiverBedTests
    {
        const int Size = 8;
        const float Ground = 5f;

        static TerrainGrid FlatGrid()
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateBasic(), 0.5f, 0f, 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, Ground) });
            return grid;
        }

        /// <summary>Water <paramref name="depth"/> deep over column 3 and column 5, dry elsewhere.</summary>
        static float[] Surfaces(float depth)
        {
            var surfaces = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    surfaces[z * Size + x] = x == 3 || x == 5 ? Ground + depth : float.NegativeInfinity;
            return surfaces;
        }

        /// <summary>Column 3 is river bed; column 5 is not (a creek, say).</summary>
        static bool[] RiverInColumnThree()
        {
            var beds = new bool[Size * Size];
            for (var z = 0; z < Size; z++)
                beds[z * Size + 3] = true;
            return beds;
        }

        [Test]
        public void AShallowRiverStillBlocksWhereShallowWaterElseDoesNot()
        {
            var grid = FlatGrid();
            grid.SetRiverBeds(RiverInColumnThree());
            grid.SetWaterSurfaces(Surfaces(0.1f));

            Assert.That(grid.IsWater(3, 4), Is.True, "10 cm of river is still river");
            Assert.That(grid.IsPassableGround(3, 4), Is.False);
            Assert.That(grid.IsWater(5, 4), Is.False, "10 cm anywhere else is waded");
            Assert.That(grid.IsPassableGround(5, 4), Is.True);
        }

        [Test]
        public void ADriedRiverBedIsGroundAgain()
        {
            var grid = FlatGrid();
            grid.SetRiverBeds(RiverInColumnThree());
            grid.SetWaterSurfaces(Surfaces(0.1f));
            Assert.That(grid.IsWater(3, 4), Is.True);

            var changed = 0;
            grid.WaterChanged += (x, z) => changed++;
            grid.SetWaterSurfaces(Surfaces(-1f));

            Assert.That(grid.IsWater(3, 4), Is.False, "no water, no river");
            Assert.That(grid.IsPassableGround(3, 4), Is.True);
            Assert.That(changed, Is.EqualTo(Size), "the column told whoever caches passability");
        }

        [Test]
        public void ClearingTheRiverBedsLetsShallowWaterBeWaded()
        {
            var grid = FlatGrid();
            grid.SetRiverBeds(RiverInColumnThree());
            grid.SetWaterSurfaces(Surfaces(0.1f));

            grid.SetRiverBeds(System.ReadOnlySpan<bool>.Empty);

            Assert.That(grid.IsWater(3, 4), Is.False);
            Assert.That(grid.IsRiverBed(3, 4), Is.False);
        }
    }
}
