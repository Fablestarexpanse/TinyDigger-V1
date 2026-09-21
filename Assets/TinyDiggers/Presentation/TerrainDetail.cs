using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The material the terrain chunks are drawn with when a texture set is supplied: the texture
    /// arrays, the per-cell material map and the tuning, held together so the view can hand one
    /// material to the renderer and forget about it.
    ///
    /// Every chunk shares this one material, so the whole map is still one draw call per chunk
    /// however many materials are showing.
    /// </summary>
    public sealed class TerrainDetail : IDisposable
    {
        public const string ShaderName = "TinyDiggers/Terrain Triplanar";

        static readonly int CellMapId = Shader.PropertyToID("_CellMap");
        static readonly int AlbedosId = Shader.PropertyToID("_Albedos");
        static readonly int NormalsId = Shader.PropertyToID("_Normals");
        static readonly int MaterialParamsId = Shader.PropertyToID("_MaterialParams");
        static readonly int TerrainOriginId = Shader.PropertyToID("_TerrainOrigin");
        static readonly int MapSizeId = Shader.PropertyToID("_MapSize");
        static readonly int AlbedoRepeatId = Shader.PropertyToID("_AlbedoRepeat");
        static readonly int DetailRepeatId = Shader.PropertyToID("_DetailRepeat");
        static readonly int DetailStrengthId = Shader.PropertyToID("_DetailStrength");
        static readonly int MottleRepeatId = Shader.PropertyToID("_MottleRepeat");
        static readonly int MottleStrengthId = Shader.PropertyToID("_MottleStrength");
        static readonly int BlendWidthId = Shader.PropertyToID("_BlendWidth");

        readonly TerrainMaterialAtlas _atlas;
        readonly TerrainCellMap _cellMap;
        readonly Material _material;
        bool _disposed;

        readonly float _cellSize;

        public TerrainDetail(TerrainGrid grid, TerrainTextureSet set, Vector2 originWS)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            _cellSize = grid.CellSize;

            var shader = Shader.Find(ShaderName);
            if (shader == null)
                throw new InvalidOperationException($"Shader '{ShaderName}' is missing.");

            _atlas = new TerrainMaterialAtlas(grid.Materials, set);
            _cellMap = new TerrainCellMap(grid);
            _material = new Material(shader) { name = "Terrain Detail", hideFlags = HideFlags.DontSave };
            _material.SetTexture(CellMapId, _cellMap.Texture);
            _material.SetTexture(AlbedosId, _atlas.Albedo);
            _material.SetTexture(NormalsId, _atlas.Normal);
            _material.SetVectorArray(MaterialParamsId, _atlas.Parameters);
            _material.SetVector(TerrainOriginId, new Vector4(originWS.x, originWS.y, 1f / grid.CellSize, 0f));
            _material.SetVector(MapSizeId, new Vector4(grid.Width, grid.Height, 1f / grid.Width, 1f / grid.Height));
        }

        public Material Material => _material;

        public TerrainCellMap CellMap => _cellMap;

        public TerrainMaterialAtlas Atlas => _atlas;

        /// <summary>Metres one tile of the albedo covers.</summary>
        public void SetTuning(float albedoRepeat, float detailRepeat, float detailStrength, float mottleRepeat, float mottleStrength, float blendWidth)
        {
            _material.SetFloat(AlbedoRepeatId, albedoRepeat);
            _material.SetFloat(DetailRepeatId, detailRepeat);
            _material.SetFloat(DetailStrengthId, detailStrength);
            _material.SetFloat(MottleRepeatId, mottleRepeat);
            _material.SetFloat(MottleStrengthId, mottleStrength);
            // Tuned in metres; the shader blends in cells.
            _material.SetFloat(BlendWidthId, blendWidth / _cellSize);
        }

        /// <summary>Sends any cells changed since the last frame. Does nothing when nothing changed.</summary>
        public void Flush() => _cellMap.Flush();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cellMap?.Dispose();
            _atlas?.Dispose();
            if (_material != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_material);
                else
                    UnityEngine.Object.DestroyImmediate(_material);
            }
        }
    }
}
