using System.Collections.Generic;
using NUnit.Framework;
using TinyDiggers.Terrain;

namespace TinyDiggers.Units.Tests
{
    public class DesignationMapTests
    {
        TerrainGrid _grid;
        DesignationMap _map;

        [SetUp]
        public void SetUp()
        {
            _grid = new TerrainGrid(8, 8, MaterialTable.CreateDefault(), heightStep: 1f);
            for (var z = 0; z < 8; z++)
                for (var x = 0; x < 8; x++)
                    _grid.SetColumn(x, z, new[] { new Layer(MaterialTable.Bedrock, 1f), new Layer(MaterialTable.Dirt, 4f) });
            _map = new DesignationMap(_grid);
        }

        [TearDown]
        public void TearDown() => _map.Dispose();

        [Test]
        public void DesignatingRecordsKindAndTarget()
        {
            Assert.That(_map.Designate(2, 3, DesignationKind.Dig, 3f), Is.True);

            Assert.That(_map.GetKind(2, 3), Is.EqualTo(DesignationKind.Dig));
            Assert.That(_map.GetTarget(2, 3), Is.EqualTo(3f));
            Assert.That(_map.Count, Is.EqualTo(1));
        }

        [Test]
        public void ATargetAlreadyMetIsNotDesignated()
        {
            Assert.That(_map.Designate(2, 3, DesignationKind.Dig, 5f), Is.False, "surface is already at 5");
            Assert.That(_map.Designate(2, 3, DesignationKind.Fill, 4f), Is.False, "surface is above 4");
            Assert.That(_map.Count, Is.EqualTo(0));
        }

        [Test]
        public void ADesignationClearsItselfWhenMet()
        {
            _map.Designate(2, 3, DesignationKind.Fill, 6f);
            var changes = new List<(int, int)>();
            _map.Changed += (x, z) => changes.Add((x, z));

            _grid.Add(2, 3, MaterialTable.Dirt, 1f);
            // Met designations are dropped on the next Prune, so that slump has had its say first.
            _map.Prune();

            Assert.That(_map.GetKind(2, 3), Is.EqualTo(DesignationKind.None));
            Assert.That(_map.Count, Is.EqualTo(0));
            Assert.That(changes, Does.Contain((2, 3)));
        }

        [Test]
        public void ADesignationPersistsUntilMet()
        {
            _map.Designate(2, 3, DesignationKind.Dig, 3f);
            var removed = new List<MaterialVolume>();

            _grid.Remove(2, 3, 1f, removed);
            Assert.That(_map.GetKind(2, 3), Is.EqualTo(DesignationKind.Dig), "4 m is not yet 3 m");

            _grid.Remove(2, 3, 1f, removed);
            _map.Prune();
            Assert.That(_map.GetKind(2, 3), Is.EqualTo(DesignationKind.None));
        }

        [Test]
        public void ClearingKeepsTheActiveListConsistent()
        {
            _map.Designate(1, 1, DesignationKind.Dig, 3f);
            _map.Designate(2, 2, DesignationKind.Dig, 3f);
            _map.Designate(3, 3, DesignationKind.Fill, 7f);

            _map.Clear(1, 1);

            Assert.That(_map.Count, Is.EqualTo(2));
            Assert.That(_map.ActiveCells, Does.Contain(2 * 8 + 2));
            Assert.That(_map.ActiveCells, Does.Contain(3 * 8 + 3));
            _map.ClearAll();
            Assert.That(_map.Count, Is.EqualTo(0));
        }

        [Test]
        public void RedesignatingReplacesKindAndTarget()
        {
            _map.Designate(1, 1, DesignationKind.Dig, 3f);
            _map.Designate(1, 1, DesignationKind.Fill, 8f);

            Assert.That(_map.Count, Is.EqualTo(1));
            Assert.That(_map.GetKind(1, 1), Is.EqualTo(DesignationKind.Fill));
            Assert.That(_map.GetTarget(1, 1), Is.EqualTo(8f));
        }
    }
}
