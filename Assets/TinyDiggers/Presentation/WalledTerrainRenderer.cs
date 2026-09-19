using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Alternative, not the default: draws every column exactly as stored, a flat top at its
    /// true surface height plus vertical walls wherever it stands above a neighbour or the edge of
    /// the world. Walls are banded by the layers they cut through. Useful for inspecting what the
    /// grid actually holds; the game look is <see cref="SmoothedTerrainRenderer"/>.
    /// </summary>
    public sealed class WalledTerrainRenderer : ChunkedTerrainRenderer
    {
        /// <summary>Wall bands thinner than this are not worth two triangles.</summary>
        const float MinWallHeight = 1e-4f;

        TerrainMeshBuilder _builder;

        public WalledTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize = DefaultChunkSize)
            : base(grid, parent, material, chunkSize)
        {
            Rebuild();
        }

        public override void MarkDirty(int x, int z)
        {
            // A cell draws its own top plus the walls on its +x and +z edges. Its -x and -z edges
            // are drawn by the neighbours on those sides, whose walls take their height and
            // colours from this cell — so those neighbours' chunks must rebuild too.
            MarkCellsChunkDirty(x, z);
            MarkCellsChunkDirty(x - 1, z);
            MarkCellsChunkDirty(x, z - 1);
        }

        protected override void BuildChunk(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder)
        {
            _builder = builder;
            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var x = originX + i;
                    var z = originZ + j;
                    var height = Grid.GetSurfaceHeight(x, z);
                    var topColor = Palette[Grid.GetTopMaterial(x, z).Value];

                    builder.AddQuad(
                        new Vector3(i, height, j),
                        new Vector3(i, height, j + 1),
                        new Vector3(i + 1, height, j + 1),
                        new Vector3(i + 1, height, j),
                        Vector3.up,
                        topColor,
                        topColor);

                    // The +x edge. Past the far border the world drops to zero, so the whole
                    // layer cake shows along the edge of the map.
                    var eastHeight = Grid.InBounds(x + 1, z) ? Grid.GetSurfaceHeight(x + 1, z) : 0f;
                    if (height > eastHeight)
                        AddWall(x, z, eastHeight, height, i + 1, j, i + 1, j + 1, Vector3.right);
                    else if (eastHeight > height)
                        AddWall(x + 1, z, height, eastHeight, i + 1, j + 1, i + 1, j, Vector3.left);

                    // The +z edge.
                    var northHeight = Grid.InBounds(x, z + 1) ? Grid.GetSurfaceHeight(x, z + 1) : 0f;
                    if (height > northHeight)
                        AddWall(x, z, northHeight, height, i + 1, j + 1, i, j + 1, Vector3.forward);
                    else if (northHeight > height)
                        AddWall(x, z + 1, height, northHeight, i, j + 1, i + 1, j + 1, Vector3.back);

                    // The near borders have no neighbour to own them, so the edge cell does.
                    if (x == 0)
                        AddWall(x, z, 0f, height, i, j + 1, i, j, Vector3.left);
                    if (z == 0)
                        AddWall(x, z, 0f, height, i, j, i + 1, j, Vector3.back);
                }
            }

            _builder = null;
        }

        /// <summary>
        /// Adds the face of column (<paramref name="columnX"/>, <paramref name="columnZ"/>) between
        /// heights <paramref name="low"/> and <paramref name="high"/>, one band per layer it
        /// crosses. (ax, az) and (bx, bz) are the bottom edge's ends, left then right as seen
        /// from the side the wall faces.
        /// </summary>
        void AddWall(int columnX, int columnZ, float low, float high, float ax, float az, float bx, float bz, Vector3 normal)
        {
            if (high - low <= MinWallHeight)
                return;

            var layerCount = Grid.GetLayerCount(columnX, columnZ);
            var layerBase = 0f;
            for (var k = 0; k < layerCount && layerBase < high; k++)
            {
                var layer = Grid.GetLayer(columnX, columnZ, k);
                var layerTop = layerBase + layer.Thickness;
                var bandBottom = Math.Max(layerBase, low);
                var bandTop = Math.Min(layerTop, high);
                if (bandTop - bandBottom > MinWallHeight)
                {
                    // Walls already show their layer, so the steep-face blend target is the same colour.
                    var color = Palette[layer.Material.Value];
                    _builder.AddQuad(
                        new Vector3(ax, bandBottom, az),
                        new Vector3(ax, bandTop, az),
                        new Vector3(bx, bandTop, bz),
                        new Vector3(bx, bandBottom, bz),
                        normal,
                        color,
                        color);
                }

                layerBase = layerTop;
            }
        }
    }
}
