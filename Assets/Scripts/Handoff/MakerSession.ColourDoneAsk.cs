using UnityEngine;

namespace Windups.Handoff
{
    // F95 (Quest 2026-11-15, the user: "when it looks like I've finished colouring, the characters should ask if I'm
    // done"): in the finish step, once the person has painted something new and then looks finished, Spark (the
    // colour teammate) asks DoneAskLine at once (AskNow, F90) with [Done] [Keep painting]:
    //  - every part you can see is painted (not "bottom" / "inside") and nothing happened for DoneAskAllPainted, or
    //  - the palette went back to Spark after the painting, DoneAskPaletteBack later, or
    //  - no colouring (paint, erase, a colour or texture picked, the wheel, words) for DoneAskIdle.
    // Never during a lesson, before the vibe offer, while a fill spreads, while a hand holds, picks, frames or draws,
    // or while the person talks. Done = "done painting" (F87: keeps the paint, then Finish | Change shape); Keep
    // painting (or "not yet", "no", "아직") = "Take your time!" and the next ask needs new paint first; at most
    // DoneAskMost asks per finish. The scripted demo asks only with ColourDoneAskWhenScripted (older checks keep their
    // questions as they were).
    public sealed partial class MakerSession
    {
        public static bool ColourDoneAsk = true, ColourDoneAskWhenScripted;
        public static float DoneAskAllPainted = 3f, DoneAskPaletteBack = 1.5f, DoneAskIdle = 12f;
        public const int DoneAskMost = 3;
        public const string DoneAskLine = "All done colouring?", KeepPaintingLabel = "Keep painting";

        public int DoneAsks { get; private set; }
        public string LastDoneAskWhy { get; private set; } = "";
        public bool DoneAskOpen => Question != null && !Question.answered && Question.text == DoneAskLine;

        private int doneAskStrokes = -1, doneAskSeenStrokes, doneAskSeenErase, doneAskSeenPicks, doneAskSeenFrames, doneAskSeenWheel;
        private float colourActiveAt, paletteBackAt = -10f, doneAskedAt = -10f;

        // StartFinish / OfferVibe: what is painted so far does not count (the lesson's own swipe asked nothing).
        private void ResetDoneAsk()
        {
            DoneAsks = 0;
            doneAskStrokes = PaintStrokes;
            colourActiveAt = Time.time;
        }

        // Every frame (RunFrameSteps).
        private void ColourDoneAskFrame()
        {
            float now = Time.time;
            // The colouring's activity clock: any paint, erase, pick, texture or wheel colour moves it.
            if (PaintStrokes != doneAskSeenStrokes || EraseTouches != doneAskSeenErase || ColourPicks != doneAskSeenPicks ||
                TextureFrames != doneAskSeenFrames || WheelPicks != doneAskSeenWheel)
            {
                doneAskSeenStrokes = PaintStrokes; doneAskSeenErase = EraseTouches; doneAskSeenPicks = ColourPicks;
                doneAskSeenFrames = TextureFrames; doneAskSeenWheel = WheelPicks;
                colourActiveAt = now;
            }
            if (!ColourDoneAsk || (Scripted && !ColourDoneAskWhenScripted) || !Begun || !FormFirst || Current != Step.Finish) return;
            if (ending != EndingState.Working || finishDone || !vibeOffered || CurrentLesson != null || DoneAsks >= DoneAskMost) return;
            if (doneAskStrokes < 0) doneAskStrokes = PaintStrokes;
            if (PaintStrokes <= doneAskStrokes || DoneAskOpen || Product == null) return;
            // Busy: keep the clock running from now.
            if (fills.Count > 0 || Holding || Drawing || Capturing || WaitingForHands || frameSince >= 0f || WheelOpen || dragSwatch != null ||
                WindupBody.UserTalking || Voice.Loading || (Question != null && !Question.answered && Question.toCrew))
            {
                colourActiveAt = Mathf.Max(colourActiveAt, now - .5f);
                return;
            }
            float quiet = now - Mathf.Max(colourActiveAt, lastHeardAt, doneAskedAt);
            string why = null;
            if (quiet >= DoneAskAllPainted && AllVisiblePainted()) why = "every part painted";
            else if (PaletteAt == ToolAt.Crew && paletteBackAt > colourActiveAt && now - paletteBackAt >= DoneAskPaletteBack && quiet >= DoneAskPaletteBack) why = "palette given back";
            else if (quiet >= DoneAskIdle) why = "quiet " + DoneAskIdle.ToString("0") + " s";
            if (why == null) return;
            AskDone(why);
        }

        private void AskDone(string why)
        {
            DoneAsks++;
            LastDoneAskWhy = why;
            doneAskLive = true;
            doneAskedAt = Time.time;
            doneAskStrokes = PaintStrokes;
            WriteInputLog("colouring: done? asked (" + why + ")", true);
            if (Product != null) LookAt(Crew.Spark, Product.position, 1.2f);
            Body(Crew.Spark)?.Play("HandRaise");
            AskNow(Crew.Spark, DoneAskLine, false, Rp("Done", "done painting"), Rp(KeepPaintingLabel, "keep painting"));
        }

        // Every part of the made thing that can be seen has paint (the bottom and the inside are not asked for).
        private bool AllVisiblePainted()
        {
            var thing = Product != null ? Product.GetComponentInChildren<PaintableThing>() : null;
            var mesh = thing != null ? thing.Mesh : null;
            if (thing == null || mesh == null || thing.Parts == null || thing.Marks.Count == 0) return false;
            int seen = 0;
            for (int i = 0; i < thing.Parts.Length && i < mesh.subMeshCount; i++)
            {
                string name = thing.Parts[i];
                if (name == "bottom" || name == "inside" || mesh.GetSubMesh(i).indexCount == 0) continue;
                seen++;
                if (!thing.Marks.ContainsKey(i)) return false;
            }
            return seen > 0;
        }

        private static bool KeepPaintingWords(string p) =>
            p == "keep painting" || p == "not yet" || p == "no" || p == "nope" || p == "no not yet" || p == "still painting" || p.Contains("keep painting") ||
            p.Contains("not done") || p.Contains("not yet") || p.StartsWith("아직") || p == "아니" || p == "아니야" || p.Contains("더 칠할") || p.Contains("계속 칠");

        // OnVoice/Route before EndingVoice: the answer to the done question, said or tapped (a tapped reply closes the
        // question before its words come here, so `doneAskLive` remembers it was ours while no other question is up).
        private bool ColourDoneVoice(string p)
        {
            if (Current != Step.Finish) { doneAskLive = false; return false; }
            if (!DoneAskOpen && !(doneAskLive && Question == null)) return false;
            if (KeepPaintingWords(p))
            {
                doneAskLive = false;
                CloseQuestion("keep painting");
                Heard();
                doneAskStrokes = PaintStrokes;
                doneAskedAt = Time.time;
                LastDoneAnswer = "keep painting";
                WriteInputLog("colouring: keep painting", true);
                Ack(Crew.Spark, "Take your time!");
                return true;
            }
            if (IsCompletionCommand(p)) return false; // "I'm done": EndingVoice ends the work (F87)
            if (IsYes(p) || DoneWords(p) || FinishedPaintingWords(p) || p == "finished")
            {
                doneAskLive = false;
                LastDoneAnswer = "done";
                KeepMyPaint(false); // F87: the person's own paint, then Finish | Change shape
                return true;
            }
            return false;
        }
        private bool doneAskLive;
        public string LastDoneAnswer { get; private set; } = "";

        // Checks.
        public void SimulateDoneAskQuiet(float seconds) { colourActiveAt -= seconds; doneAskedAt -= seconds; lastHeardAt -= seconds; paletteBackAt -= seconds; }
        public bool AllVisiblePaintedNow => AllVisiblePainted();
    }
}
