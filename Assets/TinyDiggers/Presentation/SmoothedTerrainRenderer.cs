using System;
using System.Collections.Generic;
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
    /// layer lookup is skipped. Those arrays live in a <see cref="Workspace"/>, one per worker
    /// thread, so a big rebuild can build several chunks at once.
    /// </summary>
    public sealed class SmoothedTerrainRenderer : ChunkedTerrainRenderer
    {
        /// <summary>Cells beyond the chunk on each side that the build reads: corner normals need two.</summary>
        const int Halo = 2;

        readonly List<Workspace> _workspaces = new List<Workspace>();

        /// <summary>
        /// Where the ground is drawn while it is still catching up with where it is, or null to
        /// draw it where it is. Set by whatever owns the renderer; read by every chunk build, so
        /// it must not be changed while one is running.
        /// </summary>
        public TerrainHeightLag Lag { get; set; }

        public SmoothedTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize = DefaultChunkSize, TerrainLod lod = null)
            : base(grid, parent, material, chunkSize, lod)
        {
            _workspaces.Add(new Workspace(this));
            Rebuild();
        }

        protected override int ParallelWorkers => Math.Max(1, Math.Min(Environment.ProcessorCount, 16));

        protected override int LodLevels => MaxLodLevels;

        protected override void BuildChunkLevel(int worker, int level, int originX, int originZ, int width, int depth, TerrainMeshBuilder builder) =>
            _workspaces[worker].BuildCoarse(level, originX, originZ, width, depth, builder);

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

        protected override void BuildChunk(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder) =>
            _workspaces[0].Build(originX, originZ, width, depth, builder);

        protected override void BuildChunk(int worker, int originX, int originZ, int width, int depth, TerrainMeshBuilder builder) =>
            _workspaces[worker].Build(originX, originZ, width, depth, builder);

        protected override void BeforeParallelBuild(int workers)
        {
            while (_workspaces.Count < workers)
                _workspaces.Add(new Workspace(this));
        }

        /// <summary>The scratch one chunk build needs: never shared between two threads.</summary>
        sealed class Workspace
        {
            readonly SmoothedTerrainRenderer _renderer;
            readonly TerrainGrid _grid;
            readonly float[] _cellHeights;
            readonly MaterialId[] _cellTops;
            readonly bool[] _cellInWorld;
            readonly int _cellRow;

            readonly float[] _cornerHeights;
            readonly Vector3[] _cornerNormals;
            readonly int _cornerRow;

            int _originX;
            int _originZ;

            public Workspace(SmoothedTerrainRenderer renderer)
            {
                _renderer = renderer;
                _grid = renderer.Grid;
                var chunkSize = renderer.ChunkSize;
                _cellRow = chunkSize + 2 * Halo;
                _cellHeights = new float[_cellRow * _cellRow];
                _cellTops = new MaterialId[_cellRow * _cellRow];
                _cellInWorld = new bool[_cellRow * _cellRow];

                // Corner heights one ring beyond the chunk, so edge normals can see across the border.
                _cornerRow = chunkSize + 3;
                _cornerHeights = new float[_cornerRow * _cornerRow];
                _cornerNormals = new Vector3[(chunkSize + 1) * (chunkSize + 1)];
            }

            public void Build(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder)
            {
                var palette = _renderer.Palette;
                _originX = originX;
                _originZ = originZ;
                CacheCells(width, depth);

                // Corner heights for the chunk plus one ring beyond it, clamped to the world. Only
                // in-world corners are ever read back.
                for (var j = -1; j <= depth + 1; j++)
                {
                    var cornerZ = Math.Min(Math.Max(originZ + j, 0), _grid.Height);
                    for (var i = -1; i <= width + 1; i++)
                    {
                        var cornerX = Math.Min(Math.Max(originX + i, 0), _grid.Width);
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
                        if (_grid.IsVoid(x, z))
                            continue;
                        var h00 = Corner(x, z);
                        var h10 = Corner(x + 1, z);
                        var h01 = Corner(x, z + 1);
                        var h11 = Corner(x + 1, z + 1);

                        var topMaterial = CellTop(x, z);
                        var exposed = palette[ExposedMaterial(x, z, topMaterial, (h00 + h10 + h01 + h11) * 0.25f).Value];

                        builder.AddQuad(
                            new Vector3(i, h00, j),
                            new Vector3(i, h01, j + 1),
                            new Vector3(i + 1, h11, j + 1),
                            new Vector3(i + 1, h10, j),
                            _cornerNormals[j * normalsPerRow + i],
                            _cornerNormals[(j + 1) * normalsPerRow + i],
                            _cornerNormals[(j + 1) * normalsPerRow + i + 1],
                            _cornerNormals[j * normalsPerRow + i + 1],
                            palette[topMaterial.Value],
                            exposed,
                            new Vector4(
                                DiffersFrom(topMaterial, x - 1, z),
                                DiffersFrom(topMaterial, x + 1, z),
                                DiffersFrom(topMaterial, x, z - 1),
                                DiffersFrom(topMaterial, x, z + 1)));
                    }
                }
            }

            float[] _coarseHeights = Array.Empty<float>();
            Vector3[] _coarseNormals = Array.Empty<Vector3>();

            /// <summary>
            /// Level <paramref name="level"/>: one quad per s = 2^level cells square. The corners are
            /// the grid's own corner heights every s corners, so a coarse chunk meets the land in
            /// the same places a fine one does, only with less between. Each quad is coloured by
            /// the cell in its middle, and its steep-face colour is what lies a metre under that
            /// cell. A skirt hangs from every edge, deeper for coarser levels, to cover the cracks
            /// where a coarse chunk's straight edge meets a finer neighbour's.
            /// </summary>
            public void BuildCoarse(int level, int originX, int originZ, int width, int depth, TerrainMeshBuilder builder)
            {
                var palette = _renderer.Palette;
                var s = 1 << level;
                var nx = (width + s - 1) / s;
                var nz = (depth + s - 1) / s;
                var row = nx + 1;
                if (_coarseHeights.Length < row * (nz + 1))
                {
                    _coarseHeights = new float[row * (nz + 1)];
                    _coarseNormals = new Vector3[row * (nz + 1)];
                }

                for (var b = 0; b <= nz; b++)
                {
                    for (var a = 0; a <= nx; a++)
                    {
                        var cx = originX + Math.Min(a * s, width);
                        var cz = originZ + Math.Min(b * s, depth);
                        var here = CornerOrNaN(cx, cz);
                        _coarseHeights[b * row + a] = here;
                        _coarseNormals[b * row + a] = CoarseNormal(cx, cz, s, here);
                    }
                }

                var drop = 0.75f + 1.5f * s * _grid.CellSize;
                for (var b = 0; b < nz; b++)
                {
                    for (var a = 0; a < nx; a++)
                    {
                        var x0 = a * s;
                        var z0 = b * s;
                        var x1 = Math.Min(x0 + s, width);
                        var z1 = Math.Min(z0 + s, depth);
                        var h00 = _coarseHeights[b * row + a];
                        var h10 = _coarseHeights[b * row + a + 1];
                        var h01 = _coarseHeights[(b + 1) * row + a];
                        var h11 = _coarseHeights[(b + 1) * row + a + 1];
                        if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11))
                            continue;
                        var cellX = originX + Math.Min(x0 + s / 2, width - 1);
                        var cellZ = originZ + Math.Min(z0 + s / 2, depth - 1);
                        if (_grid.IsVoid(cellX, cellZ))
                            continue;

                        var top = palette[_grid.GetTopMaterial(cellX, cellZ).Value];
                        var exposed = palette[ExposedBelow(cellX, cellZ).Value];
                        builder.AddQuad(
                            new Vector3(x0, h00, z0),
                            new Vector3(x0, h01, z1),
                            new Vector3(x1, h11, z1),
                            new Vector3(x1, h10, z0),
                            _coarseNormals[b * row + a],
                            _coarseNormals[(b + 1) * row + a],
                            _coarseNormals[(b + 1) * row + a + 1],
                            _coarseNormals[b * row + a + 1],
                            top,
                            exposed,
                            Vector4.zero);

                        // Skirts on the chunk's own borders only.
                        if (b == 0)
                            Skirt(builder, new Vector3(x0, h00, z0), new Vector3(x1, h10, z0), drop, top, exposed);
                        if (b == nz - 1)
                            Skirt(builder, new Vector3(x0, h01, z1), new Vector3(x1, h11, z1), drop, top, exposed);
                        if (a == 0)
                            Skirt(builder, new Vector3(x0, h00, z0), new Vector3(x0, h01, z1), drop, top, exposed);
                        if (a == nx - 1)
                            Skirt(builder, new Vector3(x1, h10, z0), new Vector3(x1, h11, z1), drop, top, exposed);
                    }
                }
            }

            /// <summary>A strip hanging <paramref name="drop"/> metres under the edge from p to q, both faces.</summary>
            static void Skirt(TerrainMeshBuilder builder, Vector3 p, Vector3 q, float drop, Color32 colour, Color32 exposed)
            {
                var pDown = p + Vector3.down * drop;
                var qDown = q + Vector3.down * drop;
                builder.AddQuad(p, q, qDown, pDown, Vector3.up, colour, exposed);
                builder.AddQuad(p, pDown, qDown, q, Vector3.up, colour, exposed);
            }

            /// <summary>Average height of the in-world cells round a grid corner, or NaN where there are none.</summary>
            float CornerOrNaN(int cornerX, int cornerZ)
            {
                var sum = 0f;
                var count = 0;
                for (var z = cornerZ - 1; z <= cornerZ; z++)
                {
                    for (var x = cornerX - 1; x <= cornerX; x++)
                    {
                        if (!_grid.IsGround(x, z))
                            continue;
                        sum += _grid.GetSurfaceHeight(x, z);
                        count++;
                    }
                }

                return count == 0 ? float.NaN : sum / count;
            }

            /// <summary>The slope at a coarse corner from the corners s either side, in the same units as level 0.</summary>
            Vector3 CoarseNormal(int cornerX, int cornerZ, int s, float here)
            {
                var west = Math.Max(cornerX - s, 0);
                var east = Math.Min(cornerX + s, _grid.Width);
                var south = Math.Max(cornerZ - s, 0);
                var north = Math.Min(cornerZ + s, _grid.Height);
                var hw = CornerOrNaN(west, cornerZ);
                var he = CornerOrNaN(east, cornerZ);
                var hs = CornerOrNaN(cornerX, south);
                var hn = CornerOrNaN(cornerX, north);
                if (float.IsNaN(here))
                    return Vector3.up;
                if (float.IsNaN(hw)) { hw = here; west = cornerX; }
                if (float.IsNaN(he)) { he = here; east = cornerX; }
                if (float.IsNaN(hs)) { hs = here; south = cornerZ; }
                if (float.IsNaN(hn)) { hn = here; north = cornerZ; }
                var slopeX = east > west ? (he - hw) / (east - west) : 0f;
                var slopeZ = north > south ? (hn - hs) / (north - south) : 0f;
                return new Vector3(-slopeX, 1f, -slopeZ).normalized;
            }

            /// <summary>What lies a metre under the cell's surface: the colour a steep coarse face shows.</summary>
            MaterialId ExposedBelow(int x, int z)
            {
                var top = _grid.GetTopMaterial(x, z);
                if (_grid.GetLayerCount(x, z) == 0)
                    return top;
                var below = _grid.GetMaterialAt(x, z, _grid.GetSurfaceHeight(x, z) - 1f);
                return below.Value == 0 ? top : below;
            }

            /// <summary>Copies heights and top materials of the chunk plus <see cref="Halo"/> cells around it.</summary>
            void CacheCells(int width, int depth)
            {
                var heights = _grid.SurfaceHeights;
                var tops = _grid.TopMaterials;
                var gridWidth = _grid.Width;
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
                        var inWorld = (uint)x < (uint)gridWidth && (uint)z < (uint)_grid.Height && !_grid.IsVoid(x, z);
                        _cellInWorld[at] = inWorld;
                        if (inWorld)
                        {
                            // The height it is *drawn* at, which is the height it is except for the
                            // handful of cells still easing after a cut or a tip. Taking it here
                            // means corners, normals and edge lines all follow from one number and
                            // stay consistent with each other.
                            var cell = z * gridWidth + x;
                            var height = heights[cell];
                            var lag = _renderer.Lag;
                            _cellHeights[at] = lag == null ? height : lag.Drawn(cell, height);
                            _cellTops[at] = tops[cell];
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
                var east = Math.Min(cornerX + 1, _grid.Width);
                var south = Math.Max(cornerZ - 1, 0);
                var north = Math.Min(cornerZ + 1, _grid.Height);
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

                return _grid.GetMaterialAt(tallestX, tallestZ, height);
            }
        }
    }
}
