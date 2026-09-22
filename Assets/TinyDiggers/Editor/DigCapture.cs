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
    /// The crew and its machines actually moving dirt: a block marked to be dug, a Dump Zone
    /// beside it to tip into, and the hole afterwards. Everything the machines have gained — the
    /// scoop and bed sizes, the rock cutter, the footprint — only means something if a load comes
    /// out of the ground and ends up somewhere else, so this counts the cubic metres.
    ///
    /// Play mode only. Shots and the tally go to Screenshots/Units.
    /// </summary>
    static class DigCapture
    {
        const string Folder = "Screenshots/Units";
        const float WorkSeconds = 120f;
        const float Speed = 20f;
        const int Half = 4;      // an 8 x 8 block

        [MenuItem("TinyDiggers/Dig Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Dig capture: enter play mode first.");
                return;
            }

            new GameObject("DigCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
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
                while (view.Grid == null || crew.Units.Count == 0 || tools.Map == null)
                    yield return null;
                yield return Frames(20);

                var grid = view.Grid;
                var map = tools.Map;
                var here = crew.Units[0].Cell;
                var site = Flat(grid, here);
                var ground = grid.GetSurfaceHeight(site.x, site.y);
                // Three steps deep: deeper than a robot can reach down from the rim, and within
                // the machine's arm. So the picture shows what the digger is for.
                var floor = ground - 3f * grid.HeightStep;

                var marked = 0;
                for (var z = site.y - Half; z < site.y + Half; z++)
                    for (var x = site.x - Half; x < site.x + Half; x++)
                        if (map.Designate(x, z, DesignationKind.Dig, floor))
                            marked++;

                // Somewhere to put it: a heap beside the cut, clear of the block itself.
                var tip = new Vector2Int(site.x + Half + 5, site.y);
                var zone = 0;
                for (var z = tip.y - 2; z <= tip.y + 2; z++)
                    for (var x = tip.x - 2; x <= tip.x + 2; x++)
                        if (map.SetDumpZone(x, z, true))
                            zone++;

                var before = Volume(grid, site, floor);
                Debug.Log($"Dig capture: {marked} cells to dig at ({site.x}, {site.y}) down to " +
                          $"{floor:0.##} m, {zone} cells of dump zone; {before:0.##} m3 above the floor");

                rts.enabled = false;
                var middle = view.transform.TransformPoint(new Vector3(
                    (site.x + 0.5f) * grid.CellSize, ground, (site.y + 0.5f) * grid.CellSize));
                Pose(camera, middle + new Vector3(-9f, 7f, -9f), middle);
                yield return Frames(6);
                yield return Shoot("dig_before");

                Time.timeScale = Speed;
                var clock = Time.realtimeSinceStartup;
                var shot = false;
                while (Time.realtimeSinceStartup - clock < WorkSeconds && map.Count > 0)
                {
                    // One picture partway through, with the crew at work in the cut.
                    if (!shot && Time.realtimeSinceStartup - clock > WorkSeconds * 0.35f)
                    {
                        shot = true;
                        Time.timeScale = 1f;
                        yield return Frames(4);
                        yield return Shoot("dig_working");
                        Time.timeScale = Speed;
                    }

                    yield return null;
                }

                Time.timeScale = 1f;
                var after = Volume(grid, site, floor);
                var moved = before - after;
                var heap = Heap(grid, tip);
                Debug.Log($"Dig capture: {moved:0.##} m3 shifted in {Time.realtimeSinceStartup - clock:0} s " +
                          $"at {Speed}x, {map.Count} designations left, heap stands {heap:0.##} m proud. " +
                          Roll(crew));

                yield return Frames(6);
                yield return Shoot("dig_after");

                var heapAt = view.transform.TransformPoint(new Vector3(
                    (tip.x + 0.5f) * grid.CellSize, grid.GetSurfaceHeight(tip.x, tip.y),
                    (tip.y + 0.5f) * grid.CellSize));
                Pose(camera, heapAt + new Vector3(-7f, 5f, -7f), heapAt);
                yield return Frames(6);
                yield return Shoot("dig_heap");

                rts.enabled = true;
                Debug.Log("Dig capture: done.");
                Destroy(gameObject);
            }

            /// <summary>What each unit is doing, so a stall shows in the log rather than silently.</summary>
            static string Roll(CrewView crew)
            {
                var roll = "";
                foreach (var unit in crew.Units)
                    roll += $"[{unit.Role} {unit.Id}: {unit.State}, {unit.Inventory.Total:0.##} m3, "
                         + $"{unit.UnreachableCount} out of reach: {unit.NearestUnreachable}] ";
                return roll;
            }

            static float Volume(TerrainGrid grid, Vector2Int site, float floor)
            {
                var standing = 0f;
                for (var z = site.y - Half; z < site.y + Half; z++)
                    for (var x = site.x - Half; x < site.x + Half; x++)
                        standing += Mathf.Max(0f, grid.GetSurfaceHeight(x, z) - floor);
                return standing * grid.CellArea;
            }

            static float Heap(TerrainGrid grid, Vector2Int tip)
            {
                var top = float.MinValue;
                var round = float.MaxValue;
                for (var z = tip.y - 4; z <= tip.y + 4; z++)
                    for (var x = tip.x - 4; x <= tip.x + 4; x++)
                    {
                        var height = grid.GetSurfaceHeight(x, z);
                        if (Mathf.Abs(x - tip.x) <= 2 && Mathf.Abs(z - tip.y) <= 2)
                            top = Mathf.Max(top, height);
                        else
                            round = Mathf.Min(round, height);
                    }

                return top - round;
            }

            /// <summary>Flat, dry ground near the crew with room for the cut and the heap.</summary>
            static Vector2Int Flat(TerrainGrid grid, Vector2Int near)
            {
                var best = near;
                var bestScore = float.MaxValue;
                for (var z = near.y - 40; z <= near.y + 40; z += 2)
                {
                    for (var x = near.x - 40; x <= near.x + 40; x += 2)
                    {
                        if (x < 60 || z < 60 || x >= grid.Width - 60 || z >= grid.Height - 60)
                            continue;
                        if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel + 4f)
                            continue;

                        var height = grid.GetSurfaceHeight(x, z);
                        var roughness = 0f;
                        var dry = true;
                        for (var dz = -Half - 2; dz <= Half + 2 && dry; dz++)
                            for (var dx = -Half - 2; dx <= Half + 9; dx++)
                            {
                                var cx = x + dx;
                                var cz = z + dz;
                                if (!grid.IsGround(cx, cz) || grid.IsWater(cx, cz)
                                    || grid.GetSurfaceHeight(cx, cz) < World.SeaLevel + 3f)
                                {
                                    dry = false;
                                    break;
                                }

                                roughness += Mathf.Abs(grid.GetSurfaceHeight(cx, cz) - height);
                            }

                        if (!dry)
                            continue;
                        var score = roughness + Vector2Int.Distance(near, new Vector2Int(x, z)) * 0.5f;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(x, z);
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
