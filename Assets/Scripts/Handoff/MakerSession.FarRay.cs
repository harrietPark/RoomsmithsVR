using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F89 (2026-11-15, Quest: "far touch is still too hard: with thumb and index almost together and my hand toward the
    // button, the circle and the hand ray should show, and they don't - only when I lower my hand and aim up"). The ray
    // itself is now always there while the hand is tracked (HandIO.BodyAim); here:
    //  - aim assist: a ray that misses every far target is tried again AimSnapAngle around itself (two rings of eight); the
    //    first button found there bends the hand's ray onto it (HandIO.AssistAim), so the ring sits on the button and a
    //    pinch presses it. Only buttons (FarKind.Tap; before the session begins UiHit: the intro's arrows, the note's
    //    buttons, Find ideas); a thing to move is never snapped to.
    //  - (A thin grey ray from a hand on nothing was tried and taken out the same day: the user did not want it.)
    //  - FarRayLog: every FarRayLogEvery s, a line in maker_input_log.txt on how the ray did on the headset.
    public sealed partial class MakerSession
    {
        public static bool AimAssist = true;
        // F103 (Quest 2026-11-16: pinches that missed a button were 6.6-9.8 deg off it): AimSnapAngle 3 -> 6, and a pinch
        // that begins on nothing takes the button within PinchCatchAngle (PinchCatch).
        public static float AimSnapAngle = 6f, AssistKeep = 1.6f, FarRayLogEvery = 10f, PinchCatchAngle = 9f;

        // F102 (Quest 2026-11-16, the user: "I don't want the distance ray, but it keeps coming out of my hand - it's
        // disrupting"): with F101's gate a relaxed hand (thumb and index 3-4.5 cm apart, under CurlOn) aimed roughly
        // where the person looks had the ray almost all the time, and aiming at nothing still drew a short ray. Aiming
        // itself is unchanged (a relaxed hand still aims, snaps and presses with a pinch), but nothing comes out of the
        // hand until the fingers close toward a pinch (FarRayShowGap, off past FarRayHideGap) or pinch: then the ray and
        // its ring show on what it is on. Before that, a button the hand points at only sinks a little (its own hover,
        // RaisedPillsFrame). No ray on nothing (RemoteGrab.AimStub off).
        public static bool QuietFarRay = true;
        public static float FarRayShowGap = .025f, FarRayHideGap = .035f;
        public bool FarRayVisible(Hand hand)
        {
            if (!QuietFarRay || hand == null) return true;
            if (HandIO.RawPinching(hand)) return true;
            return HandIO.NearPinch(hand, FarRayShowGap, FarRayHideGap, out _);
        }

        // Checks: a button the hand's far aim hovers (sinks a little).
        public int FarHoverCount => farHoverCount;

        public int AssistSnaps { get; private set; }
        private float farRayLogAt, gapSum, gapMin = float.MaxValue, gapMax, gazeSum;
        private int gapCount, snapsLogged;

        // F101: things to move are snapped to as well, in a wider cone (GrabSnapAngle), as Meta's distance grab picks its
        // candidate in a cone and not by a hair-thin ray: a 7 cm post-it 2 m away is 2 deg wide.
        public static float GrabSnapAngle = 7f; // (F103: 5 -> 7, wider than the buttons' AimSnapAngle)

        // At the top of Update (before the session begins too), before anything reads the hands' rays this frame.
        private void AimAssistFrame()
        {
            bool allowed = AimAssist && HandRules && !Collecting && (!HandIO.Active || HandIO.AssistSynthetic);
            farHoverCount = 0;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null) continue;
                if (!allowed || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) { HandIO.ClearAssist(hand); DropAimLock(hand); continue; }
                if (HandIO.RawPinching(hand))
                {
                    bool began = !catchPinchWas.TryGetValue(hand, out bool pinchWas) || !pinchWas;
                    catchPinchWas[hand] = true;
                    if (!HandIO.KeepAssist(hand) && began) PinchCatch(hand); // F103
                    KeepHover(hand);
                    continue; // the pinch presses where the ring was
                }
                catchPinchWas[hand] = false;
                // F101: a thing taken from afar and still carried (grab & release): its ray keeps the turn it was taken with.
                if (Begun && HandIO.AssistLocked(hand) && HeldBy(hand) != null) { HandIO.KeepAssist(hand); continue; }
                bool near = HandIO.AimIntent(hand, FarPrePinch, FarPrePinchOff, out float gap, out float gaze); // F99
                if (!near || !HandIO.Pointer(hand, out Pose own, false)) { HandIO.ClearAssist(hand); DropAimLock(hand); continue; }
                gapSum += gap; gapCount++; gazeSum += gaze;
                if (gap < gapMin) gapMin = gap;
                if (gap > gapMax) gapMax = gap;
                if (Begun && (HeldBy(hand) != null || FarQuiet(hand) || NearInReach(hand, PinchPoint(hand)))) { HandIO.ClearAssist(hand); DropAimLock(hand); continue; }
                var ray = new Ray(own.position, own.forward);
                if (SteadyAim) { SteadyAimFrame(hand, own, ray); continue; } // F102
                if (FarKindOn(ray, out _) != FarKind.None) { HandIO.ClearAssist(hand); continue; } // already on something
                bool was = HandIO.Assisting(hand);
                // The target found before, while the hand stays near it and it is still there (no new search).
                if (was && HandIO.AssistPoint(hand, out Vector3 kept, out float keptAngle) && Vector3.Angle(own.forward, kept - own.position) <= keptAngle)
                {
                    var keptKind = FarKindOn(new Ray(own.position, kept - own.position), out _);
                    if (keptKind == FarKind.Tap || keptKind == FarKind.Movable) { HandIO.AssistAim(hand, kept, keptAngle, keptKind == FarKind.Movable); continue; }
                }
                // A new search every other frame for each hand (the two hands take turns): the same work a frame as
                // before the wider cone.
                if (!HandIO.Active && ((Time.frameCount + (hand.Handedness == Handedness.Left ? 0 : 1)) & 1) != 0) { HandIO.ClearAssist(hand); continue; }
                if (FindSnap(ray, out Vector3 point, out bool movable))
                {
                    float snap = movable ? GrabSnapAngle : AimSnapAngle;
                    float bend = Vector3.Angle(own.forward, point - own.position); // (the middle of the target may lie further than the snap)
                    HandIO.AssistAim(hand, point, Mathf.Max(snap * AssistKeep, bend + snap * .5f), movable);
                    if (!was) { AssistSnaps++; if (movable) GrabSnaps++; }
                }
                else HandIO.ClearAssist(hand);
            }
            FarRayLog();
        }

        public int GrabSnaps { get; private set; } // snaps onto things to move (checks)

        // F103: a pinch that begins with the ray on nothing takes the button within PinchCatchAngle (the nearest one
        // found; never a thing to move): the ray is bent onto it for this pinch, so the press below takes it.
        private readonly Dictionary<Hand, bool> catchPinchWas = new Dictionary<Hand, bool>();
        public int PinchCatches { get; private set; }
        private void PinchCatch(Hand hand)
        {
            if (PinchCatchAngle <= AimSnapAngle * .5f || !HandIO.Pointer(hand, out Pose own, false)) return;
            if (Begun && (HeldBy(hand) != null || FarQuiet(hand) || NearInReach(hand, PinchPoint(hand)))) return;
            // (a pinch of the colour hand on nothing is a colour pick from the room, not a missed button)
            if (Begun && (ToolHand(hand) || (PaletteOnPalm && hand.Handedness == Handedness.Right))) return;
            var ray = new Ray(own.position, own.forward);
            if (Head != null && HandIO.RayOffGaze(Head, own) > HandIO.PinchGaze) return;
            if (FarKindOn(ray, out _) != FarKind.None) return;
            if (!NearestButton(ray, PinchCatchAngle, out Vector3 point)) return;
            float bend = Vector3.Angle(own.forward, point - own.position);
            HandIO.AssistAim(hand, point, Mathf.Max(PinchCatchAngle * AssistKeep, bend + 2f), false);
            PinchCatches++;
            WriteInputLog("far catch | " + hand.Handedness + " pinch on nothing took the button " + bend.ToString("0.0") + " deg off", true);
        }

        // ------------------------------------------------------------ F102: steady aim
        // Quest 2026-11-16, the user: "when the far ray lands on a button it goes jjijik, like friction". On a button's
        // edge the ray flipped every frame between the edge (the hand's own ray on the button: the assist was dropped)
        // and the button's middle (the ray just off it: snapped), and before a snap the search ran only every other
        // frame. Now the ray locks onto what it is on or near: a button's middle (its extent measured once, LockTap),
        // a thing to move where the ray last was on it (no jump to its middle), and it stays locked while the hand's own
        // ray stays within the lock's angle and is not on another target. Every frame, one probe while locked.
        public static bool SteadyAim = true;
        public static float LockMargin = .006f, LastOnKeep = .4f;
        private sealed class AimLock
        {
            public bool on;
            public FarKind kind;
            public int key;
            public Vector3 centre, axisU, axisV;
            public float halfU, halfV, angle;
        }
        private readonly Dictionary<Hand, AimLock> aimLocks = new Dictionary<Hand, AimLock>();
        private readonly Dictionary<Hand, (int key, Vector3 point, float at)> lastOnThing = new Dictionary<Hand, (int, Vector3, float)>();
        public int AimLockChanges { get; private set; } // checks: a lock dropped or moved to another target
        public int AimLocksMade { get; private set; }

        private AimLock LockOf(Hand hand)
        {
            if (!aimLocks.TryGetValue(hand, out var lk)) aimLocks[hand] = lk = new AimLock();
            return lk;
        }

        private void DropAimLock(Hand hand)
        {
            if (hand != null && aimLocks.TryGetValue(hand, out var lk) && lk.on) { lk.on = false; AimLockChanges++; }
        }

        // The point a hand's (locked) aim is on, for the buttons' hover (RaisedPillsFrame) and checks.
        public bool AimLockPoint(Hand hand, out Vector3 point, out bool button)
        {
            point = default;
            button = false;
            if (hand == null || !aimLocks.TryGetValue(hand, out var lk) || !lk.on) return false;
            point = lk.centre;
            button = lk.kind == FarKind.Tap;
            return true;
        }

        private readonly Vector3[] farHover = new Vector3[2];
        private int farHoverCount;
        private void KeepHover(Hand hand)
        {
            if (farHoverCount < farHover.Length && AimLockPoint(hand, out Vector3 p, out bool button) && button) farHover[farHoverCount++] = p;
        }

        private void SteadyAimFrame(Hand hand, Pose own, Ray ray)
        {
            var lk = LockOf(hand);
            var raw = FarKindOn(ray, out int rawKey, out float rawDistance);
            Vector3 rawHit = ray.GetPoint(rawDistance);
            if (lk.on)
            {
                bool onLocked = raw == lk.kind && (lk.kind == FarKind.Tap ? InsideLock(lk, rawHit) : rawKey == lk.key);
                bool onOther = raw != FarKind.None && !onLocked;
                if (!onOther && Vector3.Angle(own.forward, lk.centre - own.position) <= lk.angle && LockStillThere(lk, own.position))
                {
                    HandIO.AssistAim(hand, lk.centre, lk.angle, lk.kind == FarKind.Movable);
                    KeepHover(hand);
                    return;
                }
                lk.on = false;
                AimLockChanges++;
            }
            if (raw == FarKind.Tap)
            {
                if (LockTap(lk, ray, own.forward)) { HandIO.AssistAim(hand, lk.centre, lk.angle, false); KeepHover(hand); }
                else HandIO.ClearAssist(hand);
                return;
            }
            if (raw == FarKind.Movable)
            {
                // Right on it: its own point (the ring follows the hand); remembered for when the ray slips off it.
                lastOnThing[hand] = (rawKey, rawHit, Time.time);
                HandIO.ClearAssist(hand);
                return;
            }
            if (raw != FarKind.None) { HandIO.ClearAssist(hand); return; } // a small thing: near only
            // On nothing: the thing it was just on, held where the ray left it (no jump to its middle) ...
            if (Begun && lastOnThing.TryGetValue(hand, out var last) && Time.time - last.at < LastOnKeep &&
                Vector3.Angle(own.forward, last.point - own.position) <= GrabSnapAngle &&
                FarKindOn(new Ray(own.position, last.point - own.position), out int lastKey, out _) == FarKind.Movable && lastKey == last.key)
            {
                lk.on = true; lk.kind = FarKind.Movable; lk.key = last.key; lk.centre = last.point; lk.angle = GrabSnapAngle * AssistKeep;
                AimLocksMade++;
                HandIO.AssistAim(hand, lk.centre, lk.angle, true);
                return;
            }
            // ... else the nearest target in the cone around the ray.
            if (!FindSnap(ray, out Vector3 point, out bool movable)) { HandIO.ClearAssist(hand); return; }
            var to = new Ray(own.position, (point - own.position).normalized);
            float snap = movable ? GrabSnapAngle : AimSnapAngle;
            float bend = Vector3.Angle(own.forward, point - own.position);
            if (movable)
            {
                if (FarKindOn(to, out int key, out _) != FarKind.Movable) { HandIO.ClearAssist(hand); return; }
                lk.on = true; lk.kind = FarKind.Movable; lk.key = key; lk.centre = point; lk.angle = Mathf.Max(snap * AssistKeep, bend + snap * .5f);
                AimLocksMade++;
                GrabSnaps++;
            }
            else if (LockTap(lk, to, own.forward)) lk.angle = Mathf.Max(lk.angle, bend + snap * .5f);
            else { HandIO.ClearAssist(hand); return; }
            AssistSnaps++;
            HandIO.AssistAim(hand, lk.centre, lk.angle, movable);
            KeepHover(hand);
        }

        // F103: the nearest button around `ray` (rings every 1.5 deg out to `most`, twelve directions each). The first ring
        // that holds a button decides; when it holds two (two pills side by side), the one whose middle is nearest the ray
        // wins. The middle of it. Run once when a pinch begins on nothing.
        private readonly List<(Vector3 hit, Vector3 side, float angle)> catchHits = new List<(Vector3, Vector3, float)>();
        private bool NearestButton(Ray ray, float most, out Vector3 point)
        {
            point = default;
            Vector3 d = ray.direction.normalized;
            Vector3 u = Vector3.Cross(d, Mathf.Abs(d.y) < .95f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(d, u);
            catchHits.Clear();
            for (float a = 1.5f; a <= most + .01f && catchHits.Count == 0; a += 1.5f)
                for (int k = 0; k < 12; k++)
                {
                    float t = (k + (a / 1.5f) % 2f * .5f) * Mathf.PI / 6f;
                    Vector3 side = u * Mathf.Cos(t) + v * Mathf.Sin(t);
                    if (Probe(ray.origin, d, side, Vector3.Cross(d, side), a, 0f, out Ray probe) != FarKind.Tap || FarKindOn(probe, out float at) != FarKind.Tap) continue;
                    Vector3 hit = probe.GetPoint(at);
                    bool known = false;
                    foreach (var h in catchHits) if (Vector3.Distance(h.hit, hit) < .03f) { known = true; break; }
                    if (!known) catchHits.Add((hit, side, a));
                }
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in catchHits)
            {
                if (!SnapMiddle(ray.origin, d, h.side, h.angle, FarKind.Tap, out Vector3 middle)) continue;
                float angle = Vector3.Angle(d, middle - ray.origin);
                if (angle < best) { best = angle; point = middle; found = true; }
            }
            return found;
        }

        // The button under `ray`: its middle and its extent on its face, measured once by probing across it.
        private bool LockTap(AimLock lk, Ray ray, Vector3 handForward)
        {
            Vector3 o = ray.origin, d = ray.direction.normalized;
            Vector3 u = Vector3.Cross(Vector3.up, d);
            if (u.sqrMagnitude < 1e-4f) u = Vector3.right;
            u.Normalize();
            Vector3 v = Vector3.Cross(d, u);
            float plusU = Walk(o, d, u, v, 1f, 0f, true), minusU = Walk(o, d, u, v, -1f, 0f, true);
            float alongU = (plusU - minusU) * .5f;
            float plusV = Walk(o, d, u, v, 1f, alongU, false), minusV = Walk(o, d, u, v, -1f, alongU, false);
            float acrossV = (plusV - minusV) * .5f;
            if (!TapPoint(o, d, u, v, alongU, acrossV, out Vector3 centre)) return false;
            Vector3 pu = TapPoint(o, d, u, v, plusU, acrossV, out Vector3 a) ? a : centre, mu = TapPoint(o, d, u, v, -minusU, acrossV, out Vector3 b) ? b : centre;
            Vector3 pv = TapPoint(o, d, u, v, alongU, plusV, out Vector3 c) ? c : centre, mv = TapPoint(o, d, u, v, alongU, -minusV, out Vector3 e) ? e : centre;
            float step = Vector3.Distance(o, centre) * Mathf.Tan(SnapStep * Mathf.Deg2Rad) * .5f; // (the probes find the edge to within a step)
            lk.on = true;
            lk.kind = FarKind.Tap;
            lk.key = 0;
            lk.centre = (pu + mu + pv + mv) * .25f;
            lk.axisU = (pu - mu).sqrMagnitude > 1e-8f ? (pu - mu).normalized : u;
            lk.axisV = (pv - mv).sqrMagnitude > 1e-8f ? (pv - mv).normalized : v;
            lk.halfU = Vector3.Distance(pu, mu) * .5f + step;
            lk.halfV = Vector3.Distance(pv, mv) * .5f + step;
            lk.angle = Mathf.Max(AimSnapAngle * AssistKeep, Mathf.Max(plusU + minusU, plusV + minusV) * .5f + AimSnapAngle * .5f);
            if (FarKindOn(new Ray(o, lk.centre - o), out _) != FarKind.Tap) lk.centre = centre; // (a round or odd button: the probed middle)
            AimLocksMade++;
            return true;
        }

        // How far (degrees) from the ray, toward `sign` along u (or v at `along` on u), it stays on a button.
        private float Walk(Vector3 o, Vector3 d, Vector3 u, Vector3 v, float sign, float along, bool onU)
        {
            float last = 0f;
            for (float a = SnapStep; a <= SnapThrough; a += SnapStep)
            {
                var kind = onU ? Probe(o, d, u, v, sign * a, 0f, out _) : Probe(o, d, u, v, along, sign * a, out _);
                if (kind != FarKind.Tap) break;
                last = a;
            }
            return last;
        }

        private bool TapPoint(Vector3 o, Vector3 d, Vector3 u, Vector3 v, float alongDeg, float acrossDeg, out Vector3 point)
        {
            point = default;
            if (Probe(o, d, u, v, alongDeg, acrossDeg, out Ray probe) != FarKind.Tap || FarKindOn(probe, out float distance) != FarKind.Tap) return false;
            point = probe.GetPoint(distance);
            return true;
        }

        private bool InsideLock(AimLock lk, Vector3 p)
        {
            Vector3 r = p - lk.centre;
            return Mathf.Abs(Vector3.Dot(r, lk.axisU)) <= lk.halfU + LockMargin && Mathf.Abs(Vector3.Dot(r, lk.axisV)) <= lk.halfV + LockMargin &&
                   Vector3.Cross(lk.axisU, lk.axisV).sqrMagnitude > 1e-6f && Mathf.Abs(Vector3.Dot(r, Vector3.Cross(lk.axisU, lk.axisV).normalized)) < .05f;
        }

        // Still a target of that kind (that thing) along the line to the lock's point.
        private bool LockStillThere(AimLock lk, Vector3 origin)
        {
            var to = lk.centre - origin;
            if (to.sqrMagnitude < 1e-4f) return false;
            var kind = FarKindOn(new Ray(origin, to), out int key, out float distance);
            if (kind != lk.kind || (lk.kind == FarKind.Movable && key != lk.key)) return false;
            return Mathf.Abs(distance - to.magnitude) < .06f;
        }

        // What the ray is on (before the session begins: only buttons).
        private FarKind FarKindOn(Ray ray, out float distance) => FarKindOn(ray, out _, out distance);
        private FarKind FarKindOn(Ray ray, out int key, out float distance)
        {
            key = 0;
            if (!Begun) return UiHit(ray, out distance) ? FarKind.Tap : FarKind.None;
            return FarAim(ray, out key, out distance);
        }

        // Is the ray on anything of the session (tap: on a button)?
        private bool OnFarTarget(Ray ray, out bool tap, out float distance)
        {
            var kind = FarKindOn(ray, out distance);
            tap = kind == FarKind.Tap;
            return kind != FarKind.None;
        }

        // The nearest target around the ray: a point well inside it. Rings at half AimSnapAngle and AimSnapAngle (buttons
        // and things to move), then two more out to GrabSnapAngle (things to move only). The first probe that finds it
        // is on its edge (where it may share room with a neighbour, e.g. the page's check and the page's grab rim), so the
        // probe goes on in the same direction while it stays on a target of that kind, and the point is the middle of
        // that stretch.
        public static float SnapStep = .75f, SnapThrough = 9f; // degrees
        private bool FindSnap(Ray ray, out Vector3 point) => FindSnap(ray, out point, out _);
        private bool FindSnap(Ray ray, out Vector3 point, out bool movable)
        {
            point = default;
            movable = false;
            Vector3 d = ray.direction.normalized;
            Vector3 u = Vector3.Cross(d, Mathf.Abs(d.y) < .95f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(d, u);
            // A button within AimSnapAngle comes before a thing to move (the page's check before the page's rim): the
            // first thing to move found in the two inner rings waits until they hold no button.
            bool waiting = false;
            Vector3 waitSide = default;
            float waitFirst = 0f;
            for (int ring = 1; ring <= 4; ring++)
            {
                if (ring == 3 && waiting && SnapMiddle(ray.origin, d, waitSide, waitFirst, FarKind.Movable, out point)) { movable = true; return true; }
                if (ring > 2 && (!Begun || GrabSnapAngle <= AimSnapAngle)) break;
                float first = ring <= 2 ? AimSnapAngle * ring * .5f : AimSnapAngle + (GrabSnapAngle - AimSnapAngle) * (ring - 2) * .5f;
                for (int k = 0; k < 8; k++)
                {
                    float t = (k + (ring - 1) * .5f) * Mathf.PI * .25f;
                    Vector3 side = u * Mathf.Cos(t) + v * Mathf.Sin(t);
                    var kind = Probe(ray.origin, d, side, Vector3.Cross(d, side), first, 0f, out _);
                    if (kind == FarKind.Tap && ring <= 2) { if (SnapMiddle(ray.origin, d, side, first, kind, out point)) return true; continue; }
                    if (kind != FarKind.Movable) continue;
                    if (ring <= 2) { if (!waiting) { waiting = true; waitSide = side; waitFirst = first; } continue; }
                    if (SnapMiddle(ray.origin, d, side, first, kind, out point)) { movable = true; return true; }
                }
            }
            if (waiting && SnapMiddle(ray.origin, d, waitSide, waitFirst, FarKind.Movable, out point)) { movable = true; return true; }
            return false;
        }

        // Across the target a probe found at `first` degrees toward `side`: on along `side` while it stays on a target of
        // that kind, then both ways across at that middle; the point in the middle of it.
        private bool SnapMiddle(Vector3 origin, Vector3 d, Vector3 side, float first, FarKind kind, out Vector3 point)
        {
            point = default;
            Vector3 across = Vector3.Cross(d, side);
            float last = first;
            for (float a = first + SnapStep; a <= first + SnapThrough; a += SnapStep)
            {
                if (Probe(origin, d, side, across, a, 0f, out _) != kind) break;
                last = a;
            }
            float along = (first + last) * .5f, plus = 0f, minus = 0f;
            for (float b = SnapStep; b <= SnapThrough; b += SnapStep) { if (Probe(origin, d, side, across, along, b, out _) != kind) break; plus = b; }
            for (float b = SnapStep; b <= SnapThrough; b += SnapStep) { if (Probe(origin, d, side, across, along, -b, out _) != kind) break; minus = b; }
            if (Probe(origin, d, side, across, along, (plus - minus) * .5f, out Ray centre) != kind || FarKindOn(centre, out float distance) != kind) return false;
            point = centre.GetPoint(distance);
            return true;
        }

        // What the ray `alongDeg` off `d` toward `side` and `acrossDeg` toward `across` is on.
        private FarKind Probe(Vector3 origin, Vector3 d, Vector3 side, Vector3 across, float alongDeg, float acrossDeg, out Ray probe)
        {
            Vector3 direction = d + side * Mathf.Tan(alongDeg * Mathf.Deg2Rad) + across * Mathf.Tan(acrossDeg * Mathf.Deg2Rad);
            probe = new Ray(origin, direction.normalized);
            var kind = FarKindOn(probe, out float distance);
            return distance > 0f ? kind : FarKind.None;
        }

        private void FarRayLog()
        {
            if (Time.time < farRayLogAt) return;
            farRayLogAt = Time.time + FarRayLogEvery;
            if (HandIO.NearAimFrames == 0 && gapCount == 0 && AssistSnaps == snapsLogged) return;
            float off = HandIO.NearAimFrames > 0 ? 100f * HandIO.NearAimSystemOff / HandIO.NearAimFrames : 0f;
            string diff = HandIO.NearAimCompared > 0 ? (HandIO.NearAimDiffSum / HandIO.NearAimCompared).ToString("0.0") + " deg" : "-";
            string gaps = gapCount > 0 ? (gapSum / gapCount * 100f).ToString("0.0") + " cm (" + (gapMin * 100f).ToString("0.0") + "-" + (gapMax * 100f).ToString("0.0") + ")" : "-";
            WriteInputLog("far ray | almost-pinch frames " + HandIO.NearAimFrames + ", Meta aim missing " + off.ToString("0") + "% (our ray used) | ours vs Meta " + diff +
                          " | snaps " + (AssistSnaps - snapsLogged) + " | thumb-index gap " + gaps +
                          " | ray vs gaze " + (gapCount > 0 ? (gazeSum / gapCount).ToString("0") + " deg" : "-")); // F99
            HandIO.ResetAimStats();
            snapsLogged = AssistSnaps;
            gapSum = 0f; gapCount = 0; gapMin = float.MaxValue; gapMax = 0f; gazeSum = 0f;
        }
    }
}
