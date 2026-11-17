using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Windups.Handoff
{
    // F26: a coaster that fits the user's measured mug. All sizes in millimetres.
    // The app builds it itself (no outside service): a solid base, a rim around the mug's foot
    // and optional ribs, as one closed mesh that can be printed.
    // F29 adds the person's own drawn outline ("drawn") and a second kind, "vessel": a pen holder,
    // cup, planter or vase turned around its axis from a side profile (straight, tapered, curvy,
    // bowl, or the person's drawing). For a vessel cupDiameter is what goes inside, rimWidth the wall
    // and baseThickness + rimHeight the full height.
    [Serializable] public sealed class CoasterDesign
    {
        public string name = "Classic";
        public string shape = "round";     // round | hex | octagon | square | flower
        public float cupDiameter = 80f;    // the mug's measured base
        public float clearance = 2f;       // added to the inner diameter so the mug drops in
        public float rimWidth = 5f;
        public float rimHeight = 4f;       // above the floor
        public float baseThickness = 3f;
        public int ribs = 0;               // raised lines on the floor (drips, air)
        public float ribHeight = 1f;
        public string why = "";
        public string color = "";         // #rrggbb, the main colour of the chosen look
        public string kind = "coaster";    // coaster | vessel | tray | clip | model (F30)
        public string profile = "straight"; // vessel: straight | tapered | curvy | bowl | drawn
        public float[] outline;            // "drawn" coaster: 72 radii around the centre (max 1)
        public float[] sideProfile;        // "drawn" vessel: 32 radii from the bottom up (max 1)
        // F30 tray: cupDiameter is unused, clearance the gap around each pocket (both sides), rimWidth the
        // wall around the pockets, rimHeight the wall above the floor.
        public float[][] pockets;          // each pocket's outline in tray space, mm: x0, y0, x1, y1, ...
        public string outer = "rounded";   // rounded | round | hug | drawn (uses outline)
        // F30 clip: cupDiameter is the desk's thickness, clearance the grip gap (both sides), rimWidth the bar
        // thickness, rimHeight the hook drop / loop size / shelf length, grip how far it reaches over the desk.
        public string clipType = "hook";   // hook | cable | shelf
        public float width = 24f;          // along the desk edge
        public float grip = 35f;
        // F30 model: cupDiameter is the width it is made at; the mesh comes from Meshy.
        public string model;               // OBJ file on the headset, or "scripted:..." in the Editor preview
        public string texture;             // its base colour picture
        public float modelHeight;          // mm, at that width
        public float cut;                  // mm taken off the bottom so it stands flat
        // F39/F40: a model's own width (x), depth (z) and height (y, before the cut), mm; 0 = in proportion
        // to cupDiameter. Set when a size is changed on v2.
        public float sizeW, sizeD, sizeH;
        // F88b: the model was turned a quarter so its own z (its long side) runs along the person's width: sizeW (x) is
        // then the person's depth and sizeD (z) the person's width (MakerSession.SideTurn.cs).
        public bool sidesTurned;
        // F59: a model's operations (hollow, holes, cable notch, sculpting dabs), done on its rebuilt solid (SolidRebuild).
        // Never changed in place: a new array each time (versions keep a shallow copy of the design).
        public SolidRebuild.Op[] ops;
        // F83: a model's own parts (beak, wing...): `parts` is the .parts file when it is not the one beside the
        // model's OBJ (a retextured copy keeps the first shape's parts); `partEdits` are the changes made to single
        // parts ("longer beak"). Never changed in place: a new array each time.
        public string parts;
        public PartEdit[] partEdits;

        public Color Tint(Color fallback) => ColorUtility.TryParseHtmlString(color ?? "", out var c) ? c : fallback;

        public static readonly string[] Shapes = { "round", "hex", "octagon", "square", "flower" };
        public static readonly string[] AllShapes = { "round", "hex", "octagon", "square", "flower", "drawn" };
        public static readonly string[] Profiles = { "straight", "tapered", "curvy", "bowl", "drawn" };
        public bool IsVessel => kind == "vessel";
        public bool IsFitShape => kind == "tray" || kind == "clip" || kind == "model";

        public float InnerDiameter => cupDiameter + clearance;
        public float OuterDiameter => InnerDiameter + 2f * rimWidth;
        public float Height => kind == "model" ? modelHeight : baseThickness + rimHeight;

        public CoasterDesign Clamped()
        {
            var d = (CoasterDesign)MemberwiseClone();
            d.name = string.IsNullOrWhiteSpace(d.name) ? "Coaster" : d.name.Trim();
            d.kind = Array.IndexOf(FitShapes.Kinds, d.kind ?? "") >= 0 ? d.kind : "coaster";
            if (d.IsFitShape) return d.ClampedShape();
            d.shape = Array.IndexOf(AllShapes, (d.shape ?? "").ToLowerInvariant()) >= 0 ? d.shape.ToLowerInvariant() : "round";
            if (d.shape == "drawn" && (d.outline == null || d.outline.Length != 72)) d.shape = "round";
            d.profile = Array.IndexOf(Profiles, (d.profile ?? "").ToLowerInvariant()) >= 0 ? d.profile.ToLowerInvariant() : "straight";
            if (d.profile == "drawn" && (d.sideProfile == null || d.sideProfile.Length != 32)) d.profile = "straight";
            d.cupDiameter = Mathf.Clamp(d.cupDiameter, 30f, 160f);
            d.clearance = Mathf.Clamp(d.clearance, .5f, 8f);
            d.rimWidth = Mathf.Clamp(d.rimWidth, 2f, 25f);
            d.rimHeight = Mathf.Clamp(d.rimHeight, 0f, d.kind == "vessel" ? 300f : 25f);
            d.baseThickness = Mathf.Clamp(d.baseThickness, 1.6f, 10f);
            d.ribs = Mathf.Clamp(d.ribs, 0, 24);
            d.ribHeight = Mathf.Clamp(d.ribHeight, .4f, 3f);
            d.why = d.why ?? "";
            d.color = d.color ?? "";
            return d;
        }

        // F30: the trays, clips and models keep their own sensible ranges.
        private CoasterDesign ClampedShape()
        {
            name = name.Trim();
            why = why ?? "";
            color = color ?? "";
            switch (kind)
            {
                case "tray":
                    outer = Array.IndexOf(FitShapes.TrayOuters, outer ?? "") >= 0 ? outer : "rounded";
                    if (outer == "drawn" && (outline == null || outline.Length != 72)) outer = "rounded";
                    clearance = Mathf.Clamp(clearance, 1f, 8f);
                    rimWidth = Mathf.Clamp(rimWidth, 4f, 20f);
                    rimHeight = Mathf.Clamp(rimHeight, 4f, 40f);
                    baseThickness = Mathf.Clamp(baseThickness, 1.6f, 6f);
                    break;
                case "clip":
                    clipType = Array.IndexOf(FitShapes.ClipTypes, clipType ?? "") >= 0 ? clipType : "hook";
                    cupDiameter = Mathf.Clamp(cupDiameter, 6f, 80f);
                    clearance = Mathf.Clamp(clearance, .2f, 4f);
                    rimWidth = Mathf.Clamp(rimWidth, 3f, 8f);
                    rimHeight = Mathf.Clamp(rimHeight, 4f, 120f);
                    width = Mathf.Clamp(width, 8f, 200f);
                    grip = Mathf.Clamp(grip, 15f, 70f);
                    baseThickness = rimWidth;
                    break;
                default: // model
                    cupDiameter = Mathf.Clamp(cupDiameter, 15f, 400f);
                    cut = Mathf.Clamp(cut, 0f, 20f);
                    sizeW = sizeW > 0f ? Mathf.Clamp(sizeW, 8f, 600f) : 0f;
                    sizeD = sizeD > 0f ? Mathf.Clamp(sizeD, 8f, 600f) : 0f;
                    sizeH = sizeH > 0f ? Mathf.Clamp(sizeH, 8f, 600f) : 0f;
                    break;
            }
            return this;
        }

        public CoasterDesign Copy() => (CoasterDesign)MemberwiseClone();
        public string Summary =>
            kind == "tray" ? $"{name}: tray with {pockets?.Length ?? 0} pocket(s), {outer} outline, walls {rimHeight:0} mm, gap {clearance * .5f:0.#} mm"
            : kind == "clip" ? $"{name}: desk {clipType} for a {cupDiameter:0} mm desk edge, {width:0} mm wide, grip gap {clearance * .5f:0.#} mm"
            : kind == "model" ? $"{name}: model {cupDiameter:0} mm wide, {modelHeight:0} mm tall" + (cut > 0f ? $", flat base" : "")
            : IsVessel
            ? $"{name}: {profile} vessel, {Height:0} mm tall, inside Ø{InnerDiameter:0} mm, wall {rimWidth:0.#} mm, base {baseThickness:0.#} mm"
            : $"{name}: {(shape == "drawn" ? "your own outline" : shape)}, inner Ø{InnerDiameter:0} mm, rim {rimHeight:0.#} mm, base {baseThickness:0.#} mm" + (ribs > 0 ? $", {ribs} ribs" : "");
    }

    public static class CoasterMesh
    {
        private const int Around = 96;
        private const float Mm = .001f;

        // Outer edge radius at angle `a` (mm). Polygons keep at least `rimWidth` of rim everywhere.
        private static float Outer(CoasterDesign d, float a)
        {
            float apothem = d.InnerDiameter * .5f + d.rimWidth;
            switch (d.shape)
            {
                case "hex": return Polygon(apothem, 6, a, .96f);
                case "octagon": return Polygon(apothem, 8, a, .98f);
                case "square": return Polygon(apothem, 4, a, .9f);
                case "flower": return apothem * (1.04f + .07f * Mathf.Cos(6f * a));
                case "drawn":
                {
                    // The person's outline, about as big as the round one, never cutting into the rim.
                    float at = Mathf.Repeat(a * Mathf.Rad2Deg, 360f) / 5f;
                    int i = Mathf.FloorToInt(at) % 72;
                    float r = Mathf.Lerp(d.outline[i], d.outline[(i + 1) % 72], at - Mathf.Floor(at));
                    float mean = 0f;
                    foreach (var x in d.outline) mean += x;
                    mean /= 72f;
                    return Mathf.Max(d.InnerDiameter * .5f + Mathf.Max(2f, d.rimWidth * .4f), apothem * r / Mathf.Max(.2f, mean));
                }
                default: return apothem;
            }
        }

        private static float Polygon(float apothem, int sides, float a, float roundCorners)
        {
            float step = Mathf.PI * 2f / sides;
            float local = Mathf.Repeat(a + step * .5f, step) - step * .5f;
            float r = apothem / Mathf.Cos(local);
            return Mathf.Min(r, apothem / Mathf.Cos(step * .5f) * roundCorners);
        }

        // Origin at the bottom centre, y up, metres.
        public static Mesh Build(CoasterDesign design)
        {
            var d = design.Clamped();
            if (d.IsVessel) return BuildVessel(d);
            if (d.kind == "tray") return FitShapes.Tray(d);
            if (d.kind == "clip") return FitShapes.Clip(d);
            if (d.kind == "model") return FitShapes.Model(d);
            var v = new List<Vector3>();
            var t = new List<int>();
            float ri = d.InnerDiameter * .5f * Mm, floor = d.baseThickness * Mm, top = d.Height * Mm;
            var outer = new Vector3[Around];
            var inner = new Vector3[Around];
            for (int k = 0; k < Around; k++)
            {
                float a = k * Mathf.PI * 2f / Around;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                outer[k] = dir * Outer(d, a) * Mm;
                inner[k] = dir * ri;
            }
            for (int k = 0; k < Around; k++)
            {
                int n = (k + 1) % Around;
                Vector3 o0 = outer[k], o1 = outer[n], i0 = inner[k], i1 = inner[n];
                Vector3 up = Vector3.up * top, fl = Vector3.up * floor;
                Vector3 side = ((o0 + o1) * .5f).normalized;
                Tri(v, t, Vector3.zero, o0, o1, Vector3.down);                    // bottom
                Quad(v, t, o0, o1, o1 + up, o0 + up, side);                      // outer wall
                if (d.rimHeight > .01f)
                {
                    Quad(v, t, i0 + up, i1 + up, o1 + up, o0 + up, Vector3.up);  // top of the rim
                    Quad(v, t, i0 + fl, i1 + fl, i1 + up, i0 + up, -side);       // inner wall
                    Tri(v, t, fl, i0 + fl, i1 + fl, Vector3.up);                 // floor
                }
                else
                {
                    Quad(v, t, i0 + up, i1 + up, o1 + up, o0 + up, Vector3.up);  // flat top
                    Tri(v, t, up, i0 + up, i1 + up, Vector3.up);
                }
            }
            // Ribs: separate closed bars resting slightly into the floor.
            for (int r = 0; r < d.ribs; r++)
            {
                float a = r * Mathf.PI * 2f / d.ribs;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var across = new Vector3(-dir.z, 0f, dir.x) * 1.5f * Mm;
                float from = ri * .3f, to = ri - 1.5f * Mm;
                float y0 = floor - .3f * Mm, y1 = floor + d.ribHeight * Mm;
                Box(v, t, dir * from - across, dir * to - across, dir * to + across, dir * from + across, y0, y1);
            }
            var mesh = new Mesh { name = "Coaster " + d.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------ vessels (F29)

        // How wide the vessel is at height h (0 bottom .. 1 top), relative to its narrowest point.
        public static float[] ProfileRadii(CoasterDesign d, int rings = 32)
        {
            var p = new float[rings];
            for (int i = 0; i < rings; i++)
            {
                float h = i / (rings - 1f);
                switch (d.profile)
                {
                    case "tapered": p[i] = 1f + .28f * h; break;
                    case "curvy": p[i] = 1f + .38f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(h * 1.05f)) - .12f * h; break;
                    case "bowl": p[i] = 1f + .7f * Mathf.Pow(h, .6f); break;
                    case "drawn": p[i] = Mathf.Max(.05f, d.sideProfile[Mathf.Clamp(Mathf.RoundToInt(h * 31f), 0, 31)]); break;
                    default: p[i] = 1f; break;
                }
            }
            float min = float.MaxValue;
            foreach (var x in p) min = Mathf.Min(min, x);
            for (int i = 0; i < rings; i++) p[i] /= Mathf.Max(.01f, min);
            return p;
        }

        // A closed, printable vessel: outer wall, inner wall, rim, floor and bottom (origin at the bottom centre).
        private static Mesh BuildVessel(CoasterDesign d)
        {
            const int around = 64, rings = 32;
            var v = new List<Vector3>();
            var t = new List<int>();
            var p = ProfileRadii(d, rings);
            float content = d.InnerDiameter * .5f * Mm, wall = d.rimWidth * Mm, floor = d.baseThickness * Mm, top = d.Height * Mm;
            var dirs = new Vector3[around];
            for (int k = 0; k < around; k++) { float a = k * Mathf.PI * 2f / around; dirs[k] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)); }
            float Inner(int i) => content * p[i];
            float Outer(int i) => Inner(i) + wall;
            float OuterY(int i) => top * i / (rings - 1f);
            float InnerY(int i) => floor + (top - floor) * i / (rings - 1f);
            for (int k = 0; k < around; k++)
            {
                int n = (k + 1) % around;
                Vector3 a0 = dirs[k], a1 = dirs[n];
                Vector3 side = ((a0 + a1) * .5f).normalized;
                for (int i = 0; i < rings - 1; i++)
                {
                    Quad(v, t, a0 * Outer(i) + Vector3.up * OuterY(i), a1 * Outer(i) + Vector3.up * OuterY(i),
                        a1 * Outer(i + 1) + Vector3.up * OuterY(i + 1), a0 * Outer(i + 1) + Vector3.up * OuterY(i + 1), side);
                    Quad(v, t, a0 * Inner(i) + Vector3.up * InnerY(i), a1 * Inner(i) + Vector3.up * InnerY(i),
                        a1 * Inner(i + 1) + Vector3.up * InnerY(i + 1), a0 * Inner(i + 1) + Vector3.up * InnerY(i + 1), -side);
                }
                int last = rings - 1;
                Quad(v, t, a0 * Inner(last) + Vector3.up * top, a1 * Inner(last) + Vector3.up * top, a1 * Outer(last) + Vector3.up * top, a0 * Outer(last) + Vector3.up * top, Vector3.up); // rim
                Tri(v, t, Vector3.zero, a0 * Outer(0), a1 * Outer(0), Vector3.down);                          // bottom
                Tri(v, t, Vector3.up * floor, a0 * Inner(0) + Vector3.up * floor, a1 * Inner(0) + Vector3.up * floor, Vector3.up); // floor
            }
            var mesh = new Mesh { name = "Vessel " + d.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Unity's front face: Cross(b - a, c - a) points out of the solid.
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

        private static void Box(List<Vector3> v, List<int> t, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float y0, float y1)
        {
            Vector3 lo = Vector3.up * y0, hi = Vector3.up * y1;
            Vector3 c = (p0 + p1 + p2 + p3) * .25f;
            var ring = new[] { p0, p1, p2, p3 };
            Quad(v, t, p0 + lo, p1 + lo, p2 + lo, p3 + lo, Vector3.down);
            Quad(v, t, p0 + hi, p1 + hi, p2 + hi, p3 + hi, Vector3.up);
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = ring[i], b = ring[(i + 1) % 4];
                Quad(v, t, a + lo, b + lo, b + hi, a + hi, ((a + b) * .5f - c).normalized);
            }
        }

        // ------------------------------------------------------------ print file

        // Binary STL in millimetres, z up (printer convention). `scale` maps mesh units to metres.
        public static byte[] Stl(Mesh mesh, float scale = 1f, string header = "Windups")
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            int count = triangles.Length / 3;
            using (var stream = new MemoryStream(84 + count * 50))
            using (var w = new BinaryWriter(stream))
            {
                var title = new byte[80];
                Encoding.ASCII.GetBytes(header, 0, Mathf.Min(header.Length, 80), title, 0);
                w.Write(title);
                w.Write((uint)count);
                for (int i = 0; i < count; i++)
                {
                    Vector3 a = Map(vertices[triangles[i * 3]], scale), b = Map(vertices[triangles[i * 3 + 1]], scale), c = Map(vertices[triangles[i * 3 + 2]], scale);
                    // Swapping y and z mirrors the space, so the winding flips to stay outward.
                    Vector3 n = Vector3.Cross(c - a, b - a).normalized;
                    Write(w, n); Write(w, a); Write(w, c); Write(w, b);
                    w.Write((ushort)0);
                }
                w.Flush();
                return stream.ToArray();
            }
        }

        private static Vector3 Map(Vector3 p, float scale) => new Vector3(p.x, p.z, p.y) * scale * 1000f;
        private static void Write(BinaryWriter w, Vector3 p) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }

        // A rough check for a closed solid: every edge used exactly twice (by position).
        public static bool IsClosed(Mesh mesh)
        {
            var edges = new Dictionary<(Vector3Int, Vector3Int), int>();
            var v = mesh.vertices;
            var t = mesh.triangles;
            Vector3Int Key(Vector3 p) => Vector3Int.RoundToInt(p * 1e5f);
            for (int i = 0; i < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    var a = Key(v[t[i + e]]);
                    var b = Key(v[t[i + (e + 1) % 3]]);
                    var key = a.x < b.x || (a.x == b.x && (a.y < b.y || (a.y == b.y && a.z < b.z))) ? (a, b) : (b, a);
                    edges.TryGetValue(key, out int n);
                    edges[key] = n + 1;
                }
            foreach (var n in edges.Values) if (n != 2) return false;
            return true;
        }
    }
}
