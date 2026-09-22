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
    /// Slice 17 Part B evidence, UI included:
    /// - a road ghost climbing a hillside near the crew, with its grade labels and the panel's
    ///   cut / fill readout;
    /// - the same road after the crew has built it, time sped up, with the cut and the embankment;
    /// - a junction: a second road drawn from the first one's middle node.
    /// Play mode only; logs "Slice 17 road capture: done".
    /// </summary>
    static class Slice17RoadCapture
    {
        const string Folder = "Screenshots/Slice17";

        /// <summary>Real seconds the crew gets to build the road, at <see cref="Speed"/>× time.</summary>
        const float BuildSeconds = 300f;
        const float Speed = 40f;

        [MenuItem("TinyDiggers/Slice 17 Road Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 17 road capture: enter play mode first.");
                return;
            }

            new GameObject("Slice17RoadCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
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
                while (view.Grid == null || crew.Units.Count == 0 || tools.Roads == null)
                    yield return null;
                yield return Frames(20);

                var grid = view.Grid;
                var near = crew.Units[0].Cell;
                var start = FindSlope(grid, near, out var uphill);
                var roads = tools.Roads;
                tools.SetMode(ToolMode.Road);
                var draft = roads.Draft;
                draft.Snap45 = false;
                draft.Width = 3;

                // Up the hill in three legs: 6%, 15% (over the 12% limit, so orange) and 5%.
                float Ground(Vector2 at) => grid.GetSurfaceHeight(Mathf.Clamp((int)at.x, 0, grid.Width - 1), Mathf.Clamp((int)at.y, 0, grid.Height - 1));
                var across = new Vector2(-uphill.y, uphill.x);
                var p0 = (Vector2)start + new Vector2(0.5f, 0.5f);
                var p1 = p0 + uphill * 12f + across * 3f;
                var p2 = p1 + uphill * 10f - across * 4f;
                var p3 = p2 + uphill * 10f + across * 2f;
                var h = Ground(p0);
                draft.Place(p0, Ground);
                SetHeight(draft, 0, h);
                draft.Place(p1, Ground);
                h += 0.06f * Vector2.Distance(p0, p1) * grid.CellSize;
                SetHeight(draft, 1, h);
                draft.Place(p2, Ground);
                h += 0.15f * Vector2.Distance(p1, p2) * grid.CellSize;
                SetHeight(draft, 2, h);
                draft.Place(p3, Ground);
                h += 0.05f * Vector2.Distance(p2, p3) * grid.CellSize;
                SetHeight(draft, 3, h);
                yield return Frames(5);
                Debug.Log($"Slice 17 road capture: road up the hill from ({start.x}, {start.y}); grades {Describe(roads)}; cut {roads.Cut:0.#} m³, fill {roads.Fill:0.#} m³");

                rts.enabled = false;
                var middle = view.transform.TransformPoint(new Vector3((p1.x + p2.x) * 0.5f * grid.CellSize, h * 0.5f + Ground(p0) * 0.5f, (p1.y + p2.y) * 0.5f * grid.CellSize));
                var side3 = new Vector3(across.x, 0f, across.y);
                var up3 = new Vector3(uphill.x, 0f, uphill.y);
                var pose = middle + side3 * 20f - up3 * 10f + Vector3.up * 16f;
                Pose(camera, pose, middle);
                yield return Frames(4);
                yield return Shoot("road_ghost");

                // A Dump Zone for the spoil, below the road, as a player would mark one; then lay the
                // road and let the crew build it with time sped up.
                var zoneCentre = Vector2Int.FloorToInt(p0 - uphill * 10f);
                var zoneCap = grid.GetSurfaceHeight(zoneCentre.x, zoneCentre.y) + 3f;
                for (var z = zoneCentre.y - 5; z <= zoneCentre.y + 5; z++)
                    for (var x = zoneCentre.x - 5; x <= zoneCentre.x + 5; x++)
                        tools.Map.SetDumpZone(x, z, true, zoneCap);
                var laid = roads.Commit();
                Debug.Log($"Slice 17 road capture: first road laid {laid}: {tools.LastAction}; {tools.Map.Count} designations");
                var roadId = roads.Network.Roads()[roads.Network.Roads().Count - 1];
                Time.timeScale = Speed;
                var clock = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - clock < BuildSeconds && (tools.Map.Count > 0 || roads.Builder.Waiting > 0))
                    yield return null;
                Time.timeScale = 1f;
                Debug.Log($"Slice 17 road capture: after {Time.realtimeSinceStartup - clock:0} s at {Speed}×, {tools.Map.Count} designations left, {roads.Builder.Waiting} road cells unpaved");

                tools.SetMode(ToolMode.Select);
                Pose(camera, pose, middle);
                yield return Frames(6);
                yield return Shoot("road_built");

                // A junction: a second road from the first one's middle node, off to the side.
                tools.SetMode(ToolMode.Road);
                var chain = roads.Network.Chain(roadId);
                var joint = chain[1].Position;
                draft.Place(joint, Ground, roads.Network);
                draft.Place(joint + across * 14f, Ground, roads.Network);
                draft.Place(joint + across * 24f + uphill * 4f, Ground, roads.Network);
                // Across the slope, held to 8% whatever the ground does, so it can be built.
                for (var i = 1; i < draft.Nodes.Count; i++)
                {
                    var run = Vector2.Distance(draft.Nodes[i - 1].Position, draft.Nodes[i].Position) * grid.CellSize;
                    var previous = draft.Nodes[i - 1].Height;
                    SetHeight(draft, i, Mathf.Clamp(draft.Nodes[i].Height, previous - 0.08f * run, previous + 0.08f * run));
                }
                yield return Frames(4);
                var junction = view.transform.TransformPoint(new Vector3(joint.x * grid.CellSize, Ground(joint), joint.y * grid.CellSize));
                Pose(camera, junction + side3 * 6f - up3 * 16f + Vector3.up * 18f, junction + side3 * 6f);
                yield return Frames(4);
                yield return Shoot("road_junction_ghost");
                var second = roads.Commit();
                Debug.Log($"Slice 17 road capture: second road laid {second}: {tools.LastAction}");
                var node = roads.Network.NodeNear(joint, 0.1f);
                Debug.Log($"Slice 17 road capture: junction node {(node != null ? node.Id : -1)} joins {(node != null ? roads.Network.Degree(node.Id) : 0)} segments; {roads.Network.Roads().Count} roads");
                yield return Frames(6);
                yield return Shoot("road_junction");

                tools.SetMode(ToolMode.Select);
                rts.enabled = true;
                Debug.Log($"Slice 17 road capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static void SetHeight(RoadDraft draft, int index, float height) =>
                draft.Raise(index, height - draft.Nodes[index].Height);

            static string Describe(RoadsHost roads)
            {
                var text = "";
                foreach (var grade in roads.Grades)
                    text += $"{grade * 100f:0.#}% ";
                return text + roads.State;
            }

            /// <summary>A hillside cell within 60 cells of <paramref name="near"/> rising about one in ten, and which way is up.</summary>
            static Vector2Int FindSlope(TerrainGrid grid, Vector2Int near, out Vector2 uphill)
            {
                var best = near;
                var bestScore = float.MaxValue;
                uphill = Vector2.right;
                const int reach = 8;
                for (var z = near.y - 60; z <= near.y + 60; z += 3)
                {
                    for (var x = near.x - 60; x <= near.x + 60; x += 3)
                    {
                        if (x < reach + 40 || z < reach + 40 || x >= grid.Width - reach - 40 || z >= grid.Height - reach - 40)
                            continue;
                        if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel + 2f)
                            continue;
                        var dx = grid.GetSurfaceHeight(x + reach, z) - grid.GetSurfaceHeight(x - reach, z);
                        var dz = grid.GetSurfaceHeight(x, z + reach) - grid.GetSurfaceHeight(x, z - reach);
                        var rise = Mathf.Sqrt(dx * dx + dz * dz) / (2f * reach * grid.CellSize);
                        var score = Mathf.Abs(rise - 0.10f) + Vector2Int.Distance(near, new Vector2Int(x, z)) * 0.002f;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(x, z);
                        uphill = new Vector2(dx, dz).sqrMagnitude > 1e-6f ? new Vector2(dx, dz).normalized : Vector2.right;
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
