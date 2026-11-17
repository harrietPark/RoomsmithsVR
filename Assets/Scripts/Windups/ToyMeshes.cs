using System.Collections.Generic;
using UnityEngine;

namespace Windups
{
    // Small static toy meshes (vertex-coloured, for the Windups/Toy Surface shader).
    // Front faces point toward -Z (the viewer); thickness extends toward +Z.
    public static class ToyMeshes
    {
        // Rounded rectangle slab centred on the origin.
        public static Mesh RoundedSlab(float width, float height, float radius, float thickness, Color front, Color edge, Color back, string name)
        {
            var outline = new List<Vector2>();
            radius = Mathf.Min(radius, Mathf.Min(width, height) * .5f - .0005f);
            float hx = width * .5f - radius, hy = height * .5f - radius;
            Vector2[] centers = { new Vector2(hx, hy), new Vector2(-hx, hy), new Vector2(-hx, -hy), new Vector2(hx, -hy) };
            for (int corner = 0; corner < 4; corner++)
                for (int i = 0; i <= 6; i++)
                {
                    float angle = (corner * 90f + i * 15f) * Mathf.Deg2Rad;
                    outline.Add(centers[corner] + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
                }
            return Extrude(outline, thickness, front, edge, back, name);
        }

        // Flat round token (cylinder) of the given radius.
        public static Mesh Disc(float radius, float thickness, Color front, Color edge, string name)
        {
            var outline = new List<Vector2>();
            for (int i = 0; i < 32; i++)
            {
                float angle = i / 32f * Mathf.PI * 2f;
                outline.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }
            return Extrude(outline, thickness, front, edge, edge, name);
        }

        // Speech tail whose base sits at the origin (overlapping upward) and tip points down.
        public static Mesh Tail(float halfWidth, float length, float tipRadius, float overlap, float thickness, Color front, Color edge, Color back)
        {
            var outline = new List<Vector2> { new Vector2(halfWidth, overlap), new Vector2(-halfWidth, overlap) };
            var tip = new Vector2(0f, -length + tipRadius);
            for (int i = 0; i <= 6; i++)
            {
                float angle = Mathf.Lerp(205f, 335f, i / 6f) * Mathf.Deg2Rad;
                outline.Add(tip + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * tipRadius);
            }
            return Extrude(outline, thickness, front, edge, back, "ToyTail");
        }

        // Counter-clockwise convex outline extruded into a closed slab.
        private static Mesh Extrude(List<Vector2> outline, float thickness, Color front, Color edge, Color back, string name)
        {
            int n = outline.Count;
            Vector2 centroid = Vector2.zero;
            foreach (var p in outline) centroid += p / n;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            for (int face = 0; face < 2; face++)
            {
                float z = face == 0 ? 0f : thickness;
                int start = vertices.Count;
                vertices.Add(new Vector3(centroid.x, centroid.y, z));
                foreach (var p in outline) vertices.Add(new Vector3(p.x, p.y, z));
                for (int i = 0; i <= n; i++)
                {
                    normals.Add(face == 0 ? Vector3.back : Vector3.forward);
                    colors.Add(face == 0 ? front : back);
                }
                for (int i = 0; i < n; i++)
                {
                    int a = start + 1 + i, b = start + 1 + (i + 1) % n;
                    if (face == 0) triangles.AddRange(new[] { start, b, a });
                    else triangles.AddRange(new[] { start, a, b });
                }
            }
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = outline[i], p1 = outline[(i + 1) % n];
                Vector2 mid = (p0 + p1) * .5f - centroid;
                Vector2 along = (p1 - p0).normalized;
                var outward = new Vector3(along.y, -along.x, 0f);
                if (Vector2.Dot(new Vector2(outward.x, outward.y), mid) < 0f) outward = -outward;
                int start = vertices.Count;
                vertices.Add(new Vector3(p0.x, p0.y, 0f));
                vertices.Add(new Vector3(p1.x, p1.y, 0f));
                vertices.Add(new Vector3(p0.x, p0.y, thickness));
                vertices.Add(new Vector3(p1.x, p1.y, thickness));
                for (int k = 0; k < 4; k++) { normals.Add(outward); colors.Add(edge); }
                triangles.AddRange(new[] { start, start + 1, start + 2, start + 1, start + 3, start + 2 });
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Small open basket (bowl) standing on its base, opening upward (+Y).
        public static Mesh Basket(float radius, float height, float wall, Color outside, Color inside, Color rim)
        {
            const int segments = 40;
            // Profile from the outer base up over the rim and down to the inner floor.
            var profile = new List<(Vector2 p, Vector2 n, Color c)>
            {
                (new Vector2(0f, 0f), new Vector2(0f, -1f), outside),
                (new Vector2(radius * .78f, 0f), new Vector2(0f, -1f), outside),
                (new Vector2(radius * .86f, height * .12f), new Vector2(.8f, -.6f), outside),
                (new Vector2(radius, height * .9f), new Vector2(.98f, -.2f), outside),
                (new Vector2(radius, height), new Vector2(0f, 1f), rim),
                (new Vector2(radius - wall, height), new Vector2(0f, 1f), rim),
                (new Vector2(radius - wall, height * .9f), new Vector2(-.98f, .2f), inside),
                (new Vector2(radius * .78f - wall, wall), new Vector2(0f, 1f), inside),
                (new Vector2(0f, wall), new Vector2(0f, 1f), inside)
            };
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            int rings = profile.Count, columns = segments + 1;
            for (int m = 0; m < rings; m++)
            for (int s = 0; s < columns; s++)
            {
                float a = s / (float)segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                var (p, n, c) = profile[m];
                vertices.Add(new Vector3(p.x * cos, p.y, p.x * sin));
                normals.Add(new Vector3(n.x * cos, n.y, n.x * sin).normalized);
                colors.Add(c);
            }
            for (int m = 0; m < rings - 1; m++)
            for (int s = 0; s < segments; s++)
            {
                int a = m * columns + s, b = a + 1, c = a + columns, d = c + 1;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
            var mesh = new Mesh { name = "ToyBasket" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Flat torus in the XY plane with unit radius; scale the transform to size it.
        public static Mesh Torus(float tube, string name)
        {
            const int around = 64, sides = 8;
            var vertices = new Vector3[(around + 1) * (sides + 1)];
            var normals = new Vector3[vertices.Length];
            var colors = new Color[vertices.Length];
            for (int i = 0; i <= around; i++)
            for (int j = 0; j <= sides; j++)
            {
                float u = i / (float)around * Mathf.PI * 2f, v = j / (float)sides * Mathf.PI * 2f;
                var center = new Vector3(Mathf.Cos(u), Mathf.Sin(u), 0f);
                var normal = new Vector3(Mathf.Cos(u) * Mathf.Cos(v), Mathf.Sin(u) * Mathf.Cos(v), Mathf.Sin(v));
                int index = i * (sides + 1) + j;
                vertices[index] = center + normal * tube;
                normals[index] = normal;
                colors[index] = Color.white;
            }
            var triangles = new List<int>();
            for (int i = 0; i < around; i++)
            for (int j = 0; j < sides; j++)
            {
                int a = i * (sides + 1) + j, b = a + sides + 1;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // 2026-11-13 (the intro sign's letter blocks: "they look like keycaps"): a box rounded on every edge and corner,
        // like a wooden toy block, centred on the origin. Each side has its own colour (the seams lie in the middle of
        // the rounded edges); the normals are smooth across the seams. `arc` steps per rounded edge half (5: 1014
        // vertices, 1728 triangles).
        public static Mesh RoundedBox(Vector3 size, float radius, Color front, Color side, Color top, Color back, string name, int arc = 5)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            Vector3 half = size * .5f;
            radius = Mathf.Clamp(radius, .0001f, Mathf.Min(half.x, Mathf.Min(half.y, half.z)) - .0002f);
            Vector3 inner = half - Vector3.one * radius;
            // Each side: its outward normal, two axes across it, its colour.
            var sides = new[]
            {
                (n: Vector3.back, u: Vector3.left, v: Vector3.up, c: front), (n: Vector3.forward, u: Vector3.right, v: Vector3.up, c: back),
                (n: Vector3.up, u: Vector3.right, v: Vector3.forward, c: top), (n: Vector3.down, u: Vector3.right, v: Vector3.back, c: side),
                (n: Vector3.left, u: Vector3.forward, v: Vector3.up, c: side), (n: Vector3.right, u: Vector3.back, v: Vector3.up, c: side),
            };
            foreach (var s in sides)
            {
                float depth = Vector3.Dot(Abs(s.n), half);
                float[] us = BoxSteps(Vector3.Dot(Abs(s.u), inner), radius, arc), vs = BoxSteps(Vector3.Dot(Abs(s.v), inner), radius, arc);
                int start = vertices.Count;
                foreach (float b in vs)
                    foreach (float a in us)
                    {
                        // A point on the box's side pulled onto the rounded surface around the inner box.
                        Vector3 p = s.u * a + s.v * b + s.n * depth;
                        Vector3 q = new Vector3(Mathf.Clamp(p.x, -inner.x, inner.x), Mathf.Clamp(p.y, -inner.y, inner.y), Mathf.Clamp(p.z, -inner.z, inner.z));
                        Vector3 normal = (p - q).normalized;
                        vertices.Add(q + normal * radius);
                        normals.Add(normal);
                        colors.Add(s.c);
                    }
                int w = us.Length;
                for (int j = 0; j < vs.Length - 1; j++)
                    for (int i = 0; i < w - 1; i++)
                    {
                        int a0 = start + j * w + i, a1 = a0 + 1, b0 = a0 + w, b1 = b0 + 1;
                        AddOutward(triangles, vertices, normals, a0, b0, a1);
                        AddOutward(triangles, vertices, normals, a1, b0, b1);
                    }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Positions across one side: the flat middle (3 lines) and `arc` lines on each rounded edge, spaced by equal
        // angles (flat + r tan(angle), up to 45 degrees, where the next side takes over).
        private static float[] BoxSteps(float flat, float radius, int arc)
        {
            var steps = new float[arc * 2 + 3];
            for (int j = 0; j < arc; j++)
            {
                float edge = flat + radius * Mathf.Tan((arc - j) / (float)arc * Mathf.PI * .25f);
                steps[j] = -edge;
                steps[steps.Length - 1 - j] = edge;
            }
            steps[arc] = -flat;
            steps[arc + 1] = 0f;
            steps[arc + 2] = flat;
            return steps;
        }

        // Adds a triangle wound so it faces along its vertices' normals.
        private static void AddOutward(List<int> triangles, List<Vector3> vertices, List<Vector3> normals, int a, int b, int c)
        {
            Vector3 facing = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            bool outward = Vector3.Dot(facing, normals[a] + normals[b] + normals[c]) >= 0f;
            triangles.Add(a);
            triangles.Add(outward ? b : c);
            triangles.Add(outward ? c : b);
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
