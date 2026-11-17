using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F71 (2026-11-14, after the Quest try: "drawing in the sketchbook should work like the palette"). While the page is
    // open, the hand at the page wears the tools: ink on its index fingertip (it draws) and the eraser on its thumb (the
    // thumb on the page rubs the lines out where it touches, MakerSession.SketchFingers RubOut). Raise the left hand to
    // the page and the tools hop onto the left hand, the right and they go to the right; never in the middle of a line
    // or a rub. The pink eraser button on the page is gone: in its corner sits an undo button (Ctrl+Z: takes back the
    // last line, rub or "clear"). The undo and the check are raised buttons that sink under a fingertip and click, light
    // up, ripple and play their icon (the arrow turns back, the check pops), so a press shows; a press with nothing to
    // undo bonks and shakes.
    public sealed partial class MakerSession
    {
        public const float EraseAbove = .02f, EraseRadius = .007f, ThumbBelowIndex = .006f, PenHandZone = .15f, PenHandMargin = .06f;
        public const float PageButtonRadius = .0125f, CapRise = .0055f, CapSink = .0042f;
        public const int MaxUndo = 40;
        public const string SketchThumbTaughtKey = "thinkmade.sketchThumbTaught";

        public Hand PenHand => pageHand;
        public Transform InkView => inkTip;
        public Transform PageEraserView => pageEraser;
        public Transform PaperUndoButton => paperUndo;
        public Transform PaperDoneButton => paperDone;
        public bool Erasing => erasing;
        public bool CanUndo => pageHistory.Count > 0 || drawn.Count > 0;
        public int UndoSteps => pageHistory.Count;
        public int PageErases { get; private set; }
        public int PaperDonePresses { get; private set; }
        public int PaperUndoPresses { get; private set; }
        public int PenHandSwitches { get; private set; }
        public float PageButtonSink(bool check) => (check ? doneButton : undoButton)?.sink ?? 0f;
        public bool PageButtonLit(bool check) { var b = check ? doneButton : undoButton; return b != null && b.face != null && b.face.sharedMesh == b.lit; }

        private Hand pageHand;
        private Transform inkTip, pageEraser, eraserRing;
        private bool erasing, rubKept;
        private float nextRubAt, rubSoundAt;
        private readonly List<List<List<Vector2>>> pageHistory = new List<List<List<Vector2>>>();
        private static readonly Color InkColour = new Color(.2f, .22f, .3f);
        private static readonly HandJointId[] PenJoints = { HandJointId.HandIndexTip, HandJointId.HandThumbTip };

        // ------------------------------------------------------------ the tools on the fingers

        // Every frame while the page is open (UpdateDrawingHands): which hand wears the tools, and the tools on it.
        private void SketchFingersFrame()
        {
            if (PageHandled) { HideSketchFingers(); return; } // F104: no tools on the fingers while the page is lifted
            ChoosePenHand();
            bool show = pageHand != null && !drawingDone && !SketchbookHeld && !penWaitsLift;
            if (!show) { HideSketchFingers(); return; }
            if (inkTip == null) BuildSketchFingers();
            OnFinger(inkTip, pageHand, HandJointId.HandIndexTip);
            OnFinger(pageEraser, pageHand, HandJointId.HandThumbTip);
        }

        private void HideSketchFingers()
        {
            if (inkTip != null) inkTip.gameObject.SetActive(false);
            if (pageEraser != null) pageEraser.gameObject.SetActive(false);
        }

        // The eraser is the palette's (EraserTip). F104 (Quest 2026-11-16, the user: "the paint on the index finger makes it
        // look as if it will draw a thick line; take it away or make it very small"): the ink is a small dot right at the
        // fingertip (InkDot, 3 mm across in the room, about the line it draws), no longer the palette's 2.4 x 1.9 cm
        // paint cap and drop.
        public const float InkDot = .003f;
        private void BuildSketchFingers()
        {
            inkTip = new GameObject("Ink Finger").transform;
            inkTip.SetParent(transform, false);
            Transform dot = ColourBall("Ink", InkColour, .002f);
            dot.SetParent(inkTip, false);
            dot.localPosition = new Vector3(0f, 0f, .001f);
            dot.localRotation = Quaternion.identity;
            dot.localScale = Vector3.one * (InkDot / Mathf.Max(1e-4f, transform.lossyScale.x)); // (the ink tip itself hops: SetPenHand)
            pageEraser = EraserTip();
            pageEraser.name = "Page Eraser";
            inkTip.gameObject.SetActive(false);
            pageEraser.gameObject.SetActive(false);
        }

        // Puts `view` on the fingertip, pointing out along the finger; hidden when the finger is not tracked.
        private static void OnFinger(Transform view, Hand hand, HandJointId finger)
        {
            if (view == null) return;
            if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, finger, out Pose end))
            {
                view.gameObject.SetActive(false);
                return;
            }
            Vector3 along = Vector3.zero;
            if (HandIO.Joint(hand, LastJoint(finger), out Pose last)) along = end.position - last.position;
            if (along.sqrMagnitude < 1e-6f && HandIO.Joint(hand, finger == HandJointId.HandThumbTip ? HandJointId.HandThumb1 : HandJointId.HandIndex1, out Pose root)) along = end.position - root.position;
            if (along.sqrMagnitude < 1e-6f && HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm)) along = end.position - palm.position;
            along = along.sqrMagnitude < 1e-6f ? Vector3.forward : along.normalized;
            view.gameObject.SetActive(true);
            view.SetPositionAndRotation(end.position, Quaternion.LookRotation(along, Mathf.Abs(along.y) > .98f ? Vector3.forward : Vector3.up));
        }

        // The pen hand is the one at the page. When the page opens the hand nearest it takes the tools; after that the
        // other hand takes them when it comes over the page while the pen hand is away from it (or touches the page while
        // the pen hand does not). Never in the middle of a line or a rub; a pen hand that lost tracking keeps them until
        // the other hand comes to the page.
        private void ChoosePenHand()
        {
            if (paper == null) return;
            Hand other = null, nearest = null;
            float otherH = float.MaxValue, penH = float.MaxValue, nearestD = .6f;
            bool penTracked = false;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                float h = HeightOverPage(hand, out float distance);
                if (hand == pageHand) { penTracked = true; penH = h; continue; }
                if (h < otherH) { otherH = h; other = hand; }
                if (distance < nearestD) { nearestD = distance; nearest = hand; }
            }
            if (penDown || erasing) return;
            bool otherAtPage = other != null && otherH < PenHandZone;
            if (pageHand == null) { if (otherAtPage || nearest != null) SetPenHand(otherAtPage ? other : nearest); return; }
            if (!otherAtPage) return;
            if (!penTracked || penH > PenHandZone || (otherH < PenAbove && penH > PenAbove)) SetPenHand(other);
        }

        // How high the hand's index or thumb tip is over the page (MaxValue when neither is over it), and how far the
        // nearer tip is from the page's middle.
        private float HeightOverPage(Hand hand, out float distance)
        {
            distance = float.MaxValue;
            float best = float.MaxValue, half = PaperSize * .5f + PenHandMargin;
            foreach (var id in PenJoints)
            {
                if (!HandIO.Joint(hand, id, out Pose p)) continue;
                distance = Mathf.Min(distance, Vector3.Distance(p.position, paper.position));
                Vector3 local = paper.InverseTransformPoint(p.position);
                if (Mathf.Abs(local.x) > half || Mathf.Abs(local.y) > half || local.z > .03f) continue;
                best = Mathf.Min(best, Mathf.Max(0f, -local.z));
            }
            return best;
        }

        private void SetPenHand(Hand hand)
        {
            if (hand == null || hand == pageHand) return;
            bool hop = pageHand != null && inkTip != null && inkTip.gameObject.activeSelf;
            if (hop)
            {
                ToyFx.Puff(inkTip.position, .005f, 2);
                if (pageEraser != null && pageEraser.gameObject.activeSelf) ToyFx.Puff(pageEraser.position, .005f, 2);
                PenHandSwitches++;
            }
            pageHand = hand;
            penDown = false;
            drawingHand = null;
            if (inkTip == null) BuildSketchFingers();
            inkTip.localScale = pageEraser.localScale = Vector3.one * .2f;
            StartCoroutine(Grow(inkTip, 1f, .25f));
            StartCoroutine(Grow(pageEraser, 1f, .25f));
            ToySounds.Play(ToySounds.Cue.Pop, paper != null ? paper.position : transform.position, .3f, 1.7f);
            WriteInputLog("sketch tools on the " + hand.Handedness + " hand" + (hop ? " (switched)" : ""), true);
        }

        // The page closed (or the session moves on): the tools go.
        private void PutSketchFingersAway()
        {
            if (inkTip == null && pageEraser == null && pageHand == null) return;
            if (inkTip != null) { if (inkTip.gameObject.activeSelf) ToyFx.Puff(inkTip.position, .005f, 2); Destroy(inkTip.gameObject); }
            if (pageEraser != null) { if (pageEraser.gameObject.activeSelf) ToyFx.Puff(pageEraser.position, .005f, 2); Destroy(pageEraser.gameObject); }
            inkTip = pageEraser = null;
            pageHand = null;
            StopRub();
        }

        // ------------------------------------------------------------ the thumb eraser on the page

        // The pen hand's thumb on the page - lower than its index by ThumbBelowIndex, within EraseAbove, not pinching and
        // not on a button - rubs the lines out where it touches. True while it rubs (the index draws nothing meanwhile).
        private bool ThumbRub(Hand hand, float indexHeight)
        {
            if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || HandIO.Pinching(hand) ||
                !HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose thumb)) { StopRub(); return false; }
            Vector3 local = paper.InverseTransformPoint(thumb.position);
            float h = -local.z;
            Vector2 at = new Vector2(local.x, local.y);
            bool over = Mathf.Abs(local.x) < PaperSize * .5f && Mathf.Abs(local.y) < PaperSize * .5f && !OnPageButton(thumb.position);
            bool lower = h < indexHeight - (erasing ? 0f : ThumbBelowIndex);
            bool rubbing = over && lower && local.z < .01f && h < EraseAbove + (erasing ? .005f : 0f);
            EraserRing(at, over && lower && local.z < .01f && h < PenHover, rubbing);
            if (!rubbing) { StopRub(); return false; }
            erasing = true;
            if (Time.time >= nextRubAt) { nextRubAt = Time.time + .033f; RubOut(at, thumb.position); }
            return true;
        }

        private void StopRub()
        {
            erasing = false;
            rubKept = false;
            if (eraserRing != null && eraserRing.gameObject.activeSelf) eraserRing.gameObject.SetActive(false);
        }

        // Takes the ink within EraseRadius of `at` off the page (a line rubbed through its middle becomes two). The
        // first rub of a pass keeps the page for undo.
        private bool RubOut(Vector2 at, Vector3? world = null)
        {
            erasing = true;
            float r2 = EraseRadius * EraseRadius;
            bool hit = false;
            for (int s = 0; s < drawn.Count && !hit; s++)
                for (int i = 0; i < drawn[s].Count; i++)
                    if ((drawn[s][i] - at).sqrMagnitude < r2) { hit = true; break; }
            if (!hit) return false;
            if (!rubKept) { KeepPageForUndo(); rubKept = true; }
            var kept = new List<List<Vector2>>(drawn.Count + 2);
            foreach (var stroke in drawn)
            {
                List<Vector2> run = null;
                bool cut = false;
                foreach (var p in stroke)
                {
                    if ((p - at).sqrMagnitude < r2)
                    {
                        cut = true;
                        if (run != null && run.Count > 1) kept.Add(run);
                        run = null;
                        continue;
                    }
                    (run ??= new List<Vector2>()).Add(p);
                }
                if (run != null && (run.Count > 1 || !cut)) kept.Add(run);
            }
            drawn.Clear();
            drawn.AddRange(kept);
            penDown = false;
            RedrawInk();
            PageErases++;
            Heard();
            Vector3 spot = world ?? paper.TransformPoint(new Vector3(at.x, at.y, -.002f));
            if (Time.time >= rubSoundAt)
            {
                rubSoundAt = Time.time + .14f;
                ToySounds.Play(ToySounds.Cue.Brush, spot, .16f, 1.9f + Random.Range(-.1f, .1f));
                if (Random.value < .5f) ToyFx.Puff(spot, .0025f, 1);
            }
            return true;
        }

        // A pink ring on the page where the thumb would rub (darker while it rubs).
        private void EraserRing(Vector2 at, bool show, bool rubbing)
        {
            if (!show) { if (eraserRing != null && eraserRing.gameObject.activeSelf) eraserRing.gameObject.SetActive(false); return; }
            if (paper == null) return;
            if (eraserRing == null)
            {
                eraserRing = new GameObject("Eraser Ring").transform;
                eraserRing.SetParent(paper, false);
                Color pink = Hex(Eraser.hex);
                Part(eraserRing, "Ring", StickerMesh("eraserRing", () => FlatRing(EraseRadius, .0011f, pink, "EraserRing")), Vector3.zero, 0f);
            }
            eraserRing.gameObject.SetActive(true);
            eraserRing.localPosition = new Vector3(at.x, at.y, -.0012f);
            eraserRing.localScale = Vector3.one * (rubbing ? 1f : .8f);
        }

        // ------------------------------------------------------------ undo

        private void KeepPageForUndo()
        {
            var copy = new List<List<Vector2>>(drawn.Count);
            foreach (var s in drawn) copy.Add(new List<Vector2>(s));
            pageHistory.Add(copy);
            if (pageHistory.Count > MaxUndo) pageHistory.RemoveAt(0);
            pageChangedAt = Time.time; // F104
            pageChangeKept = copy;
        }

        // ------------------------------------------------------------ F104: the pen is off while the page is lifted

        public const float LineBeforeGrab = .6f, PenQuietAfterHold = .5f;
        public bool PageHandled => SketchbookHeld || takingSketchbook || sketchbookDragging;
        public int LinesTakenBackByGrab { get; private set; }
        public bool PenWaitsLift => penWaitsLift;
        private bool pageWasHandled, penWaitsLift;
        private float pageChangedAt = -10f, penQuietUntil;
        private List<List<Vector2>> pageChangeKept;

        // True while nothing may draw: the page held or being taken, PenQuietAfterHold after, and then until the pen
        // hand's index has lifted off the page (the fingers opening as they let go used to scribble).
        private bool PenQuietForPage()
        {
            if (PageHandled)
            {
                if (!pageWasHandled)
                {
                    pageWasHandled = true;
                    // The fingers closing on the rim drew a line (or rubbed) just before the grab: that change goes.
                    if (Time.time - pageChangedAt < LineBeforeGrab && pageHistory.Count > 0 && pageHistory[pageHistory.Count - 1] == pageChangeKept)
                    {
                        drawn.Clear();
                        drawn.AddRange(pageHistory[pageHistory.Count - 1]);
                        pageHistory.RemoveAt(pageHistory.Count - 1);
                        RedrawInk();
                        LinesTakenBackByGrab++;
                        WriteInputLog("sketchbook taken: the line begun " + Mathf.RoundToInt((Time.time - pageChangedAt) * 1000f) + " ms before is taken back", true);
                    }
                    pageChangeKept = null;
                }
                penQuietUntil = Time.time + PenQuietAfterHold;
                penWaitsLift = true;
                return true;
            }
            pageWasHandled = false;
            if (Time.time < penQuietUntil) return true;
            if (penWaitsLift && !PenLifted()) return true;
            penWaitsLift = false;
            return false;
        }

        // Every tracked hand's index and thumb are off the page or more than PenHover over it.
        private bool PenLifted()
        {
            if (paper == null) return true;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                foreach (var id in PenJoints)
                {
                    if (!HandIO.Joint(hand, id, out Pose p)) continue;
                    Vector3 local = paper.InverseTransformPoint(p.position);
                    if (Mathf.Abs(local.x) < PaperSize * .5f && Mathf.Abs(local.y) < PaperSize * .5f && local.z > -PenHover && local.z < .03f) return false;
                }
            }
            return true;
        }

        // "clear" / "start again": a fresh page that undo can bring back.
        private void ClearPageKeepUndo()
        {
            var before = new List<List<Vector2>>();
            foreach (var s in drawn) before.Add(new List<Vector2>(s));
            ClearPaper();
            if (before.Count > 0) pageHistory.Add(before);
        }

        // ------------------------------------------------------------ the page's buttons

        private sealed class PageButton
        {
            public Transform root, cap, icon;
            public MeshFilter face;
            public Mesh normal, lit;
            public Color colour;
            public float spin, pop;               // the icon's answer to a press: a turn (degrees), a scale bump
            public readonly FingerPress press = new FingerPress();
            public readonly HashSet<Hand> slid = new HashSet<Hand>();
            public float pressedAt = -10f, sink, near = float.MaxValue;
            public bool down, nope;
        }

        private PageButton doneButton, undoButton;

        // Built with the page (BuildPaper): the undo in the lower left corner, the check in the lower right, each a
        // raised cap in a darker socket.
        private void BuildPageButtons()
        {
            Color green = new Color(.3f, .75f, .42f), beige = new Color(.82f, .71f, .54f), white = new Color(.99f, .99f, .97f);
            doneButton = PageButtonAt("Done", new Vector3(PaperSize * .5f - .02f, -PaperSize * .5f + .02f, -.001f), green, "pageDone");
            doneButton.pop = .35f;
            doneButton.spin = -12f;
            paperDone = doneButton.root;
            var shortBar = StickerMesh("checkShort", () => ToyMeshes.RoundedSlab(.0035f, .008f, .0015f, .0008f, white, white, white, "CheckShort"));
            var longBar = StickerMesh("checkLong", () => ToyMeshes.RoundedSlab(.0035f, .015f, .0015f, .0008f, white, white, white, "CheckLong"));
            Part(doneButton.icon, "Check", shortBar, new Vector3(-.0035f, -.001f, 0f), 45f);
            Part(doneButton.icon, "Check", longBar, new Vector3(.0025f, .002f, 0f), -35f);
            undoButton = PageButtonAt("Undo", new Vector3(-PaperSize * .5f + .02f, -PaperSize * .5f + .02f, -.001f), beige, "pageUndo");
            undoButton.pop = .1f;
            undoButton.spin = 70f; // the arrow turns back (counter-clockwise as the person sees the page)
            paperUndo = undoButton.root;
            Part(undoButton.icon, "Arrow", StickerMesh("undoArrow", () => UndoArrowMesh(white)), Vector3.zero, 0f);
        }

        private PageButton PageButtonAt(string name, Vector3 at, Color colour, string key)
        {
            var b = new PageButton { colour = colour };
            b.root = new GameObject(name).transform;
            b.root.SetParent(paper, false);
            b.root.localPosition = at;
            Color socket = Color.Lerp(colour, Color.black, .38f);
            Part(b.root, "Socket", StickerMesh(key + "Socket", () => ToyMeshes.Disc(PageButtonRadius + .0022f, .0012f, socket, socket, key + "Socket")), Vector3.zero, 0f);
            b.cap = new GameObject("Cap").transform;
            b.cap.SetParent(b.root, false);
            b.cap.localPosition = new Vector3(0f, 0f, -CapRise);
            b.normal = StickerMesh(key + "Cap", () => ToyMeshes.Disc(PageButtonRadius, CapRise, colour, Color.Lerp(colour, Color.black, .22f), key + "Cap"));
            b.lit = StickerMesh(key + "CapLit", () => ToyMeshes.Disc(PageButtonRadius, CapRise, Color.Lerp(colour, Color.white, .45f), Color.Lerp(colour, Color.black, .1f), key + "CapLit"));
            Part(b.cap, "Face", b.normal, Vector3.zero, 0f);
            b.face = b.cap.Find("Face").GetComponent<MeshFilter>();
            b.icon = new GameObject("Icon").transform;
            b.icon.SetParent(b.cap, false);
            b.icon.localPosition = new Vector3(0f, 0f, -.0002f);
            return b;
        }

        private bool OnPageButton(Vector3 tip) =>
            (paperDone != null && Vector3.Distance(tip, paperDone.position) < DoneReach) ||
            (paperUndo != null && Vector3.Distance(tip, paperUndo.position) < DoneReach);

        // F80: no line starts within ButtonGuard of a button, measured along the page (`local` is in the page's space), at
        // any height: a fingertip coming down onto undo or the check drew a dot on the way and the press was then lost.
        public const float ButtonGuard = .035f;
        private bool NearPageButton(Vector3 local) => NearOnPage(paperDone, local) || NearOnPage(paperUndo, local);
        private bool NearOnPage(Transform button, Vector3 local)
        {
            if (button == null || paper == null) return false;
            Vector3 b = paper.InverseTransformPoint(button.position);
            return new Vector2(local.x - b.x, local.y - b.y).sqrMagnitude < ButtonGuard * ButtonGuard;
        }

        // F80: the line that reached a button is a real line (a dot or a tick is not: the finger was landing).
        private bool RealLineGoing(Hand hand) =>
            penDown && hand == drawingHand && drawn.Count > 0 && drawn[drawn.Count - 1].Count > 1 && Length(drawn[drawn.Count - 1]) > .008f;

        // Every frame while the page is open: fingertip presses (one per touch, a free pointing hand; a line slid onto a
        // button is not a press until the finger leaves it) when `pressable`, and both buttons' looks. True when one was
        // pressed this frame.
        private bool PageButtonsFrame(bool pressable)
        {
            if (doneButton == null || undoButton == null) return false;
            doneButton.near = undoButton.near = float.MaxValue;
            bool pressed = false;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null) continue;
                if (!hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    doneButton.press.Forget(hand);
                    undoButton.press.Forget(hand);
                    continue;
                }
                bool drew = RealLineGoing(hand); // still drawing when it reached the button: a line slid onto it (F80: a real line)
                if (ButtonTouch(doneButton, hand, tip.position, drew, pressable && !pressed)) { PressLog("sketchbook check", hand); PressPaperDone(); pressed = true; }
                if (ButtonTouch(undoButton, hand, tip.position, drew, pressable && !pressed)) { PressLog("sketchbook undo", hand); PressPaperUndo(); pressed = true; }
            }
            ButtonLook(doneButton);
            ButtonLook(undoButton);
            return pressed;
        }

        private bool ButtonTouch(PageButton b, Hand hand, Vector3 tip, bool drew, bool pressable)
        {
            float d = Vector3.Distance(tip, b.root.position);
            b.near = Mathf.Min(b.near, d);
            bool on = d < DoneReach;
            if (!on) b.slid.Remove(hand);
            else if (drew) b.slid.Add(hand);
            return b.press.Step(this, hand, on && pressable && !b.slid.Contains(hand));
        }

        // Near a fingertip the button grows and its cap starts to sink; pressed, the cap goes all the way down, lights up
        // and stays down while the fingertip is on it, then springs back up past its rest.
        private void ButtonLook(PageButton b)
        {
            float since = Time.time - b.pressedAt;
            if (b.down && b.near > DoneReach + .015f && since > .2f) b.down = false;
            // F79: no growing (and below, no icon spin or bump): the press is the cap going down and up, its colour and click.
            b.root.localScale = Vector3.one;
            float want = b.down || since < .2f ? 1f : .3f * Mathf.Clamp01(1f - (b.near - DoneReach) / .03f);
            b.sink = Mathf.MoveTowards(b.sink, want, Time.deltaTime / (want > b.sink ? .04f : .12f));
            float spring = !b.down && since > .2f && since < .55f ? -.22f * Mathf.Sin((since - .2f) / .35f * Mathf.PI) : 0f;
            b.cap.localPosition = new Vector3(0f, 0f, -CapRise + CapSink * (b.sink + spring));
            b.face.sharedMesh = b.down || since < .3f ? b.lit : b.normal;
            float t = Mathf.Clamp01(since / .4f);
            if (b.nope)
            {
                b.icon.localRotation = Quaternion.identity;
                b.icon.localScale = Vector3.one;
                b.icon.localPosition = new Vector3(since < .4f ? Mathf.Sin(since * 50f) * .0012f * (1f - t) : 0f, 0f, -.0002f);
            }
            else
            {
                b.icon.localRotation = Quaternion.identity;
                b.icon.localScale = Vector3.one;
                b.icon.localPosition = new Vector3(0f, 0f, -.0002f);
            }
        }

        // Pressed by a fingertip, a pinch from afar (FarButtons) or the Editor mouse.
        private void PressPaperDone()
        {
            PaperDonePresses++;
            ButtonPressed(doneButton, true);
            WriteInputLog("press sketchbook check (" + DrawnPoints + " points drawn)", true);
            FinishDrawing(); // too little drawn: Maker says "Draw a bit more first!"
        }

        private void PressPaperUndo()
        {
            PaperUndoPresses++;
            bool can = CanUndo;
            ButtonPressed(undoButton, can);
            WriteInputLog("press sketchbook undo (" + (can ? "took back the last change" : "nothing to undo") + ")", true);
            if (can) UndoStroke();
        }

        private void ButtonPressed(PageButton b, bool did)
        {
            if (b == null) return;
            b.pressedAt = Time.time;
            b.down = b.near < DoneReach + .015f; // a fingertip still on it keeps it down
            b.nope = !did;
            ToySounds.Play(did ? ToySounds.Cue.Press : ToySounds.Cue.Bonk, b.root.position, .55f, did ? 1.6f : 1.3f);
            // F79 (Quest: "the circle when a sketchbook button is pressed is odd - going in and up, the sound and the colour
            // are enough"): no ripple ring.
        }

        // A ring in the button's colour spreads out from it on the page.
        private IEnumerator PressRipple(PageButton b)
        {
            if (paper == null || b == null || b.root == null) yield break;
            var ring = new GameObject("Press Ripple").transform;
            ring.SetParent(paper, false);
            ring.localPosition = b.root.localPosition + new Vector3(0f, 0f, -.0003f);
            Color c = Color.Lerp(b.colour, Color.white, .15f);
            Part(ring, "Ring", StickerMesh("ripple" + ColorUtility.ToHtmlStringRGB(c), () => FlatRing(1f, .09f, c, "PressRipple")), Vector3.zero, 0f);
            for (float t = 0f; t < 1f && ring != null; t += Time.deltaTime / .35f)
            {
                float r = Mathf.Lerp(PageButtonRadius + .002f, PageButtonRadius + .016f, 1f - (1f - t) * (1f - t));
                ring.localScale = new Vector3(r, r, 1f);
                yield return null;
            }
            if (ring != null) Destroy(ring.gameObject);
        }

        // ------------------------------------------------------------ meshes

        // A flat band `width` wide along a polyline (both faces, vertex colour), joints overlapped.
        private static void FlatBand(List<Vector3> v, List<Color> c, List<Vector3> n, List<int> t, IList<Vector2> pts, float width, Color colour, bool closed)
        {
            int count = pts.Count, segments = closed ? count : count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % count];
                if ((b - a).sqrMagnitude < 1e-12f) continue;
                Vector2 d = (b - a).normalized, o = new Vector2(-d.y, d.x) * width * .5f;
                a -= d * width * .35f;
                FlatQuad(v, c, n, t, a - o, a + o, b - o, b + o, colour);
            }
        }

        private static void FlatQuad(List<Vector3> v, List<Color> c, List<Vector3> n, List<int> t, Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, Color colour)
        {
            int k = v.Count;
            v.Add(a0); v.Add(a1); v.Add(b0); v.Add(b1);
            for (int i = 0; i < 4; i++) { c.Add(colour); n.Add(Vector3.back); }
            t.AddRange(new[] { k, k + 1, k + 2, k + 2, k + 1, k + 3, k, k + 2, k + 1, k + 2, k + 3, k + 1 });
        }

        private static Mesh FlatMesh(List<Vector3> v, List<Color> c, List<Vector3> n, List<int> t, string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetNormals(n);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh FlatRing(float radius, float width, Color colour, string name)
        {
            var pts = new List<Vector2>();
            for (int i = 0; i < 40; i++) { float a = i / 40f * Mathf.PI * 2f; pts.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius); }
            var v = new List<Vector3>(); var c = new List<Color>(); var n = new List<Vector3>(); var t = new List<int>();
            FlatBand(v, c, n, t, pts, width, colour, true);
            return FlatMesh(v, c, n, t, name);
        }

        // The undo arrow as on most undo buttons: a bar along the bottom pointing left, hooked up round the right side
        // and back along the top (x is the person's right, y away from them on the page).
        private static Mesh UndoArrowMesh(Color colour)
        {
            const float w = .0022f, r = .0032f, cx = .0016f;
            var path = new List<Vector2> { new Vector2(-.0028f, -r) };
            for (int i = 0; i <= 14; i++)
            {
                float a = (-90f + i / 14f * 180f) * Mathf.Deg2Rad;
                path.Add(new Vector2(cx + Mathf.Cos(a) * r, Mathf.Sin(a) * r));
            }
            path.Add(new Vector2(-.0034f, r));
            var v = new List<Vector3>(); var c = new List<Color>(); var n = new List<Vector3>(); var t = new List<int>();
            FlatBand(v, c, n, t, path, w, colour, false);
            // The arrowhead at the bottom left, pointing left.
            int k = v.Count;
            v.Add(new Vector2(-.0064f, -r)); v.Add(new Vector2(-.0026f, -r + .0031f)); v.Add(new Vector2(-.0026f, -r - .0031f));
            for (int i = 0; i < 3; i++) { c.Add(colour); n.Add(Vector3.back); }
            t.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 1 });
            return FlatMesh(v, c, n, t, "UndoArrow");
        }
    }
}
