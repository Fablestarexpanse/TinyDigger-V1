using PromptWaffle.Terrain;
using PromptWaffle.Terrain.Generation;
using UnityEngine;

namespace TinyDiggers
{
    /// <summary>
    /// TinyDiggers' own ground materials, on top of the terrain package's basic set (2026-09-25:
    /// the terrain became a reusable package, and what the ground is made of beyond rock, dirt and
    /// sand is each game's own). Ids are the ones they always had, so saved data, texture sets and
    /// tests still agree on them.
    /// </summary>
    public static class TinyDiggersMaterials
    {
        // Ores (slice 10): each in-place ore stands like rock and digs out into its own loose form,
        // so a load of iron ore is still iron ore in the hauler.
        public static readonly MaterialId Coal = new MaterialId(10);
        public static readonly MaterialId CoalLoose = new MaterialId(11);
        public static readonly MaterialId IronOre = new MaterialId(12);
        public static readonly MaterialId IronOreLoose = new MaterialId(13);
        public static readonly MaterialId CopperOre = new MaterialId(14);
        public static readonly MaterialId CopperOreLoose = new MaterialId(15);
        public static readonly MaterialId Limestone = new MaterialId(16);
        public static readonly MaterialId LimestoneLoose = new MaterialId(17);

        /// <summary>
        /// A built road's surface (Slice 17 Part B): packed gravel laid in place of the top of the
        /// ground, not on top of it. Crews drive it at full speed whatever its slope.
        /// </summary>
        public static readonly MaterialId Road = new MaterialId(18);

        /// <summary>The in-place ores, in the order the ore view uses.</summary>
        public static readonly MaterialId[] OreIds = { Coal, IronOre, CopperOre, Limestone };

        /// <summary>Where the island generator lays each ore: coal in a seam, limestone in lowland beds, iron in lenses, copper near the ridges.</summary>
        public static readonly OreMaterials Ores = new OreMaterials
        {
            Seam = Coal,
            LowlandBeds = Limestone,
            Lenses = IronOre,
            RidgeBodies = CopperOre,
        };

        /// <summary>The ores and the road, to add to the basic set.</summary>
        public static MaterialDefinition[] Definitions() => new[]
        {
            new MaterialDefinition(Coal, "Coal", new Color32(40, 40, 44, 255), 0.45f, 80f, disturbed: CoalLoose, bulkingFactor: 1.4f, isStone: true, isOre: true),
            new MaterialDefinition(CoalLoose, "Loose coal", new Color32(52, 52, 56, 255), 0.20f, 38f, isLoose: true, isOre: true),
            new MaterialDefinition(IronOre, "Iron ore", new Color32(140, 72, 48, 255), 0.70f, 85f, disturbed: IronOreLoose, bulkingFactor: 1.5f, isStone: true, isOre: true),
            new MaterialDefinition(IronOreLoose, "Loose iron ore", new Color32(150, 82, 56, 255), 0.30f, 38f, isLoose: true, isOre: true),
            new MaterialDefinition(CopperOre, "Copper ore", new Color32(78, 140, 118, 255), 0.65f, 85f, disturbed: CopperOreLoose, bulkingFactor: 1.5f, isStone: true, isOre: true),
            new MaterialDefinition(CopperOreLoose, "Loose copper ore", new Color32(88, 148, 124, 255), 0.30f, 38f, isLoose: true, isOre: true),
            new MaterialDefinition(Limestone, "Limestone", new Color32(214, 206, 184, 255), 0.50f, 85f, disturbed: LimestoneLoose, bulkingFactor: 1.45f, isStone: true, isOre: true),
            new MaterialDefinition(LimestoneLoose, "Loose limestone", new Color32(222, 214, 192, 255), 0.25f, 36f, isLoose: true, isOre: true),
            // Packed gravel: greyer and a little darker than loose rock, so a road reads as a road.
            new MaterialDefinition(Road, "Road", new Color32(158, 150, 136, 255), 0.45f, 60f, disturbed: MaterialTable.RockLoose, bulkingFactor: 1.3f, isRoad: true),
        };

        /// <summary>The game's full table: the package's basic set and these.</summary>
        public static MaterialTable CreateTable() => MaterialTable.CreateBasic(Definitions());

        static readonly MaterialTable Shared = CreateTable();

        /// <summary>Whether a material stands up like rock, by this game's table.</summary>
        public static bool IsStone(MaterialId material) => Shared.IsStone(material);

        /// <summary>Whether a material is one of this game's ores, in place or dug.</summary>
        public static bool IsOre(MaterialId material) => Shared.IsOre(material);
    }
}
