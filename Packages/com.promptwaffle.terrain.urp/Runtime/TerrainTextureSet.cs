using System;
using PromptWaffle.Terrain;
using UnityEngine;

namespace PromptWaffle.Terrain.Rendering
{
    /// <summary>
    /// The textures the terrain shader samples, one entry per material, plus an optional "cut"
    /// variant used on faces steep enough to be a fresh cut.
    ///
    /// The game fills it in, by hand or with its own tile generator. The asset points at the files,
    /// not at what is in them, so repainting a tile under the same name needs no code change.
    /// </summary>
    [CreateAssetMenu(menuName = "PromptWaffle/Terrain Texture Set", fileName = "TerrainTextures")]
    public sealed class TerrainTextureSet : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("The material id this is the look of: 1 Bedrock, 2 Granite, 3 Rock, 4 Clay, 5 Dirt, 6 Sand, 7 Topsoil, 8 Loose rock, 9 Loose dirt, 10/11 Coal, 12/13 Iron ore, 14/15 Copper ore, 16/17 Limestone (odd = loose).")]
            public int MaterialId;

            public string Name;

            public Texture2D Albedo;

            public Texture2D Normal;

            [Tooltip("Optional: how the material looks where a cut has just exposed it.")]
            public Texture2D CutAlbedo;

            public Texture2D CutNormal;

            [Tooltip("Optional: a second look of the same ground (lush grass), blended in in large patches.")]
            public Texture2D AltAlbedo;

            public Texture2D AltNormal;

            [Tooltip("Optional: a third look (dry grass), blended in where the land is high and in its own patches.")]
            public Texture2D Alt2Albedo;

            public Texture2D Alt2Normal;

            [Range(0f, 1f)]
            [Tooltip("0 is matt (rock, soil), higher is softer and shinier (wet clay, sand sheen).")]
            public float Smoothness = 0.05f;
        }

        [SerializeField] Entry[] _entries = Array.Empty<Entry>();

        [SerializeField, Min(0f), Tooltip("Metres one tile covers, for this set; 0 leaves the terrain view's own setting. The painted set's tiles are metres across, the procedural set's half a metre.")]
        float _tileMetres;

        [SerializeField, Min(0f), Tooltip("Metres one tile of the normal covers; 0 follows the albedo's tile.")]
        float _detailTileMetres;

        public Entry[] Entries => _entries;

        /// <summary>Metres per albedo tile this set is painted for, or 0 to leave it to the view.</summary>
        public float TileMetres => _tileMetres;

        /// <summary>Metres per normal tile, or 0 to use <see cref="TileMetres"/>.</summary>
        public float DetailTileMetres => _detailTileMetres;

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

        /// <summary>Sets the tile sizes. Used by the painted-tile importer.</summary>
        public void SetTileMetres(float albedo, float detail)
        {
            _tileMetres = albedo;
            _detailTileMetres = detail;
        }
#endif
    }
}
