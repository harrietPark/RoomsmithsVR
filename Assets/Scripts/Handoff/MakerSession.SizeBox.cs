using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F75 (2026-11-14, the user: "a simple virtual cube while measuring length, width and height would be more intuitive;
    // the sizes stay, I can adjust them when I'm done, and only 'done' makes it; Maker comes to the cube to make it, the
    // plate appears where the cube is"). The size plan (F61) builds a see-through box on the desk:
    //  - line -> plane -> box: the first size, kept, drops from the hands onto the desk as a line (under the hands when they
    //    measured over a desk, else beside the thing it is for); each next size grows it while the hands pull the tape;
    //    sizes not asked are "auto" (dashed edges): the made thing keeps the shape's proportions there.
    //  - the sizes stay on three blueprint lines (F39) facing the person; when all are given, each line has a knob: pull it
    //    to change that side (an auto side becomes exact), say "2 cm taller" / "150 wide", or hold the two-hand tape along a
    //    side; the box is picked up near (or by an aimed pinch) and put anywhere: it settles onto the surface below.
    //  - nothing is made until Done (the pill, "done", a thumbs up): then the wooden stand grows under the box (F52's stand,
    //    no "Place here" step), the box rises onto it, Maker comes and carves there, and the box (outline and sizes) stays
    //    until the make is revealed. Where Maker cannot stand beside it, Maker works nearby and the thing hops onto the stand.
    // Only for Meshy models in the form-first flow; scripted runs keep the F61 flow unless SizeBoxWaitsWhenScripted
    // (checks of older features), and SizeBoxOn = false turns it off.
    public sealed partial class MakerSession
    {
        public static bool SizeBoxOn = true;
        public static bool SizeBoxWaitsWhenScripted = false;
        public const float BoxKnobReach = .025f, BoxGrabReach = .03f, BoxMinMm = 5f, BoxMaxMm = 600f, BoxRemindAfter = 30f;
        public const int BoxRemindMost = 2;

        public enum BoxPhase { None, Sizing, Ready, Making }
        public BoxPhase SizeBoxPhase { get; private set; }
        public Transform SizeBoxView => sizeBox;
        public Vector3 SizeBoxMm => boxMm;                  // x W, y H, z D (millimetres)
        public bool SizeBoxExact(int axis) => boxExact[axis];
        public int SizeBoxKnobDrags { get; private set; }
        public int SizeBoxMoves { get; private set; }
        public bool SizeBoxMovedByPerson => boxMovedByPerson;
        public string SizeBoxLabel(int axis) => boxDims[axis] != null && boxDims[axis].text != null && boxDims[axis].root.gameObject.activeInHierarchy ? boxDims[axis].text.text : "";
        public Transform SizeBoxKnob(int axis) => boxDims[axis] != null && boxDims[axis].knob != null && boxDims[axis].knob.gameObject.activeInHierarchy ? boxDims[axis].knob : null;

        private Transform sizeBox, boxFaces, boxHull;
        private BoxCollider boxCollider;
        private Material boxMaterial;
        private Mesh boxCube, boxRod;
        private readonly List<Transform> boxRods = new List<Transform>();
        private readonly DimView[] boxDims = new DimView[3];
        private Vector3 boxMm, boxShown;                    // the sizes, and the sizes as drawn (they glide)
        private readonly bool[] boxExact = new bool[3], boxAsked = new bool[3];
        private int boxPreviewAxis = -1, boxPendingAxis = -1, boxKnobAxis = -1, boxSizingAxis = -1;
        private float boxPreviewMm, boxKnobTickMm, boxReadyAt, boxRemindAt;
        private int boxReminders;
        private bool boxPending, boxMovedByPerson, boxKnobMouse, boxFading;
        private Hand boxKnobHand, boxFarHand;
        private float boxFarSince;
        private Vector3 lastBoxKey;
        private readonly Dictionary<Hand, bool> boxWasPinching = new Dictionary<Hand, bool>();

        private static readonly string[] AxisLetter = { "W", "H", "D" };
        private static int AxisIndex(string axis) => axis == "H" ? 1 : axis == "D" ? 2 : 0;

        // The box is used for this make (a Meshy model in the form-first flow).
        private bool BoxMode => SizeBoxOn && FormFirst && Kind == "model" && (!Scripted || SizeBoxWaitsWhenScripted);

        // ------------------------------------------------------------ the size plan's steps

        // StartSizePlan: a fresh box (shown once the first size is kept).
        private void BeginSizeBox()
        {
            ClearSizeBox();
            if (!BoxMode) return;
            SizeBoxPhase = BoxPhase.Sizing;
            boxMm = boxShown = Vector3.zero;
            for (int i = 0; i < 3; i++) { boxExact[i] = false; boxAsked[i] = false; }
            foreach (var s in SizePlan) boxAsked[AxisIndex(s.axis)] = true;
            boxMovedByPerson = false;
            boxReminders = 0;
        }

        // NextSize: a size of the plan was kept (spoken or by the hands). The first one puts the box down.
        private void BoxTakeSize(string axis, float mm)
        {
            if (SizeBoxPhase != BoxPhase.Sizing) return;
            int i = AxisIndex(axis);
            boxMm[i] = Mathf.Clamp(mm, BoxMinMm, BoxMaxMm);
            boxExact[i] = true;
            boxPreviewAxis = boxSizingAxis = -1;
            if (sizeBox == null) PutBoxDown((Tape.A + Tape.B) * .5f, Tape.Length > .005f);
            Tape.Reset();
            ShowTape(false);
            ToySounds.Play(ToySounds.Cue.Pop, sizeBox.position, .4f, 1.5f);
        }

        // After the last size of the plan (or a skipped last one): the box waits for Done. False: the old flow goes on.
        private bool BoxReadyNow()
        {
            if (SizeBoxPhase != BoxPhase.Sizing) return false;
            if (sizeBox == null) { SizeBoxPhase = BoxPhase.None; return false; } // (no size was kept: as before)
            SizeBoxPhase = BoxPhase.Ready;
            boxReadyAt = boxRemindAt = Time.time;
            HasMeasure = true;
            boxPending = false;
            StopSettle();
            Tape.Reset();
            ShowTape(false);
            FillAutoSizes();
            RefreshBox(true);
            // Grown by the plan's last sizes, it may now cover something: the app's box slides to a clear spot (one the
            // person moved stays where they put it).
            if (!boxMovedByPerson && !PlacementClear(sizeBox.position, BoxRadius()))
            {
                Vector3 clear = FindPlacement(sizeBox.position, BoxRadius());
                if (Vector3.Distance(clear, sizeBox.position) > .01f) StartCoroutine(SlideBox(clear));
            }
            CloseQuestion("size box ready");
            AskWith(Crew.Fixer, "That's its size. Move the box or pull a side, then tap Done.", false, Rp("Done", "done"));
            Talk.Add("Size box: " + BoxSizesText());
            WriteInputLog("size box ready: " + BoxSizesText(), true);
            return true;
        }

        // ConfirmMeasure while the box waits: a new length from the hands goes on its side; otherwise it is Done.
        private void BoxConfirm()
        {
            if (boxPending && boxPendingAxis >= 0)
            {
                BoxSetAxis(boxPendingAxis, Measured, true);
                boxPending = false;
                boxPendingAxis = -1;
                StopSettle();
                Tape.Reset();
                ShowTape(false);
                return;
            }
            FinishSizeBox();
        }

        // SetMeasure while the box waits: the hands measured a side (the one the tape lies along).
        private void BoxPendingFromTape()
        {
            // While the plan asks: this length is for the side being asked (shown on the box until it is kept).
            if (SizeBoxPhase == BoxPhase.Sizing) { boxSizingAxis = CurrentSize != null ? AxisIndex(CurrentSize.axis) : -1; return; }
            if (SizeBoxPhase != BoxPhase.Ready || sizeBox == null) return;
            boxPendingAxis = BoxAxisAlong(Tape.B - Tape.A);
            boxPending = true;
        }

        // Done: the stand grows under the box and the make starts there.
        private void FinishSizeBox()
        {
            if (SizeBoxPhase != BoxPhase.Ready || sizeBox == null) return;
            if (held == sizeBox) LetGo();
            EndBoxKnob();
            Hush(); // (before the stand: its "I'll make it over here" must not be dropped)
            float modelRadius = BoxRadius();
            placementRadius = Mathf.Clamp(modelRadius, .03f, .6f);
            standStrict = false;
            Quaternion turn = Upright(sizeBox.rotation);
            // (out of MadeThings first: the box itself must not count as what fills the stand's spot)
            SizeBoxPhase = BoxPhase.Making;
            if (boxCollider != null) boxCollider.enabled = false;
            var stand = MakeStand(sizeBox.position, turn, modelRadius);
            placementView = stand; // (as F52's placement: the stand being placed is the work place, not in its own way)
            bool approved = ApproveStand(stand);
            placementView = null;
            if (!approved) // (it says why: no clear desk to work on)
            {
                SizeBoxPhase = BoxPhase.Ready;
                if (boxCollider != null) boxCollider.enabled = true;
                DestroyStand(stand);
                WriteInputLog("size box done: no place to work", true);
                return;
            }
            placementApproved = true;
            MugSpot = approvedPlace;
            MugTurn = approvedTurn;
            HasSpot = true;
            // The plan's sizes become the box's: exact sides are given, auto sides follow the shape.
            for (int i = 0; i < 3; i++)
            {
                string axis = AxisLetter[i];
                var step = SizePlan.Find(s => s.axis == axis);
                if (boxExact[i])
                {
                    if (step == null) SizePlan.Add(step = new SizeStep { axis = axis, label = AxisQuestion[axis], why = "", must = false });
                    step.mm = Mathf.Round(boxMm[i]);
                    step.skipped = false;
                }
                else if (step != null) { step.mm = -1f; step.skipped = true; }
            }
            if (SizePlan.Count > 0 && SizePlan[0].mm > 0f) Measured = SizePlan[0].mm;
            else Measured = Mathf.Round(boxExact[1] && !boxExact[0] ? boxMm[1] : boxMm[0]);
            HasMeasure = true;
            if (boxFaces != null) boxFaces.gameObject.SetActive(false); // the outline and the sizes stay while it is made
            StartCoroutine(LiftBox(StandTopOf(stand)));
            CloseQuestion("size box done");
            Talk.Add("Person approved the size box: " + BoxSizesText());
            Note("measure", "Size" + (TargetName != null ? " for your " + TargetName : ""), BoxSizesText());
            WriteInputLog("size box done: " + BoxSizesText() + (MakerWorksOnStand ? " | Maker works on the stand" : " | Maker works beside it"), true);
            StopSettle();
            measureConfirmedAt = Time.time;
            Heard();
            ClearTags();
            RefreshBox(true);
            AfterMeasure();
        }

        private string BoxSizesText()
        {
            string Side(int i) => AxisLetter[i] + " " + (boxExact[i] ? boxMm[i].ToString("0") + " mm" : "auto");
            return Side(0) + " x " + Side(2) + " x " + Side(1);
        }

        // ------------------------------------------------------------ the box itself

        private float BoxRadius() => Mathf.Max(.015f, Mathf.Max(boxMm.x, boxMm.z) * .0005f);

        // Where the box goes: under the hands when they measured over a desk, else the model's spot (beside the thing it
        // is for, PlaceModelSpot); then the nearest clear desk spot (F50/F51).
        private void PutBoxDown(Vector3 hands, bool fromHands)
        {
            Vector3 want = HasSpot ? MugSpot : DeskPointInFront();
            if (fromHands && DeskTop(hands, out float y) && hands.y - y > -.02f && hands.y - y < .45f) want = new Vector3(hands.x, y, hands.z);
            Vector3 spot = FindPlacement(want, BoxRadius());
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - spot, Vector3.up) : Vector3.back;
            Quaternion turn = Quaternion.LookRotation(toUser.sqrMagnitude > 1e-4f ? toUser.normalized : Vector3.back, Vector3.up);
            BuildBox();
            sizeBox.SetPositionAndRotation(fromHands ? hands : spot + Vector3.up * .05f, turn);
            boxShown = boxMm;
            RefreshBox(true);
            StartCoroutine(DropBox(spot));
            WriteInputLog("size box down at " + spot.ToString("F2") + (fromHands ? " (from the hands)" : ""), true);
        }

        private void BuildBox()
        {
            if (sizeBox != null) return;
            sizeBox = new GameObject("Size Box").transform;
            sizeBox.SetParent(transform, false);
            var body = sizeBox.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            boxCube ??= Own(BoxCubeMesh());
            boxFaces = Part("Box Faces", boxCube);
            boxFaces.SetParent(sizeBox, false);
            if (boxMaterial == null)
            {
                var asset = Resources.Load<Material>("MakerHologram");
                boxMaterial = new Material(asset != null ? asset : ghostMaterial != null ? ghostMaterial : material) { name = "Size Box" };
                owned.Add(boxMaterial);
            }
            var faces = boxFaces.GetComponent<MeshRenderer>();
            faces.sharedMaterial = boxMaterial;
            faces.receiveShadows = false;
            boxHull = new GameObject("Box Hull").transform;
            boxHull.SetParent(sizeBox, false);
            boxCollider = boxHull.gameObject.AddComponent<BoxCollider>();
            boxRod ??= Own(ToyMeshes.RoundedBox(new Vector3(1f, .0022f, .0022f), .0009f, Blueprint, Blueprint, Blueprint, Blueprint, "BoxEdge", 2));
            for (int i = 0; i < 3; i++) boxDims[i] ??= BuildDim("Box " + AxisLetter[i], true);
        }

        // A unit cube standing on its base centre (vertex colour white; the hologram tints it).
        private static Mesh BoxCubeMesh()
        {
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var t = new List<int>();
            Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var d in dirs)
            {
                Vector3 a = d.x != 0f ? Vector3.up : Vector3.right, b = Vector3.Cross(d, a);
                int k = v.Count;
                foreach (var (s, u) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    v.Add(new Vector3(0f, .5f, 0f) + (d + a * s + b * u) * .5f);
                    n.Add(d);
                }
                t.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
                // (both windings: seen from inside too, when the person reaches in)
                t.AddRange(new[] { k, k + 2, k + 1, k, k + 3, k + 2 });
            }
            var mesh = new Mesh { name = "SizeBoxCube" };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetTriangles(t, 0);
            var colours = new Color[v.Count];
            for (int i = 0; i < colours.Length; i++) colours[i] = Color.white;
            mesh.colors = colours;
            mesh.RecalculateBounds();
            return mesh;
        }

        // The faces, the twelve edges (dashed along auto sides) and the hull, from the sizes as drawn.
        private void RefreshBox(bool force = false)
        {
            if (sizeBox == null) return;
            Vector3 key = boxShown + new Vector3(boxExact[0] ? 0f : .1f, boxExact[1] ? 0f : .2f, boxExact[2] ? 0f : .4f);
            if (!force && (key - lastBoxKey).sqrMagnitude < 1e-6f) return;
            lastBoxKey = key;
            Vector3 size = boxShown * .001f;
            if (boxFaces != null)
            {
                // (kept on as a thin sliver for a line: its bounds are the box's for grabbing and the F51 map)
                boxFaces.gameObject.SetActive(SizeBoxPhase != BoxPhase.Making);
                boxFaces.localScale = new Vector3(Mathf.Max(.0006f, size.x), Mathf.Max(.0006f, size.y), Mathf.Max(.0006f, size.z));
            }
            if (boxHull != null && boxCollider != null)
            {
                boxCollider.center = new Vector3(0f, Mathf.Max(.01f, size.y) * .5f, 0f);
                boxCollider.size = new Vector3(Mathf.Max(.01f, size.x), Mathf.Max(.01f, size.y), Mathf.Max(.01f, size.z));
            }
            int used = 0;
            float hx = size.x * .5f, hz = size.z * .5f, h = size.y;
            for (int axis = 0; axis < 3; axis++)
            {
                float length = size[axis];
                if (length < .001f) continue;
                bool dashed = !boxExact[axis] && SizeBoxPhase != BoxPhase.Sizing;
                for (int e = 0; e < 4; e++)
                {
                    float s1 = (e & 1) == 0 ? -1f : 1f, s2 = (e & 2) == 0 ? -1f : 1f;
                    Vector3 a, dir;
                    if (axis == 0) { a = new Vector3(-hx, s1 > 0f ? h : 0f, s2 * hz); dir = Vector3.right; }
                    else if (axis == 1) { a = new Vector3(s1 * hx, 0f, s2 * hz); dir = Vector3.up; }
                    else { a = new Vector3(s1 * hx, s2 > 0f ? h : 0f, -hz); dir = Vector3.forward; }
                    if (!dashed) { Rod(used++, a, dir, length); continue; }
                    for (float at = 0f; at < length - .001f; at += .009f) Rod(used++, a + dir * at, dir, Mathf.Min(.005f, length - at));
                }
            }
            for (int i = used; i < boxRods.Count; i++) if (boxRods[i].gameObject.activeSelf) boxRods[i].gameObject.SetActive(false);

            void Rod(int index, Vector3 from, Vector3 dir, float length)
            {
                while (boxRods.Count <= index) { var r = Part("Box Edge", boxRod); r.SetParent(sizeBox, false); boxRods.Add(r); }
                var rod = boxRods[index];
                if (!rod.gameObject.activeSelf) rod.gameObject.SetActive(true);
                rod.localPosition = from + dir * (length * .5f);
                rod.localRotation = Quaternion.FromToRotation(Vector3.right, dir);
                rod.localScale = new Vector3(length, 1f, 1f);
            }
        }

        // The line of a side (0 W, 1 H, 2 D) along the edge that faces the person best, a little out from it (the knob
        // at the top for H, the person's right for W, the far end for D), as MakerSession.DraftDims.cs does for v2.
        private bool BoxLine(int size, out Vector3 a, out Vector3 b)
        {
            a = b = Vector3.zero;
            if (sizeBox == null) return false;
            Vector3 s = boxShown * .001f;
            if (s[size] < .001f) return false;
            Vector3 centre = sizeBox.TransformPoint(new Vector3(0f, s.y * .5f, 0f));
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - centre, Vector3.up).normalized : Vector3.back;
            Vector3 right = Vector3.Cross(Vector3.up, -toUser);
            int o1 = (size + 1) % 3, o2 = (size + 2) % 3;
            Vector3 min = new Vector3(-s.x * .5f, 0f, -s.z * .5f), max = new Vector3(s.x * .5f, s.y, s.z * .5f);
            float best = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p0 = min, p1 = min;
                p0[o1] = p1[o1] = (i & 1) == 0 ? min[o1] : max[o1];
                p0[o2] = p1[o2] = (i & 2) == 0 ? min[o2] : max[o2];
                p0[size] = min[size];
                p1[size] = max[size];
                Vector3 w0 = sizeBox.TransformPoint(p0), w1 = sizeBox.TransformPoint(p1), mid = (w0 + w1) * .5f - centre;
                float score = size == 1 ? Vector3.Dot(mid, toUser) + Vector3.Dot(mid, right)
                            : size == 0 ? 2f * mid.y + Vector3.Dot(mid, toUser)
                            : -2f * mid.y + Vector3.Dot(mid, right);
                if (score <= best) continue;
                best = score;
                Vector3 outward = Vector3.ProjectOnPlane(mid, w1 - w0);
                outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : Vector3.up;
                a = w0 + outward * .014f;
                b = w1 + outward * .014f;
            }
            Vector3 d = b - a;
            bool flip = size == 1 ? d.y < 0f : size == 0 ? Vector3.Dot(d, right) < 0f : Vector3.Dot(d, toUser) > 0f;
            if (flip) { var tmp = a; a = b; b = tmp; }
            return true;
        }

        // Every frame (a frame step): the sizes glide to their values, the live tape grows its side, the lines face the
        // person, the reminders, and the box goes once the make is shown.
        private void SizeBoxFrame()
        {
            if (SizeBoxPhase == BoxPhase.None) return;
            if (SizeBoxPhase == BoxPhase.Making)
            {
                if (Current != Step.Planning && Current != Step.Making && !boxFading) StartCoroutine(FadeBox());
            }
            else if (Current != Step.Measuring) { ClearSizeBox(); return; }
            if (sizeBox == null) return;
            if (boxKnobHand != null && (!boxKnobHand.isActiveAndEnabled || !HandIO.Connected(boxKnobHand))) EndBoxKnob(); // (tracking lost)
            // The side the hands are pulling right now grows with the tape.
            Vector3 target = boxMm;
            int live = -1;
            if (SizeBoxPhase != BoxPhase.Making && Tape.State == HandTape.Mode.TwoHands && Tape.Length > .005f)
            {
                live = SizeBoxPhase == BoxPhase.Sizing ? (CurrentSize != null ? AxisIndex(CurrentSize.axis) : -1) : BoxAxisAlong(Tape.B - Tape.A);
                if (live >= 0) target[live] = Mathf.Clamp(Tape.Length * 1000f, BoxMinMm, BoxMaxMm);
            }
            else if (SizeBoxPhase == BoxPhase.Sizing && HasMeasure && boxSizingAxis >= 0 && CurrentSize != null && AxisIndex(CurrentSize.axis) == boxSizingAxis) { live = boxSizingAxis; target[live] = Measured; }
            else if (SizeBoxPhase == BoxPhase.Ready && boxPending && boxPendingAxis >= 0) { live = boxPendingAxis; target[live] = Measured; }
            boxPreviewAxis = live;
            for (int i = 0; i < 3; i++)
                boxShown[i] = i == live || boxKnobAxis == i ? target[i] : Mathf.MoveTowards(boxShown[i], target[i], Mathf.Max(80f, Mathf.Abs(target[i] - boxShown[i]) * 10f) * Time.deltaTime);
            RefreshBox();
            for (int i = 0; i < 3; i++)
            {
                if (!BoxLine(i, out var a, out var b) || boxFading) { ShowDim(boxDims[i], false); continue; }
                ShowDim(boxDims[i], true);
                bool exact = boxExact[i] || i == live || SizeBoxPhase == BoxPhase.Sizing;
                string text = AxisLetter[i] + "  " + (exact ? boxShown[i].ToString("0") + " mm" : "auto");
                DrawDim(boxDims[i], a, b, text, SizeBoxPhase == BoxPhase.Ready ? 2 : 0);
            }
            if (SizeBoxPhase == BoxPhase.Ready) RemindBox();
        }

        private void RemindBox()
        {
            if (boxReminders >= BoxRemindMost || held == sizeBox || boxKnobAxis >= 0 || Tape.State == HandTape.Mode.TwoHands) return;
            float quiet = Time.time - Mathf.Max(boxRemindAt, lastHeardAt);
            if (quiet < BoxRemindAfter || lines.Count > 0 || Voice.IsSpeaking) return;
            boxReminders++;
            boxRemindAt = Time.time;
            Say(Crew.Fixer, "Pull a side to change it, or tap Done when the size looks right.");
        }

        // The sides not asked: what they would roughly be (the made thing follows the shape there).
        private void FillAutoSizes()
        {
            float w = boxExact[0] ? boxMm[0] : 0f, h = boxExact[1] ? boxMm[1] : 0f, d = boxExact[2] ? boxMm[2] : 0f;
            if (!boxExact[0]) w = d > 0f ? d : h > 0f ? h * .8f : 80f;
            if (!boxExact[2]) d = w;
            if (!boxExact[1])
            {
                var plan = SizePlan.Find(s => s.axis == "H");
                string n = (Need ?? "").ToLowerInvariant();
                bool flat = new[] { "coaster", "mat", "pad", "trivet", "plate", "tray", "lid", "코스터", "받침", "매트" }.AnyIn(n);
                h = plan != null && plan.typical > 0f ? plan.typical : flat ? Mathf.Max(6f, w * .06f) : w;
            }
            boxMm = new Vector3(Mathf.Clamp(w, BoxMinMm, BoxMaxMm), Mathf.Clamp(h, BoxMinMm, BoxMaxMm), Mathf.Clamp(d, BoxMinMm, BoxMaxMm));
        }

        // One side set (by a knob, words or the hands): exact from now on; the auto sides follow.
        private void BoxSetAxis(int axis, float mm, bool say)
        {
            if (sizeBox == null || axis < 0) return;
            boxMm[axis] = Mathf.Round(Mathf.Clamp(mm, BoxMinMm, BoxMaxMm));
            boxExact[axis] = true;
            if (SizeBoxPhase == BoxPhase.Ready) FillAutoSizes();
            Heard();
            boxRemindAt = Time.time;
            Talk.Add("Size box " + AxisLetter[axis] + ": " + boxMm[axis].ToString("0") + " mm");
            WriteInputLog("size box " + AxisLetter[axis] + " " + boxMm[axis].ToString("0") + " mm", true);
            if (say) Ack(Crew.Fixer, (axis == 0 ? "Width " : axis == 1 ? "Height " : "Depth ") + boxMm[axis].ToString("0") + " millimetres.");
        }

        // The box side a direction lies along (the box's own axes: it may have been turned).
        private int BoxAxisAlong(Vector3 dir)
        {
            if (sizeBox == null || dir.sqrMagnitude < 1e-8f) return 0;
            dir.Normalize();
            float w = Mathf.Abs(Vector3.Dot(dir, sizeBox.right)), h = Mathf.Abs(Vector3.Dot(dir, Vector3.up)), d = Mathf.Abs(Vector3.Dot(dir, sizeBox.forward));
            return h >= w && h >= d ? 1 : d > w ? 2 : 0;
        }

        // ------------------------------------------------------------ hands on the box (from UpdateTape, while measuring)

        // A new one-hand pinch on a knob pulls that side, on the box (near, or aimed from afar and held FarHold) picks it
        // up; that hand is then left out of the two-hand tape. Both hands pinching together is still the tape.
        private void SizeBoxHands(Hand left, Hand right, ref bool p0, ref bool p1, Vector3 a0, Vector3 a1)
        {
            if (sizeBox == null || (SizeBoxPhase != BoxPhase.Sizing && SizeBoxPhase != BoxPhase.Ready)) { boxWasPinching.Clear(); return; }
            bool l0 = p0, l1 = p1;
            BoxHand(left, ref p0, a0, l1);
            BoxHand(right, ref p1, a1, l0);
        }

        private void BoxHand(Hand hand, ref bool pinching, Vector3 point, bool otherPinching)
        {
            if (hand == null) return;
            boxWasPinching.TryGetValue(hand, out bool was);
            boxWasPinching[hand] = pinching;
            if (holdHand == hand && held == sizeBox) { pinching = false; return; }       // carrying it (Update's hand loop holds it)
            if (boxKnobHand == hand && boxKnobAxis >= 0) { BoxKnobFrame(hand, pinching, point); pinching = false; return; }
            if (boxFarHand == hand)
            {
                if (!pinching || otherPinching) { boxFarHand = null; return; }
                pinching = false;
                if (Time.time - boxFarSince < FarHold) return;
                boxFarHand = null;
                if (HandIO.Pointer(hand, out Pose pointer) && MadeOnRay(new Ray(pointer.position, pointer.forward), out float far) == sizeBox)
                {
                    Grab(hand, point, sizeBox, far);
                    BoxGrabbed(hand, "far");
                }
                return;
            }
            if (!pinching || was || otherPinching || Tape.State == HandTape.Mode.TwoHands || held != null) return;
            int knob = SizeBoxPhase == BoxPhase.Ready ? BoxKnobNear(point, null) : -1;
            if (knob >= 0) { StartBoxKnob(knob, hand); pinching = false; return; }
            if (BoxNear(point, BoxGrabReach))
            {
                Grab(hand, point, sizeBox);
                BoxGrabbed(hand, "near");
                pinching = false;
                return;
            }
            if (HandIO.Pointer(hand, out Pose aim) && MadeOnRay(new Ray(aim.position, aim.forward), out float distance) == sizeBox && distance > RemoteGrab.NearLimit)
            {
                boxFarHand = hand;
                boxFarSince = Time.time;
                pinching = false;
            }
        }

        private void BoxGrabbed(Hand hand, string how)
        {
            Heard();
            boxRemindAt = Time.time;
            StopSettle();
            WriteInputLog("size box taken (" + how + ") by " + (hand != null ? hand.Handedness.ToString() : "mouse"), true);
        }

        private bool BoxNear(Vector3 point, float reach)
        {
            if (sizeBox == null || float.IsInfinity(point.x)) return false;
            Vector3 local = sizeBox.InverseTransformPoint(point);
            Vector3 s = boxShown * .001f;
            return Mathf.Abs(local.x) <= s.x * .5f + reach && local.y >= -reach && local.y <= s.y + reach && Mathf.Abs(local.z) <= s.z * .5f + reach;
        }

        // LetGo: the box settles onto the surface below (a desk, a shelf, the floor); it is the person's place now.
        private void BoxLetGo()
        {
            if (sizeBox == null) return;
            boxMovedByPerson = true;
            SizeBoxMoves++;
            Heard();
            boxRemindAt = Time.time;
            Talk.Add("Person moved the size box");
            var room = arrival.handoff.meeting.environment;
            Vector3 from = sizeBox.position;
            if (room != null && Physics.Raycast(from + Vector3.up * .01f, Vector3.down, out var hit, 1.6f, room.GeometryMask, QueryTriggerInteraction.Ignore) && hit.normal.y > .8f)
                StartCoroutine(SlideBox(hit.point, .18f));
            else ToySounds.Play(ToySounds.Cue.Press, from, .3f, 1.45f);
            WriteInputLog("size box let go at " + from.ToString("F2"), true);
        }

        // ------------------------------------------------------------ the knobs

        private int BoxKnobNear(Vector3 point, Ray? ray)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var knob = SizeBoxKnob(i);
                if (knob == null) continue;
                float d = float.IsInfinity(point.x) ? float.MaxValue : Vector3.Distance(point, knob.position);
                if (d < BoxKnobReach && d < bestD) { bestD = d; best = i; }
                if (ray.HasValue)
                {
                    float r = RayDistance(ray.Value, knob.position);
                    if (r < .018f && r + .5f < bestD) { bestD = r + .5f; best = i; }
                }
            }
            return best;
        }

        private void StartBoxKnob(int axis, Hand hand)
        {
            boxKnobAxis = axis;
            boxKnobHand = hand;
            boxKnobTickMm = boxMm[axis];
            SizeBoxKnobDrags++;
            StopSettle();
            Heard();
            boxRemindAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Press, SizeBoxKnob(axis) != null ? SizeBoxKnob(axis).position : sizeBox.position, .35f, 1.8f);
            WriteInputLog("size box knob " + AxisLetter[axis] + " by " + (hand != null ? hand.Handedness.ToString() : "mouse"), true);
        }

        private void BoxKnobFrame(Hand hand, bool pinching, Vector3 point)
        {
            if (!pinching) { EndBoxKnob(); return; }
            var knob = SizeBoxKnob(boxKnobAxis);
            if (hand != null && knob != null && HandIO.Pointer(hand, out Pose pointer) && Vector3.Distance(point, knob.position) > .08f)
                DragBoxKnobTo(OnBoxLine(new Ray(pointer.position, pointer.forward)));
            else DragBoxKnobTo(point);
        }

        // The knob follows the hand along its line: H from the bottom, W and D both ways from the middle.
        private void DragBoxKnobTo(Vector3 point)
        {
            if (boxKnobAxis < 0 || !BoxLine(boxKnobAxis, out var a, out var b)) return;
            Vector3 dir = (b - a).normalized;
            float length = boxKnobAxis == 1 ? Vector3.Dot(point - a, dir) : 2f * Mathf.Abs(Vector3.Dot(point - (a + b) * .5f, dir));
            float mm = Mathf.Round(Mathf.Clamp(length * 1000f, BoxMinMm, BoxMaxMm));
            boxMm[boxKnobAxis] = mm;
            boxExact[boxKnobAxis] = true;
            boxShown[boxKnobAxis] = mm;
            if (Mathf.Abs(mm - boxKnobTickMm) >= 5f)
            {
                boxKnobTickMm = mm;
                ToySounds.Play(ToySounds.Cue.Press, point, .08f, 2.4f);
            }
        }

        private void EndBoxKnob()
        {
            if (boxKnobAxis < 0) return;
            int axis = boxKnobAxis;
            boxKnobAxis = -1;
            boxKnobHand = null;
            boxKnobMouse = false;
            BoxSetAxis(axis, boxMm[axis], true);
        }

        private Vector3 OnBoxLine(Ray ray)
        {
            if (boxKnobAxis < 0 || !BoxLine(boxKnobAxis, out var a, out var b)) return ray.origin;
            Vector3 u = (b - a).normalized, v = ray.direction, w = a - ray.origin;
            float uv = Vector3.Dot(u, v), denom = 1f - uv * uv;
            if (denom < 1e-6f) return a;
            float s = (uv * Vector3.Dot(v, w) - Vector3.Dot(u, w)) / denom;
            return a + u * s;
        }

        // The Editor mouse: drag a knob along its line.
        private bool EditorSizeBox(UnityEngine.InputSystem.Mouse mouse, Ray ray)
        {
            if (boxKnobMouse && boxKnobAxis >= 0)
            {
                if (mouse.leftButton.isPressed) DragBoxKnobTo(OnBoxLine(ray));
                else EndBoxKnob();
                return true;
            }
            if (!mouse.leftButton.wasPressedThisFrame || SizeBoxPhase != BoxPhase.Ready) return false;
            int axis = BoxKnobNear(Vector3.positiveInfinity, ray);
            if (axis < 0) return false;
            StartBoxKnob(axis, null);
            boxKnobMouse = true;
            return true;
        }

        // ------------------------------------------------------------ words

        private static readonly Regex BoxNumber = new Regex(@"(\d+(?:[.,]\d+)?)\s*(mm|millimet\w*|밀리|cm|centimet\w*|센티|미리)?");

        // While the box waits: Done, a side's size, a side made bigger or smaller, a side left to the shape ("auto").
        private bool SizeBoxVoice(string text, string p)
        {
            if (SizeBoxPhase != BoxPhase.Ready) return false;
            int axis = new[] { "wide", "width", "across", "폭", "가로", "넓" }.AnyIn(p) ? 0
                     : new[] { "tall", "high", "height", "높" }.AnyIn(p) ? 1
                     : new[] { "deep", "depth", "long", "length", "깊", "세로", "길이" }.AnyIn(p) ? 2 : -1;
            bool auto = new[] { "auto", "you decide", "you pick", "your call", "알아서", "자동" }.AnyIn(p);
            if (auto && axis >= 0)
            {
                boxExact[axis] = false;
                FillAutoSizes();
                Heard();
                Ack(Crew.Fixer, (axis == 0 ? "Width" : axis == 1 ? "Height" : "Depth") + " follows the shape, then.");
                return true;
            }
            var m = BoxNumber.Match(p);
            bool bigger = new[] { "taller", "wider", "deeper", "longer", "bigger", "higher", "더 크", "더 높", "더 넓", "늘려" }.AnyIn(p);
            bool smaller = new[] { "shorter", "narrower", "shallower", "smaller", "lower", "thinner", "더 작", "더 낮", "줄여" }.AnyIn(p);
            if (axis < 0 && (bigger || smaller))
                axis = new[] { "taller", "higher", "shorter", "lower", "높" }.AnyIn(p) ? 1 : new[] { "deeper", "shallower", "longer", "깊" }.AnyIn(p) ? 2 : 0;
            if (m.Success && (axis >= 0 || m.Groups[2].Success))
            {
                float value = float.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
                string unit = m.Groups[2].Value;
                float mm = unit.StartsWith("c") || unit.StartsWith("센") ? value * 10f : value;
                if (axis < 0) axis = AxisIndex(SizePlan.Count > 0 ? SizePlan[0].axis : "W");
                if (bigger) mm = boxMm[axis] + mm;
                else if (smaller) mm = boxMm[axis] - mm;
                BoxSetAxis(axis, mm, true);
                return true;
            }
            if (bigger || smaller)
            {
                BoxSetAxis(axis, boxMm[axis] * (bigger ? 1.15f : 1f / 1.15f), true);
                return true;
            }
            if (p.Contains("again") || p.Contains("다시"))
            {
                Heard();
                Ack(Crew.Fixer, "Hold the tape along any side, or pull its knob.");
                return true;
            }
            if (DoneWords(p) || IsCompletionCommand(p) || IsYes(p) || MakeNow(p) || p.Contains("looks good") || p.Contains("좋아") || p.Contains("만들"))
            {
                FinishSizeBox();
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ moving and going

        private IEnumerator DropBox(Vector3 onto)
        {
            Vector3 from = sizeBox.position;
            for (float t = 0f; t < 1f && sizeBox != null && held != sizeBox; t += Time.deltaTime / .4f)
            {
                float e = t * t;
                sizeBox.position = Vector3.Lerp(from, onto, e);
                yield return null;
            }
            if (sizeBox == null || held == sizeBox) yield break;
            sizeBox.position = onto;
            ToySounds.Play(ToySounds.Cue.Press, onto, .4f, 1.3f);
            ToyFx.Puff(onto + Vector3.up * .004f, .006f, 2);
        }

        private IEnumerator SlideBox(Vector3 onto, float seconds = .35f)
        {
            Vector3 from = sizeBox.position;
            for (float t = 0f; t < 1f && sizeBox != null && held != sizeBox; t += Time.deltaTime / seconds)
            {
                sizeBox.position = Vector3.Lerp(from, onto, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (sizeBox == null || held == sizeBox) yield break;
            sizeBox.position = onto;
            ToySounds.Play(ToySounds.Cue.Press, onto, .35f, 1.3f);
        }

        // Done: the stand grows under it and lifts it onto its plate.
        private IEnumerator LiftBox(Vector3 top)
        {
            if (sizeBox == null) yield break;
            Vector3 from = sizeBox.position;
            yield return new WaitForSeconds(.12f);
            for (float t = 0f; t < 1f && sizeBox != null; t += Time.deltaTime / .28f)
            {
                sizeBox.position = Vector3.Lerp(from, top, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            if (sizeBox != null) sizeBox.position = top;
        }

        // The make is shown: the outline and its sizes shrink away.
        private IEnumerator FadeBox()
        {
            boxFading = true;
            for (int i = 0; i < 3; i++) ShowDim(boxDims[i], false);
            Vector3 start = sizeBox != null ? sizeBox.localScale : Vector3.one;
            for (float t = 0f; t < 1f && sizeBox != null; t += Time.deltaTime / .35f)
            {
                sizeBox.localScale = new Vector3(start.x * (1f - t), start.y, start.z * (1f - t));
                yield return null;
            }
            ClearSizeBox();
        }

        // Gone (the session put aside, the step left without a box, or the make shown).
        private void ClearSizeBox()
        {
            if (held != null && held == sizeBox) { holdHand = null; mouseHolding = false; held = null; heldBody = null; }
            boxKnobAxis = -1;
            boxKnobHand = boxFarHand = null;
            boxKnobMouse = false;
            boxPending = false;
            boxFading = false;
            boxPreviewAxis = boxSizingAxis = -1;
            boxWasPinching.Clear();
            for (int i = 0; i < 3; i++) ShowDim(boxDims[i], false);
            if (sizeBox != null) Destroy(sizeBox.gameObject);
            sizeBox = boxFaces = boxHull = null;
            boxCollider = null;
            boxRods.Clear();
            SizeBoxPhase = BoxPhase.None;
        }

        // ------------------------------------------------------------ tests

        public void SimulateBoxKnob(int axis, float mm)
        {
            if (SizeBoxPhase != BoxPhase.Ready || SizeBoxKnob(axis) == null) return;
            StartBoxKnob(axis, null);
            if (BoxLine(axis, out var a, out var b))
            {
                Vector3 dir = (b - a).normalized;
                float length = mm * .001f;
                Vector3 target = axis == 1 ? a + dir * length : (a + b) * .5f + dir * length * .5f;
                for (int i = 1; i <= 6; i++) DragBoxKnobTo(Vector3.Lerp(b, target, i / 6f));
            }
            EndBoxKnob();
        }

        public void SimulateBoxDone() => FinishSizeBox();
    }

    internal static class SizeBoxWords
    {
        public static bool AnyIn(this string[] words, string p)
        {
            foreach (var w in words) if (p.Contains(w)) return true;
            return false;
        }
    }
}
