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
    /// Writes the terrain texture set: one tileable albedo and normal map per material, baked to
    /// PNG, and a <see cref="TerrainTextureSet"/> that points at them.
    ///
    /// Written to Ronan's texture brief (2026-09-20):
    /// - 1024² and seamless. Every noise here is periodic, so a tile meets itself exactly.
    /// - One tile covers <see cref="TileMetres"/> of ground, so feature sizes are given in pixels
    ///   at that scale and mean something real: a pebble is 10–30 px, a soil clod 20–60 px, a
    ///   grass blade is below a pixel and only shows as grain.
    /// - Matte, warm and light. Tiny Glade, not photoreal: every material sits inside about two
    ///   stops of the next, so nothing goes black in shade and a shadowed slope still reads as the
    ///   stuff it is made of. The colours here track MaterialTable's.
    /// - No strong shadow baked into the albedo: the pattern moves the colour by a few percent and
    ///   the relief lives in the normal map, which is where the lighting should come from.
    /// - Even luminance across the tile, so a repeat grid does not show. Every pattern has its
    ///   low-frequency drift flattened out before it is used (<see cref="Flatten"/>).
    ///
    /// Normals are OpenGL-style (green up) plain RGB rather than Unity's packed normal map, so the
    /// shader can read them without the compressed swizzle.
    ///
    /// Replacing any PNG with real art needs no code: the set references the files.
    /// </summary>
    public static class TerrainTextureGenerator
    {
        const int Size = 1024;

        /// <summary>Metres of ground one tile covers. The shader repeats the albedo at this scale.</summary>
        const float TileMetres = 0.5f;

        const string Folder = "Assets/TinyDiggers/Terrain/Textures";
        const string SetPath = "Assets/TinyDiggers/Terrain/Textures/TerrainTextures.asset";

        /// <summary>A lattice period that makes features roughly this many pixels across.</summary>
        static int Feature(float pixels) => Mathf.Max(1, Mathf.RoundToInt(Size / Mathf.Max(1f, pixels)));

        /// <summary>One look: a colour, an optional second colour flecked through it, and relief.</summary>
        sealed class Pattern
        {
            public string File;
            public Color Base;

            /// <summary>Mixed into <see cref="Base"/> where <see cref="Fleck"/> says so. Lichen, clover, stones.</summary>
            public Color Fleck;

            public Func<float, float, float> Height;

            /// <summary>0..1, how much of <see cref="Fleck"/> shows. Null for none.</summary>
            public Func<float, float, float> Flecks;

            /// <summary>Metres of relief across the tile: how hard the normal map bumps.</summary>
            public float Relief = 0.5f;

            /// <summary>How far the pattern moves the albedo either way. Kept small on purpose.</summary>
            public float Shade = 0.1f;

            /// <summary>Pixels across the low-frequency drift that gets flattened out.</summary>
            public float Evenness = 256f;
        }

        sealed class Recipe
        {
            public MaterialId Id;
            public string Name;
            public float Smoothness = 0.05f;
            public Pattern Main;

            /// <summary>Optional: how the material looks where a cut has just exposed it.</summary>
            public Pattern Cut;
        }

        [MenuItem("TinyDiggers/Generate Terrain Textures")]
        public static void Generate()
        {
            Directory.CreateDirectory(Folder);
            var recipes = Recipes();
            var entries = new List<TerrainTextureSet.Entry>();
            var written = new HashSet<string>();

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

                    Write(recipe.Main, out entry.Albedo, out entry.Normal);
                    written.Add(recipe.Main.File);
                    if (recipe.Cut != null)
                    {
                        Write(recipe.Cut, out entry.CutAlbedo, out entry.CutNormal);
                        written.Add(recipe.Cut.File);
                    }

                    entries.Add(entry);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var removed = RemoveStale(written);

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
            Debug.Log(
                $"Terrain textures: wrote {written.Count} looks for {entries.Count} materials to {Folder} " +
                $"at {Size}x{Size} ({TileMetres} m a tile)" + (removed == 0 ? "." : $", and deleted {removed} stale file(s)."));
        }

        /// <summary>Deletes PNGs in the folder that this run no longer produces, so renames do not leave litter.</summary>
        static int RemoveStale(HashSet<string> written)
        {
            var removed = 0;
            foreach (var path in Directory.GetFiles(Folder, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.EndsWith("_albedo") && !name.EndsWith("_normal"))
                    continue;
                var look = name.Substring(0, name.LastIndexOf('_'));
                if (written.Contains(look))
                    continue;
                AssetDatabase.DeleteAsset(path.Replace('\\', '/'));
                removed++;
            }

            return removed;
        }

        static void Write(Pattern pattern, out Texture2D albedo, out Texture2D normal)
        {
            var heights = new float[Size * Size];
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                    heights[y * Size + x] = pattern.Height(x / (float)Size, y / (float)Size);

            Flatten(heights, Mathf.RoundToInt(pattern.Evenness));
            Normalise(heights);

            var albedoPixels = new Color32[Size * Size];
            var normalPixels = new Color32[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var at = y * Size + x;

                    // The pattern nudges the material's own colour rather than replacing it, and
                    // only by a few percent: a baked-in shadow would fight the real light.
                    var shade = 1f + (heights[at] - 0.5f) * 2f * pattern.Shade;
                    var color = pattern.Base * shade;
                    if (pattern.Flecks != null)
                        color = Color.Lerp(color, pattern.Fleck * shade, Mathf.Clamp01(pattern.Flecks(x / (float)Size, y / (float)Size)));

                    albedoPixels[at] = new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b), 1f);

                    // Sobel of the height field, wrapped, in texture space. Green is up.
                    var left = heights[y * Size + Wrap(x - 1)];
                    var right = heights[y * Size + Wrap(x + 1)];
                    var down = heights[Wrap(y - 1) * Size + x];
                    var up = heights[Wrap(y + 1) * Size + x];
                    var slope = new Vector3(
                        (left - right) * pattern.Relief * Size / 64f,
                        (down - up) * pattern.Relief * Size / 64f,
                        1f).normalized;
                    normalPixels[at] = new Color(slope.x * 0.5f + 0.5f, slope.y * 0.5f + 0.5f, slope.z * 0.5f + 0.5f, 1f);
                }
            }

            albedo = Save(albedoPixels, $"{pattern.File}_albedo.png", sRGB: true);
            normal = Save(normalPixels, $"{pattern.File}_normal.png", sRGB: false);
        }

        /// <summary>
        /// Takes the slow drift out of a pattern: blurs it hard, subtracts that, and puts the mean
        /// back. What is left is the detail without any large bright or dark region, which is what
        /// stops a tiled texture showing its own grid from a distance.
        /// </summary>
        static void Flatten(float[] values, int radius)
        {
            if (radius <= 1)
                return;

            var blurred = BoxBlur(values, radius);
            var mean = 0f;
            for (var i = 0; i < values.Length; i++)
                mean += values[i];
            mean /= values.Length;

            for (var i = 0; i < values.Length; i++)
                values[i] = values[i] - blurred[i] + mean;
        }

        /// <summary>Separable wrapped box blur, run twice so the falloff is smooth rather than square.</summary>
        static float[] BoxBlur(float[] values, int radius)
        {
            var pass = new float[values.Length];
            var output = new float[values.Length];
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var source = repeat == 0 ? values : output;
                var width = radius * 2 + 1;

                for (var y = 0; y < Size; y++)
                {
                    var row = y * Size;
                    var sum = 0f;
                    for (var k = -radius; k <= radius; k++)
                        sum += source[row + Wrap(k)];
                    for (var x = 0; x < Size; x++)
                    {
                        pass[row + x] = sum / width;
                        sum += source[row + Wrap(x + radius + 1)] - source[row + Wrap(x - radius)];
                    }
                }

                for (var x = 0; x < Size; x++)
                {
                    var sum = 0f;
                    for (var k = -radius; k <= radius; k++)
                        sum += pass[Wrap(k) * Size + x];
                    for (var y = 0; y < Size; y++)
                    {
                        output[y * Size + x] = sum / width;
                        sum += pass[Wrap(y + radius + 1) * Size + x] - pass[Wrap(y - radius) * Size + x];
                    }
                }
            }

            return output;
        }

        /// <summary>Rescales to 0..1 so every material gets the same amount of pattern to work with.</summary>
        static void Normalise(float[] values)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var value in values)
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }

            var range = max - min;
            if (range < 1e-5f)
                return;
            for (var i = 0; i < values.Length; i++)
                values[i] = (values[i] - min) / range;
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

        static int Wrap(int v) => ((v % Size) + Size) % Size;

        // --- the looks ------------------------------------------------------------------------
        //
        // Colours are given here rather than taken from MaterialTable: the table's colours are what
        // a material reads as in one flat pixel (designations, the minimap later), and these are
        // what it reads as under a metre of texture. They are deliberately muted and close together.

        static List<Recipe> Recipes()
        {
            return new List<Recipe>
            {
                new Recipe
                {
                    Id = MaterialTable.Topsoil, Name = "Grass", Smoothness = 0.08f,
                    // Short turf: the blades themselves are under a pixel at 0.5 m a tile, so they
                    // are grain, not shapes. The patchiness is what the eye actually reads.
                    Main = new Pattern
                    {
                        File = "grass",
                        // Diorama meadow (Ronan's references, 2026-09-21): saturated yellow-green
                        // that the sun lifts to #B4BF23 and the shade drops to #264122.
                        Base = new Color(0.50f, 0.60f, 0.13f),
                        Fleck = new Color(0.66f, 0.72f, 0.16f),
                        Height = (x, y) => 0.5f * Fbm(x, y, Feature(3f), 2)
                                         + 0.32f * Fbm(x, y, Feature(28f), 3)
                                         + 0.18f * Worley(x, y, Feature(90f)),
                        // Tiny clover bits: sparse, a little lighter and yellower than the turf.
                        Flecks = (x, y) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 0.95f, Fbm(x, y, Feature(14f), 2))) * 0.6f,
                        Relief = 0.45f, Shade = 0.14f, Evenness = 220f,
                    },
                    // Dug turf shows the dark crumbly soil under it, roots and all.
                    Cut = new Pattern
                    {
                        File = "topsoil",
                        Base = new Color(0.44f, 0.36f, 0.27f),
                        Fleck = new Color(0.60f, 0.55f, 0.42f),
                        Height = (x, y) => 0.55f * (1f - Worley(x, y, Feature(38f)))
                                         + 0.3f * Fbm(x, y, Feature(9f), 3)
                                         + 0.15f * Fbm(x, y, Feature(120f), 2),
                        // Roots: thin, pale, threaded through the clods.
                        Flecks = (x, y) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.78f, 0.98f, Fbm(x, y, Feature(5f), 3))) * 0.45f,
                        Relief = 0.55f, Shade = 0.12f, Evenness = 200f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.Dirt, Name = "Dirt", Smoothness = 0.04f,
                    // Packed earth: fine and fairly even, with the odd small stone in it.
                    Main = new Pattern
                    {
                        File = "dirt",
                        // Dry-grass olive: the dirt band between grass and rock reads as sparse
                        // turf, not as a khaki outline round every outcrop (rock-edge pass).
                        Base = new Color(0.36f, 0.42f, 0.14f),
                        // Stones a shade lighter than the earth, not pale: they cover much of the
                        // tile, and pale ones made dirt average a cream #A8A293 (Look loop 5).
                        Fleck = new Color(0.44f, 0.46f, 0.22f),
                        Height = (x, y) => 0.55f * Fbm(x, y, Feature(6f), 3)
                                         + 0.3f * Fbm(x, y, Feature(45f), 2)
                                         + 0.15f * (1f - Worley(x, y, Feature(26f))),
                        // Stones: raised, paler, and only where a cell centre is.
                        Flecks = (x, y) => 1f - Mathf.SmoothStep(0.1f, 0.3f, Worley(x, y, Feature(22f))),
                        Relief = 0.45f, Shade = 0.1f, Evenness = 240f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.DirtLoose, Name = "DirtLoose", Smoothness = 0.04f,
                    // Freshly turned: same earth, lumpier, and the lumps shadow each other, which
                    // is relief rather than painted-in darkness.
                    Main = new Pattern
                    {
                        File = "dirt_loose",
                        Base = new Color(0.40f, 0.43f, 0.17f),
                        Fleck = new Color(0.46f, 0.47f, 0.24f),
                        Height = (x, y) => 0.62f * (1f - Worley(x, y, Feature(40f)))
                                         + 0.25f * Fbm(x, y, Feature(12f), 3)
                                         + 0.13f * Fbm(x, y, Feature(80f), 2),
                        Flecks = (x, y) => 1f - Mathf.SmoothStep(0.1f, 0.3f, Worley(x, y, Feature(20f))),
                        Relief = 0.75f, Shade = 0.14f, Evenness = 200f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.Sand, Name = "Sand", Smoothness = 0.22f,
                    // Fine warm grain and almost nothing else: at this scale sand is texture, not
                    // shape. The ripples are barely there on purpose.
                    Main = new Pattern
                    {
                        File = "sand",
                        // Toned down for the diorama sun: at 0.89 the beaches glared cream.
                        Base = new Color(0.70f, 0.62f, 0.44f),
                        Height = (x, y) => 0.62f * Fbm(x, y, Feature(3f), 2)
                                         + 0.23f * Fbm(x, y, Feature(60f), 2)
                                         + 0.15f * Ripple(x, y, Feature(150f)),
                        Relief = 0.22f, Shade = 0.06f, Evenness = 200f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.Rock, Name = "Rock", Smoothness = 0.05f,
                    // Weathered rock: rounded forms, softened edges, lichen in the hollows.
                    Main = new Pattern
                    {
                        File = "rock",
                        // Dark grey-green outcrop, as the references' ridges (#3F4A3A lit).
                        Base = new Color(0.27f, 0.31f, 0.27f),
                        Fleck = new Color(0.42f, 0.50f, 0.22f),
                        Height = (x, y) => 0.6f * Smooth(1f - Worley(x, y, Feature(110f)))
                                         + 0.25f * Fbm(x, y, Feature(20f), 3)
                                         + 0.15f * Fbm(x, y, Feature(4f), 2),
                        // Lichen gathers in the hollows, which is where a cell's edge runs.
                        Flecks = (x, y) => Mathf.SmoothStep(0.45f, 0.85f, Worley(x, y, Feature(110f)))
                                         * Mathf.SmoothStep(0.45f, 0.8f, Fbm(x, y, Feature(16f), 2)) * 0.7f,
                        Relief = 0.7f, Shade = 0.11f, Evenness = 260f,
                    },
                    // A fresh face: sharp fractures, no lichen, a shade darker than weathered rock.
                    Cut = new Pattern
                    {
                        File = "rock_cut",
                        Base = new Color(0.24f, 0.27f, 0.25f),
                        Height = (x, y) => 0.7f * (1f - Angular(x, y, Feature(170f)))
                                         + 0.2f * Ridge(Angular(x, y, Feature(60f)))
                                         + 0.1f * Fbm(x, y, Feature(8f), 2),
                        Relief = 0.9f, Shade = 0.13f, Evenness = 300f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.RockLoose, Name = "RockLoose", Smoothness = 0.06f,
                    // Rubble: angular, fist-sized, so 80–120 px across at this scale.
                    Main = new Pattern
                    {
                        File = "rock_loose",
                        Base = new Color(0.33f, 0.35f, 0.32f),
                        Fleck = new Color(0.25f, 0.27f, 0.25f),
                        Height = (x, y) => 0.75f * (1f - Angular(x, y, Feature(100f)))
                                         + 0.15f * (1f - Angular(x, y, Feature(30f)))
                                         + 0.1f * Fbm(x, y, Feature(6f), 2),
                        // The gaps between pieces read darker, which is where the shadow belongs.
                        Flecks = (x, y) => Mathf.SmoothStep(0.5f, 0.85f, Angular(x, y, Feature(100f))),
                        Relief = 0.95f, Shade = 0.13f, Evenness = 300f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.Granite, Name = "Granite", Smoothness = 0.12f,
                    // Speckled pink-grey, and a tighter grain than rock: the speckle is the point.
                    Main = new Pattern
                    {
                        File = "granite",
                        Base = new Color(0.38f, 0.39f, 0.37f),
                        Fleck = new Color(0.26f, 0.27f, 0.27f),
                        Height = (x, y) => 0.55f * Worley(x, y, Feature(12f))
                                         + 0.28f * Fbm(x, y, Feature(4f), 2)
                                         + 0.17f * Fbm(x, y, Feature(70f), 2),
                        Flecks = (x, y) => 1f - Mathf.SmoothStep(0.15f, 0.45f, Worley(x, y, Feature(11f))),
                        Relief = 0.4f, Shade = 0.09f, Evenness = 180f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.Bedrock, Name = "Bedrock", Smoothness = 0.02f,
                    // Dark blue-grey and nearly uniform: faint banding is all it gets, because at
                    // the bottom of a pit it should read as a floor, not as a feature.
                    Main = new Pattern
                    {
                        File = "bedrock",
                        Base = new Color(0.40f, 0.41f, 0.45f),
                        Height = (x, y) => 0.45f * Band(x, y, 7f)
                                         + 0.35f * Fbm(x, y, Feature(5f), 2)
                                         + 0.2f * Fbm(x, y, Feature(55f), 2),
                        Relief = 0.3f, Shade = 0.07f, Evenness = 200f,
                    },
                },
                new Recipe
                {
                    Id = MaterialTable.Clay, Name = "Clay", Smoothness = 0.2f,
                    // Not in the brief, but the material exists, so it gets a look rather than the
                    // flat-colour fallback: smooth, damp, close-grained.
                    Main = new Pattern
                    {
                        File = "clay",
                        Base = new Color(0.46f, 0.36f, 0.26f),
                        Height = (x, y) => 0.6f * Fbm(x, y, Feature(35f), 3) + 0.4f * Fbm(x, y, Feature(8f), 2),
                        Relief = 0.3f, Shade = 0.08f, Evenness = 220f,
                    },
                },
            };
        }

        // --- noise ---------------------------------------------------------------------------

        /// <summary>Slow parallel banding, bent by noise so it is not a ruled line.</summary>
        static float Band(float x, float y, float bands) =>
            0.5f + 0.5f * Mathf.Sin((y * bands + Fbm(x, y, 4, 2) * 1.5f) * Mathf.PI * 2f);

        /// <summary>Wind-blown ripples: a bent sine, shallow.</summary>
        static float Ripple(float x, float y, int period) =>
            0.5f + 0.5f * Mathf.Sin((x * 5f + Fbm(x, y, Mathf.Max(2, period / 16), 2) * 2.5f) * Mathf.PI * 2f);

        /// <summary>Turns a cell distance into a sharp crease, for fractured faces.</summary>
        static float Ridge(float value) => 1f - Mathf.Abs(1f - 2f * value);

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

        /// <summary>
        /// Worley on a square metric rather than a round one: the cells come out as flats and
        /// straight edges, which is what makes rubble and a fresh fracture read as broken stone
        /// rather than as pebbles.
        /// </summary>
        static float Angular(float x, float y, int period)
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
                    nearest = Mathf.Min(nearest, Mathf.Max(Mathf.Abs(pointX - fx), Mathf.Abs(pointY - fy)));
                }
            }

            return Mathf.Clamp01(nearest);
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
