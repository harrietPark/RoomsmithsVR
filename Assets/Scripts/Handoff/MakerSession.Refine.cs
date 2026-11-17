using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Windups.Handoff
{
    // F84 (2026-11-14, the user: "after the sketch Maker used to ask if it should refine it - bring that back: Refine or
    // Done. Refine turns my wobbly star into a perfect star"): when the page's check is pressed, Maker asks
    // [Refine] [Done] (the page stays open). Refine makes every line what it was meant to be, exactly (Perfect): a star
    // drawn as an outline or as five crossing strokes becomes a regular star, a near-regular polygon a regular one, a
    // four-cornered shape with right angles a rectangle or a square, a round line a circle or ellipse, a nearly mirror-
    // symmetric shape (a heart, a cup's side) exactly symmetric, a straight line exactly level/upright/45 degrees, an arc
    // a true arc, anything else a smooth curve. The lines morph into place; then [Done] [Undo] (Undo, or the page's own
    // undo arrow, gives the drawing back). Done takes the page as before. Drawing on while asked closes the question.
    // The fitting runs in a Task (pure arithmetic), only the morph runs on the main thread.
    public sealed partial class MakerSession
    {
        public static bool RefineAsk = true;
        public const string RefineLabel = "Refine", RefineDoneLabel = "Done", RefineUndoLabel = "Undo";
        public const string RefineLine = "Want me to make it perfect?", RefinedLine = "There! Done, or undo?";
        public const float RefineMorphSeconds = .7f;

        public bool OfferingRefine => awaitingRefine;
        public int Refines { get; private set; }
        public int RefineUndos { get; private set; }
        public readonly List<string> RefinedKinds = new List<string>();
        private bool awaitingRefine, refineKeepDrawing;
        private string refineAnswer;
        private List<List<Vector2>> beforeRefine;

        // From HandSketchbook after the check (the page still open). Ends with Done (the page is taken), or with
        // refineKeepDrawing when the person drew on.
        private IEnumerator AskRefine()
        {
            awaitingRefine = true;
            refineKeepDrawing = false;
            refineAnswer = null;
            beforeRefine = null;
            int checks = PaperDonePresses;
            Body(Crew.Maker)?.Play("HeadTilt");
            AskWith(Crew.Maker, RefineLine, false, Rp(RefineLabel, "refine"), Rp(RefineDoneLabel, "done drawing"));
            WriteInputLog("refine asked (" + drawn.Count + " lines)", true);
            int ink = DrawnPoints;
            while (Drawing)
            {
                if (PaperDonePresses != checks) { checks = PaperDonePresses; refineAnswer = "done"; } // the check again = Done
                if (refineAnswer == "done") break;
                if (refineAnswer == "refine")
                {
                    refineAnswer = null;
                    yield return RefineLines();
                    if (!Drawing) break;
                    ink = DrawnPoints;
                    AskWith(Crew.Maker, RefinedLine, false, Rp(RefineDoneLabel, "done drawing"), Rp(RefineUndoLabel, "undo refine"));
                    continue;
                }
                if (refineAnswer == "undo")
                {
                    refineAnswer = null;
                    if (beforeRefine != null)
                    {
                        drawn.Clear();
                        drawn.AddRange(beforeRefine.Select(s => new List<Vector2>(s)));
                        beforeRefine = null;
                        RedrawInk();
                        RefineUndos++;
                        if (paper != null) ToySounds.Play(ToySounds.Cue.Whoosh, paper.position, .35f, .9f);
                        WriteInputLog("refine undone", true);
                    }
                    ink = DrawnPoints;
                    AskWith(Crew.Maker, RefineLine, false, Rp(RefineLabel, "refine"), Rp(RefineDoneLabel, "done drawing"));
                    continue;
                }
                // Drawing on (a new line, a rub or the page's undo): the question goes, the page is the person's again.
                if (penDown || DrawnPoints != ink) { refineKeepDrawing = true; break; }
                yield return null;
            }
            awaitingRefine = false;
            if (Question != null && !Question.answered && Question.who == Crew.Maker && (Question.text == RefineLine || Question.text == RefinedLine))
                CloseQuestion(refineKeepDrawing ? "drawing on" : "refine answered");
            WriteInputLog("refine: " + (refineKeepDrawing ? "drawing on" : "done") + " after " + Refines + " refine(s)", true);
        }

        // Words while asked (DrawingVoice, before the page's own "done").
        private bool RefineVoice(string p)
        {
            if (!awaitingRefine) return false;
            // (undo first: "undo refine" holds the word refine)
            if (p == "undo refine" || (beforeRefine != null && (p.Contains("undo") || p.Contains("back") || p.Contains("되돌") || p.Contains("원래"))))
            { refineAnswer = "undo"; return true; }
            if (p == "refine" || p.Contains("refine") || p.Contains("perfect") || p.Contains("clean it") || p.Contains("tidy") || p.Contains("반듯") || p.Contains("완벽") || p.Contains("다듬"))
            { refineAnswer = "refine"; return true; }
            if (p == "done drawing" || DoneWords(p) || IsYes(p) || p.Contains("that's all") || p.Contains("keep") || p.Contains("그대로") || p.Contains("다 했"))
            { refineAnswer = "done"; return true; }
            return false;
        }

        private IEnumerator RefineLines()
        {
            if (drawn.Count == 0) yield break;
            var before = drawn.Select(s => new List<Vector2>(s)).ToList();
            beforeRefine = before;
            KeepPageForUndo(); // the page's undo arrow takes the refine back too
            var kinds = new string[before.Count];
            List<List<Vector2>> after = null;
            var job = Task.Run(() =>
            {
                var result = new List<List<Vector2>>();
                for (int i = 0; i < before.Count; i++) { result.Add(Perfect(before[i], out string kind)); kinds[i] = kind; }
                return result;
            });
            Body(Crew.Maker)?.Play("Idea_Pop");
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted || job.Result == null) { Debug.LogWarning("[Windups Refine] " + job.Exception?.GetBaseException().Message); yield break; }
            after = job.Result;
            // Morph each line from what was drawn to the perfect one (both resampled to the same count).
            var from = new List<List<Vector2>>();
            var to = new List<List<Vector2>>();
            for (int i = 0; i < before.Count; i++)
            {
                int n = Mathf.Clamp(Mathf.Max(before[i].Count, after[i].Count), 2, 200);
                from.Add(Resample(before[i], n));
                to.Add(Resample(after[i], n));
            }
            if (paper != null) ToySounds.Play(ToySounds.Cue.Whoosh, paper.position, .4f, 1.4f);
            for (float t = 0f; t < 1f && Drawing; t += Time.deltaTime / RefineMorphSeconds)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                drawn.Clear();
                for (int i = 0; i < from.Count; i++)
                {
                    var line = new List<Vector2>(from[i].Count);
                    for (int k = 0; k < from[i].Count; k++) line.Add(Vector2.Lerp(from[i][k], to[i][k], e));
                    drawn.Add(line);
                }
                RedrawInk();
                yield return null;
            }
            drawn.Clear();
            drawn.AddRange(after);
            RedrawInk();
            Refines++;
            RefinedKinds.Clear();
            RefinedKinds.AddRange(kinds);
            if (paper != null) ToySounds.Play(ToySounds.Cue.Chime, paper.position, .35f, 1.8f);
            Talk.Add("Maker made the person's lines perfect: " + string.Join(", ", kinds));
            WriteInputLog("refined: " + string.Join(", ", kinds), true);
        }

        // ------------------------------------------------------------ one line made perfect

        public const float SnapDegrees = 10f;

        public static List<Vector2> Perfect(List<Vector2> stroke, out string kind)
        {
            kind = "dot";
            if (stroke == null || stroke.Count < 4) return stroke == null ? new List<Vector2>() : new List<Vector2>(stroke);
            float length = Length(stroke);
            if (length < .012f) return new List<Vector2>(stroke);
            var pts = Resample(stroke, Mathf.Clamp(Mathf.RoundToInt(length / .0015f), 24, 360));
            Vector2 min = new Vector2(pts.Min(p => p.x), pts.Min(p => p.y)), max = new Vector2(pts.Max(p => p.x), pts.Max(p => p.y));
            float diag = Mathf.Max(.005f, (max - min).magnitude);
            bool closed = Vector2.Distance(pts[0], pts[pts.Count - 1]) < Mathf.Max(.012f, length * .12f);
            if (closed)
            {
                var loop = new List<Vector2>(pts);
                loop.RemoveAt(loop.Count - 1);
                if (TryStarOutline(loop, out var star)) { kind = "star"; return star; }
                if (TryStarCrossing(loop, diag, out var pentagram)) { kind = "star"; return pentagram; }
                if (TryPolygon(loop, diag, false, out var polygon, out string polygonKind)) { kind = polygonKind; return polygon; }
                if (TryRound(loop, out var round, out string roundKind)) { kind = roundKind; return round; }
                if (TrySymmetric(loop, diag, out var mirrored)) { kind = "symmetric"; return mirrored; }
                if (TryPolygon(loop, diag, true, out var sides, out polygonKind)) { kind = polygonKind; return sides; }
                kind = "smooth";
                return Fair(stroke);
            }
            var simple = Simplify(pts, diag * .06f);
            if (simple.Count == 2 && MaxDeviation(pts, simple) < diag * .05f)
            {
                kind = "line";
                SnapSegment(simple, 0, 1);
                return Densify(simple);
            }
            if (TryArc(pts, out var arc)) { kind = "arc"; return arc; }
            if (simple.Count <= 6 && MaxDeviation(pts, simple) < diag * .06f && MeanDeviation(pts, simple) < diag * .012f)
            {
                kind = "polyline";
                for (int i = 1; i < simple.Count; i++) SnapSegment(simple, i - 1, i, true);
                return Densify(simple);
            }
            if (TrySymmetricOpen(pts, diag, out var side)) { kind = "symmetric"; return side; }
            kind = "smooth";
            return Fair(stroke);
        }

        // Level, upright or 45 degrees within SnapDegrees: the segment b turns about a (keepA) or about its middle.
        private static void SnapSegment(List<Vector2> line, int a, int b, bool keepA = false)
        {
            Vector2 d = line[b] - line[a];
            if (d.sqrMagnitude < 1e-8f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, snapped = Mathf.Round(angle / 45f) * 45f;
            if (Mathf.Abs(Mathf.DeltaAngle(angle, snapped)) > SnapDegrees) return;
            float r = snapped * Mathf.Deg2Rad;
            Vector2 u = new Vector2(Mathf.Cos(r), Mathf.Sin(r)) * d.magnitude;
            if (keepA)
            {
                Vector2 shift = line[a] + u - line[b];
                for (int k = b; k < line.Count; k++) line[k] += shift; // the rest of the line follows
                return;
            }
            Vector2 mid = (line[a] + line[b]) * .5f;
            line[a] = mid - u * .5f;
            line[b] = mid + u * .5f;
        }

        private static Vector2 Mean(List<Vector2> pts) { Vector2 s = Vector2.zero; foreach (var p in pts) s += p; return s / Mathf.Max(1, pts.Count); }
        private static float SignedArea(List<Vector2> loop)
        {
            float a = 0f;
            for (int i = 0; i < loop.Count; i++) { var p = loop[i]; var q = loop[(i + 1) % loop.Count]; a += p.x * q.y - q.x * p.y; }
            return a * .5f;
        }

        // A turn that puts something straight up (a star's tip, a polygon's flat bottom) when it is within SnapDegrees.
        private static float SnapTurn(float degrees, float period, float upright)
        {
            float off = Mathf.DeltaAngle(0f, ((degrees - upright) % period + period) % period);
            if (off > period * .5f) off -= period;
            return Mathf.Abs(off) <= SnapDegrees ? degrees - off : degrees;
        }

        // A star drawn as its outline: the distance from the middle rises to n tips and falls to n valleys in turn.
        private static bool TryStarOutline(List<Vector2> loop, out List<Vector2> star)
        {
            star = null;
            int n = loop.Count;
            Vector2 c = Mean(loop);
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = Vector2.Distance(loop[i], c);
            var soft = new float[n];
            for (int i = 0; i < n; i++) soft[i] = (r[(i - 2 + n) % n] + r[(i - 1 + n) % n] + r[i] + r[(i + 1) % n] + r[(i + 2) % n]) / 5f;
            float mean = soft.Average();
            int window = Mathf.Max(2, n / 24);
            var peaks = new List<int>();
            var valleys = new List<int>();
            for (int i = 0; i < n; i++)
            {
                bool top = soft[i] > mean, bottom = soft[i] < mean;
                for (int d = -window; d <= window && (top || bottom); d++)
                {
                    if (d == 0) continue;
                    float o = soft[(i + d + n) % n];
                    if (o > soft[i] || (o == soft[i] && d < 0)) top = false;
                    if (o < soft[i] || (o == soft[i] && d < 0)) bottom = false;
                }
                if (top) peaks.Add(i);
                if (bottom) valleys.Add(i);
            }
            int count = peaks.Count;
            if (count < 4 || count > 8 || Mathf.Abs(valleys.Count - count) > 0) return false;
            float outer = peaks.Average(i => r[i]), inner = valleys.Average(i => r[i]);
            if (inner < 1e-4f || outer / inner < 1.6f) return false;
            // Tips roughly evenly around (the gaps within 35% of 360/n) and of similar length.
            var angles = peaks.Select(i => Mathf.Atan2(loop[i].y - c.y, loop[i].x - c.x) * Mathf.Rad2Deg).OrderBy(a => a).ToList();
            float step = 360f / count;
            for (int k = 0; k < count; k++)
            {
                float gap = k + 1 < count ? angles[k + 1] - angles[k] : angles[0] + 360f - angles[k];
                if (Mathf.Abs(gap - step) > step * .35f) return false;
            }
            if (peaks.Any(i => Mathf.Abs(r[i] - outer) > outer * .3f)) return false;
            // The turn: the tips' mean angle (each taken back by its place k * step), a tip straight up if near.
            float sx = 0f, sy = 0f;
            for (int k = 0; k < count; k++)
            {
                float a = (angles[k] - k * step) * Mathf.Deg2Rad * count;
                sx += Mathf.Cos(a); sy += Mathf.Sin(a);
            }
            float turn = Mathf.Atan2(sy, sx) * Mathf.Rad2Deg / count;
            turn = SnapTurn(turn, step, 90f);
            float ratio = outer / inner;
            if (count == 5 && Mathf.Abs(ratio - 2.618f) < .65f) ratio = 2.618f; // the classic star's proportion
            inner = outer / ratio;
            bool ccw = SignedArea(loop) > 0f;
            var corners = new List<Vector2>();
            for (int k = 0; k < count * 2; k++)
            {
                int j = ccw ? k : -k;
                float a = (turn + j * step * .5f) * Mathf.Deg2Rad;
                float rad = k % 2 == 0 ? outer : inner;
                corners.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad);
            }
            corners.Add(corners[0]);
            star = Densify(corners);
            return true;
        }

        // A star drawn as five crossing strokes (a pentagram): five corners, each next one about 144 degrees on.
        private static bool TryStarCrossing(List<Vector2> loop, float diag, out List<Vector2> star)
        {
            star = null;
            var closedLoop = new List<Vector2>(loop) { loop[0] };
            var corners = Simplify(closedLoop, diag * .07f);
            if (corners.Count > 1 && Vector2.Distance(corners[0], corners[corners.Count - 1]) < diag * .1f) corners.RemoveAt(corners.Count - 1);
            if (corners.Count != 5) return false;
            Vector2 c = Mean(corners);
            float radius = corners.Average(p => Vector2.Distance(p, c));
            if (corners.Any(p => Mathf.Abs(Vector2.Distance(p, c) - radius) > radius * .3f)) return false;
            var angles = corners.Select(p => Mathf.Atan2(p.y - c.y, p.x - c.x) * Mathf.Rad2Deg).ToList();
            float sign = 0f;
            for (int k = 0; k < 5; k++)
            {
                float step = Mathf.DeltaAngle(angles[k], angles[(k + 1) % 5]);
                if (Mathf.Abs(Mathf.Abs(step) - 144f) > 30f) return false;
                if (sign == 0f) sign = Mathf.Sign(step); else if (Mathf.Sign(step) != sign) return false;
            }
            float sx = 0f, sy = 0f;
            for (int k = 0; k < 5; k++)
            {
                float a = (angles[k] - k * 144f * sign) * Mathf.Deg2Rad * 5f;
                sx += Mathf.Cos(a); sy += Mathf.Sin(a);
            }
            float turn = SnapTurn(Mathf.Atan2(sy, sx) * Mathf.Rad2Deg / 5f, 72f, 90f);
            // Start at the tip nearest where the drawing started (the morph moves less).
            var tips = new List<Vector2>();
            for (int k = 0; k < 5; k++)
            {
                float a = (turn + k * 144f * sign) * Mathf.Deg2Rad;
                tips.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
            int first = 0;
            for (int k = 1; k < 5; k++) if (Vector2.Distance(tips[k], loop[0]) < Vector2.Distance(tips[first], loop[0])) first = k;
            var ordered = new List<Vector2>();
            for (int k = 0; k <= 5; k++) ordered.Add(tips[(first + k) % 5]);
            star = Densify(ordered);
            return true;
        }

        // 3-8 straight sides: a rectangle (or square) with right angles, a regular polygon when its sides and angles
        // are nearly equal, else the same corners joined by straight sides. A round line that also passes as a polygon
        // stays round when the round fit is clearly better (as Tidy).
        private static bool TryPolygon(List<Vector2> loop, float diag, bool irregular, out List<Vector2> shape, out string kind)
        {
            shape = null;
            kind = "polygon";
            var closedLoop = new List<Vector2>(loop) { loop[0] };
            var corners = Simplify(closedLoop, diag * .07f);
            if (corners.Count > 1 && Vector2.Distance(corners[0], corners[corners.Count - 1]) < diag * .1f) corners.RemoveAt(corners.Count - 1);
            // Simplify's first point is wherever the line started (often mid-side): drop corners that barely turn.
            for (int pass = 0; pass < 3 && corners.Count > 3; pass++)
                for (int i = corners.Count - 1; i >= 0 && corners.Count > 3; i--)
                {
                    Vector2 a = corners[(i - 1 + corners.Count) % corners.Count], b = corners[i], d = corners[(i + 1) % corners.Count];
                    if (Vector2.Angle(b - a, d - b) < 22f) corners.RemoveAt(i);
                }
            int m = corners.Count;
            if (m < 3 || m > 8) return false;
            var ring = new List<Vector2>(corners) { corners[0] };
            if (MaxDeviation(closedLoop, ring) > diag * .07f) return false;
            float polyMiss = MeanDeviation(closedLoop, ring);
            // Straight sides really are straight: a curved shape (a heart) passes as a rough polygon at the corner tolerance.
            if (polyMiss > diag * (irregular ? .012f : .02f)) return false;
            if (TryRound(loop, out var round, out _) && MeanDeviation(closedLoop, round) < polyMiss * 1.4f) return false;
            Vector2 c = Mean(corners);
            var sides = new float[m];
            var turns = new float[m];
            for (int i = 0; i < m; i++)
            {
                sides[i] = Vector2.Distance(corners[i], corners[(i + 1) % m]);
                turns[i] = Vector2.Angle(corners[i] - corners[(i - 1 + m) % m], corners[(i + 1) % m] - corners[i]);
            }
            float meanSide = sides.Average();
            bool ccw = SignedArea(corners) > 0f;
            if (m == 4 && turns.All(t => Mathf.Abs(t - 90f) < 20f))
            {
                // A rectangle along its own axes (the mean direction of its sides, folded to 90 degrees).
                float sx = 0f, sy = 0f;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 d = corners[(i + 1) % 4] - corners[i];
                    float a = Mathf.Atan2(d.y, d.x) * 4f;
                    sx += Mathf.Cos(a) * d.magnitude; sy += Mathf.Sin(a) * d.magnitude;
                }
                float axis = Mathf.Atan2(sy, sx) * Mathf.Rad2Deg / 4f;
                axis = SnapTurn(axis, 90f, 0f);
                Vector2 u = new Vector2(Mathf.Cos(axis * Mathf.Deg2Rad), Mathf.Sin(axis * Mathf.Deg2Rad)), v = new Vector2(-u.y, u.x);
                float w = (Mathf.Abs(Vector2.Dot(corners[1] - corners[0], u)) + Mathf.Abs(Vector2.Dot(corners[3] - corners[2], u)) + Mathf.Abs(Vector2.Dot(corners[2] - corners[1], u)) + Mathf.Abs(Vector2.Dot(corners[0] - corners[3], u))) * .5f;
                float h = (Mathf.Abs(Vector2.Dot(corners[1] - corners[0], v)) + Mathf.Abs(Vector2.Dot(corners[3] - corners[2], v)) + Mathf.Abs(Vector2.Dot(corners[2] - corners[1], v)) + Mathf.Abs(Vector2.Dot(corners[0] - corners[3], v))) * .5f;
                kind = "rectangle";
                if (Mathf.Abs(w - h) < Mathf.Max(w, h) * .12f) { w = h = (w + h) * .5f; kind = "square"; }
                var box = new List<Vector2> { c + (-u * w - v * h) * .5f, c + (u * w - v * h) * .5f, c + (u * w + v * h) * .5f, c + (-u * w + v * h) * .5f };
                if (!ccw) box.Reverse();
                shape = StartNear(box, loop[0]);
                return true;
            }
            float want = 360f / m;
            bool regular = sides.All(s => Mathf.Abs(s - meanSide) < meanSide * .3f) && turns.All(t => Mathf.Abs(t - want) < 25f);
            if (regular)
            {
                float radius = corners.Average(p => Vector2.Distance(p, c));
                float sx = 0f, sy = 0f;
                var angles = corners.Select(p => Mathf.Atan2(p.y - c.y, p.x - c.x) * Mathf.Rad2Deg).ToList();
                for (int k = 0; k < m; k++)
                {
                    float a = (angles[k] - k * want * (ccw ? 1f : -1f)) * Mathf.Deg2Rad * m;
                    sx += Mathf.Cos(a); sy += Mathf.Sin(a);
                }
                float turn = Mathf.Atan2(sy, sx) * Mathf.Rad2Deg / m;
                // Odd: a corner straight up; even: a flat side at the bottom (a corner at 90 + half a step).
                turn = SnapTurn(turn, want, m % 2 == 1 ? 90f : 90f + want * .5f);
                var regularCorners = new List<Vector2>();
                for (int k = 0; k < m; k++)
                {
                    float a = (turn + k * want * (ccw ? 1f : -1f)) * Mathf.Deg2Rad;
                    regularCorners.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
                }
                kind = m == 3 ? "triangle" : m == 4 ? "square" : m == 5 ? "pentagon" : m == 6 ? "hexagon" : "polygon";
                shape = StartNear(regularCorners, loop[0]);
                return true;
            }
            if (!irregular) return false;
            // Irregular: clean straight sides, nearly level or upright sides made exactly so.
            var clean = new List<Vector2>(corners);
            for (int i = 0; i < m; i++)
            {
                Vector2 d = clean[(i + 1) % m] - clean[i];
                float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, snapped = Mathf.Round(angle / 90f) * 90f;
                if (Mathf.Abs(Mathf.DeltaAngle(angle, snapped)) > SnapDegrees * .7f) continue;
                Vector2 mid = (clean[i] + clean[(i + 1) % m]) * .5f;
                if (Mathf.Abs(snapped % 180f) < 1f) { clean[i] = new Vector2(clean[i].x, mid.y); clean[(i + 1) % m] = new Vector2(clean[(i + 1) % m].x, mid.y); }
                else { clean[i] = new Vector2(mid.x, clean[i].y); clean[(i + 1) % m] = new Vector2(mid.x, clean[(i + 1) % m].y); }
            }
            shape = StartNear(clean, loop[0]);
            return true;
        }

        // A closed ring of corners, starting at the corner nearest `start`, closed and densified.
        private static List<Vector2> StartNear(List<Vector2> corners, Vector2 start)
        {
            int first = 0;
            for (int k = 1; k < corners.Count; k++) if (Vector2.Distance(corners[k], start) < Vector2.Distance(corners[first], start)) first = k;
            var ordered = new List<Vector2>();
            for (int k = 0; k <= corners.Count; k++) ordered.Add(corners[(first + k) % corners.Count]);
            return Densify(ordered);
        }

        // A circle (axes within 15%) or an ellipse along its main axes (level/upright when near).
        private static bool TryRound(List<Vector2> loop, out List<Vector2> round, out string kind)
        {
            round = null;
            kind = "circle";
            Vector2 c = Mean(loop);
            float sxx = 0f, syy = 0f, sxy = 0f;
            foreach (var p in loop) { var d = p - c; sxx += d.x * d.x; syy += d.y * d.y; sxy += d.x * d.y; }
            sxx /= loop.Count; syy /= loop.Count; sxy /= loop.Count;
            float tr = sxx + syy, det = sxx * syy - sxy * sxy, gap = Mathf.Sqrt(Mathf.Max(0f, tr * tr / 4f - det));
            float l1 = tr / 2f + gap, l2 = Mathf.Max(1e-8f, tr / 2f - gap);
            float angle = .5f * Mathf.Atan2(2f * sxy, sxx - syy) * Mathf.Rad2Deg;
            float a = Mathf.Sqrt(2f * l1), b = Mathf.Sqrt(2f * l2);
            float error = 0f;
            Vector2 u0 = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)), v0 = new Vector2(-u0.y, u0.x);
            foreach (var p in loop)
            {
                var d = p - c;
                error += Mathf.Abs(Mathf.Sqrt(Mathf.Pow(Vector2.Dot(d, u0) / a, 2f) + Mathf.Pow(Vector2.Dot(d, v0) / b, 2f)) - 1f);
            }
            error /= loop.Count;
            if (error > .12f) return false;
            if (a / b < 1.15f) { a = b = (a + b) * .5f; }
            else { kind = "ellipse"; angle = SnapTurn(angle, 90f, 0f); }
            Vector2 u = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)), v = new Vector2(-u.y, u.x);
            // Start where the drawing started, going the way it went.
            float start = Mathf.Atan2(Vector2.Dot(loop[0] - c, v) / b, Vector2.Dot(loop[0] - c, u) / a);
            float dir = SignedArea(loop) > 0f ? 1f : -1f;
            round = new List<Vector2>();
            for (int i = 0; i <= 96; i++)
            {
                float t = start + dir * i / 96f * Mathf.PI * 2f;
                round.Add(c + u * (Mathf.Cos(t) * a) + v * (Mathf.Sin(t) * b));
            }
            return true;
        }

        // Nearly mirror-symmetric about an axis within 20 degrees of upright (a heart, a cup's side): made exactly so,
        // the axis upright.
        private static bool TrySymmetric(List<Vector2> loop, float diag, out List<Vector2> shape)
        {
            shape = null;
            var pts = Resample(new List<Vector2>(loop) { loop[0] }, 120);
            pts.RemoveAt(pts.Count - 1);
            if (!BestMirror(pts, out Vector2 c, out float tilt, out float miss) || miss > diag * .05f) return false;
            var even = Mirrored(pts, c, tilt);
            even.Add(even[0]);
            shape = Fair(even);
            return true;
        }

        // An open line (one side of a cup) mirrored only when its two ends are level with each other about an
        // upright axis - otherwise left as a smooth curve.
        private static bool TrySymmetricOpen(List<Vector2> pts, float diag, out List<Vector2> shape)
        {
            shape = null;
            var resampled = Resample(pts, 120);
            if (Mathf.Abs(resampled[0].y - resampled[resampled.Count - 1].y) > diag * .1f) return false;
            if (!BestMirror(resampled, out Vector2 c, out float tilt, out float miss) || miss > diag * .04f) return false;
            shape = Fair(Mirrored(resampled, c, tilt));
            return true;
        }

        private static Vector2 Reflect(Vector2 p, Vector2 c, Vector2 axis) => c + 2f * Vector2.Dot(p - c, axis) * axis - (p - c);

        private static bool BestMirror(List<Vector2> pts, out Vector2 c, out float tilt, out float miss)
        {
            Vector2 middle = Mean(pts);
            c = middle;
            tilt = 0f;
            miss = float.MaxValue;
            for (float deg = -20f; deg <= 20f + 1e-3f; deg += 2f)
            {
                Vector2 axis = new Vector2(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad));
                // The axis may sit left or right of the mean (a lopsided drawing): try a few offsets across it.
                for (float off = -.1f; off <= .1f + 1e-4f; off += .025f)
                {
                    Vector2 across = new Vector2(axis.y, -axis.x);
                    float width = pts.Max(p => Vector2.Dot(p - middle, across)) - pts.Min(p => Vector2.Dot(p - middle, across));
                    Vector2 centre = middle + across * (off * width);
                    float sum = 0f;
                    foreach (var p in pts)
                    {
                        Vector2 m = Reflect(p, centre, axis);
                        float best = float.MaxValue;
                        foreach (var q in pts) best = Mathf.Min(best, (q - m).sqrMagnitude);
                        sum += Mathf.Sqrt(best);
                    }
                    float mean = sum / pts.Count;
                    if (mean < miss) { miss = mean; tilt = deg; c = centre; }
                }
            }
            return miss < float.MaxValue;
        }

        // Each point halfway between itself and the mirror image of its partner across the axis, then turned upright.
        private static List<Vector2> Mirrored(List<Vector2> pts, Vector2 c, float tilt)
        {
            Vector2 axis = new Vector2(Mathf.Sin(tilt * Mathf.Deg2Rad), Mathf.Cos(tilt * Mathf.Deg2Rad));
            var result = new List<Vector2>(pts.Count);
            foreach (var p in pts)
            {
                Vector2 m = Reflect(p, c, axis);
                Vector2 partner = pts[0];
                float best = float.MaxValue;
                foreach (var q in pts) { float d = (q - m).sqrMagnitude; if (d < best) { best = d; partner = q; } }
                result.Add((p + Reflect(partner, c, axis)) * .5f);
            }
            float r = tilt * Mathf.Deg2Rad, cos = Mathf.Cos(r), sin = Mathf.Sin(r);
            for (int i = 0; i < result.Count; i++)
            {
                Vector2 d = result[i] - c;
                result[i] = c + new Vector2(d.x * cos - d.y * sin, d.x * sin + d.y * cos);
            }
            return result;
        }

        // A circular arc (Kasa fit) when every point is within 5% of the radius of it.
        private static bool TryArc(List<Vector2> pts, out List<Vector2> arc)
        {
            arc = null;
            int n = pts.Count;
            Vector2 m = Mean(pts);
            double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            foreach (var p in pts)
            {
                double u = p.x - m.x, v = p.y - m.y;
                suu += u * u; svv += v * v; suv += u * v;
                suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
            }
            double det = suu * svv - suv * suv;
            if (System.Math.Abs(det) < 1e-14) return false;
            double bu = .5 * (suuu + suvv), bv = .5 * (svvv + svuu);
            double uc = (bu * svv - bv * suv) / det, vc = (svv * 0 + suu * bv - suv * bu) / det;
            Vector2 c = m + new Vector2((float)uc, (float)vc);
            float radius = pts.Average(p => Vector2.Distance(p, c));
            if (radius > Length(pts) * 2.5f || radius < .004f) return false;
            if (pts.Any(p => Mathf.Abs(Vector2.Distance(p, c) - radius) > radius * .05f + .0008f)) return false;
            float a0 = Mathf.Atan2(pts[0].y - c.y, pts[0].x - c.x), total = 0f;
            for (int i = 1; i < n; i++)
                total += Mathf.DeltaAngle(Mathf.Atan2(pts[i - 1].y - c.y, pts[i - 1].x - c.x) * Mathf.Rad2Deg, Mathf.Atan2(pts[i].y - c.y, pts[i].x - c.x) * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            if (Mathf.Abs(total) < .35f) return false; // nearly straight: not an arc
            arc = new List<Vector2>();
            int steps = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(total) * radius / .002f), 8, 160);
            for (int i = 0; i <= steps; i++)
            {
                float t = a0 + total * i / steps;
                arc.Add(c + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * radius);
            }
            return true;
        }

        // ------------------------------------------------------------ tests
        public void SimulateRefineAnswer(string answer) { if (awaitingRefine) refineAnswer = answer; }
    }
}
