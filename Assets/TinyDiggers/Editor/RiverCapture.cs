using System.Collections;
using System.IO;
using PromptWaffle.DynamicWater;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Rivers and creeks, step 2 evidence: the channels running with simulated water from their
    /// springs. Lets the water settle, then logs, per channel, how much of its bed over land is
    /// wet and how much is too deep for the crew to wade. Then shoots the island from above and
    /// a river and a creek up close. Play mode only; logs "River capture: done" at the end.
    /// </summary>
    static class RiverCapture
    {
        const string Folder = "Screenshots/Rivers";

        /// <summary>Seconds of play the water gets to settle before anything is measured.</summary>
        const float Settle = 90f;

        [MenuItem("TinyDiggers/River Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("River capture: enter play mode first.");
                return;
            }

            new GameObject("RiverCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                var view = FindAnyObjectByType<TerrainView>();
                var zone = FindAnyObjectByType<WaterZone>();
                var rts = FindAnyObjectByType<RtsCamera>();
                Directory.CreateDirectory(Folder);
                // From when the capture starts, not from the level: play mode's clock has been seen
                // already a minute and a half in on entering.
                var started = Time.realtimeSinceStartup;
                while (zone.Simulation == null || Time.realtimeSinceStartup - started < Settle)
                    yield return null;

                var grid = view.Grid;
                var island = view.Island;
                foreach (var channel in island.Channels)
                    Debug.Log("River capture: " + Measure(grid, channel));

                rts.enabled = false;
                var camera = rts.GetComponent<Camera>();
                var centre = view.transform.TransformPoint(new Vector3(view.DiscCentre.x, 0f, view.DiscCentre.y));
                yield return Frames(10);
                Shot(camera, "rivers_wide", centre + new Vector3(0f, 380f, -300f), centre);

                var river = island.Channels.Find(c => c.Kind == ChannelKind.River && c.Length > 100f)
                    ?? island.Channels.Find(c => c.Kind == ChannelKind.River);
                var creek = island.Channels.Find(c => c.Kind == ChannelKind.Creek && c.Length > 80f)
                    ?? island.Channels.Find(c => c.Kind == ChannelKind.Creek);
                if (river != null)
                    yield return Close(camera, view, grid, river, "river_close", 0.75f, 45f);
                if (creek != null)
                    yield return Close(camera, view, grid, creek, "creek_close", 0.6f, 28f);

                rts.enabled = true;
                Debug.Log($"River capture: done. Shots in {Path.GetFullPath(Folder)}");
                Destroy(gameObject);
            }

            static string Measure(TerrainGrid grid, Channel channel)
            {
                int land = 0, wet = 0, deep = 0;
                var deepest = 0f;
                foreach (var point in channel.Path)
                {
                    var x = Mathf.FloorToInt(point.x);
                    var z = Mathf.FloorToInt(point.z);
                    if (grid.GetSurfaceHeight(x, z) < World.SeaLevel)
                        continue;
                    land++;
                    var depth = grid.WaterDepth(x, z);
                    deepest = Mathf.Max(deepest, depth);
                    if (depth > 0.05f)
                        wet++;
                    if (depth >= grid.DeepWater)
                        deep++;
                }

                float Share(int n) => land > 0 ? n * 100f / land : 0f;
                return $"{channel.Kind} {channel.Width:0.0} m wide, {channel.Length:0} m long: wet {Share(wet):0}%, too deep to wade {Share(deep):0}%, deepest {deepest:0.00} m";
            }

            /// <summary>Looks along the channel at a point <paramref name="along"/> of the way down it, from the side and above.</summary>
            static IEnumerator Close(Camera camera, TerrainView view, TerrainGrid grid, Channel channel, string name, float along, float distance)
            {
                var path = channel.Path;
                var i = Mathf.Clamp(Mathf.RoundToInt(path.Count * along), 1, path.Count - 2);
                Vector3 ToWorld(Vector3 p) => view.transform.TransformPoint(new Vector3(p.x * grid.CellSize, p.y, p.z * grid.CellSize));
                var at = ToWorld(path[i]);
                var direction = ToWorld(path[i + 1]) - ToWorld(path[i - 1]);
                direction.y = 0f;
                direction.Normalize();
                var side = new Vector3(-direction.z, 0f, direction.x);
                var pose = at - direction * distance * 0.7f + side * distance * 0.5f + Vector3.up * distance * 1.1f;
                yield return Frames(4);
                Shot(camera, name, pose, at);
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
