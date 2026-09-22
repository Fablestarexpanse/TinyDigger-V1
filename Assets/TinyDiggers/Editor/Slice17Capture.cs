using System.Collections;
using System.IO;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Slice 17 Part A evidence, with the UI in the shot (screen capture, not a camera render):
    /// - the toolbar with Dig active and its panel;
    /// - the brush ring and the ghost disc at H on a hillside, close up;
    /// - the crew panel with the "needs a Dump Zone" banner showing.
    /// Play mode only; logs "Slice 17 capture: done".
    /// </summary>
    static class Slice17Capture
    {
        const string Folder = "Screenshots/Slice17";

        [MenuItem("TinyDiggers/Slice 17 Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 17 capture: enter play mode first.");
                return;
            }

            new GameObject("Slice17Capture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                Directory.CreateDirectory(Folder);
                var view = FindAnyObjectByType<TerrainView>();
                var tools = FindAnyObjectByType<PlayerTools>();
                var crew = FindAnyObjectByType<CrewView>();
                var rts = FindAnyObjectByType<RtsCamera>();
                var camera = rts.GetComponent<Camera>();
                while (view.Grid == null || crew.Units.Count == 0)
                    yield return null;
                yield return Frames(30);

                var grid = view.Grid;
                var slope = FindSlope(grid, out var downhill);
                var at = view.transform.TransformPoint(TerrainSpace.CellCentre(grid, slope.x, slope.y));
                var side = new Vector3(-downhill.y, 0f, downhill.x);
                Debug.Log($"Slice 17 capture: hillside at ({slope.x}, {slope.y}), ground {grid.GetSurfaceHeight(slope.x, slope.y):0.#} m");

                rts.enabled = false;
                tools.SetMode(ToolMode.Dig);
                tools.BrushRadius = 5;
                tools.SetTargetHeight(grid.GetSurfaceHeight(slope.x, slope.y) - 1f);

                // (a) The toolbar with Dig active and its panel, the brush out on the hill.
                Pose(camera, at + side * 30f + Vector3.up * 26f - new Vector3(downhill.x, 0f, downhill.y) * 12f, at);
                yield return Aim(tools, camera, at);
                yield return Shoot("toolbar_dig");

                // (b) The ring and the ghost disc close up on the slope.
                Pose(camera, at + side * 11f + Vector3.up * 8f + new Vector3(downhill.x, 0f, downhill.y) * 5f, at);
                yield return Aim(tools, camera, at);
                yield return Shoot("brush_on_slope");

                // (c) The crew panel with the warning: a unit with a full load and nowhere to tip.
                tools.PointerOverride = null;
                tools.SetMode(ToolMode.Select);
                var unit = crew.Units[0];
                unit.Inventory.Add(MaterialTable.DirtLoose, unit.Inventory.Remaining);
                var until = Time.realtimeSinceStartup + 6f;
                while (unit.State != CrewUnitState.NeedsSomewhereToTip && Time.realtimeSinceStartup < until)
                    yield return null;
                crew.Select(0);
                var body = crew.BodyPosition(0);
                Pose(camera, body + new Vector3(10f, 9f, -12f), body);
                yield return Frames(10);
                yield return Shoot("crew_warning");
                Debug.Log($"Slice 17 capture: unit 0 is {unit.State}: {unit.Status}");

                rts.enabled = true;
                Debug.Log($"Slice 17 capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static IEnumerator Aim(PlayerTools tools, Camera camera, Vector3 world)
            {
                yield return null;
                tools.PointerOverride = camera.WorldToScreenPoint(world);
                yield return Frames(6);
            }

            /// <summary>A land cell on a steady slope a good way above the sea, and which way is downhill.</summary>
            static Vector2Int FindSlope(TerrainGrid grid, out Vector2 downhill)
            {
                var best = new Vector2Int(grid.Width / 2, grid.Height / 2);
                var bestScore = 0f;
                downhill = Vector2.right;
                const int reach = 8;
                for (var z = reach; z < grid.Height - reach; z += 5)
                {
                    for (var x = reach; x < grid.Width - reach; x += 5)
                    {
                        if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel + 4f)
                            continue;
                        var dx = grid.GetSurfaceHeight(x + reach, z) - grid.GetSurfaceHeight(x - reach, z);
                        var dz = grid.GetSurfaceHeight(x, z + reach) - grid.GetSurfaceHeight(x, z - reach);
                        var rise = Mathf.Sqrt(dx * dx + dz * dz) / (2f * reach * grid.CellSize);
                        // A hillside, not a cliff: about one in three.
                        var score = rise > 0.7f ? 0f : rise;
                        if (score <= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(x, z);
                        downhill = -new Vector2(dx, dz).normalized;
                    }
                }

                return best;
            }

            static void Pose(Camera camera, Vector3 position, Vector3 target) =>
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));

            static IEnumerator Shoot(string name)
            {
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(Folder, name + ".png"), shot.EncodeToPNG());
                Destroy(shot);
            }

            static IEnumerator Frames(int n)
            {
                for (var i = 0; i < n; i++)
                    yield return null;
            }
        }
    }
}
