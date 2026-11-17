using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // Room-level reactions of the modelled team (added by WindupsArrival):
    //  - "shh" (index finger up at the mouth): everyone stops talking and covers its mouth (Hush);
    //  - pointing at something in the room for a moment: everyone looks there (Peek).
    public sealed class WindupsGestures : MonoBehaviour
    {
        public WindupsArrival arrival;
        public int Hushes { get; private set; }
        public int Points { get; private set; }

        private float hushSince = -1f, pointSince = -1f, cooldownUntil;
        // The latest pointing finger (any duration), so "this mug" can use where the person pointed while speaking.
        public static Ray LastPointRay { get; private set; }
        public static float LastPointAt { get; private set; } = -100f;
        public static bool RecentPoint(float seconds, out Ray ray) { ray = LastPointRay; return Time.time - LastPointAt < seconds; }
        public static void SimulatePoint(Ray ray) { LastPointRay = ray; LastPointAt = Time.time; }

        private void Update()
        {
            if (arrival == null || !arrival.handoff.Ready) return;
            var head = arrival.handoff.meeting.Head;
            if (head == null || arrival.CurrentPhase < WindupsArrival.Phase.Introducing) { hushSince = pointSince = -1f; return; }
            bool hush = false, point = false;
            Ray pointRay = default;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !hand.IsConnected || hand.GetIndexFingerIsPinching()) continue;
                if (!hand.GetJointPose(HandJointId.HandIndexTip, out Pose tip) || !hand.GetJointPose(HandJointId.HandIndex1, out Pose knuckle) ||
                    !hand.GetJointPose(HandJointId.HandMiddleTip, out Pose middle) || !hand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist)) continue;
                bool indexOut = Vector3.Distance(tip.position, wrist.position) > .13f;
                bool middleIn = Vector3.Distance(middle.position, wrist.position) < .1f;
                if (!indexOut || !middleIn) continue;
                Vector3 mouth = head.position - head.up * .065f + head.forward * .05f;
                Vector3 along = (tip.position - knuckle.position).normalized;
                if (Vector3.Distance(tip.position, mouth) < .09f && Vector3.Dot(along, Vector3.up) > .5f) { hush = true; continue; }
                // The finger pressing the mic button is not pointing at anything.
                bool onMic = arrival.microphoneButton != null && Vector3.Distance(tip.position, arrival.microphoneButton.transform.position) < .1f;
                if (Vector3.Distance(tip.position, head.position) > .3f && !onMic)
                {
                    LastPointRay = new Ray(tip.position, along); // ("this mug": any pointing finger counts)
                    LastPointAt = Time.time;
                    // F68 (Quest 2026-11-14: "they all turn their heads left and do the same odd move"): a finger out
                    // to draw, press a button or reach for the frame was taken for pointing, so the crew turned to it
                    // every few seconds. The crew reacts only to a real point: the arm out (PointReach from the eyes),
                    // the hand free (holding, drawing and pressing nothing) and still for PointHold.
                    var maker = arrival.MakerActive ? arrival.Maker : null;
                    bool free = maker == null || maker.GestureFree(hand);
                    if (free && Vector3.Distance(tip.position, head.position) > PointReach) { point = true; pointRay = new Ray(tip.position, along); }
                }
            }
            if (Time.time < cooldownUntil) { hushSince = pointSince = -1f; return; }
            if (hush) { if (hushSince < 0f) hushSince = Time.time; } else hushSince = -1f;
            if (point && !hush)
            {
                // The finger must stay put (a reaching hand moves); moving restarts the wait.
                if (pointSince < 0f || Vector3.Distance(pointRay.origin, pointFrom) > .04f) { pointSince = Time.time; pointFrom = pointRay.origin; }
            }
            else pointSince = -1f;
            if (hushSince > 0f && Time.time - hushSince > .35f) Hush();
            else if (pointSince > 0f && Time.time - pointSince > PointHold) Point(pointRay);
        }

        public const float PointReach = .45f, PointHold = 1.2f;
        private Vector3 pointFrom;

        public void Hush()
        {
            hushSince = -1f;
            cooldownUntil = Time.time + 2.5f;
            Hushes++;
            foreach (var voice in FindObjectsByType<StudioVoice>(FindObjectsSortMode.None)) voice.Stop();
            // Everyone goes quiet, each a moment after the other (2026-11-11: not all on one frame).
            WindupBody.ReactAll(new[] { "Hush" }, .22f);
            Debug.Log("[Windups Gestures] Shh: everyone is quiet.");
        }

        public void Point(Ray ray)
        {
            pointSince = -1f;
            var room = arrival.handoff.meeting.environment;
            if (!Physics.Raycast(ray, out var hit, 4f, room.GeometryMask, QueryTriggerInteraction.Ignore) || hit.distance < .35f) return; // F68: .25 -> .35 (not the desk under the hand)
            cooldownUntil = Time.time + 4f;
            Points++;
            // Everyone looks where the finger points, one after the other; one peeks, one tilts its head,
            // one just looks (2026-11-11: they used to all peek on the same frame).
            WindupBody.ReactAll(new[] { "Peek", null, "HeadTilt" }, .2f, hit.point, 2.6f);
            ToyFx.Ring(hit.point + hit.normal * .004f, Quaternion.LookRotation(-hit.normal), .05f);
            Debug.Log("[Windups Gestures] Pointing at " + hit.point.ToString("F2"));
        }
    }
}
