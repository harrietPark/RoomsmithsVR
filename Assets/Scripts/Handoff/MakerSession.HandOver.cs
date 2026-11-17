using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F66 (2026-11-13, the user: "the palette should not be there whenever I open my left hand - I should get it from
    // the character; when I show that I want to colour (left palm open, looked at for a while) it hands me the palette,
    // and closing the left hand sends it back. And when I make the tape gesture it should give me the tape").
    // Tools come from the teammate who owns them; a tool that was not handed over does nothing:
    //  - Spark carries a little palette. Left palm open, up, and looked at for PaletteAskTime: Spark tosses it over and
    //    it grows into the real palette on the left hand. It stays on that hand however the hand turns. The left hand
    //    closed into a fist for PaletteFistTime (or not seen for PaletteLostTime) sends it back to Spark. Colour picks,
    //    the paint tube and wheel, the dip-to-paint brush and the L-frame texture work only while it is on the hand.
    //  - Fixer carries the yellow tape. Both free hands pinching (begun within TapeTogether of each other, no more than
    //    TapeStartGap apart): Fixer tosses it into the hands and the two-hand tape measures as before. It goes back by
    //    itself TapeBackAfter after the last use (at the measuring step it stays for the whole step).
    // The carried props follow the teammate's left mitten (no hold, so nothing else the mittens do is disturbed); one
    // carried prop bobs toward the person at the step that needs it (Finish: the palette, Measuring: the tape).
    // Nothing here allocates per frame.
    public sealed partial class MakerSession
    {
        public enum ToolAt { Crew, ToHand, Hand, ToCrew }

        // F81 (Quest: "the palette doesn't come out well, and closing my left hand doesn't make it go"): asked more easily
        // (looked at within 50 deg, palm up from PaletteUp, PaletteAskTime .25 s); a closed hand (the four fingertips near the
        // palm, PaletteFistReach) or the palm turned down (PalmDownTime) sends it back.
        // F100 (Quest 2026-11-15, the user: "the palette keeps coming out - only when I look at my left palm, not just
        // because the palm is open"): the palm must be near the middle of the view (PaletteLookAngle 22 deg, was 50), turned
        // toward the face (PaletteFaceEyes: its normal toward the eyes, was barely), held PaletteAskTime .6 s (was .25).
        public static float PaletteAskTime = .6f, PaletteFly = .5f, PaletteLookAngle = 22f, PaletteFistTime = .3f, PaletteLostTime = 8f;
        public static float PaletteUp = .3f, PaletteFistReach = .068f, PalmDownTime = .6f, PaletteFaceEyes = .6f;
        public static bool PalmDownSendsBack; // F82: off (on the Quest it sent the palette back five times while the person used it); a fist still does
        private float palmDownSince = -1f;
        // F95 (Quest 2026-11-15, the user: "the palette doesn't go away well"): the log had the left palm turned down
        // (up -0.7) for 50 s and for 15 s with the palette still on it. The palm turned down sends it back again, but
        // only from an open, free left hand (four fingers out, not pinching, holding nothing, no L frame for a texture,
        // no tape, no swatch dragged), turned past PalmAwayUp for PalmAwayTime - the moments F82 saw it go while in use
        // were a pinching or framing left hand.
        public static bool PalmAwaySendsBack = true;
        public static float PalmAwayUp = -.4f, PalmAwayTime = .7f;
        // F87 (Quest 2026-11-15: "I lowered my left hand and the palette is still there"): a left hand let down (the palm
        // PaletteDropBelow under the eyes for PaletteDropTime) gives it back, and a hand lost while low or out of view
        // gives it back after PaletteLowLostTime (PaletteLostTime only covers a flicker in front of the eyes).
        public static float PaletteDropBelow = .55f, PaletteDropTime = .5f, PaletteLowLostTime = 1f;
        private float paletteDropSince = -1f;
        private bool paletteLastLow;
        public static float TapeStartGap = .6f, TapeTogether = 1.5f, TapeFly = .3f, TapeBackAfter = 2.5f;
        private const float MiniPalette = .36f; // Spark's little palette against the real one

        public ToolAt PaletteAt { get; private set; }
        public ToolAt TapeAt { get; private set; }
        public int PaletteHandOvers { get; private set; }
        public int PaletteReturns { get; private set; }
        public int TapeHandOvers { get; private set; }
        public int TapeReturns { get; private set; }
        public string LastPaletteReturn { get; private set; } = "";
        public Transform SparkPalette => sparkPalette;
        public Transform FixerTape => fixerTape;

        private Transform sparkPalette, fixerTape;
        private float paletteAskSince = -1f, paletteFlyAt, paletteFistSince = -1f, paletteLostSince = -1f;
        private Vector3 paletteFlyFrom, paletteLastPos;
        private Quaternion paletteLastRot = Quaternion.identity;
        private bool paletteByEditor;
        private float tapePinchL = -10f, tapePinchR = -20f, tapeFlyAt, tapeIdleSince = -1f;
        private bool tapeWasL, tapeWasR;
        private Vector3 tapeFlyFrom, tapeLastPos;

        // ------------------------------------------------------------ the palette (UpdatePalm, PlacePalette)

        private void UpdatePalmHandOver()
        {
            float now = Time.time;
            var left = LiveHand(Handedness.Left);
            Vector3 palm = default, normal = default;
            bool tracked = left != null && HandIO.TryPalm(left, out palm, out normal);
            switch (PaletteAt)
            {
                case ToolAt.Crew:
                {
                    PaletteOnPalm = false;
                    bool ask = Begun && !PalmPerch.Busy(Handedness.Left) && (editorPalm || (tracked && PaletteAsked(left, palm, normal)));
                    if (!ask) { paletteAskSince = -1f; break; }
                    if (paletteAskSince < 0f)
                    {
                        paletteAskSince = now;
                        if (tracked) Body(Crew.Spark)?.LookAtPoint(palm, 1.5f); // Spark notices the open hand
                    }
                    if (now - paletteAskSince < (tracked && PalmFacesClearly(palm, normal) ? PaletteClearTime : PaletteAskTime)) break;
                    paletteAskSince = -1f;
                    paletteByEditor = editorPalm;
                    PaletteAt = ToolAt.ToHand;
                    paletteFlyAt = now;
                    paletteFlyFrom = CrewToolPoint(Crew.Spark);
                    PaletteHandOvers++;
                    Body(Crew.Spark)?.Play("Decision_Present");
                    ToySounds.Play(ToySounds.Cue.Whoosh, paletteFlyFrom, .3f, 1.5f);
                    WriteInputLog("palette: Spark passes it over", true);
                    break;
                }
                case ToolAt.ToHand:
                    PaletteOnPalm = false;
                    if (now - paletteFlyAt < PaletteFly) break;
                    PaletteAt = ToolAt.Hand;
                    PaletteOnPalm = true;
                    paletteFistSince = paletteLostSince = -1f;
                    ToySounds.Play(ToySounds.Cue.Pop, paletteBoard.position, .35f, 1.3f);
                    WriteInputLog("palette: on the left hand", true);
                    break;
                case ToolAt.Hand:
                    PaletteOnPalm = true;
                    if (paletteByEditor) { if (!editorPalm) SendPaletteBack("put away"); break; }
                    if (PalmPerch.Busy(Handedness.Left)) { SendPaletteBack("a teammate on the palm"); break; }
                    if (!tracked)
                    {
                        if (paletteLostSince < 0f) paletteLostSince = now;
                        if (now - paletteLostSince > (paletteLastLow ? PaletteLowLostTime : PaletteLostTime)) SendPaletteBack(paletteLastLow ? "hand lowered (lost)" : "hand not seen");
                        break;
                    }
                    paletteLostSince = -1f;
                    float below = Head != null ? Head.position.y - palm.y : 0f;
                    paletteLastLow = Head != null && (below > PaletteDropBelow * .7f || Vector3.Angle(Head.forward, palm - Head.position) > 70f);
                    if (Head != null && dragHand != left && below > PaletteDropBelow)
                    {
                        if (paletteDropSince < 0f) paletteDropSince = now;
                        if (now - paletteDropSince >= PaletteDropTime) { paletteDropSince = -1f; SendPaletteBack("hand lowered"); break; }
                    }
                    else paletteDropSince = -1f;
                    // F81: the palm turned down gives it back too. F95: only an open, free hand turned well down.
                    bool down = PalmDownSendsBack ? Vector3.Dot(normal, Vector3.up) < -.35f && dragHand != left
                              : PalmAwaySendsBack && Vector3.Dot(normal, Vector3.up) < PalmAwayUp && OpenFreeLeft(left, palm);
                    if (down) { if (palmDownSince < 0f) palmDownSince = now; if (now - palmDownSince >= (PalmDownSendsBack ? PalmDownTime : PalmAwayTime)) { palmDownSince = -1f; SendPaletteBack("palm down"); break; } }
                    else palmDownSince = -1f;
                    // A closed left hand gives it back (not while that hand works the tape or drags a swatch).
                    if (TapeAt != ToolAt.Crew || dragHand == left || !PaletteFist(left)) { paletteFistSince = -1f; break; }
                    if (paletteFistSince < 0f) paletteFistSince = now;
                    if (now - paletteFistSince >= PaletteFistTime) SendPaletteBack("hand closed");
                    break;
                case ToolAt.ToCrew:
                    PaletteOnPalm = false;
                    if (now - paletteFlyAt >= PaletteFly) PaletteAt = ToolAt.Crew;
                    break;
            }
        }

        // Open left palm, up and toward the face, looked at, holding nothing.
        private bool PaletteAsked(Hand left, Vector3 palm, Vector3 normal)
        {
            if (Head == null || HandIO.Pinching(left) || HeldBy(left) != null || Drawing || TapeAt != ToolAt.Crew) return false;
            if (!HandIO.Joint(left, HandJointId.HandIndexTip, out Pose index) || Vector3.Distance(index.position, palm) <= .065f) return false;
            if (HandIO.Joint(left, HandJointId.HandMiddleTip, out Pose middle) && Vector3.Distance(middle.position, palm) <= .06f) return false; // an L, a point: not an open hand
            Vector3 toEye = (Head.position - palm).normalized;
            float facing = Vector3.Dot(normal, toEye);
            if (Vector3.Dot(normal, Vector3.up) <= PaletteUp || facing <= PaletteFaceEyes) return false;
            float angle = Vector3.Angle(Head.forward, palm - Head.position);
            if (angle <= PaletteLookAngle) return true;
            // F102: a palm turned right at the face, under where the head points, is being looked at (the eyes look down;
            // the head does not follow; no eye tracking). Not one off to the side.
            if (facing < PaletteFaceClear) return false;
            Vector3 local = Quaternion.Inverse(Head.rotation) * (palm - Head.position);
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            return Mathf.Abs(yaw) <= PaletteClearSide && pitch >= -PaletteClearAngle && pitch <= PaletteLookAngle;
        }

        // F102 (Quest 2026-11-16, the user: "I opened my left hand and looked at it and the palette didn't come"): the log
        // had the palm up (.7-.99) and turned to the eyes (.7-.99) for seconds with no palette - the head pointed over the
        // low palm (eyes look down, the head does not), outside PaletteLookAngle. A palm turned clearly to the face
        // (PaletteFaceClear) asks up to PaletteClearAngle under where the head points (and within PaletteClearSide of it
        // sideways), held PaletteClearTime.
        public static float PaletteFaceClear = .8f, PaletteClearAngle = 45f, PaletteClearSide = 25f, PaletteClearTime = .4f;

        private bool PalmFacesClearly(Vector3 palm, Vector3 normal) => Head != null && Vector3.Dot(normal, (Head.position - palm).normalized) >= PaletteFaceClear;

        // The step log's view of the left palm: its angle from the head's forward (F102).
        private float PalmViewAngle(Vector3 palm) => Head != null ? Vector3.Angle(Head.forward, palm - Head.position) : 180f;

        // F95: the left hand open and doing nothing else: not pinching, holding nothing, not in an L frame for a texture,
        // no tape, no swatch dragged, and its fingers out (an L curls the middle finger in).
        private bool OpenFreeLeft(Hand left, Vector3 palm)
        {
            if (left == null || HandIO.Pinching(left) || HeldBy(left) != null || dragHand == left || TapeAt != ToolAt.Crew) return false;
            if (frameSince >= 0f || WaitingForHands || Capturing) return false;
            if (!HandIO.Joint(left, HandJointId.HandIndexTip, out Pose index) || Vector3.Distance(index.position, palm) <= .065f) return false;
            return !HandIO.Joint(left, HandJointId.HandMiddleTip, out Pose middle) || Vector3.Distance(middle.position, palm) > .06f;
        }

        // All four fingers closed (an L or a pinch keeps the index or the others out).
        // F81: measured on all four fingertips on average (the old test wanted every tip within 4.8-6 cm and missed real fists).
        private static bool PaletteFist(Hand hand)
        {
            if (hand == null || !HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm)) return false;
            float sum = 0f, most = 0f;
            foreach (var joint in FistTips)
            {
                if (!HandIO.Joint(hand, joint, out Pose tip)) return false;
                float d = Vector3.Distance(tip.position, palm.position);
                sum += d;
                most = Mathf.Max(most, d);
            }
            return sum / FistTips.Length < PaletteFistReach && most < PaletteFistReach * 1.35f;
        }
        private static readonly HandJointId[] FistTips = { HandJointId.HandIndexTip, HandJointId.HandMiddleTip, HandJointId.HandRingTip, HandJointId.HandPinkyTip };

        private void SendPaletteBack(string why)
        {
            if (PaletteAt != ToolAt.Hand && PaletteAt != ToolAt.ToHand) return;
            PaletteAt = ToolAt.ToCrew;
            PaletteOnPalm = false;
            paletteFlyAt = Time.time;
            paletteFlyFrom = paletteBoard != null ? paletteBoard.position : CrewToolPoint(Crew.Spark);
            paletteFistSince = paletteLostSince = -1f;
            PaletteReturns++;
            LastPaletteReturn = why;
            paletteBackAt = Time.time; // F95: the colouring may be over (MakerSession.ColourDoneAsk.cs)
            CloseWheel(); // F95: the wheel goes with the palette at once (not WheelPalmGrace later)
            ToySounds.Play(ToySounds.Cue.Whoosh, paletteFlyFrom, .3f, 1.2f);
            Body(Crew.Spark)?.Play("React_Nod");
            WriteInputLog("palette: back to Spark (" + why + ")", true);
        }

        private bool PaletteHandPose(out Vector3 pos, out Quaternion rot)
        {
            var left = LiveHand(Handedness.Left);
            if (!paletteByEditor && left != null && HandIO.TryPalm(left, out var palm, out var normal) && HandIO.Joint(left, HandJointId.HandMiddle1, out Pose middle))
            {
                Vector3 fingers = Vector3.ProjectOnPlane(middle.position - palm, normal).normalized;
                pos = palm + normal * .03f;
                rot = Quaternion.LookRotation(-normal, fingers);
                return true;
            }
            if (paletteByEditor)
            {
                pos = FrontPoint(.33f, -.16f, out Vector3 side) - side * .12f;
                Vector3 toEye = Head != null ? (Head.position - pos).normalized : Vector3.back;
                rot = Quaternion.LookRotation(-toEye, Vector3.up);
                return true;
            }
            pos = paletteLastPos;
            rot = paletteLastRot;
            return false;
        }

        private void PlacePaletteHandOver()
        {
            if (PaletteAt == ToolAt.Crew)
            {
                if (paletteBoard.gameObject.activeSelf) paletteBoard.gameObject.SetActive(false);
                return;
            }
            if (PaletteHandPose(out Vector3 pos, out Quaternion rot)) { paletteLastPos = pos; paletteLastRot = rot; }
            else if (paletteLastPos == Vector3.zero) { pos = paletteLastPos = FrontPoint(.33f, -.16f, out _); rot = paletteLastRot; }
            if (!paletteBoard.gameObject.activeSelf)
            {
                paletteBoard.gameObject.SetActive(true);
                paletteBoard.SetPositionAndRotation(paletteFlyFrom, rot);
            }
            if (PaletteAt == ToolAt.Hand)
            {
                float blend = 1f - Mathf.Exp(-Time.deltaTime * 12f);
                paletteBoard.SetPositionAndRotation(Vector3.Lerp(paletteBoard.position, pos, blend), Quaternion.Slerp(paletteBoard.rotation, rot, blend));
                paletteBoard.localScale = Vector3.one;
                return;
            }
            bool toHand = PaletteAt == ToolAt.ToHand;
            float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - paletteFlyAt) / PaletteFly));
            Vector3 to = toHand ? pos : CrewToolPoint(Crew.Spark);
            Vector3 at = Vector3.Lerp(paletteFlyFrom, to, e) + Vector3.up * (Mathf.Sin(e * Mathf.PI) * .06f);
            paletteBoard.SetPositionAndRotation(at, toHand ? Quaternion.Slerp(SpeechBubbleDock.FacingAt(at), rot, e) : Quaternion.Slerp(paletteBoard.rotation, SpeechBubbleDock.FacingAt(at), e));
            paletteBoard.localScale = Vector3.one * (toHand ? Mathf.Lerp(MiniPalette, 1f, e) : Mathf.Lerp(1f, MiniPalette, e));
        }

        // ------------------------------------------------------------ the tape (UpdateTape, UpdateFreeTape)

        // The hands' pinches as the tape sees them: none until Fixer's tape is in the hands.
        private void TapeGate(Hand left, Hand right, ref bool p0, ref bool p1, Vector3 a0, Vector3 a1)
        {
            if (!HandRules) return;
            float now = Time.time;
            if (p0 && !tapeWasL) tapePinchL = now;
            if (p1 && !tapeWasR) tapePinchR = now;
            tapeWasL = p0;
            tapeWasR = p1;
            switch (TapeAt)
            {
                case ToolAt.Crew:
                    if (p0 && p1 && left != null && right != null && Mathf.Abs(tapePinchL - tapePinchR) <= TapeTogether &&
                        Vector3.Distance(a0, a1) <= TapeStartGap && HeldBy(left) == null && HeldBy(right) == null && !Drawing)
                    {
                        TapeAt = ToolAt.ToHand;
                        tapeFlyAt = now;
                        tapeFlyFrom = CrewToolPoint(Crew.Fixer);
                        tapeLastPos = (a0 + a1) * .5f;
                        TapeHandOvers++;
                        farHand = null; // an aimed pinch that was waiting was the tape's
                        Body(Crew.Fixer)?.Play("Decision_Present");
                        ToySounds.Play(ToySounds.Cue.Whoosh, tapeFlyFrom, .3f, 1.8f);
                        WriteInputLog("tape: Fixer tosses it over", true);
                    }
                    p0 = p1 = false;
                    break;
                case ToolAt.ToHand:
                    if (now - tapeFlyAt < TapeFly) { p0 = p1 = false; break; }
                    TapeAt = ToolAt.Hand;
                    tapeIdleSince = -1f;
                    ToySounds.Play(ToySounds.Cue.Pop, tapeLastPos, .3f, 1.5f);
                    WriteInputLog("tape: in the hands", true);
                    break;
                case ToolAt.ToCrew:
                    p0 = p1 = false;
                    break;
            }
        }

        // After the tape step: the tape in the hands goes back to Fixer when it is not used any more.
        private void HandOverFrame()
        {
            FarAimFrame();
            if (!HandRules) return;
            float now = Time.time;
            if (TapeAt == ToolAt.ToCrew && now - tapeFlyAt >= TapeFly) TapeAt = ToolAt.Crew;
            if (TapeAt != ToolAt.Hand) return;
            bool used = Current == Step.Measuring || freeTape || Tape.State == HandTape.Mode.TwoHands || tapeDemo;
            if (used) { tapeIdleSince = -1f; return; }
            if (tapeIdleSince < 0f) tapeIdleSince = now;
            if (now - tapeIdleSince < TapeBackAfter) return;
            TapeAt = ToolAt.ToCrew;
            tapeFlyAt = now;
            tapeFlyFrom = fixerTape != null ? fixerTape.position : tapeLastPos;
            TapeReturns++;
            ToySounds.Play(ToySounds.Cue.Whoosh, tapeFlyFrom, .25f, 1.4f);
            WriteInputLog("tape: back to Fixer", true);
        }

        // ------------------------------------------------------------ the props the crew carries (LateUpdate)

        // Where a teammate carries its tool: its left mitten (as the clip holds it now), else beside it.
        private Vector3 CrewToolPoint(Crew crew)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null) return FrontPoint(.4f, -.1f, out _);
            Vector3 root = c.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - root, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
            var body = Body(crew);
            var hand = body != null ? body.RigHand(false) : null;
            if (hand != null) return hand.position + Vector3.up * .014f + toUser * .012f;
            return root + Vector3.up * .06f + Vector3.Cross(Vector3.up, toUser) * .05f;
        }

        // F67 (Quest: "Spark holding the palette and Fixer the tape looks odd - hide them normally, show them when I come
        // close, and not on their own belly but naturally"): the carried tool is put away; it comes out while a hand is
        // near that teammate (the F55 tool reveal: ToolShown) or at the step that needs it (Finish: the palette,
        // Measuring: the tape). Out, the teammate holds it up in its left mitten beside its body (WindupBody.Hold; the
        // F55 tool takes the right one), the tool popping out of the mitten.
        public static float CarryOutSide = .045f, CarryOutUp = .075f, CarryOutFront = .022f, CarryPop = .18f;
        private readonly Dictionary<Crew, float> carryShownAt = new Dictionary<Crew, float>();
        private readonly HashSet<Crew> carryHolding = new HashSet<Crew>();

        private bool CarryWanted(Crew crew, bool needStep) => CarryFree(crew, false) && (needStep || ToolShown(crew));
        private bool LessonWaiting(string name) => CurrentLesson != null && CurrentLesson.name == name;

        private void HandOverLate()
        {
            if (!HandRules || !Begun) { HideCarried(sparkPalette); HideCarried(fixerTape); LetCarryGo(Crew.Spark); LetCarryGo(Crew.Fixer); return; }
            // Spark's little palette: only while the real one is not out, and only when wanted.
            // (F84: also while a texture framed without the palette flies into it, SparkPaletteUntil.)
            bool palette = PaletteAt == ToolAt.Crew && CarryWanted(Crew.Spark, (Current == Step.Finish && LessonWaiting("palm up")) || Time.time < sparkPaletteUntil);
            if (palette && sparkPalette == null) sparkPalette = BuildCarried("palette");
            if (sparkPalette != null)
            {
                if (sparkPalette.gameObject.activeSelf != palette) sparkPalette.gameObject.SetActive(palette);
                if (palette) PlaceCarried(sparkPalette, Crew.Spark, Current == Step.Finish, -35f);
            }
            if (!palette) LetCarryGo(Crew.Spark);
            // Fixer's tape: held out by Fixer when wanted, flying, or under the tape's left end in the hands.
            bool onFixer = TapeAt == ToolAt.Crew && CarryWanted(Crew.Fixer, Current == Step.Measuring);
            bool tape = TapeAt != ToolAt.Crew || onFixer;
            if (!onFixer) LetCarryGo(Crew.Fixer);
            if (tape && fixerTape == null) fixerTape = BuildCarried("tape");
            if (fixerTape == null) return;
            if (fixerTape.gameObject.activeSelf != tape) fixerTape.gameObject.SetActive(tape);
            if (!tape) return;
            if (TapeAt == ToolAt.Crew) { PlaceCarried(fixerTape, Crew.Fixer, Current == Step.Measuring, 0f); return; }
            Vector3 inHand = TapeHandPoint();
            if (TapeAt == ToolAt.Hand)
            {
                fixerTape.SetPositionAndRotation(inHand, SpeechBubbleDock.FacingAt(inHand));
                fixerTape.localScale = Vector3.one * 1.25f;
                return;
            }
            bool toHand = TapeAt == ToolAt.ToHand;
            float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - tapeFlyAt) / TapeFly));
            Vector3 to = toHand ? inHand : CrewToolPoint(Crew.Fixer);
            Vector3 at = Vector3.Lerp(tapeFlyFrom, to, e) + Vector3.up * (Mathf.Sin(e * Mathf.PI) * .05f);
            fixerTape.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at) * Quaternion.Euler(0f, 0f, (toHand ? e : 1f - e) * 360f));
            fixerTape.localScale = Vector3.one * (toHand ? Mathf.Lerp(1f, 1.25f, e) : Mathf.Lerp(1.25f, 1f, e));
        }

        private static void HideCarried(Transform prop)
        {
            if (prop != null && prop.gameObject.activeSelf) prop.gameObject.SetActive(false);
        }

        // The teammate can be seen carrying its tool now (not in the introduction's own show, not while its F55 tool
        // is up in the same mitten).
        private bool CarryFree(Crew crew, bool hideForTool)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null || !c.gameObject.activeInHierarchy) return false;
            if (introProp != null) return false;
            var body = Body(crew);
            if (body == null || body.IntroActive) return false;
            return !hideForTool || !ToolShown(crew);
        }

        private void PlaceCarried(Transform prop, Crew crew, bool offering, float tilt)
        {
            Vector3 root = cast[crew].transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - root, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
            Vector3 right = Vector3.Cross(Vector3.up, toUser); // the teammate's left as the person sees it is +right
            var body = Body(crew);
            // F67: held up in the left mitten, beside the body (its left side, a little toward the person).
            Vector3 side = body != null ? -body.transform.right : right;
            Vector3 mitten = root + Vector3.up * CarryOutUp + Vector3.ProjectOnPlane(side, Vector3.up).normalized * CarryOutSide + toUser * CarryOutFront;
            if (offering)
            {
                // Held out now and then at the step that needs it (the teammate acting, no glow).
                float wave = Mathf.Max(0f, Mathf.Sin(Time.time * 2.4f));
                wave *= wave;
                mitten += toUser * (.012f * wave) + Vector3.up * (.008f * wave);
            }
            if (body != null) { body.Hold(false, new Pose(mitten, body.NaturalGrip(false, mitten + toUser, 50f))); carryHolding.Add(crew); }
            if (!carryShownAt.ContainsKey(crew)) carryShownAt[crew] = Time.time;
            float pop = Mathf.Clamp01((Time.time - carryShownAt[crew]) / CarryPop);
            Vector3 at = mitten + Vector3.up * .014f + toUser * .008f;
            prop.SetPositionAndRotation(at, Quaternion.AngleAxis(tilt, right) * Quaternion.LookRotation(-toUser, Vector3.up));
            prop.localScale = Vector3.one * Mathf.Max(.01f, Overshoot(pop));
        }

        // The mitten that held the tool goes back to the clip (only if this put it there).
        private void LetCarryGo(Crew crew)
        {
            carryShownAt.Remove(crew);
            if (!carryHolding.Remove(crew)) return;
            Body(crew)?.Hold(false, null);
        }

        // Under the tape's left end while it measures, else at the left hand's pinch.
        private Vector3 TapeHandPoint()
        {
            if (Tape.State == HandTape.Mode.TwoHands || Tape.State == HandTape.Mode.Measured || freeTape)
                tapeLastPos = Tape.A - Vector3.up * .022f;
            else
            {
                var left = LiveHand(Handedness.Left);
                if (left != null) tapeLastPos = PinchPoint(left) - Vector3.up * .022f;
            }
            return tapeLastPos;
        }

        private Transform BuildCarried(string tool)
        {
            var prop = new GameObject("Carried " + tool).transform;
            prop.SetParent(transform, false);
            Transform Piece(string name, Mesh mesh, Vector3 local)
            {
                owned.Add(mesh);
                var t = Part(name, mesh);
                t.SetParent(prop, false);
                t.localPosition = local;
                t.localRotation = Quaternion.identity;
                return t;
            }
            if (tool == "tape")
            {
                Color yellow = new Color(1f, .8f, .16f), dark = new Color(.82f, .6f, .06f), steel = new Color(.85f, .86f, .9f);
                Piece("Case", ToyMeshes.RoundedSlab(.026f, .026f, .008f, .012f, yellow, dark, yellow, "CarriedTapeCase"), Vector3.zero);
                Piece("Case Dot", ToyMeshes.Disc(.006f, .002f, dark, dark, "CarriedTapeDot"), new Vector3(0f, 0f, -.0065f));
                Piece("Blade", ToyMeshes.RoundedSlab(.01f, .005f, .001f, .0012f, yellow, dark, yellow, "CarriedTapeBlade"), new Vector3(-.017f, -.004f, 0f));
                Piece("Hook", ToyMeshes.RoundedSlab(.003f, .008f, .001f, .003f, steel, steel, steel, "CarriedTapeHook"), new Vector3(-.024f, -.004f, 0f));
            }
            else if (PaletteModelPiece(prop, MiniPalette, true) != null) { } // 2026-11-15: the designed palette, small, its own paints
            else
            {
                Color wood = new Color(.86f, .68f, .45f), woodEdge = new Color(.68f, .5f, .3f);
                var board = Piece("Board", ToyMeshes.Disc(.068f * MiniPalette, .003f, wood, woodEdge, "CarriedPalette"), Vector3.zero);
                board.localScale = new Vector3(1.25f, 1f, 1f);
                Piece("Blob A", ToyMeshes.Disc(.0052f, .003f, new Color(.89f, .34f, .3f), new Color(.7f, .25f, .22f), "CarriedBlob"), new Vector3(-.015f, .008f, -.003f));
                Piece("Blob B", ToyMeshes.Disc(.0052f, .003f, new Color(.36f, .69f, .85f), new Color(.25f, .5f, .65f), "CarriedBlob"), new Vector3(-.003f, .013f, -.003f));
                Piece("Blob C", ToyMeshes.Disc(.0052f, .003f, new Color(.95f, .7f, .24f), new Color(.75f, .52f, .14f), "CarriedBlob"), new Vector3(.011f, .009f, -.003f));
            }
            return prop;
        }

        // Tests.
        public void SimulatePaletteGiven() { if (PaletteAt == ToolAt.Crew) { EnsurePalette(); PaletteAt = ToolAt.Hand; PaletteOnPalm = true; paletteFistSince = paletteLostSince = -1f; } }
        public void SimulateTapeGiven() { TapeAt = ToolAt.Hand; tapeIdleSince = -1f; }
    }
}
