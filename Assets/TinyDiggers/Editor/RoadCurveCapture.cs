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
    /// Slice 17 Part C evidence: the tools that shape a road rather than place it. Four pictures of
    /// one dog-legged road across the same ground —
    /// - as drawn, with a bend too tight to drive and the readout saying so;
    /// - after Smooth (Shift+C), the bend rounded out to the minimum turn radius;
    /// - after the grade is held (Shift+G), every stretch re-cut to one slope;
    /// - carried two metres over the land as a causeway, so the fill shows on both sides.
    /// No crew and no building: these are the numbers the player reads while deciding, and they
    /// are all on the ghost. Play mode only; logs "Road curve capture: done".
    /// </summary>
    static class RoadCurveCapture
    {
        const string Folder = "Screenshots/RoadCurve";

        [MenuItem("TinyDiggers/Road Curve Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Road curve capture: enter play mode first.");
                return;
            }

            new GameObject("RoadCurveCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
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
                var roads = tools.Roads;
                tools.SetMode(ToolMode.Road);
                var draft = roads.Draft;
                draft.Snap45 = false;
                draft.Width = 3;
                draft.CellSize = grid.CellSize;

                float Ground(Vector2 at) => grid.GetSurfaceHeight(
                    Mathf.Clamp((int)at.x, 0, grid.Width - 1),
                    Mathf.Clamp((int)at.y, 0, grid.Height - 1));

                // A dog-leg out of open ground near the crew: two long legs meeting at a corner far
                // tighter than anything the units could drive round.
                var start = Open(grid, crew.Units[0].Cell);
                var p0 = (Vector2)start + new Vector2(0.5f, 0.5f);
                var p1 = p0 + new Vector2(26f, 4f);
                var p2 = p1 + new Vector2(2f, 24f);
                var p3 = p2 + new Vector2(24f, 6f);
                foreach (var point in new[] { p0, p1, p2, p3 })
                    draft.Place(point, Ground);
                yield return Frames(5);

                rts.enabled = false;   // a live mouse drifts the pose between shots
                var middle = new Vector2(p1.x + p2.x, p1.y + p2.y) * 0.5f;
                var look = view.transform.TransformPoint(
                    new Vector3(middle.x * grid.CellSize, Ground(middle) + 1f, middle.y * grid.CellSize));
                var pose = look + new Vector3(-26f, 34f, -26f);
                Pose(camera, pose, look);
                yield return Frames(4);
                Debug.Log($"Road curve capture: drawn — tightest bend {Say(roads.TightestTurn)}, {roads.TurnState}; grades {roads.State}");
                yield return Shoot("1_drawn");

                roads.SmoothWholeRoad();
                yield return Frames(5);
                Pose(camera, pose, look);
                yield return Frames(2);
                var each = "";
                foreach (var turn in roads.Turns)
                    each += Say(turn) + " ";
                Debug.Log($"Road curve capture: smoothed — tightest bend {Say(roads.TightestTurn)}, {roads.TurnState}; {draft.Nodes.Count} nodes, bends {each.Trim()}; asked for {draft.MinTurnRadius} m on {draft.CellSize} m cells");
                yield return Shoot("2_smoothed");

                roads.ApplyGradeToWholeRoad();
                yield return Frames(5);
                Pose(camera, pose, look);
                yield return Frames(2);
                Debug.Log($"Road curve capture: grade held — {Grades(roads)}; cut {roads.Cut:0.#} m³, fill {roads.Fill:0.#} m³, deepest cut {roads.DeepestCut:0.#} m, highest fill {roads.HighestFill:0.#} m");
                yield return Shoot("3_graded");

                for (var i = 0; i < draft.Nodes.Count; i++)
                    roads.SetGroundOffset(i, 2f);
                roads.SelectNode(1);   // so the panel's node fields show what they act on
                yield return Frames(5);
                Pose(camera, pose, look);
                yield return Frames(2);
                Debug.Log($"Road curve capture: two metres up — cut {roads.Cut:0.#} m³, fill {roads.Fill:0.#} m³, highest fill {roads.HighestFill:0.#} m");
                yield return Shoot("4_causeway");

                roads.Cancel();
                rts.enabled = true;
                Debug.Log("Road curve capture: done");
                Destroy(gameObject);
            }

            static string Say(float radius) => float.IsInfinity(radius) ? "straight" : $"{radius:0.#} m";

            static string Grades(RoadsHost roads)
            {
                var text = "";
                foreach (var grade in roads.Grades)
                    text += $"{grade * 100f:0.#}% ";
                return text.Trim();
            }

            /// <summary>Open, dry, roughly level ground within 70 cells of the crew, with room for the road.</summary>
            static Vector2Int Open(TerrainGrid grid, Vector2Int near)
            {
                var best = near;
                var bestScore = float.MaxValue;
                const int margin = 60;
                for (var z = near.y - 70; z <= near.y + 70; z += 3)
                {
                    for (var x = near.x - 70; x <= near.x + 70; x += 3)
                    {
                        if (x < margin || z < margin || x >= grid.Width - margin || z >= grid.Height - margin)
                            continue;
                        if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel + 1f)
                            continue;
                        var dx = grid.GetSurfaceHeight(x + 8, z) - grid.GetSurfaceHeight(x - 8, z);
                        var dz = grid.GetSurfaceHeight(x, z + 8) - grid.GetSurfaceHeight(x, z - 8);
                        var score = Mathf.Abs(dx) + Mathf.Abs(dz) + Vector2Int.Distance(near, new Vector2Int(x, z)) * 0.01f;
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
