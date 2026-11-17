using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using RoomCapsule;
using UnityEngine;

namespace Windups.Handoff
{
    // Far interaction for everything, like the UI ray: point a hand at a speech card, a placed
    // card, an earlier line in a deck, the topic note, a character or its name tag.
    //  - a ring cursor (like the UI ray's) shows wherever the ray touches something usable,
    //  - a quick pinch-and-release is a tap (hear more / expand / open a deck / name tag; two
    //    quick taps on the topic note rewrite it, exactly like tapping it up close),
    //  - pinch and move (or hold) to drag it along the ray, joined to the hand by a white
    //    elastic string; move the hand toward or away from the body to pull it in or push it
    //    out; let go to drop it (ring, head, basket, anywhere).
    // Objects within reach (25 cm of the hand) are left to the ISDK near hand grab. Pinching
    // right on an earlier line in a deck pulls it out as a card in the hand too.
    // Drags use the same Grabbable pointer events as the near grab and the Editor mouse preview,
    // so every release rule still applies. (ISDK Distance Grab stays disabled.)
    [DefaultExecutionOrder(-40)]
    public sealed class RemoteGrab : MonoBehaviour
    {
        public static RemoteGrab Instance { get; private set; }
        public WindupsArrival arrival;
        public const float NearLimit = .25f, DragMove = .015f, TapTime = .35f, PushGain = 2.5f;

        private sealed class Grip
        {
            public int pointer;
            public TransferableSpeechBubble card;
            public CapsulePetController pet;
            public CharacterIntroCard tag;
            public PaperSnapshot snapshot;
            public Grabbable target;
            public bool near, dragging, promoted;
            public int readyFrame;
            public float distance, startedAt, startDepth;
            public Vector3 startPoint, offset, attachLocal;
            public Pose pose;
            public Vector3 bend, bendVelocity; // the elastic string's middle
        }

        private readonly Dictionary<object, Grip> grips = new Dictionary<object, Grip>();
        private readonly Dictionary<Hand, bool> wasPinching = new Dictionary<Hand, bool>();
        // F66 (maker): a teammate is dragged from afar only after the hand has pointed at it for MakerSession.FarAimDwell,
        // and it moves only once the pinch has been held MakerSession.FarHold.
        private readonly Dictionary<Hand, CapsulePetController> aimedPet = new Dictionary<Hand, CapsulePetController>();
        private readonly Dictionary<Hand, float> aimedSince = new Dictionary<Hand, float>();
        private static bool Strict => MakerSession.HandRules && MakerSession.Instance != null && MakerSession.Instance.Begun;
        public int StrictRefusals { get; private set; }
        // F89: one ring and one ray per hand (0 left, 1 right; it used to be one for both, so the second hand showed none).
        private readonly Transform[] cursors = new Transform[2], cursorDots = new Transform[2];
        // F68 (Quest 2026-11-14: "show the ray too, like the hand ray, not only the circle"): a thin white line from the
        // hand to the ring cursor, whenever the cursor shows.
        private readonly LineRenderer[] farRays = new LineRenderer[2];
        private readonly bool[] cursorOn = new bool[2], rayOn = new bool[2];
        private AnimationCurve targetWidth;
        public bool FarRayVisible => Active(farRays[0]) || Active(farRays[1]);
        public bool RayVisible(bool left) => Active(farRays[left ? 0 : 1]);
        public bool TargetRayVisible(bool left) => Active(farRays[left ? 0 : 1]) && Active(cursors[left ? 0 : 1]);
        public Vector3 CursorPoint(bool left) => cursors[left ? 0 : 1] != null ? cursors[left ? 0 : 1].position : Vector3.zero;
        // Checks (F89): RemoteGrab reads hands through HandIO, so synthetic hands drive it (off: the real hands only).
        public static bool TestHands;
        private ElasticString line;
        private int nextPointer = 982100;
        private readonly List<Mesh> meshes = new List<Mesh>();

        public bool Dragging { get { foreach (var g in grips.Values) if (g.dragging) return true; return false; } }
        public bool CursorVisible => Active(cursors[0]) || Active(cursors[1]);
        public bool StringVisible => line != null && line.gameObject.activeSelf;
        private static bool Active(Component c) => c != null && c.gameObject.activeSelf;
        private static int SideOf(Hand hand) => hand != null && hand.Handedness == Handedness.Left ? 0 : 1;

        private void Awake() { Instance = this; }
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var mesh in meshes) if (mesh != null) Destroy(mesh);
        }

        private void Start()
        {
            var material = arrival != null && arrival.guide != null ? arrival.guide.authoredBubble.GetComponent<MeshRenderer>().sharedMaterial : null;
            targetWidth = new AnimationCurve(new Keyframe(0f, .0012f), new Keyframe(1f, .0028f));
            for (int side = 0; side < 2; side++)
            {
                string name = side == 0 ? " L" : " R";
                // Ring cursor (a flat torus) with a dot that fills in while pinching.
                var ring = ToyMeshes.Torus(.16f, "FarCursorRing" + name);
                cursors[side] = Visual("Far Cursor" + name, ring, material, transform);
                var ringColors = new Color[ring.vertexCount];
                for (int i = 0; i < ringColors.Length; i++) ringColors[i] = Color.white;
                ring.colors = ringColors;
                cursorDots[side] = Visual("Dot", ToyMeshes.Disc(.55f, .05f, Color.white, Color.white, "FarCursorDot" + name), material, cursors[side]);
                cursors[side].gameObject.SetActive(false);
                var ray = new GameObject("Far Ray" + name).AddComponent<LineRenderer>();
                ray.transform.SetParent(transform, false);
                ray.sharedMaterial = material;
                ray.useWorldSpace = true;
                ray.positionCount = 2;
                ray.numCapVertices = 2;
                ray.widthCurve = targetWidth;
                ray.startColor = ray.endColor = Color.white;
                ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ray.receiveShadows = false;
                ray.gameObject.SetActive(false);
                farRays[side] = ray;
            }
            line = new GameObject("Far Elastic String").AddComponent<ElasticString>();
            line.transform.SetParent(transform, false);
            line.Build(material);
            line.gameObject.SetActive(false);
        }

        private Transform Visual(string name, Mesh mesh, Material material, Transform parent)
        {
            meshes.Add(mesh);
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        private Transform Head => arrival != null ? arrival.handoff.meeting.Head : null;

        private void Update()
        {
            if (arrival == null) return;
            for (int side = 0; side < 2; side++) cursorOn[side] = rayOn[side] = false;
            foreach (var hand in HandSet.Both())
            {
                bool connected = hand != null && hand.isActiveAndEnabled && (TestHands ? HandIO.Connected(hand) : hand.IsConnected);
                if (!connected || !HandIO.Pointer(hand, out Pose pointer)) // F86: from the pinch point; F89: always there while tracked
                {
                    if (hand != null && grips.ContainsKey(hand)) End(hand, false);
                    continue;
                }
                bool pinching = TestHands ? HandIO.RawPinching(hand) : hand.GetIndexFingerIsPinching();
                wasPinching.TryGetValue(hand, out bool before);
                wasPinching[hand] = pinching;
                var ray = new Ray(pointer.position, pointer.forward);
                Pose tip;
                bool tipKnown = TestHands ? HandIO.Joint(hand, HandJointId.HandIndexTip, out tip) : hand.GetJointPose(HandJointId.HandIndexTip, out tip);
                Vector3 pinchPoint = tipKnown ? tip.position : pointer.position;
                if (grips.TryGetValue(hand, out var grip))
                {
                    if (!pinching) End(hand, true);
                    else Drive(grip, ray, pinchPoint);
                    continue;
                }
                var maker = MakerSession.Instance != null && MakerSession.Instance.isActiveAndEnabled ? MakerSession.Instance : null;
                // F82: in the maker session the ray (and its ring) shows only as the thumb and index come together.
                if (!pinching && maker != null && !MakerSession.NearlyPinching(hand)) { aimedPet.Remove(hand); continue; }
                // F89: the ring and the full ray only on something. F101: aiming at nothing yet, a short tapering ray shows
                // where the hand points (as the Quest's own hand ray does), so the person can steer it onto a thing.
                // F102: in the maker session nothing is drawn until the fingers close toward a pinch (aiming goes on).
                quiet[SideOf(hand)] = maker != null && !pinching && !maker.FarRayVisible(hand);
                bool onSomething = AimHand(hand, SideOf(hand), ray, pinching, before, pinchPoint);
                if (!onSomething && AimStub && !pinching && maker != null && maker.FarStubAllowed(hand)) ShowStub(SideOf(hand), ray);
            }
            for (int side = 0; side < 2; side++)
            {
                if (!cursorOn[side] && Active(cursors[side])) cursors[side].gameObject.SetActive(false);
                if (!rayOn[side] && Active(farRays[side])) farRays[side].gameObject.SetActive(false);
            }
            UpdateString();
        }

        // What this hand's ray is on: its ring and ray (true), or nothing (false).
        private bool AimHand(Hand hand, int side, Ray ray, bool pinching, bool before, Vector3 pinchPoint)
        {
            // Pinching right on an earlier line in a deck pulls it out as a card in the hand.
            if (pinching && !before && BubbleInteractions.Instance != null &&
                BubbleInteractions.Instance.DeckSnapshotAt(pinchPoint) is PaperSnapshot touched)
            {
                BeginNear(hand, touched, pinchPoint);
                return true;
            }
            // UI targets (stickers, basket, notebook, notes panel) answer the pinch themselves;
            // they still get the ring cursor.
            if (BubbleInteractions.Instance != null && BubbleInteractions.Instance.UiHit(ray, out float uiDistance))
            {
                ShowCursor(side, ray.GetPoint(uiDistance), ray, pinching);
                return true;
            }
            // Maker: the coasters on the desk answer the pinch themselves.
            bool strict = Strict;
            if (strict)
            {
                // F66: what the session says this hand points at (near things and busy hands show no cursor); the
                // ring is small until a thing to move has been aimed at long enough.
                if (MakerSession.Instance.FarCursor(hand, out float aimDistance, out bool aimReady))
                {
                    // F103 (Quest: "I can't tell whether far touch works"): on a button the ring shows while the hand
                    // aims quietly too (no line from the hand: F102's wish stays), so the person sees what a pinch presses.
                    quietRing[side] = quiet[side] && MakerSession.Instance.FarCursorOnButton(hand);
                    ShowCursor(side, ray.GetPoint(aimDistance), ray, pinching, aimReady);
                    aimedPet.Remove(hand);
                    return true;
                }
            }
            else if (MakerSession.Instance != null && MakerSession.Instance.UiHit(ray, out float makerDistance))
            {
                ShowCursor(side, ray.GetPoint(makerDistance), ray, pinching);
                return true;
            }
            if (!Pick(ray, out var hit)) { aimedPet.Remove(hand); return false; }
            if (strict && hit.pet != null)
            {
                if (!MakerSession.Instance.FarCrewAllowed(hand, pinchPoint)) { aimedPet.Remove(hand); return false; }
                if (!pinching && (!aimedPet.TryGetValue(hand, out var lastPet) || lastPet != hit.pet)) { aimedPet[hand] = hit.pet; aimedSince[hand] = Time.time; }
                bool ready = aimedPet.TryGetValue(hand, out var aimed) && aimed == hit.pet && Time.time - aimedSince[hand] >= MakerSession.FarAimDwell;
                ShowCursor(side, hit.point, ray, pinching, ready);
                if (pinching && !before) { if (ready) Begin(hand, hit, ray, pinchPoint); else StrictRefusals++; }
                return true;
            }
            ShowCursor(side, hit.point, ray, pinching);
            if (pinching && !before) Begin(hand, hit, ray, pinchPoint);
            return true;
        }

        // ------------------------------------------------------------ picking

        private struct Hit
        {
            public TransferableSpeechBubble card;
            public CapsulePetController pet;
            public CharacterIntroCard tag;
            public PaperSnapshot snapshot;
            public float distance;
            public Vector3 point;
        }

        private bool Allowed(TransferableSpeechBubble card)
        {
            if (card == null || !card.isActiveAndEnabled || card.IsHeld) return false;
            if (card == arrival.TopicNote)
                return arrival.CurrentPhase == WindupsArrival.Phase.OfferingTopic || arrival.CurrentPhase == WindupsArrival.Phase.WaitingForTopic ||
                       arrival.CurrentPhase == WindupsArrival.Phase.ReviewingTopic ||
                       // the idea wall's pad (the wall can be out of reach): peel from afar, carry the note
                       arrival.PadNote != WindupsArrival.PadNoteState.None ||
                       // F78 airplane mode: the blank post-it Fixer holds out, or the one just written
                       (arrival.Collect != null && (arrival.Collect.Current == WindupsCollect.Step.Offering || arrival.Collect.Current == WindupsCollect.Step.Placing));
            // Speech cards: in the meeting (the guide's caption during onboarding stays put).
            return arrival.CurrentPhase == WindupsArrival.Phase.Meeting && !arrival.MakerActive;
        }

        private bool Allowed(CapsulePetController pet) =>
            pet != null && pet.isActiveAndEnabled && !pet.IsHeld && arrival.CurrentPhase >= WindupsArrival.Phase.WaitingForClaps &&
            arrival.CurrentPhase != WindupsArrival.Phase.Introducing;

        private bool Pick(Ray ray, out Hit best)
        {
            best = new Hit { distance = float.PositiveInfinity };
            bool found = false;
            foreach (var hit in Physics.RaycastAll(ray, 6f, ~0, QueryTriggerInteraction.Collide))
            {
                if (hit.distance < NearLimit || hit.distance >= best.distance) continue;
                var card = hit.collider.GetComponentInParent<TransferableSpeechBubble>();
                if (Allowed(card)) { best = new Hit { card = card, distance = hit.distance, point = hit.point }; found = true; continue; }
                var pet = hit.collider.GetComponentInParent<CapsulePetController>();
                if (card == null && Allowed(pet)) { best = new Hit { pet = pet, distance = hit.distance, point = hit.point }; found = true; }
            }
            if (BubbleInteractions.Instance != null)
            {
                var copy = BubbleInteractions.Instance.DeckSnapshotHit(ray, out float d);
                if (copy != null && d >= NearLimit && d < best.distance) { best = new Hit { snapshot = copy, distance = d, point = ray.GetPoint(d) }; found = true; }
            }
            var tag = arrival.TagHit(ray, out float tagDistance);
            if (tag != null && tagDistance >= NearLimit && tagDistance < best.distance + .05f)
            {
                best = new Hit { tag = tag, distance = tagDistance, point = ray.GetPoint(tagDistance) };
                found = true;
            }
            return found;
        }

        // F101: the hand's ray while it aims at nothing: StubLength long, tapering to nothing.
        // F102: off (the user: the ray coming out of the hand when not wanted is disrupting).
        public static bool AimStub = false;
        private readonly bool[] quiet = new bool[2]; // F102: this side aims without drawing anything
        private readonly bool[] quietRing = new bool[2]; // F103: ... but on a button its ring shows (no line)
        public int QuietRings { get; private set; }
        public bool RingShown(bool left) => Active(cursors[left ? 0 : 1]);
        public bool LineShown(bool left) => Active(farRays[left ? 0 : 1]);
        public bool QuietSide(bool left) => quiet[left ? 0 : 1];
        public static float StubLength = .2f;
        private AnimationCurve stubWidth;
        private readonly int[] rayMode = new int[2]; // 0: to the ring, 1: the stub
        public int StubFrames { get; private set; }
        public bool StubVisible(bool left) => Active(farRays[left ? 0 : 1]) && rayMode[left ? 0 : 1] == 1;
        private void ShowStub(int side, Ray ray)
        {
            var farRay = farRays[side];
            if (farRay == null || rayOn[side]) return;
            rayOn[side] = true;
            if (!farRay.gameObject.activeSelf) farRay.gameObject.SetActive(true);
            if (rayMode[side] != 1)
            {
                stubWidth ??= new AnimationCurve(new Keyframe(0f, .0022f), new Keyframe(1f, 0f));
                farRay.widthCurve = stubWidth;
                rayMode[side] = 1;
            }
            farRay.SetPosition(0, ray.origin + ray.direction * .02f);
            farRay.SetPosition(1, ray.origin + ray.direction * (.02f + StubLength));
            StubFrames++;
        }

        private void ShowCursor(int side, Vector3 point, Ray ray, bool pressed, bool ready = true)
        {
            var cursor = cursors[side];
            bool ringOnly = quiet[side] && quietRing[side];
            quietRing[side] = false;
            if (cursor == null || cursorOn[side] || (quiet[side] && !ringOnly)) return; // (F102: aiming quietly, nothing drawn)
            cursorOn[side] = true;
            if (ringOnly) QuietRings++;
            if (!cursor.gameObject.activeSelf) cursor.gameObject.SetActive(true);
            float distance = Vector3.Distance(ray.origin, point);
            float radius = .0045f + distance * .0055f;
            cursor.SetPositionAndRotation(point - ray.direction * .003f, Quaternion.LookRotation(ray.direction, Vector3.up));
            cursor.localScale = Vector3.one * radius * (pressed ? .75f : ringOnly ? .8f : ready ? 1f : .5f);
            if (cursorDots[side].gameObject.activeSelf != pressed) cursorDots[side].gameObject.SetActive(pressed);
            var farRay = farRays[side];
            if (farRay != null && !ringOnly)
            {
                rayOn[side] = true;
                if (!farRay.gameObject.activeSelf) farRay.gameObject.SetActive(true);
                if (rayMode[side] != 0) { farRay.widthCurve = targetWidth; rayMode[side] = 0; }
                // From just ahead of the pinch point (F86: the ray starts between the thumb and index tips) to the
                // ring's edge (the ring stays clear).
                Vector3 from = ray.origin + ray.direction * Mathf.Min(.015f, distance * .3f);
                Vector3 to = point - ray.direction * (.003f + radius * .9f);
                if (Vector3.Dot(to - from, ray.direction) < .01f) to = from + ray.direction * .01f;
                farRay.SetPosition(0, from);
                farRay.SetPosition(1, to);
            }
        }

        // ------------------------------------------------------------ grips

        private void Begin(object key, Hit hit, Ray ray, Vector3 pinchPoint)
        {
            var head = Head;
            Transform target = hit.card != null ? hit.card.transform : hit.pet != null ? hit.pet.transform : hit.snapshot != null ? hit.snapshot.transform : null;
            var grip = new Grip
            {
                pointer = nextPointer++, card = hit.card, pet = hit.pet, tag = hit.tag, snapshot = hit.snapshot, distance = hit.distance,
                startedAt = Time.time, startPoint = pinchPoint,
                startDepth = head != null ? Vector3.Dot(pinchPoint - head.position, head.forward) : 0f,
                offset = target != null ? target.position - hit.point : Vector3.zero
            };
            grips[key] = grip;
        }

        private void BeginNear(object key, PaperSnapshot snapshot, Vector3 pinchPoint)
        {
            var grip = new Grip { pointer = nextPointer++, snapshot = snapshot, near = true, startedAt = Time.time, startPoint = pinchPoint };
            grips[key] = grip;
            grip.offset = snapshot.transform.position - pinchPoint;
            StartDrag(grip);
        }

        // Turn a pending pinch into a drag once the hand moves or keeps pinching.
        private void Drive(Grip grip, Ray ray, Vector3 pinchPoint)
        {
            if (!grip.dragging)
            {
                if (grip.tag != null) return; // name tags only take taps
                if (grip.pet != null && !grip.near && Strict && Time.time - grip.startedAt < MakerSession.FarHold) return; // F66: held first, then it moves
                if (!grip.near && Time.time - grip.startedAt < TapTime && Vector3.Distance(pinchPoint, grip.startPoint) < DragMove) return;
                if (!StartDrag(grip)) return;
            }
            Vector3 position;
            if (grip.near) position = pinchPoint + grip.offset;
            else
            {
                var head = Head;
                float depth = head != null ? Vector3.Dot(pinchPoint - head.position, head.forward) : grip.startDepth;
                float distance = Mathf.Clamp(grip.distance + (depth - grip.startDepth) * PushGain, .3f, 4f);
                position = ray.GetPoint(distance) + grip.offset;
            }
            // Cards turn to face the user while carried; characters stay upright as they are.
            var rotation = grip.pet != null ? grip.target.transform.rotation : SpeechBubbleDock.FacingAt(position);
            grip.pose = new Pose(position, rotation);
            grip.target.ProcessPointerEvent(new PointerEvent(grip.pointer, PointerEventType.Move, grip.pose));
        }

        private bool StartDrag(Grip grip)
        {
            if (grip.snapshot != null)
            {
                grip.card = BubbleInteractions.Instance != null ? BubbleInteractions.Instance.PromoteSnapshot(grip.snapshot) : null;
                grip.snapshot = null;
                if (grip.card == null) return false;
                // A fresh copy's Grabbable sets up its transformers in Start: wait for it.
                grip.promoted = true;
                grip.readyFrame = Time.frameCount + 2;
            }
            if (Time.frameCount < grip.readyFrame) return false;
            if (grip.pet != null)
            {
                if (grip.pet.IsHeld) return false;
                grip.target = grip.pet.GetComponent<Grabbable>();
            }
            else
            {
                if (grip.card == null || grip.card.IsHeld) return false;
                grip.target = grip.card.GetComponent<Grabbable>();
            }
            if (grip.target == null) return false;
            grip.dragging = true;
            var t = grip.target.transform;
            grip.attachLocal = t.InverseTransformPoint(t.position - grip.offset);
            grip.pose = new Pose(t.position, t.rotation);
            grip.bend = t.position;
            grip.bendVelocity = Vector3.zero;
            grip.target.ProcessPointerEvent(new PointerEvent(grip.pointer, PointerEventType.Hover, grip.pose));
            grip.target.ProcessPointerEvent(new PointerEvent(grip.pointer, PointerEventType.Select, grip.pose));
            ToySounds.Play(ToySounds.Cue.Paper, grip.pose.position, .45f, 1.25f);
            return true;
        }

        private void End(object key, bool released)
        {
            if (!grips.TryGetValue(key, out var grip)) return;
            grips.Remove(key);
            if (grip.dragging && grip.target != null)
            {
                grip.target.ProcessPointerEvent(new PointerEvent(grip.pointer, released ? PointerEventType.Unselect : PointerEventType.Cancel, grip.pose));
                grip.target.ProcessPointerEvent(new PointerEvent(grip.pointer, PointerEventType.Unhover, grip.pose));
                return;
            }
            // Pulled out of a deck but let go before the drag began: it stays where it is.
            if (grip.promoted && grip.card != null) { BubbleInteractions.Instance?.SettleFree(grip.card); return; }
            if (!released || Time.time - grip.startedAt > TapTime) return;
            // A quick pinch-and-release: a tap, the same as poking it up close.
            if (grip.tag != null) arrival.ToggleCard(grip.tag);
            else if (grip.pet != null) arrival.TogglePetTag(grip.pet);
            else if (grip.snapshot != null) BubbleInteractions.Instance?.TapSnapshot(grip.snapshot);
            else if (grip.card != null && grip.card == arrival.TopicNote) arrival.TopicNoteTapped();
            else if (grip.card != null) BubbleInteractions.Instance?.RemoteTap(grip.card);
        }

        // The elastic string: from the hand to the point that was grabbed, bowing and wobbling
        // as the object lags behind the hand.
        private void UpdateString()
        {
            if (line == null) return;
            Grip active = null;
            object owner = null;
            foreach (var pair in grips) if (pair.Value.dragging && !pair.Value.near && pair.Value.target != null) { active = pair.Value; owner = pair.Key; break; }
            Vector3 from = default;
            bool show = false;
            if (active != null)
            {
                if (owner is Hand hand) { if (HandIO.Pointer(hand, out Pose pointer)) { from = pointer.position; show = true; } }
                else { from = simulatedOrigin; show = true; }
            }
            if (line.gameObject.activeSelf != show) line.gameObject.SetActive(show);
            if (!show) return;
            Vector3 to = active.target.transform.TransformPoint(active.attachLocal);
            float length = Vector3.Distance(from, to);
            Vector3 rest = (from + to) * .5f + Vector3.down * (.03f + length * .05f);
            float dt = Mathf.Min(Time.deltaTime, .05f);
            active.bendVelocity += ((rest - active.bend) * 90f - active.bendVelocity * 9f) * dt;
            active.bend += active.bendVelocity * dt;
            line.Draw(from, active.bend, to);
        }

        // ------------------------------------------------------------ Editor / test entry points

        private const string Simulated = "simulated hand";
        private Vector3 simulatedOrigin;
        public bool SimulatePinch(Ray ray, Vector3 pinchPoint)
        {
            if (!Pick(ray, out var hit)) return false;
            simulatedOrigin = ray.origin;
            Begin(Simulated, hit, ray, pinchPoint);
            return true;
        }
        public bool SimulatePinchOnDeck(PaperSnapshot snapshot, Vector3 pinchPoint)
        {
            if (snapshot == null) return false;
            BeginNear(Simulated, snapshot, pinchPoint);
            return grips.ContainsKey(Simulated) && grips[Simulated].card != null;
        }
        public void SimulateMove(Ray ray, Vector3 pinchPoint)
        {
            simulatedOrigin = ray.origin;
            if (grips.TryGetValue(Simulated, out var grip)) Drive(grip, ray, pinchPoint);
        }
        public void SimulateRelease() => End(Simulated, true);
        public TransferableSpeechBubble SimulatedCard => grips.TryGetValue(Simulated, out var grip) ? grip.card : null;
        // F68 checks: the ring and the ray as a hand aimed along `ray` at `point` would show them (for one frame).
        public void SimulateCursor(Vector3 point, Ray ray) { cursorOn[1] = rayOn[1] = false; ShowCursor(1, point, ray, false); }
        public Vector3 FarRayEnd => farRays[1] != null ? farRays[1].GetPosition(1) : Vector3.zero;
        public Vector3 FarRayEndOf(bool left) => farRays[left ? 0 : 1] != null ? farRays[left ? 0 : 1].GetPosition(1) : Vector3.zero;
        public bool SimulateHover(Ray ray)
        {
            bool hit = Pick(ray, out var found) || (BubbleInteractions.Instance != null && BubbleInteractions.Instance.UiHit(ray, out _));
            if (hit && found.distance < float.PositiveInfinity) { cursorOn[1] = rayOn[1] = false; ShowCursor(1, found.point, ray, false); }
            return hit;
        }
    }

    // A thin white tube along a quadratic curve (hand -> bend -> grabbed point).
    public sealed class ElasticString : MonoBehaviour
    {
        private const int Segments = 24, Sides = 6;
        private const float Radius = .0011f;
        private Mesh mesh;
        private Vector3[] vertices;
        private Vector3[] normals;

        public void Build(Material material)
        {
            mesh = new Mesh { name = "ElasticString" };
            mesh.MarkDynamic();
            int count = (Segments + 1) * Sides;
            vertices = new Vector3[count];
            normals = new Vector3[count];
            var colors = new Color[count];
            for (int i = 0; i < count; i++) colors[i] = Color.white;
            var triangles = new List<int>();
            for (int s = 0; s < Segments; s++)
                for (int k = 0; k < Sides; k++)
                {
                    int a = s * Sides + k, b = s * Sides + (k + 1) % Sides, c = a + Sides, d = b + Sides;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetTriangles(triangles, 0);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        public void Draw(Vector3 from, Vector3 bend, Vector3 to)
        {
            if (mesh == null) return;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Vector3 up = Vector3.up;
            for (int s = 0; s <= Segments; s++)
            {
                float t = s / (float)Segments, u = 1f - t;
                Vector3 p = u * u * from + 2f * u * t * bend + t * t * to;
                Vector3 tangent = (2f * u * (bend - from) + 2f * t * (to - bend)).normalized;
                if (tangent.sqrMagnitude < .5f) tangent = Vector3.forward;
                Vector3 side = Vector3.Cross(tangent, Mathf.Abs(Vector3.Dot(tangent, up)) > .95f ? Vector3.right : up).normalized;
                Vector3 other = Vector3.Cross(tangent, side);
                // Slightly thinner in the middle, like a stretched band.
                float r = Radius * (1f - .35f * Mathf.Sin(t * Mathf.PI));
                for (int k = 0; k < Sides; k++)
                {
                    float a = k / (float)Sides * Mathf.PI * 2f;
                    Vector3 n = side * Mathf.Cos(a) + other * Mathf.Sin(a);
                    vertices[s * Sides + k] = p + n * r;
                    normals[s * Sides + k] = n;
                }
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        private void OnDestroy() { if (mesh != null) Destroy(mesh); }
    }
}
