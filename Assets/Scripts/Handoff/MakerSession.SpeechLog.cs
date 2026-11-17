using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace Windups.Handoff
{
    // F48 (2026-11-12): what the person said and what the app made of it.
    //  - Every utterance is one line in persistentDataPath/maker_talk_log.jsonl: the words, the state they were
    //    said in (step, v2, an open question...), where they went (the route, or the Director's reading) and the
    //    state after. Real play sessions become the test cases for how speech is understood. Written off the
    //    main thread.
    //  - The words that are not a change: a request for v2 itself, a reaction ("thanks"), a question. They used
    //    to be planted as the person's flag on v2 or kept as a change while the thing was being made.
    public sealed partial class MakerSession
    {
        public static string TalkLogPath => Path.Combine(Application.persistentDataPath, "maker_talk_log.jsonl");
        public string LastRoute { get; private set; } = "";
        public readonly List<string> Routes = new List<string>(); // tests: where each utterance went

        private const string DeferredLog = "(director)"; // the Director writes its own line when its answer is in
        private static readonly object talkLogLock = new object();
        private string voiceRoute, talkLogPath;

        // Where the session stands, in a few words (for the log and the Director).
        private string SpeechState() =>
            Current + (Draft != null ? Confirming ? " +v2 confirming" : " +v2" : "") + (Question != null && !Question.answered ? Question.toCrew ? " +crew question" : " +step question" : "") +
            (Drawing ? " +drawing" : "") + (NotebookOpen ? " +notebook" : "") + (awaitingFit ? " +fit asked" : "");

        private void LogSpeech(string text, string before, string route, object director = null)
        {
            if (route == DeferredLog) return;
            LastRoute = route;
            Routes.Add(route);
            if (Routes.Count > 100) Routes.RemoveAt(0);
            talkLogPath ??= TalkLogPath; // (persistentDataPath is main-thread only)
            string path = talkLogPath;
            string line;
            try { line = JsonConvert.SerializeObject(new { t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), text, state = before, route, after = SpeechState(), scripted = Scripted, director }) + "\n"; }
            catch (Exception exception) { Debug.LogWarning("[Windups Talk] " + exception.Message); return; }
            Task.Run(() =>
            {
                try
                {
                    lock (talkLogLock)
                    {
                        var file = new FileInfo(path);
                        if (file.Exists && file.Length > 2_000_000) file.Delete();
                        File.AppendAllText(path, line);
                    }
                }
                catch { }
            });
        }

        // "Can you make version two?", "build v2", "next version please": asks for v2 itself (not a change on it).
        public static bool WantsV2(string p)
        {
            p = Regex.Replace(p ?? "", @"[.,!?]+", "").Trim();
            const string version = @"(v ?2|v ?two|version ?(two|2|to|too)|(the |a |another )?(second|next|new) version|the next one)";
            return Regex.IsMatch(p, @"\b(make|build|create|do|start|want|have|go for|go with|give me|show me)\b.{0,20}\b" + version + @"( please| now| for me)?$") ||
                   Regex.IsMatch(p, "^" + version + @"( please| now)?$") ||
                   Regex.IsMatch(p, @"(v ?2|버전 ?2|버전 ?투|두 ?번째 버전|다음 버전|새 버전).{0,6}(만들|해 ?줘|해 ?주세요|가자|갈게)");
        }

        private static readonly HashSet<string> Reactions = new HashSet<string>
        {
            "thanks", "thank you", "thanks a lot", "thank you so much", "thanks guys", "thank you guys", "cool", "nice", "wow", "great", "awesome", "amazing",
            "love it", "i love it", "i like it", "cute", "so cute", "lol", "haha", "hmm", "um", "uh", "oh", "ok cool", "good job", "nice work", "well done",
            "that's cool", "that's nice", "that's great", "looks great", "looks nice", "let me think", "hmm let me think", "one moment", "one sec",
            "never mind", "nevermind", "고마워", "고맙습니다", "감사", "감사합니다", "좋다", "좋네", "멋지다", "멋있다", "귀엽다", "와", "우와", "대박", "음", "흠", "잠깐만", "잠시만"
        };

        // Only a reaction, nothing to do: the whole sentence is one of these.
        public static bool JustReaction(string p) => Reactions.Contains(Regex.Replace(p ?? "", @"[.,!?~]+", "").Trim());

        // A question even when the "?" was not written down ("how long will it take").
        public static bool QuestionLike(string p) =>
            Regex.IsMatch(p ?? "", @"^(so |and |but |um |hey |ok |okay )?(what|why|how|who|where|when|which|is it|is this|is that|are they|are you|does it|does this|do you|did you|will it|will this|would it|should i|should we|can i|could i|may i|am i)\b");

        // Asked something while the thing is being worked out or built: Maker says where it is (no change is kept).
        private void SayProgress(bool reaction)
        {
            if (reaction) { Body(Crew.Spark)?.Play("React_Nod"); return; }
            string line = Current == Step.Planning ? "Working it out. One moment!"
                : Sculpt == SculptStage.Paint ? "Painting it now. Almost there!"
                : Sculpt == SculptStage.Reveal ? "Nearly done!"
                : Sculpt == SculptStage.Shape ? "Shaping it now. Paint comes next."
                : Sculpt == SculptStage.Approach || Sculpt == SculptStage.Clay ? "Carving the block now. Paint comes after."
                : "Building it now. One moment!";
            Caption(Crew.Maker, line, 2.8f);
            Talk.Add("Maker: " + line);
        }
    }
}
