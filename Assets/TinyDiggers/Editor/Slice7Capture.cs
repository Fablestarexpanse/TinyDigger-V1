using System.Collections;
using System.Diagnostics;
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
    /// The Slice 7 evidence run: carves the Slice 0 pit, parks the camera at three poses, measures
    /// frame time and draw cost at each, and writes a 2560x1440 shot of each.
    ///
    /// Play mode only, and it drives the camera rig directly rather than the player's input, so the
    /// numbers are of a scene standing still rather than of a scene being panned.
    /// </summary>
    static class Slice7Capture
    {
        public const string Folder = "Screenshots/Slice7";

        [MenuItem("TinyDiggers/Slice 7 Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 7 capture: enter play mode first.");
                return;
            }

            var host = new GameObject("Slice7Capture") { hideFlags = HideFlags.DontSave };
            host.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            const int Width = 2560;
            const int Height = 1440;
            const int SettleFrames = 90;
            const int MeasureFrames = 150;

            IEnumerator Start()
            {
                var view = FindFirstObjectByType<TerrainView>();
                var camera = FindFirstObjectByType<RtsCamera>();
                if (view == null || camera == null)
                {
                    Debug.LogError("Slice 7 capture: no TerrainView or RtsCamera in the scene.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                var grid = view.Grid;
                var centreX = grid.Width / 2;
                var centreZ = grid.Height / 2;
                CarvePit(grid, centreX, centreZ);

                // Let the slump settle and the chunks rebuild before anything is measured.
                for (var i = 0; i < 120; i++)
                    yield return null;

                var pitTop = grid.GetSurfaceHeight(centreX, centreZ);
                var pivot = new Vector3(centreX, pitTop, centreZ);

                yield return Pose(camera, pivot, distance: 70f, yaw: 35f, pitchOffset: 0f, "pit", "the Slice 0 pit");

                // Close on the cut, looking down at it rather than along it: at a grazing angle a
                // surface's texture footprint per pixel is enormous, which is all mip and no grain.
                var wall = new Vector3(centreX + 11f, grid.GetSurfaceHeight(centreX + 11, centreZ), centreZ);
                yield return Pose(camera, wall, distance: 16f, yaw: 200f, pitchOffset: 15f, "cut-face", "a cut face up close");

                yield return Pose(camera, new Vector3(grid.Width * 0.5f, view.RimHeight, grid.Height * 0.5f),
                    distance: 620f, yaw: 30f, pitchOffset: 0f, "disc", "the whole disc");

                Debug.Log($"Slice 7 capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            /// <summary>The Slice 0 pit: a stepped bowl deep enough to cut through topsoil into rock.</summary>
            static void CarvePit(TerrainGrid grid, int centreX, int centreZ)
            {
                var spoil = new MaterialInventory(1_000_000f);
                for (var step = 0; step < 6; step++)
                {
                    var radius = 14 - step * 2;
                    for (var pass = 0; pass < 3; pass++)
                        Excavation.Dig(grid, spoil, centreX, centreZ, radius, 1f);
                }
            }

            IEnumerator Pose(RtsCamera camera, Vector3 pivot, float distance, float yaw, float pitchOffset, string name, string what)
            {
                camera.Rig.Pivot = pivot;
                camera.Rig.Distance = distance;
                camera.Rig.Yaw = yaw;
                camera.Rig.PitchOffset = pitchOffset;
                for (var i = 0; i < SettleFrames; i++)
                    yield return null;

                // Frame time of a still scene, measured off the wall clock rather than a profiler
                // sample, so it is the whole frame and not one marker inside it.
                var stopwatch = Stopwatch.StartNew();
                var worst = 0.0;
                for (var i = 0; i < MeasureFrames; i++)
                {
                    var before = stopwatch.Elapsed.TotalMilliseconds;
                    yield return null;
                    var frame = stopwatch.Elapsed.TotalMilliseconds - before;
                    if (frame > worst)
                        worst = frame;
                }

                var average = stopwatch.Elapsed.TotalMilliseconds / MeasureFrames;
                Debug.Log(
                    $"Slice 7 perf [{name}] {what}: {average:0.00} ms/frame average, {worst:0.00} ms worst, " +
                    $"{UnityStats.drawCalls} draw calls, " +
                    $"{UnityStats.triangles:N0} triangles, {UnityStats.setPassCalls} set-pass calls at {Width}x{Height}.");

                Capture(camera.GetComponent<Camera>(), Path.Combine(Folder, name + ".png"));
                yield return null;
            }

            static void Capture(Camera camera, string path)
            {
                if (camera == null)
                    return;
                var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
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

                File.WriteAllBytes(path, image.EncodeToPNG());
                Destroy(image);
                target.Release();
                Destroy(target);
            }
        }
    }
}
