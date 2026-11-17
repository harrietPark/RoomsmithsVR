using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // Every hand query of the maker session goes through here (2026-11-12). On the headset it is the Meta
    // hand, unchanged. In Editor checks a synthetic hand (joint poses, a pinch, a pointer) stands in for a
    // side, so the real input code (palette, colour pick, texture frame, tape, pinches) runs without a
    // headset. While any synthetic hand is set, a side without one counts as not tracked.
    public static class HandIO
    {
        public sealed class Synthetic
        {
            public bool pinching, thumbsUp;
            public readonly Dictionary<HandJointId, Pose> joints = new Dictionary<HandJointId, Pose>();
            // `pointer` stands for Meta's system aim pose (null: the runtime has none, as for a raised relaxed hand).
            // bodyAim (F89 checks): the ray is worked out from the joints and the head as on the headset; without it
            // `pointer` is the ray itself (the older checks).
            public Pose? pointer;
            public bool bodyAim;
            public Pose root = new Pose(Vector3.zero, Quaternion.identity);
            public void Set(HandJointId id, Vector3 position) => joints[id] = new Pose(position, Quaternion.identity);
        }

        public static Synthetic Left, Right;
        public static bool Active => Left != null || Right != null;
        public static void Clear() { Left = Right = null; holds.Clear(); assists.Clear(); nearPinch.Clear(); rays.Clear(); }

        private static Synthetic Of(Hand hand) => hand == null ? null : hand.Handedness == Handedness.Left ? Left : Right;

        public static bool Connected(Hand hand) => hand != null && (Active ? Of(hand) != null : hand.IsConnected);
        public static bool Tracked(Hand hand) => hand != null && (Active ? Of(hand) != null : hand.IsTrackedDataValid);

        // The fingers really closed (no grab & release).
        public static bool RawPinching(Hand hand)
        {
            if (hand == null) return false;
            if (Active) { var s = Of(hand); return s != null && s.pinching; }
            return hand.GetIndexFingerIsPinching();
        }

        // ------------------------------------------------------------ grab & release (F67)
        // 2026-11-13 (the user after a Quest try: "moving things is still too hard - a grab & release method?"; they
        // chose it): a short pinch that picks something up leaves it in the hand - the fingers open and it still comes
        // along - and the next pinch puts it down there. A long pinch that drags (the old way) still lets go when the
        // fingers open. The thing's code calls BeginHold when it takes hold; while the hand carries with open fingers,
        // Pinching keeps saying true to whoever holds it; the pinch that puts it down reads false from its start until
        // the fingers open again, so it never also takes something else.
        public static bool GrabRelease = true;
        public static float StickyPinch = .5f, StickyStill = .06f; // a pinch this short and still leaves it in the hand
        private enum HoldPhase { None, Gripping, Carrying, Releasing }
        private sealed class HoldState { public HoldPhase phase; public int frame = -1; public bool raw; public float since; public Vector3 from; }
        private static readonly Dictionary<Hand, HoldState> holds = new Dictionary<Hand, HoldState>();
        public static int StickyHolds { get; private set; }   // grabs left in the hand (checks)
        public static int StickyDrops { get; private set; }   // put down by the next pinch

        private static HoldState StateOf(Hand hand)
        {
            if (!holds.TryGetValue(hand, out var s)) holds[hand] = s = new HoldState();
            if (s.frame == Time.frameCount) return s;
            s.frame = Time.frameCount;
            bool raw = RawPinching(hand);
            bool rising = raw && !s.raw;
            s.raw = raw;
            if (rising) { s.since = Time.time; s.from = Tip(hand); }
            switch (s.phase)
            {
                case HoldPhase.Gripping:
                    if (!raw)
                    {
                        bool shortAndStill = Time.time - s.since <= StickyPinch && Vector3.Distance(Tip(hand), s.from) <= StickyStill;
                        s.phase = GrabRelease && shortAndStill ? HoldPhase.Carrying : HoldPhase.None;
                        if (s.phase == HoldPhase.Carrying) StickyHolds++;
                    }
                    break;
                case HoldPhase.Carrying:
                    if (rising) { s.phase = HoldPhase.Releasing; StickyDrops++; }
                    else if (!Connected(hand)) s.phase = HoldPhase.None;
                    break;
                case HoldPhase.Releasing:
                    if (!raw) s.phase = HoldPhase.None;
                    break;
            }
            return s;
        }

        private static Vector3 Tip(Hand hand) => Joint(hand, HandJointId.HandIndexTip, out Pose p) ? p.position : Vector3.zero;

        public static bool Pinching(Hand hand)
        {
            if (hand == null) return false;
            var s = StateOf(hand);
            switch (s.phase)
            {
                case HoldPhase.Carrying: return true;
                case HoldPhase.Releasing: return false;
                default: return s.raw;
            }
        }

        // A thing was taken by this hand's pinch: it may stay in the hand when the pinch was short (see above).
        public static void BeginHold(Hand hand)
        {
            if (hand == null) return;
            var s = StateOf(hand);
            if (s.phase == HoldPhase.None && s.raw) s.phase = HoldPhase.Gripping;
        }

        // The thing left the hand by itself (dropped by its code, or put down): the hand is plain again. A pinch that is
        // putting something down still reads false until the fingers open.
        public static void EndHold(Hand hand)
        {
            if (hand == null || !holds.TryGetValue(hand, out var s)) return;
            if (s.phase == HoldPhase.Gripping || s.phase == HoldPhase.Carrying) s.phase = HoldPhase.None;
        }

        // Holding with the pinch still closed (it may yet stay in the hand when the fingers open).
        public static bool Gripping(Hand hand) => hand != null && StateOf(hand).phase == HoldPhase.Gripping;

        // Carrying with open fingers right now.
        public static bool CarryingOpen(Hand hand) => hand != null && StateOf(hand).phase == HoldPhase.Carrying;
        public static void ClearHolds() => holds.Clear();

        public static bool Joint(Hand hand, HandJointId id, out Pose pose)
        {
            pose = default;
            if (hand == null) return false;
            if (Active) { var s = Of(hand); return s != null && s.joints.TryGetValue(id, out pose); }
            return hand.GetJointPose(id, out pose);
        }

        // The hand's ray (F86 origin, F89 direction and aim assist).
        public static bool Pointer(Hand hand, out Pose pose) => Pointer(hand, out pose, true);

        // assisted = false: the ray as the hand gives it, without F89's bend onto a button it just misses.
        public static bool Pointer(Hand hand, out Pose pose, bool assisted)
        {
            pose = default;
            if (hand == null) return false;
            bool ok;
            if (Active)
            {
                var s = Of(hand);
                if (s == null) return false;
                if (s.bodyAim) ok = PinchRay(hand, out pose);
                else
                {
                    if (s.pointer == null) return false;
                    pose = s.pointer.Value;
                    ok = true;
                }
            }
            else ok = PinchRay(hand, out pose);
            if (ok && assisted) pose = Assisted(hand, pose);
            return ok;
        }

        // ------------------------------------------------------------ the hand's ray, always there (F89)
        // 2026-11-15 (Quest: "far touch only works when I lower my hand and aim up; I want it any time, wherever my hand
        // is"). The ray used to point along Meta's system aim pose, which the runtime gives only while the hand is held in
        // an aiming pose (OVRHand.IsPointerPoseValid = HandStatus.InputStateValid: hand out in front, palm away). A raised,
        // relaxed, almost-pinching hand has none, so there was no ray, no ring and no far press at all. Now the ray is
        // worked out from the hand itself whenever the hand is tracked: from the pinch point, along the line from an
        // estimated shoulder (Meta's own ShoulderEstimatePosition offset, yaw of the head only) through the pinch point,
        // bent HandBlend toward where the hand itself points (wrist -> index/middle knuckles), smoothed (one-euro style:
        // steady when still, quick when moving). BodyAim = false goes back to Meta's aim pose.
        public static bool BodyAim = true;
        // F103: Meta's aim pose, when the runtime gives one, sets the direction (ours only where it is missing).
        public static bool PreferSystemAim = true;
        public static bool SystemAimSynthetic; // checks: a synthetic hand's `pointer` stands for Meta's aim pose (else body aim, as before)
        public static Vector3 ShoulderOffset = new Vector3(.13f, -.25f, -.13f);
        public static float HandBlend = .3f;
        public static float AimMinCutoff = 1.5f, AimBeta = .04f; // Hz, and Hz per degree a second
        private static Transform head;

        // The head the shoulder is estimated from (the rig's centre eye; checks may set it).
        public static Transform Head
        {
            get
            {
                if (head != null && head.gameObject.activeInHierarchy) return head;
                var rig = Object.FindFirstObjectByType<OVRCameraRig>();
                head = rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;
                return head;
            }
            set => head = value;
        }

        // Meta's aim pose: the system's own (null on a synthetic hand without `pointer`).
        public static bool SystemAim(Hand hand, out Pose pose)
        {
            pose = default;
            if (hand == null) return false;
            if (Active) { var s = Of(hand); if (s?.pointer == null) return false; pose = s.pointer.Value; return true; }
            return hand.GetPointerPose(out pose);
        }

        // Where the ray goes, before smoothing: from the shoulder through the pinch point, part the hand's own pointing.
        public static bool BodyDirection(Hand hand, Vector3 pinch, out Vector3 direction)
        {
            direction = Vector3.forward;
            var h = Head;
            if (hand == null || h == null) return false;
            Quaternion yaw = Quaternion.Euler(0f, h.rotation.eulerAngles.y, 0f);
            Vector3 offset = ShoulderOffset * (Active ? 1f : Mathf.Clamp(hand.Scale, .8f, 1.25f));
            if (hand.Handedness == Handedness.Left) offset.x = -offset.x;
            Vector3 arm = pinch - (h.position + yaw * offset);
            if (arm.sqrMagnitude < .0025f) return false; // the hand at the shoulder: no line to aim along
            arm.Normalize();
            direction = arm;
            if (HandBlend > 0f && Joint(hand, HandJointId.HandWristRoot, out Pose wrist) &&
                Joint(hand, HandJointId.HandIndex1, out Pose index) && Joint(hand, HandJointId.HandMiddle1, out Pose middle))
            {
                Vector3 own = (index.position + middle.position) * .5f - wrist.position;
                if (own.sqrMagnitude > 1e-4f)
                {
                    own.Normalize();
                    if (Vector3.Dot(own, arm) > .2f) direction = (arm * (1f - HandBlend) + own * HandBlend).normalized; // (a hand bent back is left out)
                }
            }
            return true;
        }

        // Quest log (MakerSession.FarRayLog): while almost pinching, how often Meta had no aim pose and how far ours was
        // from it when it had one.
        public static int NearAimFrames, NearAimSystemOff, NearAimCompared;
        public static float NearAimDiffSum;
        public static void ResetAimStats() { NearAimFrames = NearAimSystemOff = NearAimCompared = 0; NearAimDiffSum = 0f; }

        // ------------------------------------------------------------ almost pinching (F82, F89)
        // Thumb and index tips within `on` turn the hand's ray on; it stays on until they are more than `off` apart.
        private static readonly Dictionary<Hand, bool> nearPinch = new Dictionary<Hand, bool>();
        public static bool NearPinch(Hand hand, float on, float off, out float gap)
        {
            gap = -1f;
            if (hand == null) return false;
            if (!Joint(hand, HandJointId.HandIndexTip, out Pose index) || !Joint(hand, HandJointId.HandThumbTip, out Pose thumb)) { nearPinch[hand] = false; return false; }
            gap = Vector3.Distance(index.position, thumb.position);
            nearPinch.TryGetValue(hand, out bool was);
            bool near = gap < on || (was && gap <= off);
            nearPinch[hand] = near;
            return near;
        }

        // F99 (Quest 2026-11-15, the user, after F98's 2.5 cm: "the distance ray hardly shows now... please, make it
        // natural"): with 5.5 cm a relaxed hand (thumb and index 3.7-4.4 cm apart) had the ray in every frame; with 2.5 cm it
        // came only once the fingers nearly touched, so there was nothing to aim with before the pinch. A person aiming
        // points the hand where they look. Now the ray is on when the tips are within `pinchOn` (always), or when the hand
        // is naturally curled (within CurlOn) and its ray points within GazeOn of where the head looks; once on it stays
        // until the tips are past `pinchOff` and (past CurlOff or the ray past GazeOff). A hand resting on the desk or at
        // the side, pointing away from the view, shows none.
        // F101 (Quest 2026-11-15, the user: "the distance grab is still awful - I want it as natural as the Quest's own"):
        // the last play had no far grab at all (ten pinches on nothing). The gate is now the pose of someone pointing at
        // what they look at, as on the Quest: the hand's ray passes within GazeOn of where the head looks (RayOffGaze: the
        // nearest the ray comes to the middle of the view, not the angle between two directions - a hand at the hip
        // pointing at a note on the desk is 40 deg off the head's forward and still dead on what is looked at) and the
        // fingers are not spread (CurlOn). Fingers nearly touching need only PinchGaze. A resting hand, whose ray goes
        // nowhere near the view, has none (it had one all the time: thumb and index rest 2 cm apart).
        public static float CurlOn = .055f, CurlOff = .065f, GazeOn = 28f, GazeOff = 38f, PinchGaze = 55f;
        private static readonly float[] GazeSamples = { .25f, .5f, 1f, 2f, 4f };
        private static readonly Dictionary<Hand, bool> aimIntent = new Dictionary<Hand, bool>();

        // Seen from the head: the smallest angle between where it looks and a point on the ray (0.25-4 m along it).
        public static float RayOffGaze(Transform h, Pose ray)
        {
            float best = 180f;
            if (h == null) return best;
            for (int i = 0; i < GazeSamples.Length; i++)
            {
                float a = Vector3.Angle(h.forward, ray.position + ray.forward * GazeSamples[i] - h.position);
                if (a < best) best = a;
            }
            return best;
        }

        public static bool AimIntent(Hand hand, float pinchOn, float pinchOff, out float gap, out float gaze)
        {
            gap = -1f;
            gaze = 180f;
            if (hand == null) return false;
            if (!Joint(hand, HandJointId.HandIndexTip, out Pose index) || !Joint(hand, HandJointId.HandThumbTip, out Pose thumb)) { aimIntent[hand] = false; return false; }
            gap = Vector3.Distance(index.position, thumb.position);
            var h = Head;
            if (h != null && Pointer(hand, out Pose ray, false)) gaze = RayOffGaze(h, ray);
            aimIntent.TryGetValue(hand, out bool was);
            bool on = (gap < pinchOn && gaze < PinchGaze) || (gap < CurlOn && gaze < GazeOn) ||
                      (was && ((gap <= pinchOff && gaze <= PinchGaze + 10f) || (gap <= CurlOff && gaze <= GazeOff)));
            aimIntent[hand] = on;
            return on;
        }

        // ------------------------------------------------------------ aim assist (F89)
        // A ray that just misses a button is bent onto it (MakerSession.AimAssistFrame finds the button within
        // MakerSession.AimSnapAngle and calls AssistAim each frame). Every reader of Pointer gets the bent ray, so the ring
        // and the pinch agree. The bend is given up as soon as the hand's own ray is more than `angle` away.
        // F101: a thing to move is snapped to as well (a cone, as Meta's distance grab picks). Once the pinch begins the
        // bend becomes a fixed turn of the hand's ray (locked), so the thing carried along the ray follows the hand
        // from where it was taken, without a jump.
        private sealed class AimAssist { public Vector3 point; public float angle; public int frame = -10; public bool movable, locked; public Quaternion delta = Quaternion.identity; }
        private static readonly Dictionary<Hand, AimAssist> assists = new Dictionary<Hand, AimAssist>();
        public static bool AssistSynthetic; // checks: let the assist work on synthetic hands too

        public static void AssistAim(Hand hand, Vector3 point, float angle, bool movable = false)
        {
            if (hand == null) return;
            if (!assists.TryGetValue(hand, out var a)) assists[hand] = a = new AimAssist();
            a.point = point;
            a.angle = angle;
            a.movable = movable;
            a.locked = false;
            a.frame = Time.frameCount;
        }
        public static bool AssistLocked(Hand hand) => hand != null && assists.TryGetValue(hand, out var a) && a.locked && a.frame >= Time.frameCount - 1;

        // While a pinch goes on: the bend stays while the hand's own ray is still near the button.
        public static bool KeepAssist(Hand hand)
        {
            if (hand == null || !assists.TryGetValue(hand, out var a) || a.frame < Time.frameCount - 1) return false;
            if (!Pointer(hand, out Pose own, false)) { assists.Remove(hand); return false; }
            if (a.locked) { a.frame = Time.frameCount; return true; }
            if (Vector3.Angle(own.forward, a.point - own.position) > a.angle) { assists.Remove(hand); return false; }
            if (a.movable)
            {
                a.delta = Quaternion.FromToRotation(own.forward, (a.point - own.position).normalized);
                a.locked = true;
            }
            a.frame = Time.frameCount;
            return true;
        }

        public static void ClearAssist(Hand hand) { if (hand != null) assists.Remove(hand); }
        public static bool AssistPoint(Hand hand, out Vector3 point, out float angle)
        {
            point = default;
            angle = 0f;
            if (hand == null || !assists.TryGetValue(hand, out var a) || a.frame < Time.frameCount - 1) return false;
            point = a.point;
            angle = a.angle;
            return true;
        }
        public static bool Assisting(Hand hand) => hand != null && assists.TryGetValue(hand, out var a) && a.frame >= Time.frameCount - 1;

        private static Pose Assisted(Hand hand, Pose pose)
        {
            if (!assists.TryGetValue(hand, out var a) || a.frame < Time.frameCount - 1) return pose;
            if (a.locked) return new Pose(pose.position, a.delta * pose.rotation);
            Vector3 to = a.point - pose.position;
            if (to.sqrMagnitude < 1e-4f || Vector3.Angle(pose.forward, to) > a.angle) return pose;
            return new Pose(pose.position, Quaternion.LookRotation(to.normalized, pose.up));
        }

        // ------------------------------------------------------------ the hand's ray (F86)
        // 2026-11-15 (the user after a Quest try: "far touch got too hard; the hand ray should come from between my index
        // and thumb, not from the index"). The ray starts at the pinch point - halfway between the thumb tip and the index
        // tip, where the fingers meet - and keeps the SDK aim's direction. Closing the fingers pulls the hand off what it
        // aimed at, so the moment a pinch begins the ray goes back to where it was RayLookBack earlier and stays there
        // RayLatch, then glides to the live ray over RayBlend (a drag still follows the hand). Computed once a frame.
        public static bool PinchOrigin = true;
        public static float RayLookBack = .12f, RayLatch = .22f, RayBlend = .15f;
        private const int RayHistory = 24;
        private sealed class RayState
        {
            public int frame = -1;
            public bool ok, raw;
            public Pose pose;
            public readonly float[] times = new float[RayHistory];
            public readonly Vector3[] origins = new Vector3[RayHistory], directions = new Vector3[RayHistory];
            public int count, next;
            public float pinchAt = -1f;
            public Vector3 heldOrigin, heldDirection;
            public bool body;                        // F89: last frame's ray was ours (not Meta's aim pose)
            public Vector3 smoothed, lastRaw;
            public float smoothAt = -1f;
        }
        private static readonly Dictionary<Hand, RayState> rays = new Dictionary<Hand, RayState>();
        public static int RayLatches { get; private set; } // checks: pinches whose ray went back to the aim before closing

        private static bool PinchRay(Hand hand, out Pose pose)
        {
            if (!rays.TryGetValue(hand, out var s)) rays[hand] = s = new RayState();
            if (s.frame != Time.frameCount)
            {
                s.frame = Time.frameCount;
                s.ok = Aim(hand, s, out Vector3 origin, out Vector3 direction, out Vector3 up);
                if (s.ok) s.pose = Steady(hand, s, origin, direction, up);
            }
            pose = s.pose;
            return s.ok;
        }

        // This frame's ray before the pinch latch: ours (F89) from the joints and the head, else Meta's aim pose.
        private static bool Aim(Hand hand, RayState s, out Vector3 origin, out Vector3 direction, out Vector3 up)
        {
            bool system = SystemAim(hand, out Pose sdk);
            bool index = Joint(hand, HandJointId.HandIndexTip, out Pose indexTip);
            bool thumb = Joint(hand, HandJointId.HandThumbTip, out Pose thumbTip);
            bool tips = index && thumb;
            Vector3 pinch = tips ? (indexTip.position + thumbTip.position) * .5f : system ? sdk.position : Vector3.zero;
            up = system ? sdk.up : Vector3.up;
            // F103 (Quest 2026-11-16, the user: "distance touch is still very uncomfortable - it hardly presses and I can't
            // tell whether it works"): while the fingers closed toward a pinch Meta's aim pose was there in every frame
            // (0% missing), and our shoulder line pointed 7-20 deg away from it; the pinches that missed a button were
            // 6-10 deg off it. The Quest's own ray is what the person's hand is used to: when Meta gives an aim pose its
            // direction is the ray's (from the pinch point, F86); ours only fills in when it is missing.
            if (PreferSystemAim && system && tips && (!Active || SystemAimSynthetic))
            {
                origin = pinch;
                direction = sdk.forward;
                if (s.body) s.smoothAt = -1f;
                s.body = false;
                if ((nearPinch.TryGetValue(hand, out bool nearNow) && nearNow) || (aimIntent.TryGetValue(hand, out bool aimingNow) && aimingNow))
                {
                    NearAimFrames++;
                    if (BodyDirection(hand, pinch, out Vector3 ours)) { NearAimCompared++; NearAimDiffSum += Vector3.Angle(ours, sdk.forward); }
                }
                return true;
            }
            if (BodyAim && tips && BodyDirection(hand, pinch, out direction))
            {
                origin = pinch;
                if (!s.body) s.smoothAt = -1f; // coming from Meta's aim: start the smoothing afresh
                s.body = true;
                if ((nearPinch.TryGetValue(hand, out bool near) && near) || (aimIntent.TryGetValue(hand, out bool aiming) && aiming)) // (F99: the ray's own on-state too)
                {
                    NearAimFrames++;
                    if (!system) NearAimSystemOff++;
                    else { NearAimCompared++; NearAimDiffSum += Vector3.Angle(direction, sdk.forward); }
                }
                if (Mathf.Abs(Vector3.Dot(direction, up)) > .95f) up = Mathf.Abs(direction.y) < .95f ? Vector3.up : Vector3.forward;
                return true;
            }
            s.body = false;
            if (!system) { origin = direction = Vector3.zero; return false; }
            origin = PinchOrigin && tips ? pinch : sdk.position;
            direction = sdk.forward;
            return true;
        }

        private static Pose Steady(Hand hand, RayState s, Vector3 origin, Vector3 direction, Vector3 up)
        {
            float now = Time.time;
            if (s.body)
            {
                // F89: steady when the hand is still, quick when it moves (the cutoff grows with the angular speed).
                float dt = now - s.smoothAt;
                if (s.smoothAt >= 0f && dt > 0f && dt < .25f && s.smoothed.sqrMagnitude > .5f)
                {
                    float speed = Vector3.Angle(s.lastRaw, direction) / dt;
                    float cutoff = AimMinCutoff + AimBeta * speed;
                    float a = 1f - Mathf.Exp(-dt * 2f * Mathf.PI * cutoff);
                    s.lastRaw = direction;
                    direction = Vector3.Slerp(s.smoothed, direction, a).normalized;
                }
                else s.lastRaw = direction;
                if (s.smoothAt < 0f || dt > 0f) { s.smoothed = direction; s.smoothAt = now; }
                else direction = s.smoothed;
            }
            bool raw = RawPinching(hand);
            if (raw && !s.raw)
            {
                // The ray as it was just before the fingers started to close.
                s.pinchAt = now;
                s.heldOrigin = origin;
                s.heldDirection = direction;
                for (int k = 1; k <= s.count; k++)
                {
                    int i = (s.next - k + RayHistory) % RayHistory;
                    s.heldOrigin = s.origins[i];
                    s.heldDirection = s.directions[i];
                    if (now - s.times[i] >= RayLookBack) break;
                }
                RayLatches++;
            }
            if (!raw) s.pinchAt = -1f;
            s.raw = raw;
            if (!raw)
            {
                s.times[s.next] = now;
                s.origins[s.next] = origin;
                s.directions[s.next] = direction;
                s.next = (s.next + 1) % RayHistory;
                if (s.count < RayHistory) s.count++;
            }
            if (s.pinchAt >= 0f)
            {
                float t = Mathf.Clamp01((now - s.pinchAt - RayLatch) / Mathf.Max(.01f, RayBlend));
                origin = Vector3.Lerp(s.heldOrigin, origin, t);
                direction = Vector3.Slerp(s.heldDirection, direction, t);
            }
            if (direction.sqrMagnitude < 1e-6f) direction = s.directions[(s.next + RayHistory - 1) % RayHistory];
            if (direction.sqrMagnitude < 1e-6f) direction = Vector3.forward;
            direction.Normalize();
            if (Mathf.Abs(Vector3.Dot(direction, up.normalized)) > .98f) up = Mathf.Abs(direction.y) < .95f ? Vector3.up : Vector3.forward;
            return new Pose(origin, Quaternion.LookRotation(direction, up));
        }

        public static bool Root(Hand hand, out Pose pose)
        {
            pose = default;
            if (hand == null) return false;
            if (Active) { var s = Of(hand); if (s == null) return false; pose = s.root; return true; }
            return hand.GetRootPose(out pose);
        }

        // The palm's centre and the normal out of it (the same as DoubleClap.TryPalm: out of the left palm,
        // out of the back of the right hand).
        public static bool TryPalm(Hand hand, out Vector3 center, out Vector3 normal)
        {
            center = normal = Vector3.zero;
            if (!Tracked(hand) || !Joint(hand, HandJointId.HandWristRoot, out Pose wrist) ||
                !Joint(hand, HandJointId.HandIndex1, out Pose index) || !Joint(hand, HandJointId.HandPinky1, out Pose pinky)) return false;
            center = (wrist.position + index.position + pinky.position) / 3f;
            normal = Vector3.Cross(index.position - wrist.position, pinky.position - wrist.position).normalized;
            return normal.sqrMagnitude > .5f;
        }

        public static bool ThumbsUp(Hand hand)
        {
            if (hand == null) return false;
            if (Active) { var s = Of(hand); return s != null && s.thumbsUp; }
            return IsThumbsUp(hand);
        }

        // A real hand's thumbs up: the thumb straight up, the other fingers curled to the palm.
        private static bool IsThumbsUp(Hand hand)
        {
            if (!hand.GetJointPose(HandJointId.HandThumbTip, out Pose thumbTip) || !hand.GetJointPose(HandJointId.HandThumb1, out Pose thumbBase) ||
                !hand.GetJointPose(HandJointId.HandPalm, out Pose palm) || !hand.GetJointPose(HandJointId.HandIndexTip, out Pose index) ||
                !hand.GetJointPose(HandJointId.HandMiddleTip, out Pose middle) || !hand.GetJointPose(HandJointId.HandRingTip, out Pose ring)) return false;
            Vector3 thumb = thumbTip.position - thumbBase.position;
            if (thumb.magnitude < .035f || Vector3.Dot(thumb.normalized, Vector3.up) < .75f) return false;
            if (thumbTip.position.y - palm.position.y < .04f) return false;
            // Curled fingers: tips close to the palm.
            return Vector3.Distance(index.position, palm.position) < .065f && Vector3.Distance(middle.position, palm.position) < .06f &&
                   Vector3.Distance(ring.position, palm.position) < .06f;
        }
    }
}
