using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Windups.Handoff
{
    // F57 (2026-11-13): the files a made thing leaves the headset as, written from plain arrays (Task.Run safe):
    //  - STL: the closed solid in millimetres, z up (every slicer);
    //  - 3MF: the same solid with a colour per triangle, at most `MaxPrintColours` colours (the model's own texture
    //    reduced by k-means, painted parts as painted), for a multi-colour printer;
    //  - GLB: what is seen in the room at real size (metres, y up) with its textures, for Blender, Windows 3D Viewer,
    //    web viewers and other 3D programs.
    public static class PrintFiles
    {
        public const int MaxPrintColours = 4;

        // ------------------------------------------------------------ STL

        public static byte[] Stl(Vector3[] v, int[] t, float scale, string header)
        {
            int count = t.Length / 3;
            using (var stream = new MemoryStream(84 + count * 50))
            using (var w = new BinaryWriter(stream))
            {
                var title = new byte[80];
                header = header ?? "Roomsmiths";
                Encoding.ASCII.GetBytes(header, 0, Mathf.Min(header.Length, 80), title, 0);
                w.Write(title);
                w.Write((uint)count);
                for (int i = 0; i < count; i++)
                {
                    Vector3 a = Mm(v[t[i * 3]], scale), b = Mm(v[t[i * 3 + 1]], scale), c = Mm(v[t[i * 3 + 2]], scale);
                    // Swapping y and z mirrors the space, so the winding flips to stay outward.
                    Vector3 n = Vector3.Cross(c - a, b - a);
                    float m = n.magnitude;
                    n = m > 1e-12f ? n / m : Vector3.zero;
                    Write(w, n); Write(w, a); Write(w, c); Write(w, b);
                    w.Write((ushort)0);
                }
                w.Flush();
                return stream.ToArray();
            }
        }

        private static Vector3 Mm(Vector3 p, float scale) => new Vector3(p.x, p.z, p.y) * scale * 1000f;
        private static void Write(BinaryWriter w, Vector3 p) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }

        // ------------------------------------------------------------ colours for a colour printer

        // One colour per triangle: `fixedColour` (a painted part's colour, alpha 255) where given, else the average of
        // its vertex colours reduced to at most `k` colours (k-means). Returns the palette; `index` per triangle.
        public static List<Color32> Reduce(Color32[] vertexColours, int[] t, Color32?[] fixedColour, int k, out int[] index)
        {
            int count = t.Length / 3;
            index = new int[count];
            var palette = new List<Color32>();
            // painted colours first (each its own filament)
            var fixedAt = new Dictionary<int, int>();
            if (fixedColour != null)
                for (int i = 0; i < count; i++)
                    if (fixedColour[i].HasValue)
                    {
                        int key = Key(fixedColour[i].Value);
                        if (!fixedAt.ContainsKey(key)) { fixedAt[key] = palette.Count; palette.Add(fixedColour[i].Value); }
                    }
            int free = Mathf.Max(1, k - palette.Count);
            var tri = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                if (vertexColours == null) { tri[i] = new Vector3(.92f, .86f, .75f); continue; }
                Color32 a = vertexColours[t[i * 3]], b = vertexColours[t[i * 3 + 1]], c = vertexColours[t[i * 3 + 2]];
                tri[i] = new Vector3(a.r + b.r + c.r, a.g + b.g + c.g, a.b + b.b + c.b) / (3f * 255f);
            }
            // k-means++ start, a few rounds on a sample, then every triangle to its nearest centre
            var rng = new System.Random(7);
            var sample = new List<Vector3>();
            for (int i = 0; i < count; i++) if ((fixedColour == null || !fixedColour[i].HasValue) && (count < 6000 || rng.Next(count) < 6000)) sample.Add(tri[i]);
            var centres = new List<Vector3>();
            if (sample.Count > 0)
            {
                centres.Add(sample[rng.Next(sample.Count)]);
                while (centres.Count < free && centres.Count < sample.Count)
                {
                    float total = 0f;
                    var weights = new float[sample.Count];
                    for (int i = 0; i < sample.Count; i++) { float d = Nearest(centres, sample[i], out _); weights[i] = d; total += d; }
                    if (total < 1e-6f) break; // fewer distinct colours than k
                    float pick = (float)rng.NextDouble() * total;
                    int chosen = 0;
                    for (; chosen < sample.Count - 1 && pick > weights[chosen]; chosen++) pick -= weights[chosen];
                    centres.Add(sample[chosen]);
                }
                for (int round = 0; round < 8; round++)
                {
                    var sum = new Vector3[centres.Count];
                    var n = new int[centres.Count];
                    foreach (var s in sample) { Nearest(centres, s, out int c); sum[c] += s; n[c]++; }
                    for (int c = 0; c < centres.Count; c++) if (n[c] > 0) centres[c] = sum[c] / n[c];
                }
            }
            // A colour only a few triangles take is the blend along a border between two colours: dropped.
            if (centres.Count > 1)
            {
                var members = new int[centres.Count];
                foreach (var s in sample) { Nearest(centres, s, out int c); members[c]++; }
                for (int c = centres.Count - 1; c >= 0 && centres.Count > 1; c--)
                    if (members[c] < sample.Count * .04f) centres.RemoveAt(c);
            }
            int offset = palette.Count;
            foreach (var c in centres) palette.Add(new Color32((byte)Mathf.Clamp(c.x * 255f, 0, 255), (byte)Mathf.Clamp(c.y * 255f, 0, 255), (byte)Mathf.Clamp(c.z * 255f, 0, 255), 255));
            for (int i = 0; i < count; i++)
            {
                if (fixedColour != null && fixedColour[i].HasValue) { index[i] = fixedAt[Key(fixedColour[i].Value)]; continue; }
                Nearest(centres, tri[i], out int c);
                index[i] = centres.Count > 0 ? offset + c : 0;
            }
            if (palette.Count == 0) palette.Add(new Color32(235, 220, 190, 255));
            return palette;
        }

        private static int Key(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;

        private static float Nearest(List<Vector3> centres, Vector3 p, out int best)
        {
            best = 0;
            float d = float.MaxValue;
            for (int i = 0; i < centres.Count; i++) { float e = (centres[i] - p).sqrMagnitude; if (e < d) { d = e; best = i; } }
            return d;
        }

        // ------------------------------------------------------------ 3MF with a colour per triangle

        public static byte[] ThreeMf(Vector3[] v, int[] t, int[] colourIndex, IList<Color32> palette, IList<string> names, float scale, string title)
        {
            var ci = CultureInfo.InvariantCulture;
            var xml = new StringBuilder(v.Length * 60 + t.Length * 20);
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            xml.Append("<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">\n");
            xml.Append("<metadata name=\"Title\">").Append(System.Security.SecurityElement.Escape(title ?? "Roomsmiths")).Append("</metadata>\n");
            xml.Append("<metadata name=\"Application\">Roomsmiths</metadata>\n<resources>\n<basematerials id=\"1\">\n");
            for (int i = 0; i < palette.Count; i++)
            {
                var c = palette[i];
                xml.Append("<base name=\"").Append(System.Security.SecurityElement.Escape(names != null && i < names.Count ? names[i] : "colour " + (i + 1)))
                   .Append("\" displaycolor=\"#").Append(c.r.ToString("X2")).Append(c.g.ToString("X2")).Append(c.b.ToString("X2")).Append("FF\"/>\n");
            }
            xml.Append("</basematerials>\n<object id=\"2\" type=\"model\" pid=\"1\" pindex=\"0\">\n<mesh>\n<vertices>\n");
            foreach (var p in v)
            {
                var q = Mm(p, scale);
                xml.Append("<vertex x=\"").Append(q.x.ToString("0.###", ci)).Append("\" y=\"").Append(q.y.ToString("0.###", ci))
                   .Append("\" z=\"").Append(q.z.ToString("0.###", ci)).Append("\"/>\n");
            }
            xml.Append("</vertices>\n<triangles>\n");
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                if (a == b || b == c || a == c) continue;
                xml.Append("<triangle v1=\"").Append(a).Append("\" v2=\"").Append(c).Append("\" v3=\"").Append(b)
                   .Append("\" pid=\"1\" p1=\"").Append(colourIndex != null ? colourIndex[i / 3] : 0).Append("\"/>\n");
            }
            xml.Append("</triangles>\n</mesh>\n</object>\n</resources>\n<build>\n<item objectid=\"2\"/>\n</build>\n</model>\n");
            const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                                 "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                                 "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>";
            const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                                "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>";
            return FitShapes.Zip(new List<(string, byte[])>
            {
                ("[Content_Types].xml", Encoding.UTF8.GetBytes(types)),
                ("_rels/.rels", Encoding.UTF8.GetBytes(rels)),
                ("3D/3dmodel.model", Encoding.UTF8.GetBytes(xml.ToString()))
            });
        }

        // ------------------------------------------------------------ GLB (binary glTF 2.0)

        public sealed class GlbPart
        {
            public string name = "part";
            public int[] triangles;        // into the shared vertex arrays
            public Color colour = Color.white; // base colour factor (linear is not needed: glTF factors are linear, close enough for paint)
            public byte[] image;           // PNG or JPEG bytes, or null
            public string mime = "image/png";
            public int texCoord;           // 0: the model's own layout; 1: real-size projection (a tile repeats)
            public float roughness = .6f;
        }

        // Vertices in metres, Unity space (left-handed, y up): written right-handed (x mirrored, winding flipped, v flipped).
        public static byte[] Glb(string name, Vector3[] v, Vector3[] n, Vector2[] uv0, Vector2[] uv1, Color32[] colours, IList<GlbPart> parts)
        {
            // texture sets must start at 0: a real-size layout alone becomes set 0
            bool shifted = (uv0 == null || uv0.Length != v.Length) && uv1 != null && uv1.Length == v.Length;
            if (shifted) { uv0 = uv1; uv1 = null; }
            var bin = new MemoryStream();
            var views = new List<string>();
            var accessors = new List<string>();
            var ci = CultureInfo.InvariantCulture;
            string F(float x) => x.ToString("0.######", ci);

            int View(byte[] data, int? target)
            {
                while (bin.Length % 4 != 0) bin.WriteByte(0);
                long offset = bin.Length;
                bin.Write(data, 0, data.Length);
                views.Add("{\"buffer\":0,\"byteOffset\":" + offset + ",\"byteLength\":" + data.Length + (target.HasValue ? ",\"target\":" + target.Value : "") + "}");
                return views.Count - 1;
            }
            int Accessor(int view, int componentType, int count, string type, string extra = "")
            {
                accessors.Add("{\"bufferView\":" + view + ",\"componentType\":" + componentType + ",\"count\":" + count + ",\"type\":\"" + type + "\"" + extra + "}");
                return accessors.Count - 1;
            }
            byte[] Floats(int count, int width, Func<int, int, float> get)
            {
                var b = new byte[count * width * 4];
                for (int i = 0; i < count; i++)
                    for (int k = 0; k < width; k++) Buffer.BlockCopy(BitConverter.GetBytes(get(i, k)), 0, b, (i * width + k) * 4, 4);
                return b;
            }

            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            foreach (var p in v) { var q = new Vector3(-p.x, p.y, p.z); lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
            int pos = Accessor(View(Floats(v.Length, 3, (i, k) => k == 0 ? -v[i].x : v[i][k]), 34962), 5126, v.Length, "VEC3",
                ",\"min\":[" + F(lo.x) + "," + F(lo.y) + "," + F(lo.z) + "],\"max\":[" + F(hi.x) + "," + F(hi.y) + "," + F(hi.z) + "]");
            var attributes = new StringBuilder("\"POSITION\":" + pos);
            if (n != null && n.Length == v.Length)
                attributes.Append(",\"NORMAL\":").Append(Accessor(View(Floats(n.Length, 3, (i, k) => k == 0 ? -n[i].x : n[i][k]), 34962), 5126, n.Length, "VEC3"));
            if (uv0 != null && uv0.Length == v.Length)
                attributes.Append(",\"TEXCOORD_0\":").Append(Accessor(View(Floats(uv0.Length, 2, (i, k) => k == 0 ? uv0[i].x : 1f - uv0[i].y), 34962), 5126, uv0.Length, "VEC2"));
            if (uv1 != null && uv1.Length == v.Length)
                attributes.Append(",\"TEXCOORD_1\":").Append(Accessor(View(Floats(uv1.Length, 2, (i, k) => k == 0 ? uv1[i].x : 1f - uv1[i].y), 34962), 5126, uv1.Length, "VEC2"));
            if (colours != null && colours.Length == v.Length)
            {
                var c = new byte[colours.Length * 4];
                for (int i = 0; i < colours.Length; i++) { c[i * 4] = colours[i].r; c[i * 4 + 1] = colours[i].g; c[i * 4 + 2] = colours[i].b; c[i * 4 + 3] = 255; }
                attributes.Append(",\"COLOR_0\":").Append(Accessor(View(c, 34962), 5121, colours.Length, "VEC4", ",\"normalized\":true"));
            }

            var primitives = new List<string>();
            var materials = new List<string>();
            var images = new List<string>();
            var textures = new List<string>();
            var imageOf = new Dictionary<byte[], int>();
            foreach (var part in parts)
            {
                if (part.triangles == null || part.triangles.Length < 3) continue;
                var idx = new byte[part.triangles.Length * 4];
                for (int i = 0; i + 2 < part.triangles.Length; i += 3)
                {
                    // mirrored: the winding flips
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)part.triangles[i]), 0, idx, i * 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)part.triangles[i + 2]), 0, idx, (i + 1) * 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)part.triangles[i + 1]), 0, idx, (i + 2) * 4, 4);
                }
                int indices = Accessor(View(idx, 34963), 5125, part.triangles.Length, "SCALAR");
                string texture = "";
                if (part.image != null)
                {
                    if (!imageOf.TryGetValue(part.image, out int image))
                    {
                        images.Add("{\"bufferView\":" + View(part.image, null) + ",\"mimeType\":\"" + part.mime + "\"}");
                        textures.Add("{\"source\":" + (images.Count - 1) + ",\"sampler\":0}");
                        image = textures.Count - 1;
                        imageOf[part.image] = image;
                    }
                    int set = shifted ? 0 : part.texCoord;
                    texture = ",\"baseColorTexture\":{\"index\":" + image + (set != 0 ? ",\"texCoord\":" + set : "") + "}";
                }
                Color c = part.colour;
                materials.Add("{\"name\":\"" + Escape(part.name) + "\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[" + F(c.r) + "," + F(c.g) + "," + F(c.b) + ",1]" + texture +
                              ",\"metallicFactor\":0,\"roughnessFactor\":" + F(Mathf.Clamp01(part.roughness)) + "}}");
                primitives.Add("{\"attributes\":{" + attributes + "},\"indices\":" + indices + ",\"material\":" + (materials.Count - 1) + "}");
            }
            while (bin.Length % 4 != 0) bin.WriteByte(0);

            var json = new StringBuilder();
            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"Roomsmiths\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],");
            json.Append("\"nodes\":[{\"name\":\"").Append(Escape(name)).Append("\",\"mesh\":0}],");
            json.Append("\"meshes\":[{\"name\":\"").Append(Escape(name)).Append("\",\"primitives\":[").Append(string.Join(",", primitives)).Append("]}],");
            json.Append("\"materials\":[").Append(string.Join(",", materials)).Append("],");
            if (images.Count > 0)
            {
                json.Append("\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}],");
                json.Append("\"images\":[").Append(string.Join(",", images)).Append("],");
                json.Append("\"textures\":[").Append(string.Join(",", textures)).Append("],");
            }
            json.Append("\"accessors\":[").Append(string.Join(",", accessors)).Append("],");
            json.Append("\"bufferViews\":[").Append(string.Join(",", views)).Append("],");
            json.Append("\"buffers\":[{\"byteLength\":").Append(bin.Length).Append("}]}");
            var jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
            int jsonPadded = (jsonBytes.Length + 3) / 4 * 4;
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonPadded + 8 + bin.Length));
                w.Write((uint)jsonPadded); w.Write(0x4E4F534Au); w.Write(jsonBytes);
                for (int i = jsonBytes.Length; i < jsonPadded; i++) w.Write((byte)0x20);
                w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
                w.Flush();
                return stream.ToArray();
            }
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Real-size box projection (one tile every `tile` metres) for TEXCOORD_1: the painted textures repeat on it.
        public static Vector2[] BoxUv(Vector3[] v, Vector3[] n, float tile)
        {
            var uv = new Vector2[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = v[i] / tile, a = n != null && i < n.Length ? new Vector3(Mathf.Abs(n[i].x), Mathf.Abs(n[i].y), Mathf.Abs(n[i].z)) : Vector3.up;
                uv[i] = a.y >= a.x && a.y >= a.z ? new Vector2(p.x, p.z) : a.x >= a.z ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y);
            }
            return uv;
        }
    }
}
