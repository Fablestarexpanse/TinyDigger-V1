using System.Collections;
using System.Collections.Generic;
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
    /// The Slice 11 (half-metre cells) evidence run on Continent seed 11:
    /// - the disc, and a close look at a ridge, for the finer edges and terraces;
    /// - the ore view (F7), to show it still lines up with the land;
    /// - the crew digging a 6 m square pit 2 m deep, for half-metre terraces and ramps;
    /// - the numbers: generation, one dig to its mesh update, frame time and triangles.
    /// Play mode only.
    /// </summary>
    static class Slice11Capture
    {
        const string Folder = "Screenshots/Slice11";

        [MenuItem("TinyDiggers/Slice 11 Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 11 capture: enter play mode first.");
                return;
            }

            new GameObject("Slice11Capture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            const int Width = 2560;
            const int Height = 1440;

            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var camera = FindAnyObjectByType<RtsCamera>();
                var ore = FindAnyObjectByType<OreOverlayView>();
                var crew = FindAnyObjectByType<CrewView>();
                if (view == null || camera == null)
                {
                    Debug.LogError("Slice 11 capture: needs a TerrainView and an RtsCamera.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                view.Settings.Shape = LandShape.Continent;
                var regenerate = Stopwatch.StartNew();
                view.Regenerate(11);
                var regenerateMs = regenerate.Elapsed.TotalMilliseconds;
                yield return null;
                var firstFrameMs = Time.unscaledDeltaTime * 1000f;
                yield return Frames(40);
                var grid = view.Grid;
                Debug.Log($"Slice 11: regenerate in play took {regenerateMs:0} ms, the frame after it {firstFrameMs:0} ms.");
                Debug.Log($"Slice 11: {grid.Width}x{grid.Height} cells of {grid.CellSize} m, step {grid.HeightStep} m. " +
                    $"Land generated in {view.Island.Milliseconds:0} ms ({view.Island.StageSummary()}).");

                if (ore != null)
                    ore.Visible = false;
                camera.GoHome();
                yield return Settle();
                yield return FrameTime("disc");
                Capture(camera, "disc", "the disc");

                var peak = view.Island.Peak;
                yield return Pose(camera, TerrainSpace.OnSurface(grid, peak.x, peak.y), 70f, 30f, 38f);
                Capture(camera, "ridge_close", "the ridge close up");

                if (ore != null)
                {
                    camera.enabled = true;
                    camera.GoHome();
                    ore.Visible = true;
                    yield return Settle();
                    Capture(camera, "disc_ore", "the disc, ore view on (F7)");
                    ore.Visible = false;
                }

                // One dig by hand, timed to the mesh it changes, after a few quiet frames so the
                // last screenshot's encode is not in the frame measured.
                yield return Frames(10);
                var at = new Vector2Int(grid.Width / 2 + 40, grid.Height / 2 + 40);
                var clock = Stopwatch.StartNew();
                var removed = new List<MaterialVolume>();
                for (var dz = 0; dz < 4; dz++)
                    for (var dx = 0; dx < 4; dx++)
                        if (grid.IsGround(at.x + dx, at.y + dz))
                            grid.Remove(at.x + dx, at.y + dz, grid.HeightStep, removed);
                var removedMs = clock.Elapsed.TotalMilliseconds;
                yield return null;
                Debug.Log($"Slice 11: a 4 x 4 cell dig took {removedMs:0.00} ms to apply; next frame rebuilt {view.ChunksRebuiltLastFrame} chunks in a frame of {Time.unscaledDeltaTime * 1000f:0.0} ms.");

                if (crew != null && crew.Dispatcher != null && crew.Units.Count > 0)
                {
                    var home = crew.Units[0].Cell;
                    var pit = FindFlatGround(grid, home, 12);
                    var target = grid.GetSurfaceHeight(pit.x, pit.y) - 2f;
                    var map = crew.Dispatcher.Designations;
                    for (var dz = 0; dz < 12; dz++)
                        for (var dx = 0; dx < 12; dx++)
                            map.Designate(pit.x + dx, pit.y + dz, DesignationKind.Dig, target);
                    var dump = pit + new Vector2Int(20, 0);
                    for (var dz = 0; dz < 12; dz++)
                        for (var dx = 0; dx < 12; dx++)
                            map.SetDumpZone(dump.x + dx, dump.y + dz, true, grid.GetSurfaceHeight(dump.x, dump.y) + 2f);

                    Time.timeScale = 16f;
                    var until = Time.realtimeSinceStartup + 120f;
                    while (Time.realtimeSinceStartup < until && map.Count > 0)
                        yield return null;
                    Time.timeScale = 1f;
                    var ledger = crew.Dispatcher.Ledger;
                    Debug.Log($"Slice 11: crew pit at ({pit.x}, {pit.y}), 12 x 12 cells ({12 * grid.CellSize} m square) down to {target:0.0} m; " +
                        $"designations left {map.Count}, dug {ledger.Dug(MaterialTable.Topsoil) + ledger.Dug(MaterialTable.Dirt) + ledger.Dug(MaterialTable.Clay) + ledger.Dug(MaterialTable.Rock):0.0} m³ of topsoil, dirt, clay and rock.");
                    var centre = TerrainSpace.OnSurface(grid, pit.x + 6, pit.y + 6);
                    yield return Pose(camera, centre, 30f, 35f, 68f);
                    Capture(camera, "crew_pit", "the crew's pit: half-metre terraces");
                    yield return Pose(camera, centre + new Vector3(5f, 0f, 0f), 38f, 200f, 62f);
                    Capture(camera, "crew_pit_heap", "the pit and its spoil heap from the other side");
                }

                camera.enabled = true;
                Debug.Log($"Slice 11 capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            /// <summary>A size-square patch of dry grass near the crew, clear of the crew themselves.</summary>
            static Vector2Int FindFlatGround(TerrainGrid grid, Vector2Int near, int size)
            {
                for (var r = 8; r < 120; r += 4)
                    foreach (var (dx, dz) in new[] { (r, 0), (-r, 0), (0, r), (0, -r) })
                    {
                        var x = near.x + dx;
                        var z = near.y + dz;
                        if (!grid.InBounds(x, z) || !grid.InBounds(x + size + 32, z + size))
                            continue;
                        var h = grid.GetSurfaceHeight(x, z);
                        var ok = h > World.SeaLevel + 2f;
                        for (var j = 0; j < size && ok; j++)
                            for (var i = 0; i < size + 32 && ok; i++)
                                ok = grid.IsGround(x + i, z + j) && !grid.IsWater(x + i, z + j)
                                    && Mathf.Abs(grid.GetSurfaceHeight(x + i, z + j) - h) <= 1f
                                    && grid.GetTopMaterial(x + i, z + j) == MaterialTable.Topsoil;
                        if (ok)
                            return new Vector2Int(x, z);
                    }
                return near + new Vector2Int(10, 0);
            }

            IEnumerator FrameTime(string pose)
            {
                var total = 0f;
                for (var i = 0; i < 120; i++)
                {
                    yield return null;
                    total += Time.unscaledDeltaTime;
                }
                Debug.Log($"Slice 11: frame time on the {pose} pose {total / 120f * 1000f:0.0} ms (editor, 120 frames).");
            }

            /// <summary>
            /// Places the camera directly, with the RtsCamera switched off: left on, its edge pan
            /// and wheel zoom read the real mouse over the Game view and walked the first run's
            /// close-ups out to the full 700 m.
            /// </summary>
            IEnumerator Pose(RtsCamera camera, Vector3 pivot, float distance, float yaw, float pitch)
            {
                camera.enabled = false;
                var terrain = FindAnyObjectByType<TerrainView>().transform;
                var rotation = Quaternion.Euler(pitch, yaw, 0f);
                var position = pivot - rotation * Vector3.forward * distance;
                camera.transform.SetPositionAndRotation(terrain.TransformPoint(position), terrain.rotation * rotation);
                yield return Frames(10);
            }

            /// <summary>
            /// The camera eases toward the rig; at a few ms a frame, 50 frames is not long enough
            /// for it to arrive. Waits on the clock instead.
            /// </summary>
            static IEnumerator Settle()
            {
                var until = Time.realtimeSinceStartup + 2f;
                while (Time.realtimeSinceStartup < until)
                    yield return null;
            }

            static IEnumerator Frames(int count)
            {
                for (var i = 0; i < count; i++)
                    yield return null;
            }

            static void Capture(RtsCamera rig, string name, string what)
            {
                var camera = rig.GetComponent<Camera>();
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
                Debug.Log($"Slice 11 capture: {name} — {what}");
            }
        }
    }
}
