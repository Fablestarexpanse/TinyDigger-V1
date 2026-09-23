using System;
using System.Collections.Generic;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>What a unit is for. Both carry a <see cref="MaterialInventory"/>.</summary>
    /// <summary>
    /// What each kind of unit is called where the player can see it (Ronan, 2026-09-22): "digger
    /// mech, dumper mech, starter robot are good". The roles keep their own names in the code,
    /// which say what a unit does rather than what it is.
    /// </summary>
    public static class UnitNames
    {
        public const string Worker = "Starter robot";
        public const string Digger = "Digger mech";
        public const string Hauler = "Dumper mech";

        /// <summary>What to call a unit of this role.</summary>
        public static string Of(UnitRole role)
        {
            switch (role)
            {
                case UnitRole.Digger: return Digger;
                case UnitRole.Hauler: return Hauler;
                default: return Worker;
            }
        }
    }

    public enum UnitRole
    {
        /// <summary>Digs and fills the ground, and empties its scoop into a hauler or an area to tip in.</summary>
        Digger,

        /// <summary>Carries spoil: it never touches the ground itself, only takes loads and tips them.</summary>
        Hauler,

        /// <summary>
        /// The first unit: a robot with a barrow. It digs and carries its own small load to tip,
        /// and never waits for a hauler.
        /// </summary>
        Worker,
    }

    public enum CrewUnitState
    {
        Idle,
        Moving,
        Digging,
        Tipping,

        /// <summary>Emptying a scoop into a hauler standing next to it.</summary>
        Transferring,

        /// <summary>A hauler standing beside its digger, taking whatever it digs.</summary>
        Parked,

        /// <summary>A digger with a full scoop, waiting for its hauler to arrive.</summary>
        WaitingForHauler,

        /// <summary>There is work designated, but none of it can be reached and worked from.</summary>
        Unreachable,

        /// <summary>Something is waiting to be filled and there is nothing to fill it with, and no quarry to dig.</summary>
        NeedsMaterial,

        /// <summary>Held up by another unit on the next cell of its path.</summary>
        Waiting,

        /// <summary>Loaded, with nowhere it is allowed to tip. The player needs to say where.</summary>
        NeedsSomewhereToTip,
    }

    public enum CrewJobKind
    {
        None,
        Dig,
        Fill,

        /// <summary>Digging a player-marked quarry for material a Fill is waiting for.</summary>
        Quarry,

        /// <summary>Tipping a load on a player-marked Dump Zone.</summary>
        DumpZone,

        /// <summary>Emptying a scoop into a hauler.</summary>
        Transfer,

        /// <summary>A hauler driving to, or standing at, a digger it serves.</summary>
        Serve,

        /// <summary>Driving out of another unit's way, having given its job up.</summary>
        Yield,

        /// <summary>Climbing out of water that has come up round it, having given its job up.</summary>
        Escape,

        /// <summary>Going where the player ordered it.</summary>
        Go,
    }

    /// <summary>
    /// One member of the crew: a digger or a hauler (<see cref="UnitRole"/>). Plain C#: the scene
    /// side calls <see cref="Tick"/> and draws the body at <see cref="Position"/>,
    /// <see cref="Height"/> and <see cref="Heading"/>.
    ///
    /// Everything shared by the crew belongs to the <see cref="JobDispatcher"/>: the designations,
    /// which of them each unit has claimed, reachability, benching, Auto ramps, who is standing
    /// where, and which hauler serves which digger.
    ///
    /// **Nothing is ever tipped on undesignated ground.** A load can only go into a hauler, a Fill
    /// designation or a Dump Zone. A unit that is loaded with nowhere it may tip stops in state
    /// <see cref="CrewUnitState.NeedsSomewhereToTip"/> and says so; that is a question for the
    /// player, not a fault.
    ///
    /// **Tipping is done from the rim** (TERRAIN_REFERENCE.md section 4). To tip into an area, a
    /// unit stands on any reachable cell beside any cell of it and tips onto that cell. The cell
    /// may be any depth below the unit — dig reach only limits how far *up* it can tip — and slump
    /// carries the material on into the area. Rims above the cell they tip onto are preferred, so
    /// material runs in rather than having to be pushed. A Fill designation is met when every cell
    /// of it is at or above its height, however the material got there.
    ///
    /// Digger job loop:
    /// - Dig the nearest unclaimed Dig designation it can reach, one height step at a time, until
    ///   it is met or the scoop is full.
    /// - Full, with a hauler beside it: empty into the hauler (<see cref="TransferRate"/>).
    /// - Full, with any hauler in the crew: wait for one, or walk the last few cells to one that
    ///   has parked nearby. Diggers never carry spoil while there is a hauler to carry it.
    /// - No hauler in the crew at all: tip into a Fill designation or Dump Zone itself, from the
    ///   rim.
    ///
    /// Hauler job loop:
    /// - Loaded: empty the bed, tipping into the nearest Fill designation it can reach the rim
    ///   of, else the nearest Dump Zone with room under its cap. It finishes unloading before it
    ///   goes back to a digger.
    /// - Empty (or part loaded with nothing to tip into): serve the reachable digger with the
    ///   fullest load that has no hauler, parking beside it. It leaves when full, when the digger
    ///   stops, having waited <see cref="ParkPatience"/> with nothing more coming. A working
    ///   digger it waits on however long the cut takes: the bed leaves full.
    ///
    /// Traffic: units never share a cell, and hold the one they are driving into until they
    /// arrive, so they never overlap on a boundary or cut a diagonal past one another. One whose
    /// next path cell is taken waits up to
    /// <see cref="TrafficWaitSeconds"/>, then re-paths around the units in the way, and stands
    /// aside if there is no way round.
    /// </summary>
    public sealed class CrewUnit : IDisposable
    {
        const float Epsilon = 1e-3f;
        static readonly int[] NeighbourX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] NeighbourZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>Metres per second.</summary>
        public float Speed = 3f;

        /// <summary>How many height steps above its own cell the unit can dig, or tip up to.</summary>
        public int DigReachLevels = 2;

        /// <summary>
        /// How far *below* its own feet a unit can cut, in height steps.
        ///
        /// A machine standing on the rim works the face in front of it rather than driving down
        /// into the hole (Ronan, 2026-09-22: "the digger should be able to stand there at the
        /// edge and keep digging through it without needing a ramp"). How deep is the arm's
        /// business: the digger's reaches about two metres below its feet, four steps, where a
        /// robot with a shovel manages the same couple of steps it can reach up.
        /// </summary>
        public int DigDepthLevels = 2;

        /// <summary>The digger machine's arm, in height steps below its feet.</summary>
        public const int MachineDigDepth = 4;

        /// <summary>
        /// How far up a face of *any* material a unit can work from its foot, in height steps.
        ///
        /// A machine does not climb a mound to take it down; it stands at the bottom and digs
        /// into the pile (Ronan, 2026-09-22: "if it's digging into a mound or wall or face it
        /// doesn't need to be on top"). Nought for a robot, which works what it can reach.
        /// </summary>
        public int FaceReachLevels;

        /// <summary>How far up a face the digger's arm reaches, in height steps.</summary>
        public const int MachineFaceReach = 3;

        /// <summary>
        /// How far out the unit can work, in cells. One is arm's length — the eight cells round
        /// it. The digger machine reaches further, because it has a boom (Ronan, 2026-09-22: "the
        /// excavator bot has a long excavator arm so it should be able to reach out a certain
        /// distance to dig").
        /// </summary>
        public int WorkReachCells = 1;

        /// <summary>
        /// The digger's arm, in cells: a metre on half-metre cells. Three looked too far for the
        /// machine's own arm (Ronan, 2026-09-22: "its reach might be too far").
        /// </summary>
        public const int MachineWorkReach = 2;

        /// <summary>
        /// Whether this unit will step down into a cut and work it from inside, a bench at a time.
        /// A machine will: it can climb back out, and that is how a cut is worked. A robot with a
        /// barrow keeps out of the hole and waits for a way down, which is the older rule and the
        /// one its own tests are written to.
        /// </summary>
        public bool WorksFromInside;

        /// <summary>
        /// Whether the whole load goes down in one tip. A dumper mech raises its bed once and the
        /// lot falls out in a heap, which then slumps to its angle of repose like any other spoil
        /// (Ronan, 2026-09-22: "the dumper mech should dump entire load in one tip"). A robot with
        /// a barrow puts it down a step at a time, which is all a barrow can do.
        /// </summary>
        public bool TipsWholeLoad;

        /// <summary>Places to stand to work a cell, nearest first; reused so the scan allocates nothing.</summary>
        readonly List<Vector2Int> _stands = new List<Vector2Int>();

        /// <summary>The last cell this unit cut, so it carries on along the same face.</summary>
        Vector2Int _lastCut = new Vector2Int(-1, -1);

        /// <summary>
        /// How far up a rock face a unit can work from its foot, in height steps. Taller than the
        /// ordinary dig reach on purpose: see <see cref="WithinDigReach"/>.
        /// </summary>
        public int CliffReachLevels = 6;

        /// <summary>Seconds per height step dug, or per tip, unless the job has its own time.</summary>
        public float WorkInterval = 0.4f;

        /// <summary>
        /// Seconds for one cut, or nought to use <see cref="WorkInterval"/>.
        ///
        /// A machine takes as long over a scoop as its scoop takes to swing (Ronan, 2026-09-22:
        /// "every dig needs a matching animation, the time to scoop and load truck"), so whatever
        /// draws it sets this from the clip it plays. The crew logic keeps no animation of its
        /// own; it is told how long the work looks.
        /// </summary>
        public float DigSeconds;

        /// <summary>Seconds for one tip, or nought to use <see cref="WorkInterval"/>.</summary>
        public float TipSeconds;

        /// <summary>Loose m³ a second moved from a digger's scoop into a hauler.</summary>
        public float TransferRate = 5f;

        /// <summary>Seconds a parked hauler waits for something to be tipped into it before leaving.</summary>
        public float ParkPatience = 10f;

        /// <summary>How far from its digger a hauler may park when there is no room right beside it.</summary>
        public int ParkRadius = 3;

        /// <summary>Degrees per second the body turns toward its direction of travel.</summary>
        public float TurnRate = 360f;

        /// <summary>How quickly the body's height catches up with the surface under it; higher is snappier.</summary>
        public float HeightFollowRate = 10f;

        /// <summary>Seconds between re-checks for work while idle or unreachable, in case the world changed.</summary>
        public float IdleRethinkInterval = 1f;

        /// <summary>Seconds between a waiting digger's looks for its hauler.</summary>
        public float HaulerCheckInterval = 0.25f;

        /// <summary>Seconds a unit waits for another one to move off its next cell before going round.</summary>
        public float TrafficWaitSeconds = 1f;

        /// <summary>
        /// How much room this unit takes, as a radius in cells: two units keep the sum of their
        /// radii between their centres.
        ///
        /// The crew's 0.35 makes the old 0.7 between two of them — just under the 0.707 of a
        /// diagonal pass, so they still drive past one another corner to corner but never close
        /// in head-on or overlap across a cell edge. The machines are set from their models:
        /// half of a metre-long machine is about 1.1 cells at the sandbox's half-metre cells, and
        /// without it a digger and a dumper stand inside each other.
        /// </summary>
        public float Radius = CrewRadius;

        /// <summary>The starter robot, 0.41 m across on half-metre cells.</summary>
        public const float CrewRadius = 0.35f;

        /// <summary>The digger machine: about 1.1 m long, so 2.2 cells, so 1.1 either side.</summary>
        public const float DiggerRadius = 1.1f;

        /// <summary>The dumper: a little shorter than the digger, a little wider.</summary>
        public const float HaulerRadius = 0.95f;

        readonly TerrainGrid _grid;
        readonly DesignationMap _designations;
        readonly GridPathfinder _pathfinder;
        readonly JobDispatcher _dispatcher;
        readonly bool _ownsDispatcher;
        readonly List<Vector2Int> _path = new List<Vector2Int>();
        readonly bool[] _onPath;
        readonly List<int> _onPathCells = new List<int>();
        readonly List<int> _unreachableDigs = new List<int>();

        int _pathIndex;
        float _jobHeight;
        bool _repath;

        /// <summary>Seconds until a unit stuck in water with no way out looks for one again.</summary>
        float _escapeRetry;
        bool _rethink = true;
        float _rethinkTimer;
        float _workTimer;
        float _waitTimer;

        /// <summary>Seconds it has stood on the same spot while blocked. See <see cref="Move"/>.</summary>
        float _stuckTimer;

        /// <summary>Where it was when it last actually moved.</summary>
        Vector2 _lastMoved;

        /// <summary>The lowest cell of the dump zones when the tip in hand was planned.</summary>
        float _zoneFloor = float.MinValue;
        float _parkTimer;
        int _parkLoadVersion;
        bool _loadFull;
        bool _ordered;
        bool _workOnArrival;
        Vector2Int _orderTarget;
        bool _tipHighRimsOnly;
        bool _disposed;

        public CrewUnit(TerrainGrid grid, DesignationMap designations, GridPathfinder pathfinder, int startX, int startZ, float capacity = MaterialInventory.DefaultCapacity)
            : this(new JobDispatcher(grid, designations, pathfinder), startX, startZ, UnitRole.Digger, capacity, ownsDispatcher: true)
        {
        }

        public CrewUnit(JobDispatcher dispatcher, int startX, int startZ, float capacity = MaterialInventory.DefaultCapacity)
            : this(dispatcher, startX, startZ, UnitRole.Digger, capacity, ownsDispatcher: false)
        {
        }

        public CrewUnit(JobDispatcher dispatcher, int startX, int startZ, UnitRole role, float capacity)
            : this(dispatcher, startX, startZ, role, capacity, ownsDispatcher: false)
        {
        }

        CrewUnit(JobDispatcher dispatcher, int startX, int startZ, UnitRole role, float capacity, bool ownsDispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _ownsDispatcher = ownsDispatcher;
            _grid = dispatcher.Grid;
            _designations = dispatcher.Designations;
            _pathfinder = dispatcher.Pathfinder;
            if (!_grid.InBounds(startX, startZ))
                throw new ArgumentOutOfRangeException(nameof(startX), "The unit must start on the map.");

            Role = role;
            Inventory = new MaterialInventory(capacity);
            Position = new Vector2(startX + 0.5f, startZ + 0.5f);
            Height = TerrainSurface.SampleHeight(_grid, Position.x, Position.y);
            _onPath = new bool[_grid.Width * _grid.Height];

            Id = _dispatcher.Register(this);
            _dispatcher.SetCell(Id, startX, startZ);
            _grid.CellChanged += OnCellChanged;
            _grid.WaterChanged += OnCellChanged;
            _designations.Changed += OnDesignationChanged;
            Status = "Idle";
        }

        /// <summary>This unit's place in the crew; the dispatcher's key for it.</summary>
        public int Id { get; }

        public UnitRole Role { get; }

        public JobDispatcher Dispatcher => _dispatcher;

        public MaterialInventory Inventory { get; }

        /// <summary>Position on the map in cell units; cell (i, j) spans i..i+1, j..j+1.</summary>
        public Vector2 Position { get; private set; }

        /// <summary>Height of the ground under the body, smoothed.</summary>
        public float Height { get; private set; }

        /// <summary>Degrees clockwise from +z, the direction the body faces.</summary>
        public float Heading { get; private set; }

        public Vector2Int Cell => new Vector2Int(
            Math.Min(Math.Max((int)Math.Floor(Position.x), 0), _grid.Width - 1),
            Math.Min(Math.Max((int)Math.Floor(Position.y), 0), _grid.Height - 1));

        public CrewUnitState State { get; private set; }

        public CrewJobKind Job { get; private set; }

        /// <summary>The cell being dug, tipped onto, or the unit being served.</summary>
        public Vector2Int JobTarget { get; private set; }

        /// <summary>The cell the unit works from.</summary>
        public Vector2Int JobStand { get; private set; }

        /// <summary>The path being followed, including cells already passed; <see cref="PathIndex"/> is the next waypoint.</summary>
        public IReadOnlyList<Vector2Int> Path => _path;

        public int PathIndex => _pathIndex;

        /// <summary>One line describing what the unit is doing, for the readout.</summary>
        public string Status { get; private set; }

        /// <summary>Designations that no reachable cell can work, as of the last job choice.</summary>
        public int UnreachableCount { get; private set; }

        /// <summary>The unreachable designation closest to the unit, described; empty if none.</summary>
        public string NearestUnreachable { get; private set; } = "";

        /// <summary>Whether the last look for somewhere to tip found Dump Zone cells but no room under their caps.</summary>
        public bool DumpZoneFull { get; private set; }

        /// <summary>Seconds this digger has spent with a full scoop waiting for a hauler.</summary>
        public float WaitedForHauler { get; private set; }

        /// <summary>How many times it has had to stop and wait for a hauler.</summary>
        public int HaulerWaits { get; private set; }

        /// <summary>Loose m³ this unit has handed to haulers, or taken from diggers.</summary>
        public float Transferred { get; private set; }

        /// <summary>Changes whenever state, job or status changes, so displays redraw only then.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// How many times this unit has waited out a dig interval only to find it could not cut
        /// the cell after all. A unit that plans a cut, walks to it and is refused thinks again
        /// and comes straight back, so it sits in Digging looking busy while nothing moves; this
        /// counter is the only way to tell that apart from work.
        /// </summary>
        public int RefusedCuts;

        /// <summary>How many times it waited out an interval while standing off its own job stand.</summary>
        public int OffStand;

        /// <summary>How many cuts it has actually taken out of the ground.</summary>
        public int LandedCuts;

        /// <summary>Why the last cut it planned would not go when it got there.</summary>
        public string LastRefusal = "";

        /// <summary>
        /// Loose m³ of room the last cut that would not fit needed, or nought when the last cut
        /// landed. A unit is full for as long as it has less room than this, whatever a step
        /// measures in place: see <see cref="DigStep"/>.
        /// </summary>
        public float NeedsRoom;

        /// <summary>How many times <see cref="Work"/> has been called with the unit digging.</summary>
        public int DigTicks;

        /// <summary>How many times the path was re-planned, because a cell on it changed or a unit was in the way.</summary>
        public int RepathCount { get; private set; }

        /// <summary>How long it has been held up by another unit, in seconds.</summary>
        public float WaitingFor => _waitTimer;

        /// <summary>Whether this hauler has room and somewhere to take a load: worth waiting for.</summary>
        public bool CanTakeALoad => Role == UnitRole.Hauler
            && Inventory.Remaining > Epsilon
            && State != CrewUnitState.NeedsSomewhereToTip;

        /// <summary>The hauler serving this digger, or the digger this hauler serves; -1 for neither.</summary>
        public int Partner => Role == UnitRole.Hauler ? _dispatcher.DiggerFor(Id) : _dispatcher.HaulerFor(Id);

        /// <summary>Whether it digs: diggers and workers do, haulers never touch the ground.</summary>
        public bool Digs => Role != UnitRole.Hauler;

        /// <summary>
        /// Whether this unit is still at work and so still has spoil coming: digging it, carrying
        /// it, handing it over, or on its way to the next cut. A hauler parked beside it waits on
        /// this rather than on a clock.
        /// </summary>
        public bool StillWorking =>
            State == CrewUnitState.Digging || State == CrewUnitState.Moving
            || State == CrewUnitState.WaitingForHauler || State == CrewUnitState.Transferring;

        /// <summary>
        /// Whether this unit is built to cut stone. The digger machine is (see
        /// <see cref="AsMachine"/>); anything else is not, and pays <see cref="StoneEffort"/> for
        /// every step of rock it takes on (Ronan, 2026-09-22: it can, but slowly).
        /// </summary>
        public bool CutsStone;

        /// <summary>
        /// How much longer a step takes for a unit that is not built for stone, at the hardest
        /// rock. Four makes a robot about a quarter the speed of the machine in rock, and leaves
        /// soil untouched: only stone is charged for.
        /// </summary>
        public float StoneEffort = 4f;

        /// <summary>
        /// Seconds for one step of the cell at (<paramref name="x"/>, <paramref name="z"/>): the
        /// unit's own <see cref="WorkInterval"/>, and for a unit without a stone cutter, longer
        /// the harder the rock is. The hardness is the material's own, so granite costs more than
        /// soft rock without a separate table of numbers to keep.
        /// </summary>
        public float StepSeconds(int x, int z)
        {
            var cut = DigSeconds > 0f ? DigSeconds : WorkInterval;
            if (CutsStone || !_grid.InBounds(x, z))
                return cut;
            var material = _grid.GetTopMaterial(x, z);
            if (!MaterialTable.IsStone(material))
                return cut;
            return cut * (1f + _grid.Materials.Get(material).Hardness * StoneEffort);
        }

        /// <summary>
        /// Holding where the player sent it (or on its way there): it takes no work until
        /// <see cref="ReleaseHold"/>, or an order to go and work.
        /// </summary>
        public bool Holding { get; private set; }

        /// <summary>Where the player last sent it, while it has not got there yet.</summary>
        public Vector2Int? OrderTarget => _ordered ? _orderTarget : (Vector2Int?)null;

        // Crew-wide settings and state live on the dispatcher; these reach them for convenience.

        public bool AutoRamp
        {
            get => _dispatcher.AutoRamp;
            set => _dispatcher.AutoRamp = value;
        }

        public bool Benching
        {
            get => _dispatcher.Benching;
            set => _dispatcher.Benching = value;
        }

        public float RampCancelSeconds
        {
            get => _dispatcher.RampCancelSeconds;
            set => _dispatcher.RampCancelSeconds = value;
        }

        public float RampBlockedSeconds
        {
            get => _dispatcher.RampBlockedSeconds;
            set => _dispatcher.RampBlockedSeconds = value;
        }

        public bool HasRamp => _dispatcher.HasRamp;

        public Vector2Int RampTarget => _dispatcher.RampTarget;

        public string RampNote => _dispatcher.RampNote;

        public IReadOnlyList<Vector2Int> RampCorridor => _dispatcher.RampCorridor;

        public bool OnAutoRamp => Job == CrewJobKind.Dig && _designations.IsAuto(JobTarget.x, JobTarget.y);

        public bool IsRampSuppressed(int x, int z) => _dispatcher.IsRampSuppressed(x, z);

        public float DigFloor(int x, int z) => _dispatcher.DigFloor(x, z);

        float Step => _grid.HeightStep > 0f ? _grid.HeightStep : 1f;

        /// <summary>m³ in one height step on one cell: what a load has to hold to tip a step.</summary>
        float StepVolume => Step * _grid.CellArea;

        /// <summary>
        /// Whether a unit standing at height <paramref name="standHeight"/> can dig or fill a cell
        /// at <paramref name="cellHeight"/>: up to <see cref="DigReachLevels"/> above it, and down
        /// to <see cref="DigDepthLevels"/> below, which is as far as its arm goes.
        /// </summary>
        public bool WithinReach(float standHeight, float cellHeight)
        {
            var difference = cellHeight - standHeight;
            return difference >= -(DigDepthLevels * Step + Epsilon)
                && difference <= DigReachLevels * Step + Epsilon;
        }

        /// <summary>
        /// Whether a unit standing at <paramref name="standHeight"/> can dig the cell at
        /// (<paramref name="x"/>, <paramref name="z"/>).
        ///
        /// Ordinary reach, plus two ways of working a face that stands over the unit, taking the
        /// top step off at a time:
        ///
        /// - **any face**, up to <see cref="FaceReachLevels"/>: a machine digs into a mound from
        ///   the bottom rather than climbing it to work from the top;
        /// - a **rock face**, up to <see cref="CliffReachLevels"/>, for anybody. Without that a
        ///   cliff is not merely unclimbable, which it should be, but unworkable, and a hill with
        ///   a cliff on it could never be taken down. Soil needs no such allowance for a robot: a
        ///   soil face that tall cannot stand up, because it slumps.
        /// </summary>
        public bool WithinDigReach(float standHeight, int x, int z)
        {
            var cellHeight = _grid.GetSurfaceHeight(x, z);
            if (WithinReach(standHeight, cellHeight))
                return true;
            var above = cellHeight - standHeight;
            if (above <= FaceReachLevels * Step + Epsilon)
                return true;
            return above <= CliffReachLevels * Step + Epsilon
                && MaterialTable.IsStone(_grid.GetTopMaterial(x, z));
        }

        /// <summary>
        /// Every cell this unit could stand on to work (<paramref name="x"/>, <paramref name="z"/>),
        /// out to its arm's reach and nearest first, so it works from where it already is rather
        /// than walking round the pit to a far side it could have reached.
        /// </summary>
        List<Vector2Int> StandsAround(int x, int z)
        {
            _stands.Clear();
            var reach = Math.Max(1, WorkReachCells);
            for (var ring = 1; ring <= reach; ring++)
                for (var dz = -ring; dz <= ring; dz++)
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                            continue;
                        var sx = x + dx;
                        var sz = z + dz;
                        if (_grid.InBounds(sx, sz))
                            _stands.Add(new Vector2Int(sx, sz));
                    }

            return _stands;
        }

        /// <summary>Whether a unit on (standX, standZ) can work the cell (targetX, targetZ).</summary>
        public bool CanWork(int standX, int standZ, int targetX, int targetZ)
        {
            if (!_grid.InBounds(standX, standZ) || !_grid.InBounds(targetX, targetZ))
                return false;
            var out_x = Math.Abs(standX - targetX);
            var out_z = Math.Abs(standZ - targetZ);
            var inReach = Math.Max(out_x, out_z) <= Math.Max(1, WorkReachCells) && (out_x != 0 || out_z != 0);
            return inReach && WithinReach(_grid.GetSurfaceHeight(standX, standZ), _grid.GetSurfaceHeight(targetX, targetZ));
        }

        /// <summary>
        /// Fits this unit out as the machine its role names: the digger cuts stone, and both
        /// machines take a machine's room.
        ///
        /// A role says what a unit is *for*, not what it is: plenty of units dig without being
        /// the digger machine, and sizing them all as one jammed every crew in the game solid.
        /// So a unit is crew-sized and cannot cut rock quickly until something says otherwise,
        /// and the thing that knows is whatever spawns it.
        /// </summary>
        public CrewUnit AsMachine()
        {
            CutsStone = Role == UnitRole.Digger;
            TipsWholeLoad = Role == UnitRole.Hauler;
            if (Role == UnitRole.Digger)
            {
                DigDepthLevels = MachineDigDepth;
                FaceReachLevels = MachineFaceReach;
                WorkReachCells = MachineWorkReach;
                WorksFromInside = true;
            }
            Radius = Role == UnitRole.Digger ? DiggerRadius
                : Role == UnitRole.Hauler ? HaulerRadius : CrewRadius;
            return this;
        }

        /// <summary>Makes the unit choose its job afresh on the next tick, as if the world had changed.</summary>
        public void RequestRethink() => _rethink = true;

        public void Tick(float deltaTime)
        {
            if (_disposed)
                return;

            if (_ownsDispatcher)
                _dispatcher.Tick(deltaTime);

            if (State == CrewUnitState.WaitingForHauler)
            {
                WaitedForHauler += deltaTime;
                _rethinkTimer -= deltaTime;
                if (_rethinkTimer <= 0f)
                    _rethink = true;
            }
            else if (State == CrewUnitState.Idle || State == CrewUnitState.Unreachable
                || State == CrewUnitState.NeedsSomewhereToTip || State == CrewUnitState.NeedsMaterial)
            {
                _rethinkTimer -= deltaTime;
                if (_rethinkTimer <= 0f)
                    _rethink = true;
            }

            _escapeRetry -= deltaTime;
            if (Job != CrewJobKind.Escape && _escapeRetry <= 0f && IsInWater() && !TryEscape())
                _escapeRetry = 1f;

            if (_rethink)
                ChooseJob();

            switch (State)
            {
                case CrewUnitState.Moving:
                case CrewUnitState.Waiting:
                    Move(deltaTime);
                    break;
                case CrewUnitState.Digging:
                case CrewUnitState.Tipping:
                    Work(deltaTime);
                    break;
                case CrewUnitState.Transferring:
                    TransferStep(deltaTime);
                    break;
                case CrewUnitState.Parked:
                    ParkStep(deltaTime);
                    break;
            }

            var cell = Cell;
            _dispatcher.SetCell(Id, cell.x, cell.y);
            var ground = TerrainSurface.SampleHeight(_grid, Position.x, Position.y);
            Height += (ground - Height) * (1f - (float)Math.Exp(-HeightFollowRate * deltaTime));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _grid.CellChanged -= OnCellChanged;
            _grid.WaterChanged -= OnCellChanged;
            _designations.Changed -= OnDesignationChanged;
            _dispatcher.Unregister(this);
            if (_ownsDispatcher)
                _dispatcher.Dispose();
        }

        // --- orders -------------------------------------------------------------------------------

        /// <summary>
        /// The player sends it to a cell. It drops whatever it was doing (keeping any load), goes
        /// there and holds, taking no work, until <see cref="ReleaseHold"/>. With
        /// <paramref name="workOnArrival"/> it goes back to work once there instead, which is how
        /// an order onto designated ground reads. False, and nothing changes, when the cell is off
        /// the map or it has no way there.
        /// </summary>
        public bool OrderMoveTo(int x, int z, bool workOnArrival = false)
        {
            if (!_grid.InBounds(x, z))
                return false;
            var me = Cell;
            if (me.x != x || me.y != z)
            {
                var path = new List<Vector2Int>();
                if (!_pathfinder.TryFindPath(me.x, me.y, x, z, path))
                    return false;
            }

            _ordered = true;
            _orderTarget = new Vector2Int(x, z);
            _workOnArrival = workOnArrival;
            Holding = true;
            if (!TryPlanOrder())
                Hold();
            return true;
        }

        /// <summary>Lets it go back to picking its own work.</summary>
        public void ReleaseHold()
        {
            Holding = false;
            _ordered = false;
            _workOnArrival = false;
            _rethink = true;
        }

        bool TryPlanOrder()
        {
            _dispatcher.Release(Id);
            ClearPath();
            var me = Cell;
            if (me == _orderTarget)
            {
                Job = CrewJobKind.Go;
                JobTarget = JobStand = _orderTarget;
                Arrive();
                return true;
            }

            if (!_pathfinder.TryFindPath(me.x, me.y, _orderTarget.x, _orderTarget.y, _path))
                return false;
            Job = CrewJobKind.Go;
            JobTarget = _orderTarget;
            JobStand = _orderTarget;
            _pathIndex = 0;
            _rethink = false;
            _repath = false;
            MarkPath();
            SetState(CrewUnitState.Moving, "Ordered to " + DescribeJob());
            return true;
        }

        void Hold()
        {
            Job = CrewJobKind.None;
            _dispatcher.Release(Id);
            var me = Cell;
            SetState(CrewUnitState.Idle, _ordered && me != _orderTarget
                ? $"Holding at ({me.x}, {me.y}): no way to ({_orderTarget.x}, {_orderTarget.y})"
                : $"Holding at ({me.x}, {me.y})");
        }

        // --- choosing work ------------------------------------------------------------------------

        void ChooseJob()
        {
            _rethink = false;
            _rethinkTimer = IdleRethinkInterval;
            ClearPath();
            var start = Cell;
            if (Holding)
            {
                // An order it has not reached yet is picked up again (after standing aside, say,
                // or a path that closed); otherwise it stays put.
                if (_ordered && start != _orderTarget && TryPlanOrder())
                    return;
                Hold();
                return;
            }

            UpdateUnreachable();

            // Work it cannot reach asks for a way in whatever else it is doing. A ramp used to be
            // asked for only by a unit with nothing left to plan, and a crew with spoil in its
            // barrows always has somewhere else to be: it shuttles to the tip and back for ever
            // while the cut it cannot get into sits there. A pit's outer ring came down and the
            // middle was never touched, with not one ramp cut (2026-09-22). Cutting the ramp is
            // still ordinary digging — some unit with room takes the step when it comes to plan —
            // so this only puts the step on the map.
            if (Digs && _unreachableDigs.Count > 0)
                _dispatcher.RequestRamp(this, _unreachableDigs);

            if (Role == UnitRole.Hauler)
                ChooseHaulerJob(start);
            else
                ChooseDiggerJob(start);
        }

        void ChooseDiggerJob(Vector2Int start)
        {
            var step = StepVolume;
            // A tip raises a cell by a whole step, so a load smaller than that cannot be put down
            // anywhere. Latched full with such a remnant — a barrow that tipped most of its load
            // and kept a corner of it — a unit was too full to dig and too light to tip, and
            // stood in front of a job for ever. It tops up instead. The latch still holds for a
            // scoop that is full enough to tip, which is what stops a unit taking part cuts for
            // ever without emptying.
            // Room enough for a cut, measured the way the ground measures it. A step is an
            // in-place volume; the load holds loose material, and ground that bulks needs more
            // room than it stood in. Where the last cut was refused for want of room, the ground
            // said exactly how much it wanted, and that is the figure to believe.
            var room = Math.Max(step, NeedsRoom);
            if (_loadFull && Inventory.Total + Epsilon < step && Inventory.Remaining + Epsilon >= room)
                _loadFull = false;
            var full = _loadFull || Inventory.Remaining + Epsilon < room;
            if (!full && TryPlan(CrewJobKind.Dig, start))
                return;

            // Nothing left to dig, but something waiting to be filled: quarry for it. Material has
            // to come from somewhere (Ronan, 2026-09-22).
            if (!full && _designations.FillCount > 0 && TryPlan(CrewJobKind.Quarry, start))
                return;

            // Cutting a ramp is digging, so it only happens with room in the scoop.
            if (!full && _unreachableDigs.Count > 0 && _dispatcher.RequestRamp(this, _unreachableDigs) && TryPlan(CrewJobKind.Dig, start))
                return;

            var loaded = Inventory.Total + Epsilon >= step;
            if (loaded)
            {
                // A worker carries its own barrow to tip; only a digger deals with haulers.
                if (Role == UnitRole.Digger)
                {
                    // A hauler already beside it takes the load without either of them moving.
                    if (TryTransferToAdjacentHauler())
                        return;

                    // Waiting for a hauler beats driving the spoil anywhere itself, which is the
                    // point of having haulers; a crew with no haulers falls through and tips it
                    // itself.
                    var mine = _dispatcher.UnitOf(_dispatcher.HaulerFor(Id));
                    if (mine != null && mine.CanTakeALoad)
                    {
                        // Benching takes diggers onto the designated ground, where a hauler has
                        // nowhere clear to park beside them, so the digger walks the last few
                        // cells out to it.
                        if (mine.State == CrewUnitState.Parked && TryPlanTransferTo(mine, start))
                            return;
                        WaitForHauler($"hauler {mine.Id}");
                        return;
                    }

                    // With haulers in the crew, a digger waits for one instead of carrying spoil
                    // itself; only a crew with no haulers left does its own hauling.
                    if (_dispatcher.RequestHauler(this))
                    {
                        WaitForHauler("a hauler");
                        return;
                    }
                }

                if (TryPlanTip(start))
                    return;
                if (full)
                {
                    Stop();
                    return;
                }
            }

            Idle();
        }

        void ChooseHaulerJob(Vector2Int start)
        {
            var step = StepVolume;
            var loaded = Inventory.Total + Epsilon >= step;

            // A load is delivered before anything else. Serving first was tried, and a hauler
            // that had tipped a single metre counted as "not full" and drove all the way back to
            // its digger with the rest still in the bed.
            if (loaded)
            {
                if (TryPlanTip(start))
                    return;
                Stop();
                return;
            }

            var digger = _dispatcher.DiggerFor(Id);
            if (digger < 0)
                digger = _dispatcher.AssignDigger(this);
            if (digger >= 0 && TryPlanServe(start, digger))
                return;

            Idle();
        }

        void Idle()
        {
            Job = CrewJobKind.None;
            _dispatcher.Release(Id);
            var note = _dispatcher.AutoRamp && _dispatcher.RampNote.Length > 0 ? $"; {_dispatcher.RampNote}" : "";
            if (UnreachableCount > 0)
                SetState(CrewUnitState.Unreachable, $"UNREACHABLE: {NearestUnreachable}"
                    + (UnreachableCount > 1 ? $" (+{UnreachableCount - 1} more)" : "") + note);
            else if (Role == UnitRole.Hauler)
                SetState(CrewUnitState.Idle, Inventory.Total > Epsilon ? "Idle: nothing to serve, nowhere to tip" : "Idle: no digger to serve");
            else if (_designations.FillCount > 0 && Inventory.Total < StepVolume && _designations.QuarryCount == 0)
                SetState(CrewUnitState.NeedsMaterial, "Nothing to fill with: mark a Quarry to dig from" + note);
            else
                SetState(CrewUnitState.Idle, _designations.Count == 0 ? "Idle: nothing designated" : "Idle: nothing it can do yet");
        }

        /// <summary>Loaded with nowhere it is allowed to tip: a question for the player.</summary>
        void Stop()
        {
            Job = CrewJobKind.None;
            _dispatcher.Release(Id);
            SetState(CrewUnitState.NeedsSomewhereToTip, "Needs a Dump Zone or Fill designation"
                + (DumpZoneFull ? " (no room it can reach in any Dump Zone)" : ""));
        }

        void WaitForHauler(string who)
        {
            Job = CrewJobKind.None;
            _dispatcher.Release(Id);
            if (State != CrewUnitState.WaitingForHauler)
                HaulerWaits++;
            _rethinkTimer = HaulerCheckInterval;
            SetState(CrewUnitState.WaitingForHauler, $"Full: waiting for {who}");
        }

        bool TryPlan(CrewJobKind kind, Vector2Int start)
        {
            if (!AnyCandidate(kind))
                return false;

            var target = new Vector2Int(-1, -1);
            var found = _pathfinder.TryFindNearest(start.x, start.y, (standX, standZ) =>
            {
                if (!CanStandHere(standX, standZ))
                    return false;
                var standHeight = _grid.GetSurfaceHeight(standX, standZ);
                var digging = kind == CrewJobKind.Dig || kind == CrewJobKind.Quarry;
                var best = float.MaxValue;
                foreach (var cell in StandsAround(standX, standZ))
                {
                    if (!IsJobCell(kind, standX, standZ, standHeight, cell.x, cell.y))
                        continue;

                    float score;
                    if (!digging)
                    {
                        // Tipping fills the lowest cell in reach first.
                        score = _grid.GetSurfaceHeight(cell.x, cell.y);
                    }
                    else
                    {
                        // Keep to one face. Taking whichever cell came first walked a unit round
                        // and round the edge of a block (Ronan, 2026-09-22: "why it walks in a
                        // square around dig site instead of approaching and digging into it from
                        // one side to the other"), so the cut beside the last one wins, and where
                        // there is no last one, the nearest.
                        var fromLast = _lastCut.x < 0 ? Vector2.zero
                            : new Vector2(cell.x - _lastCut.x, cell.y - _lastCut.y);
                        var fromHere = new Vector2(cell.x - standX, cell.y - standZ);
                        score = fromLast.sqrMagnitude + 0.01f * fromHere.sqrMagnitude;
                    }

                    if (score >= best)
                        continue;
                    best = score;
                    target = cell;
                }

                return best < float.MaxValue;
            }, _path);
            if (!found)
                return false;

            Job = kind;
            JobTarget = target;
            JobStand = _path[_path.Count - 1];
            _jobHeight = kind == CrewJobKind.Quarry
                ? _designations.QuarryFloor(target.x, target.y)
                : _designations.GetTarget(target.x, target.y);
            _pathIndex = 0;
            _waitTimer = 0f;
            _dispatcher.Claim(target.x, target.y, Id);
            MarkPath();
            SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            return true;
        }

        /// <summary>
        /// Plans a tip: into a Fill designation first, then a Dump Zone, and for each of those
        /// preferring rim cells above the cell tipped onto, so the material runs in by itself.
        /// </summary>
        bool TryPlanTip(Vector2Int start)
        {
            DumpZoneFull = false;
            if (_designations.Count > 0)
            {
                _tipHighRimsOnly = true;
                if (TryPlan(CrewJobKind.Fill, start))
                    return true;
                _tipHighRimsOnly = false;
                if (TryPlan(CrewJobKind.Fill, start))
                    return true;
            }

            if (_designations.DumpZoneCount == 0)
                return false;

            // Build out before building up — but never stand there holding a load because of it.
            // The floor of the zone is tried first, and if every low cell is taken or out of
            // reach the whole zone is tried, which is what it did before.
            for (var pass = 0; pass < 2; pass++)
            {
                _zoneFloor = pass == 0 ? ZoneFloor() : float.MaxValue;
                _tipHighRimsOnly = true;
                if (TryPlan(CrewJobKind.DumpZone, start))
                    return true;
                _tipHighRimsOnly = false;
                if (TryPlan(CrewJobKind.DumpZone, start))
                    return true;
            }

            DumpZoneFull = true;
            return false;
        }

        /// <summary>
        /// The lowest cell of any dump zone: the bottom of the heap, which is where the next load
        /// belongs. Zones are a few dozen cells, so this is cheap enough to ask each time a tip is
        /// planned, and asking each time is what keeps it honest as the ground changes.
        /// </summary>
        float ZoneFloor()
        {
            var lowest = float.MaxValue;
            var cells = _designations.DumpZoneCells;
            var width = _grid.Width;
            for (var i = 0; i < cells.Count; i++)
            {
                var x = cells[i] % width;
                var z = cells[i] / width;
                if (_designations.GetKind(x, z) != DesignationKind.None)
                    continue;
                var height = _grid.GetSurfaceHeight(x, z);
                if (height < lowest)
                    lowest = height;
            }

            return lowest == float.MaxValue ? float.MinValue : lowest;
        }

        /// <summary>Drives to a free cell beside the digger it serves, and parks there.</summary>
        bool TryPlanServe(Vector2Int start, int diggerId)
        {
            var digger = _dispatcher.UnitOf(diggerId);
            if (digger == null)
            {
                _dispatcher.ReleaseHauler(Id);
                return false;
            }

            var at = digger.Cell;
            if (!_dispatcher.Regions.CanReach(start.x, start.y, at.x, at.y))
            {
                _dispatcher.ReleaseHauler(Id);
                return false;
            }

            var found = _pathfinder.TryFindNearest(start.x, start.y,
                (x, z) => Math.Abs(x - at.x) <= 1 && Math.Abs(z - at.y) <= 1 && IsParkCell(x, z), _path);
            if (!found)
            {
                // Nothing clear right beside it — it is probably standing on the ground it is
                // digging — so wait as near as there is room for, and let it walk out.
                found = _pathfinder.TryFindNearest(start.x, start.y,
                    (x, z) => Math.Abs(x - at.x) <= ParkRadius && Math.Abs(z - at.y) <= ParkRadius && IsParkCell(x, z), _path);
                if (!found)
                    return false;
            }

            Job = CrewJobKind.Serve;
            JobTarget = at;
            JobStand = _path[_path.Count - 1];
            _pathIndex = 0;
            _waitTimer = 0f;
            MarkPath();
            SetState(CrewUnitState.Moving, $"Moving to serve digger {diggerId}");
            return true;
        }

        /// <summary>Walks to a cell beside the hauler to empty the scoop into it.</summary>
        bool TryPlanTransferTo(CrewUnit hauler, Vector2Int start)
        {
            var at = hauler.Cell;
            if (!_dispatcher.Regions.CanReach(start.x, start.y, at.x, at.y))
                return false;
            var found = _pathfinder.TryFindNearest(start.x, start.y,
                (x, z) => Math.Abs(x - at.x) <= 1 && Math.Abs(z - at.y) <= 1 && CanStandHere(x, z), _path);
            if (!found)
                return false;

            Job = CrewJobKind.Transfer;
            JobTarget = at;
            JobStand = _path[_path.Count - 1];
            _pathIndex = 0;
            _waitTimer = 0f;
            _dispatcher.Release(Id);
            MarkPath();
            SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            return true;
        }

        /// <summary>Haulers park clear of designated ground, other units and their routes.</summary>
        bool IsParkCell(int x, int z) =>
            _designations.GetKind(x, z) == DesignationKind.None
            && !_designations.IsDumpZone(x, z)
            && !_dispatcher.IsOccupiedByOther(x, z, Id)
            && !_dispatcher.IsOnAnotherPath(x, z, Id);

        /// <summary>Empties the scoop into a hauler already standing beside this digger, if one is.</summary>
        bool TryTransferToAdjacentHauler()
        {
            var me = Cell;
            for (var n = 0; n < 8; n++)
            {
                var x = me.x + NeighbourX[n];
                var z = me.y + NeighbourZ[n];
                if (!_grid.InBounds(x, z))
                    continue;
                var other = _dispatcher.UnitOn(x, z);
                if (other == null || other.Role != UnitRole.Hauler || other.Inventory.Remaining <= Epsilon)
                    continue;

                Job = CrewJobKind.Transfer;
                JobTarget = new Vector2Int(x, z);
                JobStand = me;
                _dispatcher.Release(Id);
                SetState(CrewUnitState.Transferring, $"Loading hauler {other.Id}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether any cell could be worked for this job from a reachable place to stand: set
        /// lookups only. A nearest-first search with no goal would expand every reachable cell on
        /// the map; this is what keeps a stuck unit cheap.
        /// </summary>
        bool AnyCandidate(CrewJobKind kind)
        {
            var cells = kind == CrewJobKind.DumpZone ? _designations.DumpZoneCells
                : kind == CrewJobKind.Quarry ? _designations.QuarryCells
                : _designations.ActiveCells;
            var width = _grid.Width;
            var me = Cell;
            for (var i = 0; i < cells.Count; i++)
            {
                var x = cells[i] % width;
                var z = cells[i] / width;
                foreach (var stand in StandsAround(x, z))
                {
                    if (!CanStandHere(stand.x, stand.y)
                        || !_dispatcher.Regions.CanReach(me.x, me.y, stand.x, stand.y))
                        continue;
                    if (IsJobCell(kind, stand.x, stand.y, _grid.GetSurfaceHeight(stand.x, stand.y), x, z))
                        return true;
                }
            }

            return false;
        }

        bool IsJobCell(CrewJobKind kind, int standX, int standZ, float standHeight, int x, int z)
        {
            switch (kind)
            {
                case CrewJobKind.Dig:
                    return Digs
                        && WithinDigReach(standHeight, x, z)
                        && !_dispatcher.IsClaimedByOther(x, z, Id)
                        && CanDigStep(standHeight, x, z);
                case CrewJobKind.Quarry:
                    return Digs
                        && WithinDigReach(standHeight, x, z)
                        && !_dispatcher.IsClaimedByOther(x, z, Id)
                        && CanQuarryStep(standHeight, x, z);
                case CrewJobKind.Fill:
                    return _designations.GetKind(x, z) == DesignationKind.Fill
                        && !_dispatcher.IsClaimedByOther(x, z, Id)
                        && CanTipOnto(standX, standZ, standHeight, x, z, FillCap(x, z));
                case CrewJobKind.DumpZone:
                    return _designations.IsDumpZone(x, z)
                        && _designations.GetKind(x, z) == DesignationKind.None
                        && !_dispatcher.IsClaimedByOther(x, z, Id)
                        // Build out before building up: only the low ground of the zone takes a
                        // load, so the floor fills before anything rises. A tip is capped a climb
                        // above where the unit stands, which is the right cap and the wrong
                        // reference — the unit walks up onto its own heap, and the cap goes up
                        // with it. Nine and three quarter cubic metres made a tower 2.5 m tall
                        // that walled the tip in, where spread over the zone it would have stood
                        // 0.8 m (2026-09-22).
                        && _grid.GetSurfaceHeight(x, z) <= _zoneFloor + _dispatcher.Climb + Epsilon
                        && CanTipOnto(standX, standZ, standHeight, x, z, _designations.DumpZoneCap(x, z));
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether a unit standing at <paramref name="standHeight"/> beside (x, z) may tip onto it:
        /// the cell is not another unit's, nor on its way, the tip does not have to go further up
        /// than dig reach (dropping into a hole is free), it stays under <paramref name="cap"/>,
        /// and a whole height step of the load will land.
        /// </summary>
        bool CanTipOnto(int standX, int standZ, float standHeight, int x, int z, float cap)
        {
            var me = Cell;
            if (x == me.x && z == me.y)
                return false;
            if (_tipHighRimsOnly && standHeight <= _grid.GetSurfaceHeight(x, z) + Epsilon)
                return false;

            // Standing on a heap, a unit only tips level with itself or higher. Tipping downhill
            // from up there would bury the way it came and strand it on its own spoil.
            if (_designations.IsDumpZone(standX, standZ) && _grid.GetSurfaceHeight(x, z) < standHeight - Epsilon)
                return false;
            return CanTipHere(x, z) && TipAmount(standHeight, x, z, cap) + Epsilon >= Step;
        }

        /// <summary>
        /// How much of the load can go onto (x, z): whole height steps held, never leaving the
        /// cell more than one climbable step above the unit, and never above
        /// <paramref name="cap"/>. There is no lower limit: a unit can tip into a hole of any
        /// depth beside it, which is what rim tipping is for.
        ///
        /// The upper limit is the climb, not dig reach: a heap two steps proud cannot be driven
        /// onto, so a Dump Zone would wall itself off after one pass round its edge. Together with
        /// the rim rule (a unit never stands in the area it tips into) that keeps every heap a
        /// drivable staircase, and stops a unit heaping itself in.
        /// </summary>
        float TipAmount(float standHeight, int x, int z, float cap)
        {
            var height = _grid.GetSurfaceHeight(x, z);
            // A bed goes down in one: the load falls where it falls and the heap slumps. What it
            // may not do is bury a fill past its cap. A barrow is placed rather than dropped, so
            // it goes a step at a time and never heaps higher than the unit can climb.
            if (TipsWholeLoad)
                return Math.Min(cap - height, Inventory.Total / _grid.CellArea);
            return Math.Min(Math.Min(standHeight + _dispatcher.Climb - height, cap - height), WholeStepsHeld());
        }

        /// <summary>
        /// A fill cell may be heaped one climbable step above its target, no more: that step is
        /// what slump spreads into the rest of the area, which is how cells no rim touches get
        /// filled. Without the overfill an area is only ever filled around its edge; with more,
        /// the crew would pile spoil on a finished fill.
        /// </summary>
        float FillCap(int x, int z) => _designations.GetTarget(x, z) + _dispatcher.Climb;

        /// <summary>No tipping where another unit stands, or on a cell it is about to drive over.</summary>
        bool CanTipHere(int x, int z) =>
            !_dispatcher.IsOccupiedByOther(x, z, Id) && !_dispatcher.IsOnAnotherPath(x, z, Id);

        /// <summary>
        /// Whether one more step can be quarried from (x, z): it is a quarry cell with ground above
        /// its floor, dry, diggable, and the cut stays within reach of where the unit stands.
        /// </summary>
        bool CanQuarryStep(float standHeight, int x, int z)
        {
            if (!_designations.IsQuarry(x, z) || _grid.IsWater(x, z))
                return false;
            var after = _grid.GetSurfaceHeight(x, z) - Step;
            if (after < _designations.QuarryFloor(x, z) - Epsilon || after < standHeight - DigDepthLevels * Step - Epsilon)
                return false;
            Span<Layer> diggable = stackalloc Layer[1];
            return _grid.PeekRemove(x, z, Step, diggable) > 0;
        }

        /// <summary>
        /// Whether one more step can be dug from (x, z) by a unit standing at
        /// <paramref name="standHeight"/>: it is designated, has diggable ground on top, the cut
        /// stays above its <see cref="JobDispatcher.DigFloor"/>, and the cut surface is still
        /// within reach.
        /// </summary>
        bool CanDigStep(float standHeight, int x, int z)
        {
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return false;
            // Deep water over it: nothing digs there until it drains.
            if (_grid.IsWater(x, z))
                return false;
            var after = _grid.GetSurfaceHeight(x, z) - Step;
            if (after < _dispatcher.DigFloor(x, z) - Epsilon || after < standHeight - DigDepthLevels * Step - Epsilon)
                return false;
            Span<Layer> diggable = stackalloc Layer[1];
            return _grid.PeekRemove(x, z, Step, diggable) > 0;
        }

        /// <summary>
        /// Where the unit may stand to work: not on a cell another unit holds, and not on one that
        /// is still to be dug, which would dig the ground round itself away and strand it on a
        /// pillar. With benching on, a dig cell that is at its floor (so nothing can be dug there
        /// yet) is fine, as long as no undesignated neighbour is higher: benching is for taking
        /// hills down, and in a pit it would let the unit dig itself in.
        /// </summary>
        bool CanStandHere(int x, int z)
        {
            if (!_grid.IsPassableGround(x, z) || _dispatcher.IsOccupiedByOther(x, z, Id))
                return false;
            // Never stand on ground that is to be filled: the fill would go on under the unit.
            // Dump Zones are different — a heap has to be driven onto to grow — and the tipping
            // rule below keeps that safe.
            if (_designations.GetKind(x, z) == DesignationKind.Fill)
                return false;
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return true;
            if (!_dispatcher.Benching)
                return false;

            var height = _grid.GetSurfaceHeight(x, z);
            if (height - Step >= _dispatcher.DigFloor(x, z) - Epsilon)
                return false;
            for (var n = 0; n < 8; n++)
            {
                var nx = x + NeighbourX[n];
                var nz = z + NeighbourZ[n];
                // Standing a bench below the ground outside is how a cut is worked: the unit steps
                // down into it and takes the next step off in front of it. What it must not do is
                // get in where it cannot climb back out, so the ground outside may stand over it
                // by a climb and no more (Ronan, 2026-09-22: "eventually it will need a way down
                // ... at a point you can't reach and need to either move down or have a ramp").
                // Held to the same height, a flat block stalls after its first ring: the rim is
                // cut one step, nothing can be reached from outside, and nobody may stand in it.
                if (!_grid.InBounds(nx, nz) || _designations.GetKind(nx, nz) == DesignationKind.Dig)
                    continue;
                var outside = _grid.GetSurfaceHeight(nx, nz);
                // A machine steps down into the cut and works it a bench at a time, as long as it
                // can climb back out. A robot with a barrow keeps to the older rule and stays out
                // of the hole altogether, waiting for a way down.
                var allowed = WorksFromInside ? height + _dispatcher.Climb + Epsilon : height + Epsilon;
                if (outside > allowed)
                    return false;
            }

            return true;
        }

        /// <summary>Metres the load would raise one cell by, in whole height steps.</summary>
        float WholeStepsHeld() => (float)Math.Floor((Inventory.Total + Epsilon) / StepVolume) * Step;

        /// <summary>
        /// Counts designations this unit could work but cannot reach, and lists the unreachable
        /// digs for the ramp planner. A dig cell held up by its bench floor is waiting, not
        /// unreachable. Haulers only care about fills; digs are none of their business.
        /// </summary>
        void UpdateUnreachable()
        {
            UnreachableCount = 0;
            NearestUnreachable = "";
            _unreachableDigs.Clear();
            if (_designations.Count == 0)
                return;

            var width = _grid.Width;
            var nearestDistance = float.MaxValue;
            foreach (var cell in _designations.ActiveCells)
            {
                var x = cell % width;
                var z = cell / width;
                var kind = _designations.GetKind(x, z);
                if (kind == DesignationKind.Dig)
                {
                    if (!Digs)
                        continue;
                    if (_grid.GetSurfaceHeight(x, z) - Step < _dispatcher.DigFloor(x, z) - Epsilon)
                        continue;
                }
                else if (Inventory.Total + Epsilon < StepVolume)
                {
                    // An empty unit is not held up by a fill it has nothing to put in.
                    continue;
                }

                if (CanWorkFromSomewhereReachable(x, z))
                    continue;

                UnreachableCount++;
                if (kind == DesignationKind.Dig && !_designations.IsAuto(x, z))
                    _unreachableDigs.Add(cell);
                var distance = (new Vector2(x + 0.5f, z + 0.5f) - Position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    NearestUnreachable = DescribeDesignation(x, z);
                }
            }
        }

        /// <summary>
        /// A cell this unit could work (x, z) from if only it could get there — the same test as
        /// <see cref="CanWorkFromSomewhereReachable"/> with the "can drive to it" part left out,
        /// and the nearest one at that.
        ///
        /// This is what a ramp should be aimed at. Aimed at the work itself, the planner looked
        /// for a step too steep to drive somewhere along the way; in a pit there is no such step,
        /// because the undug middle stands level with the ground outside and a corridor runs
        /// straight over the top of it. The way in is barred at the edge of what has already been
        /// cut, which is where a unit would have to stand.
        /// </summary>
        public bool TryFindStand(int x, int z, out Vector2Int stand)
        {
            stand = new Vector2Int(-1, -1);
            var kind = _designations.GetKind(x, z);
            if (kind == DesignationKind.None)
                return false;

            var best = float.MaxValue;
            var wasHighOnly = _tipHighRimsOnly;
            _tipHighRimsOnly = false;
            try
            {
                foreach (var candidate in StandsAround(x, z))
                {
                    if (!CanStandHere(candidate.x, candidate.y))
                        continue;
                    var standHeight = _grid.GetSurfaceHeight(candidate.x, candidate.y);
                    var works = kind == DesignationKind.Dig
                        ? Digs && WithinDigReach(standHeight, x, z) && CanDigStep(standHeight, x, z)
                        : CanTipOnto(candidate.x, candidate.y, standHeight, x, z, FillCap(x, z));
                    if (!works)
                        continue;
                    var distance = (new Vector2(candidate.x + 0.5f, candidate.y + 0.5f) - Position).sqrMagnitude;
                    if (distance >= best)
                        continue;
                    best = distance;
                    stand = candidate;
                }

                return best < float.MaxValue;
            }
            finally
            {
                _tipHighRimsOnly = wasHighOnly;
            }
        }

        /// <summary>
        /// Whether this unit could work the designation on (x, z) from somewhere it can drive to.
        /// The dispatcher asks this to know when a ramp has done its job.
        /// </summary>
        public bool CanWorkFromSomewhereReachable(int x, int z)
        {
            var kind = _designations.GetKind(x, z);
            if (kind == DesignationKind.None)
                return false;
            var me = Cell;
            var wasHighOnly = _tipHighRimsOnly;
            _tipHighRimsOnly = false;
            try
            {
                foreach (var stand in StandsAround(x, z))
                {
                    if (!_dispatcher.Regions.CanReach(me.x, me.y, stand.x, stand.y)
                        || !CanStandHere(stand.x, stand.y))
                        continue;
                    var standHeight = _grid.GetSurfaceHeight(stand.x, stand.y);
                    if (kind == DesignationKind.Dig
                        ? Digs && WithinDigReach(standHeight, x, z) && CanDigStep(standHeight, x, z)
                        : CanTipOnto(stand.x, stand.y, standHeight, x, z, FillCap(x, z)))
                        return true;
                }

                return false;
            }
            finally
            {
                _tipHighRimsOnly = wasHighOnly;
            }
        }

        // --- moving -----------------------------------------------------------------------------

        void Move(float deltaTime)
        {
            if (_repath && !Replan(null))
                return;

            // Units never share a cell, and a unit holds the cell it is driving into until it
            // gets there, so two of them are never overlapping on a boundary. It also will not
            // cut a diagonal past a unit standing on the corner. Blocked, it waits for the one in
            // the way, then goes round it, and gives the job up if there is no way round.
            // Once the wait has run out, the keep-apart radius stops applying to this one step and
            // only the cells hold — one unit each, so nothing ever overlaps. Without that a
            // cluster can never unwind: a crew robot's radius is 0.35 and a cell is half a metre,
            // so two of them on neighbouring cells are already closer than the radius wants, every
            // move that does not open the gap is refused, and four in a two-by-two stand there for
            // ever, each waiting (as its own status had it) for a unit on an empty cell. The gap
            // is kept in ordinary running, which is what it is for; it is not worth a deadlock.
            // Measured by how long it has actually stood still, not by how long it has been
            // waiting: the wait is reset by every re-plan, and on a busy site a unit re-plans
            // constantly, because every cut anyone takes changes the ground and sets it thinking
            // again. A wedged unit's wait never ran out at all.
            // "Moved" means got somewhere, not twitched. A wedged unit is let a little way forward
            // and sent back again, over and over: a full dumper covered seven hundredths of a
            // cell and was refused, and any reset on bare movement handed it a fresh timer every
            // time. A quarter of a cell is the smallest step worth calling progress.
            var far = _grid.CellSize * 0.25f;
            if ((Position - _lastMoved).sqrMagnitude > far * far)
            {
                _lastMoved = Position;
                _stuckTimer = 0f;
            }

            var jammed = _stuckTimer >= TrafficWaitSeconds;
            var next = NextCell();
            var blocked = next.x >= 0
                && (_dispatcher.IsOccupiedByOther(next.x, next.y, Id)
                    || (!jammed && !_dispatcher.CanMoveTo(Position, Probe(deltaTime), Id, Radius)));
            if (blocked)
            {
                _waitTimer += deltaTime;
                _stuckTimer += deltaTime;
                if (_waitTimer < TrafficWaitSeconds)
                {
                    if (State != CrewUnitState.Waiting)
                        SetState(CrewUnitState.Waiting, $"Waiting for a unit at ({next.x}, {next.y})");
                    return;
                }

                if (!Replan((x, z) => _dispatcher.IsOccupiedByOther(x, z, Id)))
                {
                    // No way round: get out of the way instead, so neither unit is stuck waiting
                    // on the other. In a one-cell corridor this is what breaks the deadlock.
                    TryYield();
                    return;
                }
            }
            else if (State == CrewUnitState.Waiting)
            {
                _waitTimer = 0f;
                SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            }

            var travel = Speed * deltaTime / _grid.CellSize;
            while (travel > 0f && _pathIndex < _path.Count)
            {
                var waypoint = _path[_pathIndex];
                var target = new Vector2(waypoint.x + 0.5f, waypoint.y + 0.5f);
                var delta = target - Position;
                var distance = delta.magnitude;
                if (distance > 1e-5f)
                    TurnToward(delta, deltaTime);
                if (distance <= travel)
                {
                    Position = target;
                    travel -= distance;
                    _pathIndex++;
                    _dispatcher.SetCell(Id, waypoint.x, waypoint.y);
                    _dispatcher.SetPath(Id, _path, _pathIndex);
                }
                else
                {
                    Position += delta / distance * travel;
                    travel = 0f;
                }
            }

            if (_pathIndex >= _path.Count)
                Arrive();
        }

        /// <summary>
        /// Drives to the nearest cell that is not another unit's and not on its way, giving this
        /// unit's job up. Called when it is blocked and there is no way round.
        /// </summary>
        bool TryYield()
        {
            var me = Cell;
            var found = _pathfinder.TryFindNearest(me.x, me.y,
                (x, z) => !_dispatcher.IsOccupiedByOther(x, z, Id) && !_dispatcher.IsOnAnotherPath(x, z, Id) && CanStandHere(x, z),
                _path);
            if (!found || _path.Count <= 1)
                return false;

            Job = CrewJobKind.Yield;
            JobTarget = _path[_path.Count - 1];
            JobStand = JobTarget;
            _pathIndex = 0;
            _rethink = false;
            MarkPath();
            SetState(CrewUnitState.Moving, "Standing aside at " + DescribeJob());
            return true;
        }

        /// <summary>Whether the water has come up round the unit's cell: ground it could not drive onto.</summary>
        bool IsInWater()
        {
            var me = Cell;
            return _grid.IsGround(me.x, me.y) && !_grid.IsPassableGround(me.x, me.y);
        }

        /// <summary>
        /// Gives the job up and drives out of the water it is standing in, to the nearest cell that
        /// is not water. False, and the unit stays put, when there is no way out.
        /// </summary>
        bool TryEscape()
        {
            var me = Cell;
            if (!_pathfinder.TryFindWayOutOfWater(me.x, me.y, _path) || _path.Count <= 1)
                return false;

            _dispatcher.Release(Id);
            Job = CrewJobKind.Escape;
            JobTarget = _path[_path.Count - 1];
            JobStand = JobTarget;
            _pathIndex = 0;
            _rethink = false;
            _repath = false;
            MarkPath();
            SetState(CrewUnitState.Moving, "Climbing out of the water to " + DescribeJob());
            return true;
        }

        /// <summary>Where this frame's travel would take it, for the clearance check.</summary>
        Vector2 Probe(float deltaTime)
        {
            if (_pathIndex >= _path.Count)
                return Position;
            var waypoint = _path[_pathIndex];
            var target = new Vector2(waypoint.x + 0.5f, waypoint.y + 0.5f);
            var delta = target - Position;
            var distance = delta.magnitude;
            var travel = Speed * deltaTime / _grid.CellSize;
            return distance <= travel || distance < 1e-5f ? target : Position + delta / distance * travel;
        }

        /// <summary>The next cell the unit will drive into, or (-1, -1) if it is on its last one.</summary>
        Vector2Int NextCell()
        {
            var me = Cell;
            for (var i = _pathIndex; i < _path.Count; i++)
                if (_path[i] != me)
                    return _path[i];
            return new Vector2Int(-1, -1);
        }

        /// <summary>
        /// Re-plans the way to the job. False when there is none, in which case the job is given
        /// up (its claim released) and a fresh one chosen next tick.
        /// </summary>
        bool Replan(Func<int, int, bool> avoid)
        {
            _repath = false;
            RepathCount++;
            _waitTimer = 0f;
            var cell = Cell;
            if (Job == CrewJobKind.Escape)
            {
                // Still in the water: find the way out afresh; out of it: done.
                if (IsInWater() && TryEscape())
                    return true;
                _rethink = true;
                SetState(CrewUnitState.Idle, "Out of the water; looking for work");
                return false;
            }

            if (!_pathfinder.TryFindPath(cell.x, cell.y, JobStand.x, JobStand.y, _path, avoid))
            {
                _rethink = true;
                _dispatcher.Release(Id);
                return false;
            }

            _pathIndex = 0;
            MarkPath();
            if (State == CrewUnitState.Waiting)
                SetState(CrewUnitState.Moving, "Moving to " + DescribeJob());
            else
                Version++;
            return true;
        }

        void TurnToward(Vector2 direction, float deltaTime)
        {
            var desired = (float)(Math.Atan2(direction.x, direction.y) * 180.0 / Math.PI);
            Heading = Mathf.MoveTowardsAngle(Heading, desired, TurnRate * deltaTime);
        }

        void Arrive()
        {
            ClearPath();
            _workTimer = 0f;
            var toTarget = new Vector2(JobTarget.x + 0.5f, JobTarget.y + 0.5f) - Position;
            if (toTarget.sqrMagnitude > 1e-6f)
                Heading = (float)(Math.Atan2(toTarget.x, toTarget.y) * 180.0 / Math.PI);
            switch (Job)
            {
                case CrewJobKind.Yield:
                    _rethink = true;
                    SetState(CrewUnitState.Idle, "Stood aside; looking for work");
                    break;
                case CrewJobKind.Escape:
                    _rethink = true;
                    SetState(CrewUnitState.Idle, "Out of the water; looking for work");
                    break;
                case CrewJobKind.Go:
                    _ordered = false;
                    if (_workOnArrival)
                    {
                        ReleaseHold();
                        SetState(CrewUnitState.Idle, "Arrived; looking for work");
                    }
                    else
                    {
                        Hold();
                    }

                    break;
                case CrewJobKind.Dig:
                    SetState(CrewUnitState.Digging, "Digging " + DescribeJob());
                    break;
                case CrewJobKind.Quarry:
                    SetState(CrewUnitState.Digging, "Quarrying " + DescribeJob());
                    break;
                case CrewJobKind.Transfer:
                    SetState(CrewUnitState.Transferring, "Loading " + DescribeJob());
                    break;
                case CrewJobKind.Serve:
                    _parkTimer = 0f;
                    _parkLoadVersion = Inventory.Version;
                    SetState(CrewUnitState.Parked, $"Parked by digger {_dispatcher.DiggerFor(Id)}");
                    break;
                default:
                    SetState(CrewUnitState.Tipping, (Job == CrewJobKind.Fill ? "Filling " : "Dumping ") + DescribeJob());
                    break;
            }
        }

        // --- working ----------------------------------------------------------------------------

        void Work(float deltaTime)
        {
            if (State == CrewUnitState.Digging)
                DigTicks++;
            _workTimer += deltaTime;
            // Rock is slower for anything without a cutter, so how long a step takes depends on
            // what is being cut, not only on the unit.
            var interval = State == CrewUnitState.Digging
                ? StepSeconds(JobTarget.x, JobTarget.y)
                : TipSeconds > 0f ? TipSeconds : WorkInterval;
            if (_workTimer < interval)
                return;
            _workTimer -= interval;

            if (Cell != JobStand)
            {
                OffStand++;
                _rethink = true;
                return;
            }

            if (State == CrewUnitState.Digging)
                DigStep();
            else
                TipStep();
        }

        void DigStep()
        {
            var target = JobTarget;
            var standHeight = _grid.GetSurfaceHeight(JobStand.x, JobStand.y);
            var quarrying = Job == CrewJobKind.Quarry;
            var canCut = quarrying ? CanQuarryStep(standHeight, target.x, target.y) : CanDigStep(standHeight, target.x, target.y);
            if (!WithinDigReach(standHeight, target.x, target.y) || !canCut)
            {
                RefusedCuts++;
                LastRefusal = WhyNotCut(standHeight, target.x, target.y);
                if (_designations.GetKind(target.x, target.y) == DesignationKind.Dig && !HasDiggableTop(target.x, target.y))
                {
                    // Bedrock: the designation can never be met. Drop it rather than loop on it.
                    _designations.Clear(target.x, target.y);
                }

                _rethink = true;
                return;
            }

            _lastCut = target;
            var report = Excavation.Dig(_grid, Inventory, target.x, target.y, 0, Step);
            _dispatcher.Ledger.Record(report.InPlaceBySource);
            if (report.WasFull)
            {
                // The load has room, but not enough of it for one cell's worth. Room is measured
                // loose and a cut is measured in place, so ground that bulks needs more room than
                // its in-place volume: "is there a step of room?" is the wrong question and a unit
                // that asks it plans a cut, walks to it, waits out its interval and digs nothing,
                // over and over. Remember what the ground said the cut would have needed.
                NeedsRoom = report.SmallestMisfit;
                _loadFull = true;
                _rethink = true;
                return;
            }

            LandedCuts++;
            NeedsRoom = 0f;

            Version++;
            var done = quarrying
                ? !CanQuarryStep(standHeight, target.x, target.y) || _designations.FillCount == 0
                : _designations.GetKind(target.x, target.y) != DesignationKind.Dig;
            if (done)
                _rethink = true;
        }

        /// <summary>
        /// Why the cut the unit had planned would not go, in the words of the rule that stopped
        /// it. A refusal on arrival is ordinary once in a while and a fault when it is the norm,
        /// and the difference is impossible to see without the reason.
        /// </summary>
        string WhyNotCut(float standHeight, int x, int z)
        {
            if (_designations.GetKind(x, z) != DesignationKind.Dig)
                return $"({x}, {z}) is no longer marked to dig";
            if (_grid.IsWater(x, z))
                return $"({x}, {z}) is under water";
            if (!WithinDigReach(standHeight, x, z))
                return $"({x}, {z}) at {_grid.GetSurfaceHeight(x, z):0.##} m is out of reach from "
                       + $"{standHeight:0.##} m";
            var after = _grid.GetSurfaceHeight(x, z) - Step;
            var floor = _dispatcher.DigFloor(x, z);
            if (after < floor - Epsilon)
                return $"({x}, {z}) is at its bench floor: {after:0.##} m would be under {floor:0.##} m";
            if (after < standHeight - DigDepthLevels * Step - Epsilon)
                return $"({x}, {z}) would go {standHeight - after:0.##} m below its feet, past "
                       + $"{DigDepthLevels * Step:0.##} m of reach";
            return $"({x}, {z}) has nothing diggable left on top";
        }

        bool HasDiggableTop(int x, int z)
        {
            Span<Layer> diggable = stackalloc Layer[1];
            return _grid.PeekRemove(x, z, Step, diggable) > 0;
        }

        void TipStep()
        {
            var target = JobTarget;
            var standHeight = _grid.GetSurfaceHeight(JobStand.x, JobStand.y);
            var cap = Job == CrewJobKind.DumpZone ? _designations.DumpZoneCap(target.x, target.y) : FillCap(target.x, target.y);
            var stillThere = Job == CrewJobKind.DumpZone
                ? _designations.IsDumpZone(target.x, target.y)
                : _designations.GetKind(target.x, target.y) == DesignationKind.Fill;
            if (!stillThere)
            {
                _rethink = true;
                return;
            }

            var amount = TipAmount(standHeight, target.x, target.y, cap);
            if (amount + Epsilon >= Step && CanTipHere(target.x, target.y))
            {
                var report = Excavation.Tip(_grid, Inventory, target.x, target.y, amount * _grid.CellArea);
                if (report.Tipped > 0f)
                    _loadFull = false;
            }

            Version++;
            _rethink = true;
        }

        void TransferStep(float deltaTime)
        {
            var hauler = _dispatcher.UnitOn(JobTarget.x, JobTarget.y);
            if (hauler == null || hauler.Role != UnitRole.Hauler || hauler.Inventory.Remaining <= Epsilon || Inventory.Total <= Epsilon)
            {
                _rethink = true;
                return;
            }

            var moved = Excavation.Transfer(Inventory, hauler.Inventory, TransferRate * deltaTime);
            if (moved <= 0f)
            {
                _rethink = true;
                return;
            }

            Transferred += moved;
            hauler.Transferred += moved;
            _loadFull = false;
            if (Inventory.Total <= Epsilon)
                _rethink = true;
        }

        /// <summary>
        /// A parked hauler waits for its digger to fill it. It leaves when it is full, when the
        /// digger has gone or has nothing more to give, or after <see cref="ParkPatience"/> with
        /// nothing tipped into it.
        /// </summary>
        void ParkStep(float deltaTime)
        {
            if (Inventory.Version != _parkLoadVersion)
            {
                _parkLoadVersion = Inventory.Version;
                _parkTimer = 0f;
                Version++;
            }
            else
            {
                _parkTimer += deltaTime;
            }

            if (Inventory.Remaining <= Epsilon)
            {
                LeavePark("full");
                return;
            }

            var digger = _dispatcher.UnitOf(_dispatcher.DiggerFor(Id));
            if (digger == null)
            {
                LeavePark("its digger has gone");
                return;
            }

            var at = digger.Cell;
            var me = Cell;
            if (Math.Abs(at.x - me.x) > ParkRadius + 1 || Math.Abs(at.y - me.y) > ParkRadius + 1)
            {
                // The digger has moved on: park by it again, or find another digger.
                _rethink = true;
                return;
            }

            // It waits for a full bed (Ronan, 2026-09-22: "the dumper mech moves before he is all
            // the way full, he shouldn't go until full"). Patience is not a clock on the load but
            // on the digger: while that one is still working there is more coming, however long
            // this cut is taking. It only gives up on a digger that has stopped.
            if (_parkTimer >= ParkPatience && !digger.StillWorking)
                LeavePark($"its digger stopped, and nothing came for {ParkPatience:0} s");
        }

        void LeavePark(string why)
        {
            _dispatcher.ReleaseHauler(Id);
            _rethink = true;
            SetState(CrewUnitState.Idle, "Leaving: " + why);
        }

        // --- bookkeeping --------------------------------------------------------------------------

        void OnCellChanged(int x, int z)
        {
            if ((State == CrewUnitState.Moving || State == CrewUnitState.Waiting) && _onPath[z * _grid.Width + x])
                _repath = true;
        }

        void OnDesignationChanged(int x, int z)
        {
            if (State == CrewUnitState.Idle || State == CrewUnitState.Unreachable || State == CrewUnitState.NeedsSomewhereToTip)
                _rethink = true;
            else if (x == JobTarget.x && z == JobTarget.y && Job != CrewJobKind.Serve)
                _rethink = true;
        }

        /// <summary>
        /// Flags the cells whose height the path depends on: the path's cells, and for each
        /// diagonal step the two cells it cuts past. Also tells the dispatcher where this unit is
        /// heading, so nobody tips spoil in front of it.
        /// </summary>
        void MarkPath()
        {
            UnmarkPath();
            for (var i = 0; i < _path.Count; i++)
            {
                Mark(_path[i].x, _path[i].y);
                if (i == 0)
                    continue;
                var a = _path[i - 1];
                var b = _path[i];
                if (a.x != b.x && a.y != b.y)
                {
                    Mark(b.x, a.y);
                    Mark(a.x, b.y);
                }
            }

            _dispatcher.SetPath(Id, _path, _pathIndex);
        }

        void Mark(int x, int z)
        {
            var cell = z * _grid.Width + x;
            if (_onPath[cell])
                return;
            _onPath[cell] = true;
            _onPathCells.Add(cell);
        }

        void UnmarkPath()
        {
            foreach (var cell in _onPathCells)
                _onPath[cell] = false;
            _onPathCells.Clear();
        }

        void ClearPath()
        {
            UnmarkPath();
            _path.Clear();
            _pathIndex = 0;
            _repath = false;
            _waitTimer = 0f;
            _dispatcher.SetPath(Id, null, 0);
        }

        void SetState(CrewUnitState state, string status)
        {
            State = state;
            Status = status;
            Version++;
        }

        string DescribeJob()
        {
            switch (Job)
            {
                case CrewJobKind.Dig:
                    if (_designations.IsAuto(JobTarget.x, JobTarget.y))
                    {
                        var toward = _dispatcher.RampTarget;
                        return $"ramp ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m, toward ({toward.x}, {toward.y})";
                    }

                    return $"dig ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Fill:
                    return $"fill ({JobTarget.x}, {JobTarget.y}) to {_jobHeight:0.#} m";
                case CrewJobKind.Quarry:
                    return $"quarry ({JobTarget.x}, {JobTarget.y}) down to {_jobHeight:0.#} m";
                case CrewJobKind.DumpZone:
                    return $"spoil at ({JobTarget.x}, {JobTarget.y}) (dump zone)";
                case CrewJobKind.Transfer:
                    return $"hauler at ({JobTarget.x}, {JobTarget.y})";
                case CrewJobKind.Serve:
                    return $"digger at ({JobTarget.x}, {JobTarget.y})";
                case CrewJobKind.Yield:
                case CrewJobKind.Escape:
                case CrewJobKind.Go:
                    return $"({JobTarget.x}, {JobTarget.y})";
                default:
                    return "nothing";
            }
        }

        string DescribeDesignation(int x, int z)
        {
            var kind = _designations.GetKind(x, z);
            var verb = kind == DesignationKind.Fill ? "fill" : "dig";
            return $"{verb} ({x}, {z}) to {_designations.GetTarget(x, z):0.#} m";
        }
    }
}
