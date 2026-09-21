using System.Collections;
using System.IO;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Shots for picking the crew robot's size in the world. In play mode it stands a row of
    /// robots, smallest on the left, a few metres in front of the real crew, and shoots the row
    /// from close, mid and far game-camera poses. The real crew is left as it is, at the scene's
    /// <see cref="CrewView.bodyScale"/>, for comparison. Play mode only; the row is removed after.
    /// </summary>
    static class CrewScaleCapture
    {
        const string Folder = "Screenshots/Crew";
        const string PrefabPath = "Assets/TinyDiggers/Art/Props/Crew/crew_unit.prefab";

        /// <summary>The sizes tried: 1 is as modelled, a ball 0.57 m across.</summary>
        public static readonly float[] Scales = { 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };

        [MenuItem("TinyDiggers/Crew Scale Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Crew scale capture: enter play mode first.");
                return;
            }

            new GameObject("CrewScaleCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var crew = FindAnyObjectByType<CrewView>();
                var rts = FindAnyObjectByType<RtsCamera>();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                Directory.CreateDirectory(Folder);

                var until = Time.realtimeSinceStartup + 10f;
                while (crew.Units.Count == 0 && Time.realtimeSinceStartup < until)
                    yield return null;
                if (crew.Units.Count == 0 || prefab == null)
                {
                    Debug.LogError("Crew scale capture: no crew, or no crew prefab at " + PrefabPath);
                    Destroy(gameObject);
                    yield break;
                }

                var grid = view.Grid;
                var start = crew.Units[0].Cell;
                var terrain = view.transform;

                // A row along +X, 4 m in front (+Z) of the first unit, each robot a ball's width
                // plus 0.8 m from the last.
                var row = new GameObject("Crew Scale Row");
                var along = 0f;
                var line = new System.Text.StringBuilder();
                for (var i = 0; i < Scales.Length; i++)
                {
                    var scale = Scales[i];
                    var width = 0.57f * scale;
                    along += i == 0 ? 0f : width * 0.5f + 0.8f;
                    var cellX = Mathf.Clamp(start.x + Mathf.RoundToInt(along / grid.CellSize), 0, grid.Width - 1);
                    var cellZ = Mathf.Clamp(start.y + Mathf.RoundToInt(4f / grid.CellSize), 0, grid.Height - 1);
                    var ground = grid.GetSurfaceHeight(cellX, cellZ);
                    var robot = Instantiate(prefab, row.transform);
                    robot.name = $"Scale {scale:0.##}";
                    robot.transform.SetPositionAndRotation(
                        terrain.TransformPoint(TerrainSpace.CellCentre(grid, cellX, cellZ, ground)),
                        terrain.rotation * Quaternion.Euler(0f, 200f, 0f));
                    robot.transform.localScale = Vector3.one * scale;
                    line.Append($"{scale:0.##}x = {0.57f * scale:0.00} m ball, {0.85f * scale:0.00} m tall; ");
                    along += width * 0.5f;
                }

                var middleCellX = Mathf.Clamp(start.x + Mathf.RoundToInt(along * 0.5f / grid.CellSize), 0, grid.Width - 1);
                var middleCellZ = Mathf.Clamp(start.y + Mathf.RoundToInt(2f / grid.CellSize), 0, grid.Height - 1);
                var middle = terrain.TransformPoint(TerrainSpace.CellCentre(grid, middleCellX, middleCellZ,
                    grid.GetSurfaceHeight(middleCellX, middleCellZ) + 0.5f));

                rts.enabled = false;
                var camera = rts.GetComponent<Camera>();
                var wait = Time.time + 1.5f;
                while (Time.time < wait)
                    yield return null;

                Shot(camera, "crew_scale_close", middle, 12f, 35f);
                Shot(camera, "crew_scale_mid", middle, 30f, 45f);
                Shot(camera, "crew_scale_far", middle, 70f, 55f);

                Destroy(row);
                rts.enabled = true;
                Debug.Log($"Crew scale capture: left to right {line}the real crew behind at bodyScale {crew.bodyScale:0.##}. " +
                          $"Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            /// <summary>From the south-west, <paramref name="distance"/> metres out, looking down at <paramref name="pitch"/> degrees.</summary>
            static void Shot(Camera camera, string name, Vector3 target, float distance, float pitch)
            {
                var direction = Quaternion.Euler(pitch, 30f, 0f) * Vector3.forward;
                var position = target - direction * distance;
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
