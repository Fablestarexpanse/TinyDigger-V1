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
    /// A road, built: two nodes laid across a rise near the crew, the cut and fill that makes,
    /// and what the crew leaves behind. A road is the answer to ground too steep to drive, so it
    /// is worth knowing whether one can actually be got in.
    ///
    /// Play mode only. Shots and the tally go to Screenshots/Units.
    /// </summary>
    static class RoadCapture
    {
        const string Folder = "Screenshots/Units";
        const float WorkSeconds = 150f;
        const float Speed = 20f;

        [MenuItem("TinyDiggers/Road Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Road capture: enter play mode first.");
                return;
            }

            new GameObject("RoadCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                Directory.CreateDirectory(Folder);
                var view = FindAnyObjectByType<TerrainView>();
                var tools = FindAnyObjectByType<PlayerTools>();
                var crew = FindAnyObjectByType<CrewView>();
                var roads = FindAnyObjectByType<RoadsHost>();
                var rts = FindAnyObjectByType<RtsCamera>();
                var camera = rts != null ? rts.GetComponent<Camera>() : Camera.main;
                while (view.Grid == null || crew.Units.Count == 0 || tools.Map == null || roads == null)
                    yield return null;
                yield return Frames(20);

                var grid = view.Grid;
                var map = tools.Map;
                var here = crew.Units[0].Cell;

                // Two ends about twenty cells apart, over whatever the ground does between them:
                // a road that needs no cut and no fill proves nothing.
                var from = new Vector2(here.x + 3f, here.y + 3f);
                var to = new Vector2(here.x + 23f, here.y + 3f);
                var before = Profile(grid, from, to);

                // Somewhere for the spoil, or the crew stops with a full scoop.
                var tip = new Vector2Int(here.x - 6, here.y + 3);
                for (var z = tip.y - 2; z <= tip.y + 2; z++)
                    for (var x = tip.x - 2; x <= tip.x + 2; x++)
                        map.SetDumpZone(x, z, true);

                // A road across a dip wants fill, and fill has to come from somewhere: without a
                // quarry the crew stops and says so, which is right but builds no road.
                var pit = new Vector2Int(here.x - 6, here.y - 8);
                var floor = grid.GetSurfaceHeight(pit.x, pit.y) - 2f;
                var quarry = 0;
                for (var z = pit.y - 3; z <= pit.y + 2; z++)
                    for (var x = pit.x - 3; x <= pit.x + 2; x++)
                        if (map.SetQuarry(x, z, true, floor))
                            quarry++;

                roads.Draft.Clear();
                roads.Draft.Snap45 = false;
                roads.Draft.Place(from, at => grid.GetSurfaceHeight((int)at.x, (int)at.y));
                roads.Draft.Place(to, at => grid.GetSurfaceHeight((int)at.x, (int)at.y));
                var road = roads.Draft.Commit(roads.Network, grid.CellSize);
                yield return Frames(4);   // the host replans on its own Update

                Debug.Log($"Road capture: road {road} from ({from.x:0}, {from.y:0}) to ({to.x:0}, {to.y:0}), "
                          + $"width {roads.Draft.Width}; cut {roads.Cut:0.#} m3, fill {roads.Fill:0.#} m3; "
                          + $"{map.Count} designations, {map.DumpZoneCount} dump cells, "
                          + $"{quarry} quarry cells down to {floor:0.#} m");

                if (rts != null) rts.enabled = false;
                var middle = view.transform.TransformPoint(new Vector3(
                    (from.x + to.x) * 0.5f * grid.CellSize,
                    grid.GetSurfaceHeight((int)((from.x + to.x) * 0.5f), (int)from.y),
                    from.y * grid.CellSize));
                Pose(camera, middle + new Vector3(-11f, 9f, -11f), middle);
                yield return Frames(6);
                yield return Shoot("road_marked");

                Time.timeScale = Speed;
                var clock = Time.realtimeSinceStartup;
                var shot = false;
                while (Time.realtimeSinceStartup - clock < WorkSeconds && map.Count > 0)
                {
                    if (!shot && Time.realtimeSinceStartup - clock > WorkSeconds * 0.4f)
                    {
                        shot = true;
                        Time.timeScale = 1f;
                        yield return Frames(4);
                        yield return Shoot("road_working");
                        Time.timeScale = Speed;
                    }

                    yield return null;
                }

                Time.timeScale = 1f;
                var after = Profile(grid, from, to);
                var surfaced = Surfaced(grid, from, to);
                Debug.Log($"Road capture: {map.Count} designations left after "
                          + $"{Time.realtimeSinceStartup - clock:0} s at {Speed}x; the line moved "
                          + $"{Mathf.Abs(after - before):0.##} m of height in total, {surfaced} cells "
                          + $"are road surface. " + Roll(crew));

                yield return Frames(6);
                yield return Shoot("road_built");
                if (rts != null) rts.enabled = true;
                Debug.Log("Road capture: done.");
                Destroy(gameObject);
            }

            /// <summary>The total height along the line, so a cut or a fill shows as a change.</summary>
            static float Profile(TerrainGrid grid, Vector2 from, Vector2 to)
            {
                var total = 0f;
                for (var x = (int)from.x; x <= (int)to.x; x++)
                    total += grid.GetSurfaceHeight(x, (int)from.y);
                return total;
            }

            /// <summary>How much of the line is actually road material.</summary>
            static int Surfaced(TerrainGrid grid, Vector2 from, Vector2 to)
            {
                var count = 0;
                for (var x = (int)from.x; x <= (int)to.x; x++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (grid.GetTopMaterial(x, (int)from.y + dz) == MaterialTable.Road)
                            count++;
                return count;
            }

            static string Roll(CrewView crew)
            {
                var roll = "";
                foreach (var unit in crew.Units)
                    roll += $"[{UnitNames.Of(unit.Role)} {unit.Id}: {unit.State}, "
                         + $"{unit.Inventory.Total:0.##} m3] ";
                return roll;
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
