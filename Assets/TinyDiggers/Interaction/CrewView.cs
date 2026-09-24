using System.Collections.Generic;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using TinyDiggers.Units;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Scene side of the crew: it owns the <see cref="JobDispatcher"/> the units share, spawns
    /// them, ticks them and draws a body and the path of each. The body is the crew robot prefab,
    /// playing the clip <see cref="CrewAnimation"/> picks, or a placeholder box when no prefab is
    /// set. It holds the selection the RTS controls work on: click or box to select, then send
    /// the selection somewhere with <see cref="OrderSelectedTo"/>. Rings mark the selected units
    /// and, briefly, where they were sent; only selected units show their paths. Escape clears
    /// the selection. No logic of its own.
    /// </summary>
    public sealed class CrewView : MonoBehaviour
    {
        [SerializeField] TerrainView _terrain;
        [SerializeField] DesignationsView _designations;
        [SerializeField] Material _pathMaterial;
        [Tooltip("Metres from the middle of the disc the crew would like to start. It starts on the nearest level, dry ground to that.")]
        [SerializeField] Vector2 _spawnOffset;

        [Tooltip("How many worker robots to spawn: the first unit, digging and hauling barrow loads itself.")]
        [SerializeField, Min(0)] int _workerCount = 4;

        [Tooltip("Loose m³ a worker's barrow holds. One scoop of any ground is up to 0.19 m³ loose.")]
        [SerializeField, Min(0.01f)] float _workerCapacity = UnitLoads.Barrow;

        [Tooltip("A worker's speed, metres per second.")]
        [SerializeField, Min(0.1f)] float _workerSpeed = 1.5f;

        [Tooltip("How many diggers to spawn, side by side across the spawn cell.")]
        [SerializeField, Min(0)] int _diggerCount;

        [Tooltip("How many haulers to spawn.")]
        [SerializeField, Min(0)] int _haulerCount;

        [Tooltip("How many bulldozers to spawn: they grade finished road earthworks to the road's true grade.")]
        [SerializeField, Min(0)] int _bulldozerCount = 1;

        [Tooltip("How many pavers to spawn: they lay the final layer on graded road.")]
        [SerializeField, Min(0)] int _paverCount = 1;

        [Tooltip("Vehicles with no worksite park and wait to be assigned, as in Captain of Industry. Off: they take work anywhere.")]
        [SerializeField] bool _needWorksite = true;

        /// <summary>Whether vehicles with no worksite park (the game) or take work anywhere (the editor's trials).</summary>
        public bool NeedWorksite
        {
            get => _needWorksite;
            set => _needWorksite = value;
        }

        [Tooltip("Metres per second.")]
        [SerializeField, Min(0.1f)] float _speed = 3f;

        [Tooltip("Loose m³ a digger's scoop holds.")]
        [SerializeField, Min(0.01f)] float _capacity = UnitLoads.Scoop;

        [Tooltip("Loose m³ a hauler's bed holds.")]
        [SerializeField, Min(0.01f)] float _haulerCapacity = UnitLoads.Bed;

        [Tooltip("Seconds per height step dug, or per tip.")]
        [SerializeField, Min(0.01f)] float _workInterval = 0.4f;

        /// <summary>How far above or below its own cell a unit can dig or fill, in metres. Read every frame.</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("digReachLevels")]
        [Min(0f)] public float digReach = 2f;

        /// <summary>How far up a rock face a unit can work from its foot, in metres. Read every frame.</summary>
        [Min(0f)] public float cliffReach = 6f;

        /// <summary>Largest height change a unit can drive across between neighbouring cells, in metres.</summary>
        [Min(0f)] public float maxStepHeight = 1f;

        /// <summary>
        /// The steepest ground the crew drives, in degrees, or nought to use
        /// <see cref="maxStepHeight"/> as it stands. The ground decides, not the machine: steeper
        /// than this and everything wants a ramp or a road, which is what makes the topography
        /// worth anything (Ronan, 2026-09-22).
        /// </summary>
        [Min(0f)] public float maxSlopeDegrees = 45f;

        /// <summary>Take designated ground down in drivable layers, so upper cells stay workable.</summary>
        public bool benching = true;

        /// <summary>Let the crew cut ramps to work it cannot otherwise reach.</summary>
        public bool autoRamp = true;

        // Bodies fit inside one cell: units keep a cell between their centres, so anything
        // longer than that overlaps its neighbour when they work side by side.
        [SerializeField] Vector3 _bodySize = new Vector3(0.7f, 0.55f, 0.95f);
        [SerializeField] Vector3 _haulerBodySize = new Vector3(0.85f, 0.7f, 0.95f);
        [SerializeField] Color _bodyColor = new Color(1f, 0.78f, 0.1f);
        [SerializeField] Color _haulerColor = new Color(0.35f, 0.55f, 0.9f);
        [SerializeField] Color _selectedColor = new Color(1f, 1f, 1f);

        [Tooltip("The crew robot (crew_unit.prefab), with an Animator holding Idle, Move, Work and Carry. Empty: placeholder boxes.")]
        [SerializeField] GameObject _bodyPrefab;

        [Tooltip("The digger machine (digger.prefab). Empty: the crew robot's body, or a box.")]
        [SerializeField] GameObject _diggerPrefab;

        [Tooltip("The dumper machine (dumper.prefab), for haulers. Empty: the crew robot's body, or a box.")]
        [SerializeField] GameObject _haulerPrefab;

        [Tooltip("The bulldozer machine (dozer.prefab), for road grading. Empty: a stand-in box the size of the dumper.")]
        [SerializeField] GameObject _dozerPrefab;

        [Tooltip("Tint on the selected robot.")]
        [SerializeField] Color _selectedTint = new Color(1f, 0.95f, 0.55f);

        [Tooltip("Flat colour for the selection rings and the move marker (URP Unlit, transparent). Empty: no rings.")]
        [SerializeField] Material _markerMaterial;

        [Tooltip("Seconds the ring where units were sent stays up.")]
        [SerializeField, Min(0.1f)] float _orderMarkerSeconds = 0.8f;

        [Tooltip("Seconds to blend from one clip to the next.")]
        [SerializeField, Min(0f)] float _clipBlend = 0.15f;

        /// <summary>How far through the dig clip the bucket reaches the ground, 0 to 1.</summary>
        [SerializeField, Range(0f, 1f)] float _biteAt = 0.5f;

        /// <summary>How far through the tip clip the bed lets go of its load, 0 to 1.</summary>
        [SerializeField, Range(0f, 1f)] float _tipAt = 0.45f;

        /// <summary>
        /// Size of the robot body; 1 is as modelled, human scale: a ball 0.285 m across floating
        /// 0.125 m up. Read every frame.
        /// </summary>
        [Min(0.05f)] public float bodyScale = 1f;

        [Tooltip("Tip bodies to the ground under their wheels (pitch and roll from the land, yaw from the simulation).")]
        [SerializeField] bool _groundPose = true;

        [Tooltip("Seconds for a body's pitch and roll to catch up with the ground.")]
        [SerializeField, Min(0f)] float _tiltSmoothing = 0.15f;

        [Tooltip("Degrees of pitch or roll past which the fit is wrong rather than the vehicle.")]
        [SerializeField, Min(0f)] float _maxTiltDegrees = GroundPose.DefaultMaxTiltDegrees;

        /// <summary>
        /// Cells between machines where they start, from the room a machine actually takes
        /// (<see cref="CrewUnit.DiggerRadius"/>). The crew logic gives every unit one cell, but a
        /// machine is over a metre long on half-metre cells, so two of them a cell apart start
        /// inside one another.
        /// </summary>
        static int MachineSpacing => Mathf.CeilToInt(2f * CrewUnit.DiggerRadius) + 1;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        readonly List<CrewUnit> _units = new List<CrewUnit>();
        readonly List<Transform> _bodies = new List<Transform>();
        readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        readonly List<Renderer[]> _robotRenderers = new List<Renderer[]>();
        readonly List<Animator> _animators = new List<Animator>();
        /// <summary>Each body's drive and work speeds, or null for a body without one.</summary>
        readonly List<MachineBody> _machines = new List<MachineBody>();
        /// <summary>Where each body was drawn last frame, for how fast its tracks should turn.</summary>
        readonly List<Vector3> _lastDrawn = new List<Vector3>();
        readonly List<string> _clips = new List<string>();
        readonly List<bool> _tinted = new List<bool>();
        MaterialPropertyBlock _tint;
        readonly List<int> _selection = new List<int>();
        readonly List<GameObject> _rings = new List<GameObject>();
        /// <summary>Half the length and half the width of each body, in metres, for its ground pose.</summary>
        readonly List<Vector2> _prints = new List<Vector2>();
        /// <summary>The pitch and roll each body is drawn at, and how fast they are moving.</summary>
        readonly List<Vector2> _tilt = new List<Vector2>();
        readonly List<Vector2> _tiltRate = new List<Vector2>();
        Transform _orderMarker;
        Renderer _orderMarkerRenderer;
        float _orderMarkerAt = float.NegativeInfinity;
        Mesh _ringMesh;
        MaterialPropertyBlock _markerBlock;
        readonly List<LineRenderer> _lines = new List<LineRenderer>();
        readonly Vector3[] _pathPoints = new Vector3[512];

        GridPathfinder _pathfinder;

        public JobDispatcher Dispatcher { get; private set; }

        public IReadOnlyList<CrewUnit> Units => _units;

        /// <summary>The first selected unit, for the readout, or null.</summary>
        public CrewUnit Selected => _selection.Count > 0 ? _units[_selection[0]] : null;

        /// <summary>How many units are selected.</summary>
        public int SelectedCount => _selection.Count;

        /// <summary>The first unit, for readouts that want something to show with nothing selected.</summary>
        public CrewUnit Unit => _units.Count > 0 ? _units[0] : null;

        /// <summary>The units selected now, in the order they were picked.</summary>
        public IEnumerable<CrewUnit> Selection
        {
            get
            {
                foreach (var index in _selection)
                    if (index >= 0 && index < _units.Count)
                        yield return _units[index];
            }
        }

        /// <summary>
        /// Assigns the selected units to a worksite (2026-09-24): they drop any hold, go there and
        /// work its area and nothing else. Returns how many were assigned (Ronan, 2026-09-23:
        /// *"when I tell them to dig it, select units, assign, type deal"*).
        /// </summary>
        public int Assign(int site)
        {
            var told = 0;
            foreach (var unit in Selection)
            {
                unit.Site = site;
                unit.ReleaseHold();
                unit.RequestRethink();
                told++;
            }

            return told;
        }

        void Start()
        {
            var grid = _terrain.Grid;
            _pathfinder = new GridPathfinder(grid) { MaxStepHeight = maxStepHeight };
            if (maxSlopeDegrees > 0f)
                _pathfinder.MaxSlopeDegrees = maxSlopeDegrees;
            Dispatcher = new JobDispatcher(grid, _designations.Map, _pathfinder)
            {
                // Nothing works until it is told where (Ronan, 2026-09-24: "I don't want them
                // wandering, they won't do anything till assigned — I can still move them around but
                // they won't work until assigned"). A unit with no worksite parks where it is.
                //
                // Set here rather than as the default, because the tests of the crew's own rules —
                // what a unit can reach, dig, carry and tip — are about a unit with work in front of
                // it, and would all have to hand out worksites to say anything at all.
                RequireWorksite = true,
            };

            var wish = (_terrain.DiscCentre + _spawnOffset) / grid.CellSize;
            var wishX = Mathf.Clamp(Mathf.FloorToInt(wish.x), 0, grid.Width - 1);
            var wishZ = Mathf.Clamp(Mathf.FloorToInt(wish.y), 0, grid.Height - 1);
            if (!CrewSpawn.TryFind(grid, wishX, wishZ, Mathf.Max(grid.Width, grid.Height) / 2, out var spawn))
            {
                Debug.LogWarning($"CrewView: no level, dry ground anywhere near ({wishX}, {wishZ}); the crew starts there anyway.", this);
                spawn = new Vector2Int(wishX, wishZ);
            }

            _yard = spawn;
            var counts = new[]
            {
                (UnitRole.Worker, _workerCount), (UnitRole.Digger, _diggerCount), (UnitRole.Hauler, _haulerCount),
                (UnitRole.Bulldozer, _bulldozerCount), (UnitRole.Paver, _paverCount),
            };
            foreach (var (role, count) in counts)
                for (var i = 0; i < count; i++)
                    Hire(role);
        }

        /// <summary>
        /// Where the crew starts, and where anyone taken on later turns up. Set once the ground
        /// under the yard is known, which is why hiring before <see cref="Start"/> does nothing.
        /// </summary>
        Vector2Int _yard;

        /// <summary>
        /// Takes on one more unit of <paramref name="role"/> and puts it in the yard with the
        /// rest: the same unit, body, animator and path line the crew starts with, because this is
        /// the code the crew starts with. Returns it, or null before the scene is running.
        ///
        /// Ronan, 2026-09-23: "add a way for me to add more of any of the three units I want" —
        /// the counts in the inspector only ever applied at load, so trying a second dumper meant
        /// stopping, editing and starting over, which is no way to find out how many of a thing a
        /// site wants.
        /// </summary>
        public CrewUnit Hire(UnitRole role)
        {
            if (Dispatcher == null || _terrain == null || _terrain.Grid == null)
                return null;

            var grid = _terrain.Grid;
            var i = _units.Count;
            // Side by side, so no two start on the same cell — and the machines further out than
            // that, because a machine is over a metre long and two of them a metre apart stand
            // inside each other.
            var apart = role == UnitRole.Worker ? 2 : MachineSpacing;
            var x = Mathf.Clamp(_yard.x + i % 2 * apart - apart / 2, 0, grid.Width - 1);
            var z = Mathf.Clamp(_yard.y + i / 2 * apart - apart / 2, 0, grid.Height - 1);
            // The yard fills up as the crew grows, and a unit dropped on top of another has to
            // shove its way out; take the nearest clear ground instead when there is any.
            if (CrewSpawn.TryFind(grid, x, z, MachineSpacing * 3, out var clear))
            {
                x = clear.x;
                z = clear.y;
            }

            var capacity = role == UnitRole.Worker ? _workerCapacity : role == UnitRole.Digger ? _capacity : _haulerCapacity;
            var unit = new CrewUnit(Dispatcher, x, z, role, capacity);
            // In the game a digger or a hauler is a machine, with a machine's room and, for the
            // digger, a rock cutter. The crew logic itself makes no such assumption.
            if (role != UnitRole.Worker)
                unit.AsMachine();
            unit.Speed = role == UnitRole.Worker ? _workerSpeed : _speed;
            _units.Add(unit);

            var prefab = BodyFor(role);
            if (prefab != null)
                AddRobot(prefab, role, i);
            else
                AddBox(role, i, grid.CellSize);

            // A machine takes as long over a cut as the scoop takes to swing: the clip it plays is
            // what says how long the work looks, so the clip sets the time.
            TimeWorkToTheClips(unit, _animators[_animators.Count - 1], _machines[_machines.Count - 1]);

            var line = new GameObject($"{role} {i} Path") { hideFlags = HideFlags.DontSave };
            line.transform.SetParent(transform, false);
            var pathLine = line.AddComponent<LineRenderer>();
            pathLine.sharedMaterial = _pathMaterial;
            pathLine.widthMultiplier = 0.06f;
            pathLine.startColor = pathLine.endColor = new Color(1f, 1f, 1f, 0.85f);
            pathLine.useWorldSpace = true;
            pathLine.positionCount = 0;
            _lines.Add(pathLine);

            // Dust and chunks where the bucket bites and where the bed lets go. The unit raises
            // these at the bite, so the effect lands with the earth rather than with the job.
            var effects = Effects;
            if (effects != null)
            {
                unit.Bit += (cell, material, volume) => effects.Bite(cell.x, cell.y, material, volume);
                unit.Tipped += (cell, material, volume) => effects.Tip(cell.x, cell.y, material, volume);
            }

            return unit;
        }

        GroundEffects _effects;

        /// <summary>
        /// The dust and chunks, made on first use beside the terrain so the crew can be built
        /// before it. Null if there is no terrain to stand on.
        /// </summary>
        GroundEffects Effects
        {
            get
            {
                if (_effects != null || _terrain == null || _terrain.Grid == null)
                    return _effects;
                var holder = new GameObject("Ground Effects") { hideFlags = HideFlags.DontSave };
                holder.transform.SetParent(_terrain.transform, false);
                _effects = holder.AddComponent<GroundEffects>();
                _effects.Init(_terrain.Grid, _terrain.transform);
                return _effects;
            }
        }

        /// <summary>
        /// Lets a unit go: it leaves the dispatcher (which releases its job, unpairs whatever it
        /// was working with and frees the cell it stood on), its body, ring and path line are
        /// destroyed, and everything the view holds per unit drops the same slot.
        ///
        /// Every one of those lists is indexed by the unit's place in <see cref="Units"/>, so one
        /// missed list would silently attach a unit to another's body; they are removed together
        /// here for that reason. The selection holds indices too, so entries past the gap come
        /// down one.
        ///
        /// Whatever it was carrying goes with it. A unit is not a container the site can get its
        /// spoil back out of, and dismissing a full dumper to avoid a trip to the tip should cost
        /// what it was holding.
        /// </summary>
        public bool Dismiss(int index)
        {
            if (index < 0 || index >= _units.Count)
                return false;

            _units[index].Dispose();
            if (_bodies[index] != null)
                Destroy(_bodies[index].gameObject);
            if (_rings[index] != null)
                Destroy(_rings[index]);
            if (_lines[index] != null)
                Destroy(_lines[index].gameObject);

            _units.RemoveAt(index);
            _bodies.RemoveAt(index);
            _renderers.RemoveAt(index);
            _robotRenderers.RemoveAt(index);
            _animators.RemoveAt(index);
            _machines.RemoveAt(index);
            _lastDrawn.RemoveAt(index);
            _clips.RemoveAt(index);
            _tinted.RemoveAt(index);
            _rings.RemoveAt(index);
            _prints.RemoveAt(index);
            _tilt.RemoveAt(index);
            _tiltRate.RemoveAt(index);
            _lines.RemoveAt(index);

            for (var i = _selection.Count - 1; i >= 0; i--)
            {
                if (_selection[i] == index)
                    _selection.RemoveAt(i);
                else if (_selection[i] > index)
                    _selection[i]--;
            }

            return true;
        }

        void AddBox(UnitRole role, int i, float cellSize)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = $"{role} {i} Body";
            body.hideFlags = HideFlags.DontSave;
            body.transform.SetParent(transform, false);
            // A unit is a one-cell machine to the crew logic, so its body is sized to the cell.
            body.transform.localScale = BoxSize(role, cellSize);
            var bodyRenderer = body.GetComponent<MeshRenderer>();
            bodyRenderer.material.color = BoxColor(role);
            if (role == UnitRole.Bulldozer || role == UnitRole.Paver)
            {
                AddRoadMachineParts(body.transform, role);
                FitTo(body.transform, BoxSize(role, cellSize));
            }
            _bodies.Add(body.transform);
            _renderers.Add(bodyRenderer);
            _robotRenderers.Add(null);
            _rings.Add(null);
            // The box as it ends up, not as it was asked for: a road machine is shrunk by FitTo
            // after its parts go on, and its wheels are where the box is.
            AddPrint(new Vector2(body.transform.localScale.z, body.transform.localScale.x) * 0.5f);
            _animators.Add(null);
            _machines.Add(null);
            _lastDrawn.Add(body.transform.position);
            _clips.Add(null);
            _tinted.Add(false);
        }

        /// <summary>
        /// Shrinks a finished stand-in until the whole machine fits <paramref name="wanted"/> — the
        /// dumper's size — rather than just the box inside it.
        ///
        /// The box was being made the dumper's size and then a cab stacked on top of it, a blade
        /// hung off the front. The cab sits at 0.7 of a box height with 0.45 of its own, so it
        /// reaches 0.925 where the box top is 0.5: the machine came out 1.43 box-heights tall, which
        /// is exactly the 2.46 m measured against the dumper's 1.73 (Ronan, 2026-09-24: "scale the
        /// new dozer and paver stand-ins to be the same size as the dumper").
        ///
        /// It measures rather than working the factor out from those constants, so moving a blade or
        /// raising a cab cannot quietly make the machines the wrong size again. Uniform, by the worst
        /// axis, so the parts keep their proportions and nothing pokes past the dumper's envelope.
        /// </summary>
        static void FitTo(Transform body, Vector3 wanted)
        {
            var renderers = body.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0 || wanted.x <= 0f || wanted.y <= 0f || wanted.z <= 0f)
                return;

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
                bounds.Encapsulate(renderer.bounds);

            // Into the same units `wanted` is in: the view's local space.
            var parent = body.parent;
            var scale = parent != null ? parent.lossyScale : Vector3.one;
            var is_ = new Vector3(bounds.size.x / scale.x, bounds.size.y / scale.y, bounds.size.z / scale.z);

            var over = Mathf.Max(is_.x / wanted.x, Mathf.Max(is_.y / wanted.y, is_.z / wanted.z));
            if (over > 1f)
                body.localScale /= over;
        }

        /// <summary>Records a new body's ground footprint, level to start with.</summary>
        void AddPrint(Vector2 halfSize)
        {
            _prints.Add(new Vector2(Mathf.Max(0.05f, halfSize.x), Mathf.Max(0.05f, halfSize.y)));
            _tilt.Add(Vector2.zero);
            _tiltRate.Add(Vector2.zero);
        }

        /// <summary>
        /// The model for a role: each machine has its own now that the digger and the dumper are
        /// built (the crew robot stands in for anything not yet modelled, and a box for that).
        /// </summary>
        GameObject BodyFor(UnitRole role)
        {
            switch (role)
            {
                case UnitRole.Digger:
                    return _diggerPrefab != null ? _diggerPrefab : _bodyPrefab;
                case UnitRole.Hauler:
                    return _haulerPrefab != null ? _haulerPrefab : _bodyPrefab;
                case UnitRole.Bulldozer:
                    return _dozerPrefab;
                case UnitRole.Paver:
                    // Not modelled yet: a stand-in box the size of the dumper (AddBox).
                    return null;
                default:
                    return _bodyPrefab;
            }
        }

        void AddRobot(GameObject prefab, UnitRole role, int i)
        {
            var body = Instantiate(prefab, transform, false);
            body.name = $"{role} {i} Body";
            body.hideFlags = HideFlags.DontSave;
            var renderers = body.GetComponentsInChildren<Renderer>();

            // A sphere round the model, for clicking; it scales with the body.
            var bounds = new Bounds(body.transform.position, Vector3.zero);
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            var hit = body.AddComponent<SphereCollider>();
            hit.center = body.transform.InverseTransformPoint(bounds.center);
            hit.radius = bounds.extents.magnitude * 0.6f;

            _bodies.Add(body.transform);
            _renderers.Add(null);
            _robotRenderers.Add(renderers);
            // On the model rather than the root: the root is turned to face the unit's heading every
            // frame, and a forge model keeps its own turn and size on the child the clips animate.
            _animators.Add(body.GetComponentInChildren<Animator>());
            _machines.Add(body.GetComponentInChildren<MachineBody>());
            _lastDrawn.Add(body.transform.position);
            _clips.Add(null);
            _tinted.Add(false);
            AddPrint(new Vector2(bounds.size.z, bounds.size.x) * (0.5f * bodyScale));
            _rings.Add(MakeRing($"{role} {i} Ring", body.transform, 0.22f, 0.26f));
        }

        /// <summary>
        /// Sets a unit's work times from the clips its body actually plays (Ronan, 2026-09-22:
        /// "every dig needs a matching animation, the time to scoop and load truck").
        ///
        /// The crew logic holds no animation of its own — it is plain C# and knows nothing about
        /// Unity's animator — so whatever draws a unit tells it how long the work looks. A body
        /// with no clips leaves the times alone and the unit keeps its own interval.
        /// </summary>
        void TimeWorkToTheClips(CrewUnit unit, Animator animator, MachineBody machine)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return;

            // A body means a swing to watch, so the earth moves part way through it rather than at
            // the end. An OnBite or OnTip event on the clip says exactly where; without one these
            // stand in, and they are fields so the frame can be found by eye before it is authored.
            unit.BiteAt = _biteAt;
            unit.TipAt = _tipAt;

            foreach (var clip in animator.runtimeAnimatorController.animationClips)
            {
                if (clip == null || clip.length <= 0f)
                    continue;
                // The states are named for what the unit is doing: Work is the cut for a digging
                // unit and the tip for a hauler, which is exactly how CrewAnimation picks them.
                // Case-blind: the walker clips are "dig" and "tip", the forge's "Dig" and "Tip".
                var workSpeed = machine != null ? machine.WorkSpeed : 1f;
                if (clip.name.EndsWith("dig", System.StringComparison.OrdinalIgnoreCase))
                {
                    unit.DigSeconds = clip.length / workSpeed;
                    var at = EventAt(clip, "OnBite");
                    if (at >= 0f)
                        unit.BiteAt = at;
                }
                else if (clip.name.EndsWith("tip", System.StringComparison.OrdinalIgnoreCase))
                {
                    unit.TipSeconds = clip.length / workSpeed;
                    var at = EventAt(clip, "OnTip");
                    if (at >= 0f)
                        unit.TipAt = at;
                }
            }
        }

        /// <summary>
        /// How far through <paramref name="clip"/> the event called <paramref name="name"/> sits,
        /// 0 to 1, or -1 if the clip carries no such event.
        ///
        /// The event's *time* is what is wanted, not its callback: the crew logic is plain C# and
        /// cannot be called back into from an animator, so it is told when the bite lands the same
        /// way it is told how long the swing takes. Putting the event on the frame the bucket
        /// enters the ground is still how it is authored.
        /// </summary>
        static float EventAt(AnimationClip clip, string name)
        {
            if (clip.length <= 0f)
                return -1f;
            foreach (var e in clip.events)
                if (e.functionName == name)
                    return Mathf.Clamp01(e.time / clip.length);
            return -1f;
        }

        /// <summary>A flat ring on the ground, hidden until wanted; null without a marker material.</summary>
        GameObject MakeRing(string name, Transform parent, float inner, float outer)
        {
            if (_markerMaterial == null)
                return null;
            _ringMesh ??= RingMesh(48);
            var ring = new GameObject(name) { hideFlags = HideFlags.DontSave };
            ring.transform.SetParent(parent, false);
            ring.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            ring.transform.localScale = new Vector3(outer, 1f, outer);
            ring.AddComponent<MeshFilter>().sharedMesh = _ringMesh;
            var ringRenderer = ring.AddComponent<MeshRenderer>();
            ringRenderer.sharedMaterial = _markerMaterial;
            ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.SetActive(false);
            return ring;
        }

        /// <summary>A unit-radius ring 20% thick, facing up.</summary>
        static Mesh RingMesh(int segments)
        {
            const float inner = 0.8f;
            var vertices = new Vector3[segments * 2];
            var triangles = new int[segments * 6];
            for (var s = 0; s < segments; s++)
            {
                var angle = 2f * Mathf.PI * s / segments;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices[s * 2] = direction * inner;
                vertices[s * 2 + 1] = direction;
                var next = (s + 1) % segments;
                var t = s * 6;
                triangles[t] = s * 2;
                triangles[t + 1] = next * 2;
                triangles[t + 2] = s * 2 + 1;
                triangles[t + 3] = s * 2 + 1;
                triangles[t + 4] = next * 2;
                triangles[t + 5] = next * 2 + 1;
            }

            var mesh = new Mesh { name = "Crew Ring", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Longest slice of time the crew is stepped by at once, in seconds.</summary>
        [Tooltip("Bulldozer stand-in colour.")]
        [SerializeField] Color _bulldozerColor = new Color(0.95f, 0.5f, 0.1f);

        [Tooltip("Paver stand-in colour.")]
        [SerializeField] Color _paverColor = new Color(0.3f, 0.3f, 0.33f);

        Vector3? _dumperSize;

        /// <summary>
        /// The dumper model's size as it is drawn at <see cref="bodyScale"/> 1, measured once from
        /// the prefab, or null without one. The road machines are stand-ins the size of the dumper
        /// (Ronan, 2026-09-24: "they should be size of the dump truck"), so they are measured from
        /// it rather than guessed: the model is not the size <see cref="_haulerBodySize"/> says.
        /// </summary>
        Vector3? DumperSize()
        {
            if (_dumperSize.HasValue || _haulerPrefab == null)
                return _dumperSize;
            var probe = Instantiate(_haulerPrefab, transform, false);
            probe.transform.localPosition = Vector3.zero;
            probe.transform.localRotation = Quaternion.identity;
            probe.transform.localScale = Vector3.one;
            var renderers = probe.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                foreach (var r in renderers)
                    bounds.Encapsulate(r.bounds);
                var scale = transform.lossyScale;
                _dumperSize = new Vector3(bounds.size.x / scale.x, bounds.size.y / scale.y, bounds.size.z / scale.z);
            }

            DestroyImmediate(probe);
            return _dumperSize;
        }

        /// <summary>A box body's size, in this view's local units.</summary>
        Vector3 BoxSize(UnitRole role, float cellSize)
        {
            if (role == UnitRole.Bulldozer || role == UnitRole.Paver)
            {
                var dumper = DumperSize();
                if (dumper.HasValue)
                    return dumper.Value * bodyScale;
            }

            return (role == UnitRole.Digger ? _bodySize : _haulerBodySize) * cellSize;
        }

        Color BoxColor(UnitRole role) =>
            role == UnitRole.Digger ? _bodyColor
            : role == UnitRole.Bulldozer ? _bulldozerColor
            : role == UnitRole.Paver ? _paverColor
            : _haulerColor;

        /// <summary>
        /// Enough on a stand-in box to tell the two road machines apart and see which way they
        /// face: a blade across the front of the bulldozer, a roller across the front of the paver,
        /// and a cab at the back of both. Sized as fractions of the box, so they follow it.
        /// </summary>
        static void AddRoadMachineParts(Transform body, UnitRole role)
        {
            var box = body.localScale;
            if (role == UnitRole.Bulldozer)
            {
                Part(body, PrimitiveType.Cube, new Vector3(0f, -0.2f, 0.56f), Quaternion.identity,
                    new Vector3(1.2f, 0.55f, 0.1f), new Color(0.22f, 0.22f, 0.24f));
            }
            else
            {
                // A cylinder is two units long on its own y, turned onto the box's x to lie across it.
                var across = 0.5f * box.y;
                Part(body, PrimitiveType.Cylinder, new Vector3(0f, -0.25f, 0.62f), Quaternion.Euler(0f, 0f, 90f),
                    new Vector3(across / box.y, 0.55f, across / box.z), new Color(0.6f, 0.6f, 0.63f));
            }

            Part(body, PrimitiveType.Cube, new Vector3(0f, 0.7f, -0.2f), Quaternion.identity,
                new Vector3(0.7f, 0.45f, 0.4f), new Color(0.15f, 0.17f, 0.2f));
        }

        static void Part(Transform body, PrimitiveType shape, Vector3 at, Quaternion rotation, Vector3 scale, Color color)
        {
            var part = GameObject.CreatePrimitive(shape);
            part.hideFlags = HideFlags.DontSave;
            // Clicks land on the body's own box; a collider here would take them and select nothing.
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(body, false);
            part.transform.localPosition = at;
            part.transform.localRotation = rotation;
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().material.color = color;
        }

        const float MaxTickSeconds = 0.1f;

        /// <summary>Most slices one frame may be broken into: twenty seconds of crew work.</summary>
        const int MaxTickSlices = 200;

        /// <summary>
        /// Milliseconds one frame may spend stepping the crew. A budget in *real* time, because
        /// what it is protecting is the frame, not the simulation.
        /// </summary>
        const double MaxTickMilliseconds = 6.0;

        void Update()
        {
            Dispatcher.RequireWorksite = _needWorksite;
            Dispatcher.Benching = benching;
            Dispatcher.AutoRamp = autoRamp;
            _pathfinder.MaxStepHeight = maxStepHeight;
            if (maxSlopeDegrees > 0f)
                _pathfinder.MaxSlopeDegrees = maxSlopeDegrees;
            foreach (var unit in _units)
            {
                unit.DigReachLevels = Levels(digReach);
                unit.CliffReachLevels = Levels(cliffReach);
                unit.Speed = unit.Role == UnitRole.Worker ? _workerSpeed : _speed;
                unit.WorkInterval = _workInterval;
            }

            // The crew is stepped in slices no longer than MaxTickSeconds, however long the frame
            // was. A worker covers a metre and a half a second and a cell is half a metre, so a
            // single step of a third of a second already moves it a whole cell, and at speed it
            // steps clean over several: claims, reach checks and the cell it was meant to stop on
            // all go past unseen. Slicing lets the clock run fast — a fast-forward, or a trial at
            // twenty times — without the simulation going blind. The slice count is capped so a
            // hitch or a long pause cannot turn one frame into minutes of work.
            var left = Mathf.Min(Time.deltaTime, MaxTickSeconds * MaxTickSlices);
            var startedAt = Time.realtimeSinceStartupAsDouble;
            while (left > 0f)
            {
                // However far behind the clock is, one frame spends only so long catching up.
                //
                // Without this the slicing feeds itself: a frame that took a second leaves
                // Time.deltaTime at a second, which asks for ten slices, which takes longer than a
                // second, which asks for more. Profiling a stutter found exactly that — a frame of
                // 1,367 ms, all of it BehaviourUpdate, with the render thread sitting in
                // Gfx.WaitForGfxCommandsFromMainThread waiting for us (2026-09-23). The crew fall
                // a little behind the clock under load instead, which nobody can see.
                if (Time.realtimeSinceStartupAsDouble - startedAt > MaxTickMilliseconds / 1000.0)
                    break;

                var slice = Mathf.Min(left, MaxTickSeconds);
                left -= slice;
                Dispatcher.Tick(slice);
                foreach (var unit in _units)
                    unit.Tick(slice);
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                Deselect();
        }

        /// <summary>
        /// Selects the unit under the ray, if any: on its own, or added to the selection with
        /// <paramref name="add"/> (clicking a selected one then drops it). Returns whether a unit
        /// took the click, so the tools can leave it alone.
        /// </summary>
        public bool TrySelectAt(Ray ray, bool add = false)
        {
            if (!Physics.Raycast(ray, out var hit, 10000f))
                return false;
            var index = _bodies.IndexOf(hit.transform);
            if (index < 0)
                return false;
            if (!add)
                _selection.Clear();
            if (add && _selection.Contains(index))
                _selection.Remove(index);
            else
                _selection.Add(index);
            return true;
        }

        /// <summary>
        /// Selects every unit whose body is inside the screen rectangle (Input System pixels,
        /// origin bottom left); <paramref name="add"/> keeps the ones already selected. Returns how
        /// many are selected after.
        /// </summary>
        public int SelectInScreenRect(Rect box, Camera camera, bool add)
        {
            if (!add)
                _selection.Clear();
            for (var i = 0; i < _bodies.Count; i++)
            {
                // Aim at the ball, not the ground under it.
                var centre = _bodies[i].position + _bodies[i].up * (0.27f * bodyScale);
                if (SelectionBox.Contains(box, camera.WorldToScreenPoint(centre)) && !_selection.Contains(i))
                    _selection.Add(i);
            }

            return _selection.Count;
        }

        public void Deselect() => _selection.Clear();

        /// <summary>Selects unit <paramref name="index"/> of <see cref="Units"/>, on its own or added (Slice 17: the crew panel).</summary>
        public void Select(int index, bool add = false)
        {
            if (index < 0 || index >= _units.Count)
                return;
            if (!add)
                _selection.Clear();
            if (!_selection.Contains(index))
                _selection.Add(index);
        }

        public bool IsSelected(int index) => _selection.Contains(index);

        /// <summary>Where unit <paramref name="index"/>'s body is in the world, for the camera to go to.</summary>
        public Vector3 BodyPosition(int index) =>
            index >= 0 && index < _bodies.Count ? _bodies[index].position : Vector3.zero;

        /// <summary>
        /// Sends the selected units to the cell, spread over the nearest cells they can stand on,
        /// one each. Onto a dig, fill or dump designation they go back to work once there; onto
        /// anything else they hold. Returns how many took the order.
        /// </summary>
        public int OrderSelectedTo(int x, int z)
        {
            if (_selection.Count == 0)
                return 0;
            var grid = _terrain.Grid;
            var map = _designations.Map;
            var work = map.GetKind(x, z) != DesignationKind.None || map.IsDumpZone(x, z);
            var goal = new Vector2Int(x, z);
            var targets = CrewFormation.Targets(grid, goal, _selection.Count, grid.IsPassableGround);
            var starts = new List<Vector2>(_selection.Count);
            foreach (var index in _selection)
                starts.Add(_units[index].Position);
            var picks = CrewFormation.Assign(starts, targets, new Vector2(x + 0.5f, z + 0.5f));

            var ordered = 0;
            for (var s = 0; s < _selection.Count; s++)
                if (picks[s] >= 0 && _units[_selection[s]].OrderMoveTo(targets[picks[s]].x, targets[picks[s]].y, work))
                    ordered++;

            if (ordered > 0)
                ShowOrderMarker(grid, x, z);
            return ordered;
        }

        void ShowOrderMarker(TerrainGrid grid, int x, int z)
        {
            if (_orderMarker == null)
            {
                var marker = MakeRing("Order Marker", transform, 0.3f, 0.45f);
                if (marker == null)
                    return;
                _orderMarker = marker.transform;
                _orderMarkerRenderer = marker.GetComponent<Renderer>();
            }

            _orderMarker.gameObject.SetActive(true);
            _orderMarker.position = _terrain.transform.TransformPoint(
                TerrainSpace.CellCentre(grid, x, z, grid.GetSurfaceHeight(x, z) + 0.03f));
            _orderMarkerAt = Time.time;
        }

        void UpdateOrderMarker()
        {
            if (_orderMarker == null || !_orderMarker.gameObject.activeSelf)
                return;
            var t = (Time.time - _orderMarkerAt) / _orderMarkerSeconds;
            if (t >= 1f)
            {
                _orderMarker.gameObject.SetActive(false);
                return;
            }

            var radius = Mathf.Lerp(0.45f, 0.25f, t);
            _orderMarker.localScale = new Vector3(radius, 1f, radius);
            _markerBlock ??= new MaterialPropertyBlock();
            var colour = _markerMaterial.GetColor(BaseColorId);
            colour.a *= 1f - t;
            _markerBlock.SetColor(BaseColorId, colour);
            _orderMarkerRenderer.SetPropertyBlock(_markerBlock);
        }

        void LateUpdate()
        {
            UpdateOrderMarker();
            var terrainTransform = _terrain.transform;
            var grid = _terrain.Grid;
            var cellSize = grid.CellSize;
            for (var i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                // Unit positions are in cells; the terrain's local space is metres.
                var position = unit.Position * cellSize;
                var height = unit.Height;
                var rotation = terrainTransform.rotation * Quaternion.Euler(0f, unit.Heading, 0f);
                var up = rotation * Vector3.up;
                if (_groundPose)
                {
                    var print = new Footprint(_prints[i].x / cellSize, _prints[i].y / cellSize);
                    var fit = GroundPose.Fit(grid, unit.Position, unit.Heading, print, _maxTiltDegrees);
                    var tilt = _tilt[i];
                    var rate = _tiltRate[i];
                    // Smoothed, so a one-metre step reads as a lurch rather than a snap.
                    tilt.x = Mathf.SmoothDamp(tilt.x, fit.Pitch, ref rate.x, _tiltSmoothing);
                    tilt.y = Mathf.SmoothDamp(tilt.y, fit.Roll, ref rate.y, _tiltSmoothing);
                    _tilt[i] = tilt;
                    _tiltRate[i] = rate;
                    rotation = terrainTransform.rotation * GroundPose.Rotation(unit.Heading, tilt.x, tilt.y);
                    up = rotation * Vector3.up;
                    // The plane's height at the centre rather than the ground's, so a machine
                    // bridging a crest sits on it instead of sinking its tracks in. Taken as an
                    // offset from the centre sample so the unit's own height lag still applies.
                    height += fit.Height - TerrainSurface.SampleHeight(grid, unit.Position.x, unit.Position.y);
                }

                if (_renderers[i] != null)
                {
                    // Half the box the body actually is, not the size that was asked for: a road
                    // machine is shrunk after its parts go on (FitTo), and lifting it by the size
                    // before that would leave it hovering. Along the body's own up, so a tipped
                    // machine still rests on its ground rather than rising out of it.
                    var lift = up * (_bodies[i].localScale.y * 0.5f);
                    _bodies[i].SetPositionAndRotation(
                        terrainTransform.TransformPoint(new Vector3(position.x, height, position.y)) + lift, rotation);
                    _renderers[i].material.color = _selection.Contains(i) ? _selectedColor : BoxColor(unit.Role);
                }
                else
                {
                    // The robot's origin is the ground under it; it hovers by itself.
                    _bodies[i].SetPositionAndRotation(
                        terrainTransform.TransformPoint(new Vector3(position.x, height, position.y)), rotation);
                    _bodies[i].localScale = Vector3.one * bodyScale;
                    var state = CrewAnimation.StateFor(unit.State, !unit.Inventory.IsEmpty);
                    PlayClip(i, state);
                    DriveAtGroundSpeed(i, state);
                    Tint(i, _selection.Contains(i));
                    if (_rings[i] != null)
                        _rings[i].SetActive(_selection.Contains(i));
                }


                // The path still ahead: from the body to each remaining waypoint's centre.
                var path = unit.Path;
                var count = 0;
                if (_selection.Contains(i) && (unit.State == CrewUnitState.Moving || unit.State == CrewUnitState.Waiting)
                    && unit.PathIndex < path.Count)
                {
                    _pathPoints[count++] = terrainTransform.TransformPoint(new Vector3(position.x, unit.Height + 0.3f, position.y));
                    for (var p = unit.PathIndex; p < path.Count && count < _pathPoints.Length; p++)
                    {
                        var x = path[p].x + 0.5f;
                        var z = path[p].y + 0.5f;
                        _pathPoints[count++] = terrainTransform.TransformPoint(new Vector3(x * cellSize, TerrainSurface.SampleHeight(grid, x, z) + 0.3f, z * cellSize));
                    }
                }

                _lines[i].positionCount = count;
                _lines[i].SetPositions(_pathPoints);
            }
        }

        /// <summary>
        /// Turns a machine's tracks or wheels at the speed it is actually covering ground, so they
        /// neither skid nor spin: one loop of the drive clip is <see cref="MachineBody.MetresPerDriveLoop"/>
        /// of travel. Anything but driving plays at its authored speed.
        /// </summary>
        void DriveAtGroundSpeed(int i, string state)
        {
            var drawn = _bodies[i].position;
            var moved = Vector3.Distance(drawn, _lastDrawn[i]);
            _lastDrawn[i] = drawn;
            var machine = _machines[i];
            var animator = _animators[i];
            if (machine == null || animator == null)
                return;
            if (state != CrewAnimation.Move && state != CrewAnimation.Carry || Time.deltaTime <= 0f)
            {
                animator.speed = 1f;
                return;
            }

            var loopsPerSecond = moved / Time.deltaTime / machine.MetresPerDriveLoop;
            animator.speed = loopsPerSecond * machine.DriveLoopSeconds;
        }

        void PlayClip(int i, string clip)
        {
            var animator = _animators[i];
            if (animator == null || _clips[i] == clip)
                return;
            _clips[i] = clip;
            animator.CrossFadeInFixedTime(clip, _clipBlend);
        }

        /// <summary>
        /// Tints the selected robot, each material by its own colour times the tint, and clears it
        /// again when deselected. Only on a change, so unselected robots keep no property blocks
        /// and stay batched.
        /// </summary>
        void Tint(int i, bool selected)
        {
            if (_tinted[i] == selected)
                return;
            _tinted[i] = selected;
            _tint ??= new MaterialPropertyBlock();
            foreach (var r in _robotRenderers[i])
            {
                if (r.name == "CrewGlow")
                    continue;
                var materials = r.sharedMaterials;
                for (var m = 0; m < materials.Length; m++)
                {
                    if (!selected || materials[m] == null || !materials[m].HasProperty(BaseColorId))
                    {
                        r.SetPropertyBlock(null, m);
                        continue;
                    }

                    _tint.Clear();
                    _tint.SetColor(BaseColorId, materials[m].GetColor(BaseColorId) * _selectedTint);
                    r.SetPropertyBlock(_tint, m);
                }
            }
        }

        /// <summary>Metres of reach as whole height steps, never less than one.</summary>
        int Levels(float metres)
        {
            var step = _terrain.Grid.HeightStep > 0f ? _terrain.Grid.HeightStep : 1f;
            return Mathf.Max(1, Mathf.RoundToInt(metres / step));
        }

        void OnDestroy()
        {
            foreach (var unit in _units)
                unit.Dispose();
            _units.Clear();
            Dispatcher?.Dispose();
        }
    }
}
