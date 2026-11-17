using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // F84 (2026-11-14, the user: "after the shape is made, before colouring, let me sculpt it - shave a bit off, stretch a
    // bit"; then: "does it turn into clay? can't it stay as it is?", "it needs a tutorial GIF", "undo = Ctrl+Z?", and the
    // three cards with < >):
    //  - The shape review offers Sculpt it | Change shape | Start colouring (MakerSession.Review.cs ShapeReplies).
    //  - Sculpt it is F59's hand sculpting (MakerSession.SculptHands.cs) with the made thing's own look kept; Maker works
    //    its hands while it gets ready. The brushes are sized to the thing (BrushShare of its longest side, BrushMinMm to
    //    BrushMaxMm; "bigger" / "smaller" change them). The right index wears a little coral spatula (push), the left a
    //    grey sponge (smooth); a dot on the surface under each shows the brush's size.
    //  - The first time, Maker teaches it with three cards (push, pull, smooth), each waiting until the person has done
    //    it; the card carries < > to look at the others and Skip. Then Done | Undo (Undo takes back the last touch, one
    //    touch at a time, like Ctrl+Z). Later sessions get the Done | Undo question at once.
    //  - Colouring a hand-shaped thing (Repaint): its rebuilt shape is sent to Meshy as the model itself (model_url, OBJ),
    //    so the new colours fit the shaped surface; the shaping is then part of the model (no dabs replayed on top).
    //    Without that (an error) the original shape is retextured and the dabs replayed, as before.
    public sealed partial class MakerSession
    {
        // F93 (2026-11-15, the user: "I'm removing the sculpting phase altogether - it's not good"): off. The shape review
        // is Start colouring | Change shape (F77) and sculpt words go to Change shape (SculptVoice). The mode's code
        // stays switched off; only VerifyF84 and VerifyF90.RunSculpt turn it on for their own run.
        public static bool SculptChoice = false;
        public static bool SculptLessonWhenScripted;   // checks: the lesson in the scripted demo too
        public static bool SculptRetexture = true;     // colour the shaped model itself (model_url)
        public static bool SculptBakeStub;             // checks: the scripted demo "retextures" the shaped OBJ as it is
        public static bool SculptHandleLesson = true;  // 2026-11-15: handle, push, smooth (false: the F84 push, pull, smooth)
        public const string SculptItLabel = "Sculpt it";
        public const string SculptTaughtKey = "thinkmade.sculptTaught";
        private const string KeepLineSculpt = "Here's v{0}! Sculpt it, change it, or start colouring?";
        private const string KeepLineVersionsSculpt = "Here's v{0}! Put the one you like on the stand. Sculpt it, change it, or colour it?";
        private const string SwappedLineSculpt = "v{0} it is! Sculpt it, change it, or colour it?";
        public const string SculptLessonDoneLine = "Nice! Tap Done when you like it. Undo takes back the last touch.";
        public const string SculptUndoneLine = "Took it back. Done, or undo more?";
        public const float BrushShare = .08f, BrushMinMm = 5f, BrushMaxMm = 20f, BrushStep = 1.3f;

        public int SculptPushStrokes { get; private set; }
        public int SculptPullStrokes { get; private set; }
        public int SculptSmoothStrokes { get; private set; }
        public float SculptPushMm => sculpt != null ? sculpt.PushMm : 0f;
        public float SculptSmoothMm => sculpt != null ? sculpt.SmoothMm : 0f;
        public int SculptBakes { get; private set; }
        public string SculptBakeError { get; private set; }

        private bool SculptOffered => SculptChoice && FormFirst && Product != null && Design != null && Design.kind == "model" &&
                                      Review == ReviewStage.Shape && !Sculpting && !SculptMode;

        // ------------------------------------------------------------ brushes

        private void SizeBrushes(SculptState s)
        {
            float longest = Mathf.Max(s.size.x, Mathf.Max(s.size.y, s.size.z)) * 1000f; // the source box, metres -> mm
            float push = Mathf.Clamp(longest * BrushShare, BrushMinMm, BrushMaxMm);
            s.pushMm = Mathf.Max(PushRadiusMm, push);
            s.pullMm = Mathf.Max(PullRadiusMm, push * .85f);
            s.smoothMm = Mathf.Max(SmoothRadiusMm, push * 1.3f);
            s.sizeScale = 1f;
        }

        private bool BrushSizeVoice(string p)
        {
            var s = sculpt;
            if (s == null) return false;
            bool bigger = p.Contains("bigger") || p.Contains("larger") || p.Contains("크게") || p.Contains("넓게");
            bool smaller = p.Contains("smaller") || p.Contains("finer") || p.Contains("작게") || p.Contains("가늘게");
            if (!bigger && !smaller) return false;
            s.sizeScale = Mathf.Clamp(s.sizeScale * (bigger ? BrushStep : 1f / BrushStep), .5f, 2.5f);
            Heard();
            Ack(Crew.Maker, bigger ? "Bigger brush!" : "Smaller brush!");
            WriteInputLog("sculpt brush " + s.PushMm.ToString("0.#") + " mm", true);
            return true;
        }

        // ------------------------------------------------------------ the lesson

        private void TeachSculpt()
        {
            if (Taught(SculptTaughtKey) || (Scripted && !SculptLessonWhenScripted)) { AskSculptDone(SculptLine); return; }
            StopLessons();
            int push = 0, pull = 0, smooth = 0;
            if (SculptHandleLesson)
            {
                // 2026-11-15: the handle on a part (pulled: longer or shorter), then push and smooth; no clay pull.
                int edits = 0;
                if (FitShapes.BaseParts(Design) != null)
                    Teach(new Lesson { name = "sculpt part", step = Step.Done, who = Crew.Maker, card = "sculpt_pull",
                        begin = () => edits = PartEditsMade,
                        line = "Touch a part, then pinch the round handle at its tip and pull.", again = "Put a finger near a part. Pinch its coral handle and pull.",
                        done = () => !SculptMode || PartEditsMade > edits });
                Teach(new Lesson { name = "sculpt push", step = Step.Done, who = Crew.Maker, card = "sculpt_push",
                    begin = () => push = SculptPushStrokes,
                    line = "Push in with your right finger. It dents.", again = "Point your right finger and push it into the clay.",
                    done = () => !SculptMode || clayRefused || SculptPushStrokes > push });
                Teach(new Lesson { name = "sculpt smooth", step = Step.Done, who = Crew.Maker, card = "sculpt_smooth",
                    begin = () => smooth = SculptSmoothStrokes,
                    line = "Rub with your left finger to smooth it.", again = "Rub the rough part with your left finger.",
                    done = () => !SculptMode || clayRefused || SculptSmoothStrokes > smooth,
                    finished = () => { MarkTaught(SculptTaughtKey); if (SculptMode) AskSculptDone(SculptLessonDoneLine); } });
                return;
            }
            Teach(new Lesson { name = "sculpt push", step = Step.Done, who = Crew.Maker, card = "sculpt_push",
                begin = () => push = SculptPushStrokes,
                line = "Push in with your right finger. It dents.", again = "Point your right finger and push it into the clay.",
                done = () => !Sculpting || SculptPushStrokes > push });
            Teach(new Lesson { name = "sculpt pull", step = Step.Done, who = Crew.Maker, card = "sculpt_pull",
                begin = () => pull = SculptPullStrokes,
                line = "Now pinch the clay and pull. It stretches.", again = "Pinch the surface, then pull your hand away.",
                done = () => !Sculpting || SculptPullStrokes > pull });
            Teach(new Lesson { name = "sculpt smooth", step = Step.Done, who = Crew.Maker, card = "sculpt_smooth",
                begin = () => smooth = SculptSmoothStrokes,
                line = "Rub with your left finger to smooth it.", again = "Rub the rough part with your left finger.",
                done = () => !Sculpting || SculptSmoothStrokes > smooth,
                finished = () => { MarkTaught(SculptTaughtKey); if (Sculpting) AskSculptDone(SculptLessonDoneLine); } });
        }

        private void AskSculptDone(string line) => AskWith(Crew.Maker, line, false, Rp("Done", "done sculpting"), Rp("Undo", "undo stroke"));

        // ------------------------------------------------------------ the fingertip tools and the brush dots

        private static readonly Color SculptCoral = new Color(.96f, .55f, .47f), SculptSponge = new Color(.84f, .85f, .87f);
        private Transform sculptSpatula, sculptSponge, sculptDotPush, sculptDotSmooth;
        public bool SculptToolsShown => sculptSpatula != null && sculptSpatula.gameObject.activeSelf;
        public bool SculptDotShown => sculptDotPush != null && sculptDotPush.gameObject.activeSelf;

        private void EnsureSculptTools()
        {
            if (sculptSpatula != null) return;
            Color coralSide = Color.Lerp(SculptCoral, Color.black, .2f), spongeSide = Color.Lerp(SculptSponge, Color.black, .18f);
            var spatula = ToyMeshes.RoundedBox(new Vector3(.012f, .009f, .02f), .0042f, SculptCoral, coralSide, Color.Lerp(SculptCoral, Color.white, .15f), SculptCoral, "SculptSpatula");
            var sponge = ToyMeshes.RoundedBox(Vector3.one * .014f, .0068f, SculptSponge, spongeSide, Color.Lerp(SculptSponge, Color.white, .2f), SculptSponge, "SculptSponge");
            var dotPush = ToyMeshes.Disc(1f, .0008f, Color.Lerp(SculptCoral, Color.white, .25f), SculptCoral, "SculptDotPush");
            var dotSmooth = ToyMeshes.Disc(1f, .0008f, Color.Lerp(SculptSponge, Color.white, .2f), spongeSide, "SculptDotSmooth");
            owned.Add(spatula); owned.Add(sponge); owned.Add(dotPush); owned.Add(dotSmooth);
            sculptSpatula = Part("Sculpt Spatula Tip", spatula);
            sculptSponge = Part("Sculpt Sponge Tip", sponge);
            sculptDotPush = Part("Sculpt Brush Dot (push)", dotPush);
            sculptDotSmooth = Part("Sculpt Brush Dot (smooth)", dotSmooth);
            foreach (var t in new[] { sculptSpatula, sculptSponge, sculptDotPush, sculptDotSmooth }) { t.SetParent(transform, true); t.gameObject.SetActive(false); }
        }

        private void HideSculptTools()
        {
            foreach (var t in new[] { sculptSpatula, sculptSponge, sculptDotPush, sculptDotSmooth })
                if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
        }

        // From SculptFrame (hands already looked up; null = not tracked).
        private void SculptToolsFrame(SculptState s, Transform frame, Hand right, Hand left)
        {
            EnsureSculptTools();
            PlaceTip(sculptSpatula, right);
            PlaceTip(sculptSponge, left);
            PlaceDot(sculptDotPush, s, frame, right, s.PushMm, s.pullHand == right);
            PlaceDot(sculptDotSmooth, s, frame, left, s.SmoothMm, s.pullHand == left);
        }

        // On the index fingertip, along the finger.
        private static void PlaceTip(Transform tip, Hand hand)
        {
            if (tip == null) return;
            if (hand == null || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose end) || !HandIO.Joint(hand, HandJointId.HandIndex3, out Pose joint))
            {
                if (tip.gameObject.activeSelf) tip.gameObject.SetActive(false);
                return;
            }
            Vector3 along = end.position - joint.position;
            along = along.sqrMagnitude > 1e-8f ? along.normalized : end.rotation * Vector3.forward;
            Vector3 up = Mathf.Abs(Vector3.Dot(along, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            tip.SetPositionAndRotation(end.position - along * .004f, Quaternion.LookRotation(along, up));
            if (!tip.gameObject.activeSelf) tip.gameObject.SetActive(true);
        }

        // A thin disc of the brush's size on the surface under the fingertip, within 3 cm of it.
        private static void PlaceDot(Transform dot, SculptState s, Transform frame, Hand hand, float radiusMm, bool pulling)
        {
            if (dot == null) return;
            bool show = false;
            if (!pulling && hand != null && HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
            {
                Vector3 local = frame.InverseTransformPoint(tip.position);
                float d = s.field.Sample(local);
                if (d < .03f && d > -.02f)
                {
                    float h = Mathf.Max(.001f, s.field.cell);
                    Vector3 g = new Vector3(s.field.Sample(local + Vector3.right * h) - s.field.Sample(local - Vector3.right * h),
                                            s.field.Sample(local + Vector3.up * h) - s.field.Sample(local - Vector3.up * h),
                                            s.field.Sample(local + Vector3.forward * h) - s.field.Sample(local - Vector3.forward * h));
                    if (g.sqrMagnitude > 1e-12f)
                    {
                        g.Normalize();
                        Vector3 onSurface = frame.TransformPoint(local - g * d);
                        Vector3 normal = frame.TransformDirection(g).normalized;
                        float r = radiusMm * .001f * frame.lossyScale.x;
                        dot.SetPositionAndRotation(onSurface + normal * .0012f, Quaternion.LookRotation(normal));
                        dot.localScale = new Vector3(r, r, 1f);
                        show = true;
                    }
                }
            }
            if (dot.gameObject.activeSelf != show) dot.gameObject.SetActive(show);
        }

        // ------------------------------------------------------------ the card pager (< > under a card of a set)

        private static readonly string[][] CardSets = { new[] { "sculpt_push", "sculpt_pull", "sculpt_smooth" } };
        public const float PagerW = .03f, PagerH = .022f;
        private Transform cardPrev, cardNext;
        private Transform[] cardDots;
        private string[] cardSet;
        private int cardSetIndex;
        private bool cardPagerPressing;
        private readonly FingerPress cardPrevPress = new FingerPress(), cardNextPress = new FingerPress();
        public int CardPages { get; private set; }
        public Transform CardPrevButton => cardPrev;
        public Transform CardNextButton => cardNext;
        public int CardSetIndex => cardSet != null ? cardSetIndex : -1;
        private Mesh dotOn, dotOff;

        private void BuildCardPager()
        {
            cardSet = CardSets.FirstOrDefault(set => Array.IndexOf(set, CardName) >= 0);
            if (cardSet == null || card == null || card.root == null) { cardSet = null; return; }
            cardSetIndex = Array.IndexOf(cardSet, CardName);
            float bottom = -.008f - (CardSize + .034f) * .5f, row = bottom - PagerH * .5f - .006f;
            cardPrev = PagerArrow("Card Prev", -1f, row);
            cardNext = PagerArrow("Card Next", 1f, row);
            Color on = SculptCoral, off = new Color(.78f, .78f, .8f);
            dotOn ??= KeepMesh(ToyMeshes.Disc(.0028f, .0012f, on, Color.Lerp(on, Color.black, .2f), "PagerDotOn"));
            dotOff ??= KeepMesh(ToyMeshes.Disc(.0028f, .0012f, off, Color.Lerp(off, Color.black, .2f), "PagerDotOff"));
            cardDots = new Transform[cardSet.Length];
            for (int i = 0; i < cardSet.Length; i++)
            {
                var d = Part("Pager Dot", dotOff);
                d.SetParent(card.root, false);
                d.localPosition = new Vector3((i - (cardSet.Length - 1) * .5f) * .011f, row, -.004f);
                d.localRotation = Quaternion.identity;
                cardDots[i] = d;
            }
            cardPagerPressing = false;
            cardPrevPress.Clear();
            cardNextPress.Clear();
            UpdatePagerDots();
        }

        private Mesh KeepMesh(Mesh m) { owned.Add(m); return m; }

        // A raised pill with a chevron on its cap (pointing left for -1, right for +1).
        private Transform PagerArrow(string name, float dir, float row)
        {
            Color role = Color.Lerp(SkipFace, Color.black, .15f);
            var pill = Panel(name, PagerW, PagerH, SkipFace, out var text, 11f);
            text.text = "";
            var raised = MakeRaised(pill, PagerW, PagerH, SkipFace, role);
            Color white = new Color(.98f, .98f, .97f);
            var bar = StickerMesh("pagerBar", () => ToyMeshes.RoundedSlab(.0018f, .0078f, .0008f, .0006f, white, white, white, "PagerBar"));
            for (int i = 0; i < 2; i++)
            {
                var b = Part("Chevron", bar);
                b.SetParent(raised.cap, false);
                b.localPosition = new Vector3(-dir * .0012f, (i == 0 ? 1f : -1f) * .0026f, -.0026f);
                b.localRotation = Quaternion.Euler(0f, 0f, (i == 0 ? 1f : -1f) * dir * 45f);
            }
            pill.SetParent(card.root, false);
            pill.localPosition = new Vector3(dir * (CardSize * .5f - PagerW * .5f), row, -.0045f);
            pill.localRotation = Quaternion.identity;
            return pill;
        }

        private void UpdatePagerDots()
        {
            if (cardDots == null) return;
            for (int i = 0; i < cardDots.Length; i++)
                if (cardDots[i] != null) cardDots[i].GetComponent<MeshFilter>().sharedMesh = i == cardSetIndex ? dotOn : dotOff;
        }

        private void ClearCardPager()
        {
            cardPrev = cardNext = null;
            cardDots = null;
            cardSet = null;
            cardPagerPressing = false;
            cardPrevPress.Clear();
            cardNextPress.Clear();
        }

        // Show card `index` of the set (the lesson goes on waiting for its own step).
        public void PageCard(int dir)
        {
            if (cardSet == null || card == null) return;
            int next = (cardSetIndex + dir + cardSet.Length) % cardSet.Length;
            var sheet = Resources.Load<Texture2D>("TutorialCards/" + cardSet[next]);
            if (sheet == null) return;
            cardSetIndex = next;
            CardName = cardSet[next];
            card.image.texture = sheet;
            if (lessonNextImage != null) lessonNextImage.texture = sheet;
            cardSince = Time.time;
            CardPages++;
            UpdatePagerDots();
            WriteInputLog("card page " + CardName, true);
        }

        private bool OnPagerButton(Transform button, Vector3 world, float margin, float back, float front)
        {
            if (button == null || float.IsInfinity(world.x)) return false;
            Vector3 l = button.InverseTransformPoint(world);
            return Mathf.Abs(l.x) <= PagerW * .5f + margin && Mathf.Abs(l.y) <= PagerH * .5f + margin && l.z >= back && l.z <= front;
        }

        private void PressPager(Transform button, int dir)
        {
            if (button == null || cardPagerPressing) return;
            cardPagerPressing = true;
            PressRaised(button);
            ToySounds.Play(ToySounds.Cue.Press, button.position, .5f, dir > 0 ? 1.7f : 1.5f);
            StartCoroutine(After(.12f, () => { cardPagerPressing = false; PageCard(dir); }));
        }

        // Every frame while a card shows (CardFrame): a fingertip press on < or >.
        private void CardPagerFrame()
        {
            if (cardSet == null || cardPagerPressing) return;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    cardPrevPress.Forget(hand);
                    cardNextPress.Forget(hand);
                    continue;
                }
                if (cardPrevPress.Step(this, hand, OnPagerButton(cardPrev, tip.position, PressMargin, PressNear, PressThrough))) { PressLog("card <", hand); PressPager(cardPrev, -1); return; }
                if (cardNextPress.Step(this, hand, OnPagerButton(cardNext, tip.position, PressMargin, PressNear, PressThrough))) { PressLog("card >", hand); PressPager(cardNext, 1); return; }
            }
        }

        // The hand's ray on < or > (a far Tap target, through CardSkipOnRay).
        private bool CardPagerOnRay(Ray ray, out float distance, out int dir)
        {
            distance = 0f;
            dir = 0;
            if (cardSet == null || cardPagerPressing || !CardShown) return false;
            foreach (var (button, d) in new[] { (cardPrev, -1), (cardNext, 1) })
            {
                if (button == null || !new Plane(button.forward, button.position).Raycast(ray, out float hit) || hit <= 0f || hit > 4f) continue;
                if (!OnPagerButton(button, ray.GetPoint(hit), .004f + hit * .01f, -.01f, .01f)) continue;
                distance = hit;
                dir = d;
                return true;
            }
            return false;
        }

        // A pinch near < or >, aimed at one from afar, or an Editor click (from CardSkipPinch).
        private bool CardPagerPinch(Vector3 point, Ray? ray)
        {
            if (cardSet == null || cardPagerPressing || !CardShown) return false;
            if (OnPagerButton(cardPrev, point, .01f, -.03f, .03f)) { PressPager(cardPrev, -1); return true; }
            if (OnPagerButton(cardNext, point, .01f, -.03f, .03f)) { PressPager(cardNext, 1); return true; }
            if (ray.HasValue && CardPagerOnRay(ray.Value, out _, out int dir)) { PressPager(dir < 0 ? cardPrev : cardNext, dir); return true; }
            return false;
        }

        // ------------------------------------------------------------ colouring a hand-shaped thing

        private string bakedModel; // the model path whose shape already holds the hand's shaping
        private static bool HandShaped(CoasterDesign d) => d != null && d.ops != null && d.ops.Any(o => o != null && o.kind == "dab");
        private bool ColourShapedModel => SculptRetexture && Design != null && (HandShaped(Design) || (bakedModel != null && Design.model == bakedModel));

        // The made thing's shown mesh (its rebuilt solid, with the hand's shaping) as OBJ bytes in Meshy's convention
        // (x mirrored, the winding reversed, as MeshyClient.ParseObj reads it back), written in a Task; its size in mm.
        private IEnumerator ShapedObj(Action<byte[], Vector3> done)
        {
            var mesh = ProductMesh;
            var paint = Product != null ? Product.GetComponentInChildren<PaintableThing>() : null;
            if (mesh == null || paint == null) { done(null, Vector3.zero); yield break; }
            long h = HitchStart();
            var v = mesh.vertices;
            var t = mesh.triangles;
            Vector3 scale = paint.transform.lossyScale;
            HitchEnd("shaped obj read", h);
            var job = Task.Run(() =>
            {
                Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                var sb = new StringBuilder(v.Length * 36 + t.Length * 10);
                sb.Append("# Roomsmiths hand-shaped model\n");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var p in v)
                {
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                    sb.Append("v ").Append((-p.x).ToString("0.######", inv)).Append(' ').Append(p.y.ToString("0.######", inv)).Append(' ').Append(p.z.ToString("0.######", inv)).Append('\n');
                }
                for (int i = 0; i + 2 < t.Length; i += 3)
                    sb.Append("f ").Append(t[i] + 1).Append(' ').Append(t[i + 2] + 1).Append(' ').Append(t[i + 1] + 1).Append('\n');
                Vector3 size = Vector3.Scale(hi - lo, scale) * 1000f;
                return (Encoding.UTF8.GetBytes(sb.ToString()), size);
            });
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted) { Debug.LogWarning("[Windups F84] shaped obj: " + job.Exception?.GetBaseException().Message); done(null, Vector3.zero); yield break; }
            done(job.Result.Item1, job.Result.Item2);
        }

        // The design of the retextured shaped model: the shaping, cuts and part changes are in the model now.
        private CoasterDesign BakedDesign(CoasterDesign d, Vector3 sizeMm)
        {
            d.ops = null;
            d.partEdits = null;
            d.cut = 0f;
            if (sizeMm.x > 1f && sizeMm.y > 1f && sizeMm.z > 1f)
            {
                d.sizeW = sizeMm.x;
                d.sizeD = sizeMm.z;
                d.sizeH = sizeMm.y;
                d.cupDiameter = sizeMm.x;
                d.modelHeight = sizeMm.y;
            }
            return d;
        }

        // Tests (F84).
        public void SimulateCardPage(int dir) => PageCard(dir);
        public bool SculptChoiceShown => Question != null && !Question.answered && Question.chips.Any(c => c.reply.label == SculptItLabel);
    }
}
