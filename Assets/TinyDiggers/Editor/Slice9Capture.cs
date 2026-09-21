using System.Collections;
using System.IO;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// The Slice 9 evidence run for the water: the disc and coastline poses from 8e so they compare
    /// with the shots before it, then a low look along a beach twice, a second apart, so the waves
    /// can be seen moving, a look at the open sea, and the river. It also measures what the water
    /// costs a frame, by timing the same view with the water hidden and shown.
    ///
    /// Play mode only, and always the Continent seed.
    /// </summary>
    static class Slice9Capture
    {
        public const string Folder = "Screenshots/Slice9";

        [MenuItem("TinyDiggers/Slice 9 Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 9 capture: enter play mode first.");
                return;
            }

            var host = new GameObject("Slice9Capture") { hideFlags = HideFlags.DontSave };
            host.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            const int Width = 2560;
            const int Height = 1440;

            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var water = FindAnyObjectByType<WaterView>();
                var camera = FindAnyObjectByType<RtsCamera>();
                if (view == null || camera == null || water == null || view.Settings == null)
                {
                    Debug.LogError("Slice 9 capture: no island, water or camera in the scene.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                view.Settings.Shape = LandShape.Continent;
                view.Regenerate(11);
                yield return Frames(40);
                Debug.Log($"Slice 9: land {view.Island.Milliseconds:0} ms, water field {water.Field?.Milliseconds:0.0} ms, " +
                    $"{water.TriangleCount} water triangles.");

                camera.GoHome();
                yield return Frames(60);
                Capture(camera, "disc", "the whole disc");

                var grid = view.Grid;
                var shore = FindShore(grid, view);
                yield return Pose(camera, new Vector3(shore.x, World.SeaLevel, shore.y), 80f, 25f, 26f);
                Capture(camera, "coastline", "beach, shallows and deep water");

                yield return Pose(camera, new Vector3(shore.x, World.SeaLevel, shore.y), 38f, 25f, 16f);
                Capture(camera, "beach_a", "low along the beach");
                yield return Seconds(1f);
                Capture(camera, "beach_b", "the same, a second later");

                var centre = view.DiscCentre;
                var open = new Vector3(centre.x + view.DiscRadius * 0.82f, World.SeaLevel, centre.y);
                yield return Pose(camera, open, 70f, 250f, 20f);
                Capture(camera, "open_sea", "the swell out at sea");

                var island = view.Island;
                if (island != null && island.River.Count > 4)
                {
                    var point = island.River[island.River.Count / 2];
                    yield return Pose(camera, point, 110f, 40f, 55f);
                    Capture(camera, "river", "the river running");
                }

                yield return Pose(camera, new Vector3(shore.x, World.SeaLevel, shore.y), 80f, 25f, 26f);
                yield return Measure(water);

                Debug.Log($"Slice 9 capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            /// <summary>Frame time with the water hidden, then shown, from the coastline pose.</summary>
            IEnumerator Measure(WaterView water)
            {
                var sheets = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                var gpuWithout = 0.0;
                var cpuWithout = 0.0;
                var gpuWith = 0.0;
                var cpuWith = 0.0;

                for (var pass = 0; pass < 2; pass++)
                {
                    var show = pass == 1;
                    foreach (var r in sheets)
                        if (r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name == "TinyDiggers/Water")
                            r.enabled = show;
                    yield return Frames(30);

                    var gpu = 0.0;
                    var cpu = 0.0;
                    var samples = 0;
                    var timings = new FrameTiming[1];
                    for (var i = 0; i < 180; i++)
                    {
                        yield return null;
                        FrameTimingManager.CaptureFrameTimings();
                        if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                        {
                            gpu += timings[0].gpuFrameTime;
                            cpu += timings[0].cpuFrameTime;
                            samples++;
                        }
                    }

                    if (samples > 0)
                    {
                        gpu /= samples;
                        cpu /= samples;
                    }

                    if (show) { gpuWith = gpu; cpuWith = cpu; }
                    else { gpuWithout = gpu; cpuWithout = cpu; }
                }

                Debug.Log($"Slice 9: frame without water GPU {gpuWithout:0.00} ms CPU {cpuWithout:0.00} ms; " +
                    $"with water GPU {gpuWith:0.00} ms CPU {cpuWith:0.00} ms; water costs {gpuWith - gpuWithout:0.00} ms GPU.");
            }

            static Vector2Int FindShore(TerrainGrid grid, TerrainView view)
            {
                var centre = view.DiscCentre;
                for (var radius = view.DiscRadius - 30f; radius > 20f; radius -= 4f)
                {
                    for (var degrees = 0; degrees < 360; degrees += 7)
                    {
                        var radians = degrees * Mathf.Deg2Rad;
                        var x = Mathf.RoundToInt(centre.x + Mathf.Cos(radians) * radius);
                        var z = Mathf.RoundToInt(centre.y + Mathf.Sin(radians) * radius);
                        if (!grid.IsGround(x, z) || grid.IsWater(x, z))
                            continue;
                        for (var d = 1; d <= 4; d++)
                        {
                            var nx = Mathf.RoundToInt(x + Mathf.Cos(radians) * d);
                            var nz = Mathf.RoundToInt(z + Mathf.Sin(radians) * d);
                            if (grid.IsGround(nx, nz) && grid.IsWater(nx, nz))
                                return new Vector2Int(x, z);
                        }
                    }
                }

                return new Vector2Int(grid.Width / 2, grid.Height / 2);
            }

            IEnumerator Pose(RtsCamera camera, Vector3 pivot, float distance, float yaw, float pitch)
            {
                camera.Rig.Pivot = pivot;
                camera.Rig.Distance = distance;
                camera.Rig.Yaw = yaw;
                camera.Rig.SetPitch(pitch);
                yield return Frames(50);
            }

            static IEnumerator Frames(int count)
            {
                for (var i = 0; i < count; i++)
                    yield return null;
            }

            static IEnumerator Seconds(float seconds)
            {
                var until = Time.time + seconds;
                while (Time.time < until)
                    yield return null;
            }

            static void Capture(RtsCamera rig, string name, string what)
            {
                var camera = rig.GetComponent<Camera>();
                if (camera == null)
                    return;
                var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                var previous = camera.targetTexture;
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = previous;

                var active = RenderTexture.active;
                RenderTexture.active = target;
                var image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply();
                RenderTexture.active = active;

                File.WriteAllBytes(Path.Combine(Folder, name + ".png"), image.EncodeToPNG());
                Destroy(image);
                target.Release();
                Destroy(target);
                Debug.Log($"Slice 9 capture: {name} — {what}");
            }
        }
    }
}
