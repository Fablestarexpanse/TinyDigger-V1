using NUnit.Framework;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>Grass grows on dry topsoil only, the same way every time, and goes when dug.</summary>
    public class GrassFieldTests
    {
        const int Size = 64;

        /// <summary>Topsoil land for x &lt; 40, then sand, then sea past x = 50.</summary>
        static TerrainGrid Meadow()
        {
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, -10f);
            for (var z = 0; z < Size; z++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (x < 40)
                        grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Rock, 12f), new Layer(MaterialTable.Topsoil, 1f) });
                    else if (x < 50)
                        grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Sand, 11f) });
                    else
                        grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Sand, 7f) });
                }
            }

            return grid;
        }

        static int CountInColumns(GrassField field, int fromX, int toX)
        {
            var count = 0;
            for (var cz = 0; cz < field.ChunksZ; cz++)
                for (var cx = 0; cx < field.ChunksX; cx++)
                    foreach (var tuft in field.Chunk(cx, cz))
                    {
                        var x = tuft.GetColumn(3).x;
                        if (x >= fromX && x < toX)
                            count++;
                    }
            return count;
        }

        [Test]
        public void GrassGrowsOnTopsoilOnlyNeverOnSandOrInTheSea()
        {
            var field = new GrassField(Meadow(), density: 1.5f, chunkSize: 16);

            Assert.That(CountInColumns(field, 0, 40), Is.GreaterThan(40 * Size), "a meadow should be full of grass");
            Assert.That(CountInColumns(field, 40, Size), Is.Zero, "no grass on sand or in water");
        }

        [Test]
        public void DensityIsRoughlyWhatWasAskedFor()
        {
            var field = new GrassField(Meadow(), density: 1.5f, chunkSize: 16);
            var perCell = field.TuftCount / (40f * Size);

            Assert.That(perCell, Is.EqualTo(1.5f).Within(0.1f));
        }

        [Test]
        public void TuftsStandOnTheGroundOfTheirCell()
        {
            var grid = Meadow();
            var field = new GrassField(grid, chunkSize: 16);
            foreach (var tuft in field.Chunk(0, 0))
            {
                var p = tuft.GetColumn(3);
                Assert.That(p.y, Is.EqualTo(grid.GetSurfaceHeight(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z))).Within(1e-4f));
            }
        }

        [Test]
        public void TheSameMapAlwaysGrowsTheSameMeadow()
        {
            var a = new GrassField(Meadow(), seed: 3, chunkSize: 16);
            var b = new GrassField(Meadow(), seed: 3, chunkSize: 16);

            Assert.That(b.Chunk(1, 1), Is.EqualTo(a.Chunk(1, 1)));
        }

        [Test]
        public void DiggingACellTakesItsGrassAndLeavesTheRestAlone()
        {
            var grid = Meadow();
            var field = new GrassField(grid, density: 3f, chunkSize: 16);
            var neighbour = new System.Collections.Generic.List<Matrix4x4>(field.Chunk(0, 1));

            grid.Remove(5, 5, 1f, new System.Collections.Generic.List<MaterialVolume>());
            field.OnCellChanged(5, 5);

            foreach (var tuft in field.Chunk(0, 0))
            {
                var p = tuft.GetColumn(3);
                Assert.That(Mathf.FloorToInt(p.x) == 5 && Mathf.FloorToInt(p.z) == 5, Is.False, "the dug cell still has grass");
            }

            Assert.That(field.Chunk(0, 1), Is.EqualTo(neighbour), "a chunk nobody touched changed");
        }
    }
}
