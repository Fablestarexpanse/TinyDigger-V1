using System;
using TinyDiggers.Terrain;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// The default terrain look, after Captain of Industry: a displaced heightfield rather than
    /// blocks. Heights live at cell corners, each the average surface height of the up to four
    /// cells that touch it, so the surface is continuous and height steps become steep faces.
    /// With surfaces on a 1 m step the result reads as terraces.
    ///
    /// Normals are smooth, one per corner from the slope of the corner heights around it, so light
    /// rolls across the land instead of faceting per cell (TERRAIN_REFERENCE.md section 1.4). The
    /// slope is sampled from the grid, not the chunk, so a corner on a chunk border gets exactly
    /// the normal it would have in an unchunked mesh and no seam shows.
    ///
    /// Each cell is still its own quad with its own four vertices, so colours stay per cell:
    /// - the vertex colour is the cell's top material, which is what gentle ground shows;
    /// - the exposed colour (UV1) is the layer a steep face cuts through. The shader blends to it
    ///   past about 40 degrees, so a cut through topsoil shows the dirt or rock underneath.
    ///   It is read from the tallest column in the cell's 3x3 neighbourhood, the one the face is
    ///   cut into, at the height of the quad's centre;
    /// - edge flags (UV2) mark sides where the neighbour's top material differs, for a faint line.
    /// </summary>
    public sealed class SmoothedTerrainRenderer : ChunkedTerrainRenderer
    {
        readonly float[] _cornerHeights;
        readonly Vector3[] _cornerNormals;
        readonly int _cornerRow;

        int _originX;
        int _originZ;

        public SmoothedTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize = DefaultChunkSize)
            : base(grid, parent, material, chunkSize)
        {
            // Corner heights one ring beyond the chunk, so edge normals can see across the border.
            _cornerRow = chunkSize + 3;
            _cornerHeights = new float[_cornerRow * _cornerRow];
            _cornerNormals = new Vector3[(chunkSize + 1) * (chunkSize + 1)];
            Rebuild();
        }

        /// <summary>
        /// The smooth normal at grid corner (cornerX, cornerZ), from central differences of the
        /// corner heights, one-sided at the world edge. Public so tests can compare against it.
        /// </summary>
        public static Vector3 CornerNormal(TerrainGrid grid, int cornerX, int cornerZ)
        {
            var west = Math.Max(cornerX - 1, 0);
            var east = Math.Min(cornerX + 1, grid.Width);
            var south = Math.Max(cornerZ - 1, 0);
            var north = Math.Min(cornerZ + 1, grid.Height);
            var slopeX = (CornerHeight(grid, east, cornerZ) - CornerHeight(grid, west, cornerZ)) / (east - west);
            var slopeZ = (CornerHeight(grid, cornerX, north) - CornerHeight(grid, cornerX, south)) / (north - south);
            return new Vector3(-slopeX, 1f, -slopeZ).normalized;
        }

        /// <summary>Average surface height of the up to four cells that touch grid corner (cornerX, cornerZ).</summary>
        public static float CornerHeight(TerrainGrid grid, int cornerX, int cornerZ)
        {
            var sum = 0f;
            var count = 0;
            for (var z = cornerZ - 1; z <= cornerZ; z++)
            {
                for (var x = cornerX - 1; x <= cornerX; x++)
                {
                    if (!grid.InBounds(x, z))
                        continue;
                    sum += grid.GetSurfaceHeight(x, z);
                    count++;
                }
            }

            return sum / count;
        }

        public override void MarkDirty(int x, int z)
        {
            // A cell sets the four corners around it (shared with its 3x3 neighbourhood), and each
            // corner normal reads the corners one step further out, so the normals of cells up to
            // two away change too. Its layers and top material also feed its neighbours' exposed
            // colours and edge lines. Any of these cells may sit in another chunk.
            for (var dz = -2; dz <= 2; dz++)
                for (var dx = -2; dx <= 2; dx++)
                    MarkCellsChunkDirty(x + dx, z + dz);
        }

        protected override void BuildChunk(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder)
        {
            _originX = originX;
            _originZ = originZ;

            // Corner heights for the chunk plus one ring beyond it, clamped to the world. Only
            // in-world corners are ever read back.
            for (var j = -1; j <= depth + 1; j++)
            {
                var cornerZ = Math.Min(Math.Max(originZ + j, 0), Grid.Height);
                for (var i = -1; i <= width + 1; i++)
                {
                    var cornerX = Math.Min(Math.Max(originX + i, 0), Grid.Width);
                    _cornerHeights[(j + 1) * _cornerRow + i + 1] = CornerHeight(Grid, cornerX, cornerZ);
                }
            }

            var normalsPerRow = width + 1;
            for (var j = 0; j <= depth; j++)
                for (var i = 0; i <= width; i++)
                    _cornerNormals[j * normalsPerRow + i] = CachedCornerNormal(originX + i, originZ + j);

            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var h00 = Corner(originX + i, originZ + j);
                    var h10 = Corner(originX + i + 1, originZ + j);
                    var h01 = Corner(originX + i, originZ + j + 1);
                    var h11 = Corner(originX + i + 1, originZ + j + 1);

                    var x = originX + i;
                    var z = originZ + j;
                    var topMaterial = Grid.GetTopMaterial(x, z);
                    var exposed = Palette[ExposedMaterial(x, z, (h00 + h10 + h01 + h11) * 0.25f).Value];

                    builder.AddQuad(
                        new Vector3(i, h00, j),
                        new Vector3(i, h01, j + 1),
                        new Vector3(i + 1, h11, j + 1),
                        new Vector3(i + 1, h10, j),
                        _cornerNormals[j * normalsPerRow + i],
                        _cornerNormals[(j + 1) * normalsPerRow + i],
                        _cornerNormals[(j + 1) * normalsPerRow + i + 1],
                        _cornerNormals[j * normalsPerRow + i + 1],
                        Palette[topMaterial.Value],
                        exposed,
                        new Vector4(
                            DiffersFrom(topMaterial, x - 1, z),
                            DiffersFrom(topMaterial, x + 1, z),
                            DiffersFrom(topMaterial, x, z - 1),
                            DiffersFrom(topMaterial, x, z + 1)));
                }
            }
        }

        /// <summary>Cached height of in-world grid corner (cornerX, cornerZ) near the chunk being built.</summary>
        float Corner(int cornerX, int cornerZ) =>
            _cornerHeights[(cornerZ - _originZ + 1) * _cornerRow + cornerX - _originX + 1];

        /// <summary>Same maths as <see cref="CornerNormal"/>, reading the cached corner heights.</summary>
        Vector3 CachedCornerNormal(int cornerX, int cornerZ)
        {
            var west = Math.Max(cornerX - 1, 0);
            var east = Math.Min(cornerX + 1, Grid.Width);
            var south = Math.Max(cornerZ - 1, 0);
            var north = Math.Min(cornerZ + 1, Grid.Height);
            var slopeX = (Corner(east, cornerZ) - Corner(west, cornerZ)) / (east - west);
            var slopeZ = (Corner(cornerX, north) - Corner(cornerX, south)) / (north - south);
            return new Vector3(-slopeX, 1f, -slopeZ).normalized;
        }

        /// <summary>1 if cell (x, z) exists and its top material is not <paramref name="material"/>.</summary>
        float DiffersFrom(MaterialId material, int x, int z) =>
            Grid.InBounds(x, z) && Grid.GetTopMaterial(x, z) != material ? 1f : 0f;

        /// <summary>The layer a steep face over cell (x, z) cuts through at <paramref name="height"/>.</summary>
        MaterialId ExposedMaterial(int x, int z, float height)
        {
            var tallestX = x;
            var tallestZ = z;
            var tallest = Grid.GetSurfaceHeight(x, z);
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!Grid.InBounds(x + dx, z + dz))
                        continue;
                    var h = Grid.GetSurfaceHeight(x + dx, z + dz);
                    if (h > tallest)
                    {
                        tallest = h;
                        tallestX = x + dx;
                        tallestZ = z + dz;
                    }
                }
            }

            return Grid.GetMaterialAt(tallestX, tallestZ, height);
        }
    }
}
