using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Units.Tests
{
    /// <summary>The worksites themselves (2026-09-24): a building and a work area, after Captain of Industry.</summary>
    public class WorksitesTests
    {
        [Test]
        public void AnAreaIsKeptWithinTheSizesAllowed()
        {
            var sites = new Worksites { MaxSide = 40 };
            var big = sites.Add(new Vector2Int(50, 50), new RectInt(0, 0, 400, 10));
            var tiny = sites.Add(new Vector2Int(5, 5), new RectInt(5, 5, 1, 1));

            Assert.That(big.Area.width, Is.EqualTo(40));
            Assert.That(tiny.Area.width, Is.EqualTo(Worksites.MinSide));
            Assert.That(big.Id, Is.Not.EqualTo(tiny.Id));
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
