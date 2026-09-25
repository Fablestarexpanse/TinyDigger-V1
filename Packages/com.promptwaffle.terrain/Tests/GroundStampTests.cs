using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Tests
{
    /// <summary>
    /// God-mode stamps (Ronan, 2026-09-24): the stamp is the ground at once. It is built of the
    /// ground's own body under its topsoil, stops at bedrock, and can be taken back exactly.
    /// </summary>
    public class GroundStampTests
    {
        const int Size = 80;
        TerrainGrid _grid;
        HeightStamp _mound;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(Size, Size, MaterialTable.CreateBasic(), heightStep: 0.25f, cellSize: 0.5f);
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    _grid.SetColumn(x, z, new[]
                    {
                        new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Dirt, 3f), new Layer(MaterialTable.Topsoil, 0.5f),
                    });
            _mound = ScriptableObject.CreateInstance<HeightStamp>();
            _mound.EdgeFalloff = 0.3f;
            _mound.SetHeights(2, new[] { 1f, 1f, 1f, 1f });
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_mound);

        StampPlacement At(float height, bool invert = false) => new StampPlacement
        {
            Centre = new Vector2(40.5f, 40.5f), Size = 16f, Height = height, Invert = invert,
        };

        [Test]
        public void ARaisedStampIsSolidGroundUnderTheSameTopsoil()
        {
            var edit = GroundStamp.Apply(_grid, _mound, At(4f));

            Assert.That(_grid.GetSurfaceHeight(40, 40), Is.EqualTo(5.5f + 4f).Within(1e-3f), "four metres up in the middle");
            Assert.That(_grid.GetTopMaterial(40, 40), Is.EqualTo(MaterialTable.Topsoil), "grassed like its neighbours");
            Assert.That(_grid.GetLayer(40, 40, 1).Material, Is.EqualTo(MaterialTable.Dirt));
            Assert.That(_grid.GetLayer(40, 40, 1).Thickness, Is.EqualTo(7f).Within(1e-3f), "the dirt body is what grew, not loose spoil");
            Assert.That(edit.Cells, Is.GreaterThan(0));
            Assert.That(edit.Raised, Is.GreaterThan(0f));
            Assert.That(edit.Lowered, Is.EqualTo(0f));
        }

        [Test]
        public void AHillOverARiverBedIsGrassedLikeTheLandRoundIt()
        {
            // A sand bed three cells wide running right through where the hill goes.
            for (var z = 0; z < Size; z++)
                for (var x = 39; x <= 41; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 2f), new Layer(MaterialTable.Sand, 3f) });

            GroundStamp.Apply(_grid, _mound, At(4f));

            Assert.That(_grid.GetTopMaterial(40, 40), Is.EqualTo(MaterialTable.Topsoil), "grassed, not a sand stripe over the hill");
            Assert.That(_grid.GetSurfaceHeight(40, 40), Is.EqualTo(5f + 4f).Within(1e-3f), "and just as high");
            Assert.That(_grid.GetTopMaterial(40, 5), Is.EqualTo(MaterialTable.Sand), "the bed away from the hill is left alone");
        }

        [Test]
        public void AHollowStopsAtBedrock()
        {
            GroundStamp.Apply(_grid, _mound, At(10f, invert: true));

            // 3.5 m of dirt and topsoil over bedrock: that is as deep as it goes.
            Assert.That(_grid.GetSurfaceHeight(40, 40), Is.EqualTo(2f).Within(1e-3f));
            Assert.That(_grid.GetTopMaterial(40, 40), Is.EqualTo(MaterialTable.Bedrock));
        }

        [Test]
        public void UndoPutsEveryColumnBackExactly()
        {
            var before = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    before[z * Size + x] = _grid.GetSurfaceHeight(x, z);

            GroundStamp.Apply(_grid, _mound, At(4f)).Undo(_grid);

            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    Assert.That(_grid.GetSurfaceHeight(x, z), Is.EqualTo(before[z * Size + x]), $"({x}, {z})");
            Assert.That(_grid.GetLayerCount(40, 40), Is.EqualTo(3));
            Assert.That(_grid.GetTopMaterial(40, 40), Is.EqualTo(MaterialTable.Topsoil));
        }
    }
}
