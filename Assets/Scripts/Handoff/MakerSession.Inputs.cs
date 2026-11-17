using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F33 (2026-11-11): after what to make, the person gives the SHAPE, then the COLOURS, each in whatever
    // way they like, and only then the pictures are drawn (one "what vibe?" question used to cover both).
    // F56 (2026-11-13): the shape is collected as cards (MakerSession.ShapeCards.cs) and the show-me circle is
    // Spark's frame (MakerSession.ShowFrame.cs, the picture in MakerSession.ShowMe.cs):
    //  - Shape (Maker and Spark teach it the first time, showing each way as they say it): tell it with the mic
    //    (a words card), put anything in Spark's frame or hold the frame up to it and press its button (a photo
    //    card), or draw in Maker's sketchbook (a drawing card; Maker holds the book out again after each drawing).
    //    All three can be used, any number of times. Only the cards put on the note count; "done" (or the Done
    //    pill) ends the collecting. "You pick" leaves it to the crew. Nothing moves on by itself.
    //  - Colours (the old F33 order, FormFirst off; Spark teaches): tell it, pinch a colour off anything (palette
    //    on the left palm), frame a texture with both hands, put a thing in Spark's frame, or "you pick". Each
    //    input is confirmed in one line; more input within InputSettle seconds is added to it; "done" goes on.
    //  - While a drawing or a picture is under way nothing moves on. Reminders are few and never repeat.
    public sealed partial class MakerSession
    {
        public const string ShapeInputsTaughtKey = "thinkmade.shapeInputsTaught", ColourInputsTaughtKey = "thinkmade.colourInputsTaught";
        public static float InputSettle = 4f, InputRemindAfter = 8f; // F92: reminded sooner (was 14 s)

        public string ShapeWords { get; private set; } = "";
        public string ColourWords { get; private set; } = "";
        public byte[] ShapeRef { get; private set; }          // a photo of a real thing with the wanted shape
        public bool ShapeFromCrew { get; private set; }
        public bool ColourFromCrew { get; private set; }
        public readonly List<Swatch> InputSwatches = new List<Swatch>();
        public int ShapeInputs { get; private set; }
        public int ColourInputs { get; private set; }
        public int ShowCaptures { get; private set; }
        public int InputReminders { get; private set; }
        public bool OfferingBook { get; private set; }
        public bool InputTutorial => tutorialRunning;
        public Transform OfferedBook => offeredBook;

        private Transform offeredBook;
        private Coroutine tutorial;
        private bool tutorialRunning, inputReminded;
        private int inputRemindCount;
        private float lastInputRemindAt = -100f;
        private float inputSettleAt = -1f, lastInputAt, micPulseAt = -10f;

        private bool InInputs => Current == Step.ShapeInput || Current == Step.ColourInput;

        // ------------------------------------------------------------ shape

        private void StartShapeInput()
        {
            Current = Step.ShapeInput;
            ShapeWords = ColourWords = "";
            ShapeRef = null;
            DrawingJpeg = null;
            Reference = null;
            ShapeFromCrew = ColourFromCrew = false;
            InputSwatches.Clear();
            ShapeInputs = ColourInputs = 0;
            Style = Shape = "";
            ClearShapeCards(); // F56: a new make collects its shape from nothing
            ResetInputTimers();
            NewFormFirstMake(); // F49: a new make starts with its shape (the finish comes after it is made)
            // F41: a desk clip's shape is the real desk edge (hook, cable clip or shelf is asked after measuring).
            // F49: its colours come after it is made too, so it goes straight to the desk's thickness.
            if (Kind == "clip") { Talk.Add("A desk clip: its shape comes from the desk edge"); if (FormFirst) StartMeasuring(); else StartColourInput(); return; }
            if (!FrameShown) PlaceShowFrame(); // F45: the frame the thing it is for went in is the same one
            StartOfferingBook();
            if (tutorial != null) StopCoroutine(tutorial);
            tutorial = StartCoroutine(ShapeTutorial(!Taught(ShapeInputsTaughtKey)));
        }

        // F56: every way is named as its tool shows (the mic, Maker's book held out, Spark's frame on the desk), and
        // that one is enough or any number. Taught once in full; afterwards one line.
        private IEnumerator ShapeTutorial(bool full)
        {
            tutorialRunning = true;
            if (!full)
            {
                AskWith(Crew.Maker, "What shape? Say it, take my sketchbook, or snap it.", false, Rp("Let me draw"), Rp("You pick"));
                OfferBookNow(); // F61
                PulseMic();
                PulseFrame();
                EndTutorial();
                yield break;
            }
            // F92: four lines on the beat (it was six, each waiting to be read: 45-125 s on the Quest).
            Say(Crew.Maker, "What shape? Tell me, right into the mic,");
            yield return ShowWith(() => { PulseMic(); LookAt(Crew.Maker, MicPoint(), 1.6f); });
            Say(Crew.Maker, "or take my sketchbook and draw it.");
            yield return ShowWith(OfferBookNow); // F61: Maker holds it out toward the person as it says so
            Say(Crew.Spark, "Or put anything in my frame and press its button!");
            yield return ShowWith(() => { PulseFrame(); PulseFrameButton(); LookAt(Crew.Spark, ShowCircleCentre, 2f); Body(Crew.Spark)?.Play("HandRaise"); });
            AskWith(Crew.Maker, "One is enough, or as many as you like.", false, Rp("Let me draw"), Rp("You pick"));
            yield return ShowWith(() => Body(Crew.Maker)?.Play("React_Nod"));
            MarkTaught(ShapeInputsTaughtKey);
            EndTutorial();
        }

        // Runs `show` as the line just queued comes up, then waits until it has been read (or said).
        private IEnumerator ShowWith(Action show)
        {
            int before = lines.Count;
            float until = Time.time + 6f;
            while (lines.Count >= before && lines.Count > 0 && Time.time < until) yield return null;
            show?.Invoke();
            yield return new WaitForSeconds(.2f);
            while ((lines.Count > 0 || Voice.IsSpeaking || Voice.Loading || Time.time < nextLineAt - .2f) && Time.time < until + 8f) yield return null;
        }

        // F56: the collecting is over ("done", the Done pill, or "you pick"): only the cards on the note count.
        private void FinishShape()
        {
            StopTutorial();
            StopOfferingBook(false);
            var kept = KeptCards.ToList();
            UpdateShapeWords();
            autoDoneAt = -1f;
            // (What older code reads: the first photo and the first drawing on the note.)
            ShapeRef = kept.FirstOrDefault(c => c.kind == CardKind.Photo)?.jpeg;
            DrawingJpeg = kept.FirstOrDefault(c => c.kind == CardKind.Drawing)?.jpeg;
            string brief = ShapeBrief();
            Talk.Add("Shape: " + brief);
            Note("design", "Shape", brief, kept.FirstOrDefault(c => c.HasImage)?.jpeg);
            foreach (var c in kept.Where(c => c.HasImage).Skip(1)) Note("design", "Shape: " + (string.IsNullOrEmpty(c.role) ? CardKindName(c) : c.role), c.kind == CardKind.Photo ? "A picture you took." : "Drawn by you.", c.jpeg);
            DropLooseCards();
            HideShapeDone();
            HideShowFrame();
            // F49: the shape goes straight to three plain white clay sketches (colours come after it is made).
            if (FormFirst) StartCoroutine(MakeLooks("forms"));
            else StartColourInput();
        }

        // What the picture model is told about the shape (the photos and the drawings go along as images, in the
        // order they are listed here).
        private string ShapeBrief()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ShapeWords)) parts.Add(ShapeWords.Trim());
            var images = KeptCards.Where(c => c.HasImage).ToList();
            for (int i = 0; i < images.Count; i++)
            {
                var c = images[i];
                string what = c.kind == CardKind.Photo ? "a picture the person took" + (string.IsNullOrEmpty(c.text) ? "" : " of " + c.text) : "the person's own drawing";
                string use = c.role == WholeShape ? "the whole shape" : c.role == DetailRole ? "a detail to put on it" : !string.IsNullOrEmpty(c.role) ? "the " + c.role.ToLowerInvariant() : "the shape (they did not say which part)";
                parts.Add("reference image " + (i + 1) + " (" + what + ") is for " + use);
            }
            // (A drawing or photo given outside the cards: the drawing in the sketches' review, F55.)
            if (ShapeRef != null && !images.Any(c => c.jpeg == ShapeRef)) parts.Add("shaped like the real object in the person's photo");
            if (DrawingJpeg != null && !images.Any(c => c.jpeg == DrawingJpeg)) parts.Add("shaped like the person's own drawing");
            if (KeptCards.Count(c => c.role == WholeShape) >= 2) parts.Add("more than one thing was given as the whole shape: sketch the different readings");
            if (parts.Count == 0 || ShapeFromCrew) parts.Add("the crew picks a shape that suits it");
            return string.Join("; ", parts);
        }

        // ------------------------------------------------------------ colours

        private void StartColourInput()
        {
            Current = Step.ColourInput;
            ColourWords = "";
            ColourFromCrew = false;
            InputSwatches.Clear();
            ColourInputs = 0;
            ResetInputTimers();
            if (!FrameShown) PlaceShowFrame();
            if (tutorial != null) StopCoroutine(tutorial);
            tutorial = StartCoroutine(ColourTutorial(!Taught(ColourInputsTaughtKey)));
        }

        private IEnumerator ColourTutorial(bool full)
        {
            tutorialRunning = true;
            if (!full)
            {
                AskWith(Crew.Spark, "Colours? Tell me, pinch one off anything, or show me.", false, Rp("You pick"));
                EndTutorial();
                yield break;
            }
            Say(Crew.Spark, "Now the colours! Tell me,");
            yield return ShowWith(() => { PulseMic(); LookAt(Crew.Spark, MicPoint(), 1.4f); });
            Say(Crew.Spark, "or turn your left palm up and pinch a colour off anything,");
            yield return ShowWith(() => { EnsurePalette(); Body(Crew.Spark)?.Play("HandRaise"); });
            Say(Crew.Spark, "frame a texture with both hands in an L,");
            yield return ShowWith(() => Body(Crew.Spark)?.Play("React_Nod"));
            AskWith(Crew.Spark, "or put it in my frame and press the button. Or say you pick!", false, Rp("You pick"));
            yield return ShowWith(() => { PulseFrame(); LookAt(Crew.Spark, ShowCircleCentre, 2f); });
            // The palette and the frame are taught here now (the old coach would say it again).
            MarkTaught(ColourInputsTaughtKey);
            MarkTaught(PaletteTaughtKey);
            MarkTaught(TextureTaughtKey);
            EndTutorial();
        }

        private void FinishColours()
        {
            StopTutorial();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ColourWords)) parts.Add(ColourWords.Trim());
            foreach (var s in InputSwatches) parts.Add((s.kind == "texture" ? "the texture of " : "") + s.name + " (" + ColourName(Hex(s.hex)) + " " + s.hex + ")");
            if (Reference != null && !InputSwatches.Any(s => s.kind == "texture")) parts.Add("the colours and surface of the reference photo");
            if (parts.Count == 0 || ColourFromCrew) parts.Add("colours the crew picks to suit it and the room");
            Style = string.Join("; ", parts);
            Talk.Add("Colours: " + Style);
            Note("palette", "Colours", Style, Reference);
            HideShowFrame();
            // F41: a desk clip needs no pictures: straight to the desk edge's thickness.
            if (Kind == "clip") { StartMeasuring(); return; }
            StartCoroutine(MakeLooks());
        }

        // The pictures could not be made: the colours are asked again (the shape stays).
        private void BackToColours()
        {
            // F49: the sketches could not be made: back to the shape (or, for the finish pictures, to the finish).
            if (FormFirst && lookJob == "finishes") { Current = Step.Finish; vibeOffered = true; return; }
            Current = FormFirst ? Step.ShapeInput : Step.ColourInput;
            ResetInputTimers();
            if (Current == Step.ShapeInput) { StartOfferingBook(); if (KeptCount > 0) { if (GiveCardsToMaker) AskForMore(); else ShowShapeDone(); } } // F79
            if (!FrameShown) PlaceShowFrame();
        }

        // ------------------------------------------------------------ inputs

        private void ResetInputTimers()
        {
            inputSettleAt = -1f;
            inputReminded = false;
            inputRemindCount = 0;
            lastInputRemindAt = -100f;
            lastInputAt = Time.time;
        }

        private static bool YouPick(string p) =>
            new[] { "you pick", "you choose", "you decide", "up to you", "surprise me", "anything", "whatever", "no idea", "don't mind", "알아서", "맘대로", "아무거나", "니가", "네가" }.Any(p.Contains);

        private static bool DoneWords(string p) =>
            p == "done" || p == "next" || p == "that's it" || p == "that's all" || p == "go" || p == "okay" || p == "ok" || p.Contains("that's it") || p.Contains("다 했") || p.Contains("됐어") || p.Contains("다음");

        private static bool ThisWords(string p) =>
            p == "this" || p == "this one" || p == "here" || p == "take it" || p == "snap it" || p.Contains("in the circle") || p.Contains("in the frame") || p.Contains("this shape") || p.Contains("these colou") || p.Contains("이거") || p.Contains("여기");

        // A plain sentence while the shape or the colours are being given.
        private bool InputVoice(string text, string p)
        {
            if (Current == Step.ShapeInput) return ShapeVoice(text, p); // F56: the shape is collected as cards
            if (YouPick(p))
            {
                StopTutorial();
                ColourFromCrew = true;
                Ack(Crew.Spark, "We'll pick the colours!");
                FinishColours();
                return true;
            }
            if (ThisWords(p) && FrameShown) { TakeShowPicture(true); return true; }
            if (DoneWords(p))
            {
                if (capturingShow || Drawing) return true;
                if (ColourInputs > 0) { inputSettleAt = Time.time; return true; }
                Ack(Crew.Spark, "Tell me a colour, or pinch one first!");
                return true;
            }
            // "Like this mug" / "like that one over there": Spark looks at what the person looks or points at.
            if (PointsAtSomething(p) && !ThisWords(p) && Eyes != null && !Eyes.Looking) { StartCoroutine(LookForInput(text)); return true; }
            ColourWords = (ColourWords + ", " + text).Trim(' ', ',');
            Ack(Crew.Spark, "Ooh, " + MakerRoles.Clean(text) + ".");
            GotInput();
            return true;
        }

        private IEnumerator LookForInput(string words)
        {
            Ack(Crew.Spark, "Let me see it!");
            RoomEyes.Found found = null;
            yield return SparkLooks(words, list => found = list.FirstOrDefault(f => f.seen.focus) ?? list.FirstOrDefault());
            if (!InInputs) yield break;
            if (found == null || found.crop == null) { Say(Crew.Spark, Eyes.LastError ?? "I can't spot it. Put it in my frame?"); PulseFrame(); yield break; }
            Matched = found;
            ClearTags();
            HideSeenLabel(found);
            if (found.placed) Tag(found, false);
            string name = MakerRoles.Clean(found.seen.name);
            // Its words go along too: the colours and surface Spark saw, or that it is shaped like it.
            if (Current == Step.ColourInput)
                ColourWords = (ColourWords + ", " + words + " (the " + name + ": colours " + string.Join(", ", found.seen.colors ?? new string[0]) + "; surface " + MakerRoles.Clean(found.seen.surface) + ")").Trim(' ', ',');
            Talk.Add("Spark saw: " + found.seen.name + ", " + string.Join(", ", found.seen.colors ?? new string[0]) + ", " + found.seen.surface);
            InputPicture(found.crop, name);
        }

        // A picture as an input: the shape of a real thing, or its colours and surface.
        private void InputPicture(byte[] jpeg, string label)
        {
            if (jpeg == null || !InInputs) return;
            Note("capture", string.IsNullOrEmpty(label) ? "Captured by Spark" : label, "", jpeg);
            if (Current == Step.ShapeInput)
            {
                // F56: a picture for the shape is a photo card (it counts once it is on the note).
                Vector3 spark = cast.TryGetValue(Crew.Spark, out var s) && s != null ? s.transform.position + Vector3.up * .12f : FrontPoint(.35f, -.25f, out _);
                AddPhotoCard(jpeg, null, label, "seen by Spark", spark, spark + Vector3.up * .1f);
                return;
            }
            Reference = jpeg;
            Ack(Crew.Spark, label != null ? "Got the colours of your " + label + "!" : "Got its colours!");
            Body(Crew.Spark)?.Play("Peek");
            Heard();
            GotInput();
        }

        // A colour or a texture picked into the palette while the colours are being given.
        private void InputSwatch(Swatch swatch, Color colour, byte[] jpeg)
        {
            // F49: colours and textures picked in the finish step go to the finish pictures.
            if (Current == Step.Finish && swatch != null && !FinishSwatches.Contains(swatch)) FinishSwatches.Add(swatch);
            if (Current != Step.ColourInput || swatch == null) return;
            if (!InputSwatches.Contains(swatch)) InputSwatches.Add(swatch);
            if (swatch.kind == "texture" && jpeg != null) Reference = jpeg;
            Ack(Crew.Spark, swatch.kind == "texture" ? "Got that texture!" : "Got that " + ColourName(colour) + "!");
            GotInput();
        }

        private void GotInput()
        {
            StopTutorial();
            lastInputAt = Time.time;
            if (Current == Step.ShapeInput) { ShapeInputs++; return; } // F56: the shape step ends only on "done"
            ColourInputs++;
            inputSettleAt = Time.time + InputSettle;
        }

        // F92: the ways have been shown, so the reminder's clock starts now (it used to start when the step did, so
        // the first reminder came right on the heels of the explanation).
        private void EndTutorial()
        {
            tutorialRunning = false;
            lastInputAt = Mathf.Max(lastInputAt, Time.time);
        }

        private void StopTutorial()
        {
            if (!tutorialRunning) return;
            if (tutorial != null) StopCoroutine(tutorial);
            tutorial = null;
            EndTutorial();
            Hush(); // the rest of the tutorial is not needed any more
        }

        // Starting over (the coroutines were all stopped): nothing of the inputs or the making stays half done.
        private void AsideInputsAndSculpt()
        {
            tutorial = null;
            EndTutorial();
            capturingShow = false;
            inputSettleAt = -1f;
            StopOfferingBook(false);
            HideShowFrame(true);
            ClearShapeCards();
            handBusy = false;
            if (Sculpt != SculptStage.None) SculptFail();
            NewFormFirstMake(); // F49: no lesson or finish half done
        }

        // ------------------------------------------------------------ every frame

        private void InputsFrame()
        {
            if (!InInputs)
            {
                if (Current == Step.ForObject) { ForObjectFrame(); return; } // F45: the thing it is for, in the frame
                if (OfferingBook) StopOfferingBook(false);
                if (FrameShown && Current != Step.Looks && !PaintingStep) HideShowFrame(); // F87: it stays out for mood pictures while colouring
                return;
            }
            bool shape = Current == Step.ShapeInput;
            bool busy = Drawing || SketchbookHeld || takingSketchbook || paperInFlight || capturingShow || (Eyes != null && Eyes.Looking) || WindupBody.UserTalking || (Sketching && shape) ||
                        FrameBusy || carriedCard != null || summarising || roleCard != null || OfferingTidy;
            if (!shape && inputSettleAt > 0f && Time.time >= inputSettleAt && !busy)
            {
                inputSettleAt = -1f;
                FinishColours();
                return;
            }
            // (Checks of other features: the collecting ends by itself, MakerSession.ShapeCardsAuto.)
            if (shape && ShapeCardsAuto && autoDoneAt > 0f && Time.time >= autoDoneAt && !busy) { FinishShape(); return; }
            if (busy) { lastInputAt = Mathf.Max(lastInputAt, Time.time - 1f); if (inputSettleAt > 0f) inputSettleAt = Mathf.Max(inputSettleAt, Time.time + 1.5f); if (autoDoneAt > 0f) autoDoneAt = Mathf.Max(autoDoneAt, Time.time + 1.5f); }
            // F49: the shape step waits for the person, said again now and then (three times at most, further apart
            // each time); the old colour step reminds once. F56: what is said depends on where the collecting is:
            // nothing given yet, cards made but none on the note, or cards on the note (then only how to end it).
            int most = FormFirst ? LessonRemindMost : 1;
            float after = InputRemindAfter + inputRemindCount * 8f;
            int given = shape ? ShapeInputs : ColourInputs;
            bool quiet = !tutorialRunning && !busy && lines.Count == 0 && !Voice.IsSpeaking && Time.time - Mathf.Max(lastInputAt, Mathf.Max(lastInputRemindAt, lastHeardAt)) > after;
            if (inputRemindCount >= most || !quiet || (!shape && given > 0)) return;
            inputReminded = true;
            inputRemindCount++;
            lastInputRemindAt = Time.time;
            InputReminders++;
            if (!shape) { Say(Crew.Spark, "Any colours? Tell me, pinch one, or say you pick!"); PulseMic(); }
            else if (KeptCount > 0) { if (GiveCardsToMaker) { if (Question == null || Question.answered) AskForMore(); } else { Say(Crew.Maker, "Add more, or say done when that's all."); PulseShapeDone(); } }
            else if (ShapeCards.Any(c => !c.crumpled)) { if (GiveCardsToMaker) { Say(Crew.Maker, "Give me the ones you like!"); Body(Crew.Maker)?.Play("HandRaise"); } else { Say(Crew.Maker, "Put the ones you like on your note!"); arrival.RingNoteIn(); } }
            else { Say(Crew.Maker, "Tell me the shape, snap something in Spark's frame, or take my sketchbook!"); PulseFrame(); }
        }

        private void PulseMic()
        {
            micPulseAt = Time.time;
            Vector3 at = MicPoint();
            if (Head != null) ToyFx.Ring(at, Quaternion.LookRotation(Head.position - at), .05f);
            ToySounds.Play(ToySounds.Cue.Pop, at, .25f, 1.6f);
        }

        private Vector3 MicPoint()
        {
            var mic = arrival != null ? arrival.microphoneButton : null;
            return mic != null && mic.activeInHierarchy ? mic.transform.position : FrontPoint(.35f, -.25f, out _);
        }

        private void LookAt(Crew crew, Vector3 point, float seconds) => Body(crew)?.LookAtPoint(point, seconds);

        // ------------------------------------------------------------ Maker's sketchbook, held out

        private bool offeredBookReady;
        private float offeredBookForward;

        private void StartOfferingBook()
        {
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null) return;
            BuildPaper();
            if (Drawing || takingSketchbook || paperInFlight) return;
            offeredBook = paper;
            offeredBookReady = false;
            offeredBookForward = .047f;
            paper.gameObject.SetActive(false);
            paper.localScale = Vector3.one * BookOfferScale;
            SketchbookGrip(true);
            OfferingBook = true;
            offerPulseAt = -1f; // F61: the first offer a moment after it is held out
            PlaceOfferedBook(maker, 1f);
        }

        // `taken`: the person took it (the drawing flight moves it on); else Maker puts it away.
        private void StopOfferingBook(bool taken)
        {
            if (taken) BookEverTaken = true; // F61: no more "take it" offers
            if (!OfferingBook) return;
            OfferingBook = false;
            Body(Crew.Maker)?.Hold(true, null);
            Body(Crew.Maker)?.Hold(false, null);
            if (!taken && paper != null && !Drawing) PutSketchbookAway();
        }

        // The closed book at Maker's chest, cover toward the person, tipped back a little; Maker's mittens
        // hold its side edges at mid-height (never more than HandReach from its body), and the book stays clear of
        // its big head (HeadDepth).
        public const float BookOfferScale = .32f; // F61: was .25 (5 cm, not seen as something to take)
        private void PlaceOfferedBook(WindupCharacter maker, float blend)
        {
            if (paper == null) return;
            Vector3 at = maker.transform.position;
            Vector3 toUser = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
            toUser = toUser.sqrMagnitude > 1e-6f ? toUser.normalized : Vector3.back;
            Vector3 right = Vector3.Cross(Vector3.up, toUser);
            float half = PaperSize * BookOfferScale * .5f;
            Vector3 centre = at + toUser * offeredBookForward + Vector3.up * (.022f + half);
            // The book's front (its local -z) faces the person, tipped back 15 deg; its local up is the book's up.
            Quaternion rot = Quaternion.AngleAxis(-15f, right) * Quaternion.LookRotation(-toUser, Vector3.up);
            var body = WindupBody.Of(maker);
            if (body != null)
                for (int step = 0; step < 8; step++)
                {
                    float least = 9f;
                    foreach (var corner in new[] { new Vector3(-half, half, 0f), new Vector3(half, half, 0f), new Vector3(0f, half, 0f), Vector3.zero })
                        least = Mathf.Min(least, body.HeadDepth(centre + rot * corner));
                    if (least >= 1.15f) break;
                    centre += toUser * .004f;
                }
            // Keep head-clearance nudges for this offer, avoiding feedback from the moving head pose.
            offeredBookForward = Mathf.Min(.055f, Vector3.Dot(centre - at, toUser));
            centre = at + toUser * offeredBookForward + Vector3.up * (.022f + half);
            // F61: "here, take it": now and then Maker pushes the book toward the person, a little more upright.
            float offer = OfferPulse(maker);
            centre += toUser * (OfferPush * offer) + Vector3.up * (OfferLift * offer);
            rot = Quaternion.AngleAxis(OfferTip * offer, right) * rot;
            paper.SetPositionAndRotation(Vector3.Lerp(paper.position, centre, blend), Quaternion.Slerp(paper.rotation, rot, blend));
            if (body == null) return;
            // Wrist pivots are at the mitten's base; lift them so the palms centre on the side edges.
            // Keep the mitten thickness outside the cover and its wrist slightly behind the page.
            float coverHalf = (PaperSize + .01f) * BookOfferScale * .5f;
            bool ready = true;
            foreach (bool r in new[] { true, false })
            {
                // (2026-11-14, the user on Quest: "the hands holding the sketchbook are upside down") The book's own
                // turn flipped the mittens round (their bone axes do not point along the hand, as with the pencil):
                // they keep their rest turn and only yaw toward the book (WindupBody.NaturalGrip).
                Quaternion grip = body.NaturalGrip(r, paper.position, 50f);
                Vector3 hand = paper.position + paper.rotation * new Vector3(
                    (r ? -1f : 1f) * (coverHalf + .0065f), -.0115f, .011f);
                body.Hold(r, new Pose(hand, grip));
                ready &= body.HoldReady(r);
            }
            // Read the blend state: WindupBody sets the world poses later in this frame, after body motion.
            if (!offeredBookReady && ready)
            {
                offeredBookReady = true;
                paper.gameObject.SetActive(true);
            }
        }

        private void InputsLateFrame(float blend)
        {
            ShowFrameLate(blend);   // F56: Spark's frame, its label, the scan line
            ShapeCardsLate(blend);  // F56: the shape cards and the Done pill
            if (OfferingBook && !SketchbookHeld && cast.TryGetValue(Crew.Maker, out var maker) && maker != null && !Drawing)
            {
                PlaceOfferedBook(maker, blend);
                maker.lookTarget = Head;
            }
        }
    }
}
