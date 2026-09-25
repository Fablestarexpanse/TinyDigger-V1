using System.Collections.Generic;
using NUnit.Framework;
using PromptWaffle.Terrain;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Presentation.Tests
{
    /// <summary>
    /// Rivers and creeks, step 3 (Ronan, 2026-09-21: "Rivers block, creeks wade"). A generated
    /// island's channels are filled the way the springs fill them when the water starts, and the
    /// crew's pathfinder is asked to get from one bank to the other.
    /// </summary>
    public class ChannelCrossingTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Shape = LandShape.Continent;
            // The small island the terrain tests use, with spacings to fit it.
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

        /// <summary>The island with its channels filled as <see cref="ChannelSprings"/> fills them, and the sea at sea level.</summary>
        (TerrainGrid grid, IslandMap map) Filled(int seed, float riverFill = ChannelSprings.DefaultRiverFill)
        {
            _settings.Seed = seed;
            var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
            var map = IslandGenerator.Generate(grid, _settings);
            var depths = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                    if (grid.IsGround(x, z))
                        depths[z * Size + x] = Mathf.Max(0f, World.SeaLevel - grid.GetSurfaceHeight(x, z));
            ChannelSprings.Prefill(depths, Size, Size, grid.CellSize, map.Channels,
                riverFill, ChannelSprings.DefaultCreekFill);

            var surfaces = new float[Size * Size];
            for (var z = 0; z < Size; z++)
                for (var x = 0; x < Size; x++)
                {
                    var cell = z * Size + x;
                    surfaces[cell] = depths[cell] > 0f && grid.IsGround(x, z)
                        ? grid.GetSurfaceHeight(x, z) + depths[cell]
                        : float.NegativeInfinity;
                }

            grid.SetWaterSurfaces(surfaces);
            return (grid, map);
        }

        /// <summary>
        /// Crossing points along the channel: a point on its bed and dry ground either side, well
        /// clear of the bed, the sea, and any other channel.
        /// </summary>
        static IEnumerable<(Vector2Int bed, Vector2Int left, Vector2Int right)> Crossings(TerrainGrid grid, IslandMap map, Channel channel)
        {
            var path = channel.Path;
            var reach = channel.Width * 0.5f / grid.CellSize + 3f;
            for (var i = 3; i < path.Count * 3 / 4; i += 4)
            {
                var along = new Vector2(path[i + 1].x - path[i - 1].x, path[i + 1].z - path[i - 1].z).normalized;
                var across = new Vector2(-along.y, along.x);
                var at = new Vector2(path[i].x, path[i].z);
                var bed = Vector2Int.FloorToInt(at);
                var left = Vector2Int.FloorToInt(at + across * reach);
                var right = Vector2Int.FloorToInt(at - across * reach);
                if (!Dry(grid, left) || !Dry(grid, right) || NearOtherChannel(map, channel, at, reach + 6f))
                    continue;
                yield return (bed, left, right);
            }
        }

        static bool Dry(TerrainGrid grid, Vector2Int cell) =>
            grid.InBounds(cell.x, cell.y) && grid.IsGround(cell.x, cell.y) && grid.WaterDepth(cell.x, cell.y) <= 0f
            && grid.GetSurfaceHeight(cell.x, cell.y) >= World.SeaLevel + 1f;

        static bool NearOtherChannel(IslandMap map, Channel own, Vector2 at, float within)
        {
            foreach (var channel in map.Channels)
            {
                if (channel == own)
                    continue;
                foreach (var point in channel.Path)
                    if (Vector2.Distance(new Vector2(point.x, point.z), at) < within)
                        return true;
            }

            return false;
        }

        [Test]
        public void ARiversBedIsDeepWater([Values(ChannelSprings.DefaultRiverFill, 0.1f)] float riverFill)
        {
            // What a river physically is, not whether the crew can get round it. This used to
            // assert that the crew could not walk across either (Ronan, 2026-09-22: "rivers always
            // block"); on walkable slopes the crew went round a river's spring, and Ronan ruled
            // that crossings are the player's to work out (2026-09-24). At 0.1 of its bed's depth
            // a river still runs deep water.
            var probes = 0;
            for (var seed = 1; seed <= 3; seed++)
            {
                var (grid, map) = Filled(seed, riverFill);
                foreach (var river in map.Channels.FindAll(c => c.Kind == ChannelKind.River))
                {
                    foreach (var (bed, _, _) in Crossings(grid, map, river))
                    {
                        Assert.That(grid.IsWater(bed.x, bed.y), Is.True, $"seed {seed}: the river's bed at {bed} is deep water");
                        probes++;
                    }
                }
            }

            Assert.That(probes, Is.GreaterThan(5), "enough of the river was looked at to mean something");
        }

        [Test]
        public void TheCrewWadesStraightAcrossACreek()
        {
            var probes = 0;
            var waded = 0;
            for (var seed = 1; seed <= 3; seed++)
            {
                var (grid, map) = Filled(seed);
                var pathfinder = new GridPathfinder(grid);
                var path = new List<Vector2Int>();
                foreach (var creek in map.Channels.FindAll(c => c.Kind == ChannelKind.Creek))
                {
                    foreach (var (bed, left, right) in Crossings(grid, map, creek))
                    {
                        Assert.That(grid.IsWater(bed.x, bed.y), Is.False, $"seed {seed}: the creek's bed at {bed} is wadeable");
                        Assert.That(grid.WaterDepth(bed.x, bed.y), Is.GreaterThan(0f), $"seed {seed}: and there is water in it");
                        probes++;
                        var direct = Vector2Int.Distance(left, right);
                        if (pathfinder.TryFindPath(left.x, left.y, right.x, right.y, path) && path.Count <= direct * 2f + 4f)
                            waded++;
                    }
                }
            }

            Assert.That(probes, Is.GreaterThan(10), "enough crossings were tried to mean something");
            // How many the crew waded is reported, not asserted: crossings are the player's to
            // work out (Ronan, 2026-09-24). What is asserted above is what a creek is — wet,
            // and shallow enough to wade.
            Debug.Log($"creek crossings: the crew waded straight across {waded} of {probes}");
        }
    }
}
