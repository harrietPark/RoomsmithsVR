using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Networking;

namespace Windups.Handoff
{
    // F49 (2026-11-12, the order the person asked for after the 15:00 Quest try): the form first, the finish after.
    //  1. What to make (F45: the opening question and Find ideas), the thing it is for (F45: the circle).
    //  2. Shape: Maker names every way (mic, the Show me circle, its sketchbook) and waits until the person uses
    //     one (MakerSession.Inputs.cs). There is no colour step before the make any more.
    //  3. Three shape sketches as plain white clay models (picture job "forms"); a pinch picks one.
    //  4. Measuring with Fixer's tape lesson, one step at a time (pinch with both hands, pull apart, let one go),
    //     each with its card, said again after a while until it is done.
    //  5. Meshy makes the shape only (no colours, MakerSession.Shapes.cs): it stands there in white clay.
    //  6. The finish lesson (Spark): left palm up -> the palette; pinch a colour off anything; frame a texture with
    //     both hands in an L; dip a fingertip and rub the made thing; the thumb (or index) rubs it off. Each step
    //     waits until the person has done it, is said again after a while (LessonRemindFirst, then
    //     LessonRemindAgain, at most LessonRemindMost times) and shows its card. Then Maker offers to make the vibe.
    //     Taught once (thinkmade.finishTaught): later sessions get one line and the offer.
    //  7. A vibe said -> three finish pictures of the same object (job "finishes": an edit of the picked sketch,
    //     with the palette's colours as a strip and its cleaned textures) -> picked -> Maker paints the made thing
    //     with the big brush while Meshy retextures its shape with that picture; the new look covers the finger paint.
    //     "Done" / "keep mine" keeps the finger paint instead.
    //  8. Then as before: the print check, the v2 draft and its flags, print.
    // Cards are flipbooks of 4 frames (a 2x2 sheet in Resources/TutorialCards/<name>), beside the teammate who
    // teaches; a step without a sheet shows only its line. MakerSession.FormFirst = false keeps the F33 order.
    public sealed partial class MakerSession
    {
        public static bool FormFirst = true;
        public const string FinishTaughtKey = "thinkmade.finishTaught";
        public static float LessonRemindFirst = 12f, LessonRemindAgain = 20f;
        public const int LessonRemindMost = 3;
        public const float CardSize = .14f, CardFrameSeconds = .95f;
        public const string ClayHex = "#eee9e0";

        public string VibeWords { get; private set; } = "";
        public string ShapeTaskId { get; private set; }
        public byte[] FormJpeg { get; private set; }
        public bool FinishDone => finishDone;
        public bool VibeOffered => vibeOffered;
        public string LookJob => lookJob;
        public int Repaints { get; private set; }
        public string RepaintError { get; private set; }
        public readonly List<Swatch> FinishSwatches = new List<Swatch>();

        private string lookJob = "looks";
        private bool finishDone, vibeOffered, dropPaintOnce;

        // A new make (StartShapeInput): its shape comes first again.
        private void NewFormFirstMake()
        {
            ResetReview();
            finishDone = vibeOffered = dropPaintOnce = false;
            VibeWords = "";
            ShapeTaskId = null;
            bakedModel = null; // F84
            FormJpeg = null;
            RepaintError = null;
            FinishSwatches.Clear();
            ResetColouring87(); // F87: mood pictures and the colours the finish used
            ResetShapeRules(); // F63
            lookJob = FormFirst ? "forms" : "looks";
            StopLessons();
            ClearFreeMeasureQuestion();
        }

        // ------------------------------------------------------------ lessons

        public sealed class Lesson
        {
            public string name, card, line, again;
            public Crew who;
            public Step step;
            public Func<bool> done;
            public Action begin, finished;
            public float startedAt, remindAt;
            public int reminders;
        }

        public Lesson CurrentLesson { get; private set; }
        public int LessonReminders { get; private set; }
        public readonly List<string> LessonsDone = new List<string>();
        private readonly Queue<Lesson> lessonQueue = new Queue<Lesson>();

        private void Teach(Lesson lesson)
        {
            if (CurrentLesson == null) StartLesson(lesson);
            else lessonQueue.Enqueue(lesson);
        }

        private void StartLesson(Lesson lesson)
        {
            CurrentLesson = lesson;
            lesson.begin?.Invoke();
            lesson.startedAt = Time.time;
            lesson.remindAt = Time.time + LessonRemindFirst;
            SpeakLesson(lesson);
            ShowCard(lesson.card, lesson.who);
            WriteInputLog("lesson: " + lesson.name);
        }

        private void StopLessons()
        {
            bool pinned = CurrentLesson != null;
            CurrentLesson = null;
            if (pinned) HideCaption();
            lessonQueue.Clear();
            HideCard();
        }

        // Every frame: the lesson goes on once it is done; it is said again after a while without it (never over
        // other lines, while the person talks or right after they did something).
        private void LessonFrame()
        {
            var lesson = CurrentLesson;
            if (lesson != null && Current != lesson.step) { StopLessons(); return; }
            if (lesson != null && lesson.done())
            {
                CurrentLesson = null;
                HideCard();
                LessonsDone.Add(lesson.name);
                WriteInputLog("lesson done: " + lesson.name + " after " + (Time.time - lesson.startedAt).ToString("F1") + " s");
                lesson.finished?.Invoke();
                if (CurrentLesson == null && lessonQueue.Count > 0) StartLesson(lessonQueue.Dequeue());
                return;
            }
            if (lesson != null && Time.time >= lesson.remindAt && lesson.reminders < LessonRemindMost && lines.Count == 0 &&
                !Voice.IsSpeaking && !Voice.Loading && !WindupBody.UserTalking && Time.time - lastHeardAt > 5f && !Holding)
            {
                lesson.reminders++;
                LessonReminders++;
                Say(lesson.who, lesson.again ?? lesson.line);
                cardPulseAt = Time.time;
                lesson.remindAt = Time.time + LessonRemindAgain;
            }
            CardFrame();
        }

        // ------------------------------------------------------------ cards (flipbooks)

        public string CardName { get; private set; } = "";
        public bool CardShown => card != null && card.root != null && card.root.gameObject.activeSelf;
        public Transform CardView => CardShown ? card.root : null;
        private Picture card;
        private float cardSince, cardPulseAt = -10f;

        // F77 (2026-11-14, the user: "the GIF shows up in an odd place - it should be by the character who talks, by or
        // over its bubble, with a Skip or X inside it"): the card stands beside the teacher's speech bubble (it was 60 cm
        // in front of wherever the person looked when the lesson began, and jumped at every lesson step), stays put
        // through the steps of one lesson, follows the teacher only when it has walked off, and carries Skip.
        private Crew cardWho, lastCardWho;
        private Step lastCardStep;
        private Vector3 lastCardAt, cardGoal;
        private float cardHiddenAt = -10f, cardFollowAt;

        private void ShowCard(string name, Crew by)
        {
            HideCard();
            if (string.IsNullOrEmpty(name)) return;
            var sheet = Resources.Load<Texture2D>("TutorialCards/" + name);
            if (sheet == null) return; // no picture made for this step: its line alone
            card = MakePicture(CardSize, "");
            card.image.texture = sheet;
            card.image.color = Color.white;
            card.image.uvRect = new Rect(0f, .5f, .5f, .5f);
            card.label.text = "";
            CardName = name;
            cardWho = by;
            BuildLessonMotion();
            BuildCardSkip();
            BuildCardPager(); // F84: < > and dots under a card that belongs to a set (the three sculpt cards)
            cardSince = Time.time;
            // F52: only the card (the line is in the teacher's own speech bubble, not a panel beside the card).
            // F77: the next step of the same lesson stays where the last one was (no jump, no pop).
            bool sameLesson = lastCardWho == by && lastCardStep == Current && Time.time - cardHiddenAt < 2.5f;
            Vector3 at = sameLesson ? lastCardAt : CardBeside(by);
            cardGoal = at;
            cardFollowAt = Time.time + .5f;
            card.root.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
            if (sameLesson) return;
            card.root.localScale = Vector3.one * .3f;
            StartCoroutine(Grow(card.root, 1f, .25f));
            ToySounds.Play(ToySounds.Cue.Pop, at, .25f, 1.6f);
        }

        private void HideCard()
        {
            if (card != null && card.root != null)
            {
                lastCardWho = cardWho;
                lastCardStep = Current;
                lastCardAt = card.root.position;
                cardHiddenAt = Time.time;
                Destroy(card.root.gameObject);
            }
            card = null;
            cardSkip = null;
            cardSkipPress.Clear();
            ClearCardPager(); // F84
            lessonNextImage = null;
            lessonTimer = lessonTimerFill = null;
            CardName = "";
        }

        // Beside the teacher's speech bubble, on the side away from the other teammates, a little toward the person; the
        // other side, then over the bubble (above its pills), when the room hides or cuts the spot.
        private Vector3 CardBeside(Crew by)
        {
            if (!cast.TryGetValue(by, out var c) || c == null) return FrontPoint(.6f, -.04f, out _);
            var bubble = c.authoredBubble;
            Vector3 at;
            float half = .06f, halfH = .025f;
            if (bubble != null && bubble.gameObject.activeInHierarchy)
            {
                var b = new Bounds(bubble.transform.position, Vector3.zero);
                foreach (var r in bubble.GetComponentsInChildren<Renderer>())
                    if (r.enabled && !(r is ParticleSystemRenderer)) b.Encapsulate(r.bounds);
                at = b.center;
                half = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z), .03f, .16f);
                halfH = Mathf.Clamp(b.extents.y, .015f, .1f);
            }
            else at = AboveBubble(c, null, false) + Vector3.down * .02f;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-4f ? toUser.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toUser);
            Vector3 mine = c.transform.position, sum = Vector3.zero;
            int count = 0;
            foreach (var pair in cast) if (pair.Key != by && pair.Value != null) { sum += pair.Value.transform.position; count++; }
            if (count > 0 && Vector3.Dot(sum / count - mine, side) > 0f) side = -side;
            float cardHalf = (CardSize + .016f) * .5f, cardHalfH = (CardSize + .034f) * .5f;
            float gap = half + cardHalf + .015f;
            float pills = Question != null && !Question.answered && Question.who == by ? (ChipHeight + ChipGap) * 2f : 0f;
            var candidates = new[]
            {
                at + side * gap + toUser * .02f,
                at - side * gap + toUser * .02f,
                at + Vector3.up * (halfH + cardHalfH + pills + .02f) + toUser * .02f
            };
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null || Head == null) return candidates[0];
            int mask = room.GeometryMask;
            foreach (var spot in candidates)
            {
                bool inside = Physics.CheckBox(spot, new Vector3(cardHalf, cardHalfH, .01f), SpeechBubbleDock.FacingAt(spot), mask, QueryTriggerInteraction.Ignore);
                bool hidden = Physics.Linecast(Head.position, spot, mask, QueryTriggerInteraction.Ignore);
                if (!inside && !hidden) return spot;
            }
            return candidates[0];
        }

        // Tests: where the lesson card stands now, and who teaches it.
        public Crew CardTeacher => cardWho;

        // Frames 1-4 of the 2x2 sheet, CardFrameSeconds each, the last one held twice as long.
        private static readonly int[] CardFrames = { 0, 1, 2, 3, 3 };
        private void CardFrame()
        {
            if (!CardShown) return;
            AnimateLessonMotion();
            // F77: it follows its teacher only when the teacher has walked off (its bubble's spot moved over 7 cm).
            if (Time.time >= cardFollowAt)
            {
                cardFollowAt = Time.time + .5f;
                Vector3 goal = CardBeside(cardWho);
                if (Vector3.Distance(goal, cardGoal) > .07f) cardGoal = goal;
            }
            card.root.position = Vector3.Lerp(card.root.position, cardGoal, 1f - Mathf.Exp(-Time.deltaTime * 6f));
            card.root.rotation = SpeechBubbleDock.FacingAt(card.root.position);
            float pulse = Time.time - cardPulseAt;
            if (pulse < .5f && Time.time - cardSince > .3f) card.root.localScale = Vector3.one * (1f + .12f * Mathf.Sin(pulse / .5f * Mathf.PI));
            CardSkipFrame();
            CardPagerFrame(); // F84
        }

        // ------------------------------------------------------------ the tape lesson (measuring)

        private void TapeLessonOrDemo()
        {
            if (FormFirst) StartTapeLesson();
            else if (!Taught(TapeTaughtKey)) StartCoroutine(TeachTape());
        }

        private void StartTapeLesson()
        {
            StopLessons();
            if (Taught(TapeTaughtKey))
            {
                // Once taught: the card and a reminder, no lesson lines.
                Teach(new Lesson { name = "tape", step = Step.Measuring, who = Crew.Fixer, card = "tape",
                    line = "Pinch both ends, pull apart, then release one hand. The size settles in three seconds.",
                    again = "Pinch both ends with both hands, pull apart, let one go.", done = () => HasMeasure });
                return;
            }
            float startLength = 0f;
            Teach(new Lesson { name = "tape pinch", step = Step.Measuring, who = Crew.Fixer, card = "tape",
                line = HandRules ? "Pinch with both hands and I'll toss you my tape." : "Like this: thumb to finger, on both hands.",
                again = HandRules ? "Thumb to finger on both hands. My tape comes to you." : "Pinch with both hands, like holding the two ends of a tape.",
                done = () => HasMeasure || Tape.State == HandTape.Mode.TwoHands });
            Teach(new Lesson { name = "tape pull", step = Step.Measuring, who = Crew.Fixer, card = "tape",
                begin = () => startLength = Tape.Length,
                line = "Now pull them apart to the size you want.", again = "Pinch with both hands, then pull them apart.",
                done = () => HasMeasure || (Tape.State == HandTape.Mode.TwoHands && Mathf.Abs(Tape.Length - startLength) > .015f) });
            Teach(new Lesson { name = "tape keep", step = Step.Measuring, who = Crew.Fixer, card = "tape",
                line = "Let one hand go to keep it.", again = "Pinch both ends, pull, then let go with one hand. The size stays.",
                done = () => HasMeasure, finished = () => MarkTaught(TapeTaughtKey) });
        }

        // ------------------------------------------------------------ the finish (after the white clay make)

        private void StartFinish()
        {
            if (FormFirst && !ShapeConfirmed) { EnterShapeReview(); return; }
            if (ResumeVibe()) return; // F63: the vibe said before a shape change while colouring, again on the new shape
            Current = Step.Finish;
            vibeOffered = false;
            // NewFormFirstMake clears these once; resizing this make must keep its captured materials.
            StopLessons();
            Talk.Add("It is made in white clay. Now its colours and texture.");
            Body(Crew.Spark)?.Play("HandRaise");
            if (Taught(FinishTaughtKey))
            {
                Say(Crew.Spark, HandRules ? "Want my palette? Open your left palm and look at it. Then pinch colours and dip a finger to paint!"
                    : "Your palette's on your left palm: pinch colours, frame textures, dip a finger to paint!");
                ShowCard("colour", Crew.Spark);
                OfferVibe();
                return;
            }
            int picks = 0, frames = 0, touches = 0, strokes = 0, rubs = 0;
            Teach(new Lesson { name = "palm up", step = Step.Finish, who = Crew.Spark, card = "colour",
                line = HandRules ? "Let's colour it! Open your left palm and look at it. I'll pass you my palette." : "Let's colour it! Turn your left palm up.",
                again = HandRules ? "Open your left hand, palm up, and look at it. My palette will come." : "Turn your left palm up, open, toward your face.",
                done = () => PaletteOnPalm,
                finished = () => Say(Crew.Spark, HandRules ? "There's my palette. Close that hand and it comes back to me." : "That's your palette. Nice!") });
            Teach(new Lesson { name = "colour", step = Step.Finish, who = Crew.Spark, card = "colour",
                begin = () => picks = ColourPicks,
                line = "Pinch a colour off anything in your room.", again = "Point at something with your right hand, pinch, then let go.",
                done = () => ColourPicks > picks });
            Teach(new Lesson { name = "texture", step = Step.Finish, who = Crew.Spark, card = "texture",
                begin = () => frames = TextureFrames,
                line = TextureWithoutPalette && HandRules ? "You can grab a texture too: frame it with both hands in an L. No palette needed!" : "You can grab a texture too: frame it with both hands in an L.",
                again = "Make an L with each hand around a surface, hold still, then hands down.", // (F84: the palette may go back meanwhile)
                done = () => TextureFrames > frames });
            Teach(new Lesson { name = "paint", step = Step.Finish, who = Crew.Spark, card = "paint",
                begin = () => { touches = PaintTouches; strokes = PaintStrokes; },
                line = "Now dip a fingertip in a colour and swipe it over the thing we made!", again = "Touch a colour on your palette with a right fingertip, then swipe over the object.",
                done = () => PaintTouches > touches || PaintStrokes > strokes }); // F70: one swipe paints a whole part
            var erase = new Lesson { name = "erase", step = Step.Finish, who = Crew.Spark, card = "erase", done = () => EraseTouches > rubs };
            erase.begin = () =>
            {
                rubs = EraseTouches;
                string finger = EraserFinger == HandJointId.HandIndexTip ? "index finger" : "thumb";
                erase.line = "Your " + finger + " is the eraser. Rub the paint off with it.";
                erase.again = "Rub the paint with your " + finger + ". Dip in a colour again if it's gone.";
            };
            erase.finished = () => { MarkTaught(FinishTaughtKey); OfferVibe(); };
            Teach(erase);
        }

        // Maker offers to make the look from words (a model only: it is retextured by Meshy).
        private bool CanRepaint => Kind == "model" && (ShapeTaskId != null || (Scripted && !LiveMeshyInTests));

        // F77 (the user: "Done / New mood, and New mood asks what kind of mood"): Done keeps the finger paint;
        // New mood asks for one (the mic pulses) and the words said next are the vibe.
        public const string NewMoodLabel = "New mood";
        public bool AskedForMood { get; private set; }
        private void OfferVibe()
        {
            if (vibeOffered) return;
            vibeOffered = true;
            AskedForMood = false;
            ResetDoneAsk(); // F95: paint from here on can make the crew ask "All done colouring?"
            if (CanRepaint) AskWith(Crew.Maker, "Looks good! Done, or want a new mood?", false, Rp("Done", "done"), Rp(NewMoodLabel, "new mood"));
            else AskWith(Crew.Maker, "Paint it as you like. Tap Done when you're happy!", false, Rp("Done", "done"));
            PulseMic();
            if (CanRepaint) BringMoodFrame(false); // F87: Spark's frame takes a mood picture
        }

        private static bool WantsNewMood(string p) =>
            p == "new mood" || p == "new mode" || p == "a new mood" || p == "another mood" || p == "different mood" || p == "change the mood" ||
            p == "새 분위기" || p == "다른 분위기" || p == "분위기 바꾸기";

        // Words in the finish step (Route, before the crew's questions). Done / keep mine ends it; skip goes to the
        // vibe offer; how / what / why questions go to the crew (false); anything else is the vibe. `fromStep`: the
        // crew's "do" (StepInput) - then a question is the vibe too.
        private bool FinishVoice(string text, string p, bool fromStep = false)
        {
            if (Current != Step.Finish) return false;
            if (FinishShapeVoice(text, p)) return true; // F63: a shape wish is not paint (also from the crew's "do")
            if (ColouringVoice(p)) return true; // F87: "I'll paint it myself", a mood picture asked for
            if (WantsNewMood(p))
            {
                // F77: New mood asks for the mood; the next words are it (as any words in this step are).
                StopLessons();
                vibeOffered = true;
                AskedForMood = true;
                CloseQuestion("new mood asked");
                Heard();
                Say(Crew.Maker, "What mood do you want? Tell me!");
                PulseMic();
                return true;
            }
            bool skip = p == "skip" || p.Contains("skip") || p.Contains("건너") || p.Contains("넘어가");
            bool done = DoneWords(p) || IsYes(p) || p.Contains("keep mine") || p.Contains("i'm happy") || p.Contains("im happy") || p.Contains("이대로") || p.Contains("좋아");
            if (skip || done)
            {
                if (!vibeOffered && CurrentLesson != null) { StopLessons(); MarkTaught(FinishTaughtKey); OfferVibe(); }
                else EndFinish();
                return true;
            }
            if (!fromStep && System.Text.RegularExpressions.Regex.IsMatch(p, @"^(how|what|why|where|which|who|when|어떻게|뭐|왜|어디)\b")) return false;
            StartVibe(text);
            return true;
        }

        private void StartVibe(string words)
        {
            if (FormFirst && !ShapeConfirmed) { ShowShapeChoices(); return; }
            StopLessons();
            vibeOffered = true;
            VibeWords = (VibeWords + ", " + words).Trim(' ', ',');
            Talk.Add("The vibe: " + VibeWords);
            Note("palette", "The vibe", VibeWords);
            if (!CanRepaint) { Say(Crew.Maker, "I can't recolour this one. Paint it with your palette!"); return; }
            StartCoroutine(MakeLooks("finishes"));
        }

        // The finish is over (kept as painted, or repainted): the usual after-make things.
        private void EndFinish(bool ask = true)
        {
            StopLessons();
            finishDone = true;
            Current = Step.Done;
            Hush();
            Remember(); // the finger paint stays with it
            if (Product != null) StartCoroutine(CheckPrintable(Product, ProductMesh, Design));
            Review = ReviewStage.None;
            ClearFlags();
            DestroyDraft();
            // F77: Finish (the ending, then Export) or Change shape; no Print pill (export happens after finishing).
            promptedProduct = Product; // (EndingFrame's "tell me when you're done" would only repeat this)
            if (ask) AskAfterColour("All coloured! Finish it, or change its shape?");
        }

        // F87: the one next step after colouring (also after Maker's repaint, which used to end on Fixer's print tip).
        private void AskAfterColour(string line) =>
            AskWith(Crew.Maker, line, false, Rp(FinishLabel, "finish creation"), Rp(ChangeShapeLabel, "return to shape"));

        public const string FinishLabel = "Finish";
        public int RepaintWaits { get; private set; }
        public int RepaintsFromHere { get; private set; } // F100: painted without a clear spot beside the work

        // ------------------------------------------------------------ the vibe picked: Maker repaints it

        // Maker walks up to the made thing and paints it with the big brush (the F35 paint stage, on the made thing)
        // while Meshy retextures its shape with the picked picture; then the new look is revealed in place.
        private IEnumerator Repaint(Picture picked)
        {
            if (Product == null || Design == null || picked == null) yield break;
            // Another version, the mic or a teammate may now occupy the earlier work space.
            // Ask before moving the object; never finish a repaint with Maker unable to touch it.
            float workRadius = Mathf.Max(ProductMesh.bounds.extents.x, ProductMesh.bounds.extents.z);
            // F100 (Quest 2026-11-15, the user: "I picked the gradient finish, and Maker kept saying it's too far, take it to
            // the desk, and never coloured it - fix this"): the work stood on the desk, but on a crowded desk no spot 10 cm
            // beside it was clear enough for Maker (MakerStandClear), so Maker asked to have it put "on the desk near me"
            // for 90 s and gave up; when the walk failed it gave up too, and without three brush strokes the new look was
            // thrown away. Now nothing waits on that: Maker walks up if it can, else paints from where it stands (its work
            // motion, looking at the work), and the new colours are revealed either way.
            bool paintFromHere = !HasWorkStand(Product.position, workRadius);
            if (paintFromHere) { RepaintWaits++; WriteInputLog("repaint: no clear spot beside the work; Maker paints from where it is", true); }
            Current = Step.Making;
            StopLessons();
            Hush();
            finishDone = true;
            RepaintError = null;
            working.Add(Crew.Maker);
            sculptSpot = Product.position;
            sculptTurn = Product.rotation;
            // The made thing as it is, shown as the work Maker paints on (the real one waits hidden).
            if (viewMesh != null) { owned.Remove(viewMesh); Destroy(viewMesh); }
            viewMesh = Instantiate(ProductMesh);
            owned.Add(viewMesh);
            if (view == null) { view = Part("Sculpt View", viewMesh); view.SetParent(transform, false); }
            view.GetComponent<MeshFilter>().sharedMesh = viewMesh;
            view.GetComponent<MeshRenderer>().sharedMaterial = MadeMaterial(Design);
            view.SetPositionAndRotation(sculptSpot, sculptTurn);
            view.localScale = Vector3.one;
            view.gameObject.SetActive(true);
            hiddenProduct = Product;
            Product.gameObject.SetActive(false);
            // The work's size for where Maker stands (WalkToWork keeps its big head clear of it).
            Vector3 size = viewMesh.bounds.size;
            if (blockMesh == null) { blockMesh = new Mesh { name = "WoodBlock" }; blockMesh.MarkDynamic(); owned.Add(blockMesh); WoodBlock(blockMesh, ChamferStart); }
            blockHalf = Mathf.Clamp(Mathf.Max(size.x, size.z) * .5f, .02f, .09f);
            blockHeight = Mathf.Clamp(size.y, .02f, .17f);
            blockTurn = sculptTurn;
            Pats = Dabs = Chunks = 0;
            Sculpt = SculptStage.Approach;
            cast.TryGetValue(Crew.Maker, out var maker);
            if (maker != null)
            {
                makerHome = maker.movement.transform.position;
                Ack(Crew.Maker, "Painting it!");
                if (!paintFromHere) yield return WalkToWork(maker, blockHalf + .01f);
                var body = WindupBody.Of(maker);
                if (!paintFromHere && MakerAtWork(maker))
                {
                    if (body != null) { body.StartWork("Work_Loop"); body.LimitPosture(SculptLean, SculptHeadDown); }
                    StartCoroutine(PaintModel(maker));
                }
                else
                {
                    // F100: no clear spot (or the walk did not get there): painting from here, the work in view.
                    if (!paintFromHere) WriteInputLog("repaint: Maker could not get beside the work; it paints from where it is", true);
                    paintFromHere = true;
                    RepaintsFromHere++;
                    Sculpt = SculptStage.Paint;
                    if (body != null) body.StartWork("Work_Loop");
                    LookAt(Crew.Maker, view.position, 30f);
                }
            }
            else Sculpt = SculptStage.Paint;
            string path = null, texturePath = null;
            bool ok = false;
            // F84: a hand-shaped thing is coloured as it now is (its rebuilt shape sent as the model itself).
            byte[] shaped = null;
            Vector3 shapedSize = Vector3.zero;
            bool baked = false;
            if (ColourShapedModel && (!Scripted || LiveMeshyInTests || SculptBakeStub))
                yield return ShapedObj((bytes, size) => { shaped = bytes; shapedSize = size; });
            if (Scripted && !LiveMeshyInTests && shaped != null && SculptBakeStub)
            {
                // Checks: "Meshy" gives the shaped OBJ back as it is, with a plain texture.
                yield return new WaitForSeconds(ScriptedPaintSeconds);
                var stub = new MeshyClient.Job { obj = Encoding.UTF8.GetString(shaped), texture = new Texture2D(4, 4) };
                owned.Add(stub.texture);
                SaveModel(stub, out path, out texturePath);
                ok = path != null && texturePath != null && Current == Step.Making;
                baked = ok;
            }
            else if (Scripted && !LiveMeshyInTests)
            {
                yield return new WaitForSeconds(ScriptedPaintSeconds);
                path = Design.model;
                ok = Current == Step.Making;
            }
            else if (ShapeTaskId != null || shaped != null)
            {
                var paint = new MeshyClient.Job();
                if (shaped != null) StartCoroutine(MeshyClient.RetextureModel(shaped, picked.jpeg, paint));
                else StartCoroutine(MeshyClient.Retexture(ShapeTaskId, picked.jpeg, paint));
                for (float until = Time.time + 300f; !paint.Done && Time.time < until && Current == Step.Making; ) yield return null;
                if (shaped != null && (paint.error != null || !paint.Done) && ShapeTaskId != null && Current == Step.Making)
                {
                    // The shaped model was not taken: the first shape is coloured and the shaping replayed on it (F59).
                    SculptBakeError = paint.error ?? "took too long";
                    Debug.LogWarning("[Windups F84] shaped retexture: " + SculptBakeError + "; retexturing the first shape");
                    shaped = null;
                    paint = new MeshyClient.Job();
                    StartCoroutine(MeshyClient.Retexture(ShapeTaskId, picked.jpeg, paint));
                    for (float until = Time.time + 300f; !paint.Done && Time.time < until && Current == Step.Making; ) yield return null;
                }
                if (paint.Done && paint.error == null && paint.obj != null) { PaintSeconds = paint.Seconds; SaveModel(paint, out path, out texturePath); ok = path != null && texturePath != null; baked = ok && shaped != null; }
                else RepaintError = paint.error ?? "took too long";
            }
            else RepaintError = "no shape task";
            // A fast/cached texture result must still leave time for walking, dipping and a visible stroke.
            if (ok && maker != null && !paintFromHere)
            {
                for (float until = Time.time + 18f; Dabs < 3 && Time.time < until && Current == Step.Making; ) yield return null;
                if (Dabs < 3) { ok = false; RepaintError = "no clear, reachable surface for the brush"; }
            }
            working.Remove(Crew.Maker);
            if (!ok || Current != Step.Making)
            {
                Debug.LogWarning("[Windups F49] repaint: " + (RepaintError ?? "the person moved on"));
                SculptFail(); // the made thing comes back as it was
                if (Current == Step.Making)
                {
                    finishDone = false;
                    Current = Step.Finish;
                    vibeOffered = true;
                    Say(Crew.Maker, "The new colours didn't come. Paint it yourself, or tell me again!");
                }
                yield break;
            }
            var d = Design.Copy();
            KeepParts(d, Design); // F83: the same shape in new colours keeps its parts
            d.model = path;
            d.texture = texturePath;
            if (baked)
            {
                d = BakedDesign(d, shapedSize); // F84: the shaping is in the model now
                bakedModel = path;
                SculptBakes++;
            }
            d.color = TextureColour(texturePath) ?? ("#" + ColorUtility.ToHtmlStringRGB(paintColours.FirstOrDefault()));
            yield return RevealModel(d, true);
            Design = d.Clamped();
            Repaints++;
            Note("look", "Finish: " + MakerRoles.Clean(picked.look?.label ?? VibeWords), VibeWords, picked.jpeg);
            revealedBySculpt = true;
            dropPaintOnce = true; // the new look covers the finger paint
            yield return Build(false);
            if (Product == null) yield break;
            // F87 (Quest: "after painting, 'print it in PETG… say print' - what does that mean? let me go on"): no print
            // tip; the same next step as after finger painting.
            RememberFinishUsed(picked);
            Review = ReviewStage.None;
            promptedProduct = Product;
            AskAfterColour("Here it is! Finish it, or change its shape?");
        }

        // ------------------------------------------------------------ the picture requests

        // F49: the shape sketches are white clay (the drawing and the shape photo as images); the finish pictures
        // edit the picked sketch with the palette's colours (one strip) and its cleaned textures; the F33 looks as before.
        private UnityWebRequest PictureRequest(string prompt)
        {
            var refs = new List<(byte[] jpeg, string role)>();
            string style = MakerRoles.ImageStyle;
            // F63: the person's own rules for the shape go with every picture (they win over the idea note's details).
            prompt = (prompt ?? "").Trim() + RulesLine(lookJob == "finishes" ? "; the object already follows them" : "");
            if (lookJob == "forms")
            {
                style = MakerRoles.ClayStyle;
                refs.AddRange(FormRefs(pictureLook)); // F56/F60; F63: look 1 copies the person's picture, looks 2 and 3 keep its outline
            }
            else if (lookJob == "finishes")
            {
                var form = FormJpeg ?? LookJpeg;
                if (form != null) refs.Add((form, "the object itself: keep exactly this object, its shape, proportions, details, camera view and background, and change only its colours, materials and surface finish"));
                foreach (var mood in MoodPictures) refs.Add((mood, MoodRole)); // F87: photos taken in Spark's frame for the mood
                var strip = ColourStrip();
                if (strip != null) refs.Add((strip, "the colours the person picked in their room: use these colours"));
                foreach (var tile in FinishTextures()) refs.Add((tile, "a material the person picked in their room: use the look of this material"));
            }
            else
            {
                LogPicture(pictureLook, prompt, refs); // F63
                return Reference != null || DrawingJpeg != null || ShapeRef != null ? ReferenceRequest(prompt, Reference, DrawingJpeg, ShapeRef) : GenerateRequest(prompt, MakerRoles.ImageStyle);
            }
            LogPicture(pictureLook, prompt, refs); // F63
            return refs.Count > 0 ? EditRequest(prompt, refs, style) : GenerateRequest(prompt, style);
        }

        // F56: the pictures on the note, each with what the person said it is for (in ShapeBrief's order), then a drawing
        // or photo given outside the cards (e.g. drawn while the sketches are reviewed, F55). F60: the same list goes to
        // Maker as images when it writes the sketches.
        public int LastFormImages { get; private set; }
        // F63: `look` is the sketch asked for (0-2): look 1 copies the person's whole-shape picture, looks 2 and 3 get it
        // as an outline to keep while their prompts change the form (all three were told "copy exactly" and came back
        // the same). -1: Maker's own request (each picture with what the person said it is for).
        private List<(byte[] jpeg, string role)> FormRefs(int look = -1)
        {
            var refs = new List<(byte[] jpeg, string role)>();
            var cards = KeptCards.Where(c => c.HasImage && c.jpeg != null).Take(5).ToList();
            byte[] outline = look > 0 ? ExactShapePicture(cards) : null;
            foreach (var c in cards) refs.Add((c.jpeg, outline != null && c.jpeg == outline ? OutlineRole(c) : CardRefRole(c)));
            if (DrawingJpeg != null && refs.Count < 5 && !cards.Any(c => c.jpeg == DrawingJpeg))
                refs.Add((DrawingJpeg, outline != null && DrawingJpeg == outline ? "the person's own line drawing: the person's shape: " + OutlineWords : "the person's own line drawing: copy exactly this shape, its parts, proportions and outline"));
            if (ShapeRef != null && refs.Count < 5 && !cards.Any(c => c.jpeg == ShapeRef)) refs.Add((ShapeRef, "a photo of a real object: give the object its overall shape and proportions, not its colours or surface"));
            return refs;
        }

        private static UnityWebRequest GenerateRequest(string prompt, string style)
        {
            var http = new UnityWebRequest("https://api.openai.com/v1/images/generations", "POST");
            http.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MakerRoles.ImageRequest(prompt ?? "", style)));
            http.downloadHandler = new DownloadHandlerBuffer();
            http.SetRequestHeader("Content-Type", "application/json");
            return http;
        }

        private static UnityWebRequest EditRequest(string prompt, List<(byte[] jpeg, string role)> refs, string style)
        {
            string[] order = { "first", "second", "third", "fourth", "fifth" };
            int count = Mathf.Min(refs.Count, order.Length);
            var guide = new StringBuilder();
            for (int i = 0; i < count; i++) guide.Append(" The ").Append(order[i]).Append(" image is ").Append(refs[i].role).Append('.');
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("model", MakerRoles.ImageModel),
                new MultipartFormDataSection("prompt", (prompt ?? "").Trim() + guide + style),
                new MultipartFormDataSection("size", "1024x1024"),
                new MultipartFormDataSection("quality", MakerRoles.ImageQuality),
                new MultipartFormDataSection("output_format", "jpeg"),
                new MultipartFormDataSection("output_compression", "92"),
            };
            for (int i = 0; i < count; i++) form.Add(new MultipartFormFileSection("image[]", refs[i].jpeg, "ref" + i + ".jpg", "image/jpeg"));
            var http = UnityWebRequest.Post("https://api.openai.com/v1/images/edits", form);
            http.downloadHandler = new DownloadHandlerBuffer();
            return http;
        }

        // The colours picked in the finish step, side by side in one small picture.
        private byte[] ColourStrip()
        {
            var colours = FinishSwatches.Where(s => s.kind == "colour").Select(s => (Color32)Hex(s.hex)).Distinct().ToList();
            if (colours.Count == 0) return null;
            colours = colours.Skip(Mathf.Max(0, colours.Count - 4)).ToList();
            const int w = 256, h = 64;
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) pixels[y * w + x] = colours[Mathf.Min(colours.Count - 1, x * colours.Count / w)];
            return RoomEyes.Jpeg(pixels, w, h, 90);
        }

        // The textures picked in the finish step (the cleaned tiles, once the image model is done with them).
        private IEnumerable<byte[]> FinishTextures()
        {
            foreach (var s in FinishSwatches.Where(s => s.kind == "texture" && s.image != null).Reverse().Take(2))
            {
                byte[] bytes = null;
                try { if (File.Exists(s.image)) bytes = File.ReadAllBytes(s.image); }
                catch (Exception exception) { Debug.LogWarning("[Windups F49] " + exception.Message); }
                if (bytes != null) yield return bytes;
            }
        }

        // ------------------------------------------------------------ tests

        public static void ForgetF49Lessons() { PlayerPrefs.DeleteKey(FinishTaughtKey); PlayerPrefs.DeleteKey(TapeTaughtKey); PlayerPrefs.DeleteKey(ShapeInputsTaughtKey); }
        public void SimulateVibe(string words) => StartVibe(words);
        public void SimulateFinishWords(string words) => FinishVoice(words, words.ToLowerInvariant().Trim().TrimEnd('.', '!', '?'));
        public void SimulatePaintTouches(int n) { for (int i = 0; i < n; i++) PaintTouches++; }
        public void SimulateEraseTouch() => EraseTouches++;
    }
}
