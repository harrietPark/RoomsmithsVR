using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F59 (2026-11-13, the user: "there are moments it needs sculpting"): the made model as clay in your hands.
    // "Sculpt it" / "let me shape it" (after a make, the model on the desk) turns it into its rebuilt solid and:
    //  - a fingertip pushed into it dents it (push), wherever it goes;
    //  - a pinch started on its surface pulls clay out after the hand (pull);
    //  - the left fingertip rubbed over it smooths (smooth);
    //  - "undo" takes back the last stroke; Maker's [Done] (or "done") keeps it, [Undo] takes a stroke back.
    // Each touch is a dab (SolidRebuild.Op "dab", sizes in mm, place 0-1 across the model's box), applied to the
    // view's field and meshed again on a worker thread (the main thread only swaps the finished mesh, FPS first). On
    // Done the dabs join the design's operations, so the print (fine voxels) and a restart replay the same strokes.
    // While sculpting, the model cannot be grabbed (a pinch on it is a pull) and Fixer's cuts are not offered.
    public sealed partial class MakerSession
    {
        public static float PushRadiusMm = 6f, PullRadiusMm = 5f, SmoothRadiusMm = 8f, DabSpacingMm = 2f, PullStartMm = 12f;
        private const string SculptLinePlain = "Push in with a finger. Rub with your left finger to smooth.";
        private const string SculptLineParts = "Touch a part and pull its handle. Push in with a finger, rub with your left.";
        // (a thing without parts of its own has no handles to mention)
        private string SculptLine => KnobOnlyWhenSculpting && Design != null && FitShapes.BaseParts(Design) != null ? SculptLineParts : SculptLinePlain;

        // 2026-11-15 (second Quest try: "sculpting failed again, the horse is gone"; the user's flow): Sculpt it is a
        // mode. The thing stays where it is (it cannot be picked up), a fingertip near a part outlines it and puts the
        // coral handle on its tip (only in this mode now, MakerSession.Parts.cs KnobOnlyWhenSculpting): pulled, the
        // part gets longer or shorter. The right finger pushes the clay in, the left one smooths it; a pinch no longer
        // pulls clay out (a pinch on the thing was always taken as a grab first, so it never did on the headset), it
        // only takes a handle. Done | Undo end it / take back the last touch or part change.
        //  - A part change during the mode puts the clay's touches into the design first (PauseSculpt: the part edit
        //    carries them along with the surface), then the clay comes back on the new shape (ResumeSculpt).
        //  - When the clay cannot be made in one piece (StartSculpt checks the rebuilt view), the thing keeps its look
        //    and only the handles work (clayRefused).
        private bool sculptMode, sculptPaused, clayRefused;
        private float sculptStrokeAt = -100f;
        public bool SculptMode => sculptMode;
        public bool ClayRefused => clayRefused;
        public int SculptResumes { get; private set; }

        private sealed class SculptState
        {
            public Transform target;
            public CoasterDesign design;
            public SolidRebuild.Field field, baseField;
            public Vector3 min, size;                       // the source box (op coordinates)
            public readonly List<SolidRebuild.Op> dabs = new List<SolidRebuild.Op>();   // all of them, in order
            public readonly List<SolidRebuild.Op> pending = new List<SolidRebuild.Op>();
            public readonly object gate = new object();
            public bool replay, running = true, busy;
            public SolidRebuild.Result ready, last;
            public Dictionary<long, Color32> colours;
            public float colourCell;
            public Color32 fallback;
            public int stroke;
            public Mesh mesh;
            public Material[] savedMaterials;
            public Mesh savedMesh;
            public Hand pullHand;
            public Vector3 lastPush, lastPull, lastSmooth;
            public bool pushing, smoothing;
            public float soundAt;
            public int applied;
            // F84: brush sizes for this model (mm), times the person's "bigger"/"smaller"; the last stroke counted per tool.
            public float pushMm, pullMm, smoothMm, sizeScale = 1f;
            public int countedStroke = -1;
            public float PushMm => pushMm * sizeScale;
            public float PullMm => pullMm * sizeScale;
            public float SmoothMm => smoothMm * sizeScale;
        }

        private SculptState sculpt;
        public bool Sculpting => sculpt != null;
        public int SculptDabs => sculpt?.dabs.Count ?? 0;
        public int SculptMeshes { get; private set; }

        // ------------------------------------------------------------ in and out

        private IEnumerator StartSculpt(bool resume = false)
        {
            if (Sculpting || Product == null || Design == null || Design.kind != "model" || Current != Step.Done || Modelling || cutting) yield break;
            if (resume && (!sculptMode || clayRefused)) yield break;
            var target = Product;
            var design = Design.Copy();
            ClearGhosts();
            // F84 (the user: "does it turn into clay? can't it stay as it is?"): it keeps its look; while it gets ready
            // Maker works its hands (no "turning into clay" line). The decision's pills go meanwhile.
            if (!resume)
            {
                CloseQuestion("sculpt starting");
                sculptMode = true; // (the thing is held still from now on: SolidHolds)
                sculptPaused = clayRefused = false;
            }
            var makerBody = Body(Crew.Maker);
            makerBody?.LookAtPoint(target.position, 4f);
            makerBody?.StartWork("Work_Loop");
            yield return ReadTexture(design);
            var job = SolidJob(design, false);
            if (job == null) { makerBody?.StopWork(false); yield break; }
            job.room = SolidRebuild.SculptRoom; // room to pull clay out
            yield return RunJob(job);
            makerBody?.StopWork(true);
            if (!sculptMode) yield break; // (ended meanwhile)
            if (target == null || job.result == null || job.error != null || target != Product) { EndSculptMode(); Say(Crew.Maker, "I can't get it ready to shape right now."); if (target == Product) ShowShapeChoices(); yield break; }
            // The clay must be the thing in one piece; else it keeps its look and only the part handles work.
            if (job.result.vertices.Length == 0 || job.result.stats.pieces > 1)
            {
                WriteInputLog("sculpt | clay refused: " + job.result.stats.pieces + " pieces, " + job.result.volumeCm3.ToString("0") + " cm3", true);
                clayRefused = true;
                if (resume) yield break;
                if (FitShapes.BaseParts(Design) == null) { EndSculptMode(); Say(Crew.Maker, "I can't shape this one by hand, sorry."); ShowShapeChoices(); yield break; }
                Say(Crew.Maker, "This one won't turn to clay. Pull its parts by their handles!");
                AskSculptDone(SculptLessonDoneLine);
                yield break;
            }
            var paint = target.GetComponentInChildren<PaintableThing>();
            var filter = paint != null ? paint.GetComponent<MeshFilter>() : null;
            var renderer = paint != null ? paint.Renderer : null;
            if (filter == null || renderer == null) yield break;
            var s = new SculptState
            {
                target = target, design = design, field = job.field, baseField = job.baseField ?? job.field.Clone(),
                min = job.field.sourceMin, size = job.field.sourceSize, fallback = design.Tint(new Color(.95f, .85f, .62f)),
                savedMaterials = renderer.sharedMaterials, savedMesh = filter.sharedMesh, last = job.result
            };
            if (design.ops != null) s.dabs.AddRange(design.ops.Where(o => o != null && o.kind == "dab").Select(o => o.Copy()));
            s.stroke = s.dabs.Count > 0 ? s.dabs.Max(o => o.stroke) : 0;
            SizeBrushes(s);
            BuildColourLookup(s, job.result);
            s.mesh = new Mesh { name = "Clay", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            s.mesh.MarkDynamic();
            owned.Add(s.mesh);
            ShowSculptMesh(s, job.result);
            filter.sharedMesh = s.mesh;
            renderer.sharedMaterials = new[] { SolidLook(design) };
            sculpt = s;
            Task.Run(() => SculptWorker(s));
            if (resume) { SculptResumes++; WriteInputLog("sculpt | clay back after a part change (" + s.dabs.Count + " touches kept)", true); yield break; }
            Remember(); // (where it stands)
            TeachSculpt(); // F84: the three-card lesson the first time, else the Done | Undo question at once
            Talk.Add("Person started sculpting the made thing by hand");
            ToySounds.Play(ToySounds.Cue.Pop, target.position, .4f, .9f);
        }

        // Before a part change in the mode: the clay's touches go into the design and the clay stops (its view stays
        // until the part change shows the new shape).
        private void PauseSculpt()
        {
            var s = sculpt;
            if (s == null || Design == null) return;
            lock (s.gate) s.running = false;
            sculpt = null;
            sculptPaused = true;
            HideSculptTools();
            var ops = new List<SolidRebuild.Op>();
            if (Design.ops != null) ops.AddRange(Design.ops.Where(o => o != null && o.kind != "dab"));
            lock (s.gate) ops.AddRange(s.dabs.Select(o => o.Copy()));
            Design.ops = ops.Count > 0 ? ops.ToArray() : null;
            if (Product != null) madeDesigns[Product] = Design;
            WriteInputLog("sculpt | paused for a part change (" + s.dabs.Count + " touches kept)", true);
        }

        // After the part change: the clay comes back on the new shape (once its view is shown: `waitForView`).
        private IEnumerator ResumeSculpt(bool waitForView)
        {
            if (!sculptMode || !sculptPaused) yield break;
            int views = SolidViews;
            if (waitForView)
            {
                yield return null;
                for (float until = Time.time + 20f; Time.time < until && sculptMode && splitsRunning > 0; ) yield return null;
                if (Design != null && Design.ops != null && Design.ops.Length > 0)
                    for (float until = Time.time + 20f; Time.time < until && sculptMode && SolidViews == views; ) yield return null;
            }
            if (!sculptMode) yield break;
            sculptPaused = false;
            if (clayRefused) yield break;
            yield return StartSculpt(true);
        }

        // The mode ends (Done, cancelled, the step moved on): nothing of it stays up.
        private void EndSculptMode()
        {
            sculptMode = sculptPaused = clayRefused = false;
            HideSculptTools();
            if (CurrentLesson != null && CurrentLesson.name.StartsWith("sculpt")) StopLessons();
        }

        private IEnumerator FinishSculpt(bool keep)
        {
            var s = sculpt;
            if (s == null)
            {
                // (paused for a part change, or only handles: the changes are already in the design)
                if (!sculptMode) yield break;
                EndSculptMode();
                CloseQuestion("sculpt done");
                if (keep) { Remember(); Say(Crew.Maker, "Nice shaping!"); }
                if (FormFirst && Review == ReviewStage.Shape && Product != null) ShowShapeChoices();
                yield break;
            }
            sculpt = null; // no more touches
            sculptMode = sculptPaused = false;
            HideSculptTools(); // F84: the fingertip tools and the brush dots go
            if (CurrentLesson != null && CurrentLesson.name.StartsWith("sculpt")) StopLessons();
            // the last touches are meshed first
            for (float until = Time.realtimeSinceStartup + 4f; keep && Time.realtimeSinceStartup < until; )
            {
                bool idle;
                lock (s.gate) idle = !s.busy && s.pending.Count == 0 && !s.replay;
                if (idle) break;
                yield return null;
            }
            lock (s.gate) s.running = false;
            CloseQuestion("sculpt done");
            if (s.target == null) yield break;
            var paint = s.target.GetComponentInChildren<PaintableThing>();
            var filter = paint != null ? paint.GetComponent<MeshFilter>() : null;
            if (!keep || s.dabs.Count == 0 && (s.design.ops == null || !s.design.ops.Any(o => o.kind == "dab")))
            {
                // nothing changed: the old view comes back
                if (filter != null) filter.sharedMesh = s.savedMesh;
                if (paint != null && paint.Renderer != null) paint.Renderer.sharedMaterials = s.savedMaterials;
                if (FormFirst && Review == ReviewStage.Shape && s.target == Product) ShowShapeChoices();
                yield break;
            }
            var design = s.design.Copy();
            var ops = new List<SolidRebuild.Op>();
            if (design.ops != null) ops.AddRange(design.ops.Where(o => o != null && o.kind != "dab"));
            SolidRebuild.Result result;
            lock (s.gate) { ops.AddRange(s.dabs.Select(o => o.Copy())); result = s.ready ?? s.last; }
            design.ops = ops.ToArray();
            if (result != null && result.vertices.Length > 0)
            {
                if (result.colours == null) result.colours = ColoursFor(s, result);
                yield return SwapView(s.target, design, result, true);
            }
            long h = HitchStart();
            if (s.target == Product) Design = design;
            UpdateVersionDesign(s.target, design);
            Remember();
            Note("design", "Shaped by hand: " + design.name, s.dabs.Count + " touches.");
            HitchEnd("sculpt save", h);
            Talk.Add("Person finished sculpting by hand (" + s.dabs.Count + " touches)");
            Say(Crew.Maker, "Nice shaping!");
            if (FormFirst && Review == ReviewStage.Shape && s.target == Product) ShowShapeChoices();
            // (Fixer's print check is not re-run here: on a rebuilt view it costs a frame; "print check" or printing runs it)
        }

        private void UndoSculptStroke()
        {
            var s = sculpt;
            if (s == null) return;
            lock (s.gate)
            {
                if (s.dabs.Count == 0) { Caption(Crew.Maker, "Nothing to undo.", 1.5f); return; }
                int last = s.dabs[s.dabs.Count - 1].stroke;
                s.dabs.RemoveAll(d => d.stroke == last);
                s.pending.Clear();
                s.replay = true;
            }
            ToySounds.Play(ToySounds.Cue.Whoosh, s.target.position, .3f, 1.3f);
        }

        // ------------------------------------------------------------ the worker (off the main thread)

        private void SculptWorker(SculptState s)
        {
            try
            {
                while (true)
                {
                    List<SolidRebuild.Op> todo = null;
                    bool replay;
                    lock (s.gate)
                    {
                        if (!s.running) return;
                        replay = s.replay;
                        s.replay = false;
                        if (replay) todo = new List<SolidRebuild.Op>(s.dabs);
                        else if (s.pending.Count > 0) { todo = new List<SolidRebuild.Op>(s.pending); s.pending.Clear(); }
                        s.busy = todo != null;
                    }
                    if (todo == null) { Thread.Sleep(12); continue; }
                    var field = s.field;
                    if (replay)
                    {
                        field = s.baseField.Clone();
                        SolidRebuild.Apply(field, todo, s.min, s.size);
                    }
                    else SolidRebuild.Apply(field, todo, s.min, s.size);
                    var r = SolidRebuild.Mesh(field, 1, false);
                    SolidRebuild.Taubin(r, 2);
                    r.colours = ColoursFor(s, r);
                    lock (s.gate) { s.field = field; s.ready = r; s.applied += todo.Count; s.busy = false; }
                }
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Sculpt] worker: " + exception.Message); }
        }

        // New clay takes the colour of the nearest surface it grew from (a coarse lookup made once).
        private static void BuildColourLookup(SculptState s, SolidRebuild.Result r)
        {
            s.colourCell = s.field.cell * 2f;
            s.colours = new Dictionary<long, Color32>(r.vertices.Length, SolidRebuild.LongKeys.Instance);
            for (int i = 0; i < r.vertices.Length; i++)
            {
                long key = ColourKey(r.vertices[i], s.colourCell);
                if (!s.colours.ContainsKey(key)) s.colours[key] = r.colours != null ? r.colours[i] : s.fallback;
            }
        }

        private static long ColourKey(Vector3 p, float cell) =>
            SolidRebuild.PackKey(new Vector3(Mathf.Floor(p.x / cell) + 0x80000, Mathf.Floor(p.y / cell) + 0x80000, Mathf.Floor(p.z / cell) + 0x80000));

        private static Color32[] ColoursFor(SculptState s, SolidRebuild.Result r)
        {
            var c = new Color32[r.vertices.Length];
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = s.fallback;
                Vector3 p = r.vertices[i];
                bool found = false;
                for (int ring = 0; ring <= 2 && !found; ring++)
                    for (int dz = -ring; dz <= ring && !found; dz++)
                        for (int dy = -ring; dy <= ring && !found; dy++)
                            for (int dx = -ring; dx <= ring && !found; dx++)
                                if (s.colours.TryGetValue(ColourKey(p + new Vector3(dx, dy, dz) * s.colourCell, s.colourCell), out var col)) { c[i] = col; found = true; }
            }
            return c;
        }

        // ------------------------------------------------------------ every frame

        private void SculptFrame()
        {
            var s = sculpt;
            if (s == null) return;
            if (s.target == null || s.target != Product || Current != Step.Done) { StartCoroutine(FinishSculpt(false)); return; }
            SolidRebuild.Result ready = null;
            lock (s.gate) { if (s.ready != null && s.ready != s.last) { ready = s.ready; s.last = ready; } }
            if (ready != null) ShowSculptMesh(s, ready);
            long h = HitchStart();
            var paint = s.target.GetComponentInChildren<PaintableThing>();
            Transform frame = paint != null ? paint.transform : s.target;
            Hand right = HandSet.Side(Handedness.Right), left = HandSet.Side(Handedness.Left);
            bool rightOn = right != null && right.isActiveAndEnabled && HandIO.Connected(right);
            bool leftOn = left != null && left.isActiveAndEnabled && HandIO.Connected(left);
            float spacing = DabSpacingMm * .001f;

            // pull: a pinch that started on the surface (SculptPinch) drags clay out
            if (s.pullHand != null)
            {
                if (!HandIO.Pinching(s.pullHand) || !TryPinchPoint(s.pullHand, out var pinch)) s.pullHand = null;
                else
                {
                    Vector3 local = frame.InverseTransformPoint(pinch);
                    if ((local - s.lastPull).magnitude >= spacing) { AddDab(s, "pull", local, s.PullMm, 0f); s.lastPull = local; }
                }
            }
            // push: the right fingertip inside the clay (not pinching)
            if (rightOn && s.pullHand != right && !HandIO.Pinching(right) && HandIO.Joint(right, HandJointId.HandIndexTip, out var tip))
            {
                Vector3 local = frame.InverseTransformPoint(tip.position);
                float d = s.field.Sample(local);
                if (d < -.001f)
                {
                    if (!s.pushing) { s.pushing = true; s.stroke++; s.lastPush = local + Vector3.one; }
                    if ((local - s.lastPush).magnitude >= spacing) { AddDab(s, "push", local, s.PushMm, 0f, false); s.lastPush = local; }
                }
                else if (d > .004f) s.pushing = false;
            }
            else s.pushing = false;
            // smooth: the left fingertip rubbing the surface
            if (leftOn && s.pullHand != left && !HandIO.Pinching(left) && HandIO.Joint(left, HandJointId.HandIndexTip, out var rub))
            {
                Vector3 local = frame.InverseTransformPoint(rub.position);
                float d = s.field.Sample(local);
                if (d < .003f)
                {
                    if (!s.smoothing) { s.smoothing = true; s.stroke++; s.lastSmooth = local + Vector3.one; }
                    if ((local - s.lastSmooth).magnitude >= spacing) { AddDab(s, "smooth", local, s.SmoothMm, .5f, false); s.lastSmooth = local; }
                }
                else if (d > .008f) s.smoothing = false;
            }
            else s.smoothing = false;
            SculptToolsFrame(s, frame, rightOn ? right : null, leftOn ? left : null); // F84: fingertip tools and brush dots
            HitchEnd("sculpt hands", h);
        }

        // From OnPinch: a pinch on (or just over) the clay starts a pull; the model is never grabbed while sculpting.
        public static bool SculptPinchPulls; // (the older checks: a pinch on the clay pulls it out)
        private bool SculptPinch(Hand hand, Vector3 point)
        {
            var s = sculpt;
            if (s == null || s.target == null) return false;
            var paint = s.target.GetComponentInChildren<PaintableThing>();
            Transform frame = paint != null ? paint.transform : s.target;
            Vector3 local = frame.InverseTransformPoint(point);
            float d = s.field.Sample(local);
            if (Mathf.Abs(d) > PullStartMm * .001f)
                return MadeNear(point) == s.target; // a pinch near but not on it: nothing (no grab while sculpting)
            if (hand == null || !SculptPinchPulls) return true; // 2026-11-15: a pinch on the clay only takes a part's handle
            s.pullHand = hand;
            s.stroke++;
            s.lastPull = local + Vector3.one;
            return true;
        }

        private void AddDab(SculptState s, string tool, Vector3 local, float radiusMm, float strength, bool newStroke = false)
        {
            if (newStroke) s.stroke++;
            sculptStrokeAt = Time.time; // (Undo takes back the newer of a touch and a part change)
            var dab = new SolidRebuild.Op { kind = "dab", side = tool, a = radiusMm, b = strength, at = SolidRebuild.Op.Rel(local, s.min, s.size), stroke = s.stroke };
            lock (s.gate) { s.dabs.Add(dab); s.pending.Add(dab); }
            if (s.countedStroke != s.stroke)
            {
                // F84: strokes per tool (the lesson waits for one of each).
                s.countedStroke = s.stroke;
                if (tool == "push") SculptPushStrokes++; else if (tool == "pull") SculptPullStrokes++; else SculptSmoothStrokes++;
            }
            if (Time.time > s.soundAt)
            {
                s.soundAt = Time.time + .12f;
                ToySounds.Play(tool == "smooth" ? ToySounds.Cue.Brush : ToySounds.Cue.Press, s.target.TransformPoint(local), .18f, tool == "pull" ? .8f : 1.1f);
            }
        }

        private void ShowSculptMesh(SculptState s, SolidRebuild.Result r)
        {
            if (s.mesh == null || r == null) return;
            long h = HitchStart();
            s.mesh.Clear();
            s.mesh.SetVertices(r.vertices);
            s.mesh.SetNormals(r.normals);
            if (r.colours != null && r.colours.Length == r.vertices.Length) s.mesh.SetColors(r.colours);
            s.mesh.SetTriangles(r.triangles, 0, false);
            s.mesh.RecalculateBounds();
            SculptMeshes++;
            HitchEnd("sculpt mesh", h);
        }

        // ------------------------------------------------------------ words

        private bool SculptVoice(string p)
        {
            if (SculptMode)
            {
                if (BrushSizeVoice(p)) return true; // F84: "bigger" / "smaller" brush
                if (p == "done sculpting" || IsYes(p) || p.Contains("done") || p.Contains("finished") || p.Contains("that's it") || p.Contains("다 했") || p.Contains("끝"))
                { StartCoroutine(FinishSculpt(true)); return true; }
                if (p == "undo stroke" || p.Contains("undo") || p.Contains("take that back") || p.Contains("되돌려") || p.Contains("취소"))
                {
                    UndoSculptStroke();
                    // F84: the Undo pill closed the question; Done | Undo stay (a lesson still running keeps its card instead).
                    if (Sculpting && CurrentLesson == null && (Question == null || Question.answered)) AskSculptDone(SculptUndoneLine);
                    return true;
                }
                if (p.Contains("cancel") || p.Contains("never mind") || p.Contains("forget it") || p.Contains("그만")) { bool clay = Sculpting; StartCoroutine(FinishSculpt(false)); Say(Crew.Maker, clay ? "Back as it was." : "Okay, done shaping."); return true; }
                return false;
            }
            if (Product == null || Current != Step.Done || Design == null || Design.kind != "model") return false;
            if (!(p.Contains("sculpt") || p.Contains("let me shape") || p.Contains("shape it myself") || p.Contains("shape it by hand") || p.Contains("with my hands") ||
                  p.Contains("like clay") || p.Contains("조각") || p.Contains("손으로 만") || p.Contains("주물러") || p.Contains("빚어"))) return false;
            if (!SculptChoice)
            {
                // F93: no Sculpt it. In the shape review the wish to reshape it goes to Change shape (the see-through
                // next version and the flags, "Tell me what to change..."); elsewhere the words go on down the router.
                if (!FormFirst || Review != ReviewStage.Shape || changingShape) return false;
                Heard();
                EnterChangeMode();
                return true;
            }
            Heard();
            StartCoroutine(StartSculpt());
            return true;
        }

        // F57/F59 words, first in OnVoice (before the review's own "done"): sculpting, then cuts.
        private bool SolidVoice(string text, string p) => SculptVoice(p) || FeatureVoice(text, p);

        // From OnPinch, before a made thing would be grabbed.
        private bool SolidPinch(Hand hand, Vector3 point, Ray? ray) => SculptPinch(hand, point) || FeaturePinch(hand, point, ray);

        // Grab: never the clay being sculpted.
        private bool SolidHolds(Transform thing) => (sculpt != null && thing == sculpt.target) || (sculptMode && thing != null && thing == Product);

        // ------------------------------------------------------------ tests

        public void SimulateSculpt() => StartCoroutine(StartSculpt());
        public void SimulateSculptDab(string tool, Vector3 world, float radiusMm = 0f)
        {
            var s = sculpt;
            if (s == null) return;
            var paint = s.target.GetComponentInChildren<PaintableThing>();
            Vector3 local = (paint != null ? paint.transform : s.target).InverseTransformPoint(world);
            AddDab(s, tool, local, radiusMm > 0f ? radiusMm : tool == "pull" ? s.PullMm : tool == "smooth" ? s.SmoothMm : s.PushMm, tool == "smooth" ? .5f : 0f, true);
        }
        public void SimulateUndoStroke() => UndoSculptStroke();
        public void SimulateFinishSculpt(bool keep = true) => StartCoroutine(FinishSculpt(keep));
        public float SculptSample(Vector3 world)
        {
            var s = sculpt;
            if (s == null) return float.NaN;
            var paint = s.target.GetComponentInChildren<PaintableThing>();
            return s.field.Sample((paint != null ? paint.transform : s.target).InverseTransformPoint(world));
        }
        public bool SculptIdle { get { var s = sculpt; if (s == null) return true; lock (s.gate) return s.pending.Count == 0 && !s.replay; } }
    }
}
