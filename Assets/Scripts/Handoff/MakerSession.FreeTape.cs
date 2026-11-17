using System.Collections;
using System.Linq;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Windups.Handoff
{
    // The tape anytime (2026-11-11, after the Quest try). Fixer, whose job the tape is, shows it once (first at
    // the measuring step, or when the person asks about measuring or a size, or at a quiet moment while the
    // looks are up): the two tape ends pull apart in front of the person while the length counts up, and Fixer
    // says how. After that, pinching with both hands (thumb and index) and pulling apart measures anything at
    // any step; letting one hand go keeps the length. Fixer says it, and it is kept: at the measuring step it
    // is offered as the size, and after the shape "use it" fits the design to it. Not while holding, dragging,
    // drawing or picking with the palette.
    public sealed partial class MakerSession
    {
        public const string TapeTaughtKey = "thinkmade.tapeTaught";
        public float FreeMeasure { get; private set; }
        public int FreeMeasures { get; private set; }
        public bool TapeDemoShowing => tapeDemo;

        private float freeMeasureAt = -1000f, freeShownUntil, beganAt;
        private bool freeTape, tapeDemo, awaitingFreeUse;
        private float freeUseUntil;
        public const float FreeUseSeconds = 30f;
        public bool AwaitingMeasureUse => awaitingFreeUse && Time.time < freeUseUntil;
        public int AppliedFreeMeasures { get; private set; }
        public string LastMeasurePurpose { get; private set; }

        private void ClearFreeMeasureQuestion() { awaitingFreeUse = false; freeUseUntil = 0f; }

        private void FreeMeasureQuestionFrame()
        {
            if (awaitingFreeUse && Time.time >= freeUseUntil) ClearFreeMeasureQuestion();
        }

        private bool UseFreeMeasure(string purpose)
        {
            if (!AwaitingMeasureUse || string.IsNullOrWhiteSpace(purpose)) return false;
            ClearFreeMeasureQuestion();
            AppliedFreeMeasures++;
            LastMeasurePurpose = purpose;
            string request = $"Use the measured {FreeMeasure:0.#} mm for: {purpose}";
            Talk.Add("Person: " + request);
            Note("measure", "Use measured size", request);
            if (Draft != null && Current == Step.Done)
            {
                string p = purpose.ToLowerInvariant();
                bool dimension = new[] { "width", "wide", "height", "tall", "depth", "deep", "가로", "폭", "높", "깊", "세로" }.Any(p.Contains);
                if (dimension) DraftSizeWords(p + $" {FreeMeasure:0.#} mm");
                else { PersonFlag(request); Say(Crew.Maker, "Got it. I've put that measurement on v2 for you."); }
            }
            else if (Product != null && (Current == Step.Finish || Current == Step.Done))
            {
                string p = purpose.ToLowerInvariant();
                int axis = new[] { "height", "tall", "높" }.Any(p.Contains) ? 1
                    : new[] { "depth", "deep", "깊", "세로" }.Any(p.Contains) ? 2
                    : new[] { "width", "wide", "폭", "가로" }.Any(p.Contains) ? 0 : -1;
                if (axis >= 0 && ProductMesh != null && Design.kind == "model")
                {
                    // Keep the other two dimensions and the current paint, without another shape-generation job.
                    var size = ProductMesh.bounds.size * 1000f;
                    size[axis] = FreeMeasure;
                    var d = Design.Copy();
                    d.sizeW = size.x; d.sizeH = size.y + d.cut; d.sizeD = size.z;
                    d.cupDiameter = Mathf.Max(size.x, size.z); d.modelHeight = size.y;
                    Design = d.Clamped();
                    StartCoroutine(FitAndBuild());
                }
                else StartCoroutine(Plan(request));
            }
            else if (Current == Step.Planning || Current == Step.Making) pendingChange = request;
            else { ShapeWords = (ShapeWords + ", " + request).Trim(' ', ','); Say(Crew.Fixer, "Got it. I'll use that measurement for that."); }
            return true;
        }


        private bool FreeTapeAllowed => Begun && Current != Step.Measuring && Current != Step.Making &&
                                        !Holding && Dragging == null && !Drawing && !Capturing && !tapeDemo &&
                                        !FrameBusy && carriedCard == null; // F56: a pinch on Spark's frame or a shape card is not the tape

        private void UpdateFreeTape()
        {
            FreeMeasureQuestionFrame();
            if (!FreeTapeAllowed)
            {
                if (freeTape && Time.time > freeShownUntil) EndFreeTape();
                return;
            }
            Hand left = null, right = null;
            foreach (var hand in HandSet.Both())
                if (hand != null && hand.isActiveAndEnabled && HandIO.Connected(hand)) { if (hand.Handedness == Handedness.Left) left = hand; else right = hand; }
            if (left == null || right == null) { if (freeTape && Time.time > freeShownUntil) EndFreeTape(); return; }
            bool p0 = HandIO.Pinching(left), p1 = HandIO.Pinching(right);
            Vector3 a0 = PinchPoint(left), a1 = PinchPoint(right);
            TapeGate(left, right, ref p0, ref p1, a0, a1); // F66: Fixer tosses the tape over first
            Tape.Step(Time.time, p0, a0, Tip(left), p1, a1, Tip(right));
            if (Tape.State == HandTape.Mode.TwoHands)
            {
                if (NotebookOpen && !NotebookHeld) CloseNotebook(); // (the open book is not in the way of the tape)
                SnapTapeEnds(); // F39
                if (!freeTape && !Taught(TapeTaughtKey)) { MarkTaught(TapeTaughtKey); Caption(Crew.Fixer, "That's the tape! Let one hand go to keep it.", 3f); }
                freeTape = true;
                freeShownUntil = Time.time + 6f;
                ShowTape(true);
                UpdateTapeVisual();
            }
            if (Tape.JustMeasured) TookFreeMeasure(Tape.Length * 1000f);
            else if (freeTape && Tape.State != HandTape.Mode.TwoHands && Time.time > freeShownUntil) EndFreeTape();
        }

        private void EndFreeTape()
        {
            freeTape = false;
            if (Current != Step.Measuring) { Tape.Reset(); ShowTape(false); }
        }

        private void TookFreeMeasure(float mm)
        {
            FreeMeasure = Mathf.Round(Mathf.Clamp(mm, 5f, 3000f) * 2f) / 2f;
            freeMeasureAt = Time.time;
            freeShownUntil = Time.time + 6f;
            FreeMeasures++;
            Heard();
            UpdateTapeVisual();
            ToySounds.Play(ToySounds.Cue.Chime, (Tape.A + Tape.B) * .5f, .5f, 1.25f);
            ClearFreeMeasureQuestion();
            if (!FormFirst && DraftTapeSize(FreeMeasure)) return; // F49 asks what the measurement is for
            Talk.Add("Person measured something with the tape: " + FreeMeasure.ToString("0.#") + " mm");
            Note("measure", "Measured", FreeMeasure.ToString("0.#") + " mm");
            Body(Crew.Fixer)?.Play("React_Nod");
            bool canFit = HasMeasure || Product != null;
            awaitingFreeUse = canFit;
            freeUseUntil = Time.time + FreeUseSeconds;
            Say(Crew.Fixer, canFit ? $"{FreeMeasure:0.#} millimetres. What would you like to use that measurement for?" : $"{FreeMeasure:0.#} millimetres!");
        }

        // Voice: "use it" after a free measurement, and questions about measuring before the tape is taught.
        private bool TapeVoice(string p)
        {
            FreeMeasureQuestionFrame();
            if (AwaitingMeasureUse)
            {
                if (p == "no" || p == "cancel" || p == "ignore it" || p == "never mind" || p.Contains("무시") || p.Contains("취소"))
                { ClearFreeMeasureQuestion(); Ack(Crew.Fixer, "Okay, keeping the size as it was."); return true; }
                bool purpose = new[] { "use it", "use that", "fit it", "for the", "for my", "width", "height", "depth", "wide", "tall", "deep",
                    "그걸로", "맞춰", "폭", "가로", "높이", "깊이", "세로", "넣으", "쓰려고", "위해서" }.Any(p.Contains);
                bool question = System.Text.RegularExpressions.Regex.IsMatch(p, @"^(how|what|why|where|which|어떻게|뭐|왜|어디)\b");
                if (purpose && !question) return UseFreeMeasure(p);
            }
            if (!Taught(TapeTaughtKey) && Current != Step.Measuring && AsksAboutMeasuring(p))
            {
                // F49: the lesson itself belongs to the measuring step; asked before it, one line.
                if (FormFirst) Say(Crew.Fixer, "Pinch both hands, pull apart, let one go.");
                else StartCoroutine(TeachTape());
                return true;
            }
            return false;
        }

        public static bool AsksAboutMeasuring(string p) =>
            new[] { "measure", "tape", "how big", "how wide", "how long", "what size", "size of", "줄자", "재봐", "재 봐", "재볼", "크기", "길이" }.Any(p.Contains);

        // A quiet moment while the looks are up (after the palette tip): Fixer shows the tape once.
        private void TapeCoachFrame()
        {
            // F49: no tape lesson at random quiet moments (it came up in the shape and colour steps); it is taught
            // step by step when measuring.
            if (FormFirst) return;
            if (!Begun || tapeDemo || Taught(TapeTaughtKey) || CoachingPalette || Current == Step.Measuring || Current == Step.Making) return;
            if (crewBusy || lines.Count > 0 || Voice.IsSpeaking || Voice.Loading || Holding || PaletteOnPalm || Drawing || freeTape) return;
            if (Time.time - Mathf.Max(lastStepAt, beganAt) < CoachAfter + 12f) return;
            StartCoroutine(TeachTape());
        }

        // Fixer's tape lesson: the ends pull apart in front of the person while the length counts up.
        private IEnumerator TeachTape()
        {
            if (tapeDemo || Head == null) yield break;
            tapeDemo = true;
            Heard();
            Body(Crew.Fixer)?.Play("Decision_Present");
            Say(Crew.Fixer, "Tape tip: pinch both hands, pull apart, let one go.");
            Vector3 centre = FrontPoint(.38f, -.18f, out Vector3 side);
            for (int loop = 0; loop < 2; loop++)
            {
                for (float t = 0f; t < 1f; t += Time.deltaTime / 1.3f)
                {
                    float half = Mathf.Lerp(.012f, .07f, Mathf.SmoothStep(0f, 1f, t));
                    Tape.SetMeasured(centre - side * half, centre + side * half);
                    ShowTape(true);
                    UpdateTapeVisual();
                    yield return null;
                }
                ToySounds.Play(ToySounds.Cue.Chime, centre, .3f, 1.4f);
                yield return new WaitForSeconds(.5f);
            }
            Tape.Reset();
            ShowTape(false);
            MarkTaught(TapeTaughtKey);
            tapeDemo = false;
        }

        // At the measuring step: a measurement taken in the last three minutes is offered as the size.
        private void OfferFreeMeasure()
        {
            if (FormFirst || FreeMeasures == 0 || Time.time - freeMeasureAt > 180f) return;
            StartCoroutine(After(1.2f, () =>
            {
                if (Current != Step.Measuring || HasMeasure) return;
                Talk.Add("Fixer offers the earlier measurement: " + FreeMeasure.ToString("0.#") + " mm");
                SetMeasure(FreeMeasure, false, true);
            }));
        }

        public static void ForgetTapeLesson() => PlayerPrefs.DeleteKey(TapeTaughtKey);
        public void SimulateFreeTape(Vector3 a, Vector3 b) { Tape.SetMeasured(a, b); freeTape = true; ShowTape(true); TookFreeMeasure(Tape.Length * 1000f); }
        public void SimulateTeachTape() => StartCoroutine(TeachTape());
    }
}
