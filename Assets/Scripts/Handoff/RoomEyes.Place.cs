using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // Where a seen thing is in the room (2026-11-11, from the looks of the 7th Quest try: 10 of 20 things
    // were put at floor height because "the lowest depth point in the box" was taken for what the thing
    // stands on, and a monitor was measured as a 3.5 cm round thing).
    //  - What it stands on: the horizontal levels seen in and just under its box, highest first; a level
    //    counts when something stands on it there (points above it that reach down to it) or when the
    //    middle of the box lies on it (a flat thing). The floor seen behind a desk edge is neither.
    //  - Small standing things: the connected surface off that level (as before), a circle for round ones
    //    only when it fits the box. Flat things: the middle of the box on that level.
    //  - Big things (monitor, laptop, furniture) and things with nothing seen under them: where the
    //    middle of the box is, sized by the box at that distance.
    //  - Then the placement has to agree with the picture (Agrees): its middle inside its box, the depth
    //    along that line about as far, a plausible size, and for standing things its outline over its box.
    //    If not: placed = false, and nothing is shown on it.
    // 2026-11-12 (the look of the 8th try: the model's boxes were 5 to 10 cm off the things - a mug's and a
    // pill bottle's too low, a phone stand's a stand's width to the left after the zoomed-in check - so a
    // tag sat at a bottle's foot and another beside a stand; and the extra depth rays never ran):
    //  - The boxes only say roughly where. Around them (Window) the depth is searched for the thing itself:
    //    of everything that stands on a level there, the piece that lies best over the model's two boxes
    //    (Snap). Its own outline in the picture is Found.fit; its middle, width and top come from its own
    //    depth points, so a box shifted by half the thing no longer moves the tag.
    //  - Two listed things standing close together come out as one piece: it is cut between their boxes.
    //  - A piece much wider than the box is a heap (a phone stand with the books and cables leaning on
    //    it): of it, the connected part that has the thing's own colour in the picture is the thing (OwnColour).
    //  - Two names that end up on the same thing: the better match keeps the tag (Untangle).
    //  - Nothing found by depth (a flat thing, a glass the depth looks through): the box rules as before.
    // PlaceByDepth uses only the frame (its rays and depth points), so a saved look can be replayed.
    public sealed partial class RoomEyes
    {
        public const float BigArea = .08f, BigSpan = .32f;   // share of the picture, metres across
        public const float SnapScore = .12f;                 // least overlap (mean over the model's boxes) for a piece to be the thing
        private const float BoxMargin = .1f, WindowSide = .2f, WindowTop = .35f;
        private static float Under(float boxHeight) => Mathf.Max(.03f, boxHeight * .2f);

        public struct Level
        {
            public float y, noise;
            public int count;
        }

        // ------------------------------------------------------------ the boxes as hints

        private static float[] Ordered(float[] b) => new[]
        {
            Mathf.Clamp01(Mathf.Min(b[0], b[2])), Mathf.Clamp01(Mathf.Min(b[1], b[3])), Mathf.Clamp01(Mathf.Max(b[0], b[2])), Mathf.Clamp01(Mathf.Max(b[1], b[3]))
        };

        // What the model said about where the thing is: the box in use and, when it differs, its first answer.
        public static List<float[]> Hints(Found found)
        {
            var hints = new List<float[]> { Ordered(found.box) };
            var first = found.seen?.firstBox;
            if (first != null && first.Length == 4)
            {
                var f = Ordered(first);
                if (f[2] - f[0] > .008f && f[3] - f[1] > .008f && Overlap(f, hints[0]) < .98f) hints.Add(f);
            }
            return hints;
        }

        private static float[] MeanBox(Found found)
        {
            var hints = Hints(found);
            return new[] { hints.Average(h => h[0]), hints.Average(h => h[1]), hints.Average(h => h[2]), hints.Average(h => h[3]) };
        }

        // The part of the picture searched for the thing: its boxes and some room around them (more above:
        // the model's boxes tend to sit too low). x0, y0, x1, y1, origin top-left.
        public static float[] Window(Found found)
        {
            var hints = Hints(found);
            float x0 = hints.Min(h => h[0]), y0 = hints.Min(h => h[1]), x1 = hints.Max(h => h[2]), y1 = hints.Max(h => h[3]);
            float side = Mathf.Max(.02f, (x1 - x0) * WindowSide), up = Mathf.Max(.03f, (y1 - y0) * WindowTop), down = Mathf.Max(.02f, (y1 - y0) * WindowSide);
            return new[] { Mathf.Clamp01(x0 - side), Mathf.Clamp01(y0 - up), Mathf.Clamp01(x1 + side), Mathf.Clamp01(y1 + down) };
        }

        private static bool Inside(float[] box, float x, float y, float margin = 0f)
        {
            float mx = (box[2] - box[0]) * margin, my = (box[3] - box[1]) * margin;
            return x >= box[0] - mx && x <= box[2] + mx && y >= box[1] - my && y <= box[3] + my;
        }

        // ------------------------------------------------------------ one thing of a look

        // The thing's box in this picture (the model's box, tightened by its colour) and its number.
        // (Plain pixels only: runs on a worker thread.)
        private static Found Begin(Frame frame, Seen seen)
        {
            var box = Ordered(Refine(frame, seen) ?? seen.box);
            return new Found { seen = seen, box = box, crop = Crop(frame, box[0], box[1], box[2], box[3]), owner = ++frame.boxes };
        }

        // From the depth, and the picture of the thing itself once it is known where it is. (Runs on a
        // worker thread when the frame's rays are plain arithmetic.)
        private static void PlaceWithDepth(Frame frame, Found found, IList<Found> all)
        {
            found.byDepth = true;
            PlaceByDepth(frame, found, all);
            if (found.fit != null) found.crop = Crop(frame, found.fit[0], found.fit[1], found.fit[2], found.fit[3]) ?? found.crop;
        }

        private void Place(Frame frame, Found found, IList<Found> all)
        {
            if (frame.depth != null && frame.depth.Count >= 40) PlaceWithDepth(frame, found, all);
            else PlaceByDeskPlane(frame, found);
            LastLocateNote = found.note;
            LastBody.Clear();
            LastBody.AddRange(found.body);
        }

        public Found Locate(Frame frame, Seen seen)
        {
            var found = Begin(frame, seen);
            Place(frame, found, null);
            return found;
        }

        // Is the head still about where it was when the picture was taken (so the depth it sees now is the
        // depth of that picture)? Measured against the head then, not against the camera: the passthrough
        // camera sits beside the eyes and looks another way, which alone used up the old limit.
        private static bool HeadStill(Frame frame)
        {
            var head = Camera.main;
            return head != null && Vector3.Angle(head.transform.forward, frame.headForward) < 25f && Vector3.Distance(head.transform.position, frame.headPosition) < .2f;
        }

        // The extra rays around one thing: over its window and the band under it, `across` x `down` of them.
        // Half the grid's spacing, and finer for a small thing (some 14 across its own box, so a mug a
        // metre away is still told from what stands 3 cm behind it).
        public static void BoxRays(Found found, out float[] window, out float bottom, out int across, out int down)
        {
            window = Window(found);
            bottom = Mathf.Clamp01(window[3] + Under(window[3] - window[1]));
            var box = Ordered(found.box);
            float stepX = Mathf.Min(.5f / DepthCols, Mathf.Max(1e-4f, box[2] - box[0]) / 14f), stepY = Mathf.Min(.5f / DepthRows, Mathf.Max(1e-4f, box[3] - box[1]) / 16f);
            across = Mathf.Clamp(Mathf.CeilToInt((window[2] - window[0]) / stepX), 10, 40);
            down = Mathf.Clamp(Mathf.CeilToInt((bottom - window[1]) / stepY), 10, 44);
        }

        // More depth around one thing's boxes (and just under them), DepthBudgetMs of rays a frame.
        private IEnumerator AddBoxDepth(Frame frame, Found found)
        {
            frame.depth ??= new List<DepthPoint>();
            BoxRays(found, out var window, out float ay1, out int across, out int down);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var hands = HandPoints();
            for (int j = 0; j < down; j++)
                for (int i = 0; i < across; i++)
                {
                    var v = new Vector2(Mathf.Lerp(window[0], window[2], (i + .5f) / across), 1f - Mathf.Lerp(window[1], ay1, (j + .5f) / down));
                    if (DepthRay(frame.ray(v), out var p, out var n, out _) && !NearHands(p, hands)) frame.depth.Add(new DepthPoint { viewport = v, point = p, normal = n, owner = found.owner });
                    if ((i & 7) == 7 && clock.Elapsed.TotalMilliseconds > DepthBudgetMs)
                    {
                        yield return null;
                        if (!HeadStill(frame)) yield break;
                        hands = HandPoints();
                        clock.Restart();
                    }
                }
        }

        // Two names on one thing (a "cup" and a "mug" on the same mug, or a thing the depth cannot see
        // that took its neighbour): the better match keeps the place, the other gets no tag.
        public static void Untangle(List<Found> found)
        {
            for (int i = 0; i < found.Count; i++)
                for (int j = i + 1; j < found.Count; j++)
                {
                    Found a = found[i], b = found[j];
                    if (!a.placed || !b.placed || a.big || b.big || (a.fit == null && b.fit == null)) continue;
                    float apart = Vector3.ProjectOnPlane(a.basePoint - b.basePoint, Vector3.up).magnitude;
                    if (apart > Mathf.Max(.03f, Mathf.Min(a.width, b.width) * .5f) || Mathf.Abs(a.basePoint.y - b.basePoint.y) > .06f) continue;
                    var loser = a.match >= b.match ? b : a;
                    loser.placed = loser.onSurface = false;
                    loser.note = "NOT PLACED: it landed on the " + (loser == a ? b : a).seen.name + " | " + loser.note;
                }
        }

        // ------------------------------------------------------------ from depth

        // `all`: every thing listed in this picture (this one too): neighbours' boxes show where one thing
        // ends and the next begins.
        public static void PlaceByDepth(Frame frame, Found found, IList<Found> all = null)
        {
            found.box = Ordered(found.box);
            float x0 = found.box[0], y0 = found.box[1], x1 = found.box[2], y1 = found.box[3];
            float bw = Mathf.Max(1e-4f, x1 - x0), bh = Mathf.Max(1e-4f, y1 - y0), cx = (x0 + x1) * .5f, cy = (y0 + y1) * .5f;
            // Viewport y grows upward; the box's y grows downward.
            Vector2 V(float x, float y) => new Vector2(x, 1f - y);
            Ray through = frame.ray(V(cx, cy));
            Vector3 eye = through.origin;
            float Distance(DepthPoint d) => Vector3.Distance(d.point, eye);
            found.body = new List<DepthPoint>();
            found.placed = found.onSurface = found.big = false;
            found.fit = null;
            found.match = 0f;
            found.basePoint = found.top = through.GetPoint(.6f);   // only a guess until it is placed
            float mx = bw * BoxMargin, my = bh * BoxMargin, band = Under(bh);
            var mine = new List<DepthPoint>();
            var points = new List<DepthPoint>();
            var under = new List<DepthPoint>();
            foreach (var d in frame.depth)
            {
                if (d.owner != 0 && d.owner != found.owner) continue;
                mine.Add(d);
                float bx = d.viewport.x, by = 1f - d.viewport.y;
                if (bx < x0 - mx || bx > x1 + mx || by < y0 - my) continue;
                if (by <= y1 + my) points.Add(d);
                else if (by <= y1 + my + band) under.Add(d);
            }
            var hints = Hints(found);
            string note = "";
            bool outline = false;

            // What the middle of the box shows: how far away the thing is.
            List<DepthPoint> core = null;
            float coreDistance = 0f;
            Vector3 corePoint = Vector3.zero;
            if (points.Count >= 4)
            {
                core = points.Where(d => Mathf.Abs(d.viewport.x - cx) <= bw * .25f && Mathf.Abs(1f - d.viewport.y - cy) <= bh * .25f).ToList();
                if (core.Count < 3) core = points.OrderBy(d => (d.viewport - V(cx, cy)).sqrMagnitude).Take(5).ToList();
                var coreDistances = core.Select(Distance).OrderBy(x => x).ToList();
                coreDistance = coreDistances[coreDistances.Count / 2];
                float there = coreDistance;
                corePoint = Median(core.Where(d => Mathf.Abs(Distance(d) - there) < .08f).Select(d => d.point).ToList());
                // Big by every box the model gave (one loose box does not make a phone stand a monitor).
                found.big = hints.All(h => (h[2] - h[0]) * (h[3] - h[1]) > BigArea ||
                                           Vector3.Distance(frame.ray(V(h[0], cy)).GetPoint(there), frame.ray(V(h[2], cy)).GetPoint(there)) > BigSpan);
            }

            // The thing itself, where the boxes roughly point.
            bool snapped = !found.big && Snap(frame, found, mine, hints, all, eye, ref note);

            if (!snapped && points.Count < 4)
            {
                found.note = "NOT PLACED: no depth on it (" + points.Count + " points) | " + note;
                return;
            }
            if (!snapped)
            {
                var levels = Levels(points.Concat(under).ToList(), eye.y);
                note += "points " + points.Count + "+" + under.Count + ", core " + core.Count + " at " + coreDistance.ToString("F2") + " m, levels " +
                       (levels.Count == 0 ? "none" : string.Join("/", levels.Select(l => l.y.ToString("F3") + "x" + l.count)));
                if (found.big)
                {
                    // A monitor, a laptop, a chair: where the middle of its box is, on the highest level below that.
                    // (A level with a fair share of the points: the top edge of a laptop in front of a monitor
                    // gives a handful of up-facing points too, and is not what the monitor stands on.)
                    float? below = null;
                    int fair = Mathf.Max(4, Mathf.RoundToInt((points.Count + under.Count) * .025f));
                    foreach (var level in levels) if (level.count >= fair && level.y < corePoint.y - .01f) { below = level.y; break; }
                    OnFacingPlane(frame, found, corePoint, eye, below);
                    note += ", big" + (found.onSurface ? " on " + found.basePoint.y.ToString("F3") : " in the air");
                }
                else
                {
                    var pool = points.Where(d => Mathf.Abs(Distance(d) - coreDistance) < .25f).ToList();
                    var byDistance = pool.OrderBy(Distance).ToList();
                    var nearest = byDistance[byDistance.Count / 10];
                    float near = Distance(nearest);
                    // The box's top edge at the nearest surface's distance is as high as the thing can reach (a
                    // monitor or a wall behind it shows inside the box too, higher up and further away).
                    Vector3 toNear = Vector3.ProjectOnPlane(nearest.point - eye, Vector3.up);
                    var nearPlane = new Plane(toNear.sqrMagnitude > 1e-4f ? -toNear.normalized : Vector3.back, nearest.point);
                    float reach = OnPlane(nearPlane, frame.ray(V(cx, y0)), nearest.point + Vector3.up * .3f).y + .03f;

                    Level? support = null;
                    List<DepthPoint> body = null;
                    int enough = Mathf.Max(4, Mathf.RoundToInt((points.Count + under.Count) * .03f));
                    foreach (var level in levels)
                    {
                        if (level.count < enough || level.y > corePoint.y + .02f) continue;
                        float lift = Mathf.Max(.012f, level.noise * 3f);
                        var standing = pool.Where(d => d.point.y > level.y + lift && d.point.y < reach).ToList();
                        var candidate = Body(standing, level.noise, V(cx, cy), Mathf.Max(bw, bh), near, Distance);
                        // It stands there when it reaches down to the level, rises off it, and fills a good part of
                        // the box's width (a neighbour that only pokes into the box's edge is not the thing).
                        bool upright = candidate != null && candidate.Count >= 5 && candidate.Min(d => d.point.y) <= level.y + lift + .035f && candidate.Max(d => d.point.y) >= level.y + .025f &&
                                       Across(candidate, x0, x1) >= .35f;
                        bool lying = core.Count(d => Mathf.Abs(d.point.y - level.y) < .025f) >= core.Count * .6f;
                        if (!upright && !lying) continue;
                        support = level;
                        body = upright ? candidate : null;
                        break;
                    }

                    if (!support.HasValue)
                    {
                        // Nothing seen that it stands on (on a wall, in a hand, its foot hidden): where the box's middle is.
                        OnFacingPlane(frame, found, corePoint, eye, null);
                        note += ", nothing seen under it";
                    }
                    else
                    {
                        note += ", on " + support.Value.y.ToString("F3") + ", body " + (body?.Count ?? 0) + " (link " + LastLink.ToString("F3") + "), noise " + support.Value.noise.ToString("F3");
                        outline = Stand(frame, found, body, support.Value, through, corePoint, eye, found.box, found.box, ref note);
                    }
                }
            }

            found.placed = Agrees(frame, found, eye, outline, out string why);
            if (!found.placed) found.onSurface = false;
            found.note = (found.placed ? "" : "NOT PLACED: " + why + " | ") + note;
        }

        // Where one listed thing ends and its neighbour begins, in the picture: a line between their boxes.
        private struct Cut
        {
            public bool alongX, keepBelow;
            public float at;
            public bool Keeps(DepthPoint d)
            {
                float v = alongX ? d.viewport.x : 1f - d.viewport.y;
                return keepBelow ? v <= at : v >= at;
            }
        }

        private static List<Cut> Cuts(Found found, IList<Found> all)
        {
            var cuts = new List<Cut>();
            if (all == null) return cuts;
            var me = MeanBox(found);
            float mw = Mathf.Max(1e-4f, me[2] - me[0]), mh = Mathf.Max(1e-4f, me[3] - me[1]), mcx = (me[0] + me[2]) * .5f, mcy = (me[1] + me[3]) * .5f;
            foreach (var other in all)
            {
                if (other == null || other == found || other.box == null || other.box.Length != 4) continue;
                var o = MeanBox(other);
                float ow = Mathf.Max(1e-4f, o[2] - o[0]), oh = Mathf.Max(1e-4f, o[3] - o[1]), ocx = (o[0] + o[2]) * .5f, ocy = (o[1] + o[3]) * .5f;
                // Things of about one size, side by side or one above the other (a laptop around a mug is
                // not its neighbour, nor is a box that lies around this one's middle).
                float ratio = ow * oh / (mw * mh);
                if (ratio > 4f || ratio < .25f || Inside(o, mcx, mcy) || Inside(me, ocx, ocy)) continue;
                bool alongX = Mathf.Abs(ocx - mcx) / (mw + ow) >= Mathf.Abs(ocy - mcy) / (mh + oh);
                float a = alongX ? mcx : mcy, b = alongX ? ocx : ocy, wa = alongX ? mw : mh, wb = alongX ? ow : oh;
                // Each box reaches toward the other in proportion to its size.
                cuts.Add(new Cut { alongX = alongX, at = (a * wb + b * wa) / (wa + wb), keepBelow = a < b });
            }
            return cuts;
        }

        // The box of some depth points in the picture (x0, y0, x1, y1, origin top-left): their 5th to 95th
        // percentile, widened by half the usual gap between samples (samples stop short of the true edges).
        private static float[] ViewBox(List<DepthPoint> part)
        {
            var xs = part.Select(d => d.viewport.x).OrderBy(x => x).ToList();
            var ys = part.Select(d => 1f - d.viewport.y).OrderBy(y => y).ToList();
            int k = part.Count / 20;
            float gx = SampleGap(xs) * .5f, gy = SampleGap(ys) * .5f;
            return new[] { Mathf.Clamp01(xs[k] - gx), Mathf.Clamp01(ys[k] - gy), Mathf.Clamp01(xs[xs.Count - 1 - k] + gx), Mathf.Clamp01(ys[ys.Count - 1 - k] + gy) };
        }

        // Looks around the model's boxes for the thing itself: of everything standing on a level there, the
        // connected piece that lies best over the boxes. True: the thing is placed from that piece.
        private static bool Snap(Frame frame, Found found, List<DepthPoint> mine, List<float[]> hints, IList<Found> all, Vector3 eye, ref string note)
        {
            var window = Window(found);
            float band = Under(window[3] - window[1]);
            var inside = new List<DepthPoint>();
            var under = new List<DepthPoint>();
            foreach (var d in mine)
            {
                float bx = d.viewport.x, by = 1f - d.viewport.y;
                if (bx < window[0] || bx > window[2] || by < window[1]) continue;
                if (by <= window[3]) inside.Add(d);
                else if (by <= window[3] + band) under.Add(d);
            }
            if (inside.Count < 8) { note = "depth search: " + inside.Count + " points; "; return false; }
            var levels = Levels(inside.Concat(under).ToList(), eye.y);
            int enough = Mathf.Max(4, Mathf.RoundToInt((inside.Count + under.Count) * .015f));
            float edge = 1.5f / DepthCols;
            var cuts = Cuts(found, all);
            List<DepthPoint> best = null;
            float[] bestBox = null;
            Level bestLevel = default;
            float bestScore = 0f, nearly = 0f;
            int pieces = 0, behind = 0, afloat = 0, beside = 0;
            // The box in use, with a quarter of its size around it: the thing lies in there (a heap of
            // things that only passes through it is not the thing).
            var home = hints[0];
            foreach (var level in levels)
            {
                if (level.count < enough) continue;
                float lift = Mathf.Max(.012f, level.noise * 3f);
                var standing = inside.Where(d => d.point.y > level.y + lift).ToList();
                if (standing.Count < 5) continue;
                foreach (var whole in Surfaces(standing, level.noise))
                {
                    if (whole.Count < 5) continue;
                    // As wide as the whole window: what is behind (a monitor, a wall), not the thing.
                    var wholeBox = ViewBox(whole);
                    if (wholeBox[0] <= window[0] + edge && wholeBox[2] >= window[2] - edge) { behind++; continue; }
                    var part = cuts.Count == 0 ? whole : whole.Where(d => cuts.All(c => c.Keeps(d))).ToList();
                    if (part.Count < 5) continue;
                    // It reaches down to the level and rises off it.
                    if (part.Min(d => d.point.y) > level.y + lift + .035f || part.Max(d => d.point.y) < level.y + Mathf.Max(.025f, lift + .01f)) { afloat++; continue; }
                    pieces++;
                    var box = part.Count == whole.Count ? wholeBox : ViewBox(part);
                    float mx = (box[0] + box[2]) * .5f, my = (box[1] + box[3]) * .5f;
                    if (!hints.Any(h => Inside(h, mx, my, .15f)) || part.Count(d => Inside(home, d.viewport.x, 1f - d.viewport.y, .25f)) < part.Count * .75f) { beside++; continue; }
                    float score = hints.Average(h => Overlap(box, h));
                    nearly = Mathf.Max(nearly, score);
                    if (score < SnapScore || score <= bestScore) continue;
                    best = part; bestBox = box; bestLevel = level; bestScore = score;
                }
            }
            if (best == null)
            {
                note = "depth search: " + pieces + " standing pieces (" + beside + " beside the box, " + behind + " behind, " + afloat + " not on a level), best match " + nearly.ToString("F2") + "; ";
                return false;
            }
            string heap = "";
            if ((bestBox[2] - bestBox[0]) > (home[2] - home[0]) * HeapWider)
            {
                var own = OwnColour(frame, best, found.seen?.colors, bestLevel.noise);
                if (own != null)
                {
                    heap = ", its own colour " + own.Count + " of " + best.Count;
                    best = own;
                    bestBox = ViewBox(own);
                }
            }
            found.fit = bestBox;
            found.match = bestScore;
            note = "found by depth (match " + bestScore.ToString("F2") + " of " + pieces + " pieces, window " + inside.Count + "+" + under.Count + " points), levels " +
                   string.Join("/", levels.Select(l => l.y.ToString("F3") + "x" + l.count)) + ", on " + bestLevel.y.ToString("F3") + ", body " + best.Count + " (link " + LastLink.ToString("F3") +
                   "), noise " + bestLevel.noise.ToString("F3") + heap;
            Vector2 middle = new Vector2((bestBox[0] + bestBox[2]) * .5f, 1f - (bestBox[1] + bestBox[3]) * .5f);
            Stand(frame, found, best, bestLevel, frame.ray(middle), Median(best.Select(d => d.point).ToList()), eye, bestBox, window, ref note);
            return true;
        }

        public const float HeapWider = 1.3f;

        // Of a heap of depth points, the connected part whose colour in the picture is the thing's main
        // colour (the model's first colour). Null: no picture, no colour, or the colour does not tell the
        // thing from the rest (nearly all or hardly any of the heap has it).
        private static List<DepthPoint> OwnColour(Frame frame, List<DepthPoint> heap, string[] colours, float noise)
        {
            if (frame.pixels == null || colours == null || colours.Length == 0 || !TryColour(colours[0], out var target)) return null;
            var own = new List<DepthPoint>();
            foreach (var d in heap)
            {
                var c = ColourAt(frame, d.viewport, .004f);
                if (c.HasValue && Like(c.Value, target)) own.Add(d);
            }
            if (own.Count < Mathf.Max(8, heap.Count / 5) || own.Count > heap.Count * .85f) return null;
            var part = Surfaces(own, noise).OrderByDescending(g => g.Count).First();
            return part.Count >= Mathf.Max(8, own.Count / 2) ? part : null;
        }

        // The same colour, as a camera sees it: a vivid colour by its hue (shade and light change the rest),
        // white, grey and black by how light they are.
        public static bool Like(Color colour, Color target)
        {
            Color.RGBToHSV(target, out float th, out float ts, out float tv);
            Color.RGBToHSV(colour, out float h, out float s, out float v);
            if (ts > .28f && tv > .2f) return Mathf.Min(Mathf.Abs(h - th), 1f - Mathf.Abs(h - th)) < .06f && s > ts * .5f && v > .15f;
            return s < Mathf.Max(.22f, ts + .15f) && Mathf.Abs(v - tv) < .2f;
        }

        // A thing on a level: lying on it (no body, or a body that faces up: a book) or standing on it (its
        // body's middle, width and top). `sizeBox`: its box in the picture (a round thing's circle is about
        // that wide); `capBox`: its top is not above this box's top edge. Returns whether it has an outline
        // to check against its box.
        private static bool Stand(Frame frame, Found found, List<DepthPoint> body, Level level, Ray through, Vector3 corePoint, Vector3 eye, float[] sizeBox, float[] capBox, ref string note)
        {
            Vector2 V(float x, float y) => new Vector2(x, 1f - y);
            float x0 = sizeBox[0], x1 = sizeBox[2], cx = (x0 + x1) * .5f, cy = (sizeBox[1] + sizeBox[3]) * .5f;
            float baseY = level.y, noise = level.noise;
            float Distance(DepthPoint d) => Vector3.Distance(d.point, eye);
            bool flat = body == null || body.Count(d => d.normal.y > .8f) > body.Count * .6f;
            bool outline = false;
            Vector3 centre;
            float width, topY;
            if (flat)
            {
                // Lying on it: the middle of its box on that level (or the top of a low thing, like a book).
                var plane = new Plane(Vector3.up, new Vector3(0f, baseY, 0f));
                Vector3 middle = body != null && body.Count >= 4 ? Median(body.Select(d => d.point).ToList()) : OnPlane(plane, through, corePoint);
                var at = new Plane(Vector3.up, middle);
                width = Vector3.Distance(OnPlane(at, frame.ray(V(x0, cy)), middle), OnPlane(at, frame.ray(V(x1, cy)), middle));
                centre = middle;
                // (A low thing seen from above - a book, a bowl: its top is where its highest points are.)
                var high = body != null && body.Count >= 4 ? body.Select(d => d.point.y).OrderBy(y => y).ToList() : null;
                topY = Mathf.Max(high != null ? high[high.Count - 1 - high.Count / 10] : middle.y, baseY + .005f);
                note += ", flat";
            }
            else
            {
                Vector3 facing = Vector3.ProjectOnPlane(Median(body.Select(d => d.point).ToList()) - eye, Vector3.up).normalized;
                if (facing.sqrMagnitude < .01f) facing = Vector3.forward;
                Vector3 side = Vector3.Cross(Vector3.up, facing);
                var lateral = body.Select(d => Vector3.Dot(d.point - eye, side)).OrderBy(x => x).ToList();
                var along = body.Select(d => Vector3.Dot(d.point - eye, facing)).OrderBy(x => x).ToList();
                var heights = body.Select(d => d.point.y).OrderBy(x => x).ToList();
                float lo = lateral[lateral.Count / 20], hi = lateral[lateral.Count - 1 - lateral.Count / 20];
                float front = along[along.Count / 10];
                var bodyDistances = body.Select(Distance).OrderBy(x => x).ToList();
                float bodyDistance = bodyDistances[bodyDistances.Count / 2];
                float boxAcross = Vector3.Distance(frame.ray(V(x0, cy)).GetPoint(bodyDistance), frame.ray(V(x1, cy)).GetPoint(bodyDistance));
                // Round things (mugs, cups, pots, vases): the side surface seen from above is an arc; a circle
                // through it gives the middle and the width directly, and leaves out whatever touches it
                // behind. Only when the circle is about as wide as the box (a monitor's edge is not a 3 cm cup).
                var sides = body.Where(d => Mathf.Abs(d.normal.y) < .6f)
                    .Select(d => new Vector2(Vector3.Dot(d.point - eye, side), Vector3.Dot(d.point - eye, facing))).ToList();
                if (sides.Count >= 8 && FitCircle(sides, Mathf.Max(.008f, noise * 1.5f), out Vector2 circle, out float radius, out float kept) && radius > .005f && radius < .25f && kept > .55f &&
                    circle.y > front - .005f && circle.y < front + radius * 1.6f + .02f && radius * 2f > boxAcross * .4f && radius * 2f < boxAcross * 1.5f)
                {
                    width = radius * 2f;
                    centre = eye + side * circle.x + facing * circle.y;
                    note += ", round r " + radius.ToString("F3") + " kept " + kept.ToString("F2");
                }
                else
                {
                    // Samples stop short of the true edges by up to half their spacing on each side.
                    width = hi - lo + SampleGap(lateral);
                    float back = Mathf.Min(width * .5f, .1f);
                    centre = eye + side * ((lo + hi) * .5f) + facing * (front + back);
                    note += ", spread " + lo.ToString("F3") + ".." + hi.ToString("F3") + ", front " + front.ToString("F3");
                }
                topY = Mathf.Max(heights[heights.Count - 1 - heights.Count / 20] + SampleGap(heights) * .5f, baseY + .01f);
                // Never above the box's top edge at the object's own distance.
                Vector3 toCentre = Vector3.ProjectOnPlane(centre - eye, Vector3.up);
                var centrePlane = new Plane(toCentre.sqrMagnitude > 1e-4f ? -toCentre.normalized : Vector3.back, centre);
                float boxTop = OnPlane(centrePlane, frame.ray(V((capBox[0] + capBox[2]) * .5f, capBox[1])), centre + Vector3.up * topY).y + .02f;
                topY = Mathf.Clamp(topY, baseY + .01f, Mathf.Max(baseY + .01f, boxTop));
                outline = true;
            }
            found.basePoint = new Vector3(centre.x, baseY, centre.z);
            found.top = new Vector3(centre.x, topY, centre.z);
            found.width = width;
            found.height = topY - baseY;
            found.onSurface = true;
            if (body != null) found.body = body;
            return outline;
        }

        // The horizontal surfaces among the points (facing up, below the eyes), highest first: the fullest
        // 3 cm of height is one level (its middle height, how many points, how much they scatter), then
        // the fullest of what is left, and so on.
        public static List<Level> Levels(List<DepthPoint> points, float eyeY)
        {
            var ys = points.Where(d => d.normal.y > .7f && d.point.y < eyeY - .1f).Select(d => d.point.y).OrderBy(y => y).ToList();
            var levels = new List<Level>();
            while (ys.Count >= 3 && levels.Count < 6)
            {
                int best = 0, bestStart = 0, j = 0;
                for (int i = 0; i < ys.Count; i++)
                {
                    while (ys[i] - ys[j] > .03f) j++;
                    if (i - j + 1 > best) { best = i - j + 1; bestStart = j; }
                }
                if (best < 3) break;
                float mid = ys[bestStart + best / 2];
                var off = ys.Where(y => Mathf.Abs(y - mid) < .025f).Select(y => Mathf.Abs(y - mid)).OrderBy(x => x).ToList();
                levels.Add(new Level { y = mid, count = off.Count, noise = Mathf.Clamp(off[off.Count / 2] * 1.5f, .002f, .015f) });
                ys.RemoveAll(y => Mathf.Abs(y - mid) < .025f);
            }
            return levels.OrderByDescending(l => l.y).ToList();
        }

        // The thing itself among the points standing off a level: the connected surfaces; the big one near
        // the box's middle and in front (so what stands behind it or beside it is left out).
        private static List<DepthPoint> Body(List<DepthPoint> standing, float noise, Vector2 middleView, float size, float near, Func<DepthPoint, float> distance)
        {
            if (standing.Count < 4) return null;
            size = Mathf.Max(.01f, size);
            List<DepthPoint> body = null;
            float best = float.NegativeInfinity;
            foreach (var part in Surfaces(standing, noise))
            {
                var xs = part.Select(d => d.viewport.x).OrderBy(x => x).ToList();
                var ys = part.Select(d => d.viewport.y).OrderBy(y => y).ToList();
                Vector2 view = new Vector2(xs[xs.Count / 2], ys[ys.Count / 2]);
                float ahead = part.Min(distance) - near;
                float score = part.Count / (1f + 2f * (view - middleView).magnitude / size + ahead / .05f);
                if (score > best) { best = score; body = part; }
            }
            return body;
        }

        // How much of the box's width the points span (their 5th to 95th percentile, inside the box).
        private static float Across(List<DepthPoint> part, float x0, float x1)
        {
            var xs = part.Select(d => d.viewport.x).OrderBy(x => x).ToList();
            float lo = Mathf.Max(x0, xs[xs.Count / 20]), hi = Mathf.Min(x1, xs[xs.Count - 1 - xs.Count / 20]);
            return Mathf.Max(0f, hi - lo) / Mathf.Max(1e-4f, x1 - x0);
        }

        // On an upright plane through `centre`, facing the camera: the box's sides give the width, its top
        // and bottom edges the height. `supportY`: the level it stands on (when one was seen below it and
        // near its bottom edge), else it is in the air.
        private static void OnFacingPlane(Frame frame, Found found, Vector3 centre, Vector3 eye, float? supportY)
        {
            float x0 = found.box[0], y0 = found.box[1], x1 = found.box[2], y1 = found.box[3], cx = (x0 + x1) * .5f, cy = (y0 + y1) * .5f;
            Vector2 V(float x, float y) => new Vector2(x, 1f - y);
            Vector3 facing = Vector3.ProjectOnPlane(centre - eye, Vector3.up);
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(frame.forward, Vector3.up);
            facing = facing.sqrMagnitude < 1e-4f ? Vector3.forward : facing.normalized;
            var plane = new Plane(-facing, centre);
            float width = Vector3.Distance(OnPlane(plane, frame.ray(V(x0, cy)), centre), OnPlane(plane, frame.ray(V(x1, cy)), centre));
            float topY = OnPlane(plane, frame.ray(V(cx, y0)), centre).y, bottomY = OnPlane(plane, frame.ray(V(cx, y1)), centre).y;
            if (topY < bottomY) (topY, bottomY) = (bottomY, topY);
            bool stands = supportY.HasValue && supportY.Value > bottomY - .35f;
            float baseY = stands ? Mathf.Min(supportY.Value, centre.y) : Mathf.Min(bottomY, centre.y);
            found.basePoint = new Vector3(centre.x, baseY, centre.z);
            found.top = new Vector3(centre.x, Mathf.Max(topY, baseY + .01f), centre.z);
            found.width = Mathf.Max(width, .005f);
            found.height = found.top.y - baseY;
            found.onSurface = stands;
        }

        // Does the placement agree with the picture it came from? (A thing found by depth was picked for
        // lying over the model's boxes; its middle only has to be in the part of the picture searched.)
        private static bool Agrees(Frame frame, Found found, Vector3 eye, bool outline, out string why)
        {
            why = null;
            Vector3 mid = (found.basePoint + found.top) * .5f;
            float distance = Vector3.Distance(mid, eye);
            if (distance < .12f || distance > 3f) { why = "too near or too far (" + distance.ToString("F2") + " m)"; return false; }
            float most = found.big ? 3f : .7f;
            if (found.width < .005f || found.width > most || found.height > most)
            {
                why = "size " + found.width.ToString("F2") + " x " + found.height.ToString("F2") + " m";
                return false;
            }
            bool byDepth = found.fit != null;
            var within = byDepth ? Window(found) : found.box;
            float x0 = within[0], y0 = within[1], x1 = within[2], y1 = within[3];
            Vector2 view = frame.toViewport(mid);
            float bx = view.x, by = 1f - view.y, mx = byDepth ? 0f : (x1 - x0) * .15f + .01f, my = byDepth ? 0f : (y1 - y0) * .15f + .01f;
            if (bx < x0 - mx || bx > x1 + mx || by < y0 - my || by > y1 + my) { why = "its middle falls outside its box"; return false; }
            // What the depth sees along that line is about that far away.
            var along = new List<(float off, float distance)>();
            foreach (var d in frame.depth)
            {
                if (d.owner != 0 && d.owner != found.owner) continue;
                float off = (d.viewport - view).sqrMagnitude;
                if (off < .04f * .04f) along.Add((off, Vector3.Distance(d.point, eye)));
            }
            if (along.Count >= 2)
            {
                var seen = along.OrderBy(a => a.off).Take(4).Select(a => a.distance).OrderBy(x => x).ToList();
                float there = seen[seen.Count / 2];
                if (Mathf.Abs(there - distance) > .08f + .15f * distance)
                {
                    why = "the depth there is " + there.ToString("F2") + " m, not " + distance.ToString("F2") + " m";
                    return false;
                }
            }
            if (outline && !byDepth)
            {
                // A standing thing's outline, back in the picture, lies over its box.
                Vector3 facing = Vector3.ProjectOnPlane(mid - eye, Vector3.up);
                facing = facing.sqrMagnitude < 1e-4f ? Vector3.forward : facing.normalized;
                Vector3 side = Vector3.Cross(Vector3.up, facing);
                float half = found.width * .5f, px0 = 1f, px1 = 0f, py0 = 1f, py1 = 0f;
                foreach (float sx in new[] { -half, half })
                    foreach (float sz in new[] { -half, half })
                        foreach (float y in new[] { found.basePoint.y, found.top.y })
                        {
                            Vector2 p = frame.toViewport(new Vector3(found.basePoint.x, y, found.basePoint.z) + side * sx + facing * sz);
                            px0 = Mathf.Min(px0, p.x); px1 = Mathf.Max(px1, p.x); py0 = Mathf.Min(py0, 1f - p.y); py1 = Mathf.Max(py1, 1f - p.y);
                        }
                float overlap = Overlap(found.box, new[] { Mathf.Clamp01(px0), Mathf.Clamp01(py0), Mathf.Clamp01(px1), Mathf.Clamp01(py1) });
                if (overlap < .12f) { why = "its outline does not lie over its box (" + overlap.ToString("F2") + ")"; return false; }
            }
            return true;
        }

        // ------------------------------------------------------------ without depth

        // No depth at all (not supported, or the Editor without the stand-in): a ray through the bottom of
        // the box meets the room model's desk, and rays through its sides give an estimated width.
        private void PlaceByDeskPlane(Frame frame, Found found)
        {
            float x0 = found.box[0], y0 = found.box[1], x1 = found.box[2], y1 = found.box[3];
            Vector2 V(float x, float y) => new Vector2(x, 1f - y);
            int mask = arrival != null && arrival.handoff.meeting.environment != null ? arrival.handoff.meeting.environment.GeometryMask : Physics.DefaultRaycastLayers;
            Ray bottom = frame.ray(V((x0 + x1) * .5f, y1));
            // A desk or shelf below the eyes (a surface at or above eye height is not where it stands).
            if (Physics.Raycast(bottom, out var hit, 4f, mask, QueryTriggerInteraction.Ignore) && hit.normal.y > .6f && hit.point.y < bottom.origin.y - .12f && hit.distance > .2f)
            {
                found.basePoint = hit.point;
                found.onSurface = true;
            }
            else found.basePoint = bottom.GetPoint(.6f);
            // A plane through the object, facing the camera: its sides and top land on it.
            Vector3 eye = bottom.origin;
            Vector3 facing = Vector3.ProjectOnPlane(found.basePoint - eye, Vector3.up).normalized;
            if (facing.sqrMagnitude < .01f) facing = Vector3.forward;
            // The bottom of the box is the object's front edge: move the plane back by about half its width.
            var plane = new Plane(-facing, found.basePoint);
            Vector3 left = OnPlane(plane, frame.ray(V(x0, (y0 + y1) * .5f)), found.basePoint);
            Vector3 right = OnPlane(plane, frame.ray(V(x1, (y0 + y1) * .5f)), found.basePoint);
            found.width = Vector3.Distance(left, right);
            plane = new Plane(-facing, found.basePoint + facing * found.width * .5f);
            left = OnPlane(plane, frame.ray(V(x0, (y0 + y1) * .5f)), found.basePoint);
            right = OnPlane(plane, frame.ray(V(x1, (y0 + y1) * .5f)), found.basePoint);
            found.width = Vector3.Distance(left, right);
            Vector3 top = OnPlane(plane, frame.ray(V((x0 + x1) * .5f, y0)), found.basePoint);
            found.width = Mathf.Max(found.width, .02f);
            found.basePoint += facing * found.width * .5f;
            found.top = new Vector3(found.basePoint.x, Mathf.Max(top.y, found.basePoint.y + .01f), found.basePoint.z);
            found.height = found.top.y - found.basePoint.y;
            found.placed = found.onSurface;
            found.note = found.onSurface ? "desk plane (no depth)" : "NOT PLACED: no depth and no desk under it";
        }
    }
}
