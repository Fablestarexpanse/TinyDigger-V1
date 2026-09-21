using System.Collections;
using System.Collections.Generic;
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
    /// The Slice 10 (ore underground) evidence run on Continent seed 11:
    /// - the disc with the ore view off, then on;
    /// - a close look at deposits with the ore view on;
    /// - a trench cut by hand through a coal seam, so the band shows on the face;
    /// - the crew digging into the nearest iron lens, and what the mining ledger says afterwards.
    /// Play mode only.
    /// </summary>
    static class Slice10Capture
    {
        const string Folder = "Screenshots/Slice10";

        [MenuItem("TinyDiggers/Slice 10 Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Slice 10 capture: enter play mode first.");
                return;
            }

            new GameObject("Slice10Capture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
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
                if (view == null || camera == null || ore == null)
                {
                    Debug.LogError("Slice 10 capture: needs a TerrainView, an RtsCamera and an OreOverlayView.");
                    Destroy(gameObject);
                    yield break;
                }

                Directory.CreateDirectory(Folder);
                view.Settings.Shape = LandShape.Continent;
                view.Regenerate(11);
                yield return Frames(40);
                var grid = view.Grid;
                var survey = ore.Survey;
                Debug.Log($"Slice 10: land generated in {view.Island.Milliseconds:0} ms, {survey.CellsWithOre} cells with ore within {survey.Depth:0} m.");

                ore.Visible = false;
                camera.GoHome();
                yield return Frames(60);
                Capture(camera, "disc_plain", "the disc, ore view off");
                ore.Visible = true;
                yield return Frames(5);
                Capture(camera, "disc_ore", "the disc, ore view on (F7)");

                var iron = Nearest(grid, survey, MaterialTable.IronOre, new Vector2Int(grid.Width / 2, grid.Height / 2));
                yield return Pose(camera, new Vector3(iron.x, grid.GetSurfaceHeight(iron.x, iron.y), iron.y), 110f, 35f, 45f);
                Capture(camera, "ore_close", "deposits close up, ore view on");
                ore.Visible = false;

                // A pit dug by hand into the shallowest coal, so its floor is the seam. (A seam does
                // not show as a band on a pit wall: the shader colours a face by the column's top
                // and next layer, not by the strata at that height.)
                var coal = ShallowestOf(grid, survey, MaterialTable.Coal);
                var coalFind = survey.At(coal.x, coal.y);
                var cutTo = CutPit(grid, coal, grid.GetSurfaceHeight(coal.x, coal.y) - coalFind.DepthToTop - 0.5f);
                yield return Frames(10);
                Debug.Log($"Slice 10: coal pit at ({coal.x}, {coal.y}), seam top {coalFind.DepthToTop:0.0} m down, floor now {grid.GetTopMaterial(coal.x, coal.y).Value}.");
                yield return Pose(camera, new Vector3(coal.x, cutTo, coal.y), 45f, 30f, 55f);
                Capture(camera, "coal_pit", "a pit dug into the coal seam; its floor is coal");

                // The crew into an iron lens: a 4 x 4 dig down to the ore's top, run fast. The lens
                // is picked well clear of where the crew stands, so they have room to ramp down
                // (on top of them, the first try left nowhere to stand beside the pit).
                if (crew != null && crew.Dispatcher != null && crew.Units.Count > 0)
                {
                    var home = crew.Units[0].Cell;
                    iron = ShallowestNear(grid, survey, MaterialTable.IronOre, home, 15, 70);
                    var find = survey.At(iron.x, iron.y);
                    var target = grid.GetSurfaceHeight(iron.x, iron.y) - find.DepthToTop - 1f;
                    var map = crew.Dispatcher.Designations;
                    // 10 x 10: a 4 x 4 pit several metres deep left the auto ramp nowhere to stand.
                    for (var dz = -5; dz < 5; dz++)
                        for (var dx = -5; dx < 5; dx++)
                            map.Designate(iron.x + dx, iron.y + dz, DesignationKind.Dig, target);
                    var dump = FindDumpSpot(grid, iron);
                    for (var dz = 0; dz < 6; dz++)
                        for (var dx = 0; dx < 6; dx++)
                            map.SetDumpZone(dump.x + dx, dump.y + dz, true, grid.GetSurfaceHeight(dump.x, dump.y) + 3f);
                    Time.timeScale = 16f;
                    var until = Time.realtimeSinceStartup + 150f;
                    while (Time.realtimeSinceStartup < until && crew.Dispatcher.Ledger.Dug(MaterialTable.IronOre) < 2f)
                        yield return null;
                    Time.timeScale = 1f;
                    Debug.Log($"Slice 10: crew at iron ({iron.x}, {iron.y}), ore top {find.DepthToTop:0.0} m down, target {target:0.0} m. Ledger: '{crew.Dispatcher.Ledger.OreSummary(grid.Materials)}'. Designations left {map.Count}.");
                    yield return Pose(camera, new Vector3(iron.x + 2, grid.GetSurfaceHeight(iron.x + 2, iron.y + 2), iron.y + 2), 40f, 35f, 38f);
                    Capture(camera, "iron_dig", "the crew's pit into the iron lens");
                }

                Debug.Log($"Slice 10 capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static Vector2Int Nearest(TerrainGrid grid, OreSurvey survey, MaterialId ore, Vector2Int from, int minDistance = 0)
            {
                var best = from;
                var bestDistance = float.MaxValue;
                for (var z = 0; z < grid.Height; z++)
                    for (var x = 0; x < grid.Width; x++)
                    {
                        var find = survey.At(x, z);
                        if (find.Ore != ore || find.Thickness < 1.5f || grid.GetTopMaterial(x, z) != MaterialTable.Topsoil)
                            continue;
                        var d = (new Vector2Int(x, z) - from).sqrMagnitude;
                        if (d < minDistance * minDistance)
                            continue;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = new Vector2Int(x, z);
                        }
                    }
                return best;
            }

            static Vector2Int ShallowestNear(TerrainGrid grid, OreSurvey survey, MaterialId ore, Vector2Int from, int minDistance, int maxDistance)
            {
                var best = from;
                var bestDepth = float.MaxValue;
                for (var z = 6; z < grid.Height - 6; z++)
                    for (var x = 6; x < grid.Width - 6; x++)
                    {
                        var d = (new Vector2Int(x, z) - from).magnitude;
                        if (d < minDistance || d > maxDistance)
                            continue;
                        var find = survey.At(x, z);
                        if (find.Ore == ore && find.Thickness >= 1.5f && find.DepthToTop < bestDepth
                            && grid.GetTopMaterial(x, z) == MaterialTable.Topsoil)
                        {
                            bestDepth = find.DepthToTop;
                            best = new Vector2Int(x, z);
                        }
                    }
                return best;
            }

            static Vector2Int ShallowestOf(TerrainGrid grid, OreSurvey survey, MaterialId ore)
            {
                var best = new Vector2Int(grid.Width / 2, grid.Height / 2);
                var bestDepth = float.MaxValue;
                for (var z = 8; z < grid.Height - 8; z++)
                    for (var x = 8; x < grid.Width - 8; x++)
                    {
                        var find = survey.At(x, z);
                        // On bare stone, so the pit's walls stand instead of slumping onto the seam.
                        if (find.Ore == ore && find.Thickness >= 1.5f && find.DepthToTop < bestDepth && grid.GetSurfaceHeight(x, z) > 2f
                            && MaterialTable.IsStone(grid.GetTopMaterial(x, z)))
                        {
                            bestDepth = find.DepthToTop;
                            best = new Vector2Int(x, z);
                        }
                    }
                return best;
            }

            /// <summary>Removes an 8 x 8 pit down to <paramref name="floor"/>; returns the floor height.</summary>
            static float CutPit(TerrainGrid grid, Vector2Int at, float floor)
            {
                var removed = new List<MaterialVolume>();
                for (var dz = -4; dz < 4; dz++)
                    for (var dx = -4; dx < 4; dx++)
                    {
                        var x = at.x + dx;
                        var z = at.y + dz;
                        if (!grid.InBounds(x, z) || grid.IsVoid(x, z))
                            continue;
                        var depth = grid.GetSurfaceHeight(x, z) - floor;
                        if (depth > 0f)
                            grid.Remove(x, z, depth, removed, bulk: false);
                    }
                return floor;
            }

            static Vector2Int FindDumpSpot(TerrainGrid grid, Vector2Int near)
            {
                for (var r = 8; r < 60; r += 4)
                    foreach (var (dx, dz) in new[] { (r, 0), (-r, 0), (0, r), (0, -r) })
                    {
                        var x = near.x + dx;
                        var z = near.y + dz;
                        if (grid.InBounds(x + 6, z + 6) && grid.InBounds(x, z) && grid.GetTopMaterial(x, z) == MaterialTable.Topsoil)
                            return new Vector2Int(x, z);
                    }
                return near + new Vector2Int(10, 0);
            }

            IEnumerator Pose(RtsCamera camera, Vector3 pivot, float distance, float yaw, float pitch)
            {
                camera.Rig.Pivot = pivot;
                camera.Rig.Distance = distance;
                camera.Rig.Yaw = yaw;
                camera.Rig.SetPitch(pitch);
                yield return Frames(50);
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
                Debug.Log($"Slice 10 capture: {name} — {what}");
            }
        }
    }
}
