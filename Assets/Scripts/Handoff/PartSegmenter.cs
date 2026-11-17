using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups.Handoff
{
    // F83: finds a model's parts (beak, wing, handle...) with Meta's SAM 3.1, which only sees pictures:
    //   1. the model is drawn as plain clay from 8 directions, each pixel knowing its triangle (a small software
    //      rasteriser: no camera, no GPU readback, nothing on the main thread);
    //   2. every picture is sent once per part word ("beak"); SAM answers with masks;
    //   3. each mask votes for the triangles it covers; a triangle takes the word with the most votes, what no
    //      word claims is the rest ("body"); crumbs join their surroundings; separate pieces of one word are
    //      parts of their own ("wing", "wing 2").
    // Measured in the spike of 2026-11-14 (WebLab/sam-spike, a stand-in bird with known parts, 4 views): 91% of
    // the surface right on untextured clay; a request takes 0.5-2.5 s, 72 of them 14 s at 8 at a time.
    // Only the web requests touch the main thread (UnityWebRequest); drawing, PNG packing, request bodies,
    // mask decoding and voting run in Task.Run.
    public static class PartSegmenter
    {
        public const int Size = 768;                 // the picture's side (as in the spike)
        public const float Elevation = 22f, Fill = .62f;
        public static readonly float[] Azimuths = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
        public static int Parallel = 10;             // requests at a time
        public const float MinConfidence = .5f;      // SAM's own score for a mask
        public const float MaxShare = .7f;           // a mask over this much of the object is "the whole thing", not a part
        public const int MaxWords = 5, MaxPieces = 4;
        public const float SamePart = .04f;          // pieces of one word nearer than this (of the model's longest side) are one part

        // Checks: the answer's text for (view, word) instead of a web request ("" = nothing found).
        public static Func<int, string, string> Stub;
        // Checks: the pictures sent are also written here.
        public static string DumpFolder;

        public sealed class Job
        {
            public string[] words;            // what to look for ("beak", "wing"), at most MaxWords
            public string[] spoken;           // the same words in the person's language (by word), or null
            public PartField field;           // the result; null when no part was found
            public string error;
            public bool done, cancelled;
            public int requests, answered, failed, masks;
            public float drawMs, askSeconds, voteMs;
            public string report = "";
        }

        private sealed class View
        {
            public int[] id;                  // per pixel: its triangle, or -1
            public int[] pixels;              // per triangle: how many pixels show it
            public int objectPixels;
            public byte[] png;
        }

        public static IEnumerator Run(MonoBehaviour host, Vector3[] vertices, int[] triangles, Job job)
        {
            float started = Time.realtimeSinceStartup;
            var words = new List<string>();
            var spoken = new List<string>();
            for (int i = 0; job.words != null && i < job.words.Length && words.Count < MaxWords; i++)
            {
                string w = MetaSam.Plain(job.words[i]);
                if (w.Length < 2 || w == PartField.Rest || words.Contains(w)) continue;
                words.Add(w);
                spoken.Add(job.spoken != null && i < job.spoken.Length ? job.spoken[i] ?? "" : "");
            }
            if (words.Count == 0 || vertices == null || triangles == null || triangles.Length < 3) { job.report = "no part words"; job.done = true; yield break; }
            string key = null;
            if (Stub == null && !WindupsDirectCredentials.TryGetMetaKey(out key)) { job.error = "Meta SAM is not set up in this build."; job.done = true; yield break; }

            // 1. The pictures and the request bodies.
            string dump = DumpFolder;
            var draw = Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var views = DrawAll(vertices, triangles);
                var bodies = new byte[views.Length * words.Count][];
                if (Stub == null)
                    for (int v = 0; v < views.Length; v++)
                        for (int w = 0; w < words.Count; w++) bodies[v * words.Count + w] = MetaSam.RequestBody(views[v].png, words[w]);
                if (!string.IsNullOrEmpty(dump))
                {
                    Directory.CreateDirectory(dump);
                    for (int v = 0; v < views.Length; v++) File.WriteAllBytes(Path.Combine(dump, "view_" + v + ".png"), views[v].png);
                }
                return (views, bodies, ms: (float)watch.Elapsed.TotalMilliseconds);
            });
            while (!draw.IsCompleted) yield return null;
            if (draw.IsFaulted) { job.error = "drawing: " + draw.Exception?.GetBaseException().Message; job.done = true; yield break; }
            var (drawn, requestBodies, drawMs) = draw.Result;
            job.drawMs = drawMs;

            // 2. Ask, a few at a time.
            int total = drawn.Length * words.Count;
            var answers = new string[total];
            job.requests = total;
            float askFrom = Time.realtimeSinceStartup;
            if (Stub != null)
            {
                for (int i = 0; i < total; i++) answers[i] = Stub(i / words.Count, words[i % words.Count]);
                job.answered = total;
            }
            else
            {
                int next = 0, active = 0;
                while ((next < total || active > 0) && !job.cancelled)
                {
                    while (active < Parallel && next < total)
                    {
                        int index = next++;
                        active++;
                        host.StartCoroutine(Ask(key, requestBodies[index], text =>
                        {
                            answers[index] = text;
                            if (text != null) job.answered++; else job.failed++;
                            active--;
                        }));
                    }
                    yield return null;
                }
            }
            job.askSeconds = Time.realtimeSinceStartup - askFrom;
            if (job.cancelled || host == null) { job.done = true; yield break; }
            if (job.answered == 0) { job.error = "Meta SAM did not answer."; job.done = true; yield break; }

            // 3. Votes -> a part per triangle -> the field.
            bool raw = Stub == null;
            var vote = Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var field = Vote(vertices, triangles, drawn, words, spoken, answers, raw, out int masks, out string report);
                return (field, masks, report, ms: (float)watch.Elapsed.TotalMilliseconds);
            });
            while (!vote.IsCompleted) yield return null;
            if (vote.IsFaulted) { job.error = "voting: " + vote.Exception?.GetBaseException().Message; job.done = true; yield break; }
            job.field = vote.Result.field;
            job.masks = vote.Result.masks;
            job.voteMs = vote.Result.ms;
            job.report = $"{words.Count} words x {drawn.Length} views = {total} requests ({job.failed} failed), {job.masks} masks; drawn in {job.drawMs:0} ms, asked in {job.askSeconds:0.0} s, voted in {job.voteMs:0} ms, {Time.realtimeSinceStartup - started:0.0} s in all. {vote.Result.report}";
            job.done = true;
        }

        private static IEnumerator Ask(string key, byte[] body, Action<string> done)
        {
            string text = null;
            for (int attempt = 0; attempt < 2 && text == null; attempt++)
            {
                using (var http = new UnityWebRequest(MetaSam.Endpoint, "POST"))
                {
                    http.uploadHandler = new UploadHandlerRaw(body);
                    http.downloadHandler = new DownloadHandlerBuffer();
                    http.SetRequestHeader("Content-Type", "application/json");
                    http.SetRequestHeader("Authorization", "Bearer " + key);
                    http.timeout = 30;
                    yield return http.SendWebRequest();
                    if (http.result == UnityWebRequest.Result.Success) text = http.downloadHandler.text;
                    else
                    {
                        if (attempt == 1 || (http.responseCode != 429 && http.responseCode < 500 && http.responseCode != 0))
                        { Debug.LogWarning("[Windups Parts] SAM " + http.responseCode + " " + http.error); break; }
                    }
                }
                if (text == null) yield return new WaitForSecondsRealtime(.6f);
            }
            done(text);
        }

        // ------------------------------------------------------------ 1. drawing

        private static View[] DrawAll(Vector3[] source, int[] tri)
        {
            // Centred, its longest side 1.
            Vector3 lo = source[0], hi = source[0];
            foreach (var p in source) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            Vector3 centre = (lo + hi) * .5f;
            float longest = Mathf.Max(1e-9f, Mathf.Max(hi.x - lo.x, Mathf.Max(hi.y - lo.y, hi.z - lo.z)));
            var v = new Vector3[source.Length];
            for (int i = 0; i < v.Length; i++) v[i] = (source[i] - centre) / longest;
            var normals = new Vector3[v.Length];
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                Vector3 f = Vector3.Cross(v[tri[i + 1]] - v[tri[i]], v[tri[i + 2]] - v[tri[i]]);
                normals[tri[i]] += f; normals[tri[i + 1]] += f; normals[tri[i + 2]] += f;
            }
            for (int i = 0; i < normals.Length; i++) { float m = normals[i].magnitude; normals[i] = m > 1e-20f ? normals[i] / m : Vector3.up; }

            int big = Size * 2;                                   // drawn twice the size, then halved (soft edges)
            var depth = new float[big * big];
            var ids = new int[big * big];
            var shade = new byte[big * big];
            var px = new float[v.Length]; var py = new float[v.Length]; var pz = new float[v.Length];
            var views = new View[Azimuths.Length];
            for (int n = 0; n < views.Length; n++)
            {
                float a = Azimuths[n] * Mathf.Deg2Rad, e = Elevation * Mathf.Deg2Rad;
                var back = new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e));   // from the model to the eye
                var right = Vector3.Cross(Vector3.up, back).normalized;
                var up = Vector3.Cross(back, right);
                var light = (back + right * .45f + up * .55f).normalized;
                float scale = big * Fill;
                for (int i = 0; i < v.Length; i++)
                {
                    px[i] = big * .5f + Vector3.Dot(v[i], right) * scale;
                    py[i] = big * .5f - Vector3.Dot(v[i], up) * scale;
                    pz[i] = Vector3.Dot(v[i], back);
                }
                for (int i = 0; i < depth.Length; i++) { depth[i] = float.NegativeInfinity; ids[i] = -1; }
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int i0 = tri[t], i1 = tri[t + 1], i2 = tri[t + 2];
                    float ax = px[i0], ay = py[i0], bx = px[i1], by = py[i1], cx = px[i2], cy = py[i2];
                    float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                    if (Mathf.Abs(area) < 1e-9f) continue;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx)))), x1 = Mathf.Min(big - 1, Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy)))), y1 = Mathf.Min(big - 1, Mathf.CeilToInt(Mathf.Max(ay, Mathf.Max(by, cy))));
                    float inv = 1f / area;
                    // Light per corner (smooth across the triangle); a face seen from behind is lit the same.
                    float l0 = Lit(normals[i0], light, back), l1 = Lit(normals[i1], light, back), l2 = Lit(normals[i2], light, back);
                    int index = t / 3;
                    for (int y = y0; y <= y1; y++)
                    {
                        float sy = y + .5f;
                        for (int x = x0; x <= x1; x++)
                        {
                            float sx = x + .5f;
                            float w0 = ((bx - sx) * (cy - sy) - (by - sy) * (cx - sx)) * inv;
                            float w1 = ((cx - sx) * (ay - sy) - (cy - sy) * (ax - sx)) * inv;
                            float w2 = 1f - w0 - w1;
                            if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                            float z = pz[i0] * w0 + pz[i1] * w1 + pz[i2] * w2;
                            int k = y * big + x;
                            if (z <= depth[k]) continue;
                            depth[k] = z;
                            ids[k] = index;
                            shade[k] = (byte)Mathf.Clamp((.42f + .58f * Mathf.Max(0f, l0 * w0 + l1 * w1 + l2 * w2)) * 255f, 0f, 255f);
                        }
                    }
                }
                // Halved: the clay colour (226, 219, 208) times the light, on a pale grey ground (236).
                var view = new View { id = new int[Size * Size], pixels = new int[tri.Length / 3] };
                var rgb = new byte[Size * Size * 3];
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        int q = (y * 2) * big + x * 2, k = y * Size + x;
                        float r = 0f, g = 0f, b = 0f;
                        for (int s = 0; s < 4; s++)
                        {
                            int p = q + (s & 1) + (s >> 1) * big;
                            if (ids[p] < 0) { r += 236f; g += 236f; b += 236f; }
                            else { float l = shade[p] / 255f; r += 226f * l; g += 219f * l; b += 208f * l; }
                        }
                        rgb[k * 3] = (byte)(r * .25f); rgb[k * 3 + 1] = (byte)(g * .25f); rgb[k * 3 + 2] = (byte)(b * .25f);
                        int id = ids[q];
                        view.id[k] = id;
                        if (id >= 0) { view.pixels[id]++; view.objectPixels++; }
                    }
                view.png = Png(rgb, Size, Size);
                views[n] = view;
            }
            return views;
        }

        private static float Lit(Vector3 normal, Vector3 light, Vector3 back)
        {
            float d = Vector3.Dot(normal, light);
            return Vector3.Dot(normal, back) < 0f ? -d : d;
        }

        // ------------------------------------------------------------ PNG (8 bit RGB)

        private static readonly uint[] CrcTable = MakeCrc();
        private static uint[] MakeCrc()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1; table[n] = c; }
            return table;
        }

        private static void Chunk(Stream output, string type, byte[] data, int length)
        {
            var head = new byte[8];
            head[0] = (byte)(length >> 24); head[1] = (byte)(length >> 16); head[2] = (byte)(length >> 8); head[3] = (byte)length;
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            output.Write(head, 0, 8);
            output.Write(data, 0, length);
            uint c = 0xffffffffu;
            for (int i = 4; i < 8; i++) c = CrcTable[(c ^ head[i]) & 255] ^ (c >> 8);
            for (int i = 0; i < length; i++) c = CrcTable[(c ^ data[i]) & 255] ^ (c >> 8);
            c ^= 0xffffffffu;
            output.Write(new[] { (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c }, 0, 4);
        }

        public static byte[] Png(byte[] rgb, int width, int height)
        {
            int stride = width * 3 + 1;
            var rows = new byte[stride * height];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(rgb, y * width * 3, rows, y * stride + 1, width * 3);
            byte[] packed;
            using (var zipped = new MemoryStream())
            {
                zipped.WriteByte(0x78); zipped.WriteByte(0x01);
                using (var deflate = new DeflateStream(zipped, System.IO.Compression.CompressionLevel.Fastest, true)) deflate.Write(rows, 0, rows.Length);
                uint a = 1, b = 0;                                   // Adler-32 of the rows
                for (int i = 0; i < rows.Length; i++) { a = (a + rows[i]) % 65521u; b = (b + a) % 65521u; }
                uint adler = (b << 16) | a;
                zipped.Write(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler }, 0, 4);
                packed = zipped.ToArray();
            }
            using (var png = new MemoryStream(packed.Length + 64))
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var header = new byte[13];
                header[0] = (byte)(width >> 24); header[1] = (byte)(width >> 16); header[2] = (byte)(width >> 8); header[3] = (byte)width;
                header[4] = (byte)(height >> 24); header[5] = (byte)(height >> 16); header[6] = (byte)(height >> 8); header[7] = (byte)height;
                header[8] = 8; header[9] = 2;
                Chunk(png, "IHDR", header, 13);
                Chunk(png, "IDAT", packed, packed.Length);
                Chunk(png, "IEND", new byte[0], 0);
                return png.ToArray();
            }
        }

        // ------------------------------------------------------------ 3. voting

        private static PartField Vote(Vector3[] vertices, int[] tri, View[] views, List<string> words, List<string> spoken, string[] answers, bool envelopes, out int masks, out string report)
        {
            int count = tri.Length / 3, wordCount = words.Count;
            var votes = new float[count * wordCount];
            var seen = new bool[count];
            masks = 0;
            var hit = new int[count];
            var perWord = new int[wordCount];
            for (int v = 0; v < views.Length; v++)
            {
                var view = views[v];
                for (int t = 0; t < count; t++) if (view.pixels[t] > 0) seen[t] = true;
                for (int w = 0; w < wordCount; w++)
                {
                    string answer = answers[v * wordCount + w];
                    if (string.IsNullOrEmpty(answer)) continue;
                    string text;
                    try { text = envelopes ? MetaSam.OutputText(answer) : answer; }
                    catch (Exception) { continue; }
                    foreach (var record in MetaSam.Parse(text))
                    {
                        if (record.confidence < MinConfidence) continue;
                        byte[] mask;
                        try { mask = MetaSam.Decode(record); }
                        catch (Exception) { continue; }
                        Array.Clear(hit, 0, count);
                        int area = 0;
                        for (int y = 0; y < record.height; y++)
                        {
                            int row = record.y1 + y;
                            if (row < 0 || row >= Size) continue;
                            for (int x = 0; x < record.width; x++)
                            {
                                if (mask[y * record.width + x] == 0) continue;
                                int column = record.x1 + x;
                                if (column < 0 || column >= Size) continue;
                                area++;
                                int id = view.id[row * Size + column];
                                if (id >= 0) hit[id]++;
                            }
                        }
                        if (area == 0 || area > view.objectPixels * MaxShare) continue;
                        masks++;
                        perWord[w]++;
                        // The more specific part wins where two overlap (a beak inside a head).
                        float weight = 1f / Mathf.Sqrt(Mathf.Max(1e-4f, area / (float)Mathf.Max(1, view.objectPixels)));
                        for (int t = 0; t < count; t++)
                            if (view.pixels[t] > 0 && hit[t] * 2 >= view.pixels[t]) votes[t * wordCount + w] += weight;
                    }
                }
            }
            var label = new int[count];              // 0 = the rest, 1 + word
            for (int t = 0; t < count; t++)
            {
                float best = 0f;
                for (int w = 0; w < wordCount; w++) if (votes[t * wordCount + w] > best) { best = votes[t * wordCount + w]; label[t] = w + 1; }
            }
            var near = Neighbours(vertices, tri);
            // Never seen (undersides, hollows): what the neighbours are.
            var unknown = new bool[count];
            for (int t = 0; t < count; t++) unknown[t] = !seen[t];
            var tally = new int[wordCount + 1];
            for (int pass = 0; pass < 60; pass++)
            {
                bool changed = false;
                for (int t = 0; t < count; t++)
                {
                    if (!unknown[t]) continue;
                    Array.Clear(tally, 0, tally.Length);
                    int known = 0;
                    foreach (int n in near[t]) if (!unknown[n]) { tally[label[n]]++; known++; }
                    if (known == 0) continue;
                    int top = 0;
                    for (int k = 1; k < tally.Length; k++) if (tally[k] > tally[top]) top = k;
                    label[t] = top;
                    unknown[t] = false;
                    changed = true;
                }
                if (!changed) break;
            }
            // Crumbs join what surrounds them; twice, since joining can leave a new crumb.
            int crumb = Mathf.Max(8, count / 600);
            for (int pass = 0; pass < 2; pass++)
            {
                var pieces = Pieces(label, near, out var pieceOf);
                foreach (var piece in pieces)
                {
                    if (piece.Count >= crumb) continue;
                    Array.Clear(tally, 0, tally.Length);
                    int around = 0;
                    foreach (int t in piece) foreach (int n in near[t]) if (pieceOf[n] != pieceOf[t]) { tally[label[n]]++; around++; }
                    if (around == 0) continue;
                    int top = 0;
                    for (int k = 1; k < tally.Length; k++) if (tally[k] > tally[top]) top = k;
                    foreach (int t in piece) label[t] = top;
                }
            }
            // Pieces of one word that lie apart are parts of their own: "wing", "wing 2" (the biggest first). Pieces
            // that touch or nearly touch are one part: a model is often several shells pushed into each other (Meshy's
            // smart topology; a beak and the patch of head round its root), and those share no edge.
            var names = new List<string> { PartField.Rest };
            var said = new List<string> { "" };
            var final = new byte[count];
            var areas = new float[count];
            for (int t = 0; t < count; t++) areas[t] = Vector3.Cross(vertices[tri[t * 3 + 1]] - vertices[tri[t * 3]], vertices[tri[t * 3 + 2]] - vertices[tri[t * 3]]).magnitude * .5f;
            Vector3 low = vertices[0], high = vertices[0];
            foreach (var p in vertices) { low = Vector3.Min(low, p); high = Vector3.Max(high, p); }
            float apart = Mathf.Max(high.x - low.x, Mathf.Max(high.y - low.y, high.z - low.z)) * SamePart;
            var all = Pieces(label, near, out _);
            var summary = new StringBuilder();
            for (int w = 0; w < wordCount; w++)
            {
                var mine = new List<(List<int> piece, float area, Bounds box)>();
                foreach (var piece in all)
                {
                    if (label[piece[0]] != w + 1) continue;
                    float a = 0f;
                    var box = new Bounds(vertices[tri[piece[0] * 3]], Vector3.zero);
                    foreach (int t in piece) { a += areas[t]; for (int k = 0; k < 3; k++) box.Encapsulate(vertices[tri[t * 3 + k]]); }
                    mine.Add((piece, a, box));
                }
                // Join the pieces whose boxes are nearer than `apart` (until none are).
                for (bool joined = true; joined; )
                {
                    joined = false;
                    for (int i = 0; i < mine.Count && !joined; i++)
                        for (int j = i + 1; j < mine.Count && !joined; j++)
                        {
                            Vector3 gap = Vector3.Max(Vector3.zero, Vector3.Max(mine[i].box.min - mine[j].box.max, mine[j].box.min - mine[i].box.max));
                            if (gap.magnitude > apart) continue;
                            var box = mine[i].box;
                            box.Encapsulate(mine[j].box);
                            mine[i].piece.AddRange(mine[j].piece);
                            mine[i] = (mine[i].piece, mine[i].area + mine[j].area, box);
                            mine.RemoveAt(j);
                            joined = true;
                        }
                }
                mine.Sort((x, y) => y.area.CompareTo(x.area));
                int first = names.Count;
                for (int k = 0; k < mine.Count; k++)
                {
                    int index = k < MaxPieces ? names.Count : first;
                    if (k < MaxPieces) { names.Add(k == 0 ? words[w] : words[w] + " " + (k + 1)); said.Add(k == 0 ? spoken[w] : ""); }
                    foreach (int t in mine[k].piece) final[t] = (byte)index;
                }
                summary.Append(words[w]).Append(' ').Append(perWord[w]).Append(" masks -> ").Append(Mathf.Min(mine.Count, MaxPieces)).Append(mine.Count == 1 ? " piece; " : " pieces; ");
            }
            report = summary.ToString().Trim();
            if (names.Count == 1) return null;
            Vector3 lo = vertices[0], hi = vertices[0];
            foreach (var p in vertices) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            var points = new Vector3[count];
            for (int t = 0; t < count; t++) points[t] = (vertices[tri[t * 3]] + vertices[tri[t * 3 + 1]] + vertices[tri[t * 3 + 2]]) / 3f - lo;
            return new PartField(names.ToArray(), said.ToArray(), hi - lo, points, final);
        }

        // Triangles sharing an edge (corners matched by place, since a welded mesh still splits them at creases).
        private static List<int>[] Neighbours(Vector3[] vertices, int[] tri)
        {
            int count = tri.Length / 3;
            Vector3 lo = vertices[0], hi = vertices[0];
            foreach (var p in vertices) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            float grain = Mathf.Max(1e-9f, (hi - lo).magnitude * 1e-5f);
            var place = new Dictionary<(int, int, int), int>();
            var at = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var k = (Mathf.RoundToInt((vertices[i].x - lo.x) / grain), Mathf.RoundToInt((vertices[i].y - lo.y) / grain), Mathf.RoundToInt((vertices[i].z - lo.z) / grain));
                if (!place.TryGetValue(k, out int id)) { id = place.Count; place[k] = id; }
                at[i] = id;
            }
            var edges = new Dictionary<long, List<int>>();
            for (int t = 0; t < count; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = at[tri[t * 3 + k]], b = at[tri[t * 3 + (k + 1) % 3]];
                    if (a == b) continue;
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>(2);
                    list.Add(t);
                }
            var near = new List<int>[count];
            for (int t = 0; t < count; t++) near[t] = new List<int>(3);
            foreach (var list in edges.Values)
                for (int i = 0; i < list.Count; i++)
                    for (int j = 0; j < list.Count; j++)
                        if (i != j) near[list[i]].Add(list[j]);
            return near;
        }

        // Connected groups of triangles with the same label.
        private static List<List<int>> Pieces(int[] label, List<int>[] near, out int[] pieceOf)
        {
            pieceOf = new int[label.Length];
            for (int i = 0; i < pieceOf.Length; i++) pieceOf[i] = -1;
            var pieces = new List<List<int>>();
            for (int s = 0; s < label.Length; s++)
            {
                if (pieceOf[s] >= 0) continue;
                var piece = new List<int> { s };
                pieceOf[s] = pieces.Count;
                for (int q = 0; q < piece.Count; q++)
                    foreach (int n in near[piece[q]])
                        if (pieceOf[n] < 0 && label[n] == label[s]) { pieceOf[n] = pieces.Count; piece.Add(n); }
                pieces.Add(piece);
            }
            return pieces;
        }
    }
}
