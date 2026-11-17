using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F30 B: tracing the things laid on the desk for a tray.
    //  - Spark takes one picture (the headset camera; no AI call). Each point of the desk in front of the
    //    person (2 mm apart) is looked up in the picture: what differs from the desk's own colour is a
    //    thing. Shadows are let through (they only darken the desk), crumbs are dropped, and each thing's
    //    edge becomes an outline drawn on the real desk around it.
    //  - Wrong somewhere? A fingertip on the desk pulls the line under it toward itself.
    //  - The camera's size can be a little off: the tape across one thing sizes them all.
    //  - "okay": the outlines become the tray's pockets. Tall things look longer from an angle; trays are
    //    for things lying flat (a mug gets a coaster).
    public sealed partial class MakerSession
    {
        public sealed class Traced
        {
            public List<Vector3> outline = new List<Vector3>();
            public Vector3 centre;
            public string name;
            public LineRenderer line;
            public bool edited;
        }

        public readonly List<Traced> TracedThings = new List<Traced>();
        public bool TraceActive { get; private set; }
        public bool Tracing { get; private set; }
        public int Traces { get; private set; }
        public float TraceScale { get; private set; } = 1f;
        public string TraceNote { get; private set; } = "";
        public const float TraceGrid = .002f, TraceWide = .56f, TraceDeep = .42f, NudgeReach = .015f;
        public static bool TraceDebug;   // tests: write what Spark saw on the desk to trace_debug.png

        private Material traceLineMaterial;
        private float traceDeskY;
        private bool traceFresh;

        private void StartTrace()
        {
            TraceActive = true;
            ClearTrace();
            TraceActive = true;
            Say(Crew.Fixer, "Lay the things on the desk in front of you, a little apart. Spark will trace them.");
            StartCoroutine(TraceNow(Scripted ? .3f : 2.5f));
        }

        private IEnumerator TraceNow(float wait)
        {
            if (Tracing || Eyes == null) yield break;
            Tracing = true;
            foreach (var t in TracedThings) if (t.line != null) Destroy(t.line.gameObject);
            TracedThings.Clear();
            working.Add(Crew.Spark);
            Cue(Crew.Spark, "thinking");
            yield return new WaitForSeconds(wait);
            Body(Crew.Spark)?.Play("Peek");
            ToySounds.Play(ToySounds.Cue.Press, Head != null ? Head.position + Head.forward * .3f : transform.position, .4f, 1.8f);
            RoomEyes.Frame frame = null;
            yield return Eyes.Capture(f => frame = f);
            working.Remove(Crew.Spark);
            Cue(Crew.Spark, "idle");
            Tracing = false;
            Traces++;
            if (Current != Step.Measuring || Kind != "tray") yield break;
            if (frame?.texture == null) { Say(Crew.Spark, Eyes.LastError ?? "I can't see. Say ready to try again."); yield break; }
            var outlines = TraceFrame(frame);
            TraceScale = 1f;
            foreach (var outline in outlines)
            {
                var traced = new Traced { outline = outline };
                traced.centre = Centre(outline);
                traced.name = Eyes.LastFound.Where(f => f != null && f.placed && Vector3.ProjectOnPlane(f.basePoint - traced.centre, Vector3.up).magnitude < .06f)
                    .Select(f => MakerRoles.Clean(f.seen.name)).FirstOrDefault();
                traced.line = TraceLine(outline);
                TracedThings.Add(traced);
            }
            Talk.Add("Spark traced " + TracedThings.Count + " thing(s) on the desk");
            Debug.Log("[Windups Trace] " + TracedThings.Count + " outline(s). " + TraceNote);
            if (TracedThings.Count == 0)
            {
                Say(Crew.Spark, "I can't see anything lying there. Lay them down and say ready, or say let me draw.");
                yield break;
            }
            ToySounds.Play(ToySounds.Cue.Chime, TracedThings[0].centre, .6f, 1.4f);
            foreach (var t in TracedThings) ToyFx.Puff(t.centre + Vector3.up * .01f, .008f, 3);
            Say(Crew.Spark, TracedThings.Count == 1 ? "Traced it!" : $"Traced all {TracedThings.Count}!");
            Say(Crew.Fixer, "Fix a line with your fingertip. Pinch across one to check the size. Then say okay.");
        }

        // ------------------------------------------------------------ the picture -> outlines on the desk

        private List<List<Vector3>> TraceFrame(RoomEyes.Frame frame)
        {
            var result = new List<List<Vector3>>();
            var head = Head;
            if (head == null) return result;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            Vector3 centre = head.position + fwd * .4f;
            int geometry = arrival.handoff.meeting.environment.GeometryMask;
            float deskY = Physics.Raycast(centre, Vector3.down, out var top, 1.4f, geometry, QueryTriggerInteraction.Ignore) && top.normal.y > .9f
                ? top.point.y : DeskHeightAt(centre, head.position.y - .5f);
            traceDeskY = deskY;
            int nx = Mathf.RoundToInt(TraceWide / TraceGrid), ny = Mathf.RoundToInt(TraceDeep / TraceGrid);
            Vector3 origin = new Vector3(centre.x, deskY, centre.z) - side * TraceWide * .5f - fwd * TraceDeep * .3f;
            // The camera never sees the app's own things (the crew, the mic, made things), so nothing is left out
            // for them; only points off this desk top (the room model) are.
            int mask = arrival.handoff.meeting.environment.GeometryMask;

            int n = nx * ny;
            var valid = new bool[n];
            var colours = new Color[n];
            var tex = frame.texture;
            int validCount = 0;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    Vector3 p = origin + side * (i * TraceGrid) + fwd * (j * TraceGrid);
                    int k = j * nx + i;
                    if (!Physics.Raycast(p + Vector3.up * .05f, Vector3.down, out var deskHit, .1f, mask, QueryTriggerInteraction.Ignore) || Mathf.Abs(deskHit.point.y - deskY) > .012f) continue;
                    Vector2 vp = frame.toViewport(p);
                    if (vp.x < .01f || vp.x > .99f || vp.y < .01f || vp.y > .99f) continue;
                    colours[k] = tex.GetPixelBilinear(vp.x, vp.y);
                    valid[k] = true;
                    validCount++;
                }
            if (validCount < 500) { TraceNote = "too little desk in view (" + validCount + ")"; return result; }
            // The desk's own colour: the median of what is seen on it.
            var sample = new List<Color>();
            int step = Mathf.Max(1, validCount / 4000);
            for (int k = 0, seen = 0; k < n; k++) if (valid[k] && seen++ % step == 0) sample.Add(colours[k]);
            float Median(System.Func<Color, float> channel) { var list = sample.Select(channel).OrderBy(x => x).ToList(); return list[list.Count / 2]; }
            var desk = new Color(Median(c => c.r), Median(c => c.g), Median(c => c.b));
            float Luma(Color c) => .299f * c.r + .587f * c.g + .114f * c.b;
            Vector2 Chroma(Color c) { float s = c.r + c.g + c.b + .03f; return new Vector2(c.r / s, c.g / s); }
            float deskLuma = Mathf.Max(.02f, Luma(desk));
            Vector2 deskChroma = Chroma(desk);
            var thing = new bool[n];
            for (int k = 0; k < n; k++)
            {
                if (!valid[k]) continue;
                float ratio = Luma(colours[k]) / deskLuma;
                // A shadow only darkens the desk (same colour, 55-100 % as bright): not a thing.
                thing[k] = (Chroma(colours[k]) - deskChroma).magnitude > .045f || ratio < .55f || ratio > 1.65f;
            }
            thing = Morph(thing, valid, nx, ny, true, 2);   // close small gaps
            thing = Morph(thing, valid, nx, ny, false, 1);  // drop crumbs
            if (TraceDebug)
            {
                // What Spark saw on the desk (top-down): its colour, dark red where it could not look, white outline on things.
                var debug = new Texture2D(nx, ny, TextureFormat.RGB24, false);
                for (int k = 0; k < n; k++) debug.SetPixel(k % nx, k / nx, !valid[k] ? new Color(.4f, 0f, 0f) : thing[k] ? Color.Lerp(colours[k], Color.white, .6f) : colours[k]);
                debug.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(Application.persistentDataPath, "trace_debug.png"), debug.EncodeToPNG());
                Destroy(debug);
            }
            // Separate things.
            var label = new int[n];
            int labels = 0, kept = 0;
            var queue = new Queue<int>();
            for (int start = 0; start < n; start++)
            {
                if (!thing[start] || label[start] != 0) continue;
                labels++;
                var members = new List<int>();
                bool border = false;
                label[start] = labels;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int k = queue.Dequeue();
                    members.Add(k);
                    int i = k % nx, j = k / nx;
                    if (i == 0 || j == 0 || i == nx - 1 || j == ny - 1) border = true;
                    void Visit(int q) { if (thing[q] && label[q] == 0) { label[q] = labels; queue.Enqueue(q); } }
                    if (i > 0) Visit(k - 1);
                    if (i < nx - 1) Visit(k + 1);
                    if (j > 0) Visit(k - nx);
                    if (j < ny - 1) Visit(k + nx);
                }
                float area = members.Count * TraceGrid * TraceGrid;
                if (border || area < 6e-4f || members.Count > validCount * .3f) continue;
                // Its edge: a soft field (-1 inside, +1 outside) and the loop where it crosses zero.
                int i0 = members.Min(k => k % nx) - 2, i1 = members.Max(k => k % nx) + 2, j0 = members.Min(k => k / nx) - 2, j1 = members.Max(k => k / nx) + 2;
                i0 = Mathf.Max(0, i0); j0 = Mathf.Max(0, j0); i1 = Mathf.Min(nx - 1, i1); j1 = Mathf.Min(ny - 1, j1);
                int w = i1 - i0 + 1, h = j1 - j0 + 1;
                var field = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float sum = 0f;
                        int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + i0 + dx, yy = y + j0 + dy;
                                if (xx < 0 || yy < 0 || xx >= nx || yy >= ny) continue;
                                sum += label[yy * nx + xx] == labels ? -1f : 1f;
                                cnt++;
                            }
                        field[y * w + x] = sum / Mathf.Max(1, cnt);
                    }
                var loops = FitShapes.Loops(field, w - 1, h - 1, new Vector2(i0 * TraceGrid, j0 * TraceGrid), TraceGrid);
                if (loops.Count == 0) continue;
                var loop = loops.OrderByDescending(l => Mathf.Abs(Area(l))).First();
                loop = Smooth(Thin(loop, .002f));
                var outline = loop.Select(q => origin + side * q.x + fwd * q.y + Vector3.up * .0015f).ToList();
                result.Add(outline);
                kept++;
            }
            TraceNote = $"desk {ColorUtility.ToHtmlStringRGB(desk)}, {validCount} desk points, {labels} blob(s), {kept} kept";
            return result;
        }

        private static bool[] Morph(bool[] on, bool[] valid, int nx, int ny, bool close, int radius)
        {
            bool[] Pass(bool[] src, bool grow)
            {
                var dst = new bool[src.Length];
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int k = j * nx + i;
                        if (!valid[k]) continue;
                        bool any = false, all = true;
                        for (int dj = -radius; dj <= radius; dj++)
                            for (int di = -radius; di <= radius; di++)
                            {
                                int ii = i + di, jj = j + dj;
                                bool v = ii >= 0 && jj >= 0 && ii < nx && jj < ny && src[jj * nx + ii];
                                any |= v;
                                all &= v;
                            }
                        dst[k] = grow ? any : all;
                    }
                return dst;
            }
            return close ? Pass(Pass(on, true), false) : Pass(Pass(on, false), true);
        }

        private static float Area(List<Vector2> loop)
        {
            float a = 0f;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++) a += (loop[j].x * loop[i].y - loop[i].x * loop[j].y);
            return a * .5f;
        }

        // Points at least `spacing` apart.
        private static List<Vector2> Thin(List<Vector2> loop, float spacing)
        {
            var kept = new List<Vector2> { loop[0] };
            foreach (var p in loop) if ((p - kept[kept.Count - 1]).magnitude >= spacing) kept.Add(p);
            return kept.Count >= 3 ? kept : loop;
        }

        // One round of corner cutting: the pixel steps go soft.
        private static List<Vector2> Smooth(List<Vector2> loop)
        {
            var result = new List<Vector2>(loop.Count * 2);
            for (int i = 0; i < loop.Count; i++)
            {
                Vector2 a = loop[i], b = loop[(i + 1) % loop.Count];
                result.Add(Vector2.Lerp(a, b, .25f));
                result.Add(Vector2.Lerp(a, b, .75f));
            }
            return result;
        }

        private static Vector3 Centre(List<Vector3> outline)
        {
            Vector3 sum = Vector3.zero;
            foreach (var p in outline) sum += p;
            return sum / Mathf.Max(1, outline.Count);
        }

        private LineRenderer TraceLine(List<Vector3> outline)
        {
            if (traceLineMaterial == null)
            {
                traceLineMaterial = new Material(ghostMaterial != null ? ghostMaterial : material);
                traceLineMaterial.SetColor("_BaseColor", new Color(.35f, .95f, .6f, .95f));
                owned.Add(traceLineMaterial);
            }
            var go = new GameObject("Traced Outline", typeof(LineRenderer));
            go.transform.SetParent(transform, false);
            var line = go.GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.widthMultiplier = .0022f;
            line.numCornerVertices = 2;
            line.sharedMaterial = traceLineMaterial;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.positionCount = outline.Count;
            line.SetPositions(outline.ToArray());
            return line;
        }

        private void ClearTrace()
        {
            foreach (var t in TracedThings) if (t.line != null) Destroy(t.line.gameObject);
            TracedThings.Clear();
            TraceActive = false;
            traceFresh = false;
            TraceScale = 1f;
        }

        private void HideTraceLines()
        {
            foreach (var t in TracedThings) if (t.line != null) t.line.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------ fixing and sizing

        private void TraceFingerFrame()
        {
            if (!TraceActive || Current != Step.Measuring || TracedThings.Count == 0) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || HandIO.Pinching(hand)) continue;
                NudgeAt(Tip(hand));
            }
        }

        // A fingertip on the desk pulls the outline under it toward itself.
        public bool NudgeAt(Vector3 tip)
        {
            if (Mathf.Abs(tip.y - traceDeskY) > .015f) return false;
            Vector3 onDesk = new Vector3(tip.x, traceDeskY + .0015f, tip.z);
            bool moved = false;
            foreach (var t in TracedThings)
            {
                bool changed = false;
                for (int i = 0; i < t.outline.Count; i++)
                {
                    float d = Vector3.ProjectOnPlane(t.outline[i] - onDesk, Vector3.up).magnitude;
                    if (d >= NudgeReach) continue;
                    float w = 1f - d / NudgeReach;
                    t.outline[i] = Vector3.Lerp(t.outline[i], onDesk, w * .35f);
                    changed = true;
                }
                if (!changed) continue;
                t.edited = true;
                t.centre = Centre(t.outline);
                if (t.line != null) t.line.SetPositions(t.outline.ToArray());
                moved = true;
            }
            if (moved) Heard();
            return moved;
        }

        // The tape (or a spoken size) across one traced thing sizes all of them: the camera's scale is off
        // the same way for everything on the desk.
        private void TraceMeasured(bool spoken)
        {
            if (TracedThings.Count == 0) { Say(Crew.Fixer, $"{Measured:0} millimetres. Let Spark trace first: say ready."); return; }
            Vector3 dir;
            Traced target;
            if (spoken || Tape.State != HandTape.Mode.Measured)
            {
                target = TracedThings.OrderByDescending(t => Extent(t, Head != null ? Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized) : Vector3.right)).First();
                dir = Head != null ? Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized) : Vector3.right;
            }
            else
            {
                Vector3 mid = (Tape.A + Tape.B) * .5f;
                dir = Vector3.ProjectOnPlane(Tape.B - Tape.A, Vector3.up).normalized;
                target = TracedThings.OrderBy(t => Vector3.ProjectOnPlane(t.centre - mid, Vector3.up).magnitude).First();
            }
            float extent = Extent(target, dir);
            float factor = Measured * .001f / Mathf.Max(.005f, extent);
            if (factor < .6f || factor > 1.6f)
            {
                Say(Crew.Fixer, $"{Measured:0} millimetres doesn't match what Spark traced. Measure across the widest part?");
                return;
            }
            foreach (var t in TracedThings)
            {
                for (int i = 0; i < t.outline.Count; i++) t.outline[i] = t.centre + (t.outline[i] - t.centre) * factor;
                if (t.line != null) t.line.SetPositions(t.outline.ToArray());
            }
            TraceScale *= factor;
            Talk.Add($"Sized the traced things by the tape: x{factor:0.00}");
            Say(Crew.Fixer, $"{Measured:0} millimetres. I sized them all. Say okay.");
        }

        private static float Extent(Traced t, Vector3 dir)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in t.outline) { float d = Vector3.Dot(p - t.centre, dir); lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); }
            return hi - lo;
        }

        // What a plain sentence means while tracing.
        private bool TraceVoice(string p)
        {
            if (TryMillimetres(p, out float mm)) { SetMeasure(mm, true); return true; }
            if (p.Contains("again") || p.Contains("ready") || p.Contains("trace") || p.Contains("다시") || p.Contains("준비") || p.Contains("됐어"))
            {
                StartCoroutine(TraceNow(Scripted ? .2f : .8f));
                return true;
            }
            if (IsYes(p) || p.Contains("use it") || p.Contains("that's it") || p.Contains("좋아")) { TraceConfirm(); return true; }
            Ack(Crew.Fixer, "Lay them down and say ready, or say okay.");
            return true;
        }

        public void TraceConfirm()
        {
            if (Current != Step.Measuring || Kind != "tray") return;
            if (TracedThings.Count == 0 && DrawnOutline == null) { Say(Crew.Spark, "Nothing traced yet. Lay them down and say ready."); return; }
            string names = string.Join(", ", TracedThings.Select(t => t.name ?? "a thing"));
            Talk.Add("Pockets for: " + names);
            Note("measure", "Traced your things", TracedThings.Count + " pocket(s)" + (TracedThings.Any(t => t.edited) ? ", fixed by hand" : "") + (Mathf.Abs(TraceScale - 1f) > .01f ? $", sized by the tape (x{TraceScale:0.00})" : ""));
            Hush();
            Heard();
            traceFresh = true;
            TraceActive = false;
            ShowTape(false);
            Current = Step.Shape;
            Say(Crew.Maker, "Rounded corners, round, or hug the shapes? Or say let me draw.");
        }

        // The pockets in tray space (mm), the tray's centre on the desk and its turn (forward = away from the person).
        private float[][] TrayPockets(out Vector3 centre, out Quaternion turn)
        {
            centre = Vector3.zero;
            foreach (var t in TracedThings) centre += t.centre;
            centre /= Mathf.Max(1, TracedThings.Count);
            centre.y = traceDeskY;
            Vector3 away = Head != null ? Vector3.ProjectOnPlane(centre - Head.position, Vector3.up) : Vector3.forward;
            turn = Quaternion.LookRotation(away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.forward, Vector3.up);
            var inverse = Quaternion.Inverse(turn);
            var c = centre;
            return TracedThings.Select(t => t.outline.SelectMany(p => { var l = inverse * (p - c); return new[] { l.x * 1000f, l.z * 1000f }; }).ToArray()).ToArray();
        }

        // Without traced things, the drawn outline is the one pocket (as wide as measured, else 80 mm).
        private float[][] DrawnPocket()
        {
            if (DrawnOutline == null || DrawnOutline.Length != 72) return null;
            float wide = 0f;
            for (int i = 0; i < 36; i++) wide = Mathf.Max(wide, DrawnOutline[i] + DrawnOutline[i + 36]);
            float scale = (HasMeasure ? Measured : 80f) / Mathf.Max(.1f, wide);
            var flat = new float[144];
            for (int i = 0; i < 72; i++)
            {
                float a = i * 5f * Mathf.Deg2Rad;
                flat[i * 2] = Mathf.Cos(a) * DrawnOutline[i] * scale;
                flat[i * 2 + 1] = Mathf.Sin(a) * DrawnOutline[i] * scale;
            }
            return new[] { flat };
        }

        private IEnumerator TrayPlan(string change)
        {
            Current = Step.Planning;
            Hush();
            Ack(Crew.Maker, change.Length > 0 ? "On it!" : "Ooh, nice.");
            yield return new WaitForSeconds(Scripted ? .3f : .8f);
            string w = (Shape + " " + change).ToLowerInvariant();
            bool fresh = Design == null || Design.kind != "tray";
            var d = fresh ? new CoasterDesign { kind = "tray", clearance = 2.4f, rimWidth = 5f, rimHeight = 10f, baseThickness = 2.4f, outer = "rounded" } : Design.Copy();
            if (fresh || traceFresh)
            {
                if (TracedThings.Count > 0)
                {
                    d.pockets = TrayPockets(out var centre, out var turn);
                    MugSpot = centre;
                    MugTurn = turn;
                    HasSpot = true;
                }
                else
                {
                    d.pockets = DrawnPocket();
                    if (d.pockets == null) { Say(Crew.Fixer, "Nothing to make pockets for yet. Lay your things down and say ready."); pendingChange = null; StartTrace(); yield break; }
                    var spot = DeskPointInFront();
                    MugSpot = spot;
                    Vector3 away = Head != null ? Vector3.ProjectOnPlane(spot - Head.position, Vector3.up).normalized : Vector3.forward;
                    MugTurn = Quaternion.LookRotation(away, Vector3.up);
                    HasSpot = true;
                }
                traceFresh = false;
            }
            if (w.Contains("hug") || w.Contains("organic") || w.Contains("blob") || w.Contains("follow") || w.Contains("따라") || w.Contains("감싸")) d.outer = "hug";
            else if (w.Contains("round") && !w.Contains("rounded") || w.Contains("circle") || w.Contains("oval") || w.Contains("동그") || w.Contains("원형")) d.outer = "round";
            else if ((w.Contains("drawn") || w.Contains("own") || w.Contains("그린")) && DrawnOutline != null && TracedThings.Count > 0) { d.outer = "drawn"; d.outline = DrawnOutline; }
            else if (w.Contains("rounded") || w.Contains("rect") || w.Contains("square") || w.Contains("corner") || w.Contains("네모") || w.Contains("모서리")) d.outer = "rounded";
            if (w.Contains("deeper") || w.Contains("taller") || w.Contains("higher") || w.Contains("깊")) d.rimHeight += 4f;
            if (w.Contains("shallower") || w.Contains("lower") || w.Contains("얕")) d.rimHeight -= 3f;
            if (w.Contains("thicker") || w.Contains("sturd") || w.Contains("두껍")) { d.rimWidth += 2f; d.baseThickness += 1f; }
            if (w.Contains("looser") || w.Contains("roomier") || w.Contains("헐렁")) d.clearance += 1.5f;
            if (w.Contains("snugger") || w.Contains("tighter") || w.Contains("빡빡")) d.clearance -= 1f;
            d.name = Look != null ? MakerRoles.Clean(Look.label) : TracedThings.Count > 1 ? "Desk tray" : "Little tray";
            if (string.IsNullOrEmpty(d.color)) d.color = Palette.LastOrDefault(s => s.kind == "colour")?.hex ?? "#e8d2a8";
            d.why = "";
            Design = d.Clamped();
            Note("design", Design.name, Design.Summary);
            int pockets = Design.pockets?.Length ?? 0;
            Say(Crew.Maker, pockets == 1 ? "A tray with a pocket for it. Making it!" : $"A tray with {pockets} pockets. Making it!");
            yield return FitAndBuild();
        }

        // ------------------------------------------------------------ test entries

        public void SimulateTrace() => StartCoroutine(TraceNow(.1f));
    }
}
