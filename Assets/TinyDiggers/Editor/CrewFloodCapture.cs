using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PromptWaffle.DynamicWater;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Evidence that the crew climbs out of a flood, in play, with the live simulated water:
    /// - takes a crew unit;
    /// - levels a platform round it at its own height, in rock, because the crew starts on the
    ///   island's central mountain, where a bowl cut into the slope just drains downhill;
    /// - carves a stepped bowl under it (its cell and the middle 1 m down, a ring round that 0.5 m
    ///   down, so every step is one the crew can climb);
    /// - opens a spring in the middle.
    /// As the water rises over the unit it should give its job up and drive onto the ring, and
    /// off the ring onto the land if that floods too. The crew starts bunched, so every unit in
    /// the bowl is followed: each status change is logged, with the tracked unit's water every
    /// half second, and the bowl is shot dry, as the unit sets off, and once it is out. The terrain
    /// mesh takes a few seconds to show a big edit, so the spring waits for it. Play mode only.
    /// </summary>
    static class CrewFloodCapture
    {
        const string Folder = "Screenshots/DynamicWater";

        [MenuItem("TinyDiggers/Crew Flood Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Crew flood capture: enter play mode first.");
                return;
            }

            new GameObject("CrewFloodCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            const int Inner = 3;        // cells either side of the middle, 1 m down
            const int Outer = 6;        // cells either side of the middle, 0.5 m down
            const int Platform = 10;    // cells either side of the middle levelled first
            const float SpringRate = 3f;
            const float SpringSeconds = 25f;
            const float Timeout = 45f;

            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var crew = FindAnyObjectByType<CrewView>();
                var zone = FindAnyObjectByType<WaterZone>();
                var feed = FindAnyObjectByType<GridWaterFeed>();
                var rts = FindAnyObjectByType<RtsCamera>();
                Directory.CreateDirectory(Folder);

                var until = Time.realtimeSinceStartup + 10f;
                while ((zone.Simulation == null || feed.Sweeps == 0 || crew.Units.Count == 0) && Time.realtimeSinceStartup < until)
                    yield return null;
                if (zone.Simulation == null || feed.Sweeps == 0 || crew.Units.Count == 0)
                {
                    Debug.LogError("Crew flood capture: no water zone, no live water in the grid, or no crew.");
                    Destroy(gameObject);
                    yield break;
                }

                var grid = view.Grid;
                var unit = crew.Units[0];
                foreach (var candidate in crew.Units)
                    if (candidate.State == CrewUnitState.Idle)
                    {
                        unit = candidate;
                        break;
                    }

                var centre = unit.Cell;
                var ground = grid.GetSurfaceHeight(centre.x, centre.y);
                var removed = new List<MaterialVolume>();
                float lowest = float.MaxValue, highest = float.MinValue;
                for (var dz = -Outer - 1; dz <= Outer + 1; dz++)
                    for (var dx = -Outer - 1; dx <= Outer + 1; dx++)
                    {
                        var h = grid.GetSurfaceHeight(centre.x + dx, centre.y + dz);
                        lowest = Mathf.Min(lowest, h);
                        highest = Mathf.Max(highest, h);
                    }

                for (var dz = -Platform; dz <= Platform; dz++)
                    for (var dx = -Platform; dx <= Platform; dx++)
                    {
                        var inner = Mathf.Abs(dx) <= Inner && Mathf.Abs(dz) <= Inner;
                        var ring = Mathf.Abs(dx) <= Outer && Mathf.Abs(dz) <= Outer;
                        SetHeight(grid, centre.x + dx, centre.y + dz, ground - (inner ? 1f : ring ? 0.5f : 0f), removed);
                    }

                Debug.Log($"Crew flood: unit {unit.Id} ({unit.Role}, {unit.State}) at ({centre.x}, {centre.y}), ground {ground:0.00} m " +
                    $"(land round it {lowest:0.00}..{highest:0.00} m, levelled to {ground:0.00} m for {2 * Platform + 1}x{2 * Platform + 1} cells). Bowl: middle {2 * Inner + 1}x{2 * Inner + 1} cells at {ground - 1f:0.00} m, " +
                    $"ring to {2 * Outer + 1}x{2 * Outer + 1} at {ground - 0.5f:0.00} m. Spring {SpringRate} m³/s for {SpringSeconds} s.");

                rts.enabled = false;
                var camera = rts.GetComponent<Camera>();
                var middle = view.transform.TransformPoint(TerrainSpace.CellCentre(grid, centre.x, centre.y, ground - 1f));
                var pose = middle + new Vector3(-9f, 9f, -11f);
                var settle = Time.time + 4f;
                while (Time.time < settle)
                    yield return null;
                Shot(camera, "crew_flood_0_dry", pose, middle);
                var inBowl = new List<CrewUnit>();
                foreach (var member in crew.Units)
                    if (Mathf.Abs(member.Cell.x - centre.x) <= Outer && Mathf.Abs(member.Cell.y - centre.y) <= Outer)
                        inBowl.Add(member);
                var statuses = new Dictionary<CrewUnit, string>();

                var spring = new GameObject("Crew Flood Spring").AddComponent<WaterEffectorComponent>();
                spring.transform.position = middle;
                spring.Kind = WaterEffectorKind.Source;
                spring.Radius = 1.5f;
                spring.Rate = SpringRate;

                var log = new StringBuilder();
                var started = Time.time;
                var nextLine = 0f;
                var escapes = 0;
                var shotEscape = false;
                var lastJob = unit.Job;
                var outAt = -1f;
                while (Time.time - started < Timeout)
                {
                    var t = Time.time - started;
                    if (spring != null && t > SpringSeconds)
                    {
                        Destroy(spring.gameObject);
                        log.AppendLine($"  {t,5:0.00} s  spring off");
                    }

                    var cell = unit.Cell;
                    if (unit.Job != lastJob)
                    {
                        if (unit.Job == CrewJobKind.Escape)
                            escapes++;
                        lastJob = unit.Job;
                    }

                    foreach (var member in inBowl)
                    {
                        if (statuses.TryGetValue(member, out var was) && was == member.Status)
                            continue;
                        statuses[member] = member.Status;
                        var at = member.Cell;
                        log.AppendLine($"  {t,5:0.00} s  unit {member.Id} ({member.Role}) at ({at.x}, {at.y}), water {grid.WaterDepth(at.x, at.y):0.00} m: {member.Status}");
                    }

                    if (t >= nextLine)
                    {
                        nextLine += 0.5f;
                        var middleDepth = grid.WaterDepth(centre.x, centre.y);
                        log.AppendLine($"  {t,5:0.00} s  unit at ({cell.x}, {cell.y}) water under it {grid.WaterDepth(cell.x, cell.y):0.00} m " +
                            $"(middle {middleDepth:0.00} m, ring {grid.WaterDepth(centre.x + Outer, centre.y):0.00} m, land {grid.WaterDepth(centre.x + Outer + 2, centre.y):0.00} m) " +
                            $"passable {grid.IsPassableGround(cell.x, cell.y)}  {unit.State} / {unit.Job}");
                    }

                    if (!shotEscape && unit.Job == CrewJobKind.Escape)
                    {
                        shotEscape = true;
                        yield return Frames(12);
                        Shot(camera, "crew_flood_1_climbing_out", pose, middle);
                    }

                    if (escapes > 0 && unit.Job != CrewJobKind.Escape && outAt < 0f)
                    {
                        outAt = t;
                        Shot(camera, "crew_flood_2_out", pose, middle);
                    }

                    yield return null;
                }

                if (spring != null)
                    Destroy(spring.gameObject);
                var final = unit.Cell;
                Shot(camera, "crew_flood_3_end", pose, middle);
                Debug.Log($"Crew flood timeline:\n{log}");
                Debug.Log($"Crew flood: tracked unit {unit.Id}: {escapes} escape(s); out of the water at {(outAt < 0f ? "never" : outAt.ToString("0.00") + " s")}; " +
                    $"ends at ({final.x}, {final.y}) with {grid.WaterDepth(final.x, final.y):0.00} m of water, passable {grid.IsPassableGround(final.x, final.y)}, {unit.State}.");
                foreach (var member in inBowl)
                {
                    var at = member.Cell;
                    Debug.Log($"Crew flood: unit {member.Id} ({member.Role}) ends at ({at.x}, {at.y}) with {grid.WaterDepth(at.x, at.y):0.00} m of water, " +
                        $"passable {grid.IsPassableGround(at.x, at.y)}, {member.State}.");
                }
                rts.enabled = true;
                Debug.Log($"Crew flood capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            /// <summary>Digs the cell down, or builds it up in rock, to <paramref name="height"/>.</summary>
            static void SetHeight(TerrainGrid grid, int x, int z, float height, List<MaterialVolume> removed)
            {
                if (!grid.InBounds(x, z) || grid.IsVoid(x, z))
                    return;
                var gap = grid.GetSurfaceHeight(x, z) - height;
                if (gap > 0.01f)
                    grid.Remove(x, z, gap, removed, bulk: false);
                else if (gap < -0.01f)
                    grid.Add(x, z, MaterialTable.Rock, -gap);
            }

            static IEnumerator Frames(int n)
            {
                for (var i = 0; i < n; i++)
                    yield return null;
            }

            static void Shot(Camera camera, string name, Vector3 position, Vector3 target)
            {
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
