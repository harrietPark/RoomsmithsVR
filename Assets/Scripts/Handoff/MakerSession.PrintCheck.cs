using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // F42 (2026-11-12): Fixer checks every made thing for printing, the way it will lie on the printer (a clip
    // on its side), and shows what it found on the thing itself for a while:
    //  - red: faces that hang in the air (steeper than 45 degrees downward, not on the bed): they need supports;
    //  - orange: spots thinner than MinWallMm (a 0.4 mm nozzle's walls): they may break or not print;
    //  - the base: how much of it touches the bed (a tiny base needs a brim);
    //  - the size against a common printer's bed (PrintBedMm).
    // The app computes all of it (no AI). Fixer says the one or two things that matter, or that it prints fine;
    // "print check" (or "show supports") shows it again, and printing says it once more.
    public sealed partial class MakerSession
    {
        public static Vector3 PrintBedMm = new Vector3(220f, 250f, 220f); // width, height, depth of a common small printer
        public const float MinWallMm = 1.2f, OverhangLimit = 45f;

        public sealed class PrintReport
        {
            public Vector3 sizeMm;
            public float overhangShare, thinShare, baseMm2;
            public int overhangTris, thinSpots, samples;
            public int openEdges;      // F57: edges of the shown surface used by one triangle (holes); the print file is closed
            public bool tooBig, needsSupports, thin, smallBase, sideways;
            public string line;
        }

        public PrintReport LastPrintCheck { get; private set; }
        public Transform PrintMarks => printMarks;
        public bool PrintMarksShown => printMarks != null && printMarks.gameObject.activeSelf;
        public int PrintChecks { get; private set; }

        private Transform printMarks, printMarksOf;
        private float printMarksUntil;
        private Material redMark, orangeMark;

        private IEnumerator CheckPrintable(Transform thing, Mesh mesh, CoasterDesign d)
        {
            if (thing == null || mesh == null || d == null) yield break;
            var oriented = PrintOriented(mesh, d);
            var v = oriented.vertices;
            var t = oriented.triangles;
            var report = new PrintReport { sideways = d.kind == "clip" };
            var b = oriented.bounds;
            report.sizeMm = new Vector3(b.size.x, b.size.y, b.size.z) * 1000f;
            report.tooBig = report.sizeMm.x > PrintBedMm.x || report.sizeMm.z > PrintBedMm.z || report.sizeMm.y > PrintBedMm.y;
            float minY = b.min.y, onBed = .0006f, cosLimit = Mathf.Cos(OverhangLimit * Mathf.Deg2Rad);
            float total = 0f, hanging = 0f, baseArea = 0f;
            var overhang = new List<int>();
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], c1 = v[t[i + 1]], c2 = v[t[i + 2]];
                Vector3 cross = Vector3.Cross(c1 - a, c2 - a);
                float area = cross.magnitude * .5f;
                if (area < 1e-12f) continue;
                Vector3 n = cross / (area * 2f);
                total += area;
                float low = Mathf.Min(a.y, Mathf.Min(c1.y, c2.y)), high = Mathf.Max(a.y, Mathf.Max(c1.y, c2.y));
                if (n.y < -.95f && high < minY + .0003f) baseArea += area;
                else if (n.y < -cosLimit && low > minY + onBed) { hanging += area; overhang.Add(i); }
                if ((i / 3) % 4000 == 3999) yield return null;
            }
            report.overhangShare = total > 0f ? hanging / total : 0f;
            report.overhangTris = overhang.Count;
            report.baseMm2 = baseArea * 1e6f;
            report.needsSupports = report.overhangShare > .02f;
            report.smallBase = d.kind != "clip" && report.baseMm2 < Mathf.Min(150f, report.sizeMm.x * report.sizeMm.z * .04f);
            // Thin spots: from sample points, a short ray inward; the other side closer than MinWallMm is thin.
            var normals = oriented.normals;
            var grid = new TriGrid(v, t, b, MinWallMm * .001f * 1.5f);
            var thinVerts = new HashSet<int>();
            int step = Mathf.Max(1, v.Length / 260);
            for (int i = 0; i < v.Length; i += step)
            {
                report.samples++;
                Vector3 n = normals != null && i < normals.Length ? normals[i] : Vector3.up;
                if (n.sqrMagnitude < .5f) continue;
                float depth = grid.Inward(v[i], -n, MinWallMm * .001f);
                if (depth > 0f && depth < MinWallMm * .001f) { report.thinSpots++; thinVerts.Add(i); }
                if (report.samples % 60 == 0) yield return null;
            }
            report.thinShare = report.samples > 0 ? (float)report.thinSpots / report.samples : 0f;
            report.thin = report.thinSpots >= 4 && report.thinShare > .02f;
            // F57: a Meshy surface has gaps; counted off the main thread (the print file is rebuilt closed anyway)
            if (d.kind == "model")
            {
                var open = System.Threading.Tasks.Task.Run(() => SolidRebuild.OpenEdges(v, t, out _));
                while (!open.IsCompleted) yield return null;
                if (!open.IsFaulted) report.openEdges = open.Result;
            }
            if (oriented != mesh) Destroy(oriented);
            if (thing == null) yield break;
            report.line = PrintLine(report, d);
            LastPrintCheck = report;
            PrintChecks++;
            Talk.Add("Fixer's print check: " + report.line);
            ShowPrintMarks(thing, mesh, overhang, thinVerts, t);
            if (Current == Step.Done && thing == Product)
            {
                Body(Crew.Fixer)?.LookAtPoint(thing.position + Vector3.up * .03f, 2.5f);
                Say(Crew.Fixer, report.line);
            }
        }

        private static string PrintLine(PrintReport r, CoasterDesign d)
        {
            var parts = new List<string>();
            if (r.tooBig) parts.Add($"It's {r.sizeMm.x:0} by {r.sizeMm.z:0} by {r.sizeMm.y:0} mm: too big for most printers.");
            if (r.needsSupports) parts.Add("The red parts hang in the air: print with supports.");
            if (r.thin && parts.Count < 2) parts.Add("The orange spots are thinner than 1.2 mm. They may break.");
            if (r.smallBase && parts.Count < 2) parts.Add("It stands on a tiny base. A brim helps it stick.");
            if (parts.Count == 0) parts.Add(r.openEdges > 0 ? "Prints fine: no supports, and the print file seals its small gaps." : "Prints fine: no supports, walls thick enough.");
            else if (r.openEdges > 0 && parts.Count < 2) parts.Add("The print file seals its small gaps.");
            if (r.sideways) parts.Add("Print it lying on its side, so the bend is strong.");
            return string.Join(" ", parts);
        }

        // The marks on the made thing (in its own mesh, so they move with it), for a while.
        private void ShowPrintMarks(Transform thing, Mesh mesh, List<int> overhang, HashSet<int> thinVerts, int[] t)
        {
            HidePrintMarks();
            if (overhang.Count == 0 && thinVerts.Count == 0) return;
            var src = mesh.vertices;
            var red = new List<int>();
            foreach (int i in overhang) { red.Add(t[i]); red.Add(t[i + 1]); red.Add(t[i + 2]); }
            var orange = new List<int>();
            if (thinVerts.Count > 0)
                for (int i = 0; i + 2 < t.Length; i += 3)
                    if (thinVerts.Contains(t[i]) || thinVerts.Contains(t[i + 1]) || thinVerts.Contains(t[i + 2])) { orange.Add(t[i]); orange.Add(t[i + 1]); orange.Add(t[i + 2]); }
            var marks = new Mesh { name = "Print marks", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            // Pushed out a little along the normals so they sit on the surface, not in it.
            var normals = mesh.normals;
            var pushed = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) pushed[i] = src[i] + (normals != null && i < normals.Length ? normals[i] * .0004f : Vector3.zero);
            marks.vertices = pushed;
            marks.subMeshCount = 2;
            marks.SetTriangles(red, 0);
            marks.SetTriangles(orange, 1);
            marks.RecalculateNormals();
            owned.Add(marks);
            var go = new GameObject("Print Marks", typeof(MeshFilter), typeof(MeshRenderer));
            var filter = thing.GetComponentInChildren<MeshFilter>();
            go.transform.SetParent(filter != null ? filter.transform : thing, false);
            go.GetComponent<MeshFilter>().sharedMesh = marks;
            redMark ??= Tinted(new Color(1f, .25f, .2f, 1f), .5f);
            orangeMark ??= Tinted(new Color(1f, .62f, .15f, 1f), .5f);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { redMark, orangeMark };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            printMarks = go.transform;
            printMarksOf = thing;
            printMarksUntil = Time.time + 14f;
        }

        private Material Tinted(Color colour, float fill)
        {
            var asset = Resources.Load<Material>("MakerHologram");
            var m = asset != null ? new Material(asset) : new Material(ghostMaterial != null ? ghostMaterial : material);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Fill")) m.SetFloat("_Fill", fill);
            if (m.HasProperty("_TexAmount")) m.SetFloat("_TexAmount", 0f);
            if (m.HasProperty("_LineAlpha")) m.SetFloat("_LineAlpha", .1f);
            owned.Add(m);
            return m;
        }

        private void HidePrintMarks()
        {
            if (printMarks != null) Destroy(printMarks.gameObject);
            printMarks = null;
            printMarksOf = null;
        }

        private void PrintMarksFrame()
        {
            if (printMarks == null) return;
            if (printMarksOf == null || Time.time > printMarksUntil) HidePrintMarks();
        }

        // "Print check": the marks again on the made thing and Fixer's line; printing says it once more.
        private void SayPrintCheck(bool printing)
        {
            if (Product == null || Design == null) return;
            if (LastPrintCheck == null || printing && PrintChecks == 0) { StartCoroutine(CheckPrintable(Product, ProductMesh, Design)); return; }
            if (printing)
            {
                if (LastPrintCheck.tooBig || LastPrintCheck.needsSupports || LastPrintCheck.thin || LastPrintCheck.sideways) Say(Crew.Fixer, LastPrintCheck.line);
                return;
            }
            StartCoroutine(CheckPrintable(Product, ProductMesh, Design));
        }

        private bool PrintCheckVoice(string p)
        {
            if (Product == null || Current != Step.Done) return false;
            if (!(p.Contains("print check") || p.Contains("check it") || p.Contains("supports") || p.Contains("can i print") || p.Contains("printable") ||
                  p.Contains("출력 확인") || p.Contains("출력 체크") || p.Contains("서포트") || p.Contains("뽑을 수"))) return false;
            SayPrintCheck(false);
            return true;
        }

        // Triangles bucketed in a coarse grid, for short rays from the surface inward.
        private sealed class TriGrid
        {
            private readonly Vector3[] v;
            private readonly int[] t;
            private readonly Vector3 min;
            private readonly float cell;
            private readonly int nx, ny, nz;
            private readonly List<int>[] cells;
            private readonly int[] stamp;
            private int mark;

            public TriGrid(Vector3[] vertices, int[] triangles, Bounds bounds, float reach)
            {
                v = vertices;
                t = triangles;
                float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                cell = Mathf.Max(reach, largest / 28f);
                min = bounds.min - Vector3.one * cell * .5f;
                nx = Mathf.Max(1, Mathf.CeilToInt((bounds.size.x + cell) / cell));
                ny = Mathf.Max(1, Mathf.CeilToInt((bounds.size.y + cell) / cell));
                nz = Mathf.Max(1, Mathf.CeilToInt((bounds.size.z + cell) / cell));
                cells = new List<int>[nx * ny * nz];
                stamp = new int[t.Length / 3];
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                    Index(Vector3.Min(a, Vector3.Min(b, c)), out int x0, out int y0, out int z0);
                    Index(Vector3.Max(a, Vector3.Max(b, c)), out int x1, out int y1, out int z1);
                    for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                    {
                        int k = (z * ny + y) * nx + x;
                        (cells[k] ??= new List<int>()).Add(i);
                    }
                }
            }

            private void Index(Vector3 p, out int x, out int y, out int z)
            {
                x = Mathf.Clamp((int)((p.x - min.x) / cell), 0, nx - 1);
                y = Mathf.Clamp((int)((p.y - min.y) / cell), 0, ny - 1);
                z = Mathf.Clamp((int)((p.z - min.z) / cell), 0, nz - 1);
            }

            // How far the surface on the other side is along `dir` (inward), up to `reach` (else -1).
            public float Inward(Vector3 origin, Vector3 dir, float reach)
            {
                mark++;
                Vector3 o = origin + dir * 1e-5f;
                Index(Vector3.Min(o, o + dir * reach), out int x0, out int y0, out int z0);
                Index(Vector3.Max(o, o + dir * reach), out int x1, out int y1, out int z1);
                float best = -1f;
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                {
                    var list = cells[(z * ny + y) * nx + x];
                    if (list == null) continue;
                    foreach (int i in list)
                    {
                        if (stamp[i / 3] == mark) continue;
                        stamp[i / 3] = mark;
                        Vector3 a = v[t[i]], e1 = v[t[i + 1]] - a, e2 = v[t[i + 2]] - a;
                        Vector3 p = Vector3.Cross(dir, e2);
                        float det = Vector3.Dot(e1, p);
                        if (Mathf.Abs(det) < 1e-14f) continue;
                        Vector3 s = o - a;
                        float u = Vector3.Dot(s, p) / det;
                        if (u < 0f || u > 1f) continue;
                        Vector3 q = Vector3.Cross(s, e1);
                        float w = Vector3.Dot(dir, q) / det;
                        if (w < 0f || u + w > 1f) continue;
                        float dist = Vector3.Dot(e2, q) / det;
                        if (dist <= 2e-5f || dist > reach) continue;
                        if (best < 0f || dist < best) best = dist;
                    }
                }
                return best;
            }
        }

        public void SimulatePrintCheck() { if (Product != null) StartCoroutine(CheckPrintable(Product, ProductMesh, Design)); }
    }
}
