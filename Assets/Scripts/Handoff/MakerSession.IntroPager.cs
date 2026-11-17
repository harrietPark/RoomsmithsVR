using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // 2026-11-13 (the user after a Quest try: "the introduction goes by too fast - an arrow on Maker's bubble so I
    // can go on myself, < and >?"; they chose "only when I press >"). During the team introduction
    // (WindupsArrival.IntroduceTeam) Maker's speech card carries two round arrow buttons on its lower corners:
    //  - > shows the next line (with the tool it is about); it never goes on by itself. Once the line has been up
    //    long enough to read (ReadTime), > bounces now and then so the person knows where to go on;
    //  - < shows the line before again (and its tool); it is not there on the first line.
    // Pressed like every button (F60): a free pointing fingertip resting on it (FingerPress), a pinch on it near
    // or aimed with the hand's ray, an Editor click or the arrow keys. Checks (and the Editor's skip) go on by
    // themselves (IntroAutoAdvance / WindupsArrival.PreviewSkipping).
    public sealed partial class MakerSession
    {
        public const float PagerRadius = .012f, PagerBounceEvery = 2.6f;
        public static bool IntroAutoAdvance;          // checks: the introduction goes on by itself (old timing)
        public bool PagerShown => pagerRoot != null && pagerRoot.gameObject.activeSelf;
        public bool PagerBackShown => PagerShown && pagerBack != null && pagerBack.gameObject.activeSelf;
        public int PagerPresses { get; private set; }
        public Transform PagerNextView => pagerNext;
        public Transform PagerBackView => pagerBack;

        private Transform pagerRoot, pagerNext, pagerBack;
        private int pagerStep;                      // +1 / -1 waiting to be taken by the introduction
        private float pagerShownAt, pagerReadAt, pagerBounceAt, pagerPressAt = -10f;
        private int pagerPressedSide;
        private readonly FingerPress pagerNextPress = new FingerPress(), pagerBackPress = new FingerPress();
        private readonly Dictionary<Hand, bool> pagerWasPinching = new Dictionary<Hand, bool>();

        // A new line is up: show the arrows (< only when there is a line before); > may bounce after `read` seconds.
        public void ShowIntroPager(bool canGoBack, float read)
        {
            EnsureCrew();
            BuildPager();
            pagerStep = 0;
            if (!pagerRoot.gameObject.activeSelf)
            {
                pagerRoot.gameObject.SetActive(true);
                pagerShownAt = Time.time;
                PlacePager(1f);
            }
            pagerBack.gameObject.SetActive(canGoBack);
            pagerReadAt = Time.time + read;
            pagerBounceAt = pagerReadAt;
        }

        public void HideIntroPager()
        {
            if (pagerRoot != null) pagerRoot.gameObject.SetActive(false);
            pagerStep = 0;
            pagerNextPress.Clear();
            pagerBackPress.Clear();
        }

        // The introduction takes the press (+1 next, -1 back, 0 none yet).
        public int TakeIntroPage()
        {
            int step = pagerStep;
            pagerStep = 0;
            return step;
        }

        // Checks and the Editor: press an arrow as a fingertip would.
        public const float PagerPressGap = .45f; // one press per this long, whatever pressed it (the double was 0.3 s apart)
        public void PressIntroPager(int step)
        {
            if (!PagerShown || (step < 0 && !PagerBackShown)) return;
            // (Quest 2026-11-13: one press counted twice: the fingertip press, then the same hand's pinch 0.3 s later.)
            if (Time.time - pagerPressAt < PagerPressGap) return;
            PagerPresses++;
            pagerStep = step > 0 ? 1 : -1;
            pagerPressAt = Time.time;
            pagerPressedSide = pagerStep;
            var button = step > 0 ? pagerNext : pagerBack;
            ToySounds.Play(ToySounds.Cue.Press, button.position, .45f, step > 0 ? 1.6f : 1.4f);
            ToyFx.Puff(button.position, .004f, 2);
        }

        // ------------------------------------------------------------ the buttons

        // A white disc ringed in Maker's colour with a chevron (two short bars) pointing the way.
        private void BuildPager()
        {
            if (pagerRoot != null) return;
            pagerRoot = new GameObject("Intro Pager").transform;
            pagerRoot.SetParent(transform, false);
            pagerNext = PagerButton("Next", 1f);
            pagerBack = PagerButton("Back", -1f);
            pagerRoot.gameObject.SetActive(false);
        }

        private Transform PagerButton(string name, float way)
        {
            Color role = MakerRoles.Color(Crew.Maker), dark = Color.Lerp(role, Color.black, .25f);
            var button = new GameObject(name).transform;
            button.SetParent(pagerRoot, false);
            var ring = Part("Ring", StickerMesh("pagerRing", () => ToyMeshes.Disc(PagerRadius, .004f, role, dark, "PagerRing")));
            ring.SetParent(button, false);
            ring.localPosition = new Vector3(0f, 0f, .001f);
            var face = Part("Face", StickerMesh("pagerFace", () => ToyMeshes.Disc(PagerRadius - .0022f, .0045f, Color.white, Color.Lerp(Color.white, role, .2f), "PagerFace")));
            face.SetParent(button, false);
            face.localPosition = new Vector3(0f, 0f, -.0005f);
            // The chevron: two bars meeting at the point, ">" for next and "<" for back.
            var bar = StickerMesh("pagerBar", () => ToyMeshes.RoundedSlab(.0085f, .0026f, .0013f, .0012f, role, dark, role, "PagerBar"));
            for (int i = 0; i < 2; i++)
            {
                float up = i == 0 ? 1f : -1f;
                var piece = Part("Chevron", bar);
                piece.SetParent(button, false);
                piece.localRotation = Quaternion.Euler(0f, 0f, -45f * up * way);
                piece.localPosition = new Vector3(-.0013f * way, .0026f * up, -.003f);
            }
            return button;
        }

        // Beside Maker's card halfway up (F68; they were on its lower corners), a little in front; they follow the card.
        private void PlacePager(float blend)
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null || maker.authoredBubble == null) return;
            var card = maker.authoredBubble.transform;
            Vector3 half = new Vector3(.09f, .03f, 0f);
            if (card.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
            {
                var b = filter.sharedMesh.bounds;
                half = new Vector3(b.extents.x * card.lossyScale.x, b.extents.y * card.lossyScale.y, 0f);
            }
            Quaternion turn = card.rotation;
            Vector3 centre = card.position + turn * new Vector3(0f, 0f, -.006f); // F68: halfway up the card
            pagerRoot.SetPositionAndRotation(Vector3.Lerp(pagerRoot.position, centre, blend), Quaternion.Slerp(pagerRoot.rotation, turn, blend));
            // Next on the person's right, back on their left (the card's +x is their right, as on a world canvas).
            float across = Mathf.Max(.03f, half.x + PagerRadius * PagerScale + .004f); // F68: just outside the card's sides
            pagerNext.localPosition = new Vector3(across, 0f, 0f);
            pagerBack.localPosition = new Vector3(-across, 0f, 0f);
        }

        private void IntroPagerFrame()
        {
            if (!PagerShown) return;
            var maker = cast.TryGetValue(Crew.Maker, out var m) ? m : null;
            if (maker == null || maker.authoredBubble == null || !maker.authoredBubble.gameObject.activeInHierarchy)
            {
                pagerRoot.gameObject.SetActive(false);
                return;
            }
            PlacePager(1f - Mathf.Exp(-Time.deltaTime * 14f));
            // Grow in; after the reading time > bounces now and then; a pressed button squashes for a moment.
            float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - pagerShownAt) / .25f));
            float bounce = 1f;
            if (Time.time >= pagerBounceAt)
            {
                float since = Time.time - pagerBounceAt;
                if (since < .7f) bounce = 1f + .22f * Mathf.Sin(since / .7f * Mathf.PI * 2f) * (1f - since / .7f);
                else pagerBounceAt = Time.time + PagerBounceEvery;
            }
            float pressed = Time.time - pagerPressAt < .18f ? .82f : 1f;
            // F67 (the user: "make the intro's buttons work from a distance too"): an arrow a free hand points at grows
            // (PagerHoverScale), so a pinch from afar is seen to land on it.
            int hover = 0;
            foreach (var hand in HandSet.Both())
                if (hover == 0 && hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && !HandIO.Pinching(hand) && HandFree(hand) && HandIO.Pointer(hand, out Pose aim))
                    hover = PagerPinch(Vector3.positiveInfinity, new Ray(aim.position, aim.forward));
            PagerHover = hover;
            pagerNext.localScale = Vector3.one * PagerScale * grow * bounce * (pagerPressedSide > 0 ? pressed : 1f) * (hover > 0 ? PagerHoverScale : 1f);
            pagerBack.localScale = Vector3.one * PagerScale * grow * (pagerPressedSide < 0 ? pressed : 1f) * (hover < 0 ? PagerHoverScale : 1f);
            if (grow < .6f || pagerStep != 0) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand))
                {
                    if (hand != null) { pagerNextPress.Forget(hand); pagerBackPress.Forget(hand); pagerWasPinching.Remove(hand); }
                    continue;
                }
                bool tipKnown = HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip);
                if (pagerNextPress.Step(this, hand, tipKnown && OnPagerButton(pagerNext, tip.position, .004f))) { PressLog("intro next", hand); PressIntroPager(1); return; }
                if (PagerBackShown && pagerBackPress.Step(this, hand, tipKnown && OnPagerButton(pagerBack, tip.position, .004f))) { PressLog("intro back", hand); PressIntroPager(-1); return; }
                bool pinching = HandIO.Pinching(hand);
                pagerWasPinching.TryGetValue(hand, out bool before);
                pagerWasPinching[hand] = pinching;
                if (!pinching || before || !HandFree(hand)) continue;
                bool aimed = HandIO.Pointer(hand, out Pose pointer);
                int step = PagerPinch(PinchPoint(hand), aimed ? new Ray(pointer.position, pointer.forward) : (Ray?)null);
                if (step != 0) { PressLog(step > 0 ? "intro next (pinch)" : "intro back (pinch)", hand); PressIntroPager(step); return; }
            }
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var keys = UnityEngine.InputSystem.Keyboard.current;
                if (keys != null && keys.rightArrowKey.wasPressedThisFrame) { PressIntroPager(1); return; }
                if (keys != null && keys.leftArrowKey.wasPressedThisFrame) { PressIntroPager(-1); return; }
                if (Camera.main != null && UnityEngine.InputSystem.Mouse.current is { } mouse && mouse.leftButton.wasPressedThisFrame)
                {
                    int step = PagerPinch(Vector3.positiveInfinity, Camera.main.ScreenPointToRay(mouse.position.ReadValue()));
                    if (step != 0) PressIntroPager(step);
                }
            }
#endif
        }

        public const float PagerHoverScale = 1.18f, PagerFarSlack = .008f, PagerFarSlackPerMetre = .02f;
        // F68 (Quest 2026-11-14: "bigger, it is hard to press from afar; in the middle, not at the bottom"): 1.6 times the
        // size (3.8 cm across) and halfway up the card, just outside its sides.
        public const float PagerScale = 1.6f;
        public int PagerHover { get; private set; } // the arrow a free hand points at: +1 next, -1 back (checks)

        // A pinch on an arrow (near), or the hand's ray (or the Editor mouse) on it: +1, -1, or 0.
        private int PagerPinch(Vector3 point, Ray? ray)
        {
            if (OnPagerButton(pagerNext, point, .01f)) return 1;
            if (PagerBackShown && OnPagerButton(pagerBack, point, .01f)) return -1;
            if (!ray.HasValue || !new Plane(pagerRoot.forward, pagerRoot.position).Raycast(ray.Value, out float d) || d <= 0f || d > 4f) return 0;
            Vector3 hit = ray.Value.GetPoint(d);
            float slack = PagerFarSlack + d * PagerFarSlackPerMetre; // F67: 4 mm + 6 mm/m was too tight from afar
            float next = Vector3.Distance(hit, pagerNext.position), back = PagerBackShown ? Vector3.Distance(hit, pagerBack.position) : 9f;
            if (next <= PagerRadius * PagerScale + slack && next <= back) return 1;
            if (back <= PagerRadius * PagerScale + slack) return -1;
            return 0;
        }

        private static bool OnPagerButton(Transform button, Vector3 world, float margin)
        {
            if (button == null || float.IsInfinity(world.x)) return false;
            Vector3 l = button.InverseTransformPoint(world);
            return new Vector2(l.x, l.y).magnitude <= PagerRadius + Mathf.Max(margin, PressMargin) && l.z >= PressNear - .01f && l.z <= PressThrough; // F67: through it counts
        }
    }
}
