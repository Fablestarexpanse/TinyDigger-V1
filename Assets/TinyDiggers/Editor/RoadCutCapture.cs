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
    /// A long straight road driven **level** through a rise, with a big quarry to fill from and a
    /// big tip to spoil onto (Ronan, 2026-09-22: "a big long road section cutting through a hill
    /// straight not sloped with a large quarry and a large dump site").
    ///
    /// Level is the point. A road that follows the ground has no cut face and no surplus, so there
    /// is nothing to watch: held at one height it has to be cut out of the hill in the middle and
    /// built up at the ends, which is where the material comes from and goes to. What is being
    /// judged here is **how the dirt behaves** — how it comes off the face and how it lands on the
    /// heap — rather than whether the job finishes, so the shots are taken low and close rather
    /// than from the usual survey angle.
    ///
    /// Play mode only. Shots go to Screenshots/RoadCut.
    /// </summary>
    static class RoadCutCapture
    {
        const string Folder = "Screenshots/RoadCut";
        const float Speed = 20f;

        /// <summary>Cells either side of the crest: the road is twice this long.</summary>
        const int Reach = 20;

        /// <summary>
        /// The rise to look for, and the band it has to fall in. A cutting, not a canyon: the
        /// first run took the biggest rise it could find, which was a twenty-metre rock mountain.
        /// </summary>
        const float WantRise = 3f;
        const float MinRise = 1.5f;
        const float MaxRise = 6f;

        /// <summary>Game seconds to let the crew work, and how long without progress before giving up.</summary>
        const float Cap = 4000f;
        const float Patience = 600f;

        [MenuItem("TinyDiggers/Road Cut Capture")]
        static void Run()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Road cut capture: enter play mode first.");
                return;
            }

            new GameObject("RoadCutCapture") { hideFlags = HideFlags.DontSave }.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            TerrainView _view;
            PlayerTools _tools;
            CrewView _crew;
            RoadsHost _roads;
            RtsCamera _rts;
            Camera _camera;
            TerrainGrid _grid;
            DesignationMap _map;

            IEnumerator Start()
            {
                Directory.CreateDirectory(Folder);
                Application.runInBackground = true;
                _view = FindAnyObjectByType<TerrainView>();
                _tools = FindAnyObjectByType<PlayerTools>();
                _crew = FindAnyObjectByType<CrewView>();
                _roads = FindAnyObjectByType<RoadsHost>();
                _rts = FindAnyObjectByType<RtsCamera>();
                _camera = _rts != null ? _rts.GetComponent<Camera>() : Camera.main;
                while (_view.Grid == null || _crew.Units.Count == 0 || _tools.Map == null || _roads == null)
                    yield return null;
                yield return Frames(20);

                _grid = _view.Grid;
                _map = _tools.Map;
                if (_rts != null) _rts.enabled = false;

                var hill = FindTheHill(_crew.Units[0].Cell);
                if (hill.rise < MinRise)
                {
                    Debug.LogWarning($"Road cut: no rise worth cutting near the crew — best was "
                                     + $"{hill.rise:0.##} m at ({hill.crest.x}, {hill.crest.y}).");
                    Destroy(gameObject);
                    yield break;
                }

                var along = hill.along;
                var side = new Vector2Int(-along.y, along.x);
                var from = new Vector2(hill.crest.x - along.x * Reach + 0.5f, hill.crest.y - along.y * Reach + 0.5f);
                var to = new Vector2(hill.crest.x + along.x * Reach + 0.5f, hill.crest.y + along.y * Reach + 0.5f);

                // A big tip well to one side of the line, and a big quarry to the other: the road
                // wants both, and they have to be clear of the corridor or the crew tips into its
                // own cut.
                var tipAt = new Vector2Int(hill.crest.x + side.x * 14, hill.crest.y + side.y * 14);
                var dump = 0;
                for (var dz = -7; dz <= 7; dz++)
                    for (var dx = -7; dx <= 7; dx++)
                        if (_map.SetDumpZone(tipAt.x + dx, tipAt.y + dz, true))
                            dump++;

                var quarryAt = new Vector2Int(hill.crest.x - side.x * 14, hill.crest.y - side.y * 14);
                var quarryFloor = _grid.GetSurfaceHeight(quarryAt.x, quarryAt.y) - 3f;
                var quarry = 0;
                for (var dz = -6; dz <= 6; dz++)
                    for (var dx = -6; dx <= 6; dx++)
                        if (_map.SetQuarry(quarryAt.x + dx, quarryAt.y + dz, true, quarryFloor))
                            quarry++;

                // Level, not sloped: both ends are placed at the height of the lower one, so the
                // line runs flat and the hill has to come out of the middle of it.
                var level = hill.foot;
                _roads.Draft.Clear();
                _roads.Draft.Snap45 = false;
                _roads.Draft.Width = 5;
                _roads.Draft.Place(from, _ => level);
                _roads.Draft.Place(to, _ => level);
                var road = _roads.Draft.Commit(_roads.Network, _grid.CellSize);
                yield return Frames(6);

                Debug.Log($"Road cut: road {road}, {2 * Reach} cells of it at width {_roads.Draft.Width}, "
                          + $"held level at {level:0.##} m through a {hill.rise:0.##} m rise at "
                          + $"({hill.crest.x}, {hill.crest.y}); cut {_roads.Cut:0.#} m³, fill "
                          + $"{_roads.Fill:0.#} m³; {_map.Count} designations, {dump} tip cells, "
                          + $"{quarry} quarry cells down to {quarryFloor:0.##} m.");

                LookAlong(hill.crest, along, 34f, 0.28f);
                yield return Frames(6);
                yield return Shoot("cut_marked");

                // Partway through, low and close over the tip: this is the shot that says whether
                // the dirt looks like it is being dumped or like it is being teleported.
                yield return Work(Cap * 0.35f, Patience);
                Look(tipAt, 7f, 0.28f);
                yield return Frames(6);
                yield return Shoot("cut_tipping");

                LookAlong(hill.crest, along, 14f, 0.22f);
                yield return Frames(6);
                yield return Shoot("cut_face");

                yield return Work(Cap, Patience);
                LookAlong(hill.crest, along, 34f, 0.28f);
                yield return Frames(6);
                yield return Shoot("cut_done");
                Look(tipAt, 12f, 0.35f);
                yield return Frames(6);
                yield return Shoot("cut_heap");

                Debug.Log($"Road cut: {_map.Count} designations left ({_map.AutoCount} auto ramp); "
                          + $"the heap stands {Heap(tipAt):0.##} m at its highest. " + Roll());

                if (_rts != null) _rts.enabled = true;
                Debug.Log("Road cut capture: done.");
                Destroy(gameObject);
            }

            /// <summary>
            /// The best rise near the crew to drive a line through: the cell standing highest over
            /// the dry ground <see cref="Reach"/> cells away on both sides.
            /// </summary>
            (Vector2Int crest, Vector2Int along, float rise, float foot) FindTheHill(Vector2Int near)
            {
                var best = (crest: near, along: new Vector2Int(1, 0), rise: 0f, foot: 0f);
                var bestMiss = float.MaxValue;
                var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
                for (var z = near.y - 40; z <= near.y + 40; z += 2)
                    for (var x = near.x - 40; x <= near.x + 40; x += 2)
                    {
                        // Soil, not stone: this trial is about how dirt behaves, and a rock face
                        // is a different question with a different answer.
                        if (!Dry(new Vector2Int(x, z), 3) || MaterialTable.IsStone(_grid.GetTopMaterial(x, z)))
                            continue;
                        var crest = _grid.GetSurfaceHeight(x, z);
                        foreach (var dir in dirs)
                        {
                            var a = new Vector2Int(x + dir.x * Reach, z + dir.y * Reach);
                            var b = new Vector2Int(x - dir.x * Reach, z - dir.y * Reach);
                            var side = new Vector2Int(-dir.y, dir.x);
                            if (!Dry(a, 4) || !Dry(b, 4)
                                || !Dry(new Vector2Int(x + side.x * 14, z + side.y * 14), 8)
                                || !Dry(new Vector2Int(x - side.x * 14, z - side.y * 14), 7))
                                continue;
                            var foot = Mathf.Min(_grid.GetSurfaceHeight(a.x, a.y), _grid.GetSurfaceHeight(b.x, b.y));
                            var rise = crest - foot;
                            // A believable cutting, not a canyon. Taking the biggest rise it could
                            // find picked a twenty-metre rock mountain and marked a road across
                            // the top of it — true to the letter and no use at all. Aim for a
                            // rise a crew could plausibly cut through in an afternoon.
                            if (rise < MinRise || rise > MaxRise)
                                continue;
                            var miss = Mathf.Abs(rise - WantRise);
                            if (miss >= bestMiss)
                                continue;
                            bestMiss = miss;
                            best = (new Vector2Int(x, z), dir, rise, foot);
                        }
                    }

                return best;
            }

            bool Dry(Vector2Int at, int room)
            {
                for (var dz = -room; dz <= room; dz++)
                    for (var dx = -room; dx <= room; dx++)
                        if (!_grid.IsGround(at.x + dx, at.y + dz) || _grid.IsWater(at.x + dx, at.y + dz))
                            return false;
                return true;
            }

            /// <summary>How high the spoil stands over the ground it was tipped onto.</summary>
            float Heap(Vector2Int at)
            {
                var highest = float.MinValue;
                var lowest = float.MaxValue;
                for (var dz = -8; dz <= 8; dz++)
                    for (var dx = -8; dx <= 8; dx++)
                    {
                        if (!_grid.IsGround(at.x + dx, at.y + dz))
                            continue;
                        var height = _grid.GetSurfaceHeight(at.x + dx, at.y + dz);
                        highest = Mathf.Max(highest, height);
                        lowest = Mathf.Min(lowest, height);
                    }

                return highest - lowest;
            }

            string Roll()
            {
                var roll = "";
                foreach (var unit in _crew.Units)
                    roll += $"[{UnitNames.Of(unit.Role)} {unit.Id}: {unit.Status}] ";
                return roll;
            }

            /// <summary>
            /// Runs until the designations are gone, progress stops, or the cap — in **game**
            /// seconds, because an editor behind another window ticks a few frames a second and a
            /// budget in real time silently shrinks to a fraction of what it says.
            /// </summary>
            IEnumerator Work(float cap, float patience)
            {
                Time.timeScale = Speed;
                Time.maximumDeltaTime = 4f;
                var clock = Time.time;
                var mark = clock;
                var best = _map.Count;
                while (Time.time - clock < cap && _map.Count > 0)
                {
                    if (_map.Count < best)
                    {
                        best = _map.Count;
                        mark = Time.time;
                    }
                    else if (Time.time - mark > patience)
                    {
                        break;
                    }

                    yield return null;
                }

                Time.timeScale = 1f;
                Time.maximumDeltaTime = 1f / 3f;
                yield return Frames(4);
            }

            /// <summary>
            /// Looks down the line of the road from beyond one end, which is the only angle a
            /// cutting reads from: across it, the cut face is edge-on and invisible.
            /// </summary>
            void LookAlong(Vector2Int at, Vector2Int along, float back, float lift)
            {
                var target = _view.transform.TransformPoint(new Vector3(
                    (at.x + 0.5f) * _grid.CellSize, _grid.GetSurfaceHeight(at.x, at.y),
                    (at.y + 0.5f) * _grid.CellSize));
                var step = _grid.CellSize * back;
                var eye = target + new Vector3(-along.x * step, step * lift, -along.y * step);
                _camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye));
            }

            /// <summary>Puts the camera <paramref name="back"/> metres off, <paramref name="lift"/> of that up.</summary>
            void Look(Vector2Int at, float back, float lift)
            {
                var target = _view.transform.TransformPoint(new Vector3(
                    (at.x + 0.5f) * _grid.CellSize, _grid.GetSurfaceHeight(at.x, at.y),
                    (at.y + 0.5f) * _grid.CellSize));
                var eye = target + new Vector3(-back, back * lift, -back);
                _camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye));
            }

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
