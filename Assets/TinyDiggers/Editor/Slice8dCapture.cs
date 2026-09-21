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
    /// The Slice 8d evidence run: the same three shots, from the same poses as 8c, so the material
    /// pass can be judged against the 8c set rather than against memory.
    ///
    /// Play mode only, and always the Continent seed, because that is what the pass was judged on.
    /// </summary>
    static class Slice8dCapture
    {
        public const string Folder = "Screenshots/Slice8d";

        [MenuItem("TinyDiggers/Slice 8d Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 8d capture: enter play mode first.");
                return;
            }

            var host = new GameObject("Slice8cCapture") { hideFlags = HideFlags.DontSave };
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
                if (view == null || camera == null || view.Settings == null)
                {
                    Debug.LogError("Slice 8d capture: no island in the scene.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                view.Settings.Shape = LandShape.Continent;
                view.Regenerate(11);
                yield return Frames(40);

                camera.GoHome();
                yield return Frames(60);
                Capture(camera, "disc", "the whole disc under the new light");

                var grid = view.Grid;
                var peak = view.Island.Peak;
                yield return Pose(camera, new Vector3(peak.x, grid.GetSurfaceHeight(peak.x, peak.y), peak.y), 180f, 40f, 28f);
                Capture(camera, "ridge", "the ridge and its valleys");

                var shore = FindShore(grid, view);
                yield return Pose(camera, new Vector3(shore.x, World.SeaLevel, shore.y), 80f, 25f, 26f);
                Capture(camera, "coastline", "beach, shallows and deep water");

                Debug.Log($"Slice 8d capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
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

            IEnumerator Frames(int count)
            {
                for (var i = 0; i < count; i++)
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
                Debug.Log($"Slice 8d capture: {name} — {what}");
            }
        }
    }
}
