using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F77 (2026-11-14, the user: "too many buttons, confusing words, all in different places - and some things look like
    // buttons but are not"):
    //  - A button is raised: its face (a cap) stands in front of a darker socket in the asker's colour; under a fingertip
    //    the cap starts to sink, pressed it goes all the way down, lights up, clicks and sends a ripple out, then springs
    //    back (the F71 PageButton feel, for the reply pills, Done, Find ideas and the lesson card's Skip).
    //  - A label that cannot be pressed is flat paper with a small punched hole, no rim and no bold (PaperTag): version
    //    tags, sizes, names of real things, flag words, a card's role, pros and cons.
    //  - The lesson card stands beside its teacher's speech bubble (MakerSession.FormFirst.cs CardBeside) and carries a
    //    Skip button in its top-right corner: the lesson (all its steps) is over and taught; the step still waits for what
    //    it needs (a size), optional lesson steps are skipped.
    public sealed partial class MakerSession
    {
        public const float PillRise = .0045f, PillSink = .0035f;

        private sealed class RaisedPill
        {
            public Transform root, cap;
            public MeshFilter face;
            public Mesh normal, lit;
            public Color role;
            public Vector2 size;
            public float pressedAt = -10f, sink, near = float.MaxValue;
        }

        private readonly List<RaisedPill> raisedPills = new List<RaisedPill>();
        private readonly Vector3[] raisedTips = new Vector3[2];
        private int raisedTipCount;

        // A Panel-built pill becomes a raised button: its Card and Text go into a cap PillRise in front of a socket.
        private RaisedPill MakeRaised(Transform pill, float width, float height, Color face, Color role)
        {
            var r = new RaisedPill { root = pill, role = role, size = new Vector2(width, height) };
            r.cap = new GameObject("Cap").transform;
            r.cap.SetParent(pill, false);
            r.cap.localPosition = new Vector3(0f, 0f, -PillRise);
            var card = pill.Find("Card");
            var text = pill.Find("Text");
            if (card != null) card.SetParent(r.cap, false);
            if (text != null) text.SetParent(r.cap, false);
            Color dark = Color.Lerp(role, Color.black, .28f);
            float sw = width + .005f, sh = height + .005f;
            var socket = ToyMeshes.RoundedSlab(sw, sh, Mathf.Min(sh * .45f, .012f), .0025f, dark, Color.Lerp(role, Color.black, .45f), dark, "PillSocket");
            owned.Add(socket);
            var s = Part("Socket", socket);
            s.SetParent(pill, false);
            s.localPosition = Vector3.zero;
            r.face = card != null ? card.GetComponent<MeshFilter>() : null;
            r.normal = r.face != null ? r.face.sharedMesh : null;
            Color lit = Color.Lerp(face, Color.white, .6f);
            r.lit = ToyMeshes.RoundedSlab(width, height, Mathf.Min(height * .45f, .01f), .003f, lit, Color.Lerp(face, Color.black, .05f), lit, "PillLit");
            owned.Add(r.lit);
            raisedPills.Add(r);
            return r;
        }

        private RaisedPill RaisedOf(Transform pill)
        {
            for (int i = 0; i < raisedPills.Count; i++) if (raisedPills[i].root == pill) return raisedPills[i];
            return null;
        }

        // Pressed (fingertip, pinch, ray or mouse): down, lit, a ripple. The caller plays the click.
        private void PressRaised(Transform pill)
        {
            var r = RaisedOf(pill);
            if (r == null) return;
            r.pressedAt = Time.time;
            r.sink = Mathf.Max(r.sink, .6f);
            // F79 (Quest: "the outline when Find ideas is pressed is not nice"): no ripple; down, lit and the click only.
        }

        // Tests: how far a button's cap is down (0 up, 1 all the way) and whether it is lit.
        public float PillSinkOf(Transform pill) => RaisedOf(pill)?.sink ?? 0f;
        public bool PillLitOf(Transform pill) { var r = RaisedOf(pill); return r != null && r.face != null && r.face.sharedMesh == r.lit; }
        public bool PillIsRaised(Transform pill) => RaisedOf(pill) != null;

        // Every frame (frame step "buttons"): hover sink, press travel, light and spring for every raised pill.
        private void RaisedPillsFrame()
        {
            raisedTipCount = 0;
            foreach (var hand in HandSet.Both())
            {
                if (raisedTipCount >= raisedTips.Length) break;
                if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand) && HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                    raisedTips[raisedTipCount++] = tip.position;
            }
            for (int i = raisedPills.Count - 1; i >= 0; i--)
            {
                var r = raisedPills[i];
                if (r.root == null || r.cap == null) { raisedPills.RemoveAt(i); continue; }
                if (!r.root.gameObject.activeInHierarchy) continue;
                r.near = float.MaxValue;
                if (r.root.lossyScale.x > .05f)
                    for (int k = 0; k < raisedTipCount; k++)
                    {
                        Vector3 l = r.root.InverseTransformPoint(raisedTips[k]);
                        float off = new Vector3(Mathf.Max(0f, Mathf.Abs(l.x) - r.size.x * .5f), Mathf.Max(0f, Mathf.Abs(l.y) - r.size.y * .5f),
                            Mathf.Max(0f, -l.z - PillRise)).magnitude;
                        r.near = Mathf.Min(r.near, off);
                    }
                float since = Time.time - r.pressedAt;
                bool down = since < .28f || (since < 2f && r.near < .012f);
                float hover = Mathf.Clamp01(1f - r.near / .03f);
                // F102: a hand aiming at it from afar makes it sink a little too (the only sign before the fingers close).
                for (int k = 0; k < farHoverCount; k++)
                {
                    Vector3 l = r.root.InverseTransformPoint(farHover[k]);
                    if (Mathf.Abs(l.x) <= r.size.x * .5f + .004f && Mathf.Abs(l.y) <= r.size.y * .5f + .004f && Mathf.Abs(l.z) < .03f) { hover = Mathf.Max(hover, .8f); break; }
                }
                float want = down ? 1f : .3f * hover;
                r.sink = Mathf.MoveTowards(r.sink, want, Time.deltaTime / (want > r.sink ? .04f : .12f));
                float spring = !down && since < .62f && since > .28f ? -.25f * Mathf.Sin((since - .28f) / .34f * Mathf.PI) : 0f;
                r.cap.localPosition = new Vector3(0f, 0f, -PillRise + PillSink * (r.sink + spring));
                if (r.face != null && r.normal != null) r.face.sharedMesh = down || since < .35f ? r.lit : r.normal;
            }
        }

        // A band in the button's colour spreads out from behind it.
        private IEnumerator PillRipple(RaisedPill r)
        {
            if (r.root == null) yield break;
            Color c = Color.Lerp(r.role, Color.white, .35f);
            float w = r.size.x + .005f, h = r.size.y + .005f;
            var mesh = ToyMeshes.RoundedSlab(w, h, Mathf.Min(h * .45f, .012f), .0006f, c, c, c, "PillRipple");
            var ring = Part("Press Ripple", mesh);
            ring.SetParent(r.root, false);
            ring.localPosition = new Vector3(0f, 0f, .003f);
            for (float t = 0f; t < 1f && ring != null && r.root != null; t += Time.deltaTime / .35f)
            {
                float grow = .014f * (1f - (1f - t) * (1f - t));
                ring.localScale = new Vector3((w + 2f * grow) / w, (h + 2f * grow) / h, 1f);
                yield return null;
            }
            if (ring != null) Destroy(ring.gameObject);
            Destroy(mesh);
        }

        // ------------------------------------------------------------ labels that are not buttons

        // Flat paper (no rim, no bold) with a small punched hole on the left when there is room: never mistaken for a
        // button. The face is a child "Card" and the words a child "Text", as with Panel.
        private Transform PaperTag(string name, float width, float height, Color color, out TextMeshProUGUI text, float size, bool hole = true)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            var slab = ToyMeshes.RoundedSlab(width, height, Mathf.Min(height * .16f, .0025f), .0007f, color, color, color, name + " Paper");
            owned.Add(slab);
            var go = new GameObject("Card", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(root, false);
            go.GetComponent<MeshFilter>().sharedMesh = slab;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            float left = 0f;
            if (hole && width > height * 1.4f)
            {
                float radius = Mathf.Clamp(height * .11f, .0011f, .0022f);
                Color holeColour = Color.Lerp(color, Color.black, .35f);
                var disc = ToyMeshes.Disc(radius, .0002f, holeColour, holeColour, name + " Hole");
                owned.Add(disc);
                var punched = Part("Hole", disc);
                punched.SetParent(root, false);
                punched.localPosition = new Vector3(-width * .5f + radius + .0022f, 0f, -.0002f);
                left = radius * 2f + .003f;
            }
            var canvas = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(root, false);
            rect.sizeDelta = new Vector2((width - left) * 1000f, height * 1000f);
            rect.localScale = Vector3.one * .001f;
            rect.localPosition = new Vector3(left * .5f, 0f, -.0012f);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            text = HatIdeaCard.Label(rect, font, 3f, 2f, (width - left) * 1000f - 6f, height * 1000f - 4f, size * .6f, size,
                new Color(.2f, .2f, .23f), FontStyles.Normal, TextAlignmentOptions.Center);
            text.richText = true;
            return root;
        }

        // Tests: is this thing a paper label (made by PaperTag)?
        public static bool IsPaperTag(Transform t) => t != null && t.Find("Card") != null && t.Find("Card").GetComponent<MeshFilter>()?.sharedMesh != null &&
            t.Find("Card").GetComponent<MeshFilter>().sharedMesh.name.EndsWith(" Paper");

        // ------------------------------------------------------------ the lesson card's Skip

        public const float CardSkipW = .044f, CardSkipH = .02f;
        private static readonly Color SkipFace = new Color(.38f, .42f, .5f);
        private Transform cardSkip;
        private readonly FingerPress cardSkipPress = new FingerPress();
        private bool cardSkipPressing;
        public int CardSkips { get; private set; }
        public Transform CardSkipButton => cardSkip;
        public readonly List<string> LessonsSkipped = new List<string>();

        // On the card's top-right corner, inside the card: "Skip" with an X.
        private void BuildCardSkip()
        {
            if (card == null || card.root == null) return;
            Color role = Color.Lerp(SkipFace, Color.black, .15f);
            var pill = Panel("Lesson Skip", CardSkipW, CardSkipH, SkipFace, out var text, 11f);
            text.text = "Skip";
            text.color = Color.white;
            text.fontStyle = FontStyles.Bold;
            var rect = text.rectTransform;
            rect.anchoredPosition += new Vector2(-5f, 0f);
            var raised = MakeRaised(pill, CardSkipW, CardSkipH, SkipFace, role);
            // The X, on the cap (it sinks with it).
            Color white = new Color(.98f, .98f, .97f);
            var bar = StickerMesh("skipBar", () => ToyMeshes.RoundedSlab(.0016f, .0085f, .0007f, .0006f, white, white, white, "SkipBar"));
            for (int i = 0; i < 2; i++)
            {
                var b = Part("X", bar);
                b.SetParent(raised.cap, false);
                b.localPosition = new Vector3(CardSkipW * .5f - .0085f, 0f, -.0026f);
                b.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
            }
            pill.SetParent(card.root, false);
            float halfW = (CardSize + .016f) * .5f, top = -.008f + (CardSize + .034f) * .5f;
            pill.localPosition = new Vector3(halfW - CardSkipW * .5f - .005f, top - CardSkipH * .5f - .005f, -.0045f);
            pill.localRotation = Quaternion.identity;
            cardSkip = pill;
            cardSkipPressing = false;
            cardSkipPress.Clear();
        }

        private bool OnCardSkip(Vector3 world, float margin, float back, float front)
        {
            if (cardSkip == null || !CardShown || cardSkipPressing || float.IsInfinity(world.x)) return false;
            Vector3 l = cardSkip.InverseTransformPoint(world);
            return Mathf.Abs(l.x) <= CardSkipW * .5f + margin && Mathf.Abs(l.y) <= CardSkipH * .5f + margin && l.z >= back && l.z <= front;
        }

        // The hand's ray on Skip (FarButtonOnRay: a Tap target with the ray and ring).
        private bool CardSkipOnRay(Ray ray, out float distance)
        {
            distance = 0f;
            if (CardPagerOnRay(ray, out distance, out _)) return true; // F84: the card's < > are far Tap targets too
            if (cardSkip == null || !CardShown || cardSkipPressing) return false;
            if (!new Plane(cardSkip.forward, cardSkip.position).Raycast(ray, out float d) || d <= 0f || d > 4f) return false;
            if (!OnCardSkip(ray.GetPoint(d), .004f + d * .01f, -.01f, .01f)) return false;
            distance = d;
            return true;
        }

        // A pinch near Skip, or aimed at it from afar, or an Editor click.
        private bool CardSkipPinch(Vector3 point, Ray? ray)
        {
            if (CardPagerPinch(point, ray)) return true; // F84: < > under the card
            if (cardSkip == null || !CardShown || cardSkipPressing) return false;
            bool hit = OnCardSkip(point, .012f, -.03f, .03f) || (ray.HasValue && CardSkipOnRay(ray.Value, out _));
            if (!hit) return false;
            PressCardSkip();
            return true;
        }

        // Every frame while a card shows (CardFrame): a fingertip press on Skip.
        private void CardSkipFrame()
        {
            if (cardSkip == null || cardSkipPressing) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    cardSkipPress.Forget(hand);
                    continue;
                }
                if (cardSkipPress.Step(this, hand, OnCardSkip(tip.position, PressMargin, PressNear, PressThrough)))
                {
                    PressLog("lesson skip", hand);
                    PressCardSkip();
                    return;
                }
            }
        }

        public void PressCardSkip()
        {
            if (cardSkip == null || cardSkipPressing) return;
            cardSkipPressing = true;
            CardSkips++;
            PressRaised(cardSkip);
            ToySounds.Play(ToySounds.Cue.Press, cardSkip.position, .5f, 1.6f);
            WriteInputLog("press lesson skip: " + CardName + (CurrentLesson != null ? " (" + CurrentLesson.name + ")" : ""), true);
            StartCoroutine(After(.25f, SkipLesson));
        }

        // ------------------------------------------------------------ tests (F77)

        public Transform ProjectPlate => projectPlate;
        public bool OnProjectPlate(Transform thing) => projectPlate != null && StandsOn(thing, projectPlate);
        public Transform QuestionRoot => Question?.root;
        public Vector3 CrewPosition(Crew crew) => cast.TryGetValue(crew, out var c) && c != null ? c.transform.position : Vector3.zero;
        public Vector3 BubbleSpot(Crew crew) =>
            cast.TryGetValue(crew, out var c) && c != null ? AboveBubble(c, null, c.authoredBubble != null && c.authoredBubble.gameObject.activeInHierarchy) : Vector3.zero;

        // The lesson is over (all its steps) and taught. The step itself goes on as without it: measuring still waits for
        // a size; colouring goes on to Maker's offer.
        public void SkipLesson()
        {
            var lesson = CurrentLesson;
            string name = lesson?.name ?? CardName;
            Crew who = lesson?.who ?? cardWho;
            Step step = lesson?.step ?? Current;
            StopLessons();
            if (step == Step.Measuring) MarkTaught(TapeTaughtKey);
            // F84: the sculpt lesson skipped: taught; Done | Undo come up.
            if (name != null && name.StartsWith("sculpt"))
            {
                MarkTaught(SculptTaughtKey);
                if (Sculpting) AskSculptDone(SculptLessonDoneLine);
                LessonsSkipped.Add(name);
                Talk.Add("The person skipped the sculpt lesson");
                return;
            }
            if (step == Step.Finish)
            {
                MarkTaught(FinishTaughtKey);
                if (Current == Step.Finish) OfferVibe();
            }
            LessonsSkipped.Add(name);
            Talk.Add("The person skipped the " + name + " lesson");
            Ack(who, "Okay!");
        }
    }
}
