using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // F87 (2026-11-15 Quest try): colouring.
    //  - "I'm done" / "done painting" / "keep mine" while colouring (also while the finish pictures are up) keeps the
    //    person's own paint and goes on. "I'm done" ends the work at once; the others ask the one next step
    //    (Finish | Change shape). It never answers "It looks unfinished" to someone who painted it themself.
    //  - "I'll paint it myself" or "none of these" puts the finish pictures away; the person paints, Done when happy.
    //  - Spark brings its frame out with the vibe offer (and when asked for a reference / mood picture): a photo taken
    //    in it is a mood picture that goes to the finish pictures and to Maker (MoodPictures, MoodRole).
    //  - The notebook's Colours / Textures keep only what the work used: the paint on it, or the swatches the picked
    //    finish named (RememberFinishUsed).
    public sealed partial class MakerSession
    {
        public const string MoodRole = "a photo the person took in their room for the mood: use its colours, materials and feel; do not copy its objects or its shape";
        public const string MoodVibe = "the mood of the photo I took";
        public const int MoodMost = 2;
        private const string MoodFrameTaughtKey = "roomsmiths.moodFrame.taught";

        public readonly List<byte[]> MoodPictures = new List<byte[]>();
        public int MoodShots { get; private set; }
        public string LastColouringRoute { get; private set; } = "";
        private readonly List<Swatch> finishUsed = new List<Swatch>();
        private bool finishUsedKnown;

        private bool PaintingStep => FormFirst && (Current == Step.Finish || (Current == Step.Looks && lookJob == "finishes"));

        private void ResetColouring87()
        {
            MoodPictures.Clear();
            finishUsed.Clear();
            finishUsedKnown = false;
        }

        private static bool HasAny(string p, params string[] words) => words.Any(p.Contains);

        public static bool FinishedPaintingWords(string p) => IsCompletionCommand(p) || HasAny(p,
            "done painting", "done colouring", "done coloring", "finished painting", "finished colouring", "finished coloring",
            "i painted it", "i painted all", "painted it myself", "keep mine", "keep my paint", "keep my colours", "keep my colors",
            "다 칠했", "칠 다 했", "내가 칠한 걸로", "이대로 할래");

        public static bool WillPaintWords(string p) => !FinishedPaintingWords(p) && HasAny(p,
            "i'll paint", "ill paint", "i will paint", "let me paint", "paint it myself", "paint myself", "do it myself",
            "내가 칠할", "직접 칠할", "내가 할게", "직접 할게");

        public static bool NoneOfTheseWords(string p) => p.Split(' ').Length <= 7 && HasAny(p,
            "none of", "don't like", "dont like", "do not like", "no thanks", "not these", "neither", "별로", "싫어", "마음에 안");

        public static bool MoodPictureWords(string p) => HasAny(p,
            "mood picture", "mood photo", "reference", "take a picture", "take a photo", "capture", "your frame", "the frame", "snap",
            "레퍼런스", "참고", "사진", "찍어", "캡처", "캡쳐", "프레임");

        // Route/OnVoice (through EndingVoice, FinishVoice and the finish pictures' Looks branch).
        private bool ColouringVoice(string p)
        {
            if (!PaintingStep || ending != EndingState.Working) return false;
            bool pictures = Current == Step.Looks;
            if (FinishedPaintingWords(p)) { LastColouringRoute = "keep my paint"; KeepMyPaint(IsCompletionCommand(p)); return true; }
            if (WillPaintWords(p))
            {
                LastColouringRoute = "will paint";
                if (pictures) PutFinishPicturesAway();
                AskWith(Crew.Maker, "Go ahead and paint it! Tap Done when you're happy.", false, Rp("Done", "done"));
                return true;
            }
            if (pictures && LooksShown && NoneOfTheseWords(p))
            {
                LastColouringRoute = "none of these";
                PutFinishPicturesAway();
                AskWith(Crew.Maker, "Okay! Paint it yourself, or tell me another mood.", false, Rp("Done", "done"), Rp(NewMoodLabel, "new mood"));
                return true;
            }
            if (MoodPictureWords(p)) { LastColouringRoute = "mood picture"; MoodPictureAsked(p); return true; }
            return false;
        }

        private void PutFinishPicturesAway()
        {
            CloseQuestion("finish pictures put away");
            Hush();
            sketchWanted = false;
            ClearTabs();
            ClearLooks();
            LooksShown = false;
            Current = Step.Finish;
            vibeOffered = true;
            Heard();
        }

        // The person's own paint is the finish. `end`: "I'm done" finishes the work too.
        private void KeepMyPaint(bool end)
        {
            if (Current == Step.Looks) PutFinishPicturesAway();
            Heard();
            Talk.Add("The person keeps their own paint.");
            WriteInputLog("colouring: kept the person's own paint" + (end ? ", work finished" : ""), true);
            EndFinish(!end);
            if (end) StartEnding();
        }

        // ------------------------------------------------------------ Spark's frame for a mood picture

        private void MoodPictureAsked(string p)
        {
            if (!CanRepaint) { Say(Crew.Spark, "I can't recolour this one from a picture. Paint it with your palette!"); return; }
            if (FrameShown && !frameFlying && SeeWords(p)) { TakeShowPicture(true); return; }
            BringMoodFrame(true);
        }

        private void BringMoodFrame(bool asked)
        {
            if (!FrameShown && !frameFlying) PlaceShowFrame();
            else PulseFrame();
            LookAt(Crew.Spark, ShowCircleCentre, 2f);
            if (asked) Say(Crew.Spark, "Here's my frame! Frame the mood you like and press my red button.");
            else if (!Taught(MoodFrameTaughtKey)) Say(Crew.Spark, "Want a mood from your room? Frame it with my frame and press the red button!");
            MarkTaught(MoodFrameTaughtKey);
        }

        // ShowPicture while colouring: the photo is a mood picture and Maker draws the finishes with it.
        private void MoodShot(byte[] jpeg, Color32[] pixels, int width, int height)
        {
            if (jpeg == null)
            {
                ShowAnswer = "frame not in view";
                ScanFailed("I can't see my frame from here. Look at it with me?");
                PulseFrame();
                return;
            }
            ShowAnswer = "mood";
            ShowCaptures++;
            MoodShots++;
            ScanDone(pixels, width, height);
            MoodPictures.Add(jpeg);
            while (MoodPictures.Count > MoodMost) MoodPictures.RemoveAt(0);
            Note("palette", "Mood picture", "A photo you took for the mood.", jpeg);
            Talk.Add("The person took a mood picture in Spark's frame.");
            WriteInputLog("colouring: mood picture " + MoodPictures.Count, true);
            Body(Crew.Spark)?.Play("Idea_Pop");
            Ack(Crew.Spark, "Ooh, that mood!");
            if (VibeWords.Contains(MoodVibe)) { StopLessons(); vibeOffered = true; StartCoroutine(MakeLooks("finishes")); }
            else StartVibe(MoodVibe);
        }

        // ------------------------------------------------------------ the colours the work used (notebook)

        private void RememberFinishUsed(Picture picked)
        {
            finishUsed.Clear();
            finishUsedKnown = true;
            string said = ((picked?.look?.prompt ?? "") + " " + (picked?.look?.label ?? "")).ToLowerInvariant();
            foreach (var s in FinishSwatches)
            {
                string hex = (s.hex ?? "").ToLowerInvariant();
                string name = (s.name ?? "").ToLowerInvariant().Replace(" texture", "").Trim();
                if ((hex.Length == 7 && said.Contains(hex)) || (name.Length > 2 && said.Contains(name)) || (s.kind == "texture" && said.Contains("texture")))
                    finishUsed.Add(s);
            }
            if (finishUsed.Count == 0) finishUsed.AddRange(FinishSwatches);
        }

        // The palette notes of this work that it used; null when Maker did not repaint it (then only its paint counts).
        private HashSet<string> FinishUsedHexes() =>
            finishUsedKnown ? new HashSet<string>(finishUsed.Select(s => (s.hex ?? "").ToLowerInvariant())) : null;

        // Checks.
        public bool SimulateColouringWords(string p) => ColouringVoice(p);
        public void SimulateMoodShot(byte[] jpeg) => MoodShot(jpeg, null, 0, 0);
    }
}
