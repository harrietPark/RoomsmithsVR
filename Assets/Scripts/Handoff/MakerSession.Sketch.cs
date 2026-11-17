using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // The sketchbook page (2026-11-11, after the 6th try). F71: the pink eraser button is gone - the thumb of the hand
    // at the page is the eraser (MakerSession.SketchFingers.cs) - and its corner holds an undo button that takes back
    // the last change (a line, a rub or a "clear"; also "undo"; "clear" or "start again" wipes the page). When the
    // person is done, Maker offers to tidy the lines; on "yes" (or a new thumbs up) each wobbly line becomes what it
    // was meant to be - a closed round line a true circle or ellipse, a few straight runs a clean polygon or straight
    // line, anything else a smooth curve - morphing in place on the page. "No"/"keep" keeps the drawing.
    public sealed partial class MakerSession
    {
        public bool OfferingTidy => awaitingTidy || awaitingRefine; // F84: Refine / Done is asked on the open page too
        public int Tidied { get; private set; }
        public int StrokesUndone { get; private set; }
        public IReadOnlyList<List<Vector2>> DrawnStrokes => drawn;

        private Transform paperUndo;
        private float undoReadyAt;
        private bool awaitingTidy, tidyThumbReady;
        private bool? tidyAnswer;

        private static bool IsNo(string p) =>
            p == "no" || p == "nope" || p == "nah" || p.StartsWith("no ") || p.StartsWith("no,") || p.Contains("don't") || p.Contains("no thanks") || p == "아니" || p.Contains("아니요") || p.Contains("괜찮아");

        // F71: the last change on the page taken back (the undo button, "undo"): a line, a rub of the thumb eraser or a
        // "clear". With no history kept (lines set by a check) the last line goes.
        public void UndoStroke()
        {
            if (Time.time < undoReadyAt || !CanUndo) return;
            undoReadyAt = Time.time + .15f;
            penDown = false;
            StopRub();
            if (pageHistory.Count > 0)
            {
                drawn.Clear();
                drawn.AddRange(pageHistory[pageHistory.Count - 1]);
                pageHistory.RemoveAt(pageHistory.Count - 1);
            }
            else drawn.RemoveAt(drawn.Count - 1);
            RedrawInk();
            StrokesUndone++;
            if (paper != null) ToySounds.Play(ToySounds.Cue.Brush, paper.position, .4f, .7f);
        }

        private void RedrawInk()
        {
            pageInk.Rebuild(drawn, false); // F80: round ends, smooth joins, a slight taper (MakerSession.PageInk.cs)
            pageInk.Apply(paperInk);
        }

        // F80: only the last line changed (a line being finished or tidied): the others stay as they were built.
        private void RedrawLine(int index)
        {
            if (index != drawn.Count - 1 || !pageInk.HoldsAllBut(drawn.Count)) { RedrawInk(); return; }
            pageInk.UpdateLast(drawn[index], false);
            pageInk.Apply(paperInk);
        }

        // ------------------------------------------------------------ tidying the lines

        // F77 (2026-11-14, the user: "lines should tidy themselves while sketching, so we never pick Tidy them / Keep
        // mine later"): each line becomes what it was meant to be (a circle or ellipse, clean straight runs or a smooth
        // curve) the moment the fingertip lifts, morphing in place. Undo first gives back the line as it was drawn, then
        // takes it away. Maker no longer offers to tidy at the end (OfferTidy stays for old checks only).
        public static bool TidyOnLift = true;
        public static bool UndoGivesBackDrawn;           // F77's two-step undo (off since F80)
        public const float HookLength = .005f, HookAngle = 65f, SnapEndsWithin = .008f, LineSnapAngle = 7f;
        public const float TidyMorphSeconds = .18f;
        public static float QuickShapeHold = .55f;      // F81: rest the fingertip this long before lifting -> a clean shape
        public static bool AlwaysQuickShape;            // checks of the older tidy (every line snaps to a shape)
        public bool LastStrokeQuickShape { get; private set; }
        public static float FairCorner = 85f; // a real corner turns more than this within 4.5 mm (on the smoothed copy)
        public const float FairSpacing = .0015f;
        public static float FairSigma = .004f; // F81: how far the smoothing reaches (4 mm; less on short lines, 2% of the length)
        private float penMovedAt;

        // The fingertip (or the Editor mouse) lifted off the page: the line ends and is tidied.
        private void EndStroke()
        {
            if (!penDown) return;
            penDown = false;
            if (!TidyOnLift || drawn.Count == 0) return;
            int index = drawn.Count - 1;
            var line = drawn[index];
            // F80: the flick a fingertip makes as it lands or lifts is cut off, and the line gets its finished (tapered) ends.
            TrimHooks(line);
            RedrawLine(index);
            if (line.Count < 4 || Length(line) < .012f) return; // a dot or a tick stays as it is
            // F81 (Quest: "I drew a heart and it came out wobbly and angular - smooth strokes like Procreate"): what was
            // drawn is kept and only smoothed (Fair: the wobble goes, sharp corners such as a heart's tip stay). A circle,
            // polygon or straight line is made only when the fingertip rests still at the end of the line for
            // QuickShapeHold before it lifts (Procreate's QuickShape).
            bool quickShape = AlwaysQuickShape || Time.time - penMovedAt >= QuickShapeHold;
            LastStrokeQuickShape = quickShape;
            var tidy = quickShape ? Tidy(line) : Fair(line);
            if (tidy == null || tidy.Count < 2) return;
            SnapEnds(tidy, index); // F80: an end that nearly meets another line's end meets it
            // F80 (Quest: "the undo button doesn't work well"): one undo takes the whole line away. F77 first gave back the
            // line as drawn, which looked as if the press did nothing; the step kept when the line started is the page
            // before it.
            if (UndoGivesBackDrawn) KeepPageForUndo();
            StartCoroutine(MorphStroke(index, line, tidy));
        }

        private IEnumerator MorphStroke(int index, List<Vector2> line, List<Vector2> tidy)
        {
            int n = Mathf.Clamp(Mathf.Max(line.Count, tidy.Count), 2, 160);
            var from = Resample(line, n);
            var to = Resample(tidy, n);
            for (float t = 0f; t < 1f; t += Time.deltaTime / TidyMorphSeconds)
            {
                if (index >= drawn.Count || drawn[index] != line) yield break; // undone or cleared meanwhile
                float e = Mathf.SmoothStep(0f, 1f, t);
                line.Clear();
                for (int k = 0; k < n; k++) line.Add(Vector2.Lerp(from[k], to[k], e));
                RedrawLine(index);
                yield return null;
            }
            if (index >= drawn.Count || drawn[index] != line) yield break;
            line.Clear();
            line.AddRange(tidy);
            RedrawLine(index);
            Tidied++;
        }

        private IEnumerator OfferTidy()
        {
            if (drawn.Count == 0) yield break;
            awaitingTidy = true;
            tidyAnswer = null;
            tidyThumbReady = false;
            Body(Crew.Maker)?.Play("HeadTilt");
            AskWith(Crew.Maker, "Want me to tidy up your lines? Say yes, or keep them.", false, Rp("Tidy them", "yes"), Rp("Keep mine", "keep them"));
            float until = Time.time + 15f;
            while (tidyAnswer == null && Time.time < until)
            {
                bool thumbs = HandSet.Both().Any(h => h != null && h.isActiveAndEnabled && HandIO.Connected(h) && HandIO.ThumbsUp(h));
                if (!thumbs) tidyThumbReady = true;
                else if (tidyThumbReady) tidyAnswer = true;
                yield return null;
            }
            awaitingTidy = false;
            if (tidyAnswer == true) yield return TidyLines();
            else Talk.Add("The person kept their own lines.");
        }

        private bool TidyVoice(string p)
        {
            if (!awaitingTidy) return false;
            if (IsNo(p) || p.Contains("keep") || p.Contains("as it is") || p.Contains("그대로") || p.Contains("아니")) { tidyAnswer = false; return true; }
            if (IsYes(p) || p.Contains("tidy") || p.Contains("clean") || p.Contains("please") || p.Contains("perfect") || p.Contains("해줘") || p.Contains("응") || p.Contains("좋아")) { tidyAnswer = true; return true; }
            return false;
        }

        private IEnumerator TidyLines()
        {
            var before = drawn.Select(s => new List<Vector2>(s)).ToList();
            var after = before.Select(Tidy).ToList();
            // Morph each line from what was drawn to the tidy one (both resampled to the same count).
            var from = new List<List<Vector2>>();
            var to = new List<List<Vector2>>();
            for (int i = 0; i < before.Count; i++)
            {
                int n = Mathf.Clamp(Mathf.Max(before[i].Count, after[i].Count), 2, 160);
                from.Add(Resample(before[i], n));
                to.Add(Resample(after[i], n));
            }
            Body(Crew.Maker)?.Play("Idea_Pop");
            ToySounds.Play(ToySounds.Cue.Whoosh, paper.position, .4f, 1.4f);
            for (float t = 0f; t < 1f; t += Time.deltaTime / .7f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                drawn.Clear();
                for (int i = 0; i < from.Count; i++) drawn.Add(from[i].Select((p, k) => Vector2.Lerp(p, to[i][k], e)).ToList());
                RedrawInk();
                yield return null;
            }
            drawn.Clear();
            drawn.AddRange(after);
            RedrawInk();
            Tidied++;
            ToySounds.Play(ToySounds.Cue.Chime, paper.position, .35f, 1.8f);
            Talk.Add("Maker tidied the person's lines.");
        }

        // One line made what it was meant to be.
        public static List<Vector2> Tidy(List<Vector2> stroke)
        {
            var pts = Resample(stroke, Mathf.Clamp(Mathf.RoundToInt(Length(stroke) / .002f), 4, 200));
            if (pts.Count < 4) return pts;
            float length = Length(pts);
            Vector2 min = new Vector2(pts.Min(p => p.x), pts.Min(p => p.y)), max = new Vector2(pts.Max(p => p.x), pts.Max(p => p.y));
            float diag = Mathf.Max(.005f, (max - min).magnitude);
            bool closed = Vector2.Distance(pts[0], pts[pts.Count - 1]) < Mathf.Max(.012f, length * .15f);
            // Round: a circle or an ellipse along its main axes.
            if (closed)
            {
                Vector2 c = pts.Aggregate(Vector2.zero, (a, b) => a + b) / pts.Count;
                float sxx = 0f, syy = 0f, sxy = 0f;
                foreach (var p in pts) { var d = p - c; sxx += d.x * d.x; syy += d.y * d.y; sxy += d.x * d.y; }
                sxx /= pts.Count; syy /= pts.Count; sxy /= pts.Count;
                float tr = sxx + syy, det = sxx * syy - sxy * sxy, gap = Mathf.Sqrt(Mathf.Max(0f, tr * tr / 4f - det));
                float l1 = tr / 2f + gap, l2 = Mathf.Max(1e-8f, tr / 2f - gap);
                float angle = .5f * Mathf.Atan2(2f * sxy, sxx - syy);
                float a = Mathf.Sqrt(2f * l1), b = Mathf.Sqrt(2f * l2);
                Vector2 u = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), v = new Vector2(-u.y, u.x);
                // How far the drawn points are from that ellipse (relative to its size).
                float error = 0f;
                foreach (var p in pts)
                {
                    var d = p - c;
                    float r = Mathf.Sqrt(Mathf.Pow(Vector2.Dot(d, u) / a, 2f) + Mathf.Pow(Vector2.Dot(d, v) / b, 2f));
                    error += Mathf.Abs(r - 1f);
                }
                error /= pts.Count;
                var corners = Simplify(pts, diag * .07f);
                bool polygonal = corners.Count >= 4 && corners.Count <= 9 && MaxDeviation(pts, corners) < diag * .05f;
                // F77: a wobbly circle also passes as a 6-8 sided polygon; now that every line is tidied on lift, the shape
                // that really fits closer wins (the ellipse's mean miss vs the polygon's, both in metres).
                float roundMiss = error * (a + b) * .5f;
                if (polygonal && roundMiss < MeanDeviation(pts, corners) * 1.8f) polygonal = false; // F81: a polygon only when it fits clearly better (a wobbly circle came out angular)
                if (error < .12f && !polygonal)
                {
                    if (a / b < 1.18f) { float r = (a + b) * .5f; a = b = r; }
                    var round = new List<Vector2>();
                    for (int i = 0; i <= 72; i++)
                    {
                        float t = i / 72f * Mathf.PI * 2f;
                        round.Add(c + u * (Mathf.Cos(t) * a) + v * (Mathf.Sin(t) * b));
                    }
                    return round;
                }
                if (polygonal) { corners[corners.Count - 1] = corners[0]; return Densify(corners); }
            }
            // A few straight runs: a clean polyline.
            var simple = Simplify(pts, diag * .06f);
            if (simple.Count <= 6 && MaxDeviation(pts, simple) < diag * .06f)
            {
                if (simple.Count == 2) SnapStraight(simple); // F80: a nearly level or upright line is made exactly so
                return Densify(simple);
            }
            // Anything else: a smooth curve (F80: 4 -> 7 passes; the hand's few-millimetre wobble was still in it).
            var smooth = new List<Vector2>(pts);
            for (int pass = 0; pass < 7; pass++)
                for (int i = 1; i < smooth.Count - 1; i++) smooth[i] = (smooth[i - 1] + smooth[i] * 2f + smooth[i + 1]) / 4f;
            if (closed) smooth[smooth.Count - 1] = smooth[0];
            return smooth;
        }

        private static float Length(List<Vector2> pts)
        {
            float l = 0f;
            for (int i = 1; i < pts.Count; i++) l += Vector2.Distance(pts[i - 1], pts[i]);
            return l;
        }

        // `count` points evenly along the line.
        public static List<Vector2> Resample(List<Vector2> pts, int count)
        {
            var result = new List<Vector2>();
            if (pts == null || pts.Count == 0) return result;
            if (pts.Count == 1 || count < 2) { for (int i = 0; i < Mathf.Max(1, count); i++) result.Add(pts[0]); return result; }
            float total = Length(pts);
            if (total < 1e-6f) { for (int i = 0; i < count; i++) result.Add(pts[0]); return result; }
            float step = total / (count - 1), walked = 0f;
            int k = 1;
            result.Add(pts[0]);
            for (int i = 1; i < count - 1; i++)
            {
                float want = step * i;
                while (k < pts.Count - 1 && walked + Vector2.Distance(pts[k - 1], pts[k]) < want) { walked += Vector2.Distance(pts[k - 1], pts[k]); k++; }
                float seg = Vector2.Distance(pts[k - 1], pts[k]);
                result.Add(Vector2.Lerp(pts[k - 1], pts[k], seg > 1e-6f ? (want - walked) / seg : 0f));
            }
            result.Add(pts[pts.Count - 1]);
            return result;
        }

        // Ramer-Douglas-Peucker.
        private static List<Vector2> Simplify(List<Vector2> pts, float epsilon)
        {
            if (pts.Count < 3) return new List<Vector2>(pts);
            int index = 0;
            float far = 0f;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                float d = DistanceToSegment(pts[i], pts[0], pts[pts.Count - 1]);
                if (d > far) { far = d; index = i; }
            }
            if (far <= epsilon) return new List<Vector2> { pts[0], pts[pts.Count - 1] };
            var left = Simplify(pts.GetRange(0, index + 1), epsilon);
            var right = Simplify(pts.GetRange(index, pts.Count - index), epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        private static float MeanDeviation(List<Vector2> pts, List<Vector2> line)
        {
            if (pts.Count == 0 || line.Count < 2) return float.MaxValue;
            float sum = 0f;
            foreach (var p in pts)
            {
                float best = float.MaxValue;
                for (int i = 1; i < line.Count; i++) best = Mathf.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
                sum += best;
            }
            return sum / pts.Count;
        }

        private static float MaxDeviation(List<Vector2> pts, List<Vector2> line)
        {
            float worst = 0f;
            foreach (var p in pts)
            {
                float best = float.MaxValue;
                for (int i = 1; i < line.Count; i++) best = Mathf.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
                worst = Mathf.Max(worst, best);
            }
            return worst;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 1e-10f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        // Straight runs with points every 2 mm (so the morph moves along them evenly).
        private static List<Vector2> Densify(List<Vector2> corners)
        {
            var result = new List<Vector2>();
            for (int i = 1; i < corners.Count; i++)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(corners[i - 1], corners[i]) / .002f));
                for (int k = 0; k < n; k++) result.Add(Vector2.Lerp(corners[i - 1], corners[i], k / (float)n));
            }
            result.Add(corners[corners.Count - 1]);
            return result;
        }

        // ------------------------------------------------------------ F80: finishing a line

        // A sharp turn in the first or last HookLength of a line (the fingertip landing or lifting) is cut off.
        public static void TrimHooks(List<Vector2> line)
        {
            if (line == null || line.Count < 6 || Length(line) < .02f) return;
            TrimEnd(line);
            line.Reverse();
            TrimEnd(line);
            line.Reverse();
        }

        private static void TrimEnd(List<Vector2> line)
        {
            // The direction of the last HookLength vs the direction of the 2 x HookLength before it.
            int tail = line.Count - 1;
            float walked = 0f;
            while (tail > 0 && walked < HookLength) { walked += Vector2.Distance(line[tail], line[tail - 1]); tail--; }
            if (tail <= 0) return;
            int body = tail;
            walked = 0f;
            while (body > 0 && walked < HookLength * 2f) { walked += Vector2.Distance(line[body], line[body - 1]); body--; }
            Vector2 end = line[line.Count - 1] - line[tail], before = line[tail] - line[body];
            if (end.sqrMagnitude < 1e-8f || before.sqrMagnitude < 1e-8f) return;
            if (Vector2.Angle(end, before) < HookAngle) return;
            line.RemoveRange(tail + 1, line.Count - tail - 1);
        }

        // A straight line within LineSnapAngle of level or upright on the page is turned about its middle to be exactly so.
        private static void SnapStraight(List<Vector2> two)
        {
            Vector2 a = two[0], b = two[1], mid = (a + b) * .5f, d = b - a;
            if (d.sqrMagnitude < 1e-8f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            float snapped = Mathf.Round(angle / 90f) * 90f;
            if (Mathf.Abs(Mathf.DeltaAngle(angle, snapped)) > LineSnapAngle) return;
            float r = snapped * Mathf.Deg2Rad, half = d.magnitude * .5f;
            Vector2 u = new Vector2(Mathf.Cos(r), Mathf.Sin(r)) * half;
            two[0] = mid - u;
            two[1] = mid + u;
        }

        // An open line whose end comes within SnapEndsWithin of another line's end is pulled to meet it (the points
        // near that end follow, fading out over 1.5 cm), so a shape drawn in several strokes closes.
        public int EndsSnapped { get; private set; }
        private void SnapEnds(List<Vector2> line, int index)
        {
            if (line.Count < 2 || Vector2.Distance(line[0], line[line.Count - 1]) < .001f) return; // closed already
            for (int end = 0; end < 2; end++)
            {
                int at = end == 0 ? 0 : line.Count - 1;
                Vector2 best = default;
                float bestD = SnapEndsWithin;
                for (int i = 0; i < drawn.Count; i++)
                {
                    if (i == index || drawn[i].Count == 0) continue;
                    Vector2 first = drawn[i][0], last = drawn[i][drawn[i].Count - 1];
                    float d0 = Vector2.Distance(line[at], first), d1 = Vector2.Distance(line[at], last);
                    if (d0 < bestD) { bestD = d0; best = first; }
                    if (d1 < bestD) { bestD = d1; best = last; }
                }
                if (bestD >= SnapEndsWithin) continue;
                Vector2 shift = best - line[at];
                float along = 0f;
                for (int k = 0; k < line.Count; k++)
                {
                    int j = end == 0 ? k : line.Count - 1 - k;
                    if (k > 0) along += Vector2.Distance(line[j], line[end == 0 ? j - 1 : j + 1]);
                    float f = 1f - Mathf.Clamp01(along / .015f);
                    if (f <= 0f) break;
                    line[j] += shift * f;
                }
                EndsSnapped++;
            }
        }

        // ------------------------------------------------------------ F81: smoothing that keeps the drawing

        // The line evened out (points every FairSpacing), its sharp corners (a turn over FairCorner within 4 mm) kept, the
        // rest smoothed by a Gaussian between the corners (FairSigma), a closed line joined where it started.
        public static List<Vector2> Fair(List<Vector2> stroke)
        {
            float length = Length(stroke);
            if (stroke.Count < 4 || length < .012f) return new List<Vector2>(stroke);
            var pts = Resample(stroke, Mathf.Clamp(Mathf.RoundToInt(length / FairSpacing), 8, 400));
            bool closed = Vector2.Distance(pts[0], pts[pts.Count - 1]) < Mathf.Max(.01f, length * .08f) && length > .04f;
            if (closed) pts.RemoveAt(pts.Count - 1);
            int n = pts.Count, k = Mathf.Max(2, Mathf.RoundToInt(.0045f / FairSpacing));
            Vector2 At(int i) => closed ? pts[((i % n) + n) % n] : pts[Mathf.Clamp(i, 0, n - 1)];
            // Corners are found on a smoothed copy (the hand's jitter made false corners that pinned the wobble in place).
            var soft = new List<Vector2>(pts);
            var softNext = new Vector2[n];
            for (int pass = 0; pass < 12; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    bool end = !closed && (i == 0 || i == n - 1);
                    Vector2 l = closed ? soft[(i - 1 + n) % n] : soft[Mathf.Max(0, i - 1)], r = closed ? soft[(i + 1) % n] : soft[Mathf.Min(n - 1, i + 1)];
                    softNext[i] = end ? soft[i] : Vector2.Lerp(soft[i], (l + r) * .5f, .5f);
                }
                for (int i = 0; i < n; i++) soft[i] = softNext[i];
            }
            Vector2 Soft(int i) => closed ? soft[((i % n) + n) % n] : soft[Mathf.Clamp(i, 0, n - 1)];
            var turn = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i < k || i >= n - k)) continue;
                Vector2 a = Soft(i) - Soft(i - k), b = Soft(i + k) - Soft(i);
                turn[i] = a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f ? 0f : Vector2.Angle(a, b);
            }
            var fixedAt = new bool[n];
            if (!closed) { fixedAt[0] = fixedAt[n - 1] = true; }
            for (int i = 0; i < n; i++)
            {
                if (turn[i] < FairCorner) continue;
                bool peak = true;
                int apart = Mathf.RoundToInt(.01f / FairSpacing); // real corners are at least 1 cm apart
                for (int d = -apart; d <= apart && peak; d++)
                {
                    int j = closed ? ((i + d) % n + n) % n : Mathf.Clamp(i + d, 0, n - 1);
                    if (turn[j] > turn[i] || (turn[j] == turn[i] && j < i)) peak = d == 0;
                }
                if (peak) fixedAt[i] = true;
            }
            // Gaussian smoothing (FairSigma) between the corners: a corner and an open line's ends stay exactly; past a
            // segment's end the line is mirrored through that end (an odd reflection), so it neither shrinks nor bends there.
            var fixedIdx = new List<int>();
            for (int i = 0; i < n; i++) if (fixedAt[i]) fixedIdx.Add(i);
            float sigma = Mathf.Clamp(length * .02f, .0015f, FairSigma);
            int reach = Mathf.CeilToInt(sigma * 3f / FairSpacing);
            var weights = new float[reach + 1];
            for (int d = 0; d <= reach; d++) weights[d] = Mathf.Exp(-(d * FairSpacing) * (d * FairSpacing) / (2f * sigma * sigma));
            var smooth = new Vector2[n];
            if (fixedIdx.Count == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector2 acc = pts[i] * weights[0];
                    float w = weights[0];
                    for (int d = 1; d <= reach; d++) { acc += (At(i - d) + At(i + d)) * weights[d]; w += 2f * weights[d]; }
                    smooth[i] = acc / w;
                }
            }
            else
            {
                for (int f = 0; f < fixedIdx.Count; f++)
                {
                    int a = fixedIdx[f], bIdx = f + 1 < fixedIdx.Count ? fixedIdx[f + 1] : (closed ? fixedIdx[0] + n : a);
                    if (!closed && f + 1 >= fixedIdx.Count) { smooth[a] = pts[a]; break; }
                    int len = bIdx - a;
                    Vector2 pa = At(a), pb = At(bIdx);
                    Vector2 Seg(int j) // j in segment steps from a (0..len), mirrored past the ends
                    {
                        if (j < 0) return 2f * pa - Seg(-j);
                        if (j > len) return 2f * pb - Seg(2 * len - j);
                        return At(a + j);
                    }
                    smooth[((a % n) + n) % n] = pa;
                    for (int j = 1; j < len; j++)
                    {
                        Vector2 acc = Seg(j) * weights[0];
                        float w = weights[0];
                        for (int d = 1; d <= reach; d++)
                        {
                            int lo = Mathf.Max(j - d, -len), hi = Mathf.Min(j + d, 2 * len);
                            acc += (Seg(lo) + Seg(hi)) * weights[d];
                            w += 2f * weights[d];
                        }
                        smooth[((a + j) % n + n) % n] = acc / w;
                    }
                }
            }
            for (int i = 0; i < n; i++) pts[i] = smooth[i];
            if (closed) pts.Add(pts[0]);
            return pts;
        }

        // Tests
        public void SimulateTidyAnswer(bool yes) { if (awaitingTidy) tidyAnswer = yes; }
        // Tests (F77): one line drawn and the fingertip lifted (it is tidied as on the page).
        public void SimulateStrokeLifted(List<Vector2> stroke, float restedSeconds = 0f)
        {
            penDown = false;
            foreach (var p in stroke) Pen(p, null);
            penMovedAt = Time.time - restedSeconds; // F81: a rest at the end = QuickShape
            EndStroke();
        }
    }
}
