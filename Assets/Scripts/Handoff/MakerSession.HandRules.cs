using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F66 (2026-11-13, the user after the Quest tries: "near grab, distance grab and everything else are mixed up; I
    // want to move the mic but its button keeps getting pressed; it is all too messy"). Three rules for the hands:
    //   a pinch takes hold of what is within reach, a fingertip presses a button, and a pinch aimed from afar presses
    //   too - it moves a thing only when it was aimed at on purpose.
    //  - Near wins: with something to take within reach of the pinch, the hand's ray does nothing at all.
    //  - Moving from afar is strict: only big things (a made thing or its stand, Spark's frame, the open notebook, the
    //    sketchbook, a note on the wall, a teammate), only after the hand has pointed at that one thing for
    //    FarAimDwell (the cursor fills in), and it moves only once the pinch has been held FarHold. A shorter pinch
    //    moves nothing. Small things (shape cards, flags, size knobs) are taken near only.
    //  - A hand with a tool does not move things from afar: the painting hand, and the right hand while the palette is
    //    in the left (its free pinches pick colours, and only when nothing else took the pinch).
    //  - The mic: MayGrabMic / the "mic" entry of HeldBy (WindupsArrival does the rest).
    //  - Tools come from the crew (MakerSession.HandOver.cs); name tags show only when looked at or approached.
    // HandRules = false keeps the hands as they were before (the older Editor checks set it; the app never does).
    public sealed partial class MakerSession
    {
        public static bool HandRules = true;
        // F67: the pinch that takes it from afar no longer has to be held (FarHold .3 -> .08 s): aim until the ring fills,
        // pinch, and a made thing flies into the hand (Grab -> FlyToHand), then it is carried grab & release.
        // F101: the ring only has to rest on the thing a moment (.15 s, was .3): the ring and the ray now show where the
        // hand aims before the pinch, as on the Quest, so a pinch on a ringed thing is meant.
        public static float FarAimDwell = .15f, FarHold = .08f;

        // F101: the short ray from a hand that aims at nothing yet (RemoteGrab draws it): a free hand, nothing within
        // reach, not drawing, colouring, measuring or framing a texture.
        public bool FarStubAllowed(Hand hand) =>
            HandRules && hand != null && !Collecting && HeldBy(hand) == null && !Drawing && !Capturing && frameSince < 0f && !WaitingForHands &&
            TapeAt == ToolAt.Crew && !(brushSwatch != null && hand == brushHand) && !FarQuiet(hand) && !NearInReach(hand, PinchPoint(hand));

        public int FarIgnored { get; private set; }   // aimed pinches that moved nothing (not aimed long enough, a small thing, a tool hand)
        public int FarShort { get; private set; }     // let go before FarHold
        public int FarMoves { get; private set; }     // far moves that began
        public string LastFar { get; private set; } = "";

        private enum FarKind { None, Tap, Small, Movable }
        private sealed class FarAimState { public FarKind kind; public int key, frame; public float distance, since; public Ray ray; }
        private readonly Dictionary<Hand, FarAimState> aims = new Dictionary<Hand, FarAimState>();
        private Hand farHand;
        private int farKey;
        private float farAt;
        private Ray farRay;

        // ------------------------------------------------------------ the mic

        // A pinch by the mic may pick it up: a hand that holds nothing, and nothing of the session nearer.
        public bool MayGrabMic(Hand hand, Vector3 point)
        {
            if (!Begun || arrival == null) return true;
            if (HeldBy(hand) != null) return false;
            float mic = arrival.MicPressDistance(point, out _);
            return NearestGrab(point, out float d, out _) == GrabKind.None || d >= mic;
        }

        // ------------------------------------------------------------ what a hand's ray is on

        // The first thing on the ray, in the order OnPinch would give it the pinch.
        private FarKind FarAim(Ray ray, out int key, out float distance)
        {
            key = 0;
            distance = 0f;
            // F67: the introduction's < > arrows are pressed from afar too.
            if (PagerShown && PagerPinch(Vector3.positiveInfinity, ray) != 0 && new Plane(pagerRoot.forward, pagerRoot.position).Raycast(ray, out float pd)) { distance = pd; return FarKind.Tap; }
            // F68: the reply pills, Find ideas, settings, stickers, the v2 review pictures and the page's buttons too.
            if (FarButtonOnRay(ray, out distance)) return FarKind.Tap;
            foreach (var crew in ToolOrder)
                if (ToolTarget(crew, out var tool, out float reach) && RayDistance(ray, tool) <= reach * .9f && Vector3.Distance(ray.origin, tool) >= .12f)
                { distance = Vector3.Distance(ray.origin, tool); return FarKind.Tap; }
            if (SketchbookOnRay(ray, out distance)) { key = 1; return FarKind.Movable; }
            int note = WallNoteOnRay(ray, out distance);
            if (note >= 0) { if (!WallFarGrab) return FarKind.Small; key = 100 + note; return FarKind.Movable; } // F81: wall papers near only
            if (NotebookOnRay(ray, out distance))
            {
                if (!NotebookOpen) return FarKind.Tap;
                Vector3 l = notebookView.InverseTransformPoint(ray.GetPoint(distance));
                if (Mathf.Abs(l.x) <= .145f && Mathf.Abs(l.y) <= .093f) return FarKind.Tap; // its buttons and pages
                key = 2;
                return FarKind.Movable;
            }
            // F102: a flag is tapped from afar (it hops into v2); the knobs stay near only.
            if (FlagTaps && FlagNear(Vector3.positiveInfinity, ray) is Flag farFlag)
            { distance = Mathf.Max(.1f, Vector3.Dot(farFlag.root.TransformPoint(new Vector3(.0125f, .042f, 0f)) - ray.origin, ray.direction)); return FarKind.Tap; }
            if (KnobNear(Vector3.positiveInfinity, ray) >= 0 || FlagNear(Vector3.positiveInfinity, ray) != null) { distance = .5f; return FarKind.Small; }
            if (PinNear(Vector3.positiveInfinity, ray, out var pinView) != null)
            {
                distance = new Plane(pinView.forward, pinView.position).Raycast(ray, out float pinD) ? pinD : .5f;
                if (!NoteOnWall) return FarKind.Tap;
                if (!WallFarGrab) return FarKind.Small; // F81: wall papers near only
                key = 300 + PinViews.IndexOf(pinView); // F72: a picture on the wall is moved (grab & release)
                return FarKind.Movable;
            }
            if (PictureOnRay(ray, out distance) != null || ChoiceOnRay(ray, out distance) != null) return FarKind.Tap;
            if (ShapeDoneOnRay(ray, out distance)) return FarKind.Tap; // F67: the Done pill beside the cards is pressed
            if (ChosenOnRay(ray, out distance) != null) { key = 4; return FarKind.Movable; } // F67: the chosen picture
            if (CardOnRay(ray, out distance) != null) return FarKind.Small;
            if (FrameOnRay(ray, out distance))
            {
                if (FrameButtonHit(Vector3.positiveInfinity, ray)) return FarKind.Tap;
                key = 3;
                return FarKind.Movable;
            }
            var made = MadeOnRay(ray, out distance);
            if (made != null && distance > RemoteGrab.NearLimit) { key = 1000 + (made.GetInstanceID() & 0xFFFFF); return FarKind.Movable; }
            distance = 0f;
            return FarKind.None;
        }

        private int WallNoteOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (board == null || carriedNote != null) return -1;
            int pick = -1;
            float best = float.MaxValue;
            // F72: the notes the person moved and the current note are carried too.
            int total = wallViews.Count + looseNotes.Count + (NoteOnWall && ringNote != null ? 1 : 0);
            for (int i = 0; i < total; i++)
            {
                var v = i < wallViews.Count ? wallViews[i] : i < wallViews.Count + looseNotes.Count ? looseNotes[i - wallViews.Count] : ringNote;
                if (v == null) continue;
                if (!new Plane(v.forward, v.position).Raycast(ray, out float d) || d < .2f || d > 3.5f || d > best) continue;
                Vector3 local = v.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(local.x) > WallSheet * .65f || Mathf.Abs(local.y) > WallSheet * .65f) continue;
                best = d;
                pick = i;
            }
            if (pick >= 0) distance = best;
            return pick;
        }

        // Every frame: what each open hand points at, and since when (a pinching hand keeps what it aimed at).
        // F82 (Quest: "far grab/touch should not come from pointing - only when my index and thumb come together, almost
        // pinching"): the hand's ray, its ring and far aiming are on only while the thumb and index tips are within
        // FarPrePinch (or pinching). An open or pointing hand has no ray.
        // F89 (Quest: the almost-pinch with the fingers a little apart often read as open): on under FarPrePinch, and once
        // on it stays on until the tips are FarPrePinchOff apart (no flicker at the edge).
        // F98 (Quest 2026-11-15, the user: "the distance-grab hand ray shows far too much, and it's hard"): the log had
        // both hands "almost pinching" in every frame (1442 of 1442 per 10 s): a relaxed hand's thumb and index sit
        // 3.7-4.4 cm apart, inside the 5.5 cm. Now on under 2.5 cm and off past 3.5 cm: only fingers really closing.
        // F99 (the user: "now it hardly shows - make it natural"): also a naturally curled hand (HandIO.CurlOn 4.5 cm)
        // pointing where the person looks (HandIO.GazeOn 30 deg) has the ray (HandIO.AimIntent).
        public static float FarPrePinch = .025f, FarPrePinchOff = .035f;
        public static bool NearlyPinching(Hand hand)
        {
            if (hand == null) return false;
            if (HandIO.Pinching(hand)) return true;
            return HandIO.AimIntent(hand, FarPrePinch, FarPrePinchOff, out _, out _);
        }

        private void FarAimFrame()
        {
            if (!HandRules) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null) continue;
                if (!aims.TryGetValue(hand, out var aim)) aims[hand] = aim = new FarAimState();
                if (!hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Pointer(hand, out Pose pointer)) { aim.kind = FarKind.None; aim.key = 0; continue; }
                if (HandIO.Pinching(hand)) continue;
                if (FarQuiet(hand)) { aim.kind = FarKind.None; aim.key = 0; continue; } // F101: a hand at work near has no far aim
                if (!NearlyPinching(hand)) { aim.kind = FarKind.None; aim.key = 0; continue; } // F82: aiming starts as the fingers close
                var ray = new Ray(pointer.position, pointer.forward);
                var kind = FarAim(ray, out int key, out float distance);
                if (kind != aim.kind || key != aim.key) aim.since = Time.time;
                aim.kind = kind;
                aim.key = key;
                aim.distance = distance;
                aim.ray = ray;
                aim.frame = Time.frameCount;
            }
        }

        // RemoteGrab's cursor: is this hand's ray on something of the session, how far, and may a pinch act on it now
        // (a thing to move: only once it has been aimed at for FarAimDwell).
        public bool FarCursor(Hand hand, out float distance, out bool ready)
        {
            distance = 0f;
            ready = false;
            if (Collecting && !Begun) return CollectCursor(hand, out distance, out ready); // F78 airplane mode
            // F78, back online: the collected things shown in front of the person (WindupsCollect.ShowOnline).
            if (CollectedOnRay != null && hand != null && HandIO.Pointer(hand, out Pose collectPointer) && !(HeldBy(hand) != null))
            {
                float d = CollectedOnRay(new Ray(collectPointer.position, collectPointer.forward));
                if (d >= 0f && (!aims.TryGetValue(hand, out var other) || other.kind == FarKind.None || d < other.distance)) { distance = d; ready = true; return true; }
            }
            if (hand == null || !aims.TryGetValue(hand, out var aim) || aim.kind == FarKind.None || aim.kind == FarKind.Small) return false;
            if (NearInReach(hand, PinchPoint(hand)) || HeldBy(hand) != null) return false;
            if (aim.kind == FarKind.Movable && ToolHand(hand)) return false;
            distance = aim.distance;
            ready = aim.kind == FarKind.Tap || Time.time - aim.since >= FarAimDwell;
            return true;
        }

        // F103: the far aim of this hand is on a button (RemoteGrab then shows the ring there even while the ray is quiet).
        public static bool QuietButtonRing = true;
        public bool FarCursorOnButton(Hand hand)
        {
            if (!QuietButtonRing || hand == null) return false;
            if (aims.TryGetValue(hand, out var aim) && Time.frameCount - aim.frame <= 3) return aim.kind == FarKind.Tap;
            return AimLockPoint(hand, out _, out bool button) && button;
        }

        // Something to take is within reach of this pinch (then the hand's ray does nothing).
        private bool NearInReach(Hand hand, Vector3 point)
        {
            if (float.IsInfinity(point.x)) return false;
            if (NearestGrab(point, out _, out _) != GrabKind.None) return true;
            if (arrival != null && arrival.MicShown && !arrival.MicHeld && arrival.MicNear(point)) return true;
            if (MadeNear(point) != null || SwatchSlotNear(point) >= 0 || FlagNear(point, null) != null || KnobNear(point, null) >= 0) return true;
            foreach (var pair in cast)
            {
                if (pair.Value == null || pair.Value.movement == null) continue;
                Vector3 feet = pair.Value.movement.transform.position;
                float up = Mathf.Clamp(point.y - feet.y, .03f, .12f);
                if (Vector3.Distance(point, feet + Vector3.up * up) < .075f) return true;
            }
            return false;
        }

        // A hand that is using a tool: it paints, or it is the right hand while the palette is in the left.
        private bool ColourHand(Hand hand) => hand != null && PaletteOnPalm && hand.Handedness == Handedness.Right;
        private bool ToolHand(Hand hand) => hand != null && ((brushSwatch != null && hand == brushHand) || ColourHand(hand) || TapeAt == ToolAt.Hand || TapeAt == ToolAt.ToHand);

        // A teammate may be dragged from afar by this hand now (RemoteGrab).
        // F81 (the user: "no distance grab for the papers on the wall; none for the characters either"): both are taken near
        // only; WallFarGrab / CrewFarGrab bring the far grabs back.
        // F87 (Quest 2026-11-15: "can't I take the things on the wall off?" - the wall was out of reach): wall notes and
        // pictures are taken from afar again, still only through F82's near-pinch ray, the aim dwell and FarHold.
        public static bool WallFarGrab = true, CrewFarGrab;
        public bool FarCrewAllowed(Hand hand, Vector3 point) =>
            CrewFarGrab && !Collecting && // F78: on the sign (no room) a teammate pulled off from afar would hang in the air
            (!HandRules || !Begun || (HeldBy(hand) == null && !ToolHand(hand) && !NearInReach(hand, point) && farHand != hand));

        // ------------------------------------------------------------ a new pinch

        // Before OnPinch: `ray` is taken away when the hand's ray must do nothing; false when the pinch waits (a far
        // move begins only once it has been held FarHold, FarPendingFrame).
        private bool FarGate(Hand hand, Vector3 point, ref Ray? ray)
        {
            farGateNote = "";
            if (!HandRules || !ray.HasValue || hand == null) return true;
            if (NearInReach(hand, point)) { ray = null; farGateNote = "far off: something within reach"; return true; }
            if (FarQuiet(hand)) { ray = null; farGateNote = "far off: hand at work (" + LastWorkWhy + ")"; return true; } // F101: a pinch by a hand at work near presses nothing far away
            // What the hand pointed at just before the fingers closed (closing them shifts the ray), and what the ray is
            // on now.
            bool tracked = aims.TryGetValue(hand, out var aim) && Time.frameCount - aim.frame <= 3;
            var now = FarAim(ray.Value, out int nowKey, out _);
            bool steady = tracked && aim.kind == FarKind.Movable && Time.time - aim.since >= FarAimDwell;
            if (!steady)
            {
                if (now == FarKind.Small || (tracked && aim.kind == FarKind.Small && now != FarKind.Tap)) { FarOff(hand, "a small thing is taken near only"); ray = null; }
                else if (now == FarKind.Movable) { FarOff(hand, ToolHand(hand) ? "a hand with a tool moves nothing from afar" : "not aimed at it long enough"); ray = null; }
                return true; // a tap, or nothing of the session on the ray
            }
            if (now == FarKind.Tap) return true; // the ray went onto a button meanwhile: the tap
            if (ToolHand(hand)) { FarOff(hand, "a hand with a tool moves nothing from afar"); ray = null; return true; }
            farHand = hand;
            farKey = aim.key;
            farAt = Time.time;
            farRay = now == FarKind.Movable && nowKey == aim.key ? ray.Value : aim.ray;
            return false;
        }

        private void FarOff(Hand hand, string why)
        {
            FarIgnored++;
            LastFar = why;
            WriteInputLog("far (" + hand.Handedness + "): " + why, true);
        }

        // The hand whose aimed pinch is waiting: true while it waits (nothing else happens with that hand).
        private bool FarPendingFrame(Hand hand, bool pinching, Vector3 point)
        {
            if (farHand != hand) return false;
            if (!pinching)
            {
                farHand = null;
                FarShort++;
                LastFar = "let go before it moved";
                WriteInputLog("far (" + hand.Handedness + "): a short pinch moves nothing", true);
                return true;
            }
            if (Time.time - farAt < FarHold) return true;
            farHand = null;
            Ray? now = HandIO.Pointer(hand, out Pose pointer) ? new Ray(pointer.position, pointer.forward) : (Ray?)null;
            if (!now.HasValue || FarAim(now.Value, out int key, out _) != FarKind.Movable || key != farKey)
            {
                // The ray slipped while the fingers closed: the ray the pinch began with, if the thing is still on it.
                now = FarAim(farRay, out key, out _) == FarKind.Movable && key == farKey ? farRay : (Ray?)null;
            }
            if (!now.HasValue) { FarOff(hand, "moved off it"); return true; }
            FarMoves++;
            LastFar = "moved";
            try { OnPinch(hand, point, now); }
            catch (System.Exception exception) { LogStepError("pinch (far)", exception); }
            LogPinch(hand, point);
            return true;
        }

        // ------------------------------------------------------------ name tags

        public static float TagLookAngle = 10f, TagLookDwell = .3f, TagNearHand = .16f, TagNearHead = .45f, TagLinger = 1f;
        private readonly Dictionary<Crew, float> tagLookSince = new Dictionary<Crew, float>(), tagWantedUntil = new Dictionary<Crew, float>();

        // (the user: "the name tag should come when I look at one of them, or go close") Asked at each TagClear tick.
        private bool TagWanted(Crew crew)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null || c.movement == null || Head == null) return false;
            float now = Time.time;
            Vector3 middle = ToolMiddle(c);
            bool want = Vector3.Distance(Head.position, middle) < TagNearHead;
            if (!want)
                foreach (var hand in HandSet.Both())
                    if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip) &&
                        Vector3.Distance(tip.position, middle) < TagNearHand) { want = true; break; }
            if (Vector3.Angle(Head.forward, middle - Head.position) <= TagLookAngle)
            {
                if (!tagLookSince.TryGetValue(crew, out float since)) tagLookSince[crew] = since = now;
                if (now - since >= TagLookDwell) want = true;
            }
            else tagLookSince.Remove(crew);
            if (want) tagWantedUntil[crew] = now + TagLinger;
            return tagWantedUntil.TryGetValue(crew, out float until) && now < until;
        }

        // Tests.
        public bool FarPending => farHand != null;
        public bool FarAimReady(bool left)
        {
            var hand = HandSet.Side(left ? Handedness.Left : Handedness.Right);
            return hand != null && aims.TryGetValue(hand, out var aim) && aim.kind == FarKind.Movable && Time.time - aim.since >= FarAimDwell;
        }
        public string FarAimKind(bool left)
        {
            var hand = HandSet.Side(left ? Handedness.Left : Handedness.Right);
            return hand != null && aims.TryGetValue(hand, out var aim) ? aim.kind.ToString() : "None";
        }
        public bool TagLookedAt(Crew crew) => tagWantedUntil.TryGetValue(crew, out float until) && Time.time < until;
    }
}
