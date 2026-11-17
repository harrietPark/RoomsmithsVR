using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F97 (Quest 2026-11-15, the user: "when I come back I can't grab the things I made before, or the post-its and
    // pictures on the wall"): coming back, the earlier makes and the pictures pinned on the wall are put back on the way
    // in (RestoreBeforeTitle), before the title. But the session's hands (Update) only ran once the crew had said welcome
    // back (Begun), after the title, the crew coming over and the greeting: for that first half minute they could be seen
    // and not taken (the 19:28 try ended after 26 s with nothing taken). Now, before Begun, a pinch takes a made thing, a
    // stand or a pinned picture as it does later: near (on it), or aimed from afar (the ring filled, FarAimDwell), with
    // grab & release (F67). Nothing else of the session answers pinches before Begun.
    public sealed partial class MakerSession
    {
        public int EarlyTakes { get; private set; }

        private void EarlyHandsFrame()
        {
            if (Begun || arrival == null || Collecting || !HandRules) return;
            if (held == null && pinCarry == null && PinViews.Count == 0 && Kept == null && Stands.Count == 0 && RoomItems.Count == 0) return;
            FarAimFrame();       // the ring on what a hand aims at (RemoteGrab draws it)
            PinCarryFrame();     // a picture in the fingers
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand))
                {
                    if (hand != null) { wasPinching.Remove(hand); if (holdHand == hand) LetGo(); }
                    continue;
                }
                bool pinching = HandIO.Pinching(hand);
                wasPinching.TryGetValue(hand, out bool before);
                wasPinching[hand] = pinching;
                Vector3 point = PinchPoint(hand);
                if (holdHand == hand) { if (pinching) Hold(point, HandTurn(hand)); else LetGo(); continue; }
                if (!pinching || before || (pinCarry != null && pinCarry.hand == hand) || HeldBy(hand) != null) continue;
                if (arrival.MicNear(point)) continue;
                // Near: on the thing.
                var pin = PinNear(point, null, out var pinView);
                if (pin != null && NoteOnWall) { CarryPin(hand, point, null, pin, pinView); EarlyTook(hand, point, "moodboard pin"); continue; }
                var near = MadeNear(point);
                if (near != null) { Grab(hand, point, near); EarlyTook(hand, point, "made thing"); continue; }
                if (NearCrew(point)) continue; // a teammate's own grab takes it
                // From afar: only what was aimed at long enough (the ring filled).
                if (!aims.TryGetValue(hand, out var aim) || aim.kind != FarKind.Movable || Time.time - aim.since < FarAimDwell || !HandIO.Pointer(hand, out Pose pointer)) continue;
                var ray = new Ray(pointer.position, pointer.forward);
                pin = PinNear(Vector3.positiveInfinity, ray, out pinView);
                if (pin != null && NoteOnWall) { CarryPin(hand, Vector3.positiveInfinity, ray, pin, pinView); EarlyTook(hand, point, "moodboard pin (far)"); continue; }
                if (MadeOnRay(ray, out float far) is Transform aimed && far > .25f) { Grab(hand, point, aimed, far); EarlyTook(hand, point, "made thing (far)"); }
            }
        }

        private bool NearCrew(Vector3 point)
        {
            foreach (var pair in cast)
            {
                if (pair.Value == null || pair.Value.movement == null) continue;
                Vector3 feet = pair.Value.movement.transform.position;
                float up = Mathf.Clamp(point.y - feet.y, .03f, .12f);
                if (Vector3.Distance(point, feet + Vector3.up * up) < .075f) return true;
            }
            return false;
        }

        private void EarlyTook(Hand hand, Vector3 point, string what)
        {
            EarlyTakes++;
            lastPinchBy = what + " (before the welcome)";
            LogPinch(hand, point);
        }
    }
}
