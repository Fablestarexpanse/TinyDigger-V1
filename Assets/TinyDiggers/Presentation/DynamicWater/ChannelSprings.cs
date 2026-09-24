using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using PromptWaffle.DynamicWater;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Rivers and creeks, step 2 (Ronan, 2026-09-21: "simulated from springs"). Puts a spring,
    /// a Source effector, at the head of every channel the island was cut with. When the water
    /// zone builds a new simulation, fills every channel's bed to its running depth, so the
    /// rivers are flowing from the first frame instead of filling over minutes.
    ///
    /// A river runs deeper than the crew can wade and a creek does not: the fills sit either side
    /// of <see cref="TerrainGrid.DeepWater"/>. Where the water goes after that is the
    /// simulation's business: down the channels to the sea, and over the dam's spillways once
    /// the sea stands above its level.
    ///
    /// A fresh fill is then settled before the game is handed over: see
    /// <see cref="WaterSettle"/>. The springs run against the beds until the streams stop
    /// changing, water standing anywhere else is taken off in one go, and from then on a film off
    /// the beds soaks away by itself (Ronan, 2026-09-22: flooding is the player's doing, not the
    /// island's opening move).
    ///
    /// The arithmetic is in <see cref="SpringRate"/> and <see cref="Prefill"/>, so it is tested
    /// without a scene.
    /// </summary>
    [RequireComponent(typeof(WaterZone))]
    public sealed class ChannelSprings : MonoBehaviour
    {
        /// <summary>The fills the component starts with: a river runs at three quarters of its bed's depth, a creek at 0.25 m.</summary>
        public const float DefaultRiverFill = 0.75f;
        public const float DefaultCreekFill = 0.25f;

        /// <summary>The most a big catchment can scale a spring by.</summary>
        public const float MaxCatchmentScale = 1.25f;

        [SerializeField] TerrainView _terrain;

        [Tooltip("Metres of water a river's bed is filled with, as a share of the bed's depth.")]
        [SerializeField, Range(0f, 1f)] float _riverFill = DefaultRiverFill;

        [Tooltip("Metres of water a creek's bed is filled with. Under the crew's wading depth.")]
        [SerializeField, Min(0f)] float _creekFill = DefaultCreekFill;

        [Tooltip("Metres a second the water is taken to run down a channel: with the bed's width " +
            "and the fill, that sets how much each spring gives. At 0.4 the springs could not keep " +
            "the beds full; at 1.5 the biggest river flooded a flat basin (2026-09-21 play runs).")]
        [SerializeField, Min(0.01f)] float _flowSpeed = 0.8f;

        [Tooltip("Square metres of catchment at which a spring gives exactly its channel's flow. " +
            "Bigger catchments give more, smaller ones less, by the square root, within half to a quarter more. " +
            "Heads sit at the top of their valleys, so what drains to one is small: tens to hundreds of square metres.")]
        [SerializeField, Min(1f)] float _referenceCatchment = 100f;

        readonly List<WaterEffectorComponent> _springs = new List<WaterEffectorComponent>();
        WaterZone _zone;
        IslandMap _builtFor;
        WaterSimulation _filled;

        /// <summary>The springs, one per channel, in the island's channel order.</summary>
        public IReadOnlyList<WaterEffectorComponent> Springs => _springs;

        [Tooltip("Seconds of load the water may take to settle before play starts.")]
        [SerializeField, Min(0f)] float _settleBudget = WaterSettle.DefaultBudgetSeconds;

        [Tooltip("Simulation steps run between two checks for whether the streams have settled.")]
        [SerializeField, Min(1)] int _settleBatch = WaterSettle.DefaultBatch;

        /// <summary>Bed cells the last pre-fill put water in.</summary>
        public int LastFilledCells { get; private set; }

        /// <summary>Steps the last settle ran, and whether it reached a steady state before its budget ran out.</summary>
        public int LastSettleSteps { get; private set; }

        public bool LastSettleSteadied { get; private set; }

        /// <summary>Cells still holding standing water off the beds when the settle stopped.</summary>
        public int LastStrayCells { get; private set; }

        /// <summary>Cells the last settle took standing water off, and the cubic metres it took.</summary>
        public int LastZappedCells { get; private set; }

        public float LastZappedVolume { get; private set; }

        /// <summary>
        /// Cubic metres a second for a channel's spring: its bed's width times the depth it runs
        /// at times <paramref name="flowSpeed"/>, scaled by the square root of its catchment
        /// against <paramref name="referenceCatchment"/>, kept within half to a quarter more: at
        /// double, the creek with the biggest catchment overflowed its bed.
        /// </summary>
        public static float SpringRate(Channel channel, float fillDepth, float flowSpeed, float referenceCatchment)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));
            var scale = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, channel.Catchment) / Mathf.Max(1f, referenceCatchment)), 0.5f, MaxCatchmentScale);
            return channel.Width * fillDepth * flowSpeed * scale;
        }

        /// <summary>The depth a channel's bed is filled to.</summary>
        public static float FillDepth(Channel channel, float riverFill, float creekFill) =>
            channel.Kind == ChannelKind.River ? channel.Depth * riverFill : creekFill;

        /// <summary>
        /// Raises <paramref name="depths"/> (one per grid cell, row by row) so every cell of each
        /// channel's bed holds its channel's fill depth over its own ground, where it did not
        /// already hold more. Walks each path at half-cell steps and fills the cells within half
        /// the bed's width.
        ///
        /// Over each cell's own ground, not over a floor drawn along the path: on a steep reach
        /// that line stands above the ground near the lower point, and a creek came out a metre
        /// deep there, while a floor taken from the lower point left steep river reaches shallow
        /// enough to walk across. Returns how many cells it raised.
        /// </summary>
        public static int Prefill(float[] depths, int width, int depth, float cellSize, IReadOnlyList<Channel> channels,
            float riverFill, float creekFill)
        {
            if (depths == null || depths.Length != width * depth)
                throw new ArgumentException("One depth per cell.", nameof(depths));
            var raised = 0;
            foreach (var channel in channels)
            {
                var fill = FillDepth(channel, riverFill, creekFill);
                // At least three quarters of a cell either side, the same footprint the carve marks
                // as bed (RiverChannels.Cut). At half a cell a one-cell creek running along a cell
                // edge missed the cells it was cut into, and left a bed with no water in it beside
                // a wet one (seed 3, 2026-09-24).
                var drawnHalf = Mathf.Max(0.75f, channel.Width * 0.5f / cellSize);
                var path = channel.Path;
                // A channel's bed changes width along it (it grows downstream, narrows on steep
                // reaches, flares at its end), so the fill follows the width at each point where
                // the channel says what that is.
                var widths = channel.Widths.Count == path.Count ? channel.Widths : null;
                for (var p = 1; p < path.Count; p++)
                {
                    var a = path[p - 1];
                    var b = path[p];
                    var steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z)) * 2f));
                    for (var s = 0; s <= steps; s++)
                    {
                        var t = s / (float)steps;
                        var at = Vector3.Lerp(a, b, t);
                        var half = widths == null
                            ? drawnHalf
                            : Mathf.Max(0.75f, Mathf.Lerp(widths[p - 1], widths[p], t) * 0.5f / cellSize);
                        var box = Mathf.CeilToInt(half);
                        var cx = Mathf.FloorToInt(at.x);
                        var cz = Mathf.FloorToInt(at.z);
                        for (var dz = -box; dz <= box; dz++)
                        {
                            var z = cz + dz;
                            if (z < 0 || z >= depth)
                                continue;
                            for (var dx = -box; dx <= box; dx++)
                            {
                                var x = cx + dx;
                                if (x < 0 || x >= width)
                                    continue;
                                if (Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), new Vector2(at.x, at.z)) > half)
                                    continue;
                                var cell = z * width + x;
                                if (fill <= depths[cell])
                                    continue;
                                depths[cell] = fill;
                                raised++;
                            }
                        }
                    }
                }
            }

            return raised;
        }

        void Awake() => _zone = GetComponent<WaterZone>();

        void Update()
        {
            var island = _terrain != null ? _terrain.Island : null;
            if (island == null || _terrain.Grid == null)
                return;
            if (!ReferenceEquals(island, _builtFor))
                Build(island);

            // A new simulation (the first, or one rebuilt after the land was regenerated) starts
            // as a flat sea: fill the channels once it exists.
            var simulation = _zone.Simulation;
            if (simulation != null && !ReferenceEquals(simulation, _filled))
            {
                _filled = simulation;
                Fill(simulation, island);
            }
        }

        void Build(IslandMap island)
        {
            Clear();
            _builtFor = island;
            var grid = _terrain.Grid;
            foreach (var channel in island.Channels)
            {
                var spring = new GameObject($"Spring ({channel.Kind} {_springs.Count})").AddComponent<WaterEffectorComponent>();
                spring.transform.SetParent(transform, false);
                var x = channel.Spring.x;
                var z = channel.Spring.y;
                spring.transform.position = _terrain.transform.TransformPoint(new Vector3(
                    (x + 0.5f) * grid.CellSize, grid.GetSurfaceHeight(x, z), (z + 0.5f) * grid.CellSize));
                spring.Kind = WaterEffectorKind.Source;
                spring.Radius = Mathf.Max(1f, channel.Width * 0.5f);
                spring.Rate = SpringRate(channel, FillDepth(channel, _riverFill, _creekFill), _flowSpeed, _referenceCatchment);
                _springs.Add(spring);
            }

            // The land changed under the simulation: fill again once the zone has rebuilt it.
            _filled = null;
        }

        void Fill(WaterSimulation simulation, IslandMap island)
        {
            var grid = _terrain.Grid;
            var desc = simulation.Desc;
            if (desc.Width != grid.Width || desc.Height != grid.Height)
            {
                Debug.LogWarning("ChannelSprings: the water zone is not one cell to one cell over the grid; the channels start dry.", this);
                return;
            }

            var depths = simulation.ReadDepthsImmediate();
            LastFilledCells = Prefill(depths, grid.Width, grid.Height, grid.CellSize, island.Channels,
                _riverFill, _creekFill);
            simulation.SetDepths(depths);
            Settle(simulation, island, grid);
        }

        /// <summary>
        /// Runs the springs against the beds until the streams stop changing (or the budget runs
        /// out), then takes off every drop standing outside the sea and the channel beds, and
        /// hands the simulation the mask that lets a stray film soak away from then on.
        /// </summary>
        void Settle(WaterSimulation simulation, IslandMap island, TerrainGrid grid)
        {
            var cells = grid.Width * grid.Height;
            var keep = new bool[cells];
            WaterSettle.BuildKeepMask(keep, grid, island, World.SeaLevel);

            // The soak mask goes on before the settle, not after it: the springs spill for as long
            // as they run, so water settles only once spilling and soaking balance. Without it the
            // stray count climbed for the whole budget (play runs of 2026-09-22).
            var soaks = new bool[cells];
            WaterSettle.SoakMask(keep, soaks);
            simulation.SetSoakMask(soaks);

            var clock = Stopwatch.StartNew();
            var previous = -1;
            var settled = simulation.ReadDepthsImmediate();
            LastSettleSteps = 0;
            LastSettleSteadied = false;
            while (clock.Elapsed.TotalSeconds < _settleBudget)
            {
                simulation.StepExactly(_settleBatch);
                LastSettleSteps += _settleBatch;
                settled = simulation.ReadDepthsImmediate();
                LastStrayCells = WaterSettle.CountStray(settled, keep, WaterSettle.DefaultSoakDepth);
                if (WaterSettle.Steady(LastStrayCells, previous, WaterSettle.DefaultTolerance))
                {
                    LastSettleSteadied = true;
                    break;
                }

                previous = LastStrayCells;
            }

            LastZappedCells = WaterSettle.Zap(settled, keep, out var cleared);
            LastZappedVolume = cleared * grid.CellArea;
            simulation.SetDepths(settled);

            UnityEngine.Debug.Log($"ChannelSprings: settled in {LastSettleSteps} steps over {clock.Elapsed.TotalSeconds:0.00} s"
                + (LastSettleSteadied ? $" (steady at {LastStrayCells} stray cells)" : $" (budget, {LastStrayCells} stray cells)")
                + $"; took {LastZappedVolume:0.#} m³ of standing water off {LastZappedCells} cells", this);
        }

        void Clear()
        {
            foreach (var spring in _springs)
                if (spring != null)
                    Destroy(spring.gameObject);
            _springs.Clear();
        }

        void OnDestroy() => Clear();
    }
}
