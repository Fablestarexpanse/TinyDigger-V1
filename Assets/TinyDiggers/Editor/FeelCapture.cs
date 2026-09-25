using System.Collections;
using System.IO;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using PromptWaffle.Terrain;
using TinyDiggers.Units;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Twenty seconds of a digger cutting into a hillside and a dumper tipping onto a heap, for one
    /// question (Ronan, 2026-09-23): *does material look like it moves because the machine moved
    /// it?* Frames go to Screenshots/Feel/frames as PNGs; ffmpeg turns them into the mp4.
    ///
    /// The crew is cut down to the digger and its dumper first. Trial B showed six units in a
    /// three-cell face spend 41% of their time queueing, and a video of a queue answers a
    /// different question.
    ///
    /// <see cref="Time.captureFramerate"/> is what makes it a film rather than a screen recording:
    /// Unity advances its clock a fixed step a frame, so thirty frames are exactly one second of
    /// game time however long they take to render, and the dust and the height lag — both on
    /// unscaled time — come out at the speed they were tuned at.
    ///
    /// Play mode only; logs "Feel capture: done".
    /// </summary>
    static class FeelCapture
    {
        const string Folder = "Screenshots/Feel";
        const string Frames = "Screenshots/Feel/frames";
        const int Fps = 30;
        const int Seconds = 20;
        const int Width = 1280;
        const int Height = 720;

        [MenuItem("TinyDiggers/Feel Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Feel capture: enter play mode first.");
                return;
            }

            new GameObject("FeelCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                Directory.CreateDirectory(Frames);
                foreach (var stale in Directory.GetFiles(Frames, "*.png"))
                    File.Delete(stale);

                var view = FindAnyObjectByType<TerrainView>();
                var tools = FindAnyObjectByType<PlayerTools>();
                var crew = FindAnyObjectByType<CrewView>();
                var rts = FindAnyObjectByType<RtsCamera>();
                while (view == null || view.Grid == null || crew == null || crew.Units.Count == 0)
                {
                    view = FindAnyObjectByType<TerrainView>();
                    crew = FindAnyObjectByType<CrewView>();
                    yield return null;
                }

                var grid = view.Grid;
                // The capture drives the crew itself and has no worksite to assign them to.
                crew.NeedWorksite = false;

                // Just the machines: a digger and its dumper, and nobody to queue behind.
                for (var i = crew.Units.Count - 1; i >= 0; i--)
                    if (crew.Units[i].Role == UnitRole.Worker)
                        crew.Dismiss(i);
                var haveDigger = false;
                var haveDumper = false;
                foreach (var unit in crew.Units)
                {
                    haveDigger |= unit.Role == UnitRole.Digger;
                    haveDumper |= unit.Role == UnitRole.Hauler;
                }

                if (!haveDigger)
                    crew.Hire(UnitRole.Digger);
                if (!haveDumper)
                    crew.Hire(UnitRole.Hauler);
                yield return Frames_(5);

                // A rise to cut into, and a heap to build, both beside the machines.
                var near = crew.Units[0].Cell;
                var hill = Rise(grid, near);
                var face = 0;
                for (var z = hill.y - 5; z <= hill.y + 5; z++)
                    for (var x = hill.x - 2; x <= hill.x + 2; x++)
                        if (tools.Map.Designate(x, z, DesignationKind.Dig, grid.GetSurfaceHeight(x, z) - 2f))
                            face++;

                var tip = new Vector2Int(near.x - 12, near.y);
                var zone = 0;
                for (var z = tip.y - 4; z <= tip.y + 4; z++)
                    for (var x = tip.x - 4; x <= tip.x + 4; x++)
                        if (tools.Map.SetDumpZone(x, z, true, grid.GetSurfaceHeight(x, z) + 3f))
                            zone++;

                Debug.Log($"Feel capture: {face} cells of face at ({hill.x}, {hill.y}), {zone} of tip at ({tip.x}, {tip.y}); "
                          + $"crew {crew.Units.Count}");

                // Start when somebody is actually at work, not after a guessed wait.
                var settle = Time.time;
                while (Time.time - settle < 90f && Working(crew).State != CrewUnitState.Digging
                       && Working(crew).State != CrewUnitState.Tipping)
                    yield return null;
                Debug.Log($"Feel capture: rolling after {Time.time - settle:0} game s; {Working(crew).Status}");

                // The camera is placed by hand and held: a live RTS camera drifts a capture.
                if (rts != null)
                    rts.enabled = false;
                var camera = rts != null ? rts.GetComponent<Camera>() : Camera.main;

                var target = new RenderTexture(Width, Height, 24) { name = "Feel Capture" };
                var shot = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                var was = camera.targetTexture;
                var wasCapture = Time.captureFramerate;
                Time.captureFramerate = Fps;

                var effects = view.transform.Find("Ground Effects");
                var counter = effects == null ? null : effects.GetComponent<GroundEffects>();
                counter?.ResetCount();

                var total = Fps * Seconds;
                for (var frame = 0; frame < total; frame++)
                {
                    yield return new WaitForEndOfFrame();

                    // Follow whoever is working, from low and close, so the bucket and the bed fill
                    // the frame rather than the island.
                    // Framed on the ground being worked, not on the machine. A digger stands beside
                    // the cell it cuts, so a camera on the unit puts the bite behind it and the
                    // dust out of shot — which is what the first two takes did (2026-09-23). The
                    // midpoint keeps both the bucket and the earth in frame.
                    var watch = Working(crew);
                    var job = watch.JobTarget;
                    var atUnit = new Vector3(watch.Position.x, 0f, watch.Position.y);
                    var atJob = grid.InBounds(job.x, job.y)
                        ? new Vector3(job.x + 0.5f, 0f, job.y + 0.5f)
                        : atUnit;
                    var middle = (atUnit + atJob) * 0.5f;
                    var height = grid.InBounds(job.x, job.y)
                        ? Mathf.Max(watch.Height, grid.GetSurfaceHeight(job.x, job.y))
                        : watch.Height;
                    var at = view.transform.TransformPoint(new Vector3(
                        middle.x * grid.CellSize, height, middle.z * grid.CellSize));
                    var eye = at + OpenSide(grid, watch.Cell) * 4f + Vector3.up * 2.2f;
                    camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at + Vector3.up * 0.4f - eye));

                    camera.targetTexture = target;
                    camera.Render();
                    camera.targetTexture = was;

                    var previous = RenderTexture.active;
                    RenderTexture.active = target;
                    shot.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    shot.Apply(false);
                    RenderTexture.active = previous;

                    File.WriteAllBytes(Path.Combine(Frames, $"frame_{frame:0000}.png"), shot.EncodeToPNG());
                }

                Time.captureFramerate = wasCapture;
                camera.targetTexture = was;
                Destroy(shot);
                target.Release();
                Destroy(target);
                if (rts != null)
                    rts.enabled = true;

                Debug.Log($"Feel capture: done — {total} frames at {Fps} fps in {Folder}, "
                          + $"{counter?.Bursts ?? 0} effect bursts while filming");
                Destroy(gameObject);
            }

            /// <summary>Whoever is digging or tipping, or the first unit if nobody is.</summary>
            static CrewUnit Working(CrewView crew)
            {
                foreach (var unit in crew.Units)
                    if (unit.State == CrewUnitState.Digging || unit.State == CrewUnitState.Tipping)
                        return unit;
                return crew.Units[0];
            }

            /// <summary>
            /// A bank worth cutting into within 40 cells of the crew: about two metres of rise
            /// over four, in soil. Not the *steepest* ground — that is the mountain, and the first
            /// take had the digger clinging to a cliff with the camera inside it (2026-09-23).
            /// </summary>
            static Vector2Int Rise(TerrainGrid grid, Vector2Int near)
            {
                var best = new Vector2Int(near.x + 8, near.y);
                var bestScore = float.MaxValue;
                for (var z = near.y - 40; z <= near.y + 40; z += 2)
                {
                    for (var x = near.x - 40; x <= near.x + 40; x += 2)
                    {
                        if (!grid.InBounds(x - 8, z - 8) || !grid.InBounds(x + 8, z + 8))
                            continue;
                        if (!grid.IsGround(x, z) || grid.GetSurfaceHeight(x, z) < World.SeaLevel + 1f)
                            continue;
                        if (TinyDiggersMaterials.IsStone(grid.GetTopMaterial(x, z)))
                            continue;
                        var dx = grid.GetSurfaceHeight(x + 4, z) - grid.GetSurfaceHeight(x - 4, z);
                        var dz = grid.GetSurfaceHeight(x, z + 4) - grid.GetSurfaceHeight(x, z - 4);
                        var rise = Mathf.Sqrt(dx * dx + dz * dz);
                        if (rise < 1f || rise > 5f)
                            continue;
                        var score = Mathf.Abs(rise - 2f) + Vector2Int.Distance(near, new Vector2Int(x, z)) * 0.02f;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(x, z);
                    }
                }

                return best;
            }

            /// <summary>
            /// Which way to stand to see a unit: the compass point around it with the lowest
            /// ground, so the camera is on the open side of a bank rather than buried in it.
            /// </summary>
            static Vector3 OpenSide(TerrainGrid grid, Vector2Int cell)
            {
                var best = new Vector3(-1f, 0f, -1f).normalized;
                var lowest = float.MaxValue;
                for (var turn = 0; turn < 8; turn++)
                {
                    var angle = turn * Mathf.PI * 0.25f;
                    var dx = Mathf.RoundToInt(Mathf.Cos(angle) * 8f);
                    var dz = Mathf.RoundToInt(Mathf.Sin(angle) * 8f);
                    var x = cell.x + dx;
                    var z = cell.y + dz;
                    if (!grid.InBounds(x, z) || !grid.IsGround(x, z))
                        continue;
                    var height = grid.GetSurfaceHeight(x, z);
                    if (height >= lowest)
                        continue;
                    lowest = height;
                    best = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)).normalized;
                }

                return best;
            }

            static IEnumerator Frames_(int n)
            {
                for (var i = 0; i < n; i++)
                    yield return null;
            }
        }
    }
}
