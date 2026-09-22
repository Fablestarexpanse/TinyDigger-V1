using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>The shape of land a seed is asked for.</summary>
    public enum LandShape
    {
        /// <summary>Whatever the seed feels like. The default.</summary>
        Any,

        /// <summary>One mass with a rugged coast.</summary>
        Continent,

        /// <summary>One mass with a bay bitten out of one side.</summary>
        Crescent,

        /// <summary>Two masses with a strait between them.</summary>
        Twin,

        /// <summary>One main island with a scatter of islets.</summary>
        Archipelago,

        /// <summary>A ring of land around a shallow lake that opens to the sea.</summary>
        Lagoon,
    }

    /// <summary>
    /// Every number the island generator uses, in one asset so the land can be tuned without
    /// touching code. A seed and these settings fully determine the map.
    ///
    /// Heights are metres above sea level (<see cref="World.SeaLevel"/>), so the numbers here read
    /// the way a map does: the shelf is negative, the peak is positive.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Terrain Generator Settings", fileName = "IslandSettings")]
    public sealed class TerrainGenSettings : ScriptableObject
    {
        [Header("Seed")]
        [Tooltip("The same seed and settings always give the same island.")]
        public int Seed = 1;

        [Header("Datum")]
        [Tooltip("Metres above sea level that the bottom of every column sits at. Bedrock fills from here up.")]
        public float Datum = -45f;

        [Header("Base land")]
        [Tooltip("Metres across the large features of the land.")]
        [Min(10f)] public float FeatureSize = 120f;

        [Tooltip("Metres between the lowest and highest of the base field, before the island mask.")]
        [Min(1f)] public float BaseRelief = 16f;

        [Tooltip("Metres above sea level the base field sits at on average. Keep this low: every " +
            "shore is a bank this tall, relaxed to forty-five degrees, and with a coast as long as " +
            "an island's that was most of the steep ground on the map.")]
        public float BaseHeight = 7f;

        [Tooltip("Metres across the features of the medium octave, which gives texture between terraces.")]
        [Min(4f)] public float MediumSize = 40f;

        [Min(0f)] public float MediumRelief = 1.5f;

        [Tooltip("Metres across the finest octave of the land, the one that stops a slope being planar.")]
        [Min(2f)] public float DetailSize = 8f;

        [Min(0f)] public float DetailRelief = 1.1f;

        [Tooltip("Metres across the features of the domain warp, which bends every contour.")]
        [Min(4f)] public float WarpSize = 45f;

        [Tooltip("Metres the warp pushes each sample around by.")]
        [Min(0f)] public float WarpStrength = 14f;

        [Header("Land types (natural terrain, phase 2)")]
        [Tooltip("Build the land from plains, hills and mountains, each with its own relief. Off: the old single noise stack everywhere.")]
        public bool UseLandTypes = true;

        [Tooltip("Share of the land that is plains: each seed draws its own share between these.")]
        [Range(0f, 1f)] public float PlainsShareMin = 0.25f;
        [Range(0f, 1f)] public float PlainsShareMax = 0.55f;

        [Tooltip("Share of the land that is mountains: each seed draws its own share between these. Hills take the rest.")]
        [Range(0f, 1f)] public float MountainShareMin = 0.08f;
        [Range(0f, 1f)] public float MountainShareMax = 0.3f;

        [Tooltip("Metres across the field that decides where each type lies. Big, so a plain or a range is a region, not a speck.")]
        [Min(20f)] public float TypeFeatureSize = 260f;

        [Tooltip("How gradually one type turns into the next, in type-field units either side of a border.")]
        [Range(0.005f, 0.3f)] public float TypeBlend = 0.06f;

        [Tooltip("How strongly the ridge line pulls mountains onto itself.")]
        [Range(0f, 1f)] public float RidgeMountainBias = 0.35f;

        [Tooltip("How strongly land near the sea is pushed toward plains.")]
        [Range(0f, 1f)] public float CoastLowlandBias = 0.3f;

        [Tooltip("Metres in from the sea that the lowland push fades over.")]
        [Min(1f)] public float CoastLowlandDistance = 60f;

        [Tooltip("Metres above the sea the land stands at the waterline, before the coast is shaped.")]
        public float ShoreHeight = 1f;

        [Tooltip("Metres the land rises on its way inland, before any type adds its own relief.")]
        [Min(0f)] public float InlandRise = 5f;

        [Tooltip("Metres inland over which most of that rise happens.")]
        [Min(1f)] public float InlandRiseDistance = 120f;

        [Tooltip("Metres the mountains' crest adds at most: each seed draws between these (Ronan, 2026-09-21: peaks of 40-60 m, random per generation). Used with land types; RidgeHeight is the old single value.")]
        [Min(0f)] public float MountainCrestMin = 35f;
        [Min(0f)] public float MountainCrestMax = 70f;

        [Tooltip("Metres across the swells of a plain.")]
        [Min(10f)] public float PlainsFeatureSize = 180f;

        [Tooltip("Metres a plain rises and falls: small, so it reads as flat ground.")]
        [Min(0f)] public float PlainsRelief = 1.5f;

        [Tooltip("Metres across a rolling hill.")]
        [Min(10f)] public float HillsFeatureSize = 140f;

        [Tooltip("Metres the hills stand above the plain under them, at most.")]
        [Min(0f)] public float HillsRelief = 8f;

        [Header("Cliff coasts (natural terrain, phase 4)")]
        [Tooltip("Share of the coast that is cliff: each seed draws its own share between these. The rest is beach or low shore.")]
        [Range(0f, 1f)] public float CliffCoastShareMin = 0.15f;
        [Range(0f, 1f)] public float CliffCoastShareMax = 0.35f;

        [Tooltip("Metres a cliff stands above the sea: each seed draws between these.")]
        [Min(0f)] public float CliffCoastHeightMin = 5f;
        [Min(0f)] public float CliffCoastHeightMax = 10f;

        [Tooltip("Metres along the coast a stretch of cliff or beach tends to run.")]
        [Min(10f)] public float CliffCoastSize = 180f;

        [Tooltip("Metres inland the cliff top runs before it eases back down to the land behind it.")]
        [Min(1f)] public float CliffCoastInland = 35f;

        [Tooltip("How gradually a stretch of cliff rises out of the beach beside it, in cliff-field units. Too narrow and the end of a cliff is a wall running inland.")]
        [Range(0.02f, 1f)] public float CliffCoastBlend = 0.3f;

        [Tooltip("How strongly hills and mountains near the sea pull cliffs toward themselves.")]
        [Range(0f, 1f)] public float CliffUplandBias = 0.4f;

        [Header("Erosion (natural terrain, phase 3)")]
        [Tooltip("Weather the land: raindrops carve valleys, then slopes settle to their resting angle. Runs on a coarser grid and is added back as a change.")]
        public bool Erosion = true;

        [Tooltip("Metres a cell of the erosion grid is across.")]
        [Min(0.5f)] public float ErosionCellSize = 2f;

        [Tooltip("Raindrops per square kilometre of land.")]
        [Min(0f)] public float ErosionDropletsPerKm2 = 400000f;

        public TerrainErosion.DropletSettings Droplets = TerrainErosion.DropletSettings.Default;

        [Tooltip("Passes of slope settling after the rain.")]
        [Range(0, 200)] public int ThermalIterations = 40;

        [Tooltip("Degrees soil settles to.")]
        [Range(10f, 80f)] public float TalusSoil = 38f;

        [Tooltip("Degrees rock (the mountains) settles to. Steep, so the cliffs the generator lets rock stand in survive the settling.")]
        [Range(10f, 89f)] public float TalusRock = 75f;

        [Tooltip("Passes of full-resolution slope settling just before heights go onto the height steps, so the step limit has little left to cut (and no grooves to comb in).")]
        [Range(0, 60)] public int SettleIterations = 16;

        [Tooltip("Fill every hollow on land to the level it spills over at, so water always drains. Lakes (water below sea level) are kept.")]
        public bool FillPits = true;

        [Header("Land mask")]
        [Tooltip("The shape of land to make. Any lets the seed choose.")]
        public LandShape Shape = LandShape.Any;

        [Tooltip("Metres from the middle of the disc the island may reach. The island is laid out in a frame of its own this size, centred on the disc, so the same seed gives the same island on any size of disc and the rest is open sea. 0: the whole disc.")]
        [Min(0f)] public float LandRadius;

        [Tooltip("Metres across the features of the noise that decides where land is.")]
        [Min(20f)] public float LandFeatureSize = 150f;

        [Tooltip("How much of the noise counts as land. Higher is less land and a more broken coast.")]
        [Range(-0.3f, 0.3f)] public float LandThreshold = -0.02f;

        [Tooltip("Metres the land mask's second warp pushes samples around by.")]
        [Min(0f)] public float LandWarpStrength = 34f;

        [Tooltip("Cells at the rim that are always sea, whatever the noise says.")]
        [Min(4)] public int RimWaterCells = 25;

        [Tooltip("Cells a patch of land must cover to be kept. Archipelagos keep their islets.")]
        [Min(1)] public int MinLandBlob = 40;

        [Tooltip("Square metres a pocket of water inside the land must cover to be kept as a lake. Smaller ones become land: a pond a few metres across sits in the land like a crater.")]
        [Min(1)] public int MinWaterPocket = 800;

        [Header("Coast")]
        [Tooltip("Cells of beach shelf on a coast gentle enough to hold one.")]
        [Min(0)] public int BeachCells = 5;

        [Tooltip("Metres the beach rises to at its back.")]
        [Min(0f)] public float BeachHeight = 2f;

        [Tooltip("Metres a cell may step to its neighbours and still be a beach rather than a rocky shore.")]
        [Min(0f)] public float BeachMaxSlope = 0.55f;

        [Tooltip("Cells of shallow shelf out from the shore before the sea drops away.")]
        [Min(1)] public int ShallowCells = 10;

        [Header("Cliffs")]
        [Tooltip("Metres two neighbouring cells of rock may differ by. Soil keeps the one-metre " +
            "rule, because soil slumps. Clamping every steep face to one metre is what planes a " +
            "mountain into flat forty-five degree facets.")]
        [Min(1f)] public float MaxCliffStep = 3f;

        [Tooltip("Degrees of smoothed slope at which ground is allowed to stand in a cliff.")]
        [Range(10f, 80f)] public float CliffSlope = 55f;

        [Header("Ridge and valleys")]
        [Tooltip("Metres the ridge stands at its highest.")]
        [Min(0f)] public float RidgeHeight = 30f;

        [Tooltip("Cells from the ridge line to where it has faded out.")]
        [Min(4f)] public float RidgeWidth = 110f;

        [Tooltip("How deep the rivers cut their valleys. 0 leaves the land as the noise made it.")]
        [Min(0f)] public float ValleyCut = 5f;

        [Tooltip("Benches of flat ground cut into the lee of the ridge.")]
        [Range(0, 2)] public int Plateaus = 1;

        [Tooltip("Metres across the warp applied to the ridge noise, which stops the crest running " +
            "in the few directions the noise lattice allows.")]
        [Min(4f)] public float RidgeWarpSize = 25f;

        [Min(0f)] public float RidgeWarpStrength = 18f;

        [Tooltip("Cells across a bench.")]
        [Min(4f)] public float PlateauRadius = 45f;

        [Header("Island mask (old radial shelf)")]
        [Tooltip("Cells in from the rim where the land starts falling toward the sea.")]
        [Min(4)] public int ShelfCells = 40;

        [Tooltip("Metres the shelf sits at where it first goes under.")]
        public float ShelfNearDepth = -3f;

        [Tooltip("Metres the shelf has fallen to by the outer edge of the shelf.")]
        public float ShelfFarDepth = -8f;

        [Tooltip("Metres of the deeper channel at the very rim.")]
        public float ChannelDepth = -15f;

        [Tooltip("Cells of the rim given over to that channel.")]
        [Min(1)] public int ChannelCells = 8;

        [Tooltip("Metres across the noise that makes the coastline irregular rather than a circle.")]
        [Min(4f)] public float CoastNoiseSize = 70f;

        [Tooltip("Cells the coastline wanders in and out by.")]
        [Min(0f)] public float CoastNoiseCells = 16f;

        [Header("Mountain and valleys")]
        [Min(0f)] public float MountainHeight = 46f;

        [Tooltip("Cells from the peak to where the mountain has faded out.")]
        [Min(4f)] public float MountainRadius = 70f;

        [Range(0, 3)] public int Valleys = 2;

        [Min(0f)] public float ValleyDepth = 10f;

        [Min(4f)] public float ValleyRadius = 55f;

        [Header("River")]
        [Tooltip("Cells across the flat floor of the channel. The banks step up a metre a cell either side.")]
        [Min(1)] public int RiverWidth = 4;

        [Tooltip("Metres the river floor sits below the ground it runs through.")]
        [Min(0f)] public float RiverDepth = 2.5f;

        [Tooltip("How far the river is allowed to wander off the steepest way down, in cells.")]
        [Min(0f)] public float RiverWander = 1.4f;

        [Header("Surface materials")]
        [Tooltip("Cells either side of a cell that its slope is measured over. Slope is read from a " +
            "smoothed heightfield: on quantised land a uniform hillside is a staircase, and a " +
            "per-cell slope paints it in stripes that follow the contours.")]
        [Range(1, 4)] public int SlopeSmoothing = 2;

        [Tooltip("Degrees. Below this the ground is grass.")]
        [Range(0f, 90f)] public float SlopeGrass = 25f;

        [Tooltip("Degrees. Between the two the ground is grass with rock showing through.")]
        [Range(0f, 90f)] public float SlopeMixed = 44f;

        [Tooltip("Degrees. Past this the ground is bare rock.")]
        [Range(0f, 90f)] public float SlopeBare = 50f;

        [Tooltip("Metres across the noise that pushes material boundaries about, so they wander.")]
        [Min(4f)] public float MaterialNoiseSize = 20f;

        [Tooltip("Degrees of slope the noise is worth either way.")]
        [Min(0f)] public float MaterialNoiseDegrees = 7f;

        [Tooltip("Sand goes no higher than this.")]
        public float SandMaxHeight = 3f;

        [Tooltip("And no further than this many cells from water.")]
        [Min(1f)] public float SandMaxDistance = 10f;

        [Tooltip("Cells a patch of one surface material must cover; smaller ones are absorbed.")]
        [Min(1)] public int MinMaterialPatch = 12;

        [Header("Strata")]
        [Min(0f)] public float TopsoilThickness = 0.3f;

        [Min(0f)] public float DirtOnPlains = 2.2f;

        [Min(0f)] public float DirtOnSlopes = 0.5f;

        [Min(0f)] public float ClayInValleys = 1.6f;

        [Min(0f)] public float SandThickness = 0.8f;

        [Tooltip("Metres either side of sea level where the ground is sand rather than soil.")]
        [Min(0f)] public float SandBand = 6f;

        [Tooltip("Share of a mountain's height that is granite core rather than ordinary rock.")]
        [Range(0f, 1f)] public float GraniteShare = 0.55f;

        [Tooltip("Metres of rock kept between the soil and the bedrock wherever there is room.")]
        [Min(0f)] public float RockCover = 4f;

        [Header("Open sea floor (slice 14)")]
        [Tooltip("Shape the open sea floor, past the island's shelf, with a broad swell, sandbanks, shoals and reefs. Off leaves it flat at ChannelDepth.")]
        public bool OpenSeaFloor;

        [Tooltip("Metres across the broad rise and fall of the deep floor.")]
        [Min(4f)] public float SeabedFeatureSize = 220f;

        [Tooltip("Metres the deep floor rises and falls either side of ChannelDepth.")]
        [Min(0f)] public float SeabedRelief = 5f;

        [Tooltip("Metres long a sandbank runs.")]
        [Min(4f)] public float SandbankLength = 500f;

        [Tooltip("Metres across a sandbank.")]
        [Min(2f)] public float SandbankWidth = 170f;

        [Tooltip("Degrees on the map (from +x toward +z) the sandbanks run along.")]
        [Range(0f, 180f)] public float SandbankAngle = 35f;

        [Tooltip("Share of the open sea where sandbanks form.")]
        [Range(0f, 1f)] public float SandbankCoverage = 0.35f;

        [Tooltip("Metres (negative: under the sea) a sandbank's crest comes up to.")]
        public float SandbankTop = -1.5f;

        [Tooltip("Metres across a shoal.")]
        [Min(4f)] public float ShoalSize = 160f;

        [Tooltip("Share of the open sea that is shoal.")]
        [Range(0f, 1f)] public float ShoalCoverage = 0.35f;

        [Tooltip("Metres a shoal comes up to.")]
        public float ShoalTop = -1f;

        [Tooltip("Metres across a reef.")]
        [Min(2f)] public float ReefSize = 35f;

        [Tooltip("Share of the shallow banks and shoals that is reef.")]
        [Range(0f, 1f)] public float ReefCoverage = 0.25f;

        [Tooltip("Metres: reefs only grow where the bank or shoal under them is already shallower than this.")]
        public float ReefBase = -6f;

        [Tooltip("Metres a reef comes up to, give or take a rough metre. Reefs are rock.")]
        public float ReefTop = -0.6f;

        [Tooltip("Metres of water always left over the floor: nothing out here breaks the surface.")]
        [Min(0f)] public float SeabedClearDepth = 0.5f;

        [Tooltip("Metres out from the island's shore before the features start, so they never run into its shelf.")]
        [Min(0f)] public float SeabedShoreGap = 60f;

        [Tooltip("Metres in from the dam face kept deep, for the ships at the terminals.")]
        [Min(0f)] public float SeabedRimGap = 50f;

        [Header("Ore (slice 10)")]
        [Tooltip("Lay ore into the rock at all. Off gives exactly the land and strata without it.")]
        public bool Ores = true;

        [Tooltip("Metres above sea level the coal seam's top runs at, give or take 3 m.")]
        public float CoalSeamHeight = -6f;

        [Tooltip("Limestone thins out above this height, so it lies under the lowlands.")]
        [Min(1f)] public float LimestoneBelowHeight = 14f;

        public OreSpec CoalOre = new OreSpec(abundance: 0.45f, patchSize: 90f, depthMin: 3f, depthMax: 3f, maxThickness: 2.5f);
        public OreSpec IronOre = new OreSpec(abundance: 0.32f, patchSize: 45f, depthMin: 4f, depthMax: 15f, maxThickness: 4f);
        public OreSpec CopperOre = new OreSpec(abundance: 0.4f, patchSize: 32f, depthMin: 3f, depthMax: 12f, maxThickness: 3f);
        public OreSpec LimestoneOre = new OreSpec(abundance: 0.4f, patchSize: 110f, depthMin: 1.5f, depthMax: 5f, maxThickness: 6f);

        /// <summary>
        /// The cell size a scaled copy was made for (<see cref="ScaledForCells"/>); 1 on an asset.
        /// Not saved.
        /// </summary>
        [System.NonSerialized] public float GenerationCellSize = 1f;

        /// <summary>
        /// A copy with every horizontal setting turned from metres into cells of
        /// <paramref name="cellSize"/> metres, which is what the generator works in. The values on
        /// the asset were tuned when a cell was a metre, so they read as metres; heights and
        /// angles are unchanged. Lengths and cell counts divide by the cell size, areas (the
        /// minimum patch counts) by its square, and per-cell steps (cliff step, beach slope)
        /// multiply by it so the angle they make stays the same. The caller destroys the copy.
        /// </summary>
        public TerrainGenSettings ScaledForCells(float cellSize)
        {
            var s = Instantiate(this);
            s.hideFlags = HideFlags.DontSave;
            s.GenerationCellSize = cellSize;
            if (Mathf.Approximately(cellSize, 1f))
                return s;

            var k = 1f / cellSize;
            int Count(int cells) => Mathf.Max(1, Mathf.RoundToInt(cells * k));
            int Area(int cells) => Mathf.Max(1, Mathf.RoundToInt(cells * k * k));

            s.FeatureSize *= k; s.MediumSize *= k; s.DetailSize *= k; s.WarpSize *= k; s.WarpStrength *= k;
            s.LandFeatureSize *= k; s.LandWarpStrength *= k; s.LandRadius *= k;
            s.SeabedFeatureSize *= k; s.SandbankLength *= k; s.SandbankWidth *= k; s.ShoalSize *= k; s.ReefSize *= k;
            s.SeabedShoreGap *= k; s.SeabedRimGap *= k;
            s.RidgeWidth *= k; s.RidgeWarpSize *= k; s.RidgeWarpStrength *= k; s.PlateauRadius *= k;
            s.CoastNoiseSize *= k; s.CoastNoiseCells *= k; s.MountainRadius *= k; s.ValleyRadius *= k;
            s.MaterialNoiseSize *= k; s.SandMaxDistance *= k; s.RiverWander *= k;
            s.TypeFeatureSize *= k; s.CoastLowlandDistance *= k; s.InlandRiseDistance *= k;
            s.PlainsFeatureSize *= k; s.HillsFeatureSize *= k;
            s.ErosionCellSize *= k;
            s.CliffCoastSize *= k; s.CliffCoastInland *= k;

            s.RimWaterCells = Count(RimWaterCells); s.BeachCells = BeachCells == 0 ? 0 : Count(BeachCells);
            s.ShallowCells = Count(ShallowCells); s.ShelfCells = Count(ShelfCells); s.ChannelCells = Count(ChannelCells);
            s.RiverWidth = Count(RiverWidth); s.SlopeSmoothing = Count(SlopeSmoothing);
            s.MinLandBlob = Area(MinLandBlob); s.MinWaterPocket = Area(MinWaterPocket); s.MinMaterialPatch = Area(MinMaterialPatch);

            s.BeachMaxSlope *= cellSize; s.MaxCliffStep *= cellSize;

            s.CoalOre = Scaled(CoalOre, k); s.IronOre = Scaled(IronOre, k);
            s.CopperOre = Scaled(CopperOre, k); s.LimestoneOre = Scaled(LimestoneOre, k);
            return s;
        }

        static OreSpec Scaled(OreSpec spec, float k) =>
            new OreSpec(spec.Abundance, spec.PatchSize * k, spec.DepthMin, spec.DepthMax, spec.MaxThickness);
    }
}
