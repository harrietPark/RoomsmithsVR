using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // F80 (2026-11-14, the user after the Quest try: "polish the sketchbook strokes a bit more"). The person's lines on
    // the sketchbook page were square-ended quads laid one after another (InkLines): jagged where the line turned, a
    // square dot where it started, and the fingertip's few-millimetre shake straight in it. Now:
    //  - PenFilter: a One Euro filter on the fingertip (slow strokes are smoothed hard, fast ones follow closely),
    //  - PageInk: one continuous ribbon per line (joins follow the line's direction, one corner-cutting pass), round
    //    caps at both ends, a slight taper over the first and (once lifted) last TaperLength,
    //  - on lift (MakerSession.Sketch.cs): the landing/lifting flick is trimmed, nearly level/upright straight lines snap,
    //    and an end that nearly meets another line's end meets it.
    // Maker's own sketches still use InkLines. Only the line being drawn is rebuilt each frame (the others are kept).
    public sealed partial class MakerSession
    {
        public const float PageInkWidth = .0024f, TaperLength = .006f, TaperThin = .55f;
        public const float PenMinCutoff = 1.8f, PenBeta = 24f, PenDerivativeCutoff = 1f; // F81: a little more streamline

        private sealed class PenFilter
        {
            private Vector2 x, dx;
            private float t;
            private bool has;

            public void Reset() => has = false;

            public Vector2 Step(Vector2 v, float now)
            {
                if (!has) { has = true; x = v; dx = Vector2.zero; t = now; return v; }
                float dt = Mathf.Max(1f / 240f, now - t);
                t = now;
                dx = Vector2.Lerp(dx, (v - x) / dt, Alpha(dt, PenDerivativeCutoff));
                x = Vector2.Lerp(x, v, Alpha(dt, PenMinCutoff + PenBeta * dx.magnitude));
                return x;
            }

            private static float Alpha(float dt, float cutoff) => 1f / (1f + 1f / (2f * Mathf.PI * cutoff) / dt);
        }

        private sealed class PageInk
        {
            private readonly List<Vector3> vertices = new List<Vector3>(4096);
            private readonly List<int> triangles = new List<int>(12288);
            private readonly List<Color> colors = new List<Color>(4096);
            private readonly List<Vector3> normals = new List<Vector3>(4096);
            private readonly List<Vector2> path = new List<Vector2>(512);
            private readonly List<float> widths = new List<float>(512);
            private readonly List<Vector2> scratch = new List<Vector2>(512);
            private int keptVertices, keptTriangles, keptStrokes = -1;
            private static readonly Color Graphite = new Color(.21f, .22f, .29f);
            private const float Z = -.00035f;
            private const int CapSegments = 6;

            public void Clear()
            {
                vertices.Clear(); triangles.Clear(); colors.Clear(); normals.Clear();
                keptVertices = keptTriangles = 0;
                keptStrokes = 0;
            }

            // True when every line but the last (of `count`) is built and kept.
            public bool HoldsAllBut(int count) => keptStrokes == count - 1;

            // All lines built again; all but the last are kept, the last can then be rebuilt alone (UpdateLast).
            public void Rebuild(List<List<Vector2>> strokes, bool lastLive)
            {
                Clear();
                int n = strokes.Count;
                for (int i = 0; i < n - 1; i++) AddStroke(strokes[i], false);
                keptVertices = vertices.Count;
                keptTriangles = triangles.Count;
                keptStrokes = Mathf.Max(0, n - 1);
                if (n > 0) AddStroke(strokes[n - 1], lastLive);
            }

            public void UpdateLast(List<Vector2> stroke, bool live)
            {
                vertices.RemoveRange(keptVertices, vertices.Count - keptVertices);
                colors.RemoveRange(keptVertices, colors.Count - keptVertices);
                normals.RemoveRange(keptVertices, normals.Count - keptVertices);
                triangles.RemoveRange(keptTriangles, triangles.Count - keptTriangles);
                AddStroke(stroke, live);
            }

            public void Apply(Mesh mesh)
            {
                mesh.Clear();
                if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetNormals(normals);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
            }

            // One line: a ribbon along its (once corner-cut) path, round caps, thinner at its ends.
            private void AddStroke(List<Vector2> stroke, bool live)
            {
                if (stroke == null || stroke.Count == 0) return;
                path.Clear();
                path.Add(stroke[0]);
                for (int i = 1; i < stroke.Count; i++)
                    if ((stroke[i] - path[path.Count - 1]).sqrMagnitude > 1e-9f) path.Add(stroke[i]);
                if (path.Count == 1) { Disc(path[0], PageInkWidth * .5f); return; }
                Chaikin();
                // Widths: thin at the start, full after TaperLength, thin again at a finished end.
                float total = 0f;
                for (int i = 1; i < path.Count; i++) total += Vector2.Distance(path[i - 1], path[i]);
                float taper = Mathf.Min(TaperLength, total * .35f), walked = 0f;
                widths.Clear();
                for (int i = 0; i < path.Count; i++)
                {
                    if (i > 0) walked += Vector2.Distance(path[i - 1], path[i]);
                    float f = taper > 1e-5f ? Mathf.SmoothStep(TaperThin, 1f, walked / taper) : 1f;
                    if (!live && taper > 1e-5f) f = Mathf.Min(f, Mathf.SmoothStep(TaperThin, 1f, (total - walked) / taper));
                    widths.Add(PageInkWidth * f);
                }
                int first = vertices.Count;
                for (int i = 0; i < path.Count; i++)
                {
                    Vector2 a = path[Mathf.Max(0, i - 1)], b = path[Mathf.Min(path.Count - 1, i + 1)];
                    Vector2 d = (b - a).sqrMagnitude > 1e-12f ? (b - a).normalized : Vector2.right;
                    Vector2 n = new Vector2(-d.y, d.x) * widths[i] * .5f;
                    AddVertex(path[i] - n);
                    AddVertex(path[i] + n);
                    if (i == 0) continue;
                    int v = first + i * 2;
                    Both(v - 2, v - 1, v);
                    Both(v, v - 1, v + 1);
                }
                Cap(path[0], path[0] - path[1], widths[0] * .5f);
                Cap(path[path.Count - 1], path[path.Count - 1] - path[path.Count - 2], widths[widths.Count - 1] * .5f);
            }

            // F81: a Catmull-Rom curve through the points (two extra points per piece): smooth bends, and corners stay
            // where they were drawn (the corner-cutting pass rounded a heart's tip).
            private void Chaikin()
            {
                if (path.Count < 3) return;
                int n = path.Count;
                scratch.Clear();
                scratch.AddRange(path);
                path.Clear();
                for (int i = 0; i < n - 1; i++)
                {
                    Vector2 p0 = scratch[Mathf.Max(0, i - 1)], p1 = scratch[i], p2 = scratch[i + 1], p3 = scratch[Mathf.Min(n - 1, i + 2)];
                    path.Add(p1);
                    for (int s = 1; s <= 2; s++)
                    {
                        float t = s / 3f, t2 = t * t, t3 = t2 * t;
                        path.Add(.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3));
                    }
                }
                path.Add(scratch[n - 1]);
            }

            // A half disc beyond `at` in direction `outward`.
            private void Cap(Vector2 at, Vector2 outward, float r)
            {
                if (outward.sqrMagnitude < 1e-12f) outward = Vector2.left;
                outward.Normalize();
                Vector2 side = new Vector2(-outward.y, outward.x);
                int centre = vertices.Count;
                AddVertex(at);
                for (int k = 0; k <= CapSegments; k++)
                {
                    float a = Mathf.PI * k / CapSegments;
                    AddVertex(at + (side * Mathf.Cos(a) + outward * Mathf.Sin(a)) * r);
                    if (k > 0) Both(centre, centre + k, centre + k + 1);
                }
            }

            private void Disc(Vector2 at, float r)
            {
                int centre = vertices.Count;
                AddVertex(at);
                for (int k = 0; k <= CapSegments * 2; k++)
                {
                    float a = Mathf.PI * k / CapSegments;
                    AddVertex(at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
                    if (k > 0) Both(centre, centre + k, centre + k + 1);
                }
            }

            private void AddVertex(Vector2 p)
            {
                vertices.Add(new Vector3(p.x, p.y, Z));
                colors.Add(Graphite);
                normals.Add(Vector3.back);
            }

            // A triangle wound both ways (the page is seen from its front; a both-sided ribbon as InkLines had).
            private void Both(int a, int b, int c)
            {
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(a); triangles.Add(c); triangles.Add(b);
            }
        }
    }
}
