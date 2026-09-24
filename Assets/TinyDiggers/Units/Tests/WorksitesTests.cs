using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>The worksites themselves (2026-09-24): a building and a work area, after Captain of Industry.</summary>
    public class WorksitesTests
    {
        [Test]
        public void AnAreaCanBeAnySizeButNotSmallerThanTheBuilding()
        {
            var sites = new Worksites();
            var big = sites.Add(new Vector2Int(50, 50), new RectInt(0, 0, 900, 10));
            var tiny = sites.Add(new Vector2Int(5, 5), new RectInt(5, 5, 1, 1));

            Assert.That(big.Area.width, Is.EqualTo(900), "no longest side: whatever the job needs");
            Assert.That(big.CellCount, Is.EqualTo(9000));
            Assert.That(tiny.Area.width, Is.EqualTo(Worksites.MinSide));
            Assert.That(big.Id, Is.Not.EqualTo(tiny.Id));
        }

        [Test]
        public void AnOutlineCanBeAnyShape()
        {
            // An L: the notch out of its top right is not part of the site.
            var sites = new Worksites();
            var site = sites.Add(new Vector2Int(2, 2), new[]
            {
                new Vector2(0f, 0f), new Vector2(20f, 0f), new Vector2(20f, 10f),
                new Vector2(10f, 10f), new Vector2(10f, 20f), new Vector2(0f, 20f),
            }, curved: false);

            Assert.That(site.Contains(15, 5), Is.True, "the foot of the L");
            Assert.That(site.Contains(5, 15), Is.True, "its upright");
            Assert.That(site.Contains(15, 15), Is.False, "the notch is outside");
            Assert.That(site.CellCount, Is.EqualTo(300));
            Assert.That(site.Area, Is.EqualTo(new RectInt(0, 0, 20, 20)));

            sites.PlaceBuilding(site.Id, new Vector2Int(16, 16));
            Assert.That(site.Contains(site.Building.x, site.Building.y), Is.True, "a building in the notch is put on the L");
            Assert.That(site.Contains(15, 15), Is.False, "and the area did not move");
        }

        [Test]
        public void ACurvedOutlineRoundsOutThroughItsNodes()
        {
            var sites = new Worksites();
            var corners = new[] { new Vector2(10f, 10f), new Vector2(30f, 10f), new Vector2(30f, 30f), new Vector2(10f, 30f) };
            var straight = sites.Add(new Vector2Int(20, 20), corners, curved: false);
            var curved = sites.Add(new Vector2Int(20, 20), corners, curved: true);

            // A spline through a square's corners bows out past its edges between them.
            Assert.That(straight.Contains(20, 30), Is.False);
            Assert.That(curved.Contains(20, 30), Is.True, "past the middle of the top edge");
            Assert.That(curved.Contains(20, 20), Is.True);
            Assert.That(curved.CellCount, Is.GreaterThan(straight.CellCount));
        }

        [Test]
        public void AnOutlineThatHoldsNothingIsRefused()
        {
            var sites = new Worksites();
            Assert.That(sites.Add(new Vector2Int(0, 0), new[] { new Vector2(0f, 0f), new Vector2(5f, 0f) }, false), Is.Null,
                "two nodes are a line, not an area");
            var site = sites.Add(new Vector2Int(0, 0), new RectInt(0, 0, 10, 10));
            Assert.That(sites.SetOutline(site.Id, new[] { new Vector2(0f, 0f), new Vector2(5f, 0f), new Vector2(10f, 0f) }, false), Is.False,
                "a flat outline holds no cell");
            Assert.That(site.CellCount, Is.EqualTo(100), "and the area it had is kept");
        }

        [Test]
        public void AnAreaDraggedBackwardsIsTheSameArea()
        {
            var sites = new Worksites();
            var site = sites.Add(new Vector2Int(10, 10), new RectInt(20, 20, -8, -6));

            Assert.That(site.Area, Is.EqualTo(new RectInt(12, 14, 8, 6)));
            Assert.That(sites.Contains(site.Id, 12, 14), Is.True);
            Assert.That(sites.Contains(site.Id, 20, 20), Is.False, "the far edge is exclusive");
        }

        [Test]
        public void MovingTheBuildingTakesTheAreaWithIt()
        {
            var sites = new Worksites();
            var site = sites.Add(new Vector2Int(10, 10), 10);
            var changed = 0;
            sites.Changed += _ => changed++;

            sites.Move(site.Id, new Vector2Int(30, 12));

            Assert.That(site.Area, Is.EqualTo(new RectInt(25, 7, 10, 10)));
            Assert.That(site.Contains(25, 7), Is.True, "the cells went with it");
            Assert.That(site.Contains(5, 5), Is.False);
            Assert.That(changed, Is.EqualTo(1));
        }

        [Test]
        public void WhereAreasOverlapTheSmallerOneIsTheOneUnderTheCursor()
        {
            var sites = new Worksites();
            var wide = sites.Add(new Vector2Int(20, 20), 40);
            var small = sites.Add(new Vector2Int(22, 22), 6);

            Assert.That(sites.At(22, 22), Is.SameAs(small));
            Assert.That(sites.At(5, 5), Is.SameAs(wide));
            Assert.That(sites.At(100, 100), Is.Null);
            Assert.That(sites.BuildingNear(new Vector2(22.5f, 23f), 1.5f), Is.SameAs(small));
        }
    }
}
