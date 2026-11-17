using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F60 (2026-11-13, after a Quest try: "grab & release sometimes fails, pinches happen when I did not pinch, it
    // sketched when I never asked, and the plate I was moving suddenly vanished"). The logs showed: the hand holding
    // the stand brushed the Cancel pill beside it; a hand working Spark's frame pressed the mic; pinches near Maker
    // took its held-out sketchbook. Here:
    //  - One hand, one job: a hand holding something (Spark's frame, a shape card, a made thing or stand, a flag, a
    //    knob, a note, a swatch, Maker's book or notebook, a teammate) presses nothing, and neither for
    //    ReleaseQuiet after it lets go. A fingertip that was on a button while its hand was busy must leave it first.
    //  - A fingertip press (FingerPress) needs a pointing hand (not pinching, the thumb clear of the index tip) resting
    //    on the button for PressDwell. Every press and grab is written to maker_input_log.txt at once (PressLog).
    //  - Near pinches go to the nearest of Spark's frame, a shape card, the held-out sketchbook and a made thing
    //    (NearestGrab), not to whichever comes first in OnPinch's list; a small mint dot shows which one a free hand
    //    would take; a teammate is not grabbed while one of those is within reach of that hand (its hand grab is
    //    switched off for the moment).
    public sealed partial class MakerSession
    {
        // F67 (2026-11-13, Quest: "the buttons on the bubbles barely press, Done does not press"): a quick poke goes
        // through a 3 cm thick button in about 70 ms, so PressDwell .1 -> .03; the thumb only has to be clear of the
        // index tip by ThumbClear .025 -> .015; and a finger that arrives not pointing yet (a relaxed hand) waits
        // instead of being spent until it leaves - only a busy hand's finger is spent.
        public const float ReleaseQuiet = .4f, PressDwell = .03f, ThumbClear = .015f, GrabReach = .04f;
        // The fingertip range through a button facing the person (its +Z points away from them): from PressNear on the
        // person's side to PressThrough behind it (a finger goes on through a virtual button), PressMargin round it.
        public const float PressNear = -.015f, PressThrough = .06f, PressMargin = .008f;

        public int GatedPresses { get; private set; }  // fingertips kept off a button because their hand was busy
        public int Presses { get; private set; }
        public string LastPress { get; private set; } = "";

        private readonly Dictionary<Hand, float> releasedAt = new Dictionary<Hand, float>();
        private readonly Dictionary<Hand, string> heldBy = new Dictionary<Hand, string>();
        private readonly List<HandGrabInteractable> crewGrabs = new List<HandGrabInteractable>();
        private bool crewGrabsOff;
        private Transform leftDot, rightDot;

        // What this hand holds now (null: nothing). Books and teammates held by the hand SDK count for both hands.
        public string HeldBy(Hand hand)
        {
            if (hand == null) return null;
            if (holdHand == hand) return "made thing";
            if ((frameHand == hand || frameHand2 == hand) && frameMode != FrameMode.None) return "frame";
            if (cardHand == hand && carriedCard != null) return "card";
            if (carryHand == hand && carried != null) return "flag";
            if (knobHand == hand && knobAxis >= 0) return "knob";
            if (boxKnobHand == hand && boxKnobAxis >= 0) return "knob"; // F75: a size box knob
            if (partKnobHand == hand) return "knob"; // F83: the knob on a part's tip
            if (noteHand == hand && carriedNote != null) return "note";
            if (dragHand == hand && Dragging != null) return "swatch";
            if (sketchbookHand == hand && sketchbookDragging) return "sketchbook";
            if (notebookHand == hand && notebookDragging) return "notebook";
            if (captureHand == hand) return "colour pick";
            if (panelHand == hand && panelDragging) return panelGroup == PanelGroup.LookRow ? "look row" : "pictures"; // 2026-11-13: the look row or the review pictures (F80: the row is never grab & release)
            if (pinCarry != null && pinCarry.hand == hand) return "wall picture"; // F72: a picture (or its paper ball) off the wall
            if (arrival != null && arrival.MicHand == hand) return "mic"; // F66: a hand carrying the mic presses nothing
            if (SketchbookHeld || takingSketchbook) return "sketchbook";
            if (NotebookHeld) return "notebook";
            foreach (var c in cast.Values)
                if (c != null && c.movement != null && c.movement.Grabbed) return "teammate";
            return null;
        }

        // Free to press: holding nothing, and nothing for ReleaseQuiet.
        public bool HandFree(Hand hand) =>
            hand != null && HeldBy(hand) == null && (!releasedAt.TryGetValue(hand, out float at) || Time.time - at > ReleaseQuiet);

        // F68: a pointing finger may make the crew look only when this hand is doing nothing else: holding nothing,
        // not drawing on the page, not by the page, the frame or a lesson's palette (WindupsGestures).
        public bool GestureFree(Hand hand)
        {
            if (!HandFree(hand) || Drawing || PaletteOnPalm) return false;
            if (!HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip)) return true;
            if (paper != null && paper.gameObject.activeInHierarchy && Vector3.Distance(tip.position, paper.position) < .25f) return false;
            if (FrameShown && Vector3.Distance(tip.position, showFrame.position) < .25f) return false;
            return true;
        }

        public bool HandsBusy
        {
            get
            {
                foreach (var hand in HandSet.Both())
                    if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && !HandFree(hand)) return true;
                return false;
            }
        }

        // A pointing hand: not pinching, the thumb away from the index tip.
        public static bool Pointing(Hand hand)
        {
            if (hand == null || HandIO.Pinching(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip)) return false;
            return !HandIO.Joint(hand, HandJointId.HandThumbTip, out Pose thumb) || Vector3.Distance(tip.position, thumb.position) >= ThumbClear;
        }

        // The mic (WindupsArrival): a fingertip of this hand may press it now.
        public bool MayPressMic(Hand hand) => !Begun || HandFree(hand);

        // ------------------------------------------------------------ fingertip presses

        // One button's fingertip state: Step(hand, on) is true once per touch, when a free, pointing hand has rested
        // its fingertip on the button for PressDwell. A fingertip that came onto it while its hand was busy (or not
        // pointing) must leave it before it can press.
        private sealed class FingerPress
        {
            private readonly Dictionary<Hand, float> since = new Dictionary<Hand, float>();
            private readonly HashSet<Hand> spent = new HashSet<Hand>();

            public bool Step(MakerSession s, Hand hand, bool on)
            {
                if (hand == null) return false;
                if (!on) { since.Remove(hand); spent.Remove(hand); return false; }
                if (spent.Contains(hand)) return false;
                if (!s.HandFree(hand))
                {
                    if (!since.ContainsKey(hand)) { spent.Add(hand); s.GatedPresses++; }
                    else since.Remove(hand);
                    return false;
                }
                if (!Pointing(hand)) { since.Remove(hand); return false; } // F67: waits for the finger to point
                if (!since.TryGetValue(hand, out float t)) { since[hand] = Time.time; return false; }
                if (Time.time - t < PressDwell) return false;
                since.Remove(hand);
                spent.Add(hand);
                return true;
            }

            public void Forget(Hand hand) { if (hand != null) { since.Remove(hand); spent.Remove(hand); } }
            public void Clear() { since.Clear(); spent.Clear(); }
        }

        private void PressLog(string what, Hand hand)
        {
            Presses++;
            LastPress = what;
            string side = hand != null ? hand.Handedness.ToString() : "?";
            string held = hand != null ? HeldBy(hand) : null;
            WriteInputLog("press " + what + " by " + side + (held != null ? " (holding " + held + ")" : ""), true);
        }

        // ------------------------------------------------------------ every frame

        private void HandGateFrame()
        {
            // F67 (VerifyShowFrame, the F63 session): a two-hand stretch of Spark's frame is a hold-and-pull - both hands
            // let go when their fingers open. A hand still pinching when the stretch starts is no longer grab & release,
            // and the second hand never becomes one (a hand already carrying with open fingers stays as it was).
            bool stretching = frameHand2 != null && frameMode != FrameMode.None;
            if (stretching && HandIO.Gripping(frameHand)) HandIO.EndHold(frameHand);
            foreach (var hand in HandSet.Both())
            {
                if (hand == null) continue;
                string now = hand.isActiveAndEnabled && HandIO.Connected(hand) ? HeldBy(hand) : null;
                heldBy.TryGetValue(hand, out string before);
                if (before != now)
                {
                    if (before != null) { releasedAt[hand] = Time.time; WriteInputLog("let go " + before + " (" + hand.Handedness + ")", true); }
                    if (now != null) WriteInputLog("holding " + now + " (" + hand.Handedness + ")", true);
                    heldBy[hand] = now;
                    // F67 grab & release: a thing taken with a short pinch stays in the hand until the next pinch.
                    if (now != null && StaysInHand(now) && !(now == "frame" && stretching)) HandIO.BeginHold(hand);
                    else HandIO.EndHold(hand);
                }
            }
            GrabHints();
        }

        // F67: what is carried grab & release (things you move about); tools and knobs keep pinch-and-hold.
        // F73: Spark's frame is hold to carry (the user's pick): it follows the hand only while it pinches.
        private static bool StaysInHand(string what) =>
            what == "made thing" || what == "card" || what == "flag" || what == "note" || what == "wall picture" || what == "pictures" || what == "mic";

        // ------------------------------------------------------------ the nearest grab

        private enum GrabKind { None, Card, Frame, Book, Made, PartKnob }

        // What a near pinch at `point` would take: the nearest of the session's grabbable things within GrabReach.
        private GrabKind NearestGrab(Vector3 point, out float distance, out Vector3 at)
        {
            distance = GrabReach;
            at = point;
            var kind = GrabKind.None;
            if (float.IsInfinity(point.x)) return kind;
            if (carriedCard == null)
                foreach (var c in ShapeCards)
                {
                    float d = CardDistance(c, point, out Vector3 p);
                    if (d < distance) { distance = d; at = p; kind = GrabKind.Card; }
                }
            if (FrameShown && !frameFlying && frameMode == FrameMode.None)
            {
                float d = FrameDistance(point, out Vector3 p);
                if (d < distance) { distance = d; at = p; kind = GrabKind.Frame; }
            }
            if (OfferingBook && paper != null && paper.gameObject.activeInHierarchy && !paperInFlight)
            {
                float d = BookDistance(point, out Vector3 p);
                if (d < distance) { distance = d; at = p; kind = GrabKind.Book; }
            }
            var made = MadeNear(point);
            if (made != null && held == null)
            {
                float d = ThingDistance(made, point, out Vector3 p);
                if (IsStand(made) && StandGripAt(point) == made) d = Mathf.Min(d, GrabReach * .5f); // F97: the whole ring round the plate
                if (d < distance) { distance = d; at = p; kind = GrabKind.Made; }
            }
            // F83: the knob on a lit part's tip wins over the thing it is on unless the pinch is a centimetre nearer
            // to the surface than to the knob (a pinch right on the surface still takes the whole thing).
            float knob = PartKnobDistance(point);
            if (knob < float.MaxValue && knob - .01f < distance) { distance = knob; at = partKnob.position; kind = GrabKind.PartKnob; }
            return kind;
        }

        private static float RectDistance(Transform t, Vector3 point, float halfX, float halfY, out Vector3 nearest)
        {
            Vector3 l = t.InverseTransformPoint(point);
            Vector3 s = t.lossyScale;
            Vector3 c = new Vector3(Mathf.Clamp(l.x, -halfX, halfX), Mathf.Clamp(l.y, -halfY, halfY), 0f);
            nearest = t.TransformPoint(c);
            return Vector3.Distance(nearest, point);
        }

        private float CardDistance(ShapeCard c, Vector3 point, out Vector3 nearest)
        {
            nearest = point;
            if (c.crumpled)
            {
                if (c.ball == null) return float.MaxValue;
                nearest = c.ball.transform.position;
                return Mathf.Max(0f, Vector3.Distance(point, nearest) - .015f);
            }
            if (c.root == null) return float.MaxValue;
            return RectDistance(c.root, point, (c.width + .012f) * .5f, (c.height + .012f) * .5f, out nearest);
        }

        // To the frame's rim, a corner or its button (the opening itself is not grabbed).
        private float FrameDistance(Vector3 point, out Vector3 nearest)
        {
            Vector3 l = FrameLocal(point);
            float hx = FrameSize.x * .5f, hy = FrameSize.y * .5f;
            // The nearest point of the outline.
            float cx = Mathf.Clamp(l.x, -hx, hx), cy = Mathf.Clamp(l.y, -hy, hy);
            if (Mathf.Abs(cx) < hx && Mathf.Abs(cy) < hy)
            {
                if (hx - Mathf.Abs(cx) < hy - Mathf.Abs(cy)) cx = Mathf.Sign(cx == 0f ? 1f : cx) * hx;
                else cy = Mathf.Sign(cy == 0f ? 1f : cy) * hy;
            }
            nearest = showFrame.TransformPoint(new Vector3(cx, cy, 0f));
            float d = Vector3.Distance(nearest, point);
            // F66: anywhere on the (wider) band that takes a pinch counts as on the frame.
            if ((Mathf.Abs(l.z) < FrameGripDepth && OnRimBand(l)) || OverLyingFrame(l) || InOpeningStanding(l)) d = Mathf.Min(d, GrabReach * .5f); // F68: over a lying frame too; F73: its opening when it stands
            float b = Vector3.Distance(frameButton.position, point);
            if (b < d) { d = b; nearest = frameButton.position; }
            float h = Vector3.Distance(FrameHandlePoint, point);
            if (h < d) { d = h; nearest = FrameHandlePoint; }
            return d;
        }

        private float BookDistance(Vector3 point, out Vector3 nearest)
        {
            float half = (PaperSize + .01f) * .5f;
            return RectDistance(paper, point, half, half, out nearest);
        }

        private static float ThingDistance(Transform thing, Vector3 point, out Vector3 nearest)
        {
            nearest = thing.position;
            var r = thing.GetComponentInChildren<Renderer>();
            if (r == null) return Vector3.Distance(point, nearest);
            var b = r.bounds;
            nearest = b.ClosestPoint(point);
            return Vector3.Distance(nearest, point);
        }

        // A near pinch: the nearest grabbable thing takes it (true), else OnPinch's list goes on.
        private bool NearestPinch(Hand hand, Vector3 point)
        {
            // F60b: the other hand on the rim of the frame being carried: both stretch it.
            if (FrameSecondHand(hand, point)) { lastPinchBy = "show frame (both hands)"; return true; }
            var kind = NearestGrab(point, out float d, out _);
            switch (kind)
            {
                case GrabKind.Card: lastPinchBy = "shape card"; return ShapeCardPinch(hand, point, null);
                case GrabKind.Frame: lastPinchBy = "show frame"; return ShowFramePinch(hand, point, null);
                case GrabKind.Book: lastPinchBy = "sketchbook"; return SketchbookPinch(hand, point, null);
                case GrabKind.PartKnob: lastPinchBy = "part knob"; return PartKnobPinch(hand, point); // F83
                case GrabKind.Made:
                    var near = MadeNear(point);
                    if (near == null) return false;
                    lastPinchBy = "made thing";
                    Grab(hand, point, near);
                    return true;
                default: return false;
            }
        }

        // The mint dot where a free, open hand would take something, and teammates left alone meanwhile.
        // 2026-11-15 (second Quest try: "what are the circles stuck to my hand when I hold the horse? can't they just go?"):
        // the dot is gone (GrabHintDots false); the teammates' grabs are still switched off near things as before.
        public static bool GrabHintDots;
        private void GrabHints()
        {
            bool anyNear = false;
            foreach (bool left in new[] { true, false })
            {
                var hand = HandSet.Side(left ? Handedness.Left : Handedness.Right);
                var dot = left ? leftDot : rightDot;
                bool show = false;
                if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && HeldBy(hand) == null && !HandIO.Pinching(hand) && !Drawing)
                {
                    Vector3 point = PinchPoint(hand);
                    bool something = NearestGrab(point, out float d, out Vector3 at) != GrabKind.None;
                    // F66: the mic's body shows the same dot (its button glows only for a fingertip over the top).
                    if (!something && HandRules && arrival != null && arrival.MicShown && !arrival.MicHeld && arrival.MicNear(point)) { something = true; at = arrival.MicClosest(point); }
                    // F97: a flag within reach: the teammate behind it does not take the pinch.
                    if (!something && Flags.Count > 0 && FlagNear(point, null) is Flag nearFlag) { something = true; at = nearFlag.root.position; }
                    if (something)
                    {
                        anyNear = true;
                        show = GrabHintDots;
                    }
                    if (show)
                    {
                        if (dot == null)
                        {
                            dot = Part(left ? "Grab Hint L" : "Grab Hint R", StickerMesh("grabDot", () => ToyMeshes.Disc(.006f, .004f, new Color(.55f, 1f, .72f), new Color(.3f, .8f, .5f), "GrabDot")));
                            if (left) leftDot = dot; else rightDot = dot;
                        }
                        dot.position = at;
                        dot.rotation = SpeechBubbleDock.FacingAt(at);
                        dot.localScale = Vector3.one * (1f + .25f * Mathf.Sin(Time.time * 8f));
                    }
                }
                if (dot != null && dot.gameObject.activeSelf != show) dot.gameObject.SetActive(show);
            }
            // A teammate's own hand grab is off while a thing of the session is within reach (it took the pinch meant
            // for a frame corner or a card at Maker's head height).
            // F101: and while a hand works (holds, paints, draws, measures...) and CrewQuietAfter after: a teammate that
            // walked up to the working hands was picked up by the pinch meant for the work.
            if (Begun && AnyHandWorkedWithin(CrewQuietAfter)) anyNear = true;
            if (anyNear == crewGrabsOff) return;
            // (2026-11-13, the user: "the speech bubbles still stretch and crumple - I asked for that to go") The speech
            // cards hang under the characters: switching every hand grab under a character back on re-enabled the
            // cards' grabs that F52 had switched off. Only the characters' own grabs are switched here.
            if (crewGrabs.Count == 0)
                foreach (var c in cast.Values)
                    if (c != null)
                        foreach (var g in c.GetComponentsInChildren<HandGrabInteractable>(true))
                            if (g.GetComponentInParent<TransferableSpeechBubble>(true) == null) crewGrabs.Add(g);
            bool someoneHeld = false;
            foreach (var c in cast.Values) someoneHeld |= c != null && c.movement != null && c.movement.Grabbed;
            if (anyNear && someoneHeld) return;
            crewGrabsOff = anyNear;
            foreach (var g in crewGrabs) if (g != null) g.enabled = !anyNear;
        }

        // ------------------------------------------------------------ Maker's sketchbook

        public static float EmptyPageClose = 20f, DrawingIdleClose = 90f;
        public int BooksClosedUnused { get; private set; }
        private bool drawingTimedOut;

        // On or by the open page (a pinch there belongs to the drawing).
        private bool OnOpenPage(Vector3 point)
        {
            if (paper == null || !paper.gameObject.activeInHierarchy || float.IsInfinity(point.x)) return false;
            Vector3 l = paper.InverseTransformPoint(point);
            float half = PaperSize * .5f + .03f;
            return Mathf.Abs(l.x) <= half && Mathf.Abs(l.y) <= half && Mathf.Abs(l.z) <= .06f;
        }

        // The page went unused: Maker takes the book back (and holds it out again in the shape step). Nothing is drawn
        // from it, nothing changes.
        private void CloseUnusedBook(string why)
        {
            BooksClosedUnused++;
            WriteInputLog("sketchbook closed: " + why, true);
            drawingTimedOut = false;
            PutSketchbookAway();
            Say(Crew.Maker, "No drawing? I'll take my book back for now.");
            if (Current == Step.ShapeInput) StartOfferingBook();
        }

        // Tests.
        public void ForgetReleases() => releasedAt.Clear();
        public bool GrabHintShown(bool left) => (left ? leftDot : rightDot) is Transform t && t != null && t.gameObject.activeSelf;
        public bool CrewGrabsOff => crewGrabsOff;
        public bool SketchbookTaken => SketchbookHeld || takingSketchbook || sketchbookDragging;
    }
}
