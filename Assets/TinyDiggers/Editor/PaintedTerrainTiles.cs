using System.IO;
using TinyDiggers.Presentation;
using PromptWaffle.Terrain;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// TinyDiggers/Import Painted Terrain Tiles (Ronan, 2026-09-24: the ground's textures are
    /// painted-natural tiles from ComfyUI). Takes the cleaned tiles in Terrain/TexturesPainted
    /// (written by Art/Tools/td_tile_clean.py as name_albedo.png and name_normal.png), gives them the
    /// import the atlas needs — readable, uncompressed, albedo sRGB and normal linear — and builds
    /// TerrainTexturesPainted.asset: a copy of the procedural set with the painted tiles swapped in,
    /// so every material still has a look and the procedural set stays as it was to compare against.
    ///
    /// Its own folder on purpose: the procedural generator deletes any texture in its folder that it
    /// did not write.
    /// </summary>
    public static class PaintedTerrainTiles
    {
        const string Folder = "Assets/TinyDiggers/Terrain/TexturesPainted";
        const string ProceduralSet = "Assets/TinyDiggers/Terrain/Textures/TerrainTextures.asset";
        const string PaintedSet = Folder + "/TerrainTexturesPainted.asset";

        /// <summary>Metres one painted tile covers on the ground.</summary>
        public const float TileMetres = 6f;

        [MenuItem("TinyDiggers/Import Painted Terrain Tiles")]
        public static void Import()
        {
            AssetDatabase.Refresh();
            foreach (var file in Directory.GetFiles(Folder, "*.png"))
            {
                var path = file.Replace('\\', '/');
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer == null)
                    continue;
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = path.EndsWith("_albedo.png");
                importer.isReadable = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            var source = AssetDatabase.LoadAssetAtPath<TerrainTextureSet>(ProceduralSet);
            var set = AssetDatabase.LoadAssetAtPath<TerrainTextureSet>(PaintedSet);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<TerrainTextureSet>();
                AssetDatabase.CreateAsset(set, PaintedSet);
            }

            var entries = new TerrainTextureSet.Entry[source.Entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var from = source.Entries[i];
                entries[i] = new TerrainTextureSet.Entry
                {
                    MaterialId = from.MaterialId, Name = from.Name, Albedo = from.Albedo, Normal = from.Normal,
                    CutAlbedo = from.CutAlbedo, CutNormal = from.CutNormal, Smoothness = from.Smoothness,
                };
            }

            var swapped = 0;
            TerrainTextureSet.Entry Of(MaterialId id)
            {
                foreach (var entry in entries)
                    if (entry.MaterialId == id.Value)
                        return entry;
                return null;
            }

            bool Tile(string name, out Texture2D albedo, out Texture2D normal)
            {
                albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Folder}/{name}_albedo.png");
                normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Folder}/{name}_normal.png");
                if (albedo != null)
                    swapped++;
                return albedo != null;
            }

            void Base(MaterialId id, string name)
            {
                var entry = Of(id);
                if (entry != null && Tile(name, out var albedo, out var normal))
                {
                    entry.Albedo = albedo;
                    entry.Normal = normal;
                }
            }

            // Grass: the meadow, with lush and dry patches; a cut through it shows earth.
            Base(MaterialTable.Topsoil, "grass_meadow");
            var grass = Of(MaterialTable.Topsoil);
            if (Tile("grass_lush", out var lush, out var lushNormal))
            {
                grass.AltAlbedo = lush;
                grass.AltNormal = lushNormal;
            }

            if (Tile("grass_dry", out var dry, out var dryNormal))
            {
                grass.Alt2Albedo = dry;
                grass.Alt2Normal = dryNormal;
            }

            if (Tile("dirt", out var earth, out var earthNormal))
            {
                grass.CutAlbedo = earth;
                grass.CutNormal = earthNormal;
            }

            Base(MaterialTable.Dirt, "dirt");
            Base(MaterialTable.DirtLoose, "dirt_loose");
            Base(MaterialTable.Sand, "sand");
            Base(MaterialTable.Rock, "rock");
            Base(MaterialTable.RockLoose, "rock_loose");
            Base(MaterialTable.Granite, "granite");
            var rock = Of(MaterialTable.Rock);
            if (Tile("rock_cut", out var face, out var faceNormal))
            {
                rock.CutAlbedo = face;
                rock.CutNormal = faceNormal;
            }

            set.SetEntries(entries);
            set.SetTileMetres(TileMetres, 0f);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"Painted terrain tiles: {swapped} tiles into {PaintedSet}");
        }
    }
}
