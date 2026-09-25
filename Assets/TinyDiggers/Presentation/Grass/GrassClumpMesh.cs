using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// One clump of grass blades, one unit tall, standing on its origin: the mesh every GPU grass
    /// instance draws (2026-09-24). Each blade is a thin tapered strip — two quads and a tip — leaning
    /// a little outward, so a clump reads as a tuft of separate blades rather than a card. uv.y is the
    /// height up the blade (0 root, 1 tip: the wind weight and the colour ramp), uv.x a per-blade
    /// random for the shader.
    /// </summary>
    public static class GrassClumpMesh
    {
        /// <summary>Triangles in one blade: two quads and the tip.</summary>
        public const int TrianglesPerBlade = 5;

        public static Mesh Build(int blades = 5, float spread = 0.45f, float width = 0.07f, int seed = 11)
        {
            var random = new System.Random(seed);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (var b = 0; b < blades; b++)
            {
                // Where the blade stands in the clump, which way it faces, and how it leans.
                var angle = (float)random.NextDouble() * Mathf.PI * 2f;
                var radius = Mathf.Sqrt((float)random.NextDouble()) * spread * 0.5f;
                var root = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                var facing = (float)random.NextDouble() * Mathf.PI;
                var side = new Vector3(Mathf.Cos(facing), 0f, Mathf.Sin(facing));
                var lean = new Vector3(root.x, 0f, root.z).normalized * (0.15f + 0.25f * (float)random.NextDouble());
                var height = 0.7f + 0.3f * (float)random.NextDouble();
                var random01 = (float)random.NextDouble();
                var normal = Vector3.Cross(side, Vector3.up).normalized;

                var first = vertices.Count;
                var rows = new[] { 0f, 0.4f, 0.75f };
                foreach (var t in rows)
                {
                    var centre = root + Vector3.up * (t * height) + lean * (t * t * height);
                    var halfWidth = width * 0.5f * (1f - t * 0.7f);
                    vertices.Add(centre - side * halfWidth);
                    vertices.Add(centre + side * halfWidth);
                    normals.Add(normal);
                    normals.Add(normal);
                    uvs.Add(new Vector2(random01, t));
                    uvs.Add(new Vector2(random01, t));
                }

                vertices.Add(root + Vector3.up * height + lean * height);
                normals.Add(normal);
                uvs.Add(new Vector2(random01, 1f));

                for (var r = 0; r < rows.Length - 1; r++)
                {
                    var a = first + r * 2;
                    triangles.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
                }

                var lastRow = first + (rows.Length - 1) * 2;
                triangles.AddRange(new[] { lastRow, lastRow + 2, lastRow + 1 });
            }

            var mesh = new Mesh { name = "Grass Clump" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
