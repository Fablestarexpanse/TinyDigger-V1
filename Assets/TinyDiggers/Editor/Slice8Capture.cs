using System.Collections;
using System.IO;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// The Slice 8 evidence run: the whole island from the saved camera preset, a close-up of the
    /// coast, the mouth of the river, and a pit dug beside the shore so the strata show.
    ///
    /// Play mode only. It drives the camera rig directly rather than the player's input.
    /// </summary>
    static class Slice8Capture
    {
        public const string Folder = "Screenshots/Slice8";

        [MenuItem("TinyDiggers/Slice 8 Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 8 capture: enter play mode first.");
                return;
            }

            var host = new GameObject("Slice8Capture") { hideFlags = HideFlags.DontSave };
            host.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            const int Width = 2560;
            const int Height = 1440;

            IEnumerator Start()
            {
                var view = FindFirstObjectByType<TerrainView>();
                var camera = FindFirstObjectByType<RtsCamera>();
                if (view == null || camera == null || view.Island == null)
                {
                    Debug.LogError("Slice 8 capture: no island in the scene.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                var grid = view.Grid;
                var island = view.Island;

                // The whole island, from the camera the game starts with.
                camera.GoHome();
                yield return Settle(camera, 60);
                Capture(camera, "island", "the whole island from the saved preset");

                // The coast: find a shoreline cell, then stand off it.
                var shore = FindShore(grid, view);
                yield return Pose(camera, new Vector3(shore.x, World.SeaLevel, shore.y), 70f, 25f, 28f);
                Capture(camera, "coastline", "where the land meets the sea");

                // The river, at its mouth.
                var mouth = island.River[island.River.Count - 1];
                // Stand out at sea and look back up the channel.
                var inland = new Vector2(view.DiscCentre.x - mouth.x, view.DiscCentre.y - mouth.z);
                var yaw = Mathf.Atan2(inland.x, inland.y) * Mathf.Rad2Deg;
                yield return Pose(camera, new Vector3(mouth.x, World.SeaLevel, mouth.z), 70f, yaw, 22f);
                Capture(camera, "river-mouth", "the river reaching the sea");

                // A pit beside the shore, cut deep enough to show the strata.
                var pit = FindDiggable(grid, view, shore);
                CarvePit(grid, pit);
                for (var i = 0; i < 90; i++)
                    yield return null;
                yield return Pose(camera, new Vector3(pit.x, grid.GetSurfaceHeight(pit.x, pit.y), pit.y), 75f, 35f, 38f);
                Capture(camera, "strata", "a pit by the coast, cut through the layers");

                Debug.Log($"Slice 8 capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            /// <summary>A land cell with the sea a few cells away: somewhere worth photographing.</summary>
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
                        var wet = false;
                        for (var d = 1; d <= 4 && !wet; d++)
                        {
                            var nx = Mathf.RoundToInt(x + Mathf.Cos(radians) * d);
                            var nz = Mathf.RoundToInt(z + Mathf.Sin(radians) * d);
                            wet = grid.IsGround(nx, nz) && grid.IsWater(nx, nz);
                        }

                        if (wet)
                            return new Vector2Int(x, z);
                    }
                }

                return new Vector2Int(grid.Width / 2, grid.Height / 2);
            }

            /// <summary>A spot a little inland of the shore, high enough that a pit shows layers.</summary>
            static Vector2Int FindDiggable(TerrainGrid grid, TerrainView view, Vector2Int shore)
            {
                var centre = view.DiscCentre;
                var inward = (new Vector2(centre.x, centre.y) - shore).normalized;
                for (var d = 6; d < 60; d += 2)
                {
                    var x = Mathf.RoundToInt(shore.x + inward.x * d);
                    var z = Mathf.RoundToInt(shore.y + inward.y * d);
                    if (grid.IsGround(x, z) && !grid.IsWater(x, z) && grid.GetSurfaceHeight(x, z) > World.SeaLevel + 6f)
                        return new Vector2Int(x, z);
                }

                return shore;
            }

            static void CarvePit(TerrainGrid grid, Vector2Int at)
            {
                var spoil = new MaterialInventory(1_000_000f);
                for (var step = 0; step < 7; step++)
                {
                    var radius = 12 - step * 2;
                    if (radius < 1)
                        break;
                    for (var pass = 0; pass < 2; pass++)
                        Excavation.Dig(grid, spoil, at.x, at.y, radius, 1f);
                }
            }

            IEnumerator Pose(RtsCamera camera, Vector3 pivot, float distance, float yaw, float pitch)
            {
                camera.Rig.Pivot = pivot;
                camera.Rig.Distance = distance;
                camera.Rig.Yaw = yaw;
                camera.Rig.SetPitch(pitch);
                yield return Settle(camera, 50);
            }

            IEnumerator Settle(RtsCamera camera, int frames)
            {
                for (var i = 0; i < frames; i++)
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
                Debug.Log($"Slice 8 capture: {name} — {what}");
            }
        }
    }
}
