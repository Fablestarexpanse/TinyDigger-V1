using System.IO;
using UnityEditor;
using UnityEngine;
using static TinyDiggers.EditorTools.TileableNoise;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Writes the dam's concrete detail set: a tileable albedo, normal and mask, 1024² over
    /// <see cref="TileMetres"/> of wall (4 mm a pixel). This is only the close-up grain of cast
    /// concrete. The formwork, streaks, grime and waterline are drawn at world scale by the
    /// TinyDiggers/Concrete shader, so nothing in the tile is big enough to show a repeat.
    ///
    /// - Albedo: a cool mid grey with soft blotches (cement paste, not paint), fine sand grain, a
    ///   scatter of exposed aggregate, and dark air voids ("bugholes"), the pits every cast face has.
    /// - Normal: OpenGL-style (green up), plain RGB, from the same height field.
    /// - Mask: R cavity (1 open, 0 deep in a void), G bughole, B roughness grain.
    /// </summary>
    public static class DamTextureGenerator
    {
        const int Size = 1024;
        const float TileMetres = 4f;
        const string Folder = "Assets/TinyDiggers/Art/Props/Dam/Textures";

        [MenuItem("TinyDiggers/Generate Dam Textures")]
        public static void Generate()
        {
            Directory.CreateDirectory(Folder);
            var height = new float[Size * Size];
            var albedo = new Color32[Size * Size];
            var mask = new Color32[Size * Size];
            var normal = new Color32[Size * Size];

            var baseColour = new Color(0.60f, 0.60f, 0.585f);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var u = x / (float)Size;
                    var v = y / (float)Size;
                    var at = y * Size + x;

                    // Soft blotches of paste, about 30 cm, and a finer cloud inside them.
                    var blotch = Fbm(u, v, 12, 3) - 0.5f;
                    var cloud = Fbm(u, v, 48, 2) - 0.5f;
                    // Sand grain: a pixel or two.
                    var grain = Noise(u, v, 512) - 0.5f;

                    // Exposed aggregate: small stones, 5–15 mm, each its own tone.
                    var stone = Worley(u, v, 260);
                    var stoneTone = Hash(Mathf.FloorToInt(u * 260), Mathf.FloorToInt(v * 260), 260) - 0.5f;
                    var inStone = 1f - Mathf.Clamp01((stone - 0.18f) / 0.06f);

                    // Bugholes: air voids 3–10 mm, in some cells only, clustered by a slow field.
                    var voids = Worley(u, v, 150);
                    var cluster = Fbm(u + 0.37f, v + 0.11f, 6, 2);
                    var keep = Hash(Mathf.FloorToInt(u * 150) + 7, Mathf.FloorToInt(v * 150) - 3, 150) < 0.22f + cluster * 0.35f;
                    var hole = keep ? 1f - Mathf.Clamp01((voids - 0.07f) / 0.05f) : 0f;

                    var tone = 1f + blotch * 0.16f + cloud * 0.08f + grain * 0.06f + inStone * stoneTone * 0.18f;
                    var colour = baseColour * tone;
                    // A faint warm/cool drift between blotches, as real cement has.
                    colour.r += blotch * 0.012f;
                    colour.b -= blotch * 0.012f;
                    colour = Color.Lerp(colour, colour * 0.35f, hole);
                    albedo[at] = new Color(Mathf.Clamp01(colour.r), Mathf.Clamp01(colour.g), Mathf.Clamp01(colour.b), 1f);

                    height[at] = 0.5f + blotch * 0.25f + cloud * 0.2f + grain * 0.35f + inStone * 0.25f - hole * 1.4f;
                    var rough = 0.5f + grain * 0.6f + cloud * 0.3f;
                    mask[at] = new Color(1f - hole, hole, Mathf.Clamp01(rough), 1f);
                }
            }

            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var left = height[y * Size + Wrap(x - 1)];
                    var right = height[y * Size + Wrap(x + 1)];
                    var down = height[Wrap(y - 1) * Size + x];
                    var up = height[Wrap(y + 1) * Size + x];
                    var slope = new Vector3((left - right) * 2.2f, (down - up) * 2.2f, 1f).normalized;
                    normal[y * Size + x] = new Color(slope.x * 0.5f + 0.5f, slope.y * 0.5f + 0.5f, slope.z * 0.5f + 0.5f, 1f);
                }
            }

            Save(albedo, "concrete_albedo.png", sRGB: true);
            Save(normal, "concrete_normal.png", sRGB: false);
            Save(mask, "concrete_mask.png", sRGB: false);
            Debug.Log($"Dam textures written to {Folder} ({Size}² over {TileMetres} m).");
        }

        static int Wrap(int v) => ((v % Size) + Size) % Size;

        static void Save(Color32[] pixels, string fileName, bool sRGB)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, !sRGB);
            texture.SetPixels32(pixels);
            texture.Apply();
            var path = $"{Folder}/{fileName}";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = sRGB;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.mipmapEnabled = true;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
    }
}
