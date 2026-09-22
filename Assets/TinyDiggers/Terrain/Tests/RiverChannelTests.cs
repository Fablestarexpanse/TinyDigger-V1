using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace TinyDiggers.Terrain.Tests
{
    /// <summary>
    /// Rivers and creeks (Ronan, 2026-09-21): one to three rivers and four to ten creeks per seed,
    /// cut as winding channels that run downhill to water. Rivers are wide and deep; creeks are
    /// narrow and shallow.
    /// </summary>
    public class RiverChannelTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Shape = LandShape.Continent;
            // The same small island LandShapeTests makes, and spacings to suit it: the game's
            // 150 m between river heads would not fit three on a 256 m map.
            _settings.RimWaterCells = 14;
            _settings.LandFeatureSize = 80f;
            _settings.RidgeWidth = 34f;
            _settings.PlateauRadius = 26f;
            _settings.ShallowCells = 6;
            _settings.ChannelCells = 5;
            _settings.RiverHeadSpacing = 50f;
            _settings.CreekHeadSpacing = 20f;
            _settings.MinRiverLength = 30f;
            _settings.MinCreekLength = 10f;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        (TerrainGrid grid, IslandMap map) Generate(int seed)
        {
            _settings.Seed = seed;
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
            return (grid, IslandGenerator.Generate(grid, _settings));
        }

        static List<Channel> Of(IslandMap map, ChannelKind kind) => map.Channels.FindAll(c => c.Kind == kind);

        [Test]
        public void TheCountsAreDrawnPerSeedWithinTheirRanges()
        {
            var drawn = new HashSet<(int, int)>();
            for (var seed = 1; seed <= 6; seed++)
            {
                var (_, map) = Generate(seed);
                Assert.That(map.RiversWanted, Is.InRange(_settings.RiverCountMin, _settings.RiverCountMax), $"seed {seed}");
                Assert.That(map.CreeksWanted, Is.InRange(_settings.CreekCountMin, _settings.CreekCountMax), $"seed {seed}");
                Assert.That(Of(map, ChannelKind.River).Count, Is.EqualTo(map.RiversWanted), $"seed {seed}: every river drawn was cut");
                Assert.That(Of(map, ChannelKind.Creek).Count, Is.GreaterThanOrEqualTo(map.CreeksWanted - 1),
                    $"seed {seed}: the creeks drawn were cut, all but one at most");
                drawn.Add((map.RiversWanted, map.CreeksWanted));
            }

            Assert.That(drawn.Count, Is.GreaterThan(1), "the counts change from seed to seed");
        }

        [Test]
        public void EveryChannelRunsDownhillAndEndsInWater()
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                var (grid, map) = Generate(seed);
                foreach (var channel in map.Channels)
                {
                    var path = channel.Path;
                    Assert.That(path.Count, Is.GreaterThan(2), $"seed {seed}: a {channel.Kind} with no length");
                    for (var i = 1; i < path.Count; i++)
                        Assert.That(path[i].y, Is.LessThanOrEqualTo(path[i - 1].y + 1e-4f), $"seed {seed}: a {channel.Kind} runs uphill");

                    var mouth = path[path.Count - 1];
                    if (channel.EndsInRiver)
                    {
                        Assert.That(channel.Kind, Is.EqualTo(ChannelKind.Creek), "only a creek ends in a river");
                        continue;
                    }

                    var x = Mathf.FloorToInt(mouth.x);
                    var z = Mathf.FloorToInt(mouth.z);
                    Assert.That(grid.GetSurfaceHeight(x, z), Is.LessThan(World.SeaLevel), $"seed {seed}: a {channel.Kind} stops short of the water");
                }
            }
        }

        [Test]
        public void RiversAreWiderAndDeeperThanCreeks()
        {
            var (_, map) = Generate(3);
            var rivers = Of(map, ChannelKind.River);
            var creeks = Of(map, ChannelKind.Creek);
            Assert.That(rivers, Is.Not.Empty);
            Assert.That(creeks, Is.Not.Empty);
            foreach (var river in rivers)
            {
                Assert.That(river.Width, Is.InRange(_settings.RiverWidthMin, _settings.RiverWidthMax + 1e-3f));
                Assert.That(river.Depth, Is.InRange(_settings.RiverDepthMin, _settings.RiverDepthMax));
                foreach (var creek in creeks)
                {
                    Assert.That(river.Width, Is.GreaterThan(creek.Width));
                    Assert.That(river.Depth, Is.GreaterThan(creek.Depth));
                }
            }
        }

        [Test]
        public void ARiverBedSitsBelowTheLandEitherSide()
        {
            var checkedAcross = 0;
            for (var seed = 1; seed <= 4; seed++)
            {
                var (grid, map) = Generate(seed);
                foreach (var river in Of(map, ChannelKind.River))
                {
                    var path = river.Path;
                    // The upper two thirds, clear of the mouth, where the land either side is dry.
                    for (var i = 2; i < path.Count * 2 / 3; i += 3)
                    {
                        var along = new Vector2(path[i + 1].x - path[i - 1].x, path[i + 1].z - path[i - 1].z).normalized;
                        var across = new Vector2(-along.y, along.x);
                        var bed = grid.GetSurfaceHeight(Mathf.FloorToInt(path[i].x), Mathf.FloorToInt(path[i].z));
                        // Just outside the bed, where the water would spill if there were no bank.
                        var reach = river.Width * 0.5f + 1.5f;
                        var left = Height(grid, new Vector2(path[i].x, path[i].z) + across * reach);
                        var right = Height(grid, new Vector2(path[i].x, path[i].z) - across * reach);
                        if (float.IsNaN(left) || float.IsNaN(right) || left < World.SeaLevel || right < World.SeaLevel)
                            continue;
                        // Where a creek comes in, the bank is open to it: that is a junction, not a leak.
                        if (NearAnotherChannel(map, river, i, reach + 4f))
                            continue;
                        Assert.That(bed, Is.LessThanOrEqualTo(Mathf.Min(left, right)), $"seed {seed} at point {i}: the bed stands above its banks");
                        checkedAcross++;
                    }
                }
            }

            Assert.That(checkedAcross, Is.GreaterThan(15), "enough of the rivers was checked to mean something");
        }

        [Test]
        public void ChannelBedsAreGravel()
        {
            // Ronan, 2026-09-22: grass under clear running water read as green water.
            var (grid, map) = Generate(3);
            // Where a channel drops through a rock step, the bed is that rock: a cliff stays stone.
            int points = 0, gravel = 0, stone = 0;
            foreach (var channel in map.Channels)
            {
                foreach (var point in channel.Path)
                {
                    var x = Mathf.FloorToInt(point.x);
                    var z = Mathf.FloorToInt(point.z);
                    if (grid.GetSurfaceHeight(x, z) < World.SeaLevel)
                        continue;
                    points++;
                    var top = grid.GetTopMaterial(x, z);
                    if (top == MaterialTable.RockLoose)
                        gravel++;
                    else if (IslandGenerator.IsStone(top))
                        stone++;
                }
            }

            Assert.That(points, Is.GreaterThan(50));
            Assert.That(gravel + stone, Is.EqualTo(points), $"{points - gravel - stone} of {points} bed points are neither gravel nor rock");
            Assert.That(gravel, Is.GreaterThanOrEqualTo(points * 3 / 4), $"{gravel} of {points} bed points are gravel");
            Assert.That(map.ChannelBeds, Is.Not.Null);
        }

        [Test]
        public void TheSameSeedCutsTheSameChannels()
        {
            var (_, a) = Generate(5);
            var (_, b) = Generate(5);
            Assert.That(b.Channels.Count, Is.EqualTo(a.Channels.Count));
            for (var i = 0; i < a.Channels.Count; i++)
            {
                Assert.That(b.Channels[i].Spring, Is.EqualTo(a.Channels[i].Spring));
                Assert.That(b.Channels[i].Path.Count, Is.EqualTo(a.Channels[i].Path.Count));
            }
        }

        /// <summary>
        /// Whether another channel, or another reach of this one well along it, passes close to
        /// its point <paramref name="index"/>: there the bank is open to that water, not leaking.
        /// </summary>
        static bool NearAnotherChannel(IslandMap map, Channel own, int index, float within)
        {
            var at = new Vector2(own.Path[index].x, own.Path[index].z);
            foreach (var channel in map.Channels)
            {
                for (var j = 0; j < channel.Path.Count; j++)
                {
                    // Path points are two cells apart: this reach's own neighbours, out to twice
                    // the distance checked, are the same stretch of river, not another.
                    if (channel == own && Mathf.Abs(j - index) < within)
                        continue;
                    var point = channel.Path[j];
                    if (Vector2.Distance(new Vector2(point.x, point.z), at) < within)
                        return true;
                }
            }

            return false;
        }

        static float Height(TerrainGrid grid, Vector2 at)
        {
            var x = Mathf.FloorToInt(at.x);
            var z = Mathf.FloorToInt(at.y);
            return grid.InBounds(x, z) && grid.IsGround(x, z) ? grid.GetSurfaceHeight(x, z) : float.NaN;
        }
    }
}
