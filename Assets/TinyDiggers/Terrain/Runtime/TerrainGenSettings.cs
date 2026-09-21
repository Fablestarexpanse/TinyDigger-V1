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
        [Min(1f)] public float BaseRelief = 26f;

        [Tooltip("Metres above sea level the base field sits at on average.")]
        public float BaseHeight = 14f;

        [Tooltip("Metres across the features of the medium octave, which gives texture between terraces.")]
        [Min(4f)] public float MediumSize = 40f;

        [Min(0f)] public float MediumRelief = 1.5f;

        [Tooltip("Metres across the features of the domain warp, which bends every contour.")]
        [Min(4f)] public float WarpSize = 45f;

        [Tooltip("Metres the warp pushes each sample around by.")]
        [Min(0f)] public float WarpStrength = 14f;

        [Header("Land mask")]
        [Tooltip("The shape of land to make. Any lets the seed choose.")]
        public LandShape Shape = LandShape.Any;

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

        [Tooltip("Cells a pocket of water inside the land must cover to be kept as a lake.")]
        [Min(1)] public int MinWaterPocket = 12;

        [Header("Coast")]
        [Tooltip("Cells of beach shelf on a coast gentle enough to hold one.")]
        [Min(0)] public int BeachCells = 5;

        [Tooltip("Metres the beach rises to at its back.")]
        [Min(0f)] public float BeachHeight = 2f;

        [Tooltip("Metres a cell may step to its neighbours and still be a beach rather than a rocky shore.")]
        [Min(0f)] public float BeachMaxSlope = 0.55f;

        [Tooltip("Cells of shallow shelf out from the shore before the sea drops away.")]
        [Min(1)] public int ShallowCells = 10;

        [Header("Ridge and valleys")]
        [Tooltip("Metres the ridge stands at its highest.")]
        [Min(0f)] public float RidgeHeight = 52f;

        [Tooltip("Cells from the ridge line to where it has faded out.")]
        [Min(4f)] public float RidgeWidth = 55f;

        [Tooltip("How deep the rivers cut their valleys. 0 leaves the land as the noise made it.")]
        [Min(0f)] public float ValleyCut = 9f;

        [Tooltip("Benches of flat ground cut into the lee of the ridge.")]
        [Range(0, 2)] public int Plateaus = 1;

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
    }
}
