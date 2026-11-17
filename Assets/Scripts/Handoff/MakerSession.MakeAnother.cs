using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // F98 (Quest 2026-11-15, the user: "after the first work was finished, celebrated and exported, they should ask: make
    // another? / not now. Make another -> a new post-it, and I can speak or find inspiration (the magnifier) and so on,
    // the same as the first time. Not now -> after a while Maker may ask first what to make; if I take out a post-it,
    // 'let's make it!'; if I say into the mic that I want to make a new one, a new session starts"):
    //  - Fixer's Export question answered (Export done, or Not now; or no export at all): Maker asks MakeAnotherQuestion
    //    with [Make another] [Not now].
    //  - Make another (or the words, any time the work is finished): StartNewMake: the finished work stays where it is,
    //    and the guide holds out a fresh post-it: the first-time steps (WindupsArrival.OfferNewNote).
    //  - Not now: "Okay! Whenever you're ready." After AskAgainAfter of quiet Maker asks once more ("What shall we make
    //    next?", the same buttons); peeling a note off the wall pad -> "Let's make it! Tell me your idea."
    //  - Before this, every word said after a finished work was swallowed (EndingVoice returned true for everything).
    public sealed partial class MakerSession
    {
        public const string MakeAnotherQuestion = "Make something new?", MakeAnotherAgain = "What shall we make next?";
        public const string MakeAnotherLabel = "Make another", MakeAnotherLaterLabel = "Not now";
        public static float AskAgainAfter = 75f;
        public int MakeAnotherAsks { get; private set; }
        public int NewMakesStarted { get; private set; }
        private float makeAnotherAgainAt = -1f;

        private bool makeAnotherAskedAgain;
        private void AskMakeAnother(bool again = false)
        {
            makeAnotherAgainAt = -1f;
            makeAnotherAskedAgain = again;
            MakeAnotherAsks++;
            AskWith(Crew.Maker, again ? MakeAnotherAgain : MakeAnotherQuestion, false,
                Rp(MakeAnotherLabel, "make another"), Rp(MakeAnotherLaterLabel, "maybe later"));
        }

        private static readonly string[] NewMakeWordList =
        {
            "make another", "make something new", "make something else", "make a new one", "another one", "a new one",
            "new creation", "let's make", "lets make", "new idea", "start a new", "make one more", "one more",
            "새로 만들", "또 만들", "다른 거 만들", "다른거 만들", "새로운 거", "새거", "하나 더", "새 작품", "만들자", "만들래",
        };
        public static bool NewMakeWords(string p) => !string.IsNullOrEmpty(p) && NewMakeWordList.Any(p.Contains);

        // In EndingVoice, while the work is finished (WorkArchived): the answers and the words.
        private bool MakeAnotherVoice(string words)
        {
            bool open = Question != null && !Question.answered && (Question.text == MakeAnotherQuestion || Question.text == MakeAnotherAgain);
            if (words == "maybe later" || (open && (IsNo(words) || words == "not now" || words.Contains("나중"))))
            {
                CloseQuestion("make another: later");
                Ack(Crew.Maker, "Okay! Whenever you're ready.");
                makeAnotherAgainAt = makeAnotherAskedAgain ? -1f : Time.time + AskAgainAfter; // asked again only once
                WriteInputLog("make another: not now" + (makeAnotherAskedAgain ? "" : " (asks again after " + AskAgainAfter.ToString("0") + " s of quiet)"), true);
                return true;
            }
            if (words == "make another" || NewMakeWords(words) || (open && IsYes(words)))
            {
                StartNewMake();
                return true;
            }
            return false;
        }

        public void StartNewMake()
        {
            CloseQuestion("make another");
            makeAnotherAgainAt = -1f;
            newCreationAsked = false;
            NewMakesStarted++;
            WriteInputLog("make another: a fresh post-it is offered (the first-time steps)", true);
            Talk.Add("The person wants to make something new.");
            PutAside();
            arrival.OfferNewNote();
        }

        // Every frame while the work is finished: after Not now and a quiet while, Maker asks once more.
        private void MakeAnotherFrame()
        {
            if (makeAnotherAgainAt < 0f || Time.time < makeAnotherAgainAt) return;
            if (!WorkArchived) { makeAnotherAgainAt = -1f; return; }
            if (Holding || Question != null || WindupBody.UserTalking || lines.Count > 0 || Voice.IsSpeaking || NotebookOpen ||
                arrival.PadNote == WindupsArrival.PadNoteState.Peeled || Time.time - lastHeardAt < 20f) return;
            AskMakeAnother(true);
        }
    }
}
