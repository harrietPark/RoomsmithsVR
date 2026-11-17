using System;
using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F57 (2026-11-13, the user: "can we take what we made to a real 3D printer or a 3D program - is it made that
    // well?"): Meshy's mesh is a surface with holes (the one saved on disk: 408 triangles, 32 open edges), and the
    // app wrote it to the STL as it was. Here a made model becomes a real solid:
    //  1. what is inside: rays along x, y and z through every voxel column (0.5-2 mm voxels), counting crossings;
    //     a column whose count is odd ran through a hole and does not vote, and each voxel goes with the majority,
    //  2. clean-up: voids sealed inside are filled, crumbs (pieces under 5% of the biggest) dropped,
    //  3. a signed distance field: the exact distance to Meshy's triangles next to the surface, carried inward
    //     and outward by a chamfer transform (holes Meshy left open get a surface where the voxels end),
    //  4. F59 operations on the field: hollow with open top or bottom, holes, cable notches, sculpting dabs,
    //  5. surface nets: one vertex per surface cell, one quad per crossing edge - closed by construction, with
    //     normals from the field's gradient.
    // Everything is plain C# on arrays (no Unity objects), so it runs in Task.Run (FPS: never below 60).
    public static class SolidRebuild
    {
        public const int MaxVoxels = 6_000_000;
        public const float PrintCellMax = .0008f, PrintCellMin = .0004f, DisplayCellMin = .0012f;
        public static float SculptRoom = .03f; // metres of room around a sculpting view for clay pulled out

        // ------------------------------------------------------------ operations (F59)

        // Where and how big, in the made thing's own space: `at` 0-1 across its bounds (so a feature stays in place
        // when the size changes), sizes in mm.
        [Serializable] public sealed class Op
        {
            public string kind;      // hollow | open | hole | notch | dab
            public float a, b;       // hollow: wall; hole: diameter, depth; notch: width, height; dab: radius, strength
            public float[] at;       // 0-1 across the bounds (x, y, z)
            public float[] dir;      // hole / notch axis (unit, own space)
            public string side;      // open: top | bottom; dab: push | pull | smooth
            public int stroke;       // dab: the stroke it belongs to (undo takes a whole stroke)

            public Op Copy() => new Op { kind = kind, a = a, b = b, at = at != null ? (float[])at.Clone() : null, dir = dir != null ? (float[])dir.Clone() : null, side = side, stroke = stroke };
            public Vector3 At(Vector3 min, Vector3 size) => at != null && at.Length >= 3 ? min + Vector3.Scale(size, new Vector3(at[0], at[1], at[2])) : min + size * .5f;
            // (a method, not a property: the design is saved with Newtonsoft, which would walk a Vector3 property)
            public Vector3 Direction() => dir != null && dir.Length >= 3 ? new Vector3(dir[0], dir[1], dir[2]).normalized : Vector3.up;
            public static float[] Rel(Vector3 p, Vector3 min, Vector3 size) =>
                new[] { (p.x - min.x) / Mathf.Max(size.x, 1e-6f), (p.y - min.y) / Mathf.Max(size.y, 1e-6f), (p.z - min.z) / Mathf.Max(size.z, 1e-6f) };
        }

        // ------------------------------------------------------------ the field

        public sealed class Field
        {
            public int nx, ny, nz;
            public float cell;            // metres
            public Vector3 origin;        // centre of voxel (0, 0, 0)
            public float[] d;             // signed distance in metres, negative inside
            public Vector3 sourceMin, sourceSize; // the source's bounds (operations are relative to them)
            public float limit;           // distances are clamped to +-limit

            public int Index(int x, int y, int z) => (z * ny + y) * nx + x;
            public Vector3 Centre(int x, int y, int z) => origin + new Vector3(x, y, z) * cell;

            public float Sample(Vector3 p)
            {
                Vector3 g = (p - origin) / cell;
                int x = Mathf.FloorToInt(g.x), y = Mathf.FloorToInt(g.y), z = Mathf.FloorToInt(g.z);
                if (x < 0 || y < 0 || z < 0 || x >= nx - 1 || y >= ny - 1 || z >= nz - 1) return limit;
                float fx = g.x - x, fy = g.y - y, fz = g.z - z;
                int i = Index(x, y, z), sx = 1, sy = nx, sz = nx * ny;
                float c00 = Mathf.Lerp(d[i], d[i + sx], fx), c10 = Mathf.Lerp(d[i + sy], d[i + sy + sx], fx);
                float c01 = Mathf.Lerp(d[i + sz], d[i + sz + sx], fx), c11 = Mathf.Lerp(d[i + sz + sy], d[i + sz + sy + sx], fx);
                return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
            }

            public Vector3 Gradient(Vector3 p)
            {
                float h = cell * .5f;
                var g = new Vector3(Sample(p + new Vector3(h, 0, 0)) - Sample(p - new Vector3(h, 0, 0)),
                                    Sample(p + new Vector3(0, h, 0)) - Sample(p - new Vector3(0, h, 0)),
                                    Sample(p + new Vector3(0, 0, h)) - Sample(p - new Vector3(0, 0, h)));
                float m = g.magnitude;
                return m > 1e-12f ? g / m : Vector3.up;
            }

            public Field Clone() => new Field { nx = nx, ny = ny, nz = nz, cell = cell, origin = origin, d = (float[])d.Clone(), sourceMin = sourceMin, sourceSize = sourceSize, limit = limit };
        }

        public sealed class Stats
        {
            public int sourceTriangles, sourceOpenEdges, voxels, unsureVoxels, voidsFilled, crumbsRemoved, pieces, cornersJoined, innerShells, enclosedFilled;
            public float cellMm;
            public long milliseconds;
        }

        public sealed class Result
        {
            public Vector3[] vertices;   // metres, the made thing's own space
            public Vector3[] normals;
            public int[] triangles;
            public Color32[] colours;     // per vertex (from the model's texture), or null
            public int openEdges, nonManifoldEdges;
            public float volumeCm3;
            public Stats stats;
            public bool Closed => openEdges == 0;
        }

        // A voxel size for a thing of this size: about 150 voxels across for printing (0.4-0.8 mm), coarser for the view.
        public static float CellFor(Vector3 size, bool forPrint, float room = 0f)
        {
            float big = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            size += Vector3.one * (2f * room); // (the budget counts the room around it too)
            float cell = forPrint ? Mathf.Clamp(big / 150f, PrintCellMin, PrintCellMax) : Mathf.Max(big / 72f, DisplayCellMin);
            // never more voxels than MaxVoxels
            for (int i = 0; i < 20; i++)
            {
                long n = (long)(size.x / cell + 8) * (long)(size.y / cell + 8) * (long)(size.z / cell + 8);
                if (n <= MaxVoxels) break;
                cell *= 1.15f;
            }
            return cell;
        }

        // ------------------------------------------------------------ 1-3: from triangles to a field

        public static Field Voxelize(Vector3[] v, int[] t, float cell, Stats stats = null, float room = 0f)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            t = WithoutInnerShells(v, t, out int shells);
            if (stats != null) stats.innerShells = shells;
            Vector3 lo = v[0], hi = v[0];
            for (int i = 1; i < v.Length; i++) { lo = Vector3.Min(lo, v[i]); hi = Vector3.Max(hi, v[i]); }
            Vector3 size = hi - lo;
            // 4 voxels around the shape, plus `room` (metres) where sculpting can pull clay out
            int pad = 4 + Mathf.CeilToInt(Mathf.Max(0f, room) / cell);
            var f = new Field
            {
                cell = cell, sourceMin = lo, sourceSize = size,
                nx = Mathf.CeilToInt(size.x / cell) + 2 * pad + 1, ny = Mathf.CeilToInt(size.y / cell) + 2 * pad + 1, nz = Mathf.CeilToInt(size.z / cell) + 2 * pad + 1,
                origin = lo - Vector3.one * (pad * cell), limit = cell * 8f
            };
            int count = f.nx * f.ny * f.nz;
            var inside = new byte[count];
            var valid = new byte[count];
            for (int axis = 0; axis < 3; axis++) Parity(v, t, f, axis, inside, valid);

            // Each voxel goes with the majority of the axes whose rays were whole; 2 = not sure yet.
            var occ = new byte[count];
            int unsure = 0;
            for (int i = 0; i < count; i++)
            {
                if (valid[i] == 0 || inside[i] * 2 == valid[i]) { occ[i] = 2; unsure++; }
                else occ[i] = (byte)(inside[i] * 2 > valid[i] ? 1 : 0);
            }
            ResolveUnsure(f, occ);
            int enclosed = FillEnclosed(v, t, f, occ);
            if (stats != null) stats.enclosedFilled = enclosed;
            int voids = FillVoids(f, occ);
            int crumbs = DropCrumbs(f, occ, out int pieces);
            f.d = Distances(v, t, f, occ);
            if (stats != null)
            {
                stats.sourceTriangles = t.Length / 3;
                stats.sourceOpenEdges = OpenEdges(v, t, out _);
                stats.voxels = count; stats.unsureVoxels = unsure; stats.voidsFilled = voids; stats.crumbsRemoved = crumbs; stats.pieces = pieces;
                stats.cellMm = cell * 1000f;
                stats.milliseconds = watch.ElapsedMilliseconds;
            }
            return f;
        }

        // 2026-11-15 (Quest: "I said sculpt it and the horse suddenly turned really weird"): Meshy can make a model as a
        // hollow skin - its outside plus an inside-out copy about 1.4 mm within it (the horse: +578 cm3 outside, -481 cm3
        // inside). Voxels coarser than that wall (3 mm for the sculpting view) found only crumbs of it, and the view
        // fell apart into 66 pieces. A sealed inner skin is a void, which FillVoids fills anyway: its triangles are left
        // out, so the thing is solid (also for printing). Kept: a skin that is not inside-out, or not inside another one,
        // and every open vessel (its inside is joined to its outside at the rim: one piece).
        public static int[] WithoutInnerShells(Vector3[] v, int[] t, out int dropped)
        {
            dropped = 0;
            int tris = t.Length / 3;
            if (tris < 8) return t;
            var id = WeldIds(v);
            int ids = 0;
            for (int i = 0; i < id.Length; i++) ids = Math.Max(ids, id[i] + 1);
            var parent = new int[ids];
            for (int i = 0; i < ids; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = Find(id[t[k]]), b = Find(id[t[k + 1]]), c = Find(id[t[k + 2]]);
                if (b != a) parent[b] = a;
                if (c != a) parent[Find(c)] = a;
            }
            // Per piece: signed volume (about the middle, in doubles) and bounds.
            Vector3 mid = Vector3.zero;
            foreach (var p in v) mid += p;
            mid /= Mathf.Max(1, v.Length);
            var volume = new Dictionary<int, double>();
            var lo = new Dictionary<int, Vector3>();
            var hi = new Dictionary<int, Vector3>();
            var root = new int[tris];
            for (int k = 0, n = 0; k + 2 < t.Length; k += 3, n++)
            {
                int r = Find(id[t[k]]);
                root[n] = r;
                Vector3 p0 = v[t[k]] - mid, p1 = v[t[k + 1]] - mid, p2 = v[t[k + 2]] - mid;
                double det = (double)p0.x * ((double)p1.y * p2.z - (double)p1.z * p2.y) - (double)p0.y * ((double)p1.x * p2.z - (double)p1.z * p2.x) + (double)p0.z * ((double)p1.x * p2.y - (double)p1.y * p2.x);
                volume[r] = (volume.TryGetValue(r, out double sum) ? sum : 0.0) + det / 6.0;
                Vector3 l = Vector3.Min(p0, Vector3.Min(p1, p2)), h = Vector3.Max(p0, Vector3.Max(p1, p2));
                lo[r] = lo.TryGetValue(r, out var ol) ? Vector3.Min(ol, l) : l;
                hi[r] = hi.TryGetValue(r, out var oh) ? Vector3.Max(oh, h) : h;
            }
            if (volume.Count < 2) return t;
            var inner = new HashSet<int>();
            foreach (var pair in volume)
            {
                if (pair.Value >= 0.0) continue;
                Vector3 bl = lo[pair.Key], bh = hi[pair.Key];
                float tol = Mathf.Max(bh.x - bl.x, Mathf.Max(bh.y - bl.y, bh.z - bl.z)) * .01f;
                foreach (var outer in volume)
                {
                    if (outer.Key == pair.Key || outer.Value <= -pair.Value) continue;
                    Vector3 ol = lo[outer.Key], oh = hi[outer.Key];
                    if (bl.x >= ol.x - tol && bl.y >= ol.y - tol && bl.z >= ol.z - tol && bh.x <= oh.x + tol && bh.y <= oh.y + tol && bh.z <= oh.z + tol)
                    { inner.Add(pair.Key); break; }
                }
            }
            if (inner.Count == 0) return t;
            dropped = inner.Count;
            var kept = new List<int>(t.Length);
            for (int k = 0, n = 0; k + 2 < t.Length; k += 3, n++)
                if (!inner.Contains(root[n])) { kept.Add(t[k]); kept.Add(t[k + 1]); kept.Add(t[k + 2]); }
            return kept.ToArray();
        }

        // 2026-11-15 (second Quest try: "sculpting again - the horse is all gone"): the next Meshy horse was a hollow
        // skin too, but with openings inside it (four cut cross-sections in the body, 186 open edges), so its hollow was
        // not sealed and the ray vote kept only the thin walls: the view lost the body (4 pieces, 669 crumbs) and the
        // print would have been a 99 cm3 shell. Whatever the outside cannot reach is inside: the voxels the triangles
        // pass through are walls, the outside floods in from the grid's border through the rest (face neighbours, so a
        // wall of touching voxels holds it), and every voxel the flood never reached is filled. Inner skins, cut
        // cross-sections and overlapping patches inside no longer matter; a real opening to the outside (a cup's mouth,
        // a handle's hole) lets the flood in and stays open. The walls themselves keep the ray vote.
        private static int FillEnclosed(Vector3[] v, int[] t, Field f, byte[] occ)
        {
            int count = occ.Length, nx = f.nx, ny = f.ny, nz = f.nz;
            var wall = new bool[count];
            float step = f.cell * .5f, inv = 1f / f.cell;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                Vector3 p0 = v[t[k]], p1 = v[t[k + 1]], p2 = v[t[k + 2]];
                float longest = Mathf.Max((p1 - p0).magnitude, Mathf.Max((p2 - p1).magnitude, (p0 - p2).magnitude));
                int n = Mathf.Clamp(Mathf.CeilToInt(longest / step), 1, 400);
                Vector3 e1 = (p1 - p0) / n, e2 = (p2 - p0) / n;
                for (int i = 0; i <= n; i++)
                    for (int j = 0; j <= n - i; j++)
                    {
                        Vector3 g = (p0 + e1 * i + e2 * j - f.origin) * inv;
                        int x = Mathf.RoundToInt(g.x), y = Mathf.RoundToInt(g.y), z = Mathf.RoundToInt(g.z);
                        if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz) continue;
                        wall[(z * ny + y) * nx + x] = true;
                    }
            }
            // Gaps in the skin up to about CloseGapMm wide (seams Meshy left a little open) are closed first: the walls
            // grow `r` voxels before the flood (fine print voxels leaked through them), and the flooded outside grows
            // back the same `r` after it, so the outer surface stays where it was.
            int r = Mathf.FloorToInt(CloseGapMm * .001f / f.cell);
            var block = wall;
            if (r > 0) { block = (bool[])wall.Clone(); for (int pass = 0; pass < r; pass++) Grow(block, nx, ny, nz); }
            var reached = new bool[count];
            var stack = new Stack<int>();
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (x != 0 && y != 0 && z != 0 && x != nx - 1 && y != ny - 1 && z != nz - 1) continue;
                        int i = (z * ny + y) * nx + x;
                        if (!block[i] && !reached[i]) { reached[i] = true; stack.Push(i); }
                    }
            int sy = nx, sz = nx * ny;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % nx, y = (i / nx) % ny, z = i / sz;
                if (x > 0) Reach(i - 1);
                if (x < nx - 1) Reach(i + 1);
                if (y > 0) Reach(i - sy);
                if (y < ny - 1) Reach(i + sy);
                if (z > 0) Reach(i - sz);
                if (z < nz - 1) Reach(i + sz);
            }
            void Reach(int j) { if (!block[j] && !reached[j]) { reached[j] = true; stack.Push(j); } }
            for (int pass = 0; pass < r; pass++) Grow(reached, nx, ny, nz);
            int filled = 0;
            for (int i = 0; i < count; i++)
                if (!reached[i] && !wall[i] && occ[i] == 0) { occ[i] = 1; filled++; }
            // The walls: one buried in the solid (no outside next to it: an inner skin, a cut cross-section) is solid; one
            // on the outer skin is inside when its middle lies behind the nearest triangle (the ray vote was noisy there
            // on a skin full of seams: the sculpting view came out lumpy). The skin's facing is checked once by its volume.
            double volume = 0.0;
            for (int k = 0; k + 2 < t.Length; k += 3) volume += Vector3.Dot(v[t[k]] - f.origin, Vector3.Cross(v[t[k + 1]] - f.origin, v[t[k + 2]] - f.origin));
            float facing = volume >= 0.0 ? 1f : -1f;
            var buckets = new TriangleBuckets(v, t, f.origin, f.cell * 4f, f.cell * 1.5f);
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        int i = (z * ny + y) * nx + x;
                        if (!wall[i]) continue;
                        bool outside = (x > 0 && reached[i - 1]) || (x < nx - 1 && reached[i + 1]) || (y > 0 && reached[i - sy]) || (y < ny - 1 && reached[i + sy]) ||
                                       (z > 0 && reached[i - sz]) || (z < nz - 1 && reached[i + sz]);
                        byte was = occ[i];
                        if (!outside) occ[i] = 1;
                        else
                        {
                            Vector3 p = f.Centre(x, y, z);
                            if (buckets.Nearest(p, f.cell * 1.5f, out int k, out Vector3 w) == float.PositiveInfinity) continue;
                            Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                            Vector3 n = Vector3.Cross(b - a, c - a);
                            Vector3 q = a * w.x + b * w.y + c * w.z;
                            occ[i] = (byte)(Vector3.Dot(p - q, n) * facing < 0f ? 1 : 0);
                        }
                        if (occ[i] == 1 && was == 0) filled++;
                    }
            return filled;
        }

        public static float CloseGapMm = 1.6f;

        // One voxel more of what is set, to its face neighbours.
        private static void Grow(bool[] set, int nx, int ny, int nz)
        {
            var was = (bool[])set.Clone();
            int sy = nx, sz = nx * ny;
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        int i = (z * ny + y) * nx + x;
                        if (was[i]) continue;
                        if ((x > 0 && was[i - 1]) || (x < nx - 1 && was[i + 1]) || (y > 0 && was[i - sy]) || (y < ny - 1 && was[i + sy]) ||
                            (z > 0 && was[i - sz]) || (z < nz - 1 && was[i + sz])) set[i] = true;
                    }
        }

        // Rays along one axis through the centres of every column of voxels; crossings counted per column.
        private static void Parity(Vector3[] v, int[] t, Field f, int axis, byte[] inside, byte[] valid)
        {
            int ua = (axis + 1) % 3, va = (axis + 2) % 3;
            int[] n = { f.nx, f.ny, f.nz };
            int nu = n[ua], nv = n[va], na = n[axis];
            float ou = f.origin[ua] + f.cell * .0137f, ov = f.origin[va] + f.cell * .0291f, oa = f.origin[axis]; // a small offset: rays never run exactly along an edge
            var columns = new List<float>[nu * nv];
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                Vector3 p0 = v[t[k]], p1 = v[t[k + 1]], p2 = v[t[k + 2]];
                float u0 = p0[ua], u1 = p1[ua], u2 = p2[ua], v0 = p0[va], v1 = p1[va], v2 = p2[va];
                float area = (u1 - u0) * (v2 - v0) - (u2 - u0) * (v1 - v0);
                if (Mathf.Abs(area) < 1e-14f) continue;
                int iu0 = Mathf.Max(0, Mathf.CeilToInt((Mathf.Min(u0, Mathf.Min(u1, u2)) - ou) / f.cell));
                int iu1 = Mathf.Min(nu - 1, Mathf.FloorToInt((Mathf.Max(u0, Mathf.Max(u1, u2)) - ou) / f.cell));
                int iv0 = Mathf.Max(0, Mathf.CeilToInt((Mathf.Min(v0, Mathf.Min(v1, v2)) - ov) / f.cell));
                int iv1 = Mathf.Min(nv - 1, Mathf.FloorToInt((Mathf.Max(v0, Mathf.Max(v1, v2)) - ov) / f.cell));
                for (int iu = iu0; iu <= iu1; iu++)
                    for (int iv = iv0; iv <= iv1; iv++)
                    {
                        float cu = ou + iu * f.cell, cv = ov + iv * f.cell;
                        float w0 = ((u1 - cu) * (v2 - cv) - (u2 - cu) * (v1 - cv)) / area;
                        float w1 = ((u2 - cu) * (v0 - cv) - (u0 - cu) * (v2 - cv)) / area;
                        float w2 = 1f - w0 - w1;
                        if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                        float a = w0 * p0[axis] + w1 * p1[axis] + w2 * p2[axis];
                        int c = iv * nu + iu;
                        (columns[c] ??= new List<float>(4)).Add(a);
                    }
            }
            for (int iv = 0; iv < nv; iv++)
                for (int iu = 0; iu < nu; iu++)
                {
                    var list = columns[iv * nu + iu];
                    int hits = list?.Count ?? 0;
                    if ((hits & 1) == 1) continue; // ran through a hole: no vote
                    if (hits > 1) list.Sort();
                    int next = 0;
                    bool isInside = false;
                    for (int ia = 0; ia < na; ia++)
                    {
                        float ca = oa + ia * f.cell;
                        while (next < hits && list[next] < ca) { isInside = !isInside; next++; }
                        int x = axis == 0 ? ia : ua == 0 ? iu : iv;
                        int y = axis == 1 ? ia : ua == 1 ? iu : iv;
                        int z = axis == 2 ? ia : ua == 2 ? iu : iv;
                        int i = f.Index(x, y, z);
                        valid[i]++;
                        if (isInside) inside[i]++;
                    }
                }
        }

        private static void ResolveUnsure(Field f, byte[] occ)
        {
            for (int pass = 0; pass < 6; pass++)
            {
                bool any = false;
                for (int z = 1; z < f.nz - 1; z++)
                    for (int y = 1; y < f.ny - 1; y++)
                        for (int x = 1; x < f.nx - 1; x++)
                        {
                            int i = f.Index(x, y, z);
                            if (occ[i] != 2) continue;
                            int ins = 0, outs = 0;
                            void Look(int j) { if (occ[j] == 1) ins++; else if (occ[j] == 0) outs++; }
                            Look(i - 1); Look(i + 1); Look(i - f.nx); Look(i + f.nx); Look(i - f.nx * f.ny); Look(i + f.nx * f.ny);
                            if (ins + outs == 0) { any = true; continue; }
                            occ[i] = (byte)(ins > outs ? 1 : 0);
                        }
                if (!any) break;
            }
            for (int i = 0; i < occ.Length; i++) if (occ[i] == 2) occ[i] = 0;
        }

        // Empty voxels the outside cannot reach become solid (a sealed void prints badly and fools the slicer).
        private static int FillVoids(Field f, byte[] occ)
        {
            var reached = new bool[occ.Length];
            var stack = new Stack<int>();
            for (int z = 0; z < f.nz; z++)
                for (int y = 0; y < f.ny; y++)
                    for (int x = 0; x < f.nx; x++)
                        if (x == 0 || y == 0 || z == 0 || x == f.nx - 1 || y == f.ny - 1 || z == f.nz - 1)
                        {
                            int i = f.Index(x, y, z);
                            if (occ[i] == 0 && !reached[i]) { reached[i] = true; stack.Push(i); }
                        }
            Flood(f, occ, reached, stack, 0);
            int filled = 0;
            for (int i = 0; i < occ.Length; i++) if (occ[i] == 0 && !reached[i]) { occ[i] = 1; filled++; }
            return filled;
        }

        private static void Flood(Field f, byte[] occ, bool[] seen, Stack<int> stack, byte value, List<int> into = null)
        {
            int sx = 1, sy = f.nx, sz = f.nx * f.ny, count = occ.Length;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                into?.Add(i);
                int x = i % f.nx, y = (i / f.nx) % f.ny, z = i / sz;
                void Go(int j) { if (j >= 0 && j < count && !seen[j] && occ[j] == value) { seen[j] = true; stack.Push(j); } }
                if (x > 0) Go(i - sx);
                if (x < f.nx - 1) Go(i + sx);
                if (y > 0) Go(i - sy);
                if (y < f.ny - 1) Go(i + sy);
                if (z > 0) Go(i - sz);
                if (z < f.nz - 1) Go(i + sz);
            }
        }

        // Pieces under 5% of the biggest piece are crumbs of a broken surface: dropped.
        private static int DropCrumbs(Field f, byte[] occ, out int pieces)
        {
            var seen = new bool[occ.Length];
            var stack = new Stack<int>();
            var parts = new List<List<int>>();
            for (int i = 0; i < occ.Length; i++)
            {
                if (occ[i] != 1 || seen[i]) continue;
                var part = new List<int>();
                seen[i] = true;
                stack.Push(i);
                Flood(f, occ, seen, stack, 1, part);
                parts.Add(part);
            }
            int biggest = 0;
            foreach (var p in parts) biggest = Mathf.Max(biggest, p.Count);
            int removed = 0;
            pieces = 0;
            foreach (var p in parts)
            {
                if (p.Count >= biggest * .05f) { pieces++; continue; }
                foreach (int i in p) occ[i] = 0;
                removed += p.Count;
            }
            return removed;
        }

        // Signed distances: exact to the source triangles next to the surface, then a chamfer transform.
        private static float[] Distances(Vector3[] v, int[] t, Field f, byte[] occ)
        {
            int count = occ.Length, sx = 1, sy = f.nx, sz = f.nx * f.ny;
            var u = new float[count];
            for (int i = 0; i < count; i++) u[i] = f.limit;
            var buckets = new TriangleBuckets(v, t, f.origin, f.cell * 4f, f.cell * 1.5f);
            for (int z = 1; z < f.nz - 1; z++)
                for (int y = 1; y < f.ny - 1; y++)
                    for (int x = 1; x < f.nx - 1; x++)
                    {
                        int i = f.Index(x, y, z);
                        byte o = occ[i];
                        if (occ[i - sx] == o && occ[i + sx] == o && occ[i - sy] == o && occ[i + sy] == o && occ[i - sz] == o && occ[i + sz] == o) continue;
                        Vector3 p = f.Centre(x, y, z);
                        float best = buckets.Nearest(p, f.cell * 1.5f, out _, out _);
                        // Next to the surface the voxels follow Meshy's own triangles; where Meshy left a hole there are
                        // none near, and the surface lies halfway between the voxels.
                        u[i] = best <= f.cell * 1.5f ? Mathf.Min(best, f.cell) : f.cell * .5f;
                        if (u[i] < 1e-6f) u[i] = 1e-6f;
                    }
            Chamfer(f, u);
            var d = new float[count];
            for (int i = 0; i < count; i++) d[i] = occ[i] == 1 ? -u[i] : u[i];
            return d;
        }

        // Distances carried from the surface voxels to the rest (3x3x3 chamfer, forward then backward).
        private static void Chamfer(Field f, float[] u)
        {
            float a = f.cell, b = f.cell * 1.41421356f, c = f.cell * 1.7320508f;
            int sx = 1, sy = f.nx, sz = f.nx * f.ny;
            // the 13 neighbours before a voxel in scan order, with their weights
            int[] off = { -sx, -sy - sx, -sy, -sy + sx, -sz - sy - sx, -sz - sy, -sz - sy + sx, -sz - sx, -sz, -sz + sx, -sz + sy - sx, -sz + sy, -sz + sy + sx };
            float[] w = { a, b, a, b, c, b, c, b, a, b, c, b, c };
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z = 1; z < f.nz - 1; z++)
                    for (int y = 1; y < f.ny - 1; y++)
                        for (int x = 1; x < f.nx - 1; x++)
                        {
                            int i = f.Index(x, y, z);
                            float best = u[i];
                            for (int k = 0; k < 13; k++) { float s = u[i + off[k]] + w[k]; if (s < best) best = s; }
                            u[i] = best;
                        }
                for (int z = f.nz - 2; z >= 1; z--)
                    for (int y = f.ny - 2; y >= 1; y--)
                        for (int x = f.nx - 2; x >= 1; x--)
                        {
                            int i = f.Index(x, y, z);
                            float best = u[i];
                            for (int k = 0; k < 13; k++) { float s = u[i - off[k]] + w[k]; if (s < best) best = s; }
                            u[i] = best;
                        }
            }
            for (int i = 0; i < u.Length; i++) if (u[i] > f.limit) u[i] = f.limit;
        }

        // ------------------------------------------------------------ 4: operations on the field

        public static void Apply(Field f, IList<Op> ops, Vector3 min, Vector3 size)
        {
            if (ops == null) return;
            foreach (var op in ops) Apply(f, op, min, size);
        }

        public static void Apply(Field f, Op op, Vector3 min, Vector3 size)
        {
            if (op == null || f == null) return;
            switch (op.kind)
            {
                // (a coarse view field keeps the wall at least 1.3 voxels, else the preview would show holes)
                case "hollow": Hollow(f, Mathf.Max(Mathf.Max(.8f, op.a) * .001f, f.cell * 1.3f), op.side); break;
                case "hole":
                {
                    Vector3 at = op.At(min, size), dir = op.Direction();
                    float r = Mathf.Max(1f, op.a) * .0005f, half = Mathf.Max(2f, op.b) * .0005f;
                    Carve(f, at, r + half + f.cell * 2f, p =>
                    {
                        Vector3 q = p - at;
                        float along = Vector3.Dot(q, dir);
                        float across = (q - dir * along).magnitude;
                        return Mathf.Max(across - r, Mathf.Abs(along) - half);
                    });
                    break;
                }
                case "notch":
                {
                    // A slot up from the bottom through the wall, along `dir` (outward), `a` wide, `b` tall.
                    Vector3 at = op.At(min, size), dir = op.Direction();
                    dir.y = 0f;
                    dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
                    Vector3 across = Vector3.Cross(Vector3.up, dir);
                    float hw = Mathf.Max(2f, op.a) * .0005f, h = Mathf.Max(2f, op.b) * .001f, depth = Mathf.Max(size.x, size.z) * .25f + .004f;
                    Vector3 centre = new Vector3(at.x, min.y + h * .5f - .002f, at.z);
                    Carve(f, centre, depth + hw + h + f.cell * 2f, p =>
                    {
                        Vector3 q = p - centre;
                        Vector3 e = new Vector3(Mathf.Abs(Vector3.Dot(q, across)) - hw, Mathf.Abs(q.y) - (h * .5f + .002f), Mathf.Abs(Vector3.Dot(q, dir)) - depth);
                        return Vector3.Max(e, Vector3.zero).magnitude + Mathf.Min(Mathf.Max(e.x, Mathf.Max(e.y, e.z)), 0f);
                    });
                    break;
                }
                case "dab":
                {
                    Vector3 at = op.At(min, size);
                    float r = Mathf.Max(1f, op.a) * .001f, k = r * .6f;
                    if (op.side == "smooth") { Smooth(f, at, r, Mathf.Clamp01(op.b <= 0f ? .5f : op.b)); break; }
                    bool pull = op.side == "pull";
                    Region(f, at, r + k + f.cell * 2f, (i, p) =>
                    {
                        float s = (p - at).magnitude - r;
                        f.d[i] = pull ? SmoothMin(f.d[i], s, k) : -SmoothMin(-f.d[i], s, k);
                    });
                    break;
                }
            }
        }

        // Smooth union (Inigo Quilez's polynomial smooth minimum).
        public static float SmoothMin(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Min(a, b) - h * h * k * .25f;
        }

        private static void Carve(Field f, Vector3 centre, float reach, Func<Vector3, float> shape)
        {
            Region(f, centre, reach, (i, p) => { float s = shape(p); if (-s > f.d[i]) f.d[i] = -s; });
        }

        private static void Region(Field f, Vector3 centre, float reach, Action<int, Vector3> each)
        {
            Vector3 g0 = (centre - Vector3.one * reach - f.origin) / f.cell, g1 = (centre + Vector3.one * reach - f.origin) / f.cell;
            int x0 = Mathf.Max(1, Mathf.FloorToInt(g0.x)), y0 = Mathf.Max(1, Mathf.FloorToInt(g0.y)), z0 = Mathf.Max(1, Mathf.FloorToInt(g0.z));
            int x1 = Mathf.Min(f.nx - 2, Mathf.CeilToInt(g1.x)), y1 = Mathf.Min(f.ny - 2, Mathf.CeilToInt(g1.y)), z1 = Mathf.Min(f.nz - 2, Mathf.CeilToInt(g1.z));
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        each(f.Index(x, y, z), f.Centre(x, y, z));
        }

        private static void Smooth(Field f, Vector3 at, float r, float strength)
        {
            var copy = new Dictionary<int, float>();
            Region(f, at, r + f.cell * 2f, (i, p) => copy[i] = f.d[i]);
            int sx = 1, sy = f.nx, sz = f.nx * f.ny;
            float Get(int j) => copy.TryGetValue(j, out var value) ? value : f.d[j];
            Region(f, at, r, (i, p) =>
            {
                float fall = 1f - Mathf.Clamp01((p - at).magnitude / r);
                if (fall <= 0f) return;
                float avg = (Get(i - sx) + Get(i + sx) + Get(i - sy) + Get(i + sy) + Get(i - sz) + Get(i + sz)) / 6f;
                f.d[i] = Mathf.Lerp(copy[i], avg, strength * fall);
            });
        }

        // A shell `wall` thick; `open` top or bottom takes the shell off right above or below the hollow inside.
        private static void Hollow(Field f, float wall, string open)
        {
            int count = f.d.Length;
            var cavity = new bool[count];
            for (int i = 0; i < count; i++) cavity[i] = f.d[i] < -wall;
            for (int i = 0; i < count; i++) if (cavity[i]) f.d[i] = Mathf.Max(f.d[i], -(f.d[i] + wall));
            if (open != "top" && open != "bottom") return;
            float outside = f.cell * .5f;
            // 2026-11-15 (F90b): only a floor (or roof) about a wall thick is opened under (over) the hollow. It used to
            // open the whole column below the lowest hollow voxel, so a lid overhanging the body (its own hollow above the
            // side wall) cut that side wall away; it showed once solids came out solid instead of as thin skins.
            int most = Mathf.CeilToInt(wall / f.cell) + 2;
            for (int z = 0; z < f.nz; z++)
                for (int x = 0; x < f.nx; x++)
                {
                    int low = -1, high = -1;
                    for (int y = 0; y < f.ny; y++) if (cavity[f.Index(x, y, z)]) { if (low < 0) low = y; high = y; }
                    if (low < 0) continue;
                    int step = open == "bottom" ? -1 : 1, from = open == "bottom" ? low - 1 : high + 1, run = 0;
                    for (int y = from; y >= 0 && y < f.ny && f.d[f.Index(x, y, z)] < 0f && !cavity[f.Index(x, y, z)]; y += step) run++;
                    if (run > most) continue; // (a side wall, not a floor)
                    for (int k = 0, y = from; k < run; k++, y += step) { int i = f.Index(x, y, z); f.d[i] = Mathf.Max(f.d[i], outside); }
                }
        }

        // Voxels that touch only across a corner of a square (a checkerboard 2x2) make an edge four faces share, which
        // a slicer reports as non-manifold. Such a pair is joined: the outside voxel nearer the surface becomes inside
        // (a thin wall gets a voxel thicker, never broken). Runs before meshing; a few passes settle it.
        public static int JoinCorners(Field f, int passes = 4)
        {
            int fixedCount = 0, sx = 1, sy = f.nx, sz = f.nx * f.ny;
            float join = -f.cell * .05f;
            int[,] planes = { { sx, sy }, { sy, sz }, { sx, sz } };
            for (int pass = 0; pass < passes; pass++)
            {
                int fixedNow = 0;
                for (int z = 1; z < f.nz - 2; z++)
                    for (int y = 1; y < f.ny - 2; y++)
                        for (int x = 1; x < f.nx - 2; x++)
                        {
                            int i = f.Index(x, y, z);
                            for (int p = 0; p < 3; p++)
                            {
                                int a = i, b = i + planes[p, 0], c = i + planes[p, 1], d = i + planes[p, 0] + planes[p, 1];
                                bool ia = f.d[a] < 0f, ib = f.d[b] < 0f, ic = f.d[c] < 0f, id = f.d[d] < 0f;
                                if (ia != id || ib != ic || ia == ib) continue; // not a checkerboard
                                // the two outside ones: join through the one nearer the surface
                                int o1 = ia ? b : a, o2 = ia ? c : d;
                                int pick = f.d[o1] <= f.d[o2] ? o1 : o2;
                                f.d[pick] = join;
                                fixedNow++;
                            }
                        }
                fixedCount += fixedNow;
                if (fixedNow == 0) break;
            }
            return fixedCount;
        }

        // ------------------------------------------------------------ 5: surface nets

        public static string LastMeshTimes = "";

        // `step` 2 meshes every second voxel (a lighter mesh for the view; the print uses 1).
        public static Result Mesh(Field f, int step = 1, bool check = true)
        {
            step = Mathf.Max(1, step);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int mx = (f.nx - 1) / step + 1, my = (f.ny - 1) / step + 1, mz = (f.nz - 1) / step + 1;
            float cell = f.cell * step;
            float D(int x, int y, int z) => f.d[f.Index(Mathf.Min(x * step, f.nx - 1), Mathf.Min(y * step, f.ny - 1), Mathf.Min(z * step, f.nz - 1))];
            Vector3 P(int x, int y, int z) => f.origin + new Vector3(x, y, z) * cell;
            var vertexOf = new int[(mx - 1) * (my - 1) * (mz - 1)];
            for (int i = 0; i < vertexOf.Length; i++) vertexOf[i] = -1;
            int CubeIndex(int x, int y, int z) => (z * (my - 1) + y) * (mx - 1) + x;
            var vertices = new List<Vector3>();
            var values = new float[8];
            int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            // (step 1: the eight corners straight from the array; a cube is skipped unless its corners differ)
            int sy1 = f.nx, sz1 = f.nx * f.ny;
            for (int z = 0; z < mz - 1; z++)
                for (int y = 0; y < my - 1; y++)
                    for (int x = 0; x < mx - 1; x++)
                    {
                        int mask = 0;
                        if (step == 1)
                        {
                            int i0 = (z * f.ny + y) * f.nx + x;
                            bool s0 = f.d[i0] < 0f;
                            if (s0 == (f.d[i0 + 1] < 0f) && s0 == (f.d[i0 + sy1] < 0f) && s0 == (f.d[i0 + sy1 + 1] < 0f) &&
                                s0 == (f.d[i0 + sz1] < 0f) && s0 == (f.d[i0 + sz1 + 1] < 0f) && s0 == (f.d[i0 + sz1 + sy1] < 0f) && s0 == (f.d[i0 + sz1 + sy1 + 1] < 0f)) continue;
                            values[0] = f.d[i0]; values[1] = f.d[i0 + 1]; values[2] = f.d[i0 + sy1]; values[3] = f.d[i0 + sy1 + 1];
                            values[4] = f.d[i0 + sz1]; values[5] = f.d[i0 + sz1 + 1]; values[6] = f.d[i0 + sz1 + sy1]; values[7] = f.d[i0 + sz1 + sy1 + 1];
                            for (int k = 0; k < 8; k++) if (values[k] < 0f) mask |= 1 << k;
                        }
                        else
                            for (int k = 0; k < 8; k++)
                            {
                                values[k] = D(x + (k & 1), y + ((k >> 1) & 1), z + ((k >> 2) & 1));
                                if (values[k] < 0f) mask |= 1 << k;
                            }
                        if (mask == 0 || mask == 255) continue;
                        Vector3 sum = Vector3.zero;
                        int crossings = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            int a = edges[e, 0], b = edges[e, 1];
                            float da = values[a], db = values[b];
                            if ((da < 0f) == (db < 0f)) continue;
                            float t = da / (da - db);
                            var pa = new Vector3(a & 1, (a >> 1) & 1, (a >> 2) & 1);
                            var pb = new Vector3(b & 1, (b >> 1) & 1, (b >> 2) & 1);
                            sum += Vector3.Lerp(pa, pb, t);
                            crossings++;
                        }
                        vertexOf[CubeIndex(x, y, z)] = vertices.Count;
                        vertices.Add(P(x, y, z) + sum / crossings * cell);
                    }
            long tCells = watch.ElapsedMilliseconds;
            var triangles = new List<int>(vertices.Count * 6);
            void Quad(int c00, int c10, int c11, int c01, bool flip)
            {
                if (c00 < 0 || c10 < 0 || c11 < 0 || c01 < 0) return;
                if (flip) { int s = c10; c10 = c01; c01 = s; }
                // split along the shorter diagonal
                if ((vertices[c00] - vertices[c11]).sqrMagnitude <= (vertices[c10] - vertices[c01]).sqrMagnitude)
                { triangles.Add(c00); triangles.Add(c10); triangles.Add(c11); triangles.Add(c00); triangles.Add(c11); triangles.Add(c01); }
                else
                { triangles.Add(c00); triangles.Add(c10); triangles.Add(c01); triangles.Add(c10); triangles.Add(c11); triangles.Add(c01); }
            }
            for (int z = 1; z < mz - 1; z++)
                for (int y = 1; y < my - 1; y++)
                    for (int x = 1; x < mx - 1; x++)
                    {
                        float d0 = D(x, y, z);
                        bool inside0 = d0 < 0f;
                        // along x: cubes around the edge in the y-z plane (y x z = x)
                        if (x < mx - 1 && (D(x + 1, y, z) < 0f) != inside0)
                            Quad(vertexOf[CubeIndex(x, y - 1, z - 1)], vertexOf[CubeIndex(x, y, z - 1)], vertexOf[CubeIndex(x, y, z)], vertexOf[CubeIndex(x, y - 1, z)], !inside0);
                        // along y: z, x (z x x = y)
                        if (y < my - 1 && (D(x, y + 1, z) < 0f) != inside0)
                            Quad(vertexOf[CubeIndex(x - 1, y, z - 1)], vertexOf[CubeIndex(x - 1, y, z)], vertexOf[CubeIndex(x, y, z)], vertexOf[CubeIndex(x, y, z - 1)], !inside0);
                        // along z: x, y (x x y = z)
                        if (z < mz - 1 && (D(x, y, z + 1) < 0f) != inside0)
                            Quad(vertexOf[CubeIndex(x - 1, y - 1, z)], vertexOf[CubeIndex(x, y - 1, z)], vertexOf[CubeIndex(x, y, z)], vertexOf[CubeIndex(x - 1, y, z)], !inside0);
                    }
            long tQuads = watch.ElapsedMilliseconds;
            var r = new Result { vertices = vertices.ToArray(), triangles = triangles.ToArray() };
            double volume = 0;
            for (int i = 0; i + 2 < r.triangles.Length; i += 3)
                volume += Vector3.Dot(r.vertices[r.triangles[i]], Vector3.Cross(r.vertices[r.triangles[i + 1]], r.vertices[r.triangles[i + 2]])) / 6.0;
            // Front faces have Cross(b - a, c - a) outward (Unity's own normals): an outward closed surface has a
            // positive volume. The quads above are wound that way; this only guards against a mistake.
            if (volume < 0)
            {
                for (int i = 0; i + 2 < r.triangles.Length; i += 3) { int s = r.triangles[i + 1]; r.triangles[i + 1] = r.triangles[i + 2]; r.triangles[i + 2] = s; }
                volume = -volume;
            }
            r.volumeCm3 = (float)(volume * 1e6);
            // Normals: the faces around each vertex (area weighted); the field's gradient only where they cancel out.
            r.normals = new Vector3[r.vertices.Length];
            for (int i = 0; i + 2 < r.triangles.Length; i += 3)
            {
                int a = r.triangles[i], b = r.triangles[i + 1], c = r.triangles[i + 2];
                Vector3 n = Vector3.Cross(r.vertices[b] - r.vertices[a], r.vertices[c] - r.vertices[a]);
                r.normals[a] += n; r.normals[b] += n; r.normals[c] += n;
            }
            for (int i = 0; i < r.normals.Length; i++)
            {
                float m = r.normals[i].magnitude;
                r.normals[i] = m > 1e-14f ? r.normals[i] / m : f.Gradient(r.vertices[i]);
            }
            long tNormals = watch.ElapsedMilliseconds;
            if (check) r.openEdges = OpenEdges(r.vertices, r.triangles, out r.nonManifoldEdges, false);
            LastMeshTimes = $"cells {tCells} ms, quads {tQuads - tCells} ms, normals {tNormals - tQuads} ms, edges {watch.ElapsedMilliseconds - tNormals} ms";
            return r;
        }

        // ------------------------------------------------------------ smoothing

        // Taubin smoothing (a shrink step, then an inflate step, so the size stays): takes the voxel steps off a surface
        // nets mesh without rounding it away. Normals are made again from the faces.
        public static void Taubin(Result r, int iterations, float lambda = .5f, float mu = -.53f)
        {
            if (r == null || iterations <= 0 || r.vertices.Length == 0) return;
            int n = r.vertices.Length;
            // neighbours (CSR): every edge of every triangle, both ways, duplicates kept (weights by use are fine)
            var count = new int[n + 1];
            var t = r.triangles;
            for (int i = 0; i + 2 < t.Length; i += 3) { count[t[i] + 1] += 2; count[t[i + 1] + 1] += 2; count[t[i + 2] + 1] += 2; }
            for (int i = 0; i < n; i++) count[i + 1] += count[i];
            var fill = (int[])count.Clone();
            var around = new int[count[n]];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                around[fill[a]++] = b; around[fill[a]++] = c;
                around[fill[b]++] = a; around[fill[b]++] = c;
                around[fill[c]++] = a; around[fill[c]++] = b;
            }
            var v = r.vertices;
            var next = new Vector3[n];
            for (int it = 0; it < iterations * 2; it++)
            {
                float k = (it & 1) == 0 ? lambda : mu;
                for (int i = 0; i < n; i++)
                {
                    int from = count[i], to = count[i + 1];
                    if (to == from) { next[i] = v[i]; continue; }
                    Vector3 sum = Vector3.zero;
                    for (int j = from; j < to; j++) sum += v[around[j]];
                    next[i] = v[i] + (sum / (to - from) - v[i]) * k;
                }
                var swap = v; v = next; next = swap;
            }
            r.vertices = v;
            var normals = new Vector3[n];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 f = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                normals[t[i]] += f; normals[t[i + 1]] += f; normals[t[i + 2]] += f;
            }
            for (int i = 0; i < n; i++) { float m = normals[i].magnitude; normals[i] = m > 1e-14f ? normals[i] / m : Vector3.up; }
            r.normals = normals;
        }

        public static int ViewSmoothing = 4, PrintSmoothing = 2;

        // ------------------------------------------------------------ checks

        // Edges used by one triangle (a hole) and by more than two (a pinch), welding by position (`weld` false: the
        // vertices are already one per position, like the surface nets' own).
        public static int OpenEdges(Vector3[] v, int[] t, out int nonManifold, bool weld = true)
        {
            var id = weld ? WeldIds(v) : null;
            // Sorted edge keys, counted in runs (a long's own hash is low ^ high, which collides for edges).
            var keys = new long[t.Length];
            int count = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = t[i + e], b = t[i + (e + 1) % 3];
                    if (id != null) { a = id[a]; b = id[b]; }
                    if (a == b) continue;
                    keys[count++] = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                }
            Array.Sort(keys, 0, count);
            int open = 0;
            nonManifold = 0;
            for (int i = 0; i < count; )
            {
                int j = i + 1;
                while (j < count && keys[j] == keys[i]) j++;
                int n = j - i;
                if (n == 1) open++; else if (n > 2) nonManifold++;
                i = j;
            }
            return open;
        }

        // A good hash for long keys (splitmix64's finaliser): the default one is low ^ high.
        public sealed class LongKeys : IEqualityComparer<long>
        {
            public static readonly LongKeys Instance = new LongKeys();
            public bool Equals(long a, long b) => a == b;
            public int GetHashCode(long key)
            {
                ulong z = (ulong)key + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return (int)(z ^ (z >> 31));
            }
        }

        // One id per distinct position (a millionth of the size apart counts as the same). Keys are packed into a
        // long: Unity's Vector3Int hash collides badly for big coordinates (that made this take seconds).
        public static int[] WeldIds(Vector3[] v)
        {
            var id = new int[v.Length];
            if (v.Length == 0) return id;
            Vector3 lo = v[0], hi = lo;
            for (int i = 1; i < v.Length; i++) { lo = Vector3.Min(lo, v[i]); hi = Vector3.Max(hi, v[i]); }
            float q = Mathf.Max(1e-9f, Mathf.Max(hi.x - lo.x, Mathf.Max(hi.y - lo.y, hi.z - lo.z)) * 1e-6f);
            var ids = new Dictionary<long, int>(v.Length, LongKeys.Instance);
            for (int i = 0; i < v.Length; i++) id[i] = Id(ids, PackKey((v[i] - lo) / q));
            return id;
        }

        public static long PackKey(Vector3 g) =>
            ((long)(Mathf.RoundToInt(g.x) & 0x1FFFFF) << 42) | ((long)(Mathf.RoundToInt(g.y) & 0x1FFFFF) << 21) | (long)(Mathf.RoundToInt(g.z) & 0x1FFFFF);

        private static int Id(Dictionary<long, int> ids, long key)
        {
            if (!ids.TryGetValue(key, out int k)) { k = ids.Count; ids[key] = k; }
            return k;
        }

        // ------------------------------------------------------------ colours from the model's texture

        // Each vertex takes the colour of the nearest point of the source surface (its texture there), or `fallback`.
        public static Color32[] Colours(Result r, Vector3[] sv, int[] st, Vector2[] suv, Color32[] texture, int tw, int th, Color32 fallback, float reach)
        {
            var colours = new Color32[r.vertices.Length];
            bool textured = texture != null && suv != null && suv.Length == sv.Length && tw > 0 && th > 0;
            if (!textured) { for (int i = 0; i < colours.Length; i++) colours[i] = fallback; return colours; }
            var buckets = new TriangleBuckets(sv, st, r.vertices.Length > 0 ? r.vertices[0] : Vector3.zero, reach * 2f, reach);
            for (int i = 0; i < colours.Length; i++)
            {
                float d = buckets.Nearest(r.vertices[i], reach, out int tri, out Vector3 bary);
                if (tri < 0 || d > reach) { colours[i] = fallback; continue; }
                Vector2 uv = suv[st[tri]] * bary.x + suv[st[tri + 1]] * bary.y + suv[st[tri + 2]] * bary.z;
                colours[i] = Bilinear(texture, tw, th, uv);
            }
            return colours;
        }

        private static Color32 Bilinear(Color32[] px, int w, int h, Vector2 uv)
        {
            float x = Mathf.Clamp(uv.x * w - .5f, 0f, w - 1.001f), y = Mathf.Clamp(uv.y * h - .5f, 0f, h - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            Color a = px[y0 * w + x0], b = px[y0 * w + x0 + 1], c = px[(y0 + 1) * w + x0], d = px[(y0 + 1) * w + x0 + 1];
            return Color.Lerp(Color.Lerp(a, b, fx), Color.Lerp(c, d, fx), fy);
        }

        // ------------------------------------------------------------ nearest triangle (buckets)

        public sealed class TriangleBuckets
        {
            private readonly Vector3[] v;
            private readonly int[] t;
            private readonly Vector3 origin;
            private readonly float size;
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>(LongKeys.Instance);

            public TriangleBuckets(Vector3[] v, int[] t, Vector3 origin, float size, float grow)
            {
                this.v = v; this.t = t; this.origin = origin; this.size = Mathf.Max(size, 1e-5f);
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    Vector3 lo = Vector3.Min(v[t[k]], Vector3.Min(v[t[k + 1]], v[t[k + 2]])) - Vector3.one * grow;
                    Vector3 hi = Vector3.Max(v[t[k]], Vector3.Max(v[t[k + 1]], v[t[k + 2]])) + Vector3.one * grow;
                    Vector3Int a = Cell(lo), b = Cell(hi);
                    for (int z = a.z; z <= b.z; z++)
                        for (int y = a.y; y <= b.y; y++)
                            for (int x = a.x; x <= b.x; x++)
                            {
                                long key = Key(x, y, z);
                                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>(8);
                                list.Add(k);
                            }
                }
            }

            private Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt((p.x - origin.x) / size), Mathf.FloorToInt((p.y - origin.y) / size), Mathf.FloorToInt((p.z - origin.z) / size));
            private static long Key(int x, int y, int z) => ((long)(x + 0x100000) << 42) | ((long)(y + 0x100000) << 21) | (long)(z + 0x100000);

            // The distance to the nearest triangle within `reach` (else infinity), its first index and barycentric weights.
            public float Nearest(Vector3 p, float reach, out int triangle, out Vector3 bary)
            {
                triangle = -1;
                bary = Vector3.zero;
                var c = Cell(p);
                if (!cells.TryGetValue(Key(c.x, c.y, c.z), out var list)) return float.PositiveInfinity;
                float best = reach * reach;
                foreach (int k in list)
                {
                    Vector3 q = Closest(p, v[t[k]], v[t[k + 1]], v[t[k + 2]], out Vector3 w);
                    float d = (q - p).sqrMagnitude;
                    if (d < best) { best = d; triangle = k; bary = w; }
                }
                return triangle < 0 ? float.PositiveInfinity : Mathf.Sqrt(best);
            }

            // Closest point on a triangle (Ericson), with its barycentric weights.
            private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 w)
            {
                Vector3 ab = b - a, ac = c - a, ap = p - a;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f) { w = new Vector3(1, 0, 0); return a; }
                Vector3 bp = p - b;
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3) { w = new Vector3(0, 1, 0); return b; }
                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float s = d1 / (d1 - d3); w = new Vector3(1 - s, s, 0); return a + ab * s; }
                Vector3 cp = p - c;
                float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0f && d5 <= d6) { w = new Vector3(0, 0, 1); return c; }
                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float s = d2 / (d2 - d6); w = new Vector3(1 - s, 0, s); return a + ac * s; }
                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) { float s = (d4 - d3) / ((d4 - d3) + (d5 - d6)); w = new Vector3(0, 1 - s, s); return b + (c - b) * s; }
                float denom = 1f / (va + vb + vc);
                float sv = vb * denom, sw = vc * denom;
                w = new Vector3(1 - sv - sw, sv, sw);
                return a + ab * sv + ac * sw;
            }
        }

        // ------------------------------------------------------------ the whole job

        public sealed class Job
        {
            // in (copied on the main thread)
            public Vector3[] vertices;   // the made thing's mesh, own space, metres
            public int[] triangles;
            public Vector2[] uv;
            public Color32[] texture;
            public int textureWidth, textureHeight;
            public Color32 colour = new Color32(235, 220, 190, 255);
            public List<Op> ops = new List<Op>();
            public bool forPrint = true;
            public float cell;           // 0: CellFor
            public float room;           // metres of room around the shape (a sculpting view: SculptRoom)
            // out
            public Field field;          // after the operations
            public Field baseField;      // before the dabs (sculpting replays from here)
            public Result result;
            public string error;
        }

        public static Job Run(Job job)
        {
            try
            {
                var stats = new Stats();
                Vector3 lo = job.vertices[0], hi = lo;
                foreach (var p in job.vertices) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                // room around the shape: what the dabs reach beyond it (print), or room to pull clay out (sculpting view)
                float room = Mathf.Max(0f, job.room);
                foreach (var op in job.ops)
                {
                    if (op == null || op.kind != "dab") continue;
                    Vector3 at = op.At(lo, hi - lo);
                    float r = Mathf.Max(1f, op.a) * .001f * 1.6f;
                    Vector3 beyond = Vector3.Max(lo - at, at - hi);
                    room = Mathf.Max(room, Mathf.Max(beyond.x, Mathf.Max(beyond.y, beyond.z)) + r);
                }
                room = Mathf.Clamp(room, 0f, .06f);
                float cell = job.cell > 0f ? job.cell : CellFor(hi - lo, job.forPrint, room);
                var field = Voxelize(job.vertices, job.triangles, cell, stats, room);
                var features = new List<Op>();
                var dabs = new List<Op>();
                foreach (var op in job.ops) (op.kind == "dab" ? dabs : features).Add(op);
                // hollowing first (it looks at the whole inside), then the cuts, then the hand's dabs
                features.Sort((a, b) => (a.kind == "hollow" ? 0 : 1).CompareTo(b.kind == "hollow" ? 0 : 1));
                Apply(field, features, lo, hi - lo);
                job.baseField = dabs.Count > 0 || !job.forPrint ? field.Clone() : null;
                Apply(field, dabs, lo, hi - lo);
                stats.cornersJoined = JoinCorners(field);
                job.field = field;
                var result = Mesh(field, 1);
                Taubin(result, job.forPrint ? PrintSmoothing : ViewSmoothing); // the voxel steps off (size kept)
                result.stats = stats;
                result.colours = Colours(result, job.vertices, job.triangles, job.uv, job.texture, job.textureWidth, job.textureHeight, job.colour, cell * 6f);
                job.result = result;
            }
            catch (Exception exception) { job.error = exception.GetType().Name + ": " + exception.Message; }
            return job;
        }
    }
}
