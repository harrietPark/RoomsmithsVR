using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // Something the brush can paint, part by part (2026-11-11). Its mesh is split into named parts (a cube's
    // faces; a made thing's rim, floor, bottom, outside and inside), each its own submesh with box-projected
    // UVs (one tile every 6 cm), so a texture lies on it like a real material. Painting a part gives that
    // part (only) the swatch's look: a colour, or the material tile with its relief.
    public sealed class PaintableThing : MonoBehaviour
    {
        [Serializable] public sealed class Mark { public string part, kind, hex, image; }

        public const float TileSize = .06f;
        public static readonly List<PaintableThing> All = new List<PaintableThing>();
        public static readonly string[] CubeParts = { "right", "left", "top", "bottom", "front", "back" };
        public static readonly string[] MadeParts = { "rim", "floor", "bottom", "outside", "inside" };

        public string[] Parts { get; private set; }
        public MeshRenderer Renderer { get; private set; }
        public Mesh Mesh { get; private set; }
        public readonly Dictionary<int, Mark> Marks = new Dictionary<int, Mark>();
        public int PaintCount { get; private set; }

        private List<Vector3>[] triangles;   // per part: local corners, three by three
        private Material[] originals;        // each part's look before any paint
        private int[] sourceIndex;           // each new vertex's vertex in the source mesh (for re-tinting)

        private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        private void OnDisable() => All.Remove(this);
        private void OnDestroy() { if (Mesh != null) Destroy(Mesh); }

        // The cube's faces by the side they face.
        public static int CubePart(Vector3 normal, Vector3 centre, Bounds bounds)
        {
            Vector3 a = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            if (a.x >= a.y && a.x >= a.z) return normal.x > 0f ? 0 : 1;
            if (a.y >= a.z) return normal.y > 0f ? 2 : 3;
            return normal.z > 0f ? 4 : 5;
        }

        // A coaster or a vessel: the top of its rim, the floor (inside bottom), the underside, the outer wall
        // and the inner wall.
        public static int MadePart(Vector3 normal, Vector3 centre, Bounds bounds)
        {
            if (normal.y > .6f) return centre.y > bounds.max.y - Mathf.Max(.0035f, bounds.size.y * .12f) ? 0 : 1;
            if (normal.y < -.6f) return 2;
            Vector3 radial = centre - bounds.center;
            radial.y = 0f;
            Vector3 flat = new Vector3(normal.x, 0f, normal.z);
            return Vector3.Dot(flat, radial) >= 0f ? 3 : 4;
        }

        // `keepUv`: a model with its own texture (F30, Meshy) keeps its texture coordinates.
        public static PaintableThing Make(MeshFilter filter, MeshRenderer renderer, Mesh source, Func<Vector3, Vector3, Bounds, int> classify, string[] names, bool keepUv = false)
        {
            var thing = filter.gameObject.GetComponent<PaintableThing>() ?? filter.gameObject.AddComponent<PaintableThing>();
            thing.Build(filter, renderer, source, classify, names, keepUv);
            return thing;
        }

        private void Build(MeshFilter filter, MeshRenderer renderer, Mesh source, Func<Vector3, Vector3, Bounds, int> classify, string[] names, bool keepUv)
        {
            // UVs in real size (the mesh may be scaled by its transform, like the unit test cube).
            float metres = Mathf.Abs(filter.transform.lossyScale.x) / TileSize;
            var prepared = Prepare(source.vertices, source.normals, source.colors, keepUv ? source.uv : null, source.triangles, source.bounds, metres, classify, names, source.name);
            ApplyPrepared(filter, renderer, prepared, true);
        }

        // F57: the heavy part of building the parts, as plain arrays (safe in Task.Run): a rebuilt solid of 30k
        // vertices is split off the main thread, then ApplyPrepared only hands the arrays to the mesh.
        public sealed class Prepared
        {
            public string name;
            public string[] parts;
            public List<Vector3>[] triangles;
            public List<Vector3> vertices = new List<Vector3>();
            public List<Vector3> normals = new List<Vector3>();
            public List<Color> colors = new List<Color>();
            public List<Vector2> uvs = new List<Vector2>();
            public List<int> from = new List<int>();
            public List<int[]> subs = new List<int[]>();
        }

        public static Prepared Prepare(Vector3[] v, Vector3[] n, Color[] c, Vector2[] own, int[] t, Bounds bounds, float metres,
            Func<Vector3, Vector3, Bounds, int> classify, string[] names, string name = "Made")
        {
            if (own != null && own.Length != v.Length) own = null;
            int count = names.Length;
            var vertices = new List<Vector3>[count];
            var normals = new List<Vector3>[count];
            var colors = new List<Color>[count];
            var uvs = new List<Vector2>[count];
            var from = new List<int>[count];
            for (int p = 0; p < count; p++) { vertices[p] = new List<Vector3>(); normals[p] = new List<Vector3>(); colors[p] = new List<Color>(); uvs[p] = new List<Vector2>(); from[p] = new List<int>(); }
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], d = v[t[i + 2]];
                Vector3 face = Vector3.Cross(b - a, d - a);
                if (face.sqrMagnitude < 1e-14f)
                {
                    // 2026-11-15 (F90b): a sliver (the thin walls of a hollowed solid make some) is kept, so the shown mesh
                    // stays closed; it is classified by its vertex normals (it draws nothing itself).
                    face = n != null && n.Length == v.Length ? n[t[i]] + n[t[i + 1]] + n[t[i + 2]] : Vector3.up;
                    if (face.sqrMagnitude < 1e-12f) face = Vector3.up;
                }
                face /= Mathf.Sqrt(face.sqrMagnitude); // not Normalize(): it returns zero below 1e-5 (small triangles)
                int part = Mathf.Clamp(classify(face, (a + b + d) / 3f, bounds), 0, count - 1);
                // Box projection by the face's main direction.
                Vector3 m = new Vector3(Mathf.Abs(face.x), Mathf.Abs(face.y), Mathf.Abs(face.z));
                for (int k = 0; k < 3; k++)
                {
                    int s = t[i + k];
                    Vector3 p = v[s];
                    Vector2 uv = m.y >= m.x && m.y >= m.z ? new Vector2(p.x, p.z) : m.x >= m.z ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y);
                    vertices[part].Add(p);
                    normals[part].Add(n != null && n.Length == v.Length ? n[s] : face);
                    colors[part].Add(c != null && c.Length == v.Length ? c[s] : Color.white);
                    uvs[part].Add(own != null ? own[s] : uv * metres);
                    from[part].Add(s);
                }
            }
            var result = new Prepared { name = name };
            var used = new List<int>();
            for (int p = 0; p < count; p++) if (vertices[p].Count > 0) used.Add(p);
            result.parts = new string[used.Count];
            result.triangles = new List<Vector3>[used.Count];
            for (int k = 0; k < used.Count; k++)
            {
                int p = used[k];
                result.parts[k] = names[p];
                result.triangles[k] = vertices[p];
                int start = result.vertices.Count;
                result.vertices.AddRange(vertices[p]);
                result.normals.AddRange(normals[p]);
                result.colors.AddRange(colors[p]);
                result.uvs.AddRange(uvs[p]);
                result.from.AddRange(from[p]);
                var idx = new int[vertices[p].Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = start + i;
                result.subs.Add(idx);
            }
            return result;
        }

        // `tangents` false skips RecalculateTangents (a big rebuilt solid: its paint is triplanar and needs none).
        public static PaintableThing MakePrepared(MeshFilter filter, MeshRenderer renderer, Prepared prepared, bool tangents)
        {
            var thing = filter.gameObject.GetComponent<PaintableThing>() ?? filter.gameObject.AddComponent<PaintableThing>();
            thing.ApplyPrepared(filter, renderer, prepared, tangents);
            return thing;
        }

        private void ApplyPrepared(MeshFilter filter, MeshRenderer renderer, Prepared p, bool tangents)
        {
            Renderer = renderer;
            Parts = p.parts;
            triangles = p.triangles;
            if (Mesh != null) Destroy(Mesh);
            Mesh = new Mesh { name = p.name + " (parts)" };
            if (p.vertices.Count > 65000) Mesh.indexFormat = IndexFormat.UInt32;
            Mesh.SetVertices(p.vertices);
            Mesh.SetNormals(p.normals);
            Mesh.SetColors(p.colors);
            Mesh.SetUVs(0, p.uvs);
            Mesh.subMeshCount = p.subs.Count;
            for (int k = 0; k < p.subs.Count; k++) Mesh.SetTriangles(p.subs[k], k);
            if (tangents) Mesh.RecalculateTangents();
            Mesh.RecalculateBounds();
            sourceIndex = p.from.ToArray();
            filter.sharedMesh = Mesh;
            var materials = new Material[p.subs.Count];
            for (int k = 0; k < materials.Length; k++) materials[k] = renderer.sharedMaterial;
            renderer.sharedMaterials = materials;
            originals = (Material[])materials.Clone();
            Marks.Clear();
        }

        // The source mesh was re-tinted (vertex colours): copy them over.
        public void RefreshColours(Mesh source)
        {
            if (Mesh == null || source == null || sourceIndex == null) return;
            var c = source.colors;
            if (c == null || c.Length == 0) return;
            var colors = new Color[sourceIndex.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = sourceIndex[i] < c.Length ? c[sourceIndex[i]] : Color.white;
            Mesh.colors = colors;
        }

        public int PartIndex(string name) => Parts == null ? -1 : Array.IndexOf(Parts, name);

        // The part nearest a point in the world (the one the brush touches).
        public int PartAt(Vector3 world, out float distance)
        {
            distance = float.MaxValue;
            if (triangles == null) return -1;
            Vector3 local = transform.InverseTransformPoint(world);
            int best = -1;
            for (int p = 0; p < triangles.Length; p++)
            {
                var list = triangles[p];
                for (int i = 0; i + 2 < list.Count; i += 3)
                {
                    float d = (ClosestOnTriangle(local, list[i], list[i + 1], list[i + 2]) - local).sqrMagnitude;
                    if (d < distance) { distance = d; best = p; }
                }
            }
            distance = Mathf.Sqrt(distance) * transform.lossyScale.x;
            return best;
        }

        // The nearest point on any part's surface to a point in the world, with that triangle's normal turned
        // toward the point (the side the brush is on).
        public int Closest(Vector3 world, out Vector3 point, out Vector3 normal, out float distance)
        {
            point = world;
            normal = Vector3.up;
            distance = float.MaxValue;
            if (triangles == null) return -1;
            Vector3 local = transform.InverseTransformPoint(world);
            int best = -1;
            Vector3 bestPoint = local, bestNormal = Vector3.up;
            for (int p = 0; p < triangles.Length; p++)
            {
                var list = triangles[p];
                for (int i = 0; i + 2 < list.Count; i += 3)
                {
                    Vector3 q = ClosestOnTriangle(local, list[i], list[i + 1], list[i + 2]);
                    float d = (q - local).sqrMagnitude;
                    if (!(d < distance)) continue; // (NaN-safe)
                    distance = d;
                    best = p;
                    bestPoint = q;
                    bestNormal = Vector3.Cross(list[i + 1] - list[i], list[i + 2] - list[i]);
                }
            }
            point = transform.TransformPoint(bestPoint);
            normal = Unit(transform.TransformDirection(bestNormal));
            if (Vector3.Dot(normal, world - point) < 0f) normal = -normal;
            distance = Vector3.Distance(world, point);
            return best;
        }

        // Not .normalized: it gives zero below 1e-5, and a made thing's small triangles (in metres) are
        // smaller than that (the brush's smears then had no size).
        private static Vector3 Unit(Vector3 v)
        {
            float m = v.sqrMagnitude;
            return m > 1e-30f ? v / Mathf.Sqrt(m) : Vector3.up;
        }

        // The nearest point on one part (the stroke stays on the part it started on).
        public bool ClosestOnPart(int part, Vector3 world, out Vector3 point, out Vector3 normal, out float distance)
        {
            point = world;
            normal = Vector3.up;
            distance = float.MaxValue;
            if (triangles == null || part < 0 || part >= triangles.Length) return false;
            Vector3 local = transform.InverseTransformPoint(world);
            var list = triangles[part];
            Vector3 bestPoint = local, bestNormal = Vector3.up;
            for (int i = 0; i + 2 < list.Count; i += 3)
            {
                Vector3 q = ClosestOnTriangle(local, list[i], list[i + 1], list[i + 2]);
                float d = (q - local).sqrMagnitude;
                if (!(d < distance)) continue; // (NaN-safe)
                distance = d;
                bestPoint = q;
                bestNormal = Vector3.Cross(list[i + 1] - list[i], list[i + 2] - list[i]);
            }
            point = transform.TransformPoint(bestPoint);
            normal = Unit(transform.TransformDirection(bestNormal));
            if (Vector3.Dot(normal, world - point) < 0f) normal = -normal;
            distance = Vector3.Distance(world, point);
            return true;
        }

        public Material Original(int part) => originals != null && part >= 0 && part < originals.Length ? originals[part] : null;

        // The paint comes off: the part looks as it was made.
        public void Clear(int part)
        {
            if (Renderer == null || originals == null || part < 0 || part >= originals.Length) return;
            var materials = Renderer.sharedMaterials;
            materials[part] = originals[part];
            Renderer.sharedMaterials = materials;
            Marks.Remove(part);
            PaintCount++;
        }

        public Material Current(int part) => Renderer != null && part >= 0 && part < Renderer.sharedMaterials.Length ? Renderer.sharedMaterials[part] : null;

        // A part's surface area in square metres.
        public float PartArea(int part)
        {
            if (triangles == null || part < 0 || part >= triangles.Length) return 0f;
            float area = 0f;
            var list = triangles[part];
            for (int i = 0; i + 2 < list.Count; i += 3) area += Vector3.Cross(list[i + 1] - list[i], list[i + 2] - list[i]).magnitude * .5f;
            float s = transform.lossyScale.x;
            return area * s * s;
        }

        public void Apply(int part, Material material, Mark mark)
        {
            if (Renderer == null || part < 0 || part >= Renderer.sharedMaterials.Length) return;
            var materials = Renderer.sharedMaterials;
            materials[part] = material;
            Renderer.sharedMaterials = materials;
            if (mark != null) { mark.part = Parts[part]; Marks[part] = mark; }
            PaintCount++;
        }

        public List<Mark> SavedMarks()
        {
            var list = new List<Mark>();
            foreach (var m in Marks.Values) list.Add(new Mark { part = m.part, kind = m.kind, hex = m.hex, image = m.image });
            return list;
        }

        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            // 2026-11-15 (F93): a sliver kept by Prepare (F90b) has no area: 1/0 made the point NaN, and a NaN distance
            // won every later comparison in Closest / ClosestOnPart (the last part was always "nearest": no part lit).
            float sum = va + vb + vc;
            if (!(sum > 0f))
            {
                float da = (p - a).sqrMagnitude, db = (p - b).sqrMagnitude, dc = (p - c).sqrMagnitude;
                return da <= db && da <= dc ? a : db <= dc ? b : c;
            }
            float denom = 1f / sum;
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        // A unit cube (1 m, centred) with its own vertices per face, for the paint test cube.
        public static Mesh CubeMesh()
        {
            var mesh = new Mesh { name = "Paint Cube" };
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var tri = new List<int>();
            void Face(Vector3 normal, Vector3 up)
            {
                Vector3 right = Vector3.Cross(normal, up); // clockwise seen from outside
                int s = v.Count;
                Vector3 c = normal * .5f;
                v.Add(c - right * .5f - up * .5f); v.Add(c - right * .5f + up * .5f); v.Add(c + right * .5f + up * .5f); v.Add(c + right * .5f - up * .5f);
                for (int k = 0; k < 4; k++) n.Add(normal);
                tri.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
            }
            Face(Vector3.right, Vector3.up); Face(Vector3.left, Vector3.up); Face(Vector3.up, Vector3.forward);
            Face(Vector3.down, Vector3.back); Face(Vector3.forward, Vector3.up); Face(Vector3.back, Vector3.up);
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
