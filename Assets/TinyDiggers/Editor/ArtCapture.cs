using System.Collections;
using System.Collections.Generic;
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
    /// The art pipeline's evaluation shots: every prop prefab in <c>Art/Props</c> placed on the
    /// island together, the way STYLE.md is judged, then the home view from the saved camera preset
    /// and an RTS-zoom look at the grove.
    ///
    /// Placement is seeded, so the same set lands in the same places every run and loops can be
    /// compared shot for shot. Props go on grass only: dry, not steep, not near the shore.
    /// Play mode only, Continent seed 11 (the island every slice has been judged on).
    /// </summary>
    static class ArtCapture
    {
        const string PropsFolder = "Assets/TinyDiggers/Art/Props";
        const string Folder = "Screenshots/Art";
        const int PerProp = 20;

        /// <summary>Name for this run's shots; set from a RunCommand before the menu item.</summary>
        public static string Tag = "run";

        [MenuItem("TinyDiggers/Art Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Art capture: enter play mode first.");
                return;
            }

            var host = new GameObject("ArtCapture") { hideFlags = HideFlags.DontSave };
            host.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            const int Width = 2560;
            const int Height = 1440;

            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var camera = FindAnyObjectByType<RtsCamera>();
                if (view == null || camera == null || view.Settings == null)
                {
                    Debug.LogError("Art capture: no island or camera in the scene.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                view.Settings.Shape = LandShape.Continent;
                view.Regenerate(11);
                yield return Frames(40);

                var prefabs = new List<GameObject>();
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PropsFolder }))
                    prefabs.Add(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)));
                if (prefabs.Count == 0)
                {
                    Debug.LogError("Art capture: no prefabs in " + PropsFolder);
                    Destroy(gameObject);
                    yield break;
                }

                var grid = view.Grid;
                var grove = FindGrove(grid, view);
                var root = new GameObject("ArtCaptureProps") { hideFlags = HideFlags.DontSave };
                var placed = Place(grid, grove, prefabs, root.transform);
                Debug.Log($"Art capture [{Tag}]: {placed} props from {prefabs.Count} prefab(s) round ({grove.x}, {grove.y}).");

                camera.GoHome();
                yield return Frames(60);
                Capture(camera, Tag + "_home", "home view from the saved preset");

                var pivot = new Vector3(grove.x, grid.GetSurfaceHeight(grove.x, grove.y), grove.y);
                yield return Pose(camera, pivot, 80f, 35f, 38f);
                Capture(camera, Tag + "_rts", "RTS zoom on the grove");

                yield return Pose(camera, pivot, 32f, 35f, 22f);
                Capture(camera, Tag + "_close", "close on the grove");

                Debug.Log($"Art capture [{Tag}]: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static bool Grass(TerrainGrid grid, int x, int z)
            {
                if (!grid.IsGround(x, z) || grid.IsWater(x, z))
                    return false;
                if (grid.GetTopMaterial(x, z) != MaterialTable.Topsoil)
                    return false;
                var h = grid.GetSurfaceHeight(x, z);
                if (h < World.SeaLevel + 2f)
                    return false;
                for (var dz = -2; dz <= 2; dz += 2)
                    for (var dx = -2; dx <= 2; dx += 2)
                        if (grid.IsGround(x + dx, z + dz) && Mathf.Abs(grid.GetSurfaceHeight(x + dx, z + dz) - h) > 1.5f)
                            return false;
                return true;
            }

            /// <summary>The grassiest 60 m patch among a seeded sample of candidates.</summary>
            static Vector2Int FindGrove(TerrainGrid grid, TerrainView view)
            {
                var random = new System.Random(5);
                var best = new Vector2Int(grid.Width / 2, grid.Height / 2);
                var bestScore = -1;
                for (var i = 0; i < 300; i++)
                {
                    var x = random.Next(40, grid.Width - 40);
                    var z = random.Next(40, grid.Height - 40);
                    if (!Grass(grid, x, z))
                        continue;
                    var score = 0;
                    for (var dz = -30; dz <= 30; dz += 6)
                        for (var dx = -30; dx <= 30; dx += 6)
                            if (Grass(grid, x + dx, z + dz))
                                score++;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Vector2Int(x, z);
                    }
                }

                return best;
            }

            static int Place(TerrainGrid grid, Vector2Int grove, List<GameObject> prefabs, Transform parent)
            {
                var random = new System.Random(9);
                var spots = new List<Vector2>();
                var placed = 0;
                foreach (var prefab in prefabs)
                {
                    var count = 0;
                    for (var attempt = 0; attempt < 2000 && count < PerProp; attempt++)
                    {
                        var angle = random.NextDouble() * Mathf.PI * 2;
                        var radius = Mathf.Sqrt((float)random.NextDouble()) * 38f;
                        var x = Mathf.RoundToInt(grove.x + Mathf.Cos((float)angle) * radius);
                        var z = Mathf.RoundToInt(grove.y + Mathf.Sin((float)angle) * radius);
                        if (!Grass(grid, x, z))
                            continue;
                        var spot = new Vector2(x, z);
                        var clear = true;
                        foreach (var other in spots)
                            if ((other - spot).sqrMagnitude < 5.5f * 5.5f)
                                clear = false;
                        if (!clear)
                            continue;

                        spots.Add(spot);
                        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                        instance.transform.position = new Vector3(x + 0.5f, grid.GetSurfaceHeight(x, z) - 0.1f, z + 0.5f);
                        instance.transform.rotation = Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
                        instance.transform.localScale = Vector3.one * (0.85f + 0.3f * (float)random.NextDouble());
                        count++;
                    }

                    placed += count;
                }

                return placed;
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
                Debug.Log($"Art capture: {name} — {what}");
            }
        }
    }
}
