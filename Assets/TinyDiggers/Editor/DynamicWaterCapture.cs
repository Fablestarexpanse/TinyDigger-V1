using System.Collections;
using System.Collections.Generic;
using System.IO;
using PromptWaffle.DynamicWater;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// PromptWaffle Dynamic Water milestone 1 evidence, on the TinyDiggers disc:
    /// - the sea as the dynamic zone draws it;
    /// - a trench dug from the sea 20 m inland to a basin, below sea level, photographed as the sea
    ///   floods in;
    /// - the numbers: zone size, steps, frame time, and how deep the far basin got.
    /// Play mode only.
    /// </summary>
    static class DynamicWaterCapture
    {
        const string Folder = "Screenshots/DynamicWater";

        [MenuItem("TinyDiggers/Dynamic Water Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Dynamic water capture: enter play mode first.");
                return;
            }

            new GameObject("DynamicWaterCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var zone = FindAnyObjectByType<WaterZone>();
                var rts = FindAnyObjectByType<RtsCamera>();
                Directory.CreateDirectory(Folder);
                var until = Time.realtimeSinceStartup + 10f;
                while (zone.Simulation == null && Time.realtimeSinceStartup < until)
                    yield return null;
                if (zone.Simulation == null)
                {
                    Debug.LogError("Dynamic water capture: the zone never built its simulation.");
                    Destroy(gameObject);
                    yield break;
                }

                var grid = view.Grid;
                var desc = zone.Simulation.Desc;
                Debug.Log($"Dynamic water: zone {desc.Width}x{desc.Height} cells of {desc.CellSize} m, step {desc.FixedStep * 1000f:0.0} ms, {zone.Simulation.TotalVolumeImmediate():0} m³ of sea.");

                rts.enabled = false;
                var camera = rts.GetComponent<Camera>();
                var centre = view.transform.TransformPoint(new Vector3(view.DiscCentre.x, 0f, view.DiscCentre.y));
                yield return Frames(30);
                Shot(camera, "sea_wide", centre + new Vector3(0f, 420f, -470f), centre);

                // A coast facing open sea, with low land behind it.
                var site = FindCoast(grid, out var inland);
                var seaEnd = site;
                var basinCentre = site + Vector2Int.RoundToInt(inland * 44f);
                var removed = new List<MaterialVolume>();
                var trench = 0;
                for (var s = -6; s <= 44; s++)
                {
                    var along = site + inland * s;
                    for (var w = -2; w <= 2; w++)
                    {
                        var across = new Vector2(-inland.y, inland.x) * w;
                        var cell = Vector2Int.RoundToInt(along + across);
                        trench += Lower(grid, cell, -1.2f, removed) ? 1 : 0;
                    }
                }

                for (var dz = -10; dz <= 10; dz++)
                    for (var dx = -10; dx <= 10; dx++)
                        Lower(grid, basinCentre + new Vector2Int(dx, dz), -1.6f, removed);

                var siteWorld = view.transform.TransformPoint(TerrainSpace.CellCentre(grid, site.x, site.y));
                var basinWorld = view.transform.TransformPoint(TerrainSpace.CellCentre(grid, basinCentre.x, basinCentre.y));
                var middle = (siteWorld + basinWorld) * 0.5f;
                var side = new Vector3(-inland.y, 0f, inland.x);
                var pose = middle + side * 22f + Vector3.up * 26f - new Vector3(inland.x, 0f, inland.y) * 14f;
                Debug.Log($"Dynamic water: trench of {trench} cells from ({site.x}, {site.y}) inland to a basin at ({basinCentre.x}, {basinCentre.y}), floors at -1.2 m and -1.6 m.");

                var clock = Time.realtimeSinceStartup;
                var times = new[] { 0f, 2f, 5f, 12f };
                foreach (var t in times)
                {
                    while (Time.realtimeSinceStartup - clock < t)
                        yield return null;
                    zone.Simulation.Query.Update();
                    yield return Frames(4);
                    Shot(camera, $"flood_{t:00}s", pose, middle + Vector3.down * 1f);
                    var depths = zone.Simulation.ReadDepthsImmediate();
                    var zoneCell = new Vector2Int(Mathf.FloorToInt((basinWorld.x - desc.Origin.x) / desc.CellSize), Mathf.FloorToInt((basinWorld.z - desc.Origin.y) / desc.CellSize));
                    Debug.Log($"Dynamic water: t {t:0} s, basin centre depth {depths[zoneCell.y * desc.Width + zoneCell.x]:0.00} m.");
                }

                // Frame time with the zone stepping.
                var frames = 0;
                var start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < 2f)
                {
                    frames++;
                    yield return null;
                }

                Debug.Log($"Dynamic water: {1000f * (Time.realtimeSinceStartup - start) / frames:0.0} ms a frame with the zone running; {zone.Simulation.LastSteps} steps last frame, {zone.Simulation.DroppedTime:0.00} s dropped.");
                Shot(camera, "flood_wide", siteWorld + side * 70f + Vector3.up * 70f - new Vector3(inland.x, 0f, inland.y) * 30f, middle);
                rts.enabled = true;
                Debug.Log($"Dynamic water capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static bool Lower(TerrainGrid grid, Vector2Int cell, float floor, List<MaterialVolume> removed)
            {
                if (!grid.InBounds(cell.x, cell.y) || grid.IsVoid(cell.x, cell.y))
                    return false;
                var depth = grid.GetSurfaceHeight(cell.x, cell.y) - floor;
                if (depth <= 0.01f)
                    return false;
                grid.Remove(cell.x, cell.y, depth, removed, bulk: false);
                return true;
            }

            /// <summary>A sea cell on the shore with the land rising gently away from it for 30 m.</summary>
            static Vector2Int FindCoast(TerrainGrid grid, out Vector2 inland)
            {
                var best = new Vector2Int(grid.Width / 2, grid.Height / 2);
                inland = Vector2.right;
                var bestScore = float.MaxValue;
                var directions = new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down };
                for (var z = 60; z < grid.Height - 60; z += 7)
                {
                    for (var x = 60; x < grid.Width - 60; x += 7)
                    {
                        if (grid.IsVoid(x, z) || grid.GetSurfaceHeight(x, z) > -0.6f)
                            continue;
                        foreach (var d in directions)
                        {
                            var shore = new Vector2Int(x + (int)d.x * 6, z + (int)d.y * 6);
                            var far = new Vector2Int(x + (int)d.x * 50, z + (int)d.y * 50);
                            if (!grid.InBounds(far.x, far.y) || grid.IsVoid(far.x, far.y))
                                continue;
                            var hShore = grid.GetSurfaceHeight(shore.x, shore.y);
                            var hFar = grid.GetSurfaceHeight(far.x, far.y);
                            if (hShore < 0.3f || hFar < 0.5f || hFar > 3f)
                                continue;
                            // Low all the way: tall banks slump into a narrow trench and dam it.
                            var highest = 0f;
                            for (var k = 6; k <= 50; k += 2)
                                highest = Mathf.Max(highest, grid.GetSurfaceHeight(x + (int)d.x * k, z + (int)d.y * k));
                            if (highest > 3f)
                                continue;
                            var score = Mathf.Abs(hFar - 2f) + Mathf.Abs(hShore - 1f);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = new Vector2Int(x, z);
                                inland = d;
                            }
                        }
                    }
                }

                return best;
            }

            static IEnumerator Frames(int n)
            {
                for (var i = 0; i < n; i++)
                    yield return null;
            }

            static void Shot(Camera camera, string name, Vector3 position, Vector3 target)
            {
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
                var rt = new RenderTexture(1920, 1080, 24);
                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = null;
                RenderTexture.active = rt;
                var image = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                image.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(Folder, name + ".png"), image.EncodeToPNG());
                Destroy(image);
                rt.Release();
                Destroy(rt);
            }
        }
    }
}
