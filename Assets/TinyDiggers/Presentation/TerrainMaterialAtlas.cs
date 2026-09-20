using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The material set as the shader wants it: one <see cref="Texture2DArray"/> of albedos and
    /// one of normals, so every material on the map is sampled from the same pair of textures and
    /// a chunk is still one draw call whatever it is made of.
    ///
    /// Slice 0 is blank and stands for "no material"; slices 1..N are the materials by id, so the
    /// shader can index straight with the id it reads out of the cell map. Cut variants, where a
    /// material has one, are appended after those, and <see cref="Parameters"/> tells the shader
    /// which slice to use for a fresh cut and how smooth the material is.
    /// </summary>
    public sealed class TerrainMaterialAtlas : IDisposable
    {
        /// <summary>x: smoothness. y: slice to use on a cut face. z, w: spare.</summary>
        public const int MaxSlices = 24;

        readonly Texture2DArray _albedo;
        readonly Texture2DArray _normal;
        bool _disposed;

        public TerrainMaterialAtlas(MaterialTable table, TerrainTextureSet set)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            if (set == null)
                throw new ArgumentNullException(nameof(set));

            var reference = FindReference(set);
            if (reference == null)
                throw new InvalidOperationException("The terrain texture set has no albedo textures. Run TinyDiggers > Generate Placeholder Terrain Textures.");

            Size = reference.width;
            var slices = table.MaxId + 1;
            var cuts = 0;
            foreach (var entry in set.Entries)
                if (entry != null && entry.CutAlbedo != null)
                    cuts++;
            slices += cuts;
            if (slices > MaxSlices)
                throw new InvalidOperationException($"{slices} texture slices is more than the shader's {MaxSlices}.");

            _albedo = NewArray(Size, slices, linear: false);
            _normal = NewArray(Size, slices, linear: true);
            Parameters = new Vector4[MaxSlices];
            for (var i = 0; i < MaxSlices; i++)
                Parameters[i] = new Vector4(0.05f, i, 0f, 0f);

            // Blank slice 0, so a cell with no material still samples something sane.
            Fill(0, Color.gray, new Color(0.5f, 0.5f, 1f));

            var nextCut = table.MaxId + 1;
            for (var id = 1; id <= table.MaxId; id++)
            {
                var materialId = new MaterialId((byte)id);
                var entry = set.Find(materialId);
                if (entry == null || entry.Albedo == null)
                {
                    var color = table.Contains(materialId) ? (Color)table.Get(materialId).Color : Color.magenta;
                    Fill(id, color, new Color(0.5f, 0.5f, 1f));
                    Parameters[id] = new Vector4(0.05f, id, 0f, 0f);
                    continue;
                }

                Copy(entry.Albedo, entry.Normal, id);
                var cutSlice = id;
                if (entry.CutAlbedo != null)
                {
                    cutSlice = nextCut++;
                    Copy(entry.CutAlbedo, entry.CutNormal ?? entry.Normal, cutSlice);
                    Parameters[cutSlice] = new Vector4(entry.Smoothness, cutSlice, 0f, 0f);
                }

                Parameters[id] = new Vector4(entry.Smoothness, cutSlice, 0f, 0f);
            }

            _albedo.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            _normal.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            SliceCount = slices;
        }

        /// <summary>Texels across one tile of every texture in the set.</summary>
        public int Size { get; }

        public int SliceCount { get; }

        public Texture2DArray Albedo => _albedo;

        public Texture2DArray Normal => _normal;

        /// <summary>Per-slice shader parameters, indexed by slice.</summary>
        public Vector4[] Parameters { get; }

        static Texture2D FindReference(TerrainTextureSet set)
        {
            foreach (var entry in set.Entries)
                if (entry != null && entry.Albedo != null)
                    return entry.Albedo;
            return null;
        }

        static Texture2DArray NewArray(int size, int slices, bool linear)
        {
            var array = new Texture2DArray(size, size, slices, TextureFormat.RGBA32, mipChain: true, linear)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                name = linear ? "Terrain Normals" : "Terrain Albedos",
                hideFlags = HideFlags.DontSave,
            };
            return array;
        }

        void Copy(Texture2D albedo, Texture2D normal, int slice)
        {
            SetSlice(_albedo, slice, albedo, Color.magenta);
            SetSlice(_normal, slice, normal, new Color(0.5f, 0.5f, 1f));
        }

        void SetSlice(Texture2DArray array, int slice, Texture2D source, Color fallback)
        {
            if (source == null || source.width != Size || source.height != Size)
            {
                Fill(array, slice, fallback);
                return;
            }

            try
            {
                array.SetPixels(source.GetPixels(), slice, 0);
            }
            catch (UnityException)
            {
                // Not readable: the importer settings the generator applies were changed.
                Debug.LogWarning($"Terrain texture '{source.name}' is not readable; using a flat colour instead.");
                Fill(array, slice, fallback);
            }
        }

        void Fill(int slice, Color albedo, Color normal)
        {
            Fill(_albedo, slice, albedo);
            Fill(_normal, slice, normal);
        }

        void Fill(Texture2DArray array, int slice, Color color)
        {
            var pixels = new Color[Size * Size];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            array.SetPixels(pixels, slice, 0);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            DestroyObject(_albedo);
            DestroyObject(_normal);
        }

        /// <summary>Destroys a runtime object the way the editor allows outside play mode.</summary>
        static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(target);
            else
                UnityEngine.Object.DestroyImmediate(target);
        }

    }
}
