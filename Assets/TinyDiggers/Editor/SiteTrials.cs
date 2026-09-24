using System.Collections;
using System.Collections.Generic;
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
    /// A bench of digging trials, run one after another in a single play session: a deep pit that
    /// wants a ramp, a cut through a hill, a patch smoothed flat, and what a tipped load leaves
    /// behind. Each one says in the log what it measured, so a change can be judged by numbers
    /// rather than by how it looked.
    ///
    /// They run on the live island rather than a made-up grid, because most of what has gone wrong
    /// here has been the world's doing — wet ground, rock where soil was expected, a spawn boxed
    /// in — and none of that shows up on a flat test fixture.
    ///
    /// A trial ends when the work is done or when it stops making progress, never on a stopwatch
    /// alone: the first run of trial A reported a failure that was only the clock running out
    /// while the crew was still digging happily.
    /// </summary>
    static class SiteTrials
    {
        const string Folder = "Screenshots/Trials";
        const float Speed = 20f;

        /// <summary>
        /// Game seconds of no measurable progress before a trial is called stalled.
        ///
        /// Game seconds, not real ones. An unfocused editor ticks at a few frames a second, so
        /// <see cref="Time.timeScale"/> of twenty buys twenty times a *frame*, not twenty times a
        /// second, and a budget in real time silently shrinks to a tenth of what it says. That is
        /// what made the crew look fifteen times too slow: the clock was wrong, not the crew.
        /// </summary>
        const float StallSeconds = 400f;

        /// <summary>Game seconds any one trial may run, however well it is going.</summary>
        const float CapSeconds = 4000f;

        /// <summary>Game seconds between progress lines in the log.</summary>
        const float TraceSeconds = 500f;

        [MenuItem("TinyDiggers/Site Trials")]
        static void Run() => Launch(Trial.All);

        [MenuItem("TinyDiggers/Site Trial A (deep pit)")]
        static void RunA() => Launch(Trial.DeepPit);

        [MenuItem("TinyDiggers/Site Trial B (cut a hill)")]
        static void RunB() => Launch(Trial.CutHill);

        [MenuItem("TinyDiggers/Site Trial C (smooth)")]
        static void RunC() => Launch(Trial.Smooth);

        static void Launch(Trial which)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Site trials: enter play mode first.");
                return;
            }

            var host = new GameObject("SiteTrials") { hideFlags = HideFlags.DontSave };
            host.AddComponent<Runner>().Which = which;
        }

        internal enum Trial { All, DeepPit, CutHill, Smooth }

        sealed class Runner : MonoBehaviour
        {
            internal Trial Which;

            TerrainView _view;
            PlayerTools _tools;
            CrewView _crew;
            RtsCamera _rts;
            Camera _camera;
            TerrainGrid _grid;
            DesignationMap _map;

            /// <summary>Game seconds the crew has spent in each state during the trial in hand.</summary>
            readonly Dictionary<CrewUnitState, float> _spent = new Dictionary<CrewUnitState, float>();

            IEnumerator Start()
            {
                Directory.CreateDirectory(Folder);
                // Without this the editor drops to a few frames a second the moment it loses
                // focus, and every trial's clock quietly runs at a tenth of what it says.
                Application.runInBackground = true;
                _view = FindAnyObjectByType<TerrainView>();
                _tools = FindAnyObjectByType<PlayerTools>();
                _crew = FindAnyObjectByType<CrewView>();
                // The trials drive the crew themselves and have no worksite to assign them to.
                if (_crew != null)
                    _crew.NeedWorksite = false;
                _rts = FindAnyObjectByType<RtsCamera>();
                _camera = _rts != null ? _rts.GetComponent<Camera>() : Camera.main;
                while (_view.Grid == null || _crew.Units.Count == 0 || _tools.Map == null)
                    yield return null;
                yield return Frames(20);

                _grid = _view.Grid;
                _map = _tools.Map;
                if (_rts != null) _rts.enabled = false;

                if (Which == Trial.All || Which == Trial.DeepPit)
                    yield return DeepPit();
                if (Which == Trial.All || Which == Trial.CutHill)
                    yield return CutHill();
                if (Which == Trial.All || Which == Trial.Smooth)
                    yield return Smooth();

                if (_rts != null) _rts.enabled = true;
                Debug.Log("Site trials: done.");
                Destroy(gameObject);
            }

            // --- the trials --------------------------------------------------------------------

            /// <summary>
            /// Trial A. A pit six steps deep, past the reach of anything on the crew: the last
            /// steps can only be cut from inside, so this says whether a way down gets made.
            /// </summary>
            IEnumerator DeepPit()
            {
                _map.ClearAll();
                var site = Flat(_crew.Units[0].Cell, 6);
                var ground = _grid.GetSurfaceHeight(site.x, site.y);
                var floor = ground - 6f * _grid.HeightStep;

                var marked = Mark(site, 3, DesignationKind.Dig, floor);
                DumpZone(new Vector2Int(site.x + 9, site.y), 2);

                Debug.Log($"Trial A (deep pit): {marked} cells at ({site.x}, {site.y}) from "
                          + $"{ground:0.##} m down to {floor:0.##} m — six steps, where a robot "
                          + $"reaches two and the mech four. " + Ground(site));

                Look(site, 10f);
                yield return Frames(6);
                yield return Shoot("A_pit_marked");

                var before = Depth(site, 3, floor);
                yield return Work("A");
                var after = Depth(site, 3, floor);

                Debug.Log($"Trial A: {_map.Count} designations left ({_map.AutoCount} auto ramp); "
                          + $"the pit went from {before:0.##} m to {after:0.##} m above its floor. "
                          + Roll());
                yield return Shoot("A_pit_worked");
            }

            /// <summary>
            /// Trial B. A corridor driven through a rise, floor level with the ground either side:
            /// the classic road cutting. It asks whether a cut that has to be worked from both
            /// ends completes, or stalls once the easy shoulders are off.
            /// </summary>
            IEnumerator CutHill()
            {
                _map.ClearAll();
                var hill = Rise(_crew.Units[0].Cell, 8);
                if (hill.height < 1f)
                {
                    Debug.LogWarning($"Trial B: no rise worth cutting near the crew (best was "
                                     + $"{hill.height:0.##} m). Skipped.");
                    yield break;
                }

                // Floor the cut at the ground on the low side, so it is a through-cut, not a pit.
                var floor = hill.foot;
                var marked = 0;
                for (var i = -hill.run; i <= hill.run; i++)
                    for (var w = -1; w <= 1; w++)
                    {
                        var x = hill.centre.x + i * hill.along.x + w * hill.along.y;
                        var z = hill.centre.y + i * hill.along.y - w * hill.along.x;
                        if (_map.Designate(x, z, DesignationKind.Dig, floor))
                            marked++;
                    }

                DumpZone(new Vector2Int(hill.centre.x + hill.along.y * 7, hill.centre.y - hill.along.x * 7), 3);

                Debug.Log($"Trial B (cut a hill): {marked} cells through a {hill.height:0.##} m rise "
                          + $"at ({hill.centre.x}, {hill.centre.y}), floored at {floor:0.##} m.");

                Look(hill.centre, 14f);
                yield return Frames(6);
                yield return Shoot("B_cut_marked");

                var before = Depth(hill.centre, hill.run, floor);
                yield return Work("B");
                var after = Depth(hill.centre, hill.run, floor);

                Debug.Log($"Trial B: {_map.Count} designations left ({_map.AutoCount} auto ramp); "
                          + $"the cut went from {before:0.##} m to {after:0.##} m of standing ground. "
                          + Roll());
                yield return Shoot("B_cut_worked");
            }

            /// <summary>
            /// Trial C. A bumpy patch levelled to its own middle height: half the cells want
            /// cutting and half want filling, and the spoil from one should feed the other. It
            /// asks how flat flat ends up, and whether the job terminates at all.
            /// </summary>
            IEnumerator Smooth()
            {
                _map.ClearAll();
                var site = Bumpy(_crew.Units[0].Cell, 4);
                var level = Median(site, 4);
                var step = _grid.HeightStep;
                level = Mathf.Round(level / step) * step;

                var cut = 0;
                var fill = 0;
                for (var z = site.y - 4; z <= site.y + 4; z++)
                    for (var x = site.x - 4; x <= site.x + 4; x++)
                    {
                        var here = _grid.GetSurfaceHeight(x, z);
                        if (here > level + step * 0.5f && _map.Designate(x, z, DesignationKind.Dig, level))
                            cut++;
                        else if (here < level - step * 0.5f && _map.Designate(x, z, DesignationKind.Fill, level))
                            fill++;
                    }

                Debug.Log($"Trial C (smoothing): level {level:0.##} m at ({site.x}, {site.y}) — "
                          + $"{cut} cells to cut, {fill} to fill. Rough before: "
                          + $"{Rough(site, 4, level):0.###} m mean error.");

                Look(site, 10f);
                yield return Frames(6);
                yield return Shoot("C_smooth_marked");

                yield return Work("C");

                Debug.Log($"Trial C: {_map.Count} designations left ({_map.AutoCount} auto); rough "
                          + $"after: {Rough(site, 4, level):0.###} m mean error. " + Roll());
                yield return Shoot("C_smooth_worked");
            }

            // --- marking -----------------------------------------------------------------------

            int Mark(Vector2Int centre, int radius, DesignationKind kind, float height)
            {
                var marked = 0;
                for (var z = centre.y - radius; z <= centre.y + radius; z++)
                    for (var x = centre.x - radius; x <= centre.x + radius; x++)
                        if (_map.Designate(x, z, kind, height))
                            marked++;
                return marked;
            }

            void DumpZone(Vector2Int centre, int radius)
            {
                for (var z = centre.y - radius; z <= centre.y + radius; z++)
                    for (var x = centre.x - radius; x <= centre.x + radius; x++)
                        _map.SetDumpZone(x, z, true);
            }

            // --- reading the ground -------------------------------------------------------------

            /// <summary>Flat, dry ground near the crew with room round it.</summary>
            Vector2Int Flat(Vector2Int near, int room)
            {
                var best = near;
                var bestScore = float.MaxValue;
                for (var z = near.y - 30; z <= near.y + 30; z += 2)
                    for (var x = near.x - 30; x <= near.x + 30; x += 2)
                    {
                        if (!_grid.IsGround(x, z) || _grid.GetSurfaceHeight(x, z) < World.SeaLevel + 5f)
                            continue;
                        var height = _grid.GetSurfaceHeight(x, z);
                        var rough = 0f;
                        var dry = true;
                        for (var dz = -room - 4; dz <= room + 4 && dry; dz++)
                            for (var dx = -room - 4; dx <= room + 10; dx++)
                            {
                                if (!_grid.IsGround(x + dx, z + dz) || _grid.IsWater(x + dx, z + dz))
                                {
                                    dry = false;
                                    break;
                                }

                                rough += Mathf.Abs(_grid.GetSurfaceHeight(x + dx, z + dz) - height);
                            }

                        if (!dry)
                            continue;
                        var score = rough + Vector2Int.Distance(near, new Vector2Int(x, z));
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        best = new Vector2Int(x, z);
                    }

                return best;
            }

            /// <summary>
            /// The best rise near the crew to drive a cut through: the place where the ground is
            /// highest above the level of the dry ground a short way off on both sides. Returns
            /// the crest, the direction along the cut, how far it runs, and the height to floor at.
            /// </summary>
            (Vector2Int centre, Vector2Int along, int run, float height, float foot) Rise(Vector2Int near, int run)
            {
                var best = (centre: near, along: new Vector2Int(1, 0), run: run, height: 0f, foot: 0f);
                var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
                for (var z = near.y - 30; z <= near.y + 30; z += 2)
                    for (var x = near.x - 30; x <= near.x + 30; x += 2)
                    {
                        if (!_grid.IsGround(x, z) || _grid.GetSurfaceHeight(x, z) < World.SeaLevel + 5f)
                            continue;
                        var crest = _grid.GetSurfaceHeight(x, z);
                        foreach (var dir in dirs)
                        {
                            var a = new Vector2Int(x + dir.x * run, z + dir.y * run);
                            var b = new Vector2Int(x - dir.x * run, z - dir.y * run);
                            if (!Dry(a, 2) || !Dry(b, 2))
                                continue;
                            var foot = Mathf.Max(_grid.GetSurfaceHeight(a.x, a.y), _grid.GetSurfaceHeight(b.x, b.y));
                            var lift = crest - foot;
                            if (lift <= best.height)
                                continue;
                            best = (new Vector2Int(x, z), dir, run, lift, foot);
                        }
                    }

                return best;
            }

            /// <summary>The bumpiest dry patch near the crew: the one worth smoothing.</summary>
            Vector2Int Bumpy(Vector2Int near, int room)
            {
                var best = near;
                var bestRough = -1f;
                for (var z = near.y - 30; z <= near.y + 30; z += 2)
                    for (var x = near.x - 30; x <= near.x + 30; x += 2)
                    {
                        var site = new Vector2Int(x, z);
                        if (!Dry(site, room + 3) || _grid.GetSurfaceHeight(x, z) < World.SeaLevel + 5f)
                            continue;
                        var rough = Rough(site, room, Median(site, room));
                        // Bumpy, but not a cliff: a wall is trial B's job, not this one.
                        if (rough <= bestRough || rough > 2f)
                            continue;
                        bestRough = rough;
                        best = site;
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

            float Median(Vector2Int at, int room)
            {
                var heights = new List<float>();
                for (var dz = -room; dz <= room; dz++)
                    for (var dx = -room; dx <= room; dx++)
                        if (_grid.IsGround(at.x + dx, at.y + dz))
                            heights.Add(_grid.GetSurfaceHeight(at.x + dx, at.y + dz));
                if (heights.Count == 0)
                    return 0f;
                heights.Sort();
                return heights[heights.Count / 2];
            }

            /// <summary>Mean distance from a level, over a square: how far off flat the ground is.</summary>
            float Rough(Vector2Int at, int room, float level)
            {
                var total = 0f;
                var cells = 0;
                for (var dz = -room; dz <= room; dz++)
                    for (var dx = -room; dx <= room; dx++)
                    {
                        if (!_grid.IsGround(at.x + dx, at.y + dz))
                            continue;
                        total += Mathf.Abs(_grid.GetSurfaceHeight(at.x + dx, at.y + dz) - level);
                        cells++;
                    }

                return cells == 0 ? 0f : total / cells;
            }

            /// <summary>How far the ground still stands above a floor, averaged over a square.</summary>
            float Depth(Vector2Int at, int room, float floor)
            {
                var total = 0f;
                var cells = 0;
                for (var dz = -room; dz <= room; dz++)
                    for (var dx = -room; dx <= room; dx++)
                    {
                        total += Mathf.Max(0f, _grid.GetSurfaceHeight(at.x + dx, at.y + dz) - floor);
                        cells++;
                    }

                return cells == 0 ? 0f : total / cells;
            }

            /// <summary>
            /// The work still outstanding, in cubic metres: what every live designation asks for,
            /// summed. This is the progress measure, because a count of cells barely moves while
            /// a deep cell is worked step by step.
            /// </summary>
            float Outstanding()
            {
                var total = 0f;
                var cells = _map.ActiveCells;
                for (var i = 0; i < cells.Count; i++)
                {
                    var x = cells[i] % _grid.Width;
                    var z = cells[i] / _grid.Width;
                    total += Mathf.Abs(_grid.GetSurfaceHeight(x, z) - _map.GetTarget(x, z));
                }

                return total * _grid.CellSize * _grid.CellSize;
            }

            /// <summary>
            /// What the ground at a site is made of and what a step of it costs each unit. Rock
            /// costs a unit without a cutter several times what soil does, so a trial that crawls
            /// wants this said out loud before anything else is blamed.
            /// </summary>
            string Ground(Vector2Int at)
            {
                var material = _grid.GetTopMaterial(at.x, at.y);
                var said = $"Ground: {_grid.Materials.Get(material).DisplayName}";
                foreach (var unit in _crew.Units)
                    said += $", {UnitNames.Of(unit.Role)} {unit.Id} {unit.StepSeconds(at.x, at.y):0.##} s a step";
                return said + ".";
            }

            string Roll()
            {
                var roll = "";
                foreach (var unit in _crew.Units)
                    roll += $"[{UnitNames.Of(unit.Role)} {unit.Id}: {unit.Status}] ";
                return roll;
            }

            /// <summary>
            /// Where the crew's time went, in game seconds per state, biggest first. A trial that
            /// crawls is nearly always a trial where the units are somewhere other than Digging,
            /// and guessing which is a waste of a run.
            /// </summary>
            string Spend()
            {
                var total = 0f;
                foreach (var pair in _spent)
                    total += pair.Value;
                if (total <= 0f)
                    return "no time measured";

                var states = new List<KeyValuePair<CrewUnitState, float>>(_spent);
                states.Sort((a, b) => b.Value.CompareTo(a.Value));
                var said = "time: ";
                foreach (var state in states)
                    said += $"{state.Key} {100f * state.Value / total:0}% ";

                // Time in Digging is not the same as digging. A unit that has latched onto a cell
                // it cannot actually cut sits in Digging, waits out its interval, refuses, thinks
                // again and comes straight back — and reads as busy while nothing moves. Cuts
                // landed against seconds spent digging is what tells the two apart.
                _spent.TryGetValue(CrewUnitState.Digging, out var digging);
                var refused = 0;
                var offStand = 0;
                var landed = 0;
                var ticks = 0;
                foreach (var unit in _crew.Units)
                {
                    refused += unit.RefusedCuts;
                    offStand += unit.OffStand;
                    landed += unit.LandedCuts;
                    ticks += unit.DigTicks;
                }

                return said + $"— {digging:0} unit-seconds of it in Digging over {ticks} ticks, "
                       + $"{landed} cuts landed, {refused} refused on arrival, {offStand} waited "
                       + $"off its own stand.";
            }

            // --- running and looking ----------------------------------------------------------

            /// <summary>
            /// Run the crew until the designations are gone, until they stop making progress, or
            /// until the cap — whichever comes first — and say in the log which it was. Progress is
            /// measured in cubic metres outstanding, so a crew slowly chewing a deep cell counts as
            /// working and a crew walking in circles does not.
            /// </summary>
            IEnumerator Work(string trial)
            {
                _spent.Clear();
                Time.timeScale = Speed;
                // Unity clamps a frame's delta to maximumDeltaTime — a third of a second — so at
                // twenty times speed a slow editor advances the game clock barely faster than the
                // wall clock, and a trial that should take a minute takes an hour. Letting a frame
                // carry more time is only safe because CrewView steps the crew in tenth-second
                // slices whatever the frame was.
                Time.maximumDeltaTime = 4f;
                var wall = Time.realtimeSinceStartup;
                var clock = Time.time;
                var mark = clock;
                var lastTrace = clock;
                var best = Outstanding();
                var start = best;
                var why = "cap";
                while (Time.time - clock < CapSeconds)
                {
                    foreach (var unit in _crew.Units)
                    {
                        _spent.TryGetValue(unit.State, out var so_far);
                        _spent[unit.State] = so_far + Time.deltaTime;
                    }

                    if (_map.Count == 0)
                    {
                        why = "done";
                        break;
                    }

                    var now = Outstanding();
                    if (now < best - 0.01f)
                    {
                        best = now;
                        mark = Time.time;
                    }
                    else if (Time.time - mark > StallSeconds)
                    {
                        why = "stalled";
                        break;
                    }

                    if (Time.time - lastTrace > TraceSeconds)
                    {
                        lastTrace = Time.time;
                        Debug.Log($"Trial {trial} at {Time.time - clock:0} game s: "
                                  + $"{now:0.##} m³ of {start:0.##} left, {_map.Count} cells.");
                    }

                    yield return null;
                }

                Time.timeScale = 1f;
                Time.maximumDeltaTime = 1f / 3f;
                yield return Frames(4);
                var ran = Time.time - clock;
                var moved = start - Outstanding();
                Debug.Log($"Trial {trial} ended: {why}, after {ran:0} game s "
                          + $"({Time.realtimeSinceStartup - wall:0} s real); {Outstanding():0.##} m³ "
                          + $"of {start:0.##} still outstanding, so {moved:0.##} m³ moved at "
                          + $"{(ran > 0f ? moved / ran : 0f):0.####} m³ a game second. " + Spend());
            }

            void Look(Vector2Int at, float back)
            {
                var target = _view.transform.TransformPoint(new Vector3(
                    (at.x + 0.5f) * _grid.CellSize, _grid.GetSurfaceHeight(at.x, at.y),
                    (at.y + 0.5f) * _grid.CellSize));
                var eye = target + new Vector3(-back, back * 0.8f, -back);
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
