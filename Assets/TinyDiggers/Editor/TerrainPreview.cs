using System.IO;
using System.Linq;
using System.Text;
using TinyDiggers.Presentation;
using PromptWaffle.Terrain;
using PromptWaffle.Terrain.Generation;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// A quick look at what the island generator makes, without entering play mode. For the
    /// open scene's TerrainView settings and a few seeds, it generates the island the way the
    /// scene does (the same grid, then settled by the slump simulator), measures it with the
    /// <see cref="TerrainScorecard"/>, and writes two top-down maps per seed:
    /// - shaded: sea by depth, land by height, lit from the north-west;
    /// - slope: flat green, gentle yellow, moderate orange, steep red, pits magenta.
    /// Each set of maps also goes into a contact sheet, and the scorecards into scorecard.txt.
    /// </summary>
    static class TerrainPreview
    {
        const string Folder = "Screenshots/Terrain/Preview";

        /// <summary>The scene's own seed first, then three more, so one look covers some variety.</summary>
        static readonly int[] ExtraSeeds = { 23, 37, 58 };

        const int Pixels = 900;

        [MenuItem("TinyDiggers/Terrain Preview")]
        static void Run() => Preview("preview");

        /// <summary>Generates, measures and draws; returns the scorecards' text. Usable from scripts.</summary>
        public static string Preview(string label)
        {
            var view = Object.FindAnyObjectByType<TerrainView>();
            if (view == null)
            {
                Debug.LogError("Terrain preview: no TerrainView in the open scene.");
                return "";
            }

            var so = new SerializedObject(view);
            var width = so.FindProperty("_width").intValue;
            var height = so.FindProperty("_height").intValue;
            var cellSize = so.FindProperty("_cellSize").floatValue;
            var settings = (TerrainGenSettings)so.FindProperty("_settings").objectReferenceValue;
            var step = view.HeightStep;
            Directory.CreateDirectory(Folder);

            var seeds = new int[ExtraSeeds.Length + 1];
            seeds[0] = settings.Seed;
            ExtraSeeds.CopyTo(seeds, 1);

            var report = new StringBuilder();
            var sheetShade = new Texture2D(Pixels * 2, Pixels * 2, TextureFormat.RGB24, false);
            var sheetSlope = new Texture2D(Pixels * 2, Pixels * 2, TextureFormat.RGB24, false);
            for (var i = 0; i < seeds.Length; i++)
            {
                var copy = Object.Instantiate(settings);
                copy.Seed = seeds[i];
                var started = Time.realtimeSinceStartupAsDouble;
                var grid = new TerrainGrid(width, height, MaterialTable.CreateDefault(), step, copy.Datum, cellSize);
                var slump = new AngleOfReposeSimulator(grid);
                var island = IslandGenerator.Generate(grid, copy);
                slump.RunUntilStable();
                slump.Dispose();
                var seconds = Time.realtimeSinceStartupAsDouble - started;

                var card = TerrainScorecard.Measure(grid);
                var line = $"{label} seed {seeds[i]} ({island.Shape}; {island.Mix}; cliff coast {island.CliffCoastShare:P0} at {island.CliffCoastHeight:0.0} m; crest {island.MountainCrest:0} m; erosion moved {island.ErosionMeanChange:0.00} m on average, {island.PitsFilled} cells filled; {Channels(island)}), {seconds:0.00} s: {card}";
                report.AppendLine(line);
                Debug.Log("Terrain preview: " + line);

                var (shade, slope) = Draw(grid, copy, island);
                File.WriteAllBytes(Path.Combine(Folder, $"{label}_seed{seeds[i]}_shade.png"), shade.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(Folder, $"{label}_seed{seeds[i]}_slope.png"), slope.EncodeToPNG());
                var ox = i % 2 * Pixels;
                var oy = (1 - i / 2) * Pixels;
                sheetShade.SetPixels(ox, oy, Pixels, Pixels, shade.GetPixels());
                sheetSlope.SetPixels(ox, oy, Pixels, Pixels, slope.GetPixels());
                Object.DestroyImmediate(shade);
                Object.DestroyImmediate(slope);
                Object.DestroyImmediate(copy);
            }

            File.WriteAllBytes(Path.Combine(Folder, $"{label}_sheet_shade.png"), sheetShade.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(Folder, $"{label}_sheet_slope.png"), sheetSlope.EncodeToPNG());
            Object.DestroyImmediate(sheetShade);
            Object.DestroyImmediate(sheetSlope);
            File.AppendAllText(Path.Combine(Folder, "scorecard.txt"), report.ToString());
            Debug.Log($"Terrain preview: done. Maps in {Path.GetFullPath(Folder)}");
            return report.ToString();
        }

        /// <summary>The island's frame, square, <see cref="Pixels"/> across, shaded and by slope.</summary>
        static (Texture2D shade, Texture2D slope) Draw(TerrainGrid grid, TerrainGenSettings settings, IslandMap island)
        {
            var half = (settings.LandRadius > 0f ? settings.LandRadius : grid.Width * grid.CellSize * 0.5f) + 20f;
            var centre = new Vector2(grid.Width, grid.Height) * 0.5f;
            var cellsAcross = 2f * half / grid.CellSize;
            var perPixel = cellsAcross / Pixels;
            var heights = new float[Pixels * Pixels];
            var ground = new bool[Pixels * Pixels];
            for (var py = 0; py < Pixels; py++)
                for (var px = 0; px < Pixels; px++)
                {
                    var x = Mathf.FloorToInt(centre.x - cellsAcross * 0.5f + (px + 0.5f) * perPixel);
                    var z = Mathf.FloorToInt(centre.y - cellsAcross * 0.5f + (py + 0.5f) * perPixel);
                    var i = py * Pixels + px;
                    ground[i] = grid.IsGround(x, z);
                    heights[i] = ground[i] ? grid.GetSurfaceHeight(x, z) : -20f;
                }

            var metresPerPixel = perPixel * grid.CellSize;
            var shade = new Texture2D(Pixels, Pixels, TextureFormat.RGB24, false);
            var slope = new Texture2D(Pixels, Pixels, TextureFormat.RGB24, false);
            var light = new Vector3(-1f, 1.4f, 1f).normalized;
            var shadeRow = new Color[Pixels * Pixels];
            var slopeRow = new Color[Pixels * Pixels];
            for (var py = 0; py < Pixels; py++)
                for (var px = 0; px < Pixels; px++)
                {
                    var i = py * Pixels + px;
                    float H(int ax, int ay) => heights[Mathf.Clamp(ay, 0, Pixels - 1) * Pixels + Mathf.Clamp(ax, 0, Pixels - 1)];
                    var h = heights[i];
                    var dx = (H(px + 1, py) - H(px - 1, py)) / (2f * metresPerPixel);
                    var dz = (H(px, py + 1) - H(px, py - 1)) / (2f * metresPerPixel);
                    var normal = new Vector3(-dx, 1f, -dz).normalized;
                    var lit = Mathf.Clamp01(0.35f + 0.75f * Vector3.Dot(normal, light));
                    var degrees = Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;

                    Color colour;
                    Color band;
                    if (!ground[i])
                    {
                        colour = band = new Color(0.1f, 0.1f, 0.12f);
                    }
                    else if (h < World.SeaLevel)
                    {
                        var deep = Mathf.Clamp01(-h / 15f);
                        colour = Color.Lerp(new Color(0.35f, 0.75f, 0.8f), new Color(0.05f, 0.18f, 0.4f), deep);
                        band = colour * 0.8f;
                    }
                    else
                    {
                        var t = Mathf.Clamp01(h / 35f);
                        var low = h < 1.5f ? new Color(0.9f, 0.84f, 0.62f) : new Color(0.45f, 0.68f, 0.3f);
                        colour = Color.Lerp(low, t < 0.5f ? new Color(0.62f, 0.6f, 0.36f) : new Color(0.75f, 0.73f, 0.7f), Mathf.Clamp01((t - 0.05f) * 1.6f)) * lit;
                        band = degrees < TerrainScorecard.FlatBelow ? new Color(0.25f, 0.7f, 0.3f)
                            : degrees < TerrainScorecard.GentleBelow ? new Color(0.9f, 0.85f, 0.3f)
                            : degrees <= TerrainScorecard.SteepAbove ? new Color(0.95f, 0.55f, 0.2f)
                            : new Color(0.85f, 0.15f, 0.12f);
                        band *= 0.55f + 0.45f * lit;
                    }

                    colour.a = band.a = 1f;
                    shadeRow[i] = colour;
                    slopeRow[i] = band;
                }

            // Pits, from the grid itself so none are lost to the downsampling.
            var x0 = Mathf.FloorToInt(centre.x - cellsAcross * 0.5f);
            var z0 = Mathf.FloorToInt(centre.y - cellsAcross * 0.5f);
            for (var z = Mathf.Max(1, z0); z < Mathf.Min(grid.Height - 1, z0 + (int)cellsAcross); z++)
                for (var x = Mathf.Max(1, x0); x < Mathf.Min(grid.Width - 1, x0 + (int)cellsAcross); x++)
                {
                    if (!grid.IsGround(x, z))
                        continue;
                    var h = grid.GetSurfaceHeight(x, z);
                    if (h < World.SeaLevel)
                        continue;
                    var pit = true;
                    for (var dz = -1; dz <= 1 && pit; dz++)
                        for (var dx = -1; dx <= 1 && pit; dx++)
                            if ((dx != 0 || dz != 0) && (!grid.IsGround(x + dx, z + dz) || grid.GetSurfaceHeight(x + dx, z + dz) <= h + 1e-3f))
                                pit = false;
                    if (!pit)
                        continue;
                    var px = Mathf.Clamp(Mathf.FloorToInt((x - x0) / perPixel), 0, Pixels - 1);
                    var py = Mathf.Clamp(Mathf.FloorToInt((z - z0) / perPixel), 0, Pixels - 1);
                    slopeRow[py * Pixels + px] = new Color(1f, 0f, 1f);
                }

            // Rivers dark blue and creeks light blue over the shaded map, each head a white dot, so
            // what the water will run down can be read without the water.
            foreach (var channel in island.Channels)
            {
                var colour = channel.Kind == ChannelKind.River ? new Color(0.05f, 0.2f, 0.75f) : new Color(0.3f, 0.75f, 1f);
                for (var p = 1; p < channel.Path.Count; p++)
                {
                    var a = channel.Path[p - 1];
                    var b = channel.Path[p];
                    var steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z)) / perPixel * 2f));
                    for (var k = 0; k <= steps; k++)
                    {
                        var point = Vector3.Lerp(a, b, k / (float)steps);
                        var px = Mathf.FloorToInt((point.x - x0) / perPixel);
                        var py = Mathf.FloorToInt((point.z - z0) / perPixel);
                        if (px >= 0 && py >= 0 && px < Pixels && py < Pixels)
                            shadeRow[py * Pixels + px] = colour;
                    }
                }

                var sx = Mathf.FloorToInt((channel.Spring.x - x0) / perPixel);
                var sy = Mathf.FloorToInt((channel.Spring.y - z0) / perPixel);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (sx + dx >= 0 && sy + dy >= 0 && sx + dx < Pixels && sy + dy < Pixels)
                            shadeRow[(sy + dy) * Pixels + sx + dx] = Color.white;
            }

            shade.SetPixels(shadeRow);
            shade.Apply();
            slope.SetPixels(slopeRow);
            slope.Apply();
            return (shade, slope);
        }

        /// <summary>"rivers 2 of 3 (410, 260 m), creeks 7 of 7 (3 join rivers, 40-180 m)".</summary>
        static string Channels(IslandMap island)
        {
            var rivers = island.Channels.FindAll(c => c.Kind == ChannelKind.River);
            var creeks = island.Channels.FindAll(c => c.Kind == ChannelKind.Creek);
            var riverLengths = string.Join(", ", rivers.ConvertAll(c => c.Length.ToString("0")));
            var creekRange = creeks.Count == 0 ? "none"
                : $"{creeks.Min(c => c.Length):0}-{creeks.Max(c => c.Length):0} m";
            var joins = creeks.Count(c => c.EndsInRiver);
            return $"rivers {rivers.Count} of {island.RiversWanted} ({riverLengths} m), creeks {creeks.Count} of {island.CreeksWanted} ({joins} join rivers, {creekRange})";
        }
    }
}
