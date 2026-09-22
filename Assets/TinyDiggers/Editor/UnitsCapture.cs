using System.Collections;
using System.IO;
using TinyDiggers.Interaction;
using TinyDiggers.Presentation;
using TinyDiggers.Units;
using UnityEngine;
using UnityEditor;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// A picture of the crew with its machines: the starter robots, the digger and the dumper, all
    /// at their proper sizes, taken from play so the models, clips and terrain are the real ones.
    ///
    /// The camera is placed here rather than driven, because the live RTS camera drifts with
    /// whatever the mouse last did and the capture comes out somewhere else each run.
    /// </summary>
    public static class UnitsCapture
    {
        const string Folder = "Screenshots/Units";

        [MenuItem("TinyDiggers/Units Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Units capture: enter play mode first.");
                return;
            }

            new GameObject("UnitsCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            IEnumerator Start()
            {
                var crew = FindAnyObjectByType<CrewView>();
                var terrain = FindAnyObjectByType<TerrainView>();
                var rts = FindAnyObjectByType<RtsCamera>();
                Directory.CreateDirectory(Folder);

                var until = Time.realtimeSinceStartup + 20f;
                while ((crew == null || crew.Units.Count == 0) && Time.realtimeSinceStartup < until)
                {
                    crew = FindAnyObjectByType<CrewView>();
                    yield return null;
                }

                if (crew == null || crew.Units.Count == 0)
                {
                    Debug.LogError("Units capture: no crew after 20 s.");
                    Destroy(gameObject);
                    yield break;
                }

                // Let them settle into a pose and the terrain finish building.
                for (var frame = 0; frame < 120; frame++)
                    yield return null;

                if (rts != null)
                    rts.enabled = false;

                var middle = Vector3.zero;
                foreach (var unit in crew.Units)
                {
                    var cell = terrain.Grid.CellSize;
                    middle += terrain.transform.TransformPoint(
                        new Vector3(unit.Position.x * cell, unit.Height, unit.Position.y * cell));
                }
                middle /= crew.Units.Count;

                var camera = Camera.main;
                camera.transform.position = middle + new Vector3(4.2f, 3.0f, -4.2f);
                camera.transform.LookAt(middle + Vector3.up * 0.4f);
                camera.fieldOfView = 32f;

                yield return new WaitForEndOfFrame();
                var path = Path.Combine(Folder, "units_in_game.png");
                ScreenCapture.CaptureScreenshot(path, 1);
                Debug.Log($"Units capture: {crew.Units.Count} units, written to {path}");
                Destroy(gameObject);
            }
        }
    }
}
