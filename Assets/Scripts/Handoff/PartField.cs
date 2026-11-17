using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Windups.Handoff
{
    // F83: one change to a part's shape ("make the beak longer"), kept with the design and applied to the
    // model's own vertices every time it is built (FitShapes.ModelArrays), so prints and rebuilt solids have it.
    [Serializable] public sealed class PartEdit
    {
        public string part;        // the part's name in the field ("beak", "wing 2")
        public string kind;        // length | size | width
        public float factor = 1f;  // 1.25 = a quarter more
        public PartEdit Copy() => (PartEdit)MemberwiseClone();
    }

    // F83: which part each bit of a model's surface belongs to (beak, wing, handle...), found by PartSegmenter
    // with Meta's SAM. It is a cloud of labelled points (triangle middles) with a grid for "the nearest one", so
    // it does not depend on the mesh's triangle order: the retextured copy of a model, its stretched sizes and
    // its rebuilt solid are all labelled by asking where their own triangles lie. Read-only after it is made,
    // so the lookups run in Task.Run.
    public sealed class PartField
    {
        public const string Rest = "body";             // label 0: whatever no named part claimed
        public readonly string[] names;                // by label
        public readonly string[] spoken;               // the same parts in the person's language ("" = none), by label
        public readonly Vector3 size;                  // the box the points lie in (its own units)
        public readonly Vector3[] points;              // from the box's low corner
        public readonly byte[] labels;
        private readonly int gx, gy, gz;
        private readonly float cell;
        private readonly int[] start, order;

        public int Count => labels.Length;

        public PartField(string[] names, string[] spoken, Vector3 size, Vector3[] points, byte[] labels)
        {
            this.names = names;
            this.spoken = spoken ?? new string[names.Length];
            this.size = new Vector3(Mathf.Max(1e-6f, size.x), Mathf.Max(1e-6f, size.y), Mathf.Max(1e-6f, size.z));
            this.points = points;
            this.labels = labels;
            float longest = Mathf.Max(this.size.x, Mathf.Max(this.size.y, this.size.z));
            cell = longest / 24f;
            gx = Mathf.Clamp(Mathf.CeilToInt(this.size.x / cell), 1, 24);
            gy = Mathf.Clamp(Mathf.CeilToInt(this.size.y / cell), 1, 24);
            gz = Mathf.Clamp(Mathf.CeilToInt(this.size.z / cell), 1, 24);
            // Points sorted by cell (a counting sort).
            start = new int[gx * gy * gz + 1];
            var cellOf = new int[points.Length];
            for (int i = 0; i < points.Length; i++) { cellOf[i] = CellOf(points[i]); start[cellOf[i] + 1]++; }
            for (int c = 0; c < start.Length - 1; c++) start[c + 1] += start[c];
            order = new int[points.Length];
            var fill = (int[])start.Clone();
            for (int i = 0; i < points.Length; i++) order[fill[cellOf[i]]++] = i;
        }

        private int Axis(float value, int count) => Mathf.Clamp((int)(value / cell), 0, count - 1);
        private int CellOf(Vector3 p) => (Axis(p.z, gz) * gy + Axis(p.y, gy)) * gx + Axis(p.x, gx);

        // The label nearest a place given 0-1 across the box (so any copy of the model can ask, whatever its size).
        public int At(Vector3 unit)
        {
            if (points.Length == 0) return 0;
            var p = Vector3.Scale(unit, size);
            int cx = Axis(p.x, gx), cy = Axis(p.y, gy), cz = Axis(p.z, gz);
            float best = float.MaxValue;
            int found = 0;
            int rings = Mathf.Max(gx, Mathf.Max(gy, gz));
            for (int r = 0; r <= rings; r++)
            {
                // Nothing in a farther ring can be nearer than (r - 1) cells.
                if (best < float.MaxValue && r >= 2 && best <= (r - 1) * cell * (r - 1) * cell) break;
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (z < 0 || z >= gz) continue;
                    for (int y = cy - r; y <= cy + r; y++)
                    {
                        if (y < 0 || y >= gy) continue;
                        bool shell = z == cz - r || z == cz + r || y == cy - r || y == cy + r;
                        for (int x = cx - r; x <= cx + r; x += shell ? 1 : Mathf.Max(1, 2 * r))
                        {
                            if (x < 0 || x >= gx) continue;
                            int c = (z * gy + y) * gx + x;
                            for (int k = start[c]; k < start[c + 1]; k++)
                            {
                                float d = (points[order[k]] - p).sqrMagnitude;
                                if (d < best) { best = d; found = labels[order[k]]; }
                            }
                        }
                    }
                }
            }
            return found;
        }

        // For PaintableThing.Prepare: a triangle's part by where its middle lies in the mesh's own bounds.
        public Func<Vector3, Vector3, Bounds, int> Classifier() => (normal, centre, bounds) =>
        {
            var s = bounds.size;
            return At(new Vector3((centre.x - bounds.min.x) / Mathf.Max(1e-9f, s.x), (centre.y - bounds.min.y) / Mathf.Max(1e-9f, s.y), (centre.z - bounds.min.z) / Mathf.Max(1e-9f, s.z)));
        };

        // The label of each triangle of a mesh that is this model (any size, any triangle order).
        public int[] LabelsFor(Vector3[] vertices, int[] triangles)
        {
            var result = new int[triangles.Length / 3];
            if (vertices.Length == 0) return result;
            Vector3 lo = vertices[0], hi = vertices[0];
            foreach (var p in vertices) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            Vector3 s = hi - lo;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector3 c = (vertices[triangles[i]] + vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3f - lo;
                result[i / 3] = At(new Vector3(c.x / Mathf.Max(1e-9f, s.x), c.y / Mathf.Max(1e-9f, s.y), c.z / Mathf.Max(1e-9f, s.z)));
            }
            return result;
        }

        // The same parts on another shape of the model (after part edits, at its made size): its own triangle middles.
        public static PartField Over(PartField names, Vector3[] vertices, int[] triangles, int[] triLabels)
        {
            Vector3 lo = vertices[0], hi = vertices[0];
            foreach (var p in vertices) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            int count = triangles.Length / 3;
            var points = new Vector3[count];
            var labels = new byte[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = (vertices[triangles[i * 3]] + vertices[triangles[i * 3 + 1]] + vertices[triangles[i * 3 + 2]]) / 3f - lo;
                labels[i] = (byte)Mathf.Clamp(triLabels[i], 0, 255);
            }
            return new PartField(names.names, names.spoken, hi - lo, points, labels);
        }

        // The same field with one part called something else ("this is the tail").
        public PartField Renamed(int label, string name, string said)
        {
            var n = (string[])names.Clone();
            var s = (string[])spoken.Clone();
            if (label >= 0 && label < n.Length) { n[label] = name; s[label] = said ?? ""; }
            return new PartField(n, s, size, points, labels);
        }

        // The labels whose name is `word`, also "wing 2" for "wing", a plural, or the spoken word.
        public List<int> LabelsNamed(string word)
        {
            var list = new List<int>();
            word = (word ?? "").Trim().ToLowerInvariant();
            if (word.Length == 0) return list;
            for (int i = 1; i < names.Length; i++)
            {
                string name = BaseName(names[i]);
                bool match = name == word || name + "s" == word || name + "es" == word || (name.EndsWith("f", StringComparison.Ordinal) && name.Substring(0, name.Length - 1) + "ves" == word)
                    || names[i] == word || (!string.IsNullOrEmpty(spoken[i]) && word.Contains(spoken[i]));
                if (match) list.Add(i);
            }
            return list;
        }

        // "wing 2" -> "wing".
        public static string BaseName(string name)
        {
            name = name ?? "";
            int space = name.LastIndexOf(' ');
            return space > 0 && int.TryParse(name.Substring(space + 1), out _) ? name.Substring(0, space) : name;
        }

        // ------------------------------------------------------------ on disk, beside the model's OBJ

        private const int Magic = 0x31545250; // "PRT1"

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(names.Length);
                for (int i = 0; i < names.Length; i++) { w.Write(names[i] ?? ""); w.Write(spoken[i] ?? ""); }
                w.Write(size.x); w.Write(size.y); w.Write(size.z);
                w.Write(points.Length);
                foreach (var p in points) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                w.Write(labels);
                w.Flush();
                return stream.ToArray();
            }
        }

        public static PartField FromBytes(byte[] bytes)
        {
            using (var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("not a parts file");
                int n = r.ReadInt32();
                var names = new string[n];
                var spoken = new string[n];
                for (int i = 0; i < n; i++) { names[i] = r.ReadString(); spoken[i] = r.ReadString(); }
                var size = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int count = r.ReadInt32();
                var points = new Vector3[count];
                for (int i = 0; i < count; i++) points[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                return new PartField(names, spoken, size, points, r.ReadBytes(count));
            }
        }

        // ------------------------------------------------------------ the fields known to this run (main thread)

        private static readonly Dictionary<string, PartField> known = new Dictionary<string, PartField>();
        private static readonly HashSet<string> missing = new HashSet<string>();
        public static int Changes { get; private set; }   // goes up whenever a field is set (caches made from fields)

        // Where a model's parts are kept: beside its OBJ. Scripted stand-ins have no file (memory only).
        public static string PathFor(string model) =>
            string.IsNullOrEmpty(model) || model.StartsWith("scripted:", StringComparison.Ordinal) ? null : Path.ChangeExtension(model, ".parts");

        public static string KeyFor(string model, string parts) => !string.IsNullOrEmpty(parts) ? parts : PathFor(model) ?? model ?? "";

        // The field under `key` (a .parts path, or a scripted model's name), read from disk the first time.
        public static PartField Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (known.TryGetValue(key, out var field)) return field;
            if (missing.Contains(key)) return null;
            try
            {
                if (File.Exists(key)) { field = FromBytes(File.ReadAllBytes(key)); known[key] = field; return field; }
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Parts] reading " + key + ": " + exception.Message); }
            missing.Add(key);
            return null;
        }

        public static void Set(string key, PartField field)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (field == null) { known.Remove(key); missing.Add(key); }
            else { known[key] = field; missing.Remove(key); }
            Changes++;
        }

        public static void ForgetAll() { known.Clear(); missing.Clear(); Changes++; }
    }

    // F83: a part made longer, bigger or thicker. Only that part's points move, and by nothing where the part
    // joins the rest (a soft ramp from the seam), so the surface stays in one piece.
    public static class PartShape
    {
        public static Vector3[] Apply(Vector3[] source, int[] triangles, int[] triLabels, string[] names, PartEdit[] edits)
        {
            if (edits == null || edits.Length == 0 || triLabels == null) return source;
            var v = (Vector3[])source.Clone();
            Vector3 lo = v[0], hi = v[0];
            foreach (var p in v) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            float grain = Mathf.Max(1e-9f, (hi - lo).magnitude * 1e-5f);
            foreach (var edit in edits)
            {
                if (edit == null || Mathf.Approximately(edit.factor, 1f)) continue;
                int label = Array.IndexOf(names, edit.part);
                if (label <= 0) continue;
                if (!Measure(v, triangles, triLabels, label, lo, hi, grain, out var inPart, out var seam, out var anchor, out var u, out float reach)) continue;
                float ramp = Mathf.Max(1e-6f, reach * .3f);
                float f = Mathf.Clamp(edit.factor, .3f, 3f) - 1f;
                for (int i = 0; i < v.Length; i++)
                {
                    if (!inPart[i]) continue;
                    float w = 1f;
                    if (seam.Count > 0)
                    {
                        float nearest = float.MaxValue;
                        foreach (var s in seam) nearest = Mathf.Min(nearest, (s - v[i]).sqrMagnitude);
                        float t = Mathf.Clamp01(Mathf.Sqrt(nearest) / ramp);
                        w = t * t * (3f - 2f * t);
                    }
                    Vector3 rel = v[i] - anchor;
                    float along = Vector3.Dot(rel, u);
                    switch (edit.kind)
                    {
                        case "length": v[i] += u * (along * f * w); break;
                        case "width": v[i] += (rel - u * along) * (f * w); break;
                        default: v[i] = anchor + rel * (1f + f * w); break;
                    }
                }
            }
            return v;
        }

        // Where a part joins the rest (`anchor`), the way it sticks out (`u`) and how far (`reach`), in the mesh's own
        // space: what a length edit stretches along and where the F83 knob sits (at anchor + u * reach). Plain
        // arithmetic (safe in Task.Run). False for a part with no triangles.
        public static bool Frame(Vector3[] v, int[] triangles, int[] triLabels, int label, out Vector3 anchor, out Vector3 u, out float reach)
        {
            anchor = u = Vector3.zero;
            reach = 0f;
            if (v == null || v.Length == 0 || triLabels == null) return false;
            Vector3 lo = v[0], hi = v[0];
            foreach (var p in v) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            return Measure(v, triangles, triLabels, label, lo, hi, Mathf.Max(1e-9f, (hi - lo).magnitude * 1e-5f), out _, out _, out anchor, out u, out reach);
        }

        private static bool Measure(Vector3[] v, int[] triangles, int[] triLabels, int label, Vector3 lo, Vector3 hi, float grain,
            out bool[] inPart, out List<Vector3> seam, out Vector3 anchor, out Vector3 u, out float reach)
        {
            inPart = new bool[v.Length];
            seam = new List<Vector3>();
            anchor = u = Vector3.zero;
            reach = 0f;
            var others = new HashSet<(int, int, int)>();
            for (int i = 0; i < triLabels.Length; i++)
                for (int k = 0; k < 3; k++)
                {
                    int index = triangles[i * 3 + k];
                    if (triLabels[i] == label) inPart[index] = true;
                    else others.Add(Key(v[index], lo, grain));
                }
            Vector3 middle = Vector3.zero;
            int count = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (!inPart[i]) continue;
                middle += v[i];
                count++;
                if (others.Contains(Key(v[i], lo, grain))) seam.Add(v[i]);
            }
            if (count == 0) return false;
            middle /= count;
            // A part that is a shell of its own, pushed into the rest (Meshy's smart topology often builds a
            // model that way; a beak stuck into a head) shares no point with it: its seam is then where it
            // comes nearest to the rest, at most a third of it.
            if (seam.Count == 0 && count < v.Length)
            {
                var mine = new List<(float d, int i)>();
                for (int i = 0; i < v.Length; i++)
                {
                    if (!inPart[i]) continue;
                    float nearest = float.MaxValue;
                    for (int k = 0; k < v.Length; k++) if (!inPart[k]) nearest = Mathf.Min(nearest, (v[k] - v[i]).sqrMagnitude);
                    mine.Add((Mathf.Sqrt(nearest), i));
                }
                mine.Sort((x, y) => x.d.CompareTo(y.d));
                float reachOfSeam = mine[0].d + (hi - lo).magnitude * .015f;
                int most = Mathf.Max(3, mine.Count / 3);
                for (int k = 0; k < mine.Count && k < most && mine[k].d <= reachOfSeam; k++) seam.Add(v[mine[k].i]);
            }
            anchor = middle;
            if (seam.Count > 0) { anchor = Vector3.zero; foreach (var p in seam) anchor += p; anchor /= seam.Count; }
            Vector3 axis = middle - anchor;
            // (a part with no seam, or one wrapped evenly round its seam: stretch it upright)
            u = axis.magnitude > (hi - lo).magnitude * 1e-3f ? axis.normalized : Vector3.up;
            for (int i = 0; i < v.Length; i++) if (inPart[i]) reach = Mathf.Max(reach, Mathf.Abs(Vector3.Dot(v[i] - anchor, u)));
            return true;
        }

        private static (int, int, int) Key(Vector3 p, Vector3 lo, float grain) =>
            (Mathf.RoundToInt((p.x - lo.x) / grain), Mathf.RoundToInt((p.y - lo.y) / grain), Mathf.RoundToInt((p.z - lo.z) / grain));
    }
}
