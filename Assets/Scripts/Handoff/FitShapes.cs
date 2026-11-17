using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Windups.Handoff
{
    // F30: the made things beyond coasters and vessels.
    //  - tray: pockets shaped like the real things laid on the desk (traced from the camera, fixed by a
    //    fingertip, sized by the hand tape), inside a rounded, round, hugging or drawn outline.
    //  - clip: a profile that clamps the real desk edge (its thickness measured with the tape), pushed out
    //    along the edge: a hook, a cable clip or a little shelf.
    //  - model: a Meshy model made from the chosen picture, scaled to the measured width.
    // Trays and clips come out of one 2D mesher: a field (negative = material) sampled on a grid; each layer
    // is filled cell by cell (marching squares) and the walls follow the same edge points, so the solid is
    // closed and prints as one piece. Everything is in millimetres here; meshes are in metres.
    public static class FitShapes
    {
        private const float Mm = .001f;
        public static readonly string[] TrayParts = { "top", "pocket", "floor", "bottom", "side" };
        public static readonly string[] ClipParts = { "hook", "top", "front", "inside", "ends", "bottom" };
        public static readonly string[] ModelParts = { "top", "side", "bottom" };
        public static readonly string[] Kinds = { "coaster", "vessel", "tray", "clip", "model" };
        public static readonly string[] TrayOuters = { "rounded", "round", "hug", "drawn" };
        public static readonly string[] ClipTypes = { "hook", "cable", "shelf" };

        // ------------------------------------------------------------ 2D fields (mm, negative inside)

        public static float PolygonDistance(Vector2 p, IList<Vector2> poly)
        {
            float best = float.MaxValue;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[j], b = poly[i];
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-9f, ab.sqrMagnitude));
                best = Mathf.Min(best, (p - (a + ab * t)).sqrMagnitude);
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            float d = Mathf.Sqrt(best);
            return inside ? -d : d;
        }

        private static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-9f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude;
        }

        private static float RoundBox(Vector2 p, Vector2 centre, Vector2 half, float r)
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x - centre.x), Mathf.Abs(p.y - centre.y)) - half + Vector2.one * r;
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        // Convex hull (monotone chain), counter-clockwise.
        private static List<Vector2> Hull(List<Vector2> points)
        {
            var p = points.Distinct().OrderBy(q => q.x).ThenBy(q => q.y).ToList();
            if (p.Count < 3) return p;
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var hull = new List<Vector2>();
            foreach (var q in p) { while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 0f) hull.RemoveAt(hull.Count - 1); hull.Add(q); }
            int lower = hull.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 0f) hull.RemoveAt(hull.Count - 1); hull.Add(q); }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        // ------------------------------------------------------------ the grid mesher

        private sealed class Grid
        {
            public float x0, y0, cell;
            public int nx, ny;
            public float[] v;
            public float At(int i, int j) => v[j * (nx + 1) + i];
            public Vector2 P(int i, int j) => new Vector2(x0 + i * cell, y0 + j * cell);
        }

        private struct Seg { public Vector2 a, b; }

        // Never exactly on the line, so every corner is clearly in or out.
        private static float Nudge(float f) => Mathf.Abs(f) < 1e-4f ? 1e-4f : f;

        private static Grid Sample(Vector2 min, Vector2 max, float cell, Func<Vector2, float> field)
        {
            var g = new Grid
            {
                cell = cell, x0 = min.x, y0 = min.y,
                nx = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / cell)), ny = Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / cell))
            };
            g.v = new float[(g.nx + 1) * (g.ny + 1)];
            for (int j = 0; j <= g.ny; j++)
                for (int i = 0; i <= g.nx; i++)
                    g.v[j * (g.nx + 1) + i] = Nudge(field(g.P(i, j)));
            return g;
        }

        private static Grid Combine(Grid a, Grid b, Func<float, float, float> f)
        {
            var g = new Grid { x0 = a.x0, y0 = a.y0, cell = a.cell, nx = a.nx, ny = a.ny, v = new float[a.v.Length] };
            for (int k = 0; k < g.v.Length; k++) g.v[k] = f(a.v[k], b.v[k]);
            return g;
        }

        // The crossing on one grid edge, always computed from the edge's lower corner, so the two cells
        // sharing the edge (and every layer using the same values there) get exactly the same point.
        private static Vector2 EdgePoint(Grid g, int ia, int ja, int ib, int jb)
        {
            if (ib < ia || jb < ja) { (ia, ib) = (ib, ia); (ja, jb) = (jb, ja); }
            float fa = g.At(ia, ja), fb = g.At(ib, jb);
            // Never right on a corner (a sliver there would weld into a broken edge when printed).
            float t = Mathf.Clamp(fa / (fa - fb), .035f, .965f);
            Vector2 a = g.P(ia, ja), b = g.P(ib, jb);
            return a + (b - a) * t;
        }

        // Each cell's inside as pieces (counter-clockwise) and the boundary segments (inside on the left).
        private static void March(Grid g, List<List<Vector2>> pieces, List<Seg> segs)
        {
            int[] di = { 0, 1, 1, 0 }, dj = { 0, 0, 1, 1 };
            var corner = new Vector2[4];
            var f = new float[4];
            var ins = new bool[4];
            for (int j = 0; j < g.ny; j++)
                for (int i = 0; i < g.nx; i++)
                {
                    int count = 0;
                    for (int k = 0; k < 4; k++) { f[k] = g.At(i + di[k], j + dj[k]); ins[k] = f[k] < 0f; if (ins[k]) count++; }
                    if (count == 0) continue;
                    for (int k = 0; k < 4; k++) corner[k] = g.P(i + di[k], j + dj[k]);
                    if (count == 4) { pieces?.Add(new List<Vector2>(corner)); continue; }
                    var poly = new List<Vector2>(6);
                    var onEdge = new List<bool>(6);
                    for (int k = 0; k < 4; k++)
                    {
                        int n = (k + 1) & 3;
                        if (ins[k]) { poly.Add(corner[k]); onEdge.Add(false); }
                        if (ins[k] != ins[n]) { poly.Add(EdgePoint(g, i + di[k], j + dj[k], i + di[n], j + dj[n])); onEdge.Add(true); }
                    }
                    bool split = count == 2 && ins[0] == ins[2] && (f[0] + f[1] + f[2] + f[3]) * .25f >= 0f;
                    if (split)
                    {
                        // A saddle with its middle outside: two separate corner triangles.
                        for (int k = 0; k < poly.Count; k++)
                        {
                            if (onEdge[k]) continue;
                            int before = (k + poly.Count - 1) % poly.Count, after = (k + 1) % poly.Count;
                            AddPiece(new List<Vector2> { poly[before], poly[k], poly[after] }, new List<bool> { true, false, true }, pieces, segs);
                        }
                    }
                    else AddPiece(poly, onEdge, pieces, segs);
                }
        }

        private static void AddPiece(List<Vector2> poly, List<bool> onEdge, List<List<Vector2>> pieces, List<Seg> segs)
        {
            pieces?.Add(poly);
            if (segs == null) return;
            for (int k = 0; k < poly.Count; k++)
            {
                int n = (k + 1) % poly.Count;
                if (onEdge[k] && onEdge[n]) segs.Add(new Seg { a = poly[k], b = poly[n] });
            }
        }

        private static void Face(List<List<Vector2>> pieces, Func<Vector2, Vector3> to3, Vector3 outward, List<Vector3> v, List<int> t)
        {
            foreach (var piece in pieces)
            {
                if (piece.Count > 5)
                {
                    // The joined saddle (not convex): fan from its middle.
                    Vector2 c = Vector2.zero;
                    foreach (var p in piece) c += p;
                    c /= piece.Count;
                    for (int k = 0; k < piece.Count; k++) Tri(v, t, to3(c), to3(piece[k]), to3(piece[(k + 1) % piece.Count]), outward);
                }
                else for (int k = 1; k + 1 < piece.Count; k++) Tri(v, t, to3(piece[0]), to3(piece[k]), to3(piece[k + 1]), outward);
            }
        }

        private static void Tri(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) (b, c) = (c, b);
            v.Add(a); v.Add(b); v.Add(c);
            t.Add(v.Count - 3); t.Add(v.Count - 2); t.Add(v.Count - 1);
        }

        private static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
        {
            Tri(v, t, a, b, c, outward);
            Tri(v, t, a, c, d, outward);
        }

        private static Mesh ToMesh(string name, List<Vector3> v, List<int> t)
        {
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // The closed loops where a field crosses zero (for traced outlines): inside on the left.
        public static List<List<Vector2>> Loops(float[] values, int nx, int ny, Vector2 origin, float cell)
        {
            var g = new Grid { x0 = origin.x, y0 = origin.y, cell = cell, nx = nx, ny = ny, v = values.Select(Nudge).ToArray() };
            var segs = new List<Seg>();
            March(g, null, segs);
            var next = new Dictionary<Vector2, Vector2>();
            foreach (var s in segs) next[s.a] = s.b;
            var loops = new List<List<Vector2>>();
            var used = new HashSet<Vector2>();
            foreach (var s in segs)
            {
                if (used.Contains(s.a)) continue;
                var loop = new List<Vector2>();
                Vector2 at = s.a;
                for (int guard = 0; guard < segs.Count + 2 && !used.Contains(at); guard++)
                {
                    used.Add(at);
                    loop.Add(at);
                    if (!next.TryGetValue(at, out at)) break;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }

        // ------------------------------------------------------------ tray

        // The pockets in tray space (mm, x right, y away from the person), from the design.
        public static List<List<Vector2>> Pockets(CoasterDesign d)
        {
            var list = new List<List<Vector2>>();
            if (d.pockets == null) return list;
            foreach (var flat in d.pockets)
            {
                if (flat == null || flat.Length < 6) continue;
                var poly = new List<Vector2>();
                for (int i = 0; i + 1 < flat.Length; i += 2) poly.Add(new Vector2(flat[i], flat[i + 1]));
                list.Add(poly);
            }
            return list;
        }

        private static Func<Vector2, float> PocketField(List<List<Vector2>> pockets, float gap)
        {
            if (pockets.Count == 0) return p => 1e3f;
            return p =>
            {
                float best = float.MaxValue;
                foreach (var poly in pockets) best = Mathf.Min(best, PolygonDistance(p, poly));
                return best - gap;
            };
        }

        private static void PocketBounds(List<List<Vector2>> pockets, out Vector2 min, out Vector2 max)
        {
            min = Vector2.one * float.MaxValue;
            max = Vector2.one * float.MinValue;
            foreach (var poly in pockets) foreach (var p in poly) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            if (pockets.Count == 0) { min = new Vector2(-30f, -30f); max = new Vector2(30f, 30f); }
        }

        // The outline: always holds every pocket with at least `margin` of wall around it.
        private static Func<Vector2, float> OuterField(CoasterDesign d, List<List<Vector2>> pockets, Func<Vector2, float> pocket, float margin)
        {
            PocketBounds(pockets, out var min, out var max);
            Vector2 centre = (min + max) * .5f, half = (max - min) * .5f + Vector2.one * (d.clearance * .5f + margin);
            Func<Vector2, float> hold = p => pocket(p) - margin;
            switch (d.outer)
            {
                case "round":
                {
                    Vector2 axes = half * 1.12f;
                    float small = Mathf.Min(axes.x, axes.y);
                    return p => Mathf.Min(hold(p), (new Vector2((p.x - centre.x) / axes.x, (p.y - centre.y) / axes.y).magnitude - 1f) * small);
                }
                case "hug":
                {
                    // One piece wrapped around all the things: their convex hull, grown by the wall.
                    var hull = Hull(pockets.SelectMany(q => q).ToList());
                    float grow = d.clearance * .5f + margin;
                    return p => Mathf.Min(hold(p), PolygonDistance(p, hull) - grow);
                }
                case "drawn" when d.outline != null && d.outline.Length == 72:
                {
                    float mean = d.outline.Average();
                    float radius = Mathf.Max(half.x, half.y) * 1.08f;
                    return p =>
                    {
                        Vector2 q = p - centre;
                        float a = Mathf.Repeat(Mathf.Atan2(q.y, q.x) * Mathf.Rad2Deg, 360f) / 5f;
                        int i = Mathf.FloorToInt(a) % 72;
                        float r = radius * Mathf.Lerp(d.outline[i], d.outline[(i + 1) % 72], a - Mathf.Floor(a)) / Mathf.Max(.2f, mean);
                        return Mathf.Min(hold(p), q.magnitude - r);
                    };
                }
                default:
                {
                    float corner = Mathf.Min(14f, Mathf.Min(half.x, half.y) * .9f);
                    return p => Mathf.Min(hold(p), RoundBox(p, centre, half, corner));
                }
            }
        }

        public static Mesh Tray(CoasterDesign d)
        {
            var pockets = Pockets(d);
            float margin = Mathf.Max(4f, d.rimWidth), floor = d.baseThickness, top = d.Height;
            var pocket = PocketField(pockets, d.clearance * .5f);
            var outer = OuterField(d, pockets, pocket, margin);
            PocketBounds(pockets, out var min, out var max);
            float reach = Mathf.Max(max.x - min.x, max.y - min.y);
            float pad = d.clearance + margin + reach * .35f + 8f;
            min -= Vector2.one * pad;
            max += Vector2.one * pad;
            // Cells smaller than the wall around the pockets, so the outline and the pockets never share a cell.
            float cell = Mathf.Clamp(Mathf.Max(max.x - min.x, max.y - min.y) / 140f, .9f, Mathf.Min(2.4f, margin / 1.7f));
            var o = Sample(min, max, cell, outer);
            var p = Sample(min, max, cell, pocket);
            var topGrid = Combine(o, p, (a, b) => Mathf.Max(a, -b));
            var floorGrid = Combine(o, p, Mathf.Max);
            var v = new List<Vector3>();
            var t = new List<int>();
            Vector3 To3(Vector2 q, float y) => new Vector3(q.x * Mm, y * Mm, q.y * Mm);
            var bottomPieces = new List<List<Vector2>>();
            March(o, bottomPieces, null);
            Face(bottomPieces, q => To3(q, 0f), Vector3.down, v, t);
            var floorPieces = new List<List<Vector2>>();
            March(floorGrid, floorPieces, null);
            Face(floorPieces, q => To3(q, floor), Vector3.up, v, t);
            var topPieces = new List<List<Vector2>>();
            var segs = new List<Seg>();
            March(topGrid, topPieces, segs);
            Face(topPieces, q => To3(q, top), Vector3.up, v, t);
            foreach (var s in segs)
            {
                Vector2 mid = (s.a + s.b) * .5f;
                bool outside = outer(mid) > -pocket(mid);
                float y0 = outside ? 0f : floor;
                Vector2 dir = s.b - s.a;
                Quad(v, t, To3(s.a, y0), To3(s.b, y0), To3(s.b, top), To3(s.a, top), new Vector3(dir.y, 0f, -dir.x));
            }
            return ToMesh("Tray " + d.name, v, t);
        }

        // ------------------------------------------------------------ clip on the desk edge

        // The clip's profile as chains of round bars (mm): u out from the desk's front face (toward the
        // person), v up from the desk top. The desk is u < 0, -thickness < v < 0.
        public static List<List<Vector2>> ClipChains(CoasterDesign d)
        {
            float desk = d.cupDiameter, g = d.clearance * .5f, w = d.rimWidth, h = w * .5f, reach = d.grip;
            var corner = new Vector2(g + h, g + h);
            var frontBottom = new Vector2(g + h, -desk - g - h);
            // F41: both tips flare out (a lead-in that guides it onto the edge) and the lower bar has a small snap
            // bump reaching 0.3 mm into the desk, so a slightly flexible print (PETG) clicks on and stays.
            float low = -desk - g - h, bump = -desk - h + .3f;
            var chains = new List<List<Vector2>>
            {
                new List<Vector2>
                {
                    new Vector2(-reach - 2.5f, g + h + 2.5f), new Vector2(-reach, g + h), corner, frontBottom,
                    new Vector2(-reach * .4f, low), new Vector2(-reach * .52f, Mathf.Max(low, bump)), new Vector2(-reach * .64f, low),
                    new Vector2(-reach * .7f, low), new Vector2(-reach * .7f - 2.5f, low - 2.5f)
                }
            };
            switch (d.clipType)
            {
                case "cable":
                {
                    float r = Mathf.Max(3f, d.rimHeight * .5f);
                    var centre = new Vector2(-reach * .45f, g + w + r - h * .4f);
                    var loop = new List<Vector2>();
                    for (float a = 120f; a <= 420.01f; a += 15f) loop.Add(centre + new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)) * (r + h));
                    chains.Add(loop);
                    break;
                }
                case "shelf":
                {
                    float length = Mathf.Max(20f, d.rimHeight);
                    chains.Add(new List<Vector2> { corner, corner + new Vector2(length, 0f), corner + new Vector2(length, 7f) });
                    chains.Add(new List<Vector2> { frontBottom + new Vector2(0f, h), corner + new Vector2(length * .6f, -h) });
                    break;
                }
                default: // hook: down from the front, round, and a little tip up
                {
                    float drop = Mathf.Max(10f, d.rimHeight), r = 14f;
                    var down = frontBottom + new Vector2(0f, -drop);
                    var centre = down + new Vector2(r, 0f);
                    var hook = new List<Vector2> { frontBottom, down };
                    for (float a = 195f; a <= 360.01f; a += 15f) hook.Add(centre + new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)) * r);
                    hook.Add(hook[hook.Count - 1] + new Vector2(0f, r * .7f));
                    chains.Add(hook);
                    break;
                }
            }
            return chains;
        }

        public static Mesh Clip(CoasterDesign d) => Extrude(ClipChains(d), d.rimWidth, d.width, "Clip " + d.name);

        // Round bars along the chains (thickness `bar`), pushed out `length` along z (centred on z = 0).
        private static Mesh Extrude(List<List<Vector2>> chains, float bar, float length, string name)
        {
            float h = bar * .5f;
            Func<Vector2, float> field = p =>
            {
                float best = float.MaxValue;
                foreach (var chain in chains)
                    for (int i = 0; i + 1 < chain.Count; i++) best = Mathf.Min(best, Segment(p, chain[i], chain[i + 1]));
                return best - h;
            };
            Vector2 min = Vector2.one * float.MaxValue, max = Vector2.one * float.MinValue;
            foreach (var chain in chains) foreach (var p in chain) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            min -= Vector2.one * (h + 3f);
            max += Vector2.one * (h + 3f);
            float cell = Mathf.Clamp(bar / 4f, .5f, 1.2f);
            var g = Sample(min, max, cell, field);
            var pieces = new List<List<Vector2>>();
            var segs = new List<Seg>();
            March(g, pieces, segs);
            var v = new List<Vector3>();
            var t = new List<int>();
            float z0 = -length * .5f, z1 = length * .5f;
            Vector3 To3(Vector2 q, float z) => new Vector3(q.x * Mm, q.y * Mm, z * Mm);
            Face(pieces, q => To3(q, z0), Vector3.back, v, t);
            Face(pieces, q => To3(q, z1), Vector3.forward, v, t);
            foreach (var s in segs)
            {
                Vector2 dir = s.b - s.a;
                Quad(v, t, To3(s.a, z0), To3(s.b, z0), To3(s.b, z1), To3(s.a, z1), new Vector3(dir.y, -dir.x, 0f));
            }
            return ToMesh(name, v, t);
        }

        // ------------------------------------------------------------ model (Meshy)

        private static readonly Dictionary<string, Mesh> sources = new Dictionary<string, Mesh>();

        // A stand-in for the scripted Editor preview (no Meshy call): a little stand, clearly not a real model.
        private static Mesh StandIn() => Extrude(new List<List<Vector2>>
        {
            new List<Vector2> { new Vector2(-35f, 3f), new Vector2(35f, 3f) },
            new List<Vector2> { new Vector2(-28f, 3f), new Vector2(-8f, 62f) },
            new List<Vector2> { new Vector2(30f, 3f), new Vector2(30f, 13f) }
        }, 6f, 70f, "Stand-in model");

        public static Mesh Source(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            if (sources.TryGetValue(model, out var cached) && cached != null) return cached;
            Mesh mesh = null;
            try
            {
                if (model.StartsWith("scripted:", StringComparison.Ordinal)) mesh = StandIn();
                else if (File.Exists(model)) mesh = MeshyClient.ParseObj(File.ReadAllText(model));
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Shapes] model: " + exception.Message); }
            if (mesh != null) sources[model] = mesh;
            return mesh;
        }

        // The model at the measured width (its widest side), standing on y = 0, centred; `cut` mm off the
        // bottom flattens a base that would tip.
        public static Mesh Model(CoasterDesign d)
        {
            if (!ModelArrays(d, out var v, out var triangles, out var uv, out _))
                return Extrude(new List<List<Vector2>> { new List<Vector2> { new Vector2(-20f, 10f), new Vector2(20f, 10f) } }, 20f, 40f, "Missing model");
            var mesh = new Mesh { name = "Model " + d.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = v;
            if (uv != null && uv.Length == v.Length) mesh.uv = uv;
            mesh.triangles = triangles;
            // F57: smooth across gentle bends, sharp at real edges (Unity's own normals split at every uv seam).
            mesh.normals = MeshSmooth.VertexNormals(v, triangles);
            mesh.RecalculateBounds();
            return mesh;
        }

        // The model's points at its made size (as Model lays them out), with each triangle's part when the model
        // has parts (F83; else null). A part's shape edits ("longer beak") are done here, on the model's own points
        // before they are sized, so everything built from them (the view, the rebuilt solid, the print) has them.
        public static bool ModelArrays(CoasterDesign d, out Vector3[] v, out int[] triangles, out Vector2[] uv, out int[] triLabels)
        {
            v = null; triangles = null; uv = null; triLabels = null;
            var input = ModelSource(d);
            if (input == null) return false;
            v = ModelCompute(input);
            triangles = input.triangles;
            uv = input.uv;
            triLabels = input.labels;
            RememberLabels(input);
            return true;
        }

        // What ModelCompute needs, fetched on the main thread (the model's mesh, its parts file).
        public sealed class ModelInput
        {
            public Vector3[] source;
            public int[] triangles;
            public Vector2[] uv;
            public Bounds bounds;
            public Vector3 scale;
            public float cut;
            public PartField parts;        // null: the model has no parts of its own
            public int[] labels;           // each triangle's part (worked out by ModelCompute when not known yet)
            public PartEdit[] edits;
            public string labelsKey, builtKey;
        }

        public static ModelInput ModelSource(CoasterDesign d)
        {
            var source = d != null ? Source(d.model) : null;
            if (source == null) return null;
            var b = source.bounds;
            float scale = d.cupDiameter * Mm / Mathf.Max(1e-5f, Mathf.Max(b.size.x, b.size.z));
            // F39/F40: a width, depth or height of its own (set on v2) stretches that side only.
            var s = Vector3.one * scale;
            if (d.sizeW > 0f) s.x = d.sizeW * Mm / Mathf.Max(1e-5f, b.size.x);
            if (d.sizeD > 0f) s.z = d.sizeD * Mm / Mathf.Max(1e-5f, b.size.z);
            if (d.sizeH > 0f) s.y = d.sizeH * Mm / Mathf.Max(1e-5f, b.size.y);
            var input = new ModelInput { source = source.vertices, triangles = source.triangles, uv = source.uv, bounds = b, scale = s, cut = d.cut * Mm, parts = BaseParts(d), edits = d.partEdits };
            if (input.parts != null)
            {
                FreshParts();
                input.labelsKey = d.model + "|" + d.parts;
                input.builtKey = BuiltKey(d);
                if (sourceLabels.TryGetValue(input.labelsKey, out var labels) && labels.Length == input.triangles.Length / 3) input.labels = labels;
            }
            return input;
        }

        // Plain arithmetic (safe in Task.Run): the model's points at its made size, its part edits done.
        public static Vector3[] ModelCompute(ModelInput m)
        {
            var src = m.source;
            float floor = m.bounds.min.y;
            if (m.parts != null)
            {
                if (m.labels == null) m.labels = m.parts.LabelsFor(src, m.triangles);
                if (m.edits != null && m.edits.Length > 0)
                {
                    src = PartShape.Apply(src, m.triangles, m.labels, m.parts.names, m.edits);
                    // (a leg made longer must not go through the desk: the lowest point still stands on it)
                    floor = float.MaxValue;
                    foreach (var p in src) floor = Mathf.Min(floor, p.y);
                }
            }
            var centre = new Vector3(m.bounds.center.x, floor, m.bounds.center.z);
            var v = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var p = Vector3.Scale(src[i] - centre, m.scale);
                if (m.cut > 0f) p.y = Mathf.Max(p.y, m.cut) - m.cut;
                v[i] = p;
            }
            return v;
        }

        // F83/F84: cuts and sculpting dabs are placed 0-1 across the model's box, so they would slide when a part
        // edit ("longer beak") moves the surface or grows the box. Each goes with the model point nearest to it:
        // the same move that point makes, then its place in the new box. Plain arithmetic (safe in Task.Run);
        // `before` and `after` are the same model (ModelSource) with the old and the new part edits.
        public static SolidRebuild.Op[] CarryOps(ModelInput before, ModelInput after, SolidRebuild.Op[] ops)
        {
            if (ops == null || ops.Length == 0 || before == null || after == null) return ops;
            var was = ModelCompute(before);
            var now = ModelCompute(after);
            if (was.Length == 0 || was.Length != now.Length) return ops;
            Vector3 lo0 = was[0], hi0 = was[0], lo1 = now[0], hi1 = now[0];
            for (int i = 1; i < was.Length; i++) { lo0 = Vector3.Min(lo0, was[i]); hi0 = Vector3.Max(hi0, was[i]); lo1 = Vector3.Min(lo1, now[i]); hi1 = Vector3.Max(hi1, now[i]); }
            var result = new SolidRebuild.Op[ops.Length];
            for (int k = 0; k < ops.Length; k++)
            {
                var op = ops[k]?.Copy();
                result[k] = op;
                if (op?.at == null || op.at.Length < 3) continue;
                Vector3 p = op.At(lo0, hi0 - lo0);
                int nearest = 0;
                float best = float.MaxValue;
                for (int i = 0; i < was.Length; i++) { float d = (was[i] - p).sqrMagnitude; if (d < best) { best = d; nearest = i; } }
                op.at = SolidRebuild.Op.Rel(now[nearest] + (p - was[nearest]), lo1, hi1 - lo1);
            }
            return result;
        }

        // Main thread, after ModelCompute: what it worked out is kept for the next ask.
        public static void RememberLabels(ModelInput m)
        {
            if (m?.labelsKey != null && m.labels != null) sourceLabels[m.labelsKey] = m.labels;
        }

        public static void RememberBuilt(ModelInput m, PartField built)
        {
            if (m?.builtKey == null || built == null) return;
            if (builtParts.Count > 8) builtParts.Clear();
            builtParts[m.builtKey] = built;
        }

        // ------------------------------------------------------------ a model's own parts (F83: beak, wing, handle...)

        private static readonly Dictionary<string, int[]> sourceLabels = new Dictionary<string, int[]>();
        private static readonly Dictionary<string, PartField> builtParts = new Dictionary<string, PartField>();
        private static int partsSeen = -1;

        // The parts found for this model (PartSegmenter), or null: it then has only top, side and bottom.
        public static PartField BaseParts(CoasterDesign d) =>
            d != null && d.kind == "model" ? PartField.Get(PartField.KeyFor(d.model, d.parts)) : null;

        private static void FreshParts()
        {
            if (partsSeen == PartField.Changes) return;
            partsSeen = PartField.Changes;
            sourceLabels.Clear();
            builtParts.Clear();
        }

        private static string BuiltKey(CoasterDesign d)
        {
            var key = new System.Text.StringBuilder().Append(d.model).Append('|').Append(d.parts).Append('|').Append(d.cupDiameter).Append('|')
                .Append(d.sizeW).Append('|').Append(d.sizeD).Append('|').Append(d.sizeH).Append('|').Append(d.cut);
            if (d.partEdits != null) foreach (var e in d.partEdits) if (e != null) key.Append('|').Append(e.part).Append(':').Append(e.kind).Append(':').Append(e.factor);
            return key.ToString();
        }

        // The parts laid over the model as it is made now (its sizes, its part edits): what the brush, the rebuilt
        // solid and the print's colours ask.
        public static PartField BuiltParts(CoasterDesign d)
        {
            var parts = BaseParts(d);
            if (parts == null) return null;
            FreshParts();
            string k = BuiltKey(d);
            if (builtParts.TryGetValue(k, out var built)) return built;
            if (!ModelArrays(d, out var v, out var t, out _, out var labels) || labels == null) return null;
            if (builtParts.Count > 8) builtParts.Clear();
            built = PartField.Over(parts, v, t, labels);
            builtParts[k] = built;
            return built;
        }

        // How much of the model's footprint touches the desk (0-1): low means it would tip over.
        public static float BaseContact(CoasterDesign d)
        {
            var source = Source(d.model);
            if (source == null) return 1f;
            var b = source.bounds;
            float band = b.min.y + b.size.y * .02f;
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in source.vertices)
            {
                if (p.y > band) continue;
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z);
            }
            if (x1 < x0) return 0f;
            return Mathf.Clamp01((x1 - x0) * (z1 - z0) / Mathf.Max(1e-8f, b.size.x * b.size.z));
        }

        // ------------------------------------------------------------ parts (for the brush and the 3MF colours)

        public static string[] PartsOf(CoasterDesign d) =>
            d == null ? PaintableThing.MadeParts : d.kind == "tray" ? TrayParts : d.kind == "clip" ? ClipParts : d.kind == "model" ? (BuiltParts(d)?.names ?? ModelParts) : PaintableThing.MadeParts;

        public static Func<Vector3, Vector3, Bounds, int> ClassifierOf(CoasterDesign d)
        {
            switch (d?.kind)
            {
                case "tray":
                {
                    var pockets = Pockets(d);
                    var pocket = PocketField(pockets, d.clearance * .5f);
                    var outer = OuterField(d, pockets, pocket, Mathf.Max(4f, d.rimWidth));
                    float middle = (d.baseThickness + d.Height) * .5f * Mm;
                    return (n, c, b) =>
                    {
                        if (n.y > .6f) return c.y > middle ? 0 : 2;
                        if (n.y < -.6f) return 3;
                        var q = new Vector2(c.x / Mm, c.z / Mm);
                        return Mathf.Abs(outer(q)) <= Mathf.Abs(pocket(q)) ? 4 : 1;
                    };
                }
                case "clip":
                {
                    float low = -d.cupDiameter - d.clearance * .5f - d.rimWidth - 1f;
                    return (n, c, b) =>
                    {
                        if (Mathf.Abs(n.z) > .6f) return 4;
                        if (c.y / Mm < low) return 0;
                        if (n.y > .6f) return 1;
                        if (n.y < -.6f) return 5;
                        return n.x > .3f ? 2 : 3;
                    };
                }
                case "model":
                {
                    // F83: its own parts when they were found (beak, wing...), else top, side and bottom.
                    var parts = BuiltParts(d);
                    if (parts != null) return parts.Classifier();
                    return (n, c, b) => n.y > .5f ? 0 : n.y < -.5f ? 2 : 1;
                }
                default:
                    return PaintableThing.MadePart;
            }
        }

        // The widest size on the desk (mm): for spacing versions apart.
        public static float Footprint(CoasterDesign d)
        {
            if (d == null) return 90f;
            switch (d.kind)
            {
                case "tray":
                {
                    PocketBounds(Pockets(d), out var min, out var max);
                    return Mathf.Max(max.x - min.x, max.y - min.y) + 2f * (d.clearance + Mathf.Max(4f, d.rimWidth));
                }
                case "clip": return d.width + 10f;
                case "model": return d.cupDiameter;
                default: return d.OuterDiameter;
            }
        }

        // ------------------------------------------------------------ 3MF with a colour per part

        // `parts` is the made thing's mesh split into parts (one submesh each, in metres); `scale` maps it
        // to metres. Millimetres, z up, the same mapping as the STL.
        public static byte[] ThreeMf(Mesh parts, float scale, IList<string> names, IList<Color> colours, string title)
        {
            var vertices = parts.vertices;
            var key = new Dictionary<long, int>(SolidRebuild.LongKeys.Instance); // F57: Vector3Int keys hashed badly on big rebuilt meshes
            var points = new List<Vector3>();
            var tris = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            int Index(Vector3 p)
            {
                var q = new Vector3(p.x, p.z, p.y) * scale * 1000f;
                long k = SolidRebuild.PackKey(new Vector3(q.x * 1000f + 0x80000, q.y * 1000f + 0x80000, q.z * 1000f + 0x80000));
                if (key.TryGetValue(k, out int i)) return i;
                key[k] = points.Count;
                points.Add(q);
                return points.Count - 1;
            }
            int count = 0;
            for (int s = 0; s < parts.subMeshCount; s++)
            {
                var t = parts.GetTriangles(s);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = Index(vertices[t[i]]), b = Index(vertices[t[i + 1]]), c = Index(vertices[t[i + 2]]);
                    if (a == b || b == c || a == c) continue;
                    // Swapping y and z mirrors the space: the winding flips to stay outward.
                    tris.Append("<triangle v1=\"").Append(a).Append("\" v2=\"").Append(c).Append("\" v3=\"").Append(b)
                        .Append("\" pid=\"1\" p1=\"").Append(Mathf.Clamp(s, 0, Mathf.Max(0, colours.Count - 1))).Append("\"/>\n");
                    count++;
                }
            }
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            xml.Append("<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">\n");
            xml.Append("<metadata name=\"Title\">").Append(System.Security.SecurityElement.Escape(title ?? "Roomsmiths")).Append("</metadata>\n");
            xml.Append("<metadata name=\"Application\">Roomsmiths</metadata>\n<resources>\n<basematerials id=\"1\">\n");
            for (int i = 0; i < colours.Count; i++)
                xml.Append("<base name=\"").Append(System.Security.SecurityElement.Escape(i < names.Count ? names[i] : "part " + i))
                   .Append("\" displaycolor=\"#").Append(ColorUtility.ToHtmlStringRGB(colours[i])).Append("FF\"/>\n");
            xml.Append("</basematerials>\n<object id=\"2\" type=\"model\" pid=\"1\" pindex=\"0\">\n<mesh>\n<vertices>\n");
            foreach (var p in points)
                xml.Append("<vertex x=\"").Append(p.x.ToString("0.###", ci)).Append("\" y=\"").Append(p.y.ToString("0.###", ci))
                   .Append("\" z=\"").Append(p.z.ToString("0.###", ci)).Append("\"/>\n");
            xml.Append("</vertices>\n<triangles>\n").Append(tris).Append("</triangles>\n</mesh>\n</object>\n</resources>\n");
            xml.Append("<build>\n<item objectid=\"2\"/>\n</build>\n</model>\n");
            const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                                 "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                                 "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>";
            const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                                "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>";
            Debug.Log("[Windups Shapes] 3MF: " + points.Count + " vertices, " + count + " triangles, " + colours.Count + " colours");
            return Zip(new List<(string, byte[])>
            {
                ("[Content_Types].xml", Encoding.UTF8.GetBytes(types)),
                ("_rels/.rels", Encoding.UTF8.GetBytes(rels)),
                ("3D/3dmodel.model", Encoding.UTF8.GetBytes(xml.ToString()))
            });
        }

        // A plain (stored, uncompressed) zip: what a 3MF package is.
        public static byte[] Zip(IList<(string name, byte[] data)> files)
        {
            using (var stream = new MemoryStream())
            {
                // 3MF model XML compresses well; storing it verbatim exceeded the web upload limit.
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    foreach (var (name, data) in files)
                    {
                        var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using (var output = entry.Open()) output.Write(data, 0, data.Length);
                    }
                return stream.ToArray();
            }
        }
    }
}
