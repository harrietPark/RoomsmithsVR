using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F57 (2026-11-13, the user: "whatever material it takes it's a bit bumpy - isn't it usually smooth?"): Meshy's
    // OBJ came in as one vertex per triangle corner, so Unity's normals were one per face and every model looked
    // faceted. Here the normals are averaged over the faces that meet at a point (welded by position, so a UV seam
    // does not show as a crease), except across a real edge (faces more than `creaseDeg` apart), and corners that
    // end up identical are merged into one vertex. Plain C# on arrays: safe off the main thread.
    public static class MeshSmooth
    {
        public const float CreaseDegrees = 40f;

        // Corner normals for an indexed or per-corner mesh: one normal per entry of `t` (corner i of triangle i/3).
        public static Vector3[] CornerNormals(Vector3[] v, int[] t, float creaseDeg = CreaseDegrees)
        {
            int faces = t.Length / 3;
            var face = new Vector3[faces];      // area-weighted
            var unit = new Vector3[faces];
            for (int f = 0; f < faces; f++)
            {
                Vector3 a = v[t[f * 3]], b = v[t[f * 3 + 1]], c = v[t[f * 3 + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                face[f] = n;
                float m = n.magnitude;
                unit[f] = m > 1e-20f ? n / m : Vector3.zero;
            }
            // Corners by welded position.
            // welded by position (packed long keys: Unity's Vector3Int hash collides badly for big coordinates)
            var weld = SolidRebuild.WeldIds(v);
            int points = 0;
            for (int i = 0; i < weld.Length; i++) points = Mathf.Max(points, weld[i] + 1);
            var pointOf = new int[t.Length];
            for (int i = 0; i < t.Length; i++) pointOf[i] = weld[t[i]];
            // Faces around each point.
            var start = new int[points + 1];
            for (int i = 0; i < t.Length; i++) start[pointOf[i] + 1]++;
            for (int i = 0; i < points; i++) start[i + 1] += start[i];
            var fill = (int[])start.Clone();
            var around = new int[t.Length];
            for (int i = 0; i < t.Length; i++) around[fill[pointOf[i]]++] = i / 3;
            float cos = Mathf.Cos(creaseDeg * Mathf.Deg2Rad);
            var result = new Vector3[t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                int f = i / 3, p = pointOf[i];
                Vector3 own = unit[f], sum = Vector3.zero;
                for (int k = start[p]; k < start[p + 1]; k++)
                {
                    int g = around[k];
                    if (g == f || Vector3.Dot(own, unit[g]) >= cos) sum += face[g];
                }
                float m = sum.magnitude;
                result[i] = m > 1e-20f ? sum / m : (own.sqrMagnitude > 0f ? own : Vector3.up);
            }
            return result;
        }

        // A mesh from corners (positions, optional uvs and colours per vertex index) with smooth normals, merging
        // corners that share position, uv and normal. Returns the new arrays through the out parameters.
        public static void Weld(Vector3[] v, Vector2[] uv, int[] t, float creaseDeg,
            out Vector3[] vertices, out Vector3[] normals, out Vector2[] uvs, out int[] triangles)
        {
            var corner = CornerNormals(v, t, creaseDeg);
            var bounds = Bounds(v);
            float q = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) * 1e-6f;
            if (q <= 0f) q = 1e-9f;
            var map = new Dictionary<(int, int, int, int, int, int, int, int), int>(t.Length / 3);
            var vs = new List<Vector3>(t.Length / 3);
            var ns = new List<Vector3>(t.Length / 3);
            var us = new List<Vector2>(t.Length / 3);
            triangles = new int[t.Length];
            bool hasUv = uv != null && uv.Length == v.Length;
            for (int i = 0; i < t.Length; i++)
            {
                Vector3 p = v[t[i]], n = corner[i];
                Vector2 u = hasUv ? uv[t[i]] : Vector2.zero;
                var key = (Mathf.RoundToInt(p.x / q), Mathf.RoundToInt(p.y / q), Mathf.RoundToInt(p.z / q),
                           Mathf.RoundToInt(u.x * 65536f), Mathf.RoundToInt(u.y * 65536f),
                           Mathf.RoundToInt(n.x * 1024f), Mathf.RoundToInt(n.y * 1024f), Mathf.RoundToInt(n.z * 1024f));
                if (!map.TryGetValue(key, out int index))
                {
                    index = vs.Count;
                    map[key] = index;
                    vs.Add(p); ns.Add(n); us.Add(u);
                }
                triangles[i] = index;
            }
            vertices = vs.ToArray();
            normals = ns.ToArray();
            uvs = hasUv ? us.ToArray() : null;
        }

        // Smooth normals for a mesh that is already indexed (vertices split where the uv changes): a vertex gets the
        // average over every face that meets at its position within the crease angle of its own faces.
        public static Vector3[] VertexNormals(Vector3[] v, int[] t, float creaseDeg = CreaseDegrees)
        {
            var corner = CornerNormals(v, t, creaseDeg);
            var sum = new Vector3[v.Length];
            for (int i = 0; i < t.Length; i++) sum[t[i]] += corner[i];
            for (int i = 0; i < sum.Length; i++)
            {
                float m = sum[i].magnitude;
                sum[i] = m > 1e-20f ? sum[i] / m : Vector3.up;
            }
            return sum;
        }

        private static Bounds Bounds(Vector3[] v)
        {
            if (v.Length == 0) return new Bounds();
            Vector3 lo = v[0], hi = v[0];
            for (int i = 1; i < v.Length; i++) { lo = Vector3.Min(lo, v[i]); hi = Vector3.Max(hi, v[i]); }
            var b = new Bounds();
            b.SetMinMax(lo, hi);
            return b;
        }
    }
}
