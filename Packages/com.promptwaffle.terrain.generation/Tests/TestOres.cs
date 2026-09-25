using PromptWaffle.Terrain;
using UnityEngine;

namespace PromptWaffle.Terrain.Generation.Tests
{
    /// <summary>
    /// Four ores for the generator's tests, one per habit, on top of the basic set: the package
    /// ships no ores of its own (each game brings its own), so its tests bring some.
    /// </summary>
    static class TestOres
    {
        public static readonly MaterialId Seam = new MaterialId(10);
        public static readonly MaterialId SeamLoose = new MaterialId(11);
        public static readonly MaterialId Lens = new MaterialId(12);
        public static readonly MaterialId LensLoose = new MaterialId(13);
        public static readonly MaterialId Ridge = new MaterialId(14);
        public static readonly MaterialId RidgeLoose = new MaterialId(15);
        public static readonly MaterialId Lowland = new MaterialId(16);
        public static readonly MaterialId LowlandLoose = new MaterialId(17);

        public static readonly MaterialId[] InPlace = { Seam, Lens, Ridge, Lowland };

        public static readonly OreMaterials Ores = new OreMaterials { Seam = Seam, LowlandBeds = Lowland, Lenses = Lens, RidgeBodies = Ridge };

        /// <summary>One table for tests that ask whether a material is ore or stone many times over.</summary>
        public static readonly MaterialTable Table = CreateTable();

        public static MaterialTable CreateTable() => MaterialTable.CreateBasic(
            new MaterialDefinition(Seam, "Seam ore", new Color32(40, 40, 44, 255), 0.45f, 80f, disturbed: SeamLoose, bulkingFactor: 1.4f, isStone: true, isOre: true),
            new MaterialDefinition(SeamLoose, "Loose seam ore", new Color32(52, 52, 56, 255), 0.20f, 38f, isLoose: true, isOre: true),
            new MaterialDefinition(Lens, "Lens ore", new Color32(140, 72, 48, 255), 0.70f, 85f, disturbed: LensLoose, bulkingFactor: 1.5f, isStone: true, isOre: true),
            new MaterialDefinition(LensLoose, "Loose lens ore", new Color32(150, 82, 56, 255), 0.30f, 38f, isLoose: true, isOre: true),
            new MaterialDefinition(Ridge, "Ridge ore", new Color32(78, 140, 118, 255), 0.65f, 85f, disturbed: RidgeLoose, bulkingFactor: 1.5f, isStone: true, isOre: true),
            new MaterialDefinition(RidgeLoose, "Loose ridge ore", new Color32(88, 148, 124, 255), 0.30f, 38f, isLoose: true, isOre: true),
            new MaterialDefinition(Lowland, "Lowland ore", new Color32(214, 206, 184, 255), 0.50f, 85f, disturbed: LowlandLoose, bulkingFactor: 1.45f, isStone: true, isOre: true),
            new MaterialDefinition(LowlandLoose, "Loose lowland ore", new Color32(222, 214, 192, 255), 0.25f, 36f, isLoose: true, isOre: true));
    }
}
