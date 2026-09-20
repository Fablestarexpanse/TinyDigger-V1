using System;
using System.Collections.Generic;
using System.IO;
using TinyDiggers.Presentation;
using TinyDiggers.Terrain;
using UnityEditor;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Writes a placeholder texture set: one tileable albedo and normal map per material, baked
    /// to PNG, and a <see cref="TerrainTextureSet"/> that points at them.
    ///
    /// The patterns are procedural stand-ins for real art, chosen to read at RTS distance: Perlin
    /// and Worley for rock, soft blobs for the soils, fine high-frequency speckle for grass and
    /// sand. All noise is periodic, so the maps tile. The normal map is the Sobel slope of the
    /// same height field the pattern came from, stored as plain RGB rather than a Unity normal
    /// map, so the shader can read it without worrying about the compressed normal swizzle.
    ///
    /// Replacing any of the PNGs with real art needs no code: the set references the files.
    /// </summary>
    public static class TerrainTextureGenerator
    {
        const int Size = 1024;
        const string Folder = "Assets/TinyDiggers/Terrain/Textures";
        const string SetPath = "Assets/TinyDiggers/Terrain/Textures/TerrainTextures.asset";

        sealed class Recipe
        {
            public MaterialId Id;
            public string Name;
            public Color Base;

            /// <summary>Metres across one tile of the texture, which sets how coarse the pattern is.</summary>
            public float Grain = 1f;

            public float Smoothness = 0.05f;

            /// <summary>How strongly the surface bumps: metres of height across the tile.</summary>
            public float Relief = 0.6f;

            public Func<float, float, float> Height;

            /// <summary>Optional second pattern for a fresh cut through this material.</summary>
            public Func<float, float, float> CutHeight;

            public Color CutTint = Color.white;
        }

        [MenuItem("TinyDiggers/Generate Placeholder Terrain Textures")]
        public static void Generate()
        {
            Directory.CreateDirectory(Folder);
            var table = MaterialTable.CreateDefault();
            var recipes = Recipes(table);
            var entries = new List<TerrainTextureSet.Entry>();

            try
            {
                for (var i = 0; i < recipes.Count; i++)
                {
                    var recipe = recipes[i];
                    EditorUtility.DisplayProgressBar("Terrain textures", recipe.Name, (float)i / recipes.Count);
                    var entry = new TerrainTextureSet.Entry
                    {
                        MaterialId = recipe.Id.Value,
                        Name = recipe.Name,
                        Smoothness = recipe.Smoothness,
                    };

                    Write(recipe, recipe.Height, Color.white, recipe.Name.ToLowerInvariant(), out entry.Albedo, out entry.Normal);
                    if (recipe.CutHeight != null)
                        Write(recipe, recipe.CutHeight, recipe.CutTint, recipe.Name.ToLowerInvariant() + "cut", out entry.CutAlbedo, out entry.CutNormal);
                    entries.Add(entry);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var set = AssetDatabase.LoadAssetAtPath<TerrainTextureSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<TerrainTextureSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }

            set.SetEntries(entries.ToArray());
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Terrain textures: wrote {entries.Count} materials to {Folder} at {Size}x{Size}.");
        }

        static void Write(Recipe recipe, Func<float, float, float> height, Color tint, string name, out Texture2D albedo, out Texture2D normal)
        {
            var heights = new float[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                    heights[y * Size + x] = Mathf.Clamp01(height(x / (float)Size, y / (float)Size));
            }

            var albedoPixels = new Color32[Size * Size];
            var normalPixels = new Color32[Size * Size];
            var baseColor = recipe.Base * tint;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var at = y * Size + x;
                    // Pattern darkens and lightens the material's colour rather than replacing it,
                    // so a placeholder still reads as the right stuff from a distance.
                    var shade = Mathf.Lerp(0.78f, 1.18f, heights[at]);
                    albedoPixels[at] = new Color(
                        Mathf.Clamp01(baseColor.r * shade),
                        Mathf.Clamp01(baseColor.g * shade),
                        Mathf.Clamp01(baseColor.b * shade),
                        1f);

                    // Sobel of the height field, wrapped, in texture space.
                    var left = heights[y * Size + Wrap(x - 1)];
                    var right = heights[y * Size + Wrap(x + 1)];
                    var down = heights[Wrap(y - 1) * Size + x];
                    var up = heights[Wrap(y + 1) * Size + x];
                    var slope = new Vector3((left - right) * recipe.Relief * Size / 64f, (down - up) * recipe.Relief * Size / 64f, 1f).normalized;
                    normalPixels[at] = new Color(slope.x * 0.5f + 0.5f, slope.y * 0.5f + 0.5f, slope.z * 0.5f + 0.5f, 1f);
                }
            }

            albedo = Save(albedoPixels, $"{name}_albedo.png", sRGB: true);
            normal = Save(normalPixels, $"{name}_normal.png", sRGB: false);
        }

        static Texture2D Save(Color32[] pixels, string fileName, bool sRGB)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, !sRGB);
            texture.SetPixels32(pixels);
            texture.Apply();
            var path = $"{Folder}/{fileName}";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = sRGB;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.mipmapEnabled = true;
            importer.isReadable = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static int Wrap(int v) => (v + Size) % Size;

        // --- the patterns ---------------------------------------------------------------------

        static List<Recipe> Recipes(MaterialTable table)
        {
            Color ColorOf(MaterialId id) => table.Get(id).Color;

            return new List<Recipe>
            {
                new Recipe
                {
                    Id = MaterialTable.Bedrock, Name = "Bedrock", Base = ColorOf(MaterialTable.Bedrock),
                    Smoothness = 0.02f, Relief = 0.9f,
                    Height = (x, y) => 0.45f * Worley(x, y, 6) + 0.35f * Fbm(x, y, 16, 4) + 0.2f * Fbm(x, y, 64, 2),
                },
                new Recipe
                {
                    Id = MaterialTable.Granite, Name = "Granite", Base = ColorOf(MaterialTable.Granite),
                    Smoothness = 0.12f, Relief = 0.5f,
                    // Speckled: fine grain over a slow mottle.
                    Height = (x, y) => 0.55f * Fbm(x, y, 128, 2) + 0.3f * Fbm(x, y, 8, 3) + 0.15f * Worley(x, y, 10),
                },
                new Recipe
                {
                    Id = MaterialTable.Rock, Name = "Rock", Base = ColorOf(MaterialTable.Rock),
                    Smoothness = 0.05f, Relief = 0.8f,
                    Height = (x, y) => 0.5f * Fbm(x, y, 16, 4) + 0.5f * Worley(x, y, 8),
                    // A cut face is blockier and cleaner: bigger plates, less weathering.
                    CutHeight = (x, y) => 0.7f * Worley(x, y, 4) + 0.3f * Fbm(x, y, 32, 2),
                    CutTint = new Color(1.06f, 1.05f, 1.02f),
                },
                new Recipe
                {
                    Id = MaterialTable.Clay, Name = "Clay", Base = ColorOf(MaterialTable.Clay),
                    Smoothness = 0.2f, Relief = 0.35f,
                    Height = (x, y) => 0.7f * Fbm(x, y, 8, 3) + 0.3f * Fbm(x, y, 32, 2),
                },
                new Recipe
                {
                    Id = MaterialTable.Dirt, Name = "Dirt", Base = ColorOf(MaterialTable.Dirt),
                    Smoothness = 0.04f, Relief = 0.5f,
                    // Soft blobs with a little grain in them.
                    Height = (x, y) => 0.6f * Fbm(x, y, 12, 3) + 0.25f * Worley(x, y, 16) + 0.15f * Fbm(x, y, 96, 2),
                    CutHeight = (x, y) => 0.75f * Fbm(x, y, 20, 3) + 0.25f * Fbm(x, y, 64, 2),
                    CutTint = new Color(0.95f, 0.93f, 0.9f),
                },
                new Recipe
                {
                    Id = MaterialTable.Sand, Name = "Sand", Base = ColorOf(MaterialTable.Sand),
                    Smoothness = 0.25f, Relief = 0.3f,
                    // Fine, almost uniform, with slow ripples across it.
                    Height = (x, y) => 0.6f * Fbm(x, y, 160, 2) + 0.4f * Mathf.Abs(Mathf.Sin((x * 6f + Fbm(x, y, 8, 2) * 2f) * Mathf.PI)),
                },
                new Recipe
                {
                    Id = MaterialTable.Topsoil, Name = "Grass", Base = ColorOf(MaterialTable.Topsoil),
                    Smoothness = 0.1f, Relief = 0.45f,
                    // Fine high-frequency speckle, with patchy growth over it.
                    Height = (x, y) => 0.55f * Fbm(x, y, 192, 2) + 0.3f * Fbm(x, y, 24, 3) + 0.15f * Worley(x, y, 24),
                },
                new Recipe
                {
                    Id = MaterialTable.RockLoose, Name = "RockLoose", Base = ColorOf(MaterialTable.RockLoose),
                    Smoothness = 0.06f, Relief = 0.7f,
                    // Pebbles: small cells, strongly separated.
                    Height = (x, y) => 0.8f * Worley(x, y, 20) + 0.2f * Fbm(x, y, 64, 2),
                },
                new Recipe
                {
                    Id = MaterialTable.DirtLoose, Name = "DirtLoose", Base = ColorOf(MaterialTable.DirtLoose),
                    Smoothness = 0.04f, Relief = 0.55f,
                    // Clods: bigger than pebbles, softer edges.
                    Height = (x, y) => 0.55f * Worley(x, y, 12) + 0.45f * Fbm(x, y, 40, 3),
                },
            };
        }

        /// <summary>Periodic value noise in 0..1; <paramref name="period"/> lattice cells across the tile.</summary>
        static float Noise(float x, float y, int period)
        {
            var fx = x * period;
            var fy = y * period;
            var x0 = Mathf.FloorToInt(fx);
            var y0 = Mathf.FloorToInt(fy);
            var tx = Smooth(fx - x0);
            var ty = Smooth(fy - y0);
            var a = Hash(x0, y0, period);
            var b = Hash(x0 + 1, y0, period);
            var c = Hash(x0, y0 + 1, period);
            var d = Hash(x0 + 1, y0 + 1, period);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        /// <summary>Several octaves of <see cref="Noise"/>, each twice as fine and half as strong.</summary>
        static float Fbm(float x, float y, int period, int octaves)
        {
            var sum = 0f;
            var weight = 0f;
            var amplitude = 1f;
            for (var i = 0; i < octaves; i++)
            {
                sum += Noise(x, y, period << i) * amplitude;
                weight += amplitude;
                amplitude *= 0.5f;
            }

            return sum / weight;
        }

        /// <summary>Periodic Worley (cellular) noise: 0 at a feature point, 1 far from every one.</summary>
        static float Worley(float x, float y, int period)
        {
            var fx = x * period;
            var fy = y * period;
            var cellX = Mathf.FloorToInt(fx);
            var cellY = Mathf.FloorToInt(fy);
            var nearest = float.MaxValue;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var gx = cellX + dx;
                    var gy = cellY + dy;
                    var pointX = gx + Hash(gx, gy, period);
                    var pointY = gy + Hash(gy + 41, gx - 17, period);
                    var deltaX = pointX - fx;
                    var deltaY = pointY - fy;
                    nearest = Mathf.Min(nearest, deltaX * deltaX + deltaY * deltaY);
                }
            }

            return Mathf.Clamp01(Mathf.Sqrt(nearest));
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        /// <summary>A stable 0..1 hash of a lattice point, wrapped so the pattern tiles.</summary>
        static float Hash(int x, int y, int period)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            var h = x * 374761393 + y * 668265263 + period * 2147483647;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}
