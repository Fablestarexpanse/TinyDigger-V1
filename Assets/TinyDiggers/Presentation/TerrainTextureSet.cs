using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The textures the terrain shader samples, one entry per material, plus an optional "cut"
    /// variant used on faces steep enough to be a fresh cut.
    ///
    /// The asset is filled in by <c>TinyDiggers > Generate Placeholder Terrain Textures</c>, which
    /// writes procedural PNGs and links them here. Dropping real PNGs in over them, with the same
    /// names, needs no code change: the asset points at the files, not at what is in them.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Terrain Texture Set", fileName = "TerrainTextures")]
    public sealed class TerrainTextureSet : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("The material id this is the look of: 1 Bedrock, 2 Granite, 3 Rock, 4 Clay, 5 Dirt, 6 Sand, 7 Topsoil, 8 Loose rock, 9 Loose dirt.")]
            public int MaterialId;

            public string Name;

            public Texture2D Albedo;

            public Texture2D Normal;

            [Tooltip("Optional: how the material looks where a cut has just exposed it.")]
            public Texture2D CutAlbedo;

            public Texture2D CutNormal;

            [Range(0f, 1f)]
            [Tooltip("0 is matt (rock, soil), higher is softer and shinier (wet clay, sand sheen).")]
            public float Smoothness = 0.05f;
        }

        [SerializeField] Entry[] _entries = Array.Empty<Entry>();

        public Entry[] Entries => _entries;

        /// <summary>The entry for a material id, or null.</summary>
        public Entry Find(MaterialId id)
        {
            foreach (var entry in _entries)
                if (entry != null && entry.MaterialId == id.Value)
                    return entry;
            return null;
        }

#if UNITY_EDITOR
        /// <summary>Replaces the entries. Used by the generator.</summary>
        public void SetEntries(Entry[] entries)
        {
            _entries = entries ?? Array.Empty<Entry>();
        }
#endif
    }
}
