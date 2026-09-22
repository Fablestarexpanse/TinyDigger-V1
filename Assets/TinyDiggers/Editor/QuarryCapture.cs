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
    /// Slice 17 evidence for the Quarry zone (Ronan: fill material "has to come from someplace"):
    /// - a Fill pad marked with nothing to fill it, the crew stopped and the banner saying so;
    /// - a quarry marked beside it, and the same pad filled after time sped up, with the pit the
    ///   material came out of.
    /// Play mode only; logs "Quarry capture: done".
    /// </summary>
    static class QuarryCapture
    {
        const string Folder = "Screenshots/Slice17";
        const float WaitSeconds = 20f;
        const float WorkSeconds = 240f;
        const float Speed = 40f;

        [MenuItem("TinyDiggers/Quarry Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Quarry capture: enter play mode first.");
                return;
            }

            new GameObject("QuarryCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
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
                var near = crew.Units[0].Cell;
                var pad = FindFlat(grid, near);
                var ground = grid.GetSurfaceHeight(pad.x, pad.y);
                var top = ground + 1.5f;

                // A pad to be raised 1.5 m: 36 m³ of fill with nothing on the map to fill it.
                var marked = 0;
                for (var z = pad.y - 3; z <= pad.y + 2; z++)
                    for (var x = pad.x - 3; x <= pad.x + 2; x++)
                        if (map.Designate(x, z, DesignationKind.Fill, top))
                            marked++;
                Debug.Log($"Quarry capture: Fill pad at ({pad.x}, {pad.y}), {marked} cells up to {top:0.#} m, FillCount {map.FillCount}, quarries {map.QuarryCount}");

                rts.enabled = false;
                var centre = view.transform.TransformPoint(new Vector3((pad.x + 0.5f) * grid.CellSize, ground, (pad.y + 0.5f) * grid.CellSize));
                Pose(camera, centre + new Vector3(-14f, 14f, -14f), centre);
                tools.SetMode(ToolMode.Quarry);

                Time.timeScale = Speed;
                var clock = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - clock < WaitSeconds && !AnyNeedsMaterial(crew))
                    yield return null;
                Time.timeScale = 1f;
                Debug.Log($"Quarry capture: after {Time.realtimeSinceStartup - clock:0} s, crew says \"{crew.Units[0].Status}\"; pad still at {grid.GetSurfaceHeight(pad.x, pad.y):0.##} m");
                yield return Frames(6);
                yield return Shoot("quarry_needed");

                // A quarry near the pad, floor 2 m down: where the material comes from. It has to be
                // dry with dry ground around it — the first run put it in a wet hollow, the cut
                // filled with water and nothing digs under water, which is right but shows nothing.
                var pit = FindDryPit(grid, pad);
                var floor = grid.GetSurfaceHeight(pit.x, pit.y) - 2f;
                var cells = 0;
                for (var z = pit.y - 3; z <= pit.y + 2; z++)
                    for (var x = pit.x - 3; x <= pit.x + 2; x++)
                        if (map.SetQuarry(x, z, true, floor))
                            cells++;
                Debug.Log($"Quarry capture: quarry at ({pit.x}, {pit.y}), {cells} cells down to {floor:0.#} m");

                Time.timeScale = Speed;
                clock = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - clock < WorkSeconds && map.Count > 0)
                    yield return null;
                Time.timeScale = 1f;

                var dug = 0f;
                for (var z = pit.y - 3; z <= pit.y + 2; z++)
                    for (var x = pit.x - 3; x <= pit.x + 2; x++)
                        dug += Mathf.Max(0f, floor + 2f - grid.GetSurfaceHeight(x, z));
                Debug.Log($"Quarry capture: after {Time.realtimeSinceStartup - clock:0} s at {Speed}×, {map.Count} designations left; pad at {grid.GetSurfaceHeight(pad.x, pad.y):0.##} m of {top:0.##} m; {dug * grid.CellArea:0.#} m³ out of the quarry");

                tools.SetMode(ToolMode.Select);
                Pose(camera, centre + new Vector3(-14f, 14f, -14f), centre);
                yield return Frames(6);
                yield return Shoot("quarry_filled");

                var pitCentre = view.transform.TransformPoint(new Vector3((pit.x + 0.5f) * grid.CellSize, floor, (pit.y + 0.5f) * grid.CellSize));
                Pose(camera, pitCentre + new Vector3(-10f, 10f, -10f), pitCentre);
                yield return Frames(6);
                yield return Shoot("quarry_pit");

                rts.enabled = true;
                Debug.Log($"Quarry capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static bool AnyNeedsMaterial(CrewView crew)
            {
                foreach (var unit in crew.Units)
                    if (unit.State == CrewUnitState.NeedsMaterial)
                        return true;
                return false;
            }

            /// <summary>
            /// A quarry site 12 to 24 cells from the pad, high enough over the sea that cutting 2 m
            /// out of it stays dry, with dry ground all round the block and its approach.
            /// </summary>
            static Vector2Int FindDryPit(TerrainGrid grid, Vector2Int pad)
            {
                var best = new Vector2Int(pad.x + 20, pad.y);
                var bestScore = float.MaxValue;
                for (var z = pad.y - 24; z <= pad.y + 24; z++)
                {
                    for (var x = pad.x - 24; x <= pad.x + 24; x++)
                    {
                        var away = Vector2Int.Distance(pad, new Vector2Int(x, z));
                        if (away < 12f || away > 24f)
                            continue;
                        var height = grid.GetSurfaceHeight(x, z);
                        if (height < World.SeaLevel + 3.5f)
                            continue;

                        var dry = true;
                        var roughness = 0f;
                        for (var dz = -5; dz <= 4 && dry; dz++)
                            for (var dx = -5; dx <= 4; dx++)
                            {
                                var cx = x + dx;
                                var cz = z + dz;
                                if (!grid.IsGround(cx, cz) || grid.IsWater(cx, cz)
                                    || grid.GetSurfaceHeight(cx, cz) < World.SeaLevel + 3.5f)
                                {
                                    dry = false;
                                    break;
                                }

                                roughness += Mathf.Abs(grid.GetSurfaceHeight(cx, cz) - height);
                            }

                        if (!dry)
                            continue;
                        var score = roughness + away * 0.2f;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(x, z);
                    }
                }

                return best;
            }

            /// <summary>A flat, dry cell near the crew with room around it.</summary>
            static Vector2Int FindFlat(TerrainGrid grid, Vector2Int near)
            {
                var best = near;
                var bestScore = float.MaxValue;
                for (var z = near.y - 50; z <= near.y + 50; z += 2)
                {
                    for (var x = near.x - 50; x <= near.x + 50; x += 2)
                    {
                        if (x < 60 || z < 60 || x >= grid.Width - 60 || z >= grid.Height - 60)
                            continue;
                        if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel + 4f)
                            continue;
                        var height = grid.GetSurfaceHeight(x, z);
                        var roughness = 0f;
                        var dry = true;
                        for (var dz = -4; dz <= 26 && dry; dz++)
                            for (var dx = -4; dx <= 26; dx++)
                            {
                                if (!grid.IsGround(x + dx, z + dz))
                                {
                                    dry = false;
                                    break;
                                }

                                roughness += Mathf.Abs(grid.GetSurfaceHeight(x + dx, z + dz) - height);
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
