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
    // Static mesh interchange. Coordinates use metres and right-handed Y-up space.
    public static class ModelExchange
    {
        static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        public static byte[] Obj(Vector3[] vertices, Vector3[] normals, Vector2[] uv0, Vector2[] uv1,
            IList<PrintFiles.GlbPart> parts)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                var obj = new StringBuilder("# Roomsmiths. Units: metres. Y up.\nmtllib model.mtl\n");
                var mtl = new StringBuilder();
                foreach (var v in vertices) obj.AppendLine($"v {F(-v.x)} {F(v.y)} {F(v.z)}");
                // F87: each UV set is written once and shared by the parts that use it (it was written again per part).
                var uvStart = new Dictionary<int, int>();
                int uvWritten = 0;
                for (int p = 0; p < parts.Count; p++)
                {
                    var part = parts[p];
                    var uv = part.texCoord == 1 ? uv1 : uv0;
                    bool mapped = uv != null && uv.Length == vertices.Length;
                    if (mapped && !uvStart.ContainsKey(part.texCoord))
                    {
                        uvStart[part.texCoord] = uvWritten;
                        foreach (var t in uv) obj.AppendLine($"vt {F(t.x)} {F(t.y)}");
                        uvWritten += uv.Length;
                    }
                    int uvOffset = mapped ? uvStart[part.texCoord] : 0;
                    obj.AppendLine($"g part{p}\nusemtl material{p}");
                    var c = part.colour;
                    mtl.AppendLine($"newmtl material{p}\nKd {F(c.r)} {F(c.g)} {F(c.b)}\nd 1");
                    if (part.image != null && mapped)
                    {
                        string filename = $"texture{p}." + (part.mime == "image/jpeg" ? "jpg" : "png");
                        using (var stream = zip.CreateEntry(filename).Open()) stream.Write(part.image, 0, part.image.Length);
                        mtl.AppendLine("map_Kd " + filename);
                    }
                    string Index(int i) => mapped ? $"{i + 1}/{i + uvOffset + 1}" : (i + 1).ToString();
                    for (int i = 0; i < part.triangles.Length; i += 3)
                        obj.AppendLine($"f {Index(part.triangles[i])} {Index(part.triangles[i + 2])} {Index(part.triangles[i + 1])}");
                }
                using (var writer = new StreamWriter(zip.CreateEntry("model.obj").Open())) writer.Write(obj);
                using (var writer = new StreamWriter(zip.CreateEntry("model.mtl").Open())) writer.Write(mtl);
            }
            return buffer.ToArray();
        }

        sealed class Node
        {
            public string name;
            public object[] values;
            public List<Node> children = new List<Node>();
            public Node(string name, params object[] values) { this.name = name; this.values = values; }
            public Node Add(Node child) { children.Add(child); return this; }
        }
        static Node P(string name, string type, params object[] value) => new Node("P", new object[] {
            name, type, type == "double" ? "Number" : type == "int" ? "Integer" : "", "" }.Concat(value).ToArray());

        public static byte[] Fbx(Vector3[] vertices, Vector3[] normals, Vector2[] uv0, Vector2[] uv1,
            IList<PrintFiles.GlbPart> parts)
        {
            var settings = new Node("GlobalSettings").Add(new Node("Version", 1000)).Add(new Node("Properties70")
                .Add(P("UpAxis", "int", 1)).Add(P("UpAxisSign", "int", 1))
                .Add(P("FrontAxis", "int", 2)).Add(P("FrontAxisSign", "int", -1))
                .Add(P("CoordAxis", "int", 0)).Add(P("CoordAxisSign", "int", 1))
                .Add(P("UnitScaleFactor", "double", 100d)).Add(P("OriginalUnitScaleFactor", "double", 100d)));
            var objects = new Node("Objects");
            var connections = new Node("Connections");
            for (int p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                long model = 1000 + p * 10, geometry = model + 1, material = model + 2;
                // F87 (Quest: "your FBX exceeds the web size limit", 23 MB): each part keeps only the vertices it uses
                // (every part used to carry the whole model's vertices, normals and UVs).
                var remap = new Dictionary<int, int>();
                var used = new List<int>();
                var indices = new int[part.triangles.Length];
                int Local(int index)
                {
                    if (!remap.TryGetValue(index, out int local)) { local = used.Count; remap[index] = local; used.Add(index); }
                    return local;
                }
                for (int i = 0; i < indices.Length; i += 3)
                { indices[i] = Local(part.triangles[i]); indices[i + 1] = Local(part.triangles[i + 2]); indices[i + 2] = -Local(part.triangles[i + 1]) - 1; }
                var v = new double[used.Count * 3];
                for (int i = 0; i < used.Count; i++) { var t = vertices[used[i]]; v[i * 3] = -t.x; v[i * 3 + 1] = t.y; v[i * 3 + 2] = t.z; }
                var geo = new Node("Geometry", geometry, $"Geometry::part{p}", "Mesh")
                    .Add(new Node("Vertices", v)).Add(new Node("PolygonVertexIndex", indices));
                var layer = new Node("Layer", 0).Add(new Node("Version", 100));
                Node Element(string type) => new Node("LayerElement").Add(new Node("Type", type)).Add(new Node("TypedIndex", 0));
                if (normals != null && normals.Length == vertices.Length)
                {
                    geo.Add(new Node("LayerElementNormal", 0).Add(new Node("Version", 101)).Add(new Node("Name", ""))
                        .Add(new Node("MappingInformationType", "ByVertice")).Add(new Node("ReferenceInformationType", "Direct"))
                        .Add(new Node("Normals", used.SelectMany(i => new[] { -(double)normals[i].x, normals[i].y, normals[i].z }).ToArray())));
                    layer.Add(Element("LayerElementNormal"));
                }
                var uv = part.texCoord == 1 ? uv1 : uv0;
                if (uv != null && uv.Length == vertices.Length)
                {
                    geo.Add(new Node("LayerElementUV", 0).Add(new Node("Version", 101)).Add(new Node("Name", "UVMap"))
                        .Add(new Node("MappingInformationType", "ByVertice")).Add(new Node("ReferenceInformationType", "Direct"))
                        .Add(new Node("UV", used.SelectMany(i => new[] { (double)uv[i].x, uv[i].y }).ToArray())));
                    layer.Add(Element("LayerElementUV"));
                }
                geo.Add(new Node("LayerElementMaterial", 0).Add(new Node("Version", 101)).Add(new Node("Name", ""))
                    .Add(new Node("MappingInformationType", "AllSame")).Add(new Node("ReferenceInformationType", "IndexToDirect"))
                    .Add(new Node("Materials", new int[] { 0 })));
                layer.Add(Element("LayerElementMaterial"));
                geo.Add(layer);
                objects.Add(geo).Add(new Node("Model", model, $"Model::part{p}", "Mesh")
                    .Add(new Node("Version", 232)).Add(new Node("Properties70")).Add(new Node("Shading", true)).Add(new Node("Culling", "CullingOff")));
                var c = part.colour;
                objects.Add(new Node("Material", material, $"Material::material{p}", "")
                    .Add(new Node("Version", 102)).Add(new Node("ShadingModel", "phong"))
                    .Add(new Node("Properties70").Add(P("DiffuseColor", "Color", (double)c.r, (double)c.g, (double)c.b))));
                connections.Add(new Node("C", "OO", model, 0L)).Add(new Node("C", "OO", geometry, model)).Add(new Node("C", "OO", material, model));
                if (part.image != null && uv != null)
                {
                    long texture = model + 3, video = model + 4;
                    string file = $"texture{p}." + (part.mime == "image/jpeg" ? "jpg" : "png");
                    objects.Add(new Node("Video", video, $"Video::texture{p}", "Clip").Add(new Node("Type", "Clip"))
                        .Add(new Node("Filename", file)).Add(new Node("RelativeFilename", file)).Add(new Node("Content", part.image)));
                    objects.Add(new Node("Texture", texture, $"Texture::texture{p}", "").Add(new Node("Type", "TextureVideoClip"))
                        .Add(new Node("Version", 202)).Add(new Node("TextureName", $"Texture::texture{p}"))
                        .Add(new Node("Media", $"Video::texture{p}")).Add(new Node("FileName", file)).Add(new Node("RelativeFilename", file)));
                    connections.Add(new Node("C", "OO", video, texture)).Add(new Node("C", "OP", texture, material, "DiffuseColor"));
                }
            }
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")); writer.Write(7400);
            WriteNode(writer, new Node("FBXHeaderExtension").Add(new Node("FBXHeaderVersion", 1003)).Add(new Node("FBXVersion", 7400)).Add(new Node("Creator", "Roomsmiths")));
            WriteNode(writer, settings); WriteNode(writer, objects); WriteNode(writer, connections); writer.Write(new byte[13]);
            return output.ToArray();
        }

        // F87: large arrays go zlib-compressed (FBX array encoding 1); small ones stay plain.
        static void WriteArray(BinaryWriter writer, char type, int count, Array values, int size)
        {
            var raw = new byte[count * size];
            Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
            writer.Write((byte)type); writer.Write(count);
            if (raw.Length < 128) { writer.Write(0); writer.Write(raw.Length); writer.Write(raw); return; }
            var packed = Zlib(raw);
            writer.Write(1); writer.Write(packed.Length); writer.Write(packed);
        }

        // RFC 1950: a 2-byte header, the deflate stream, then the Adler-32 of the raw bytes (big-endian).
        public static byte[] Zlib(byte[] raw)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78); output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) deflate.Write(raw, 0, raw.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < raw.Length; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            output.WriteByte((byte)(adler >> 24)); output.WriteByte((byte)(adler >> 16)); output.WriteByte((byte)(adler >> 8)); output.WriteByte((byte)adler);
            return output.ToArray();
        }

        static void WriteNode(BinaryWriter writer, Node node)
        {
            long start = writer.BaseStream.Position;
            writer.Write(0u); writer.Write(node.values.Length); writer.Write(0u);
            var name = Encoding.UTF8.GetBytes(node.name); writer.Write((byte)name.Length); writer.Write(name);
            long properties = writer.BaseStream.Position;
            foreach (var value in node.values)
            {
                if (value is int i) { writer.Write((byte)'I'); writer.Write(i); }
                else if (value is long l) { writer.Write((byte)'L'); writer.Write(l); }
                else if (value is double d) { writer.Write((byte)'D'); writer.Write(d); }
                else if (value is bool b) { writer.Write((byte)'C'); writer.Write(b); }
                else if (value is string s)
                {
                    int separator = s.IndexOf("::", StringComparison.Ordinal);
                    if (separator > 0) s = s.Substring(separator + 2) + "\0\u0001" + s.Substring(0, separator);
                    var bytes = Encoding.UTF8.GetBytes(s); writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes);
                }
                else if (value is byte[] bytes) { writer.Write((byte)'R'); writer.Write(bytes.Length); writer.Write(bytes); }
                else if (value is double[] doubles) WriteArray(writer, 'd', doubles.Length, doubles, 8);
                else if (value is int[] ints) WriteArray(writer, 'i', ints.Length, ints, 4);
                else throw new InvalidDataException("Unsupported FBX property");
            }
            long propertiesEnd = writer.BaseStream.Position;
            foreach (var child in node.children) WriteNode(writer, child);
            if (node.children.Count > 0) writer.Write(new byte[13]);
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start; writer.Write(checked((uint)end)); writer.BaseStream.Position = start + 8;
            writer.Write(checked((uint)(propertiesEnd - properties))); writer.BaseStream.Position = end;
        }
    }
}
