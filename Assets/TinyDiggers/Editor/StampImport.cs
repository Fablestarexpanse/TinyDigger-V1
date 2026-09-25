using System.IO;
using TinyDiggers.Terrain;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// TinyDiggers/Import Stamps: every cleaned heightmap in Stamps/Heightmaps (16-bit greyscale PNGs
    /// from Art/Tools/td_stamp_clean.py) gets a readable, uncompressed, linear R16 import, a
    /// <see cref="HeightStamp"/> asset beside it in Stamps, and a place in the
    /// <see cref="StampLibrary"/> at Stamps/Resources. An existing stamp keeps the size, height and
    /// blend it was given; only a new one takes the defaults below.
    /// </summary>
    public static class StampImport
    {
        const string Root = "Assets/TinyDiggers/Stamps";
        const string Heightmaps = Root + "/Heightmaps";
        const string LibraryPath = Root + "/Resources/" + StampLibrary.ResourcePath + ".asset";

        /// <summary>Starting size and height for the starter set, in metres; anything else gets 40 × 6.</summary>
        static (float Size, float Height) Defaults(string key)
        {
            switch (key)
            {
                case "crater": return (40f, 8f);
                case "mesa": return (40f, 6f);
                case "hill": return (40f, 5f);
                case "ridge": return (50f, 6f);
                case "butte": return (24f, 8f);
                case "terraces": return (40f, 6f);
                case "ridge2": return (60f, 8f);
                case "volcano": return (80f, 20f);
                case "hills3": return (50f, 5f);
                case "plateau": return (60f, 6f);
                case "drumlins": return (50f, 3f);
                case "crag": return (24f, 6f);
                default: return (40f, 6f);
            }
        }

        [MenuItem("TinyDiggers/Import Stamps")]
        public static void Import()
        {
            Directory.CreateDirectory(Heightmaps);
            Directory.CreateDirectory(Root + "/Resources");
            AssetDatabase.Refresh();

            var library = AssetDatabase.LoadAssetAtPath<StampLibrary>(LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<StampLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }

            var count = 0;
            foreach (var file in Directory.GetFiles(Heightmaps, "*.png"))
            {
                var path = file.Replace('\\', '/');
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.SingleChannel;
                importer.sRGBTexture = false;
                importer.isReadable = true;
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.wrapMode = TextureWrapMode.Clamp;
                var settings = importer.GetDefaultPlatformTextureSettings();
                settings.format = TextureImporterFormat.R16;
                importer.SetPlatformTextureSettings(settings);
                importer.SaveAndReimport();

                var key = Path.GetFileNameWithoutExtension(path);
                var stampPath = $"{Root}/{key}.asset";
                var stamp = AssetDatabase.LoadAssetAtPath<HeightStamp>(stampPath);
                if (stamp == null)
                {
                    stamp = ScriptableObject.CreateInstance<HeightStamp>();
                    var (size, height) = Defaults(key);
                    stamp.DisplayName = ObjectNames.NicifyVariableName(key);
                    stamp.NativeSize = size;
                    stamp.NativeHeight = height;
                    AssetDatabase.CreateAsset(stamp, stampPath);
                }

                stamp.Heightmap = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                EditorUtility.SetDirty(stamp);
                if (!library.Stamps.Contains(stamp))
                    library.Stamps.Add(stamp);
                count++;
            }

            library.Stamps.RemoveAll(s => s == null);
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log($"StampImport: {count} stamps, library holds {library.Stamps.Count}");
        }
    }
}
