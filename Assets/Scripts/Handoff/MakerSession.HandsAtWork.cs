using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F101 (Quest 2026-11-15, the user: "the far ray must not get in the way while I interact with other things... and
    // while I work the characters keep coming near my hands and odd things get pressed - I hate that"). A hand at work
    // (holding something, painting, drawing, picking a colour, framing a texture, measuring, or right by a thing of the
    // session) is left alone, and for a moment after:
    //  - no far ray, ring, snap or far press from it for FarQuietAfter (a missed near pinch used to press whatever
    //    button the ray happened to cross);
    //  - no teammate's tool comes out or is pressed, and no teammate is grabbed, while a hand works and CrewQuietAfter
    //    after; a teammate that walks up to a hand shows no tool until that hand has left it;
    //  - teammates choosing a new place to stand keep HandRoom clear of the hands that worked in the last HandRoomFor
    //    seconds (the F51 map, as a real thing: avoided by new spots, nobody is sent away for it), and nobody walks
    //    over to be looked at (F76) while a hand works.
    // Once a frame, no allocation.
    public sealed partial class MakerSession
    {
        public const float PageWorkReach = .22f;
        public static float FarQuietAfter = 1.2f, CrewQuietAfter = 1.5f, HandRoom = .14f, HandRoomFor = 4f;
        public static bool PaletteQuietsOneHand = true; // F102
        private readonly Dictionary<Hand, float> workedAt = new Dictionary<Hand, float>();
        private readonly Dictionary<Hand, Vector3> workedPalm = new Dictionary<Hand, Vector3>();
        private int handsWorkFrame = -1;
        private float anyWorkedAt = -100f;

        // Is this hand doing something with the session's things right now?
        public string LastWorkWhy { get; private set; } = "";
        private bool HandAtWork(Hand hand, Vector3 point)
        {
            string held = HeldBy(hand);
            if (held != null) { LastWorkWhy = held; return true; }
            if (brushSwatch != null && hand == brushHand) { LastWorkWhy = "painting"; return true; }
            // (Only a hand at the page: from further off the page's check and undo are still pressed with the ray, F68b.)
            if (Drawing && paper != null && !float.IsInfinity(point.x) && Vector3.Distance(point, paper.position) < PageWorkReach) { LastWorkWhy = "drawing"; return true; }
            if (Capturing || WaitingForHands || frameSince >= 0f) { LastWorkWhy = "colour or texture"; return true; }
            if (TapeAt == ToolAt.Hand || TapeAt == ToolAt.ToHand) { LastWorkWhy = "tape"; return true; }
            // F102: only the hand the palette is on (the right hand works when it paints, dips or picks: brush, capture,
            // a swatch by it); it used to silence the right hand's far ray for the whole colouring step.
            if (PaletteOnPalm && (!PaletteQuietsOneHand || hand.Handedness == Handedness.Left)) { LastWorkWhy = "palette"; return true; }
            if (float.IsInfinity(point.x)) return false;
            if (NearestGrab(point, out _, out _) != GrabKind.None) { LastWorkWhy = "by a thing"; return true; }
            if (SwatchSlotNear(point) >= 0 || FlagNear(point, null) != null || KnobNear(point, null) >= 0) { LastWorkWhy = "by a small thing"; return true; }
            return false;
        }

        private void HandsAtWorkFrame()
        {
            if (handsWorkFrame == Time.frameCount) return;
            handsWorkFrame = Time.frameCount;
            if (!Begun) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                if (!HandAtWork(hand, PinchPoint(hand))) continue;
                workedAt[hand] = Time.time;
                anyWorkedAt = Time.time;
                if (HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm)) workedPalm[hand] = palm.position;
            }
        }

        public bool HandWorkedWithin(Hand hand, float seconds)
        {
            HandsAtWorkFrame();
            return hand != null && workedAt.TryGetValue(hand, out float at) && Time.time - at < seconds;
        }

        public bool AnyHandWorkedWithin(float seconds)
        {
            HandsAtWorkFrame();
            return Time.time - anyWorkedAt < seconds;
        }

        // The far ray keeps away from a hand that works near (and a moment after).
        public bool FarQuiet(Hand hand) => Begun && HandWorkedWithin(hand, FarQuietAfter);

        // The hands that worked lately, for the map of what stands where (MakerSession.Clearance.cs).
        private void AddHandRoom(List<Foot> feet)
        {
            if (!Begun) return;
            foreach (var pair in workedAt)
            {
                if (Time.time - pair.Value > HandRoomFor || !workedPalm.TryGetValue(pair.Key, out Vector3 palm)) continue;
                if (!DeskTop(palm, out float y) || palm.y - y > .4f || palm.y - y < -.05f) continue;
                feet.Add(Round(FootKind.Real, pair.Key.Handedness == Handedness.Left ? "left hand" : "right hand", null, new Vector3(palm.x, y, palm.z), HandRoom, .1f));
            }
        }
    }
}
