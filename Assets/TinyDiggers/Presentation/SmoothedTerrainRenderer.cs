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
    /// Each cell is its own quad with its own four vertices and a flat normal (the low-poly look)
    /// and two colours:
    /// - the vertex colour is the cell's top material, which is what gentle ground shows;
    /// - the exposed colour (UV1) is the layer a steep face cuts through. The shader blends to it
    ///   past about 45 degrees, so a cut through topsoil shows the dirt or rock underneath.
    ///
    /// The exposed layer is read from the tallest column in the cell's 3x3 neighbourhood — the
    /// one the face is cut into — at the height of the quad's centre.
    /// </summary>
    public sealed class SmoothedTerrainRenderer : ChunkedTerrainRenderer
    {
        readonly float[] _cornerHeights;

        public SmoothedTerrainRenderer(TerrainGrid grid, Transform parent, Material material, int chunkSize = DefaultChunkSize)
            : base(grid, parent, material, chunkSize)
        {
            _cornerHeights = new float[(chunkSize + 1) * (chunkSize + 1)];
            Rebuild();
        }

        public override void MarkDirty(int x, int z)
        {
            // A cell sets the four corners around it, and those corners are shared with its eight
            // neighbours; its layers can also be the exposed face of any of those neighbours.
            // Any of them may sit in the next chunk over.
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    MarkCellsChunkDirty(x + dx, z + dz);
        }

        protected override void BuildChunk(int originX, int originZ, int width, int depth, TerrainMeshBuilder builder)
        {
            var cornersPerRow = width + 1;
            for (var j = 0; j <= depth; j++)
                for (var i = 0; i <= width; i++)
                    _cornerHeights[j * cornersPerRow + i] = CornerHeight(originX + i, originZ + j);

            for (var j = 0; j < depth; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var h00 = _cornerHeights[j * cornersPerRow + i];
                    var h10 = _cornerHeights[j * cornersPerRow + i + 1];
                    var h01 = _cornerHeights[(j + 1) * cornersPerRow + i];
                    var h11 = _cornerHeights[(j + 1) * cornersPerRow + i + 1];

                    // Cross of the two diagonals: flat per quad, whatever the corners do.
                    var rise = h11 - h00;
                    var fall = h01 - h10;
                    var normal = new Vector3(fall - rise, 2f, -(rise + fall)).normalized;

                    var x = originX + i;
                    var z = originZ + j;
                    var top = Palette[Grid.GetTopMaterial(x, z).Value];
                    var exposed = Palette[ExposedMaterial(x, z, (h00 + h10 + h01 + h11) * 0.25f).Value];

                    builder.AddQuad(
                        new Vector3(i, h00, j),
                        new Vector3(i, h01, j + 1),
                        new Vector3(i + 1, h11, j + 1),
                        new Vector3(i + 1, h10, j),
                        normal,
                        top,
                        exposed);
                }
            }
        }

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

        float CornerHeight(int cornerX, int cornerZ)
        {
            var sum = 0f;
            var count = 0;
            for (var z = cornerZ - 1; z <= cornerZ; z++)
            {
                for (var x = cornerX - 1; x <= cornerX; x++)
                {
                    if (!Grid.InBounds(x, z))
                        continue;
                    sum += Grid.GetSurfaceHeight(x, z);
                    count++;
                }
            }

            return sum / count;
        }
    }
}
