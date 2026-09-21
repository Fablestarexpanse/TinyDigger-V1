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
    ///
    /// Building a chunk first copies the heights and top materials of the chunk plus a two-cell
    /// halo into local arrays, then works only from those, so the per-quad loop pays no bounds
    /// checks or grid lookups. On flat ground the exposed layer is the top material and the
    /// layer lookup is skipped.
    /// </summary>
    public sealed class SmoothedTerrainRenderer : ChunkedTerrainRenderer
    {
        /// <summary>Cells beyond the chunk on each side that the build reads: corner normals need two.</summary>
        const int Halo = 2;

        readonly float[] _cellHeights;
        readonly MaterialId[] _cellTops;
        readonly bool[] _cellInWorld;
        readonly int _cellRow;

        readonly float[] _cornerHeights;
        readonly Vector3[] _cornerNormals;
        readonly int _cornerRow;

        int _originX;
        int _originZ;

        public SmoothedTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize = DefaultChunkSize)
            : base(grid, parent, material, chunkSize)
        {
            _cellRow = chunkSize + 2 * Halo;
            _cellHeights = new float[_cellRow * _cellRow];
            _cellTops = new MaterialId[_cellRow * _cellRow];
            _cellInWorld = new bool[_cellRow * _cellRow];

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
        public static float CornerHeight(TerrainGrid grid, int cornerX, int cornerZ) =>
            TerrainSurface.CornerHeight(grid, cornerX, cornerZ);

        protected override void MarkChunksAffectedBy(int x, int z)
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
            CacheCells(width, depth);

            // Corner heights for the chunk plus one ring beyond it, clamped to the world. Only
            // in-world corners are ever read back.
            for (var j = -1; j <= depth + 1; j++)
            {
                var cornerZ = Math.Min(Math.Max(originZ + j, 0), Grid.Height);
                for (var i = -1; i <= width + 1; i++)
                {
                    var cornerX = Math.Min(Math.Max(originX + i, 0), Grid.Width);
                    _cornerHeights[(j + 1) * _cornerRow + i + 1] = CachedCornerHeight(cornerX, cornerZ);
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
                    var x = originX + i;
                    var z = originZ + j;
                    if (Grid.IsVoid(x, z))
                        continue;
                    var h00 = Corner(x, z);
                    var h10 = Corner(x + 1, z);
                    var h01 = Corner(x, z + 1);
                    var h11 = Corner(x + 1, z + 1);

                    var topMaterial = CellTop(x, z);
                    var exposed = Palette[ExposedMaterial(x, z, topMaterial, (h00 + h10 + h01 + h11) * 0.25f).Value];

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

        /// <summary>Copies heights and top materials of the chunk plus <see cref="Halo"/> cells around it.</summary>
        void CacheCells(int width, int depth)
        {
            var heights = Grid.SurfaceHeights;
            var tops = Grid.TopMaterials;
            var gridWidth = Grid.Width;
            for (var j = -Halo; j < depth + Halo; j++)
            {
                var z = _originZ + j;
                var row = (j + Halo) * _cellRow;
                for (var i = -Halo; i < width + Halo; i++)
                {
                    var x = _originX + i;
                    var at = row + i + Halo;
                    // Void cells are off the map, as in TerrainSurface.CornerHeight: counting them
                    // dragged every rim corner down toward the datum, a comb of blades under the
                    // disc edge that only the plinth used to hide.
                    var inWorld = (uint)x < (uint)gridWidth && (uint)z < (uint)Grid.Height && !Grid.IsVoid(x, z);
                    _cellInWorld[at] = inWorld;
                    if (inWorld)
                    {
                        _cellHeights[at] = heights[z * gridWidth + x];
                        _cellTops[at] = tops[z * gridWidth + x];
                    }
                }
            }
        }

        int CellIndex(int x, int z) => (z - _originZ + Halo) * _cellRow + x - _originX + Halo;

        MaterialId CellTop(int x, int z) => _cellTops[CellIndex(x, z)];

        /// <summary>Same sum, in the same order, as <see cref="CornerHeight"/>, so results match bit for bit.</summary>
        float CachedCornerHeight(int cornerX, int cornerZ)
        {
            var sum = 0f;
            var count = 0;
            for (var z = cornerZ - 1; z <= cornerZ; z++)
            {
                for (var x = cornerX - 1; x <= cornerX; x++)
                {
                    var at = CellIndex(x, z);
                    if (!_cellInWorld[at])
                        continue;
                    sum += _cellHeights[at];
                    count++;
                }
            }

            return sum / count;
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
        float DiffersFrom(MaterialId material, int x, int z)
        {
            var at = CellIndex(x, z);
            return _cellInWorld[at] && _cellTops[at] != material ? 1f : 0f;
        }

        /// <summary>The layer a steep face over cell (x, z) cuts through at <paramref name="height"/>.</summary>
        MaterialId ExposedMaterial(int x, int z, MaterialId top, float height)
        {
            var tallestX = x;
            var tallestZ = z;
            var own = _cellHeights[CellIndex(x, z)];
            var tallest = own;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var at = CellIndex(x + dx, z + dz);
                    if (!_cellInWorld[at])
                        continue;
                    var h = _cellHeights[at];
                    if (h > tallest)
                    {
                        tallest = h;
                        tallestX = x + dx;
                        tallestZ = z + dz;
                    }
                }
            }

            // The cell itself is the tallest and the face is not below its surface: nothing is cut
            // into, and the answer is its own top. This is every cell on flat ground.
            if (tallestX == x && tallestZ == z && height >= own)
                return top;

            return Grid.GetMaterialAt(tallestX, tallestZ, height);
        }
    }
}
