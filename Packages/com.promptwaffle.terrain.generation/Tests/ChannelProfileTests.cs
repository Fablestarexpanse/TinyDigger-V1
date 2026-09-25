using PromptWaffle.Terrain;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>
    /// What the rivers and creeks look like, in numbers (Ronan, 2026-09-24: *"the main thing is
    /// getting realistic looking and varied streams and river channels"*). Nothing here is about
    /// whether the crew can cross; that is the player's to work out.
    ///
    /// For every channel on three seeds:
    /// - **width at the head and at the mouth**, counted across the carved bed, so a channel that
    ///   is the same width from spring to sea shows as a ratio of 1;
    /// - **sinuosity**, the length along the channel over the straight line from end to end;
    /// - **depth variation**, how much the bed's depth below its banks changes along the channel,
    ///   so pools and shallows show and a uniform gutter does not;
    /// - **bank asymmetry** at bends, the outside bank's rise over the inside bank's, so a cut
    ///   bank over a point bar shows and two identical banks read 1.
    /// </summary>
    public class ChannelProfileTests
    {
        const int Size = 256;

        TerrainGenSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<TerrainGenSettings>();
            _settings.Shape = LandShape.Continent;
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
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_settings);

        static Vector2 Flat(Vector3 point) => new Vector2(point.x, point.z);

        /// <summary>Direction along the path at point <paramref name="i"/>.</summary>
        static Vector2 Along(List<Vector3> path, int i)
        {
            var a = Flat(path[Mathf.Max(0, i - 1)]);
            var b = Flat(path[Mathf.Min(path.Count - 1, i + 1)]);
            var along = b - a;
            return along.sqrMagnitude > 1e-6f ? along.normalized : Vector2.right;
        }

        /// <summary>Cells of carved bed straight across the channel at point <paramref name="i"/>.</summary>
        static int WidthAcross(IslandMap map, List<Vector3> path, int i, int width)
        {
            if (map.ChannelBeds == null)
                return 0;
            var normal = Vector2.Perpendicular(Along(path, i));
            var at = Flat(path[i]);
            var seen = new HashSet<int>();
            for (var t = -12f; t <= 12f; t += 0.25f)
            {
                var p = at + normal * t;
                var x = Mathf.FloorToInt(p.x);
                var z = Mathf.FloorToInt(p.y);
                if (x < 0 || z < 0 || x >= width || z >= width)
                    continue;
                var cell = z * width + x;
                if (map.ChannelBeds[cell] != 0)
                    seen.Add(cell);
            }

            return seen.Count;
        }

        static float HeightAt(TerrainGrid grid, Vector2 at)
        {
            var x = Mathf.Clamp(Mathf.FloorToInt(at.x), 0, grid.Width - 1);
            var z = Mathf.Clamp(Mathf.FloorToInt(at.y), 0, grid.Height - 1);
            return grid.GetSurfaceHeight(x, z);
        }

        [Test]
        public void WhatTheChannelsLookLike()
        {
            var headWidths = new List<float>();
            var mouthWidths = new List<float>();
            var riverHead = new List<float>();
            var riverMouth = new List<float>();
            var sinuosity = new List<float>();
            var depthVariation = new List<float>();
            var asymmetry = new List<float>();
            var channels = 0;

            for (var seed = 1; seed <= 3; seed++)
            {
                _settings.Seed = seed;
                var grid = new TerrainGrid(Size, Size, MaterialTable.CreateDefault(), 1f, _settings.Datum);
                var map = IslandGenerator.Generate(grid, _settings);
                foreach (var channel in map.Channels)
                {
                    var path = channel.Path;
                    if (path.Count < 8)
                        continue;
                    channels++;

                    var headWidth = WidthAcross(map, path, Mathf.RoundToInt(path.Count * 0.15f), Size);
                    var mouthWidth = WidthAcross(map, path, Mathf.RoundToInt(path.Count * 0.8f), Size);
                    headWidths.Add(headWidth);
                    mouthWidths.Add(mouthWidth);
                    if (channel.Kind == ChannelKind.River)
                    {
                        riverHead.Add(headWidth);
                        riverMouth.Add(mouthWidth);
                    }

                    var along = 0f;
                    for (var i = 1; i < path.Count; i++)
                        along += Vector2.Distance(Flat(path[i - 1]), Flat(path[i]));
                    var straight = Vector2.Distance(Flat(path[0]), Flat(path[path.Count - 1]));
                    if (straight > 1f)
                        sinuosity.Add(along / straight);

                    // Depth below the lower bank, three bed-widths out either side.
                    var depths = new List<float>();
                    for (var i = 1; i < path.Count - 1; i++)
                    {
                        var normal = Vector2.Perpendicular(Along(path, i));
                        var at = Flat(path[i]);
                        // Just past the bank's foot, from the bed's own width here: further out,
                        // the hillside a channel runs across swamps its banks.
                        var here = channel.Widths.Count == path.Count ? channel.Widths[i] : channel.Width;
                        var reach = here / grid.CellSize * 0.5f + 1.5f;
                        var bed = HeightAt(grid, at);
                        var left = HeightAt(grid, at + normal * reach);
                        var right = HeightAt(grid, at - normal * reach);
                        depths.Add(Mathf.Min(left, right) - bed);

                        // Bends: outside bank over inside bank.
                        var before = Flat(path[i]) - Flat(path[i - 1]);
                        var after = Flat(path[i + 1]) - Flat(path[i]);
                        var turn = before.x * after.y - before.y * after.x;
                        if (before.sqrMagnitude < 1e-6f || after.sqrMagnitude < 1e-6f)
                            continue;
                        var bend = turn / (before.magnitude * after.magnitude);
                        if (Mathf.Abs(bend) < 0.25f)
                            continue;
                        // Turning left (bend > 0) puts the outside on the right.
                        var outside = bend > 0f ? right : left;
                        var inside = bend > 0f ? left : right;
                        var outsideRise = outside - bed;
                        var insideRise = inside - bed;
                        if (insideRise > 0.1f && outsideRise > 0f)
                            asymmetry.Add(outsideRise / insideRise);
                    }

                    if (depths.Count > 3)
                    {
                        var mean = 0f;
                        foreach (var d in depths)
                            mean += d;
                        mean /= depths.Count;
                        var variance = 0f;
                        foreach (var d in depths)
                            variance += (d - mean) * (d - mean);
                        variance /= depths.Count;
                        if (mean > 0.05f)
                            depthVariation.Add(Mathf.Sqrt(variance) / mean);
                    }
                }
            }

            Debug.Log($"channel profile — {channels} channels over three seeds\n"
                      + $"  width across the bed: head {Mean(headWidths):0.0} cells, mouth {Mean(mouthWidths):0.0} cells "
                      + $"(mouth over head {Mean(mouthWidths) / Mathf.Max(0.01f, Mean(headWidths)):0.00}; rivers alone "
                      + $"{Mean(riverHead):0.0} to {Mean(riverMouth):0.0}, {Mean(riverMouth) / Mathf.Max(0.01f, Mean(riverHead)):0.00})\n"
                      + $"  sinuosity {Mean(sinuosity):0.00} (1 is dead straight)\n"
                      + $"  depth variation along a channel {Mean(depthVariation):0.00} (standard deviation over mean)\n"
                      + $"  bank asymmetry at bends {Mean(asymmetry):0.00} over {asymmetry.Count} bends (outside rise over inside; 1 is symmetric)");

            Assert.That(channels, Is.GreaterThan(0), "there are channels to measure");
        }

        static float Mean(List<float> values)
        {
            if (values.Count == 0)
                return 0f;
            var sum = 0f;
            foreach (var value in values)
                sum += value;
            return sum / values.Count;
        }
    }
}
